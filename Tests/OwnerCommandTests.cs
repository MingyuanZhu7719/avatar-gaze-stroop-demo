using System;
using System.IO;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class OwnerCommandTests
    {
        static string Id(int n){return n.ToString("x32");}
        static OwnerCommand Command(int id,OwnerCommandKind kind=OwnerCommandKind.Practice,ControlSelection? source=null)
        {return new OwnerCommand(Id(id),kind,source);}
        static void WithDirectory(Action<string> test)
        {string path=Path.Combine(Path.GetTempPath(),"stroop-owner-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);try{test(Path.Combine(path,"commands.txt"));}finally{Directory.Delete(path,true);}}
        public static void Register(TestSuite suite)
        {
            suite.Run("M3 owner whitelist rejects factor overrides scripts source omissions and malformed IDs",()=>{
                OwnerCommand parsed;string error;
                foreach(var command in new[]{Command(1),Command(2,OwnerCommandKind.Lock,ControlSelection.Automatic),Command(3,OwnerCommandKind.Lock,ControlSelection.SimulatorRehearsal),Command(4,OwnerCommandKind.Lock,ControlSelection.LiveWoz),Command(5,OwnerCommandKind.Begin),Command(6,OwnerCommandKind.Export)})
                {Check.True(OwnerCommandCodec.TryParse(OwnerCommandCodec.Encode(command),out parsed,out error));Check.Equal(command.Source,parsed.Source);Check.Equal(command.Kind,parsed.Kind);}
                foreach(var line in new[]{"ASOWNER|1|"+Id(1)+"|Lock|~","ASOWNER|1|"+Id(1)+"|Practice|Automatic","ASOWNER|1|"+Id(1)+"|Resume|~","ASOWNER|1|"+Id(1)+"|Lock|A","ASOWNER|1|"+Id(1)+"|Lock|Automatic|condition=A","ASOWNER|1|"+Id(1)+"|Export|/tmp/arbitrary","ASOWNER|2|"+Id(1)+"|Begin|~","ASOWNER|1|not-an-id|Begin|~","ASOWNER|1|"+Id(1)+"|1|~","ASOWNER|1|"+Id(1)+"|Lock|0","ASOWNER|1|"+Id(1)+"|Practice|~\nBegin"})
                    Check.True(!OwnerCommandCodec.TryParse(line,out parsed,out error),line);
                Check.Throws<ArgumentException>(()=>new OwnerCommand(Id(1),OwnerCommandKind.Lock));
            });
            suite.Run("M3 owner acknowledgement escapes delimiters newline and Unicode without command injection",()=>{
                string reason="Rejected | newline\n中文 \\";var command=Command(1);var ack=new OwnerAcknowledgement(command.RequestId,false,reason,OwnerCommandCodec.Hash(command));
                string wire=OwnerCommandCodec.EncodeAcknowledgement(ack);Check.True(wire.IndexOf('\n')<0);OwnerAcknowledgement decoded;Check.True(OwnerCommandCodec.TryParseAcknowledgement(wire,out decoded));Check.Equal(reason,decoded.Reason);
                Check.True(!OwnerCommandCodec.TryParseAcknowledgement(wire+"|extra",out decoded));Check.True(!OwnerCommandCodec.TryParseAcknowledgement(wire.Replace("Rejected","True"),out decoded));
            });
            suite.Run("M3 owner partial lines wait for newline and malformed records acknowledge rejection",()=>WithDirectory(path=>{
                int calls=0;var command=Command(1);string wire=OwnerCommandCodec.Encode(command);
                using(var dispatcher=new OwnerCommandDispatcher(path,"session-a",c=>{calls++;return null;}))
                {
                    File.WriteAllText(path,wire.Substring(0,20),new UTF8Encoding(false));Check.Equal(0,dispatcher.DispatchPending());Check.Equal(0,calls);
                    Check.Throws<IOException>(()=>OwnerCommandFile.Append(path,Command(2)));
                    File.AppendAllText(path,wire.Substring(20)+"\n",new UTF8Encoding(false));Check.Equal(1,dispatcher.DispatchPending());Check.Equal(1,calls);
                    File.AppendAllText(path,"invalid|record\n"+new String('x',300)+"\n",new UTF8Encoding(false));Check.Equal(2,dispatcher.DispatchPending());Check.Equal(1,calls);
                    Check.True(OwnerCommandFile.FindAcknowledgement(path,command).Accepted);
                }
            }));
            suite.Run("M3 owner successful duplicate ID remains idempotent across dispatcher restart",()=>WithDirectory(path=>{
                int calls=0;var command=Command(1);OwnerCommandFile.Append(path,command);OwnerCommandFile.Append(path,command);
                using(var dispatcher=new OwnerCommandDispatcher(path,"session-a",c=>{calls++;return null;})){Check.Equal(2,dispatcher.DispatchPending());Check.Equal(1,calls);Check.Equal(0,dispatcher.DispatchPending());}
                using(var dispatcher=new OwnerCommandDispatcher(path,"session-a",c=>{calls++;return null;})){dispatcher.DispatchPending();Check.Equal(1,calls);OwnerCommandFile.Append(path,command);dispatcher.DispatchPending();Check.Equal(1,calls);}
                Check.True(OwnerCommandFile.FindAcknowledgement(path,command).Accepted);
            }));
            suite.Run("M3 owner same ID different command rejected without overwriting original receipt",()=>WithDirectory(path=>{
                int calls=0;var first=Command(1);var other=Command(1,OwnerCommandKind.Begin);OwnerCommandFile.Append(path,first);OwnerCommandFile.Append(path,other);
                using(var dispatcher=new OwnerCommandDispatcher(path,"session-a",c=>{calls++;return null;})){dispatcher.DispatchPending();}
                Check.Equal(1,calls);Check.True(OwnerCommandFile.FindAcknowledgement(path,first).Accepted);Check.Equal("request_id_conflicts_with_prior_command",OwnerCommandFile.FindAcknowledgement(path,other).Reason);
                using(var dispatcher=new OwnerCommandDispatcher(path,"session-a",c=>{calls++;return null;})){dispatcher.DispatchPending();}Check.Equal(1,calls);
            }));
            suite.Run("M3 owner wrong-phase lock rejected and assigned factors unchanged",()=>WithDirectory(path=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock,Condition.B);string assigned=String.Join("",experiment.Configuration.Allocation.Order);var target=experiment.Configuration.Allocation.BInitialTarget;
                Func<OwnerCommand,string> execute=c=>OwnerCommandExecution.Execute(c,()=>{if(experiment.Phase==Phase.Preparation)experiment.BeginFamiliarization(Actor.Owner);experiment.BeginPractice(Actor.Owner);},selection=>experiment.LockBlock(Actor.Owner,selection,"development-owner-test"),()=>experiment.BeginCountdown(Actor.Owner,false),null);
                var wrong=Command(1,OwnerCommandKind.Lock,ControlSelection.Automatic);OwnerCommandFile.Append(path,wrong);
                using(var dispatcher=new OwnerCommandDispatcher(path,"session-a",execute))
                {
                    dispatcher.DispatchPending();Check.True(!OwnerCommandFile.FindAcknowledgement(path,wrong).Accepted);Check.Equal(Phase.Preparation,experiment.Phase);
                    var practice=Command(2);OwnerCommandFile.Append(path,practice);dispatcher.DispatchPending();Check.Equal(Phase.Practice,experiment.Phase);
                }
                Check.Equal(assigned,String.Join("",experiment.Configuration.Allocation.Order));Check.Equal(target,experiment.Configuration.Allocation.BInitialTarget);Check.True(!experiment.LockedControl.HasValue);
            }));
            suite.Run("M3 owner allowed lock keeps selected source and unavailable readiness blocks begin",()=>WithDirectory(path=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock,Condition.B);Fixtures.Practice(experiment,clock,8);int exports=0;
                Func<OwnerCommand,string> execute=c=>OwnerCommandExecution.Execute(c,null,s=>experiment.LockBlock(Actor.Owner,s,"owner"),()=>experiment.BeginCountdown(Actor.Owner,false),()=>exports++);
                var unavailable=Command(1,OwnerCommandKind.Lock,ControlSelection.LiveWoz);var locked=Command(2,OwnerCommandKind.Lock,ControlSelection.SimulatorRehearsal);var begin=Command(3,OwnerCommandKind.Begin);var switched=Command(4,OwnerCommandKind.Lock,ControlSelection.Automatic);var export=Command(5,OwnerCommandKind.Export);
                foreach(var c in new[]{unavailable,locked,begin,switched,export})OwnerCommandFile.Append(path,c);
                using(var dispatcher=new OwnerCommandDispatcher(path,"session-a",execute)){dispatcher.DispatchPending();}
                Check.Equal("live_woz_adapter_unavailable_in_development_simulation",OwnerCommandFile.FindAcknowledgement(path,unavailable).Reason);Check.True(!OwnerCommandFile.FindAcknowledgement(path,unavailable).Accepted);Check.True(OwnerCommandFile.FindAcknowledgement(path,locked).Accepted);Check.True(!OwnerCommandFile.FindAcknowledgement(path,begin).Accepted);Check.True(!OwnerCommandFile.FindAcknowledgement(path,switched).Accepted);Check.Equal((ControlSelection?)ControlSelection.SimulatorRehearsal,experiment.LockedControl);Check.Equal(Phase.BlockReady,experiment.Phase);Check.Equal(1,exports);
            }));
            suite.Run("M3 owner crash intent and handler exceptions never automatically repeat actions",()=>WithDirectory(path=>{
                var pending=Command(1);var throws=Command(2);int calls=0;OwnerCommandFile.Append(path,pending);
                File.WriteAllText(path+".pending","ASOWNERPENDING|1|"+pending.RequestId+"|"+OwnerCommandCodec.Hash(pending)+"\n",new UTF8Encoding(false));
                using(var dispatcher=new OwnerCommandDispatcher(path,"session-a",c=>{calls++;throw new InvalidOperationException("possible side effect");}))
                {dispatcher.DispatchPending();Check.Equal(0,calls);OwnerCommandFile.Append(path,throws);OwnerCommandFile.Append(path,throws);dispatcher.DispatchPending();Check.Equal(1,calls);}
                Check.True(OwnerCommandFile.FindAcknowledgement(path,pending).Reason.Contains("uncertain"));Check.True(OwnerCommandFile.FindAcknowledgement(path,throws).Reason.Contains("uncertain"));
                using(var dispatcher=new OwnerCommandDispatcher(path,"session-a",c=>{calls++;return null;})){dispatcher.DispatchPending();}Check.Equal(1,calls);
            }));
            suite.Run("M3 owner exclusive dispatcher session binding damaged journals and truncation fail closed",()=>WithDirectory(path=>{
                Check.Throws<ArgumentException>(()=>new OwnerCommandDispatcher("relative.txt","session-a",c=>null));
                using(var dispatcher=new OwnerCommandDispatcher(path,"session-a",c=>null))
                {
                    Check.Throws<IOException>(()=>new OwnerCommandDispatcher(path,"session-a",c=>null));OwnerCommandFile.Append(path,Command(1));dispatcher.DispatchPending();
                    File.WriteAllText(path,"");Check.Throws<IOException>(()=>dispatcher.DispatchPending());
                }
                Check.Throws<InvalidOperationException>(()=>new OwnerCommandDispatcher(path,"session-b",c=>null));
                File.AppendAllText(path+".ack","partial");Check.Throws<IOException>(()=>new OwnerCommandDispatcher(path,"session-a",c=>null));
            }));
        }
    }
}
