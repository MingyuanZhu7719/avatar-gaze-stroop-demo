using System;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Pure synthetic clocks and byte arrays. No socket, headset or filesystem.
    public static class ViveWozSourceConfirmationTests
    {
        sealed class Clock:IClock
        {double now;public bool Throw;public string ClockId {get;set;} public double Now {get{if(Throw)throw new InvalidOperationException();return now;}set{now=value;}}}
        sealed class Reader:IViveWozSourceStateReader
        {
            public TaskSnapshot Task;public ControlSelection? Selection=ControlSelection.LiveWoz;
            public bool Explicit=true,Fresh=true,Throw;public string Session="session",Owner="owner";public long Revision;public Action BeforeRead;
            public ViveWozSourceLocalState ReadCurrent()
            {if(BeforeRead!=null)BeforeRead();if(Throw)throw new InvalidOperationException();return new ViveWozSourceLocalState(Session,Owner,Task,Selection,Explicit,Fresh,Revision);}
        }
        sealed class Fixture
        {
            public readonly Clock Sender=new Clock{ClockId="sender",Now=3.6},Receiver=new Clock{ClockId="receiver",Now=3};
            public readonly ViveWozSourceContext Context;public readonly ClockSynchronizer Mapping;
            public readonly Reader Reader=new Reader();
            public readonly ViveWozSourceOwner Owner;public readonly ViveWozSourceReceiver Gate;
            public Fixture(double maxAge=.5,double maxUncertainty=.1)
            {
                string stream=ViveDevelopmentTransport.OperatorStream("map","epoch");
                Context=new ViveWozSourceContext("session","owner",stream,"map","epoch","fresh-challenge","sender","receiver",0,0,Condition.B);
                Mapping=new ClockSynchronizer("receiver","sender",new DevelopmentClockConfiguration(3,.2,.2,4,200));
                var peer=new PeerIdentity("session","remote",PeerRole.Operator,stream,ControlSource.LiveOperator,"sender");
                for(int i=0;i<3;i++){string id="clock-"+i;double t=i;Check.True(Mapping.RegisterRequest(id,t));
                    Check.True(Mapping.ObserveReply(WirePacket.TimeReply(peer,i,t+.5,id,"receiver",t,t+.5),t));}
                Reader.Task=Task();Owner=new ViveWozSourceOwner(Context,Sender,Reader);Gate=new ViveWozSourceReceiver(Context,Receiver,Mapping,Reader,maxAge,maxUncertainty,true);
            }
            public TaskSnapshot Task(Phase phase=Phase.BlockReady,int block=0,int segment=0,Condition condition=Condition.B,string session="session")
            {return new TaskSnapshot(session,block,segment,null,null,null,phase,condition);}
            public byte[] Confirm(){return Owner.Confirm(true);}
            public bool Accept(byte[] bytes){Receiver.Now=3.2;return Gate.Observe(bytes,true);}
        }
        static string Change(byte[] packet,int index,string value)
        {var fields=Encoding.ASCII.GetString(packet).Split('|');fields[index]=value;return String.Join("|",fields);}
        static void Invalid(string wire)
        {ViveWozSourceMessage message;string error;Check.True(!ViveWozSourceWire.TryDecode(Encoding.UTF8.GetBytes(wire),out message,out error));Check.Equal(null,message);}
        public static void Register(TestSuite suite)
        {
            suite.Run("WOZ source messages require explicit local and owner choices",()=>{
                var f=new Fixture();Check.Throws<InvalidOperationException>(()=>f.Owner.Confirm(false));
                f.Reader.Selection=ControlSelection.Automatic;Check.Throws<InvalidOperationException>(()=>f.Owner.Confirm(true));
                Check.Throws<InvalidOperationException>(()=>new ViveWozSourceReceiver(f.Context,f.Receiver,f.Mapping,f.Reader,.5,.1,false));
                Check.True(!f.Owner.LocallyConfirmed&&!f.Gate.CanEnterCountdown&&!f.Gate.CryptographicallyAuthenticated);
            });
            suite.Run("WOZ source codec round trips bounded context and exact time",()=>{
                var f=new Fixture();byte[] bytes=f.Confirm();ViveWozSourceMessage message;string error;
                Check.True(ViveWozSourceWire.TryDecode(bytes,out message,out error),error);Check.Equal(ViveWozSourceAction.Confirm,message.Action);
                Check.Equal("session",message.Context.SessionId);Check.Equal("owner",message.Context.OwnerId);Check.Equal("epoch",message.Context.Epoch);
                Check.Equal("fresh-challenge",message.Context.Challenge);Check.Equal("sender",message.Context.SenderClockId);
                Check.Equal("receiver",message.Context.ReceiverClockId);Check.Equal(Condition.B,message.Context.Condition);Check.Equal(0L,message.Sequence);
                Check.Equal(BitConverter.DoubleToInt64Bits(3.6),BitConverter.DoubleToInt64Bits(message.SentLocalSeconds));
                Check.True(f.Accept(bytes),f.Gate.LastIssue);f.Reader.Task=f.Task(Phase.Countdown);f.Reader.Revision++;Check.True(f.Gate.Candidate());
                f.Reader.Task=f.Task(Phase.MainTask);f.Reader.Revision++;Check.True(f.Gate.Candidate());
                Check.True(!f.Gate.CanEnterCountdown&&!f.Gate.CryptographicallyAuthenticated);
            });
            suite.Run("WOZ source wire rejects noncanonical malformed oversized and wrong protocol",()=>{
                var f=new Fixture();var bytes=f.Confirm();var wire=Encoding.ASCII.GetString(bytes);
                Invalid("");Invalid(new string('x',2049));Invalid(wire+"\n");Invalid(wire+"|extra");Invalid(Change(bytes,0,"ASW"));Invalid(Change(bytes,1,"2"));
                Invalid(Change(bytes,2,"confirm"));Invalid(Change(bytes,3,"!!!!"));Invalid(Change(bytes,11,"00"));Invalid(Change(bytes,12,"-1"));
                Invalid(Change(bytes,13,"0"));Invalid(Change(bytes,14,"+0"));Invalid(Change(bytes,15,"7ff8000000000000"));
            });
            suite.Run("WOZ duplicate or reordered confirmation never extends the candidate",()=>{
                var f=new Fixture();var bytes=f.Confirm();Check.True(f.Accept(bytes));f.Receiver.Now=3.21;
                Check.True(!f.Gate.Observe(bytes,true));Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);
                Check.True(f.Gate.MustPauseLiveTask&&f.Gate.MustClearLivePose&&!f.Gate.Candidate());
            });
            suite.Run("WOZ explicit revoke clears local and remote state and is terminal",()=>{
                var f=new Fixture();var bytes=f.Confirm();Check.True(f.Accept(bytes));f.Sender.Now=3.7;
                var revoke=f.Owner.Revoke(true);Check.True(!f.Owner.LocallyConfirmed&&f.Owner.Terminal);
                f.Receiver.Now=3.3;f.Reader.Task=f.Task(Phase.Countdown);f.Reader.Revision++;Check.True(f.Gate.Candidate());
                f.Reader.Task=f.Task(Phase.MainTask);f.Reader.Revision++;Check.True(f.Gate.Observe(revoke,true));
                Check.Equal(ViveWozSourceEvidenceState.Revoked,f.Gate.State);Check.True(f.Gate.MustPauseLiveTask&&f.Gate.MustClearLivePose);
                Check.True(!f.Gate.Observe(bytes,true));Check.Throws<InvalidOperationException>(()=>f.Owner.Confirm(true));
            });
            suite.Run("WOZ older or replayed revoke fails closed rather than reviving live source",()=>{
                var f=new Fixture();var confirm=f.Confirm();Check.True(f.Accept(confirm));f.Sender.Now=3.7;var revoke=f.Owner.Revoke(true);
                var old=Encoding.ASCII.GetBytes(Change(revoke,14,"0"));f.Receiver.Now=3.3;Check.True(!f.Gate.Observe(old,true));
                Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);Check.True(f.Gate.MustPauseLiveTask&&f.Gate.MustClearLivePose);
            });
            suite.Run("WOZ rejects foreign epoch challenge owner endpoint and task",()=>{
                foreach(int field in new[]{3,4,7,8}){var f=new Fixture();var bytes=f.Confirm();f.Receiver.Now=3.2;
                    string other=Convert.ToBase64String(Encoding.UTF8.GetBytes("other"));Check.True(!f.Gate.Observe(Encoding.ASCII.GetBytes(Change(bytes,field,other)),true));
                    Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);}
                var fixture=new Fixture();var packet=fixture.Confirm();fixture.Receiver.Now=3.2;Check.True(!fixture.Gate.Observe(packet,false));
                fixture=new Fixture();packet=fixture.Confirm();fixture.Receiver.Now=3.2;fixture.Reader.Task=fixture.Task(condition:Condition.D);Check.True(!fixture.Gate.Observe(packet,true));
                fixture=new Fixture();packet=fixture.Confirm();fixture.Receiver.Now=3.2;fixture.Reader.Fresh=false;Check.True(!fixture.Gate.Observe(packet,true));
            });
            suite.Run("WOZ requires bounded fresh mapped send after the active context",()=>{
                var f=new Fixture();var packet=f.Confirm();f.Receiver.Now=4;Check.True(!f.Gate.Observe(packet,true));Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);
                f=new Fixture();f.Sender.Now=3.4;packet=f.Confirm();f.Receiver.Now=3.2;Check.True(!f.Gate.Observe(packet,true));
                f=new Fixture();f.Sender.Now=4;packet=f.Confirm();f.Receiver.Now=3.2;Check.True(!f.Gate.Observe(packet,true));
            });
            suite.Run("WOZ candidate expires and cannot be restored by late replay",()=>{
                var f=new Fixture();var packet=f.Confirm();Check.True(f.Accept(packet));f.Receiver.Now=3.7;
                Check.True(!f.Gate.Candidate());Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);
                Check.True(!f.Gate.Observe(packet,true));
            });
            suite.Run("WOZ context change loss and pause invalidate candidate",()=>{
                var f=new Fixture();var packet=f.Confirm();Check.True(f.Accept(packet));f.Reader.Task=f.Task(Phase.Paused);Check.True(!f.Gate.Candidate());Check.True(f.Gate.MustPauseLiveTask);
                f=new Fixture();packet=f.Confirm();Check.True(f.Accept(packet));f.Reader.Task=f.Task(segment:1);Check.True(!f.Gate.Candidate());
                f=new Fixture();packet=f.Confirm();Check.True(f.Accept(packet));f.Gate.SignalLoss("peer_missing");Check.True(f.Gate.MustPauseLiveTask&&!f.Gate.Candidate());
            });
            suite.Run("WOZ owner and receiver recheck bound live selection and local consent",()=>{
                var f=new Fixture();var packet=f.Confirm();Check.True(f.Accept(packet));f.Reader.Selection=ControlSelection.Automatic;
                Check.True(!f.Owner.LocallyConfirmed&&f.Owner.Terminal);Check.True(!f.Gate.Candidate()&&f.Gate.MustPauseLiveTask);
                f=new Fixture();packet=f.Confirm();Check.True(f.Accept(packet));f.Reader.Explicit=false;Check.True(!f.Gate.Candidate());
                f=new Fixture();packet=f.Confirm();Check.True(f.Accept(packet));f.Reader.Throw=true;Check.True(!f.Gate.Candidate());
            });
            suite.Run("WOZ phase cannot move backward within an epoch",()=>{
                var f=new Fixture();var packet=f.Confirm();Check.True(f.Accept(packet));f.Reader.Task=f.Task(Phase.Countdown);f.Reader.Revision++;Check.True(f.Gate.Candidate());
                f.Reader.Task=f.Task(Phase.MainTask);f.Reader.Revision++;Check.True(f.Gate.Candidate());
                f.Reader.Task=f.Task(Phase.BlockReady);f.Reader.Revision++;Check.True(!f.Gate.Candidate());Check.True(f.Gate.MustPauseLiveTask);
                f=new Fixture();packet=f.Confirm();Check.True(f.Accept(packet));f.Reader.Task=f.Task(Phase.MainTask);f.Reader.Revision++;
                Check.True(!f.Gate.Candidate());Check.True(f.Gate.MustPauseLiveTask);
            });
            suite.Run("WOZ rejects a changed task under the same revision and revision rollback",()=>{
                var f=new Fixture();var packet=f.Confirm();Check.True(f.Accept(packet));f.Reader.Task=f.Task(Phase.Countdown);
                Check.True(!f.Gate.Candidate());Check.True(f.Gate.MustPauseLiveTask);
                f=new Fixture();packet=f.Confirm();Check.True(f.Accept(packet));f.Reader.Revision=1;Check.True(f.Gate.Candidate());
                f.Reader.Revision=0;Check.True(!f.Gate.Candidate());Check.True(f.Gate.MustPauseLiveTask);
                f=new Fixture();packet=f.Confirm();Check.True(f.Accept(packet));f.Reader.Task=f.Task(Phase.Countdown);f.Reader.Revision=1;Check.True(f.Gate.Candidate());
                // Synthetic unobserved Countdown -> Paused -> Countdown: task
                // appears unchanged, but the complete revision stream skipped.
                f.Reader.Revision=3;Check.True(!f.Gate.Candidate());Check.True(f.Gate.MustPauseLiveTask);
            });
            suite.Run("WOZ clock refinements cannot extend the original confirmation deadline",()=>{
                var f=new Fixture();var map=new ClockSynchronizer("receiver","sender",new DevelopmentClockConfiguration(3,.2,.5,10,200));
                var peer=new PeerIdentity("session","remote",PeerRole.Operator,f.Context.OperatorStreamId,ControlSource.LiveOperator,"sender");
                for(int i=0;i<3;i++){double t=i;string id="uncertain-"+i;Check.True(map.RegisterRequest(id,t));
                    Check.True(map.ObserveReply(WirePacket.TimeReply(peer,i,t+.6,id,"receiver",t,t+.6),t+.2),map.LastRejection);}
                f.Receiver.Now=2.5;var gate=new ViveWozSourceReceiver(f.Context,f.Receiver,map,f.Reader,1,.5,true);
                f.Sender.Now=3.7;var packet=f.Owner.Confirm(true);f.Receiver.Now=3.5;
                double local,uncertainty;Check.True(map.TryMapRemote(3.7,out local,out uncertainty));double originalDeadline=local-uncertainty+1;
                Check.True(gate.Observe(packet,true),gate.LastIssue);
                for(int i=0;i<64;i++){double t=2.21+i*.01;string id="precise-"+i;Check.True(map.RegisterRequest(id,t));
                    Check.True(map.ObserveReply(WirePacket.TimeReply(peer,i+3,t+.5,id,"receiver",t,t+.5),t),map.LastRejection);}
                Check.True(map.TryMapRemote(3.7,out local,out uncertainty));double revisedDeadline=local-uncertainty+1;
                Check.True(revisedDeadline>originalDeadline+.001,"fixture did not narrow mapping uncertainty");
                f.Receiver.Now=(originalDeadline+revisedDeadline)/2;Check.True(!gate.Candidate());Check.Equal(ViveWozSourceEvidenceState.Faulted,gate.State);
            });
            suite.Run("WOZ clock failures and uncertain send are terminal",()=>{
                var f=new Fixture();var packet=f.Confirm();f.Owner.SignalSendFailure();Check.True(!f.Owner.LocallyConfirmed&&f.Owner.Terminal);
                Check.True(f.Accept(packet));f.Receiver.Now=2;Check.True(!f.Gate.Candidate());Check.True(f.Gate.MustPauseLiveTask);
                f=new Fixture();packet=f.Confirm();f.Receiver.Throw=true;Check.True(!f.Gate.Observe(packet,true));Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);
                f=new Fixture();f.Sender.Throw=true;Check.Throws<InvalidOperationException>(()=>f.Owner.Confirm(true));Check.True(f.Owner.Terminal);
            });
            suite.Run("WOZ source state requires its constructing thread",()=>{
                var f=new Fixture();var packet=f.Confirm();Exception caught=null;var thread=new Thread(()=>{try{f.Gate.Observe(packet,true);}catch(Exception e){caught=e;}});
                thread.Start();Check.True(thread.Join(3000));Check.True(caught is InvalidOperationException);Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);
                Check.True(!f.Gate.Candidate()&&f.Gate.MustClearLivePose&&f.Gate.MustPauseLiveTask);
            });
            suite.Run("WOZ source receiver cross-thread loss latches an active candidate",()=>{
                var f=new Fixture();var packet=f.Confirm();Check.True(f.Accept(packet));Check.True(f.Gate.Candidate());Exception caught=null;
                var thread=new Thread(()=>{try{f.Gate.SignalLoss("synthetic");}catch(Exception e){caught=e;}});
                thread.Start();Check.True(thread.Join(3000));Check.True(caught is InvalidOperationException);
                Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);Check.Equal("receiver_thread_violation",f.Gate.LastIssue);
                Check.True(f.Gate.MustPauseLiveTask&&f.Gate.MustClearLivePose);
                Check.True(!f.Gate.Candidate()&&!f.Gate.Observe(packet,true));
            });
            suite.Run("WOZ safety getters fail closed on wrong thread before and after confirmation",()=>{
                foreach(bool active in new[]{false,true})foreach(bool pause in new[]{false,true})
                {var f=new Fixture();if(active){var packet=f.Confirm();Check.True(f.Accept(packet));}
                    bool safety=false;Exception caught=null;var thread=new Thread(()=>{try{safety=pause?f.Gate.MustPauseLiveTask:f.Gate.MustClearLivePose;}catch(Exception e){caught=e;}});
                    thread.Start();Check.True(thread.Join(3000));Check.True(caught==null&&safety);
                    Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);Check.Equal("receiver_thread_violation",f.Gate.LastIssue);
                    Check.True(f.Gate.MustPauseLiveTask&&f.Gate.MustClearLivePose&&!f.Gate.Candidate());}
            });
            suite.Run("WOZ source inspection on wrong thread cannot preserve confirmation",()=>{
                foreach(bool terminalQuery in new[]{false,true})
                {var f=new Fixture();f.Confirm();Check.True(f.Owner.LocallyConfirmed);bool result=terminalQuery?false:true;Exception caught=null;
                    var thread=new Thread(()=>{try{result=terminalQuery?f.Owner.Terminal:f.Owner.LocallyConfirmed;}catch(Exception e){caught=e;}});
                    thread.Start();Check.True(thread.Join(3000));Check.True(caught==null);Check.Equal(terminalQuery,result);
                    Check.True(f.Owner.Terminal&&!f.Owner.LocallyConfirmed);}
                var g=new Fixture();var packet=g.Confirm();Check.True(g.Accept(packet));ViveWozSourceEvidenceState state=ViveWozSourceEvidenceState.Awaiting;
                var other=new Thread(()=>state=g.Gate.State);other.Start();Check.True(other.Join(3000));
                Check.Equal(ViveWozSourceEvidenceState.Faulted,state);Check.True(g.Gate.MustPauseLiveTask&&g.Gate.MustClearLivePose);
            });
            suite.Run("WOZ source receiver rejects thread misuse during state callbacks",()=>{
                var f=new Fixture();var packet=f.Confirm();Exception caught=null;
                f.Reader.BeforeRead=()=>{var thread=new Thread(()=>{try{f.Gate.SignalLoss("synthetic");}catch(Exception e){caught=e;}});
                    thread.Start();Check.True(thread.Join(3000));};
                f.Receiver.Now=3.2;Check.True(!f.Gate.Observe(packet,true));Check.True(caught is InvalidOperationException);
                Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);Check.True(f.Gate.MustPauseLiveTask&&f.Gate.MustClearLivePose);
                f=new Fixture();packet=f.Confirm();Check.True(f.Accept(packet));caught=null;
                f.Reader.BeforeRead=()=>{var thread=new Thread(()=>{try{f.Gate.SignalLoss("synthetic");}catch(Exception e){caught=e;}});
                    thread.Start();Check.True(thread.Join(3000));};
                Check.True(!f.Gate.Candidate());Check.True(caught is InvalidOperationException);
                Check.True(f.Gate.MustPauseLiveTask&&f.Gate.MustClearLivePose);
            });
            suite.Run("WOZ source owner cross-thread misuse makes local confirmation terminal",()=>{
                var f=new Fixture();f.Confirm();Check.True(f.Owner.LocallyConfirmed);Exception caught=null;
                var thread=new Thread(()=>{try{f.Owner.Revoke(true);}catch(Exception e){caught=e;}});
                thread.Start();Check.True(thread.Join(3000));Check.True(caught is InvalidOperationException);
                Check.True(f.Owner.Terminal&&!f.Owner.LocallyConfirmed);Check.Throws<InvalidOperationException>(()=>f.Owner.Confirm(true));
                f=new Fixture();caught=null;f.Reader.BeforeRead=()=>{var t=new Thread(()=>{try{f.Owner.SignalSendFailure();}catch(Exception e){caught=e;}});
                    t.Start();Check.True(t.Join(3000));};
                Check.Throws<InvalidOperationException>(()=>f.Owner.Confirm(true));Check.True(caught is InvalidOperationException);
                Check.True(f.Owner.Terminal&&!f.Owner.LocallyConfirmed);
            });
            suite.Run("WOZ invalid loss reason still clears an active candidate",()=>{
                foreach(string reason in new string[]{null,""," "})
                {var f=new Fixture();var packet=f.Confirm();Check.True(f.Accept(packet));Check.Throws<ArgumentException>(()=>f.Gate.SignalLoss(reason));
                    Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);Check.True(f.Gate.MustPauseLiveTask&&f.Gate.MustClearLivePose);}
            });
        }
        public static int Main(string[] args)
        {var suite=new TestSuite();Register(suite);Console.WriteLine("WOZ source confirmation: "+suite.Passed+" passed, "+suite.Failed+" failed");return suite.Failed==0?0:1;}
    }
}
