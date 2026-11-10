using System;
using System.Net;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveLegacyWozSceneTests
    {
        static int passed,failed;
        static void Assert(bool value,string label){if(!value)throw new Exception(label);}
        static void Eq<T>(T expected,T actual,string label){Assert(Object.Equals(expected,actual),label+": expected "+expected+", got "+actual);}
        static void Test(string name,Action body){try{body();passed++;Console.WriteLine("PASS scene-memory "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL scene-memory "+name+" "+e);}}
        sealed class Fixture:IDisposable
        {
            internal readonly MonotonicClock PC=new MonotonicClock("scene-p-"+Guid.NewGuid().ToString("N")),OC=new MonotonicClock("scene-o-"+Guid.NewGuid().ToString("N"));
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveLegacyWozTaskOwner Owner;
            internal readonly ViveLegacyWozObservationOwner.SyntheticObservations Observations;
            internal readonly ViveLegacyWozParticipantProcess P;
            internal readonly ViveLegacyWozOperatorProcess O;
            internal readonly ViveLegacyWozControlledScene Scene;
            internal readonly ViveLegacyWozControlledScene.MemorySink Sink=new ViveLegacyWozControlledScene.MemorySink();
            internal readonly ViveLegacyWozProcessTransport.MemorySocket PS=new ViveLegacyWozProcessTransport.MemorySocket(),OS=new ViveLegacyWozProcessTransport.MemorySocket();
            long sequence;
            internal Fixture()
            {
                Task=ViveLegacyWozProcessMemoryTests.Prepare(PC);Owner=Task.CreateLegacyWozTaskOwner(PC);Observations=new ViveLegacyWozObservationOwner.SyntheticObservations(OC);PS.Other=OS;OS.Other=PS;
                var pe=new IPEndPoint(IPAddress.Loopback,45171);var oe=new IPEndPoint(IPAddress.Loopback,45172);
                using(var offer=ViveLegacyWozProcessPairing.CreateSyntheticOffer(Owner,"scene-owner",OC.ClockId))
                {var token=offer.RevealToken();try{using(var imported=ViveLegacyWozProcessPairing.Parse(token))
                    {P=ViveLegacyWozParticipantProcess.Memory(offer,Owner,pe,oe,2,PS);O=ViveLegacyWozOperatorProcess.Synthetic(imported,OC,Observations,oe,pe,2,OS);}}
                    finally{Array.Clear(token,0,token.Length);}}
                Scene=ViveLegacyWozControlledScene.CreateMemoryFixture(P,Sink);
                Assert(O.ConfirmLocalOperatorSource(),"operator explicit consent");Assert(O.Start(),"operator start");Assert(P.Start(),"participant start");
            }
            internal void Fresh(){sequence++;Observations.Fresh(sequence,(int)sequence,(int)sequence);}
            internal void Deliver()
            {O.Tick();Fresh();O.PollPhasePreflight();Fresh();O.PollObservation();P.Tick();if(P.Terminal||O.Terminal) Assert(false,"role terminal: P="+P.LastIssue+", O="+O.LastIssue+", transport="+ParticipantTransportIssue(P));}
            static string ParticipantTransportIssue(ViveLegacyWozParticipantProcess participant)
            {
                // Failure-only diagnostics avoid altering successful initialization timing.
                // LastIssue is a fixed transport code; no packet, key or identity is logged.
                var field=typeof(ViveLegacyWozParticipantProcess).GetField("transport",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                return ((ViveLegacyWozProcessTransport)field.GetValue(participant)).LastIssue;
            }
            internal void Cycle()
            {Scene.PumpBeforeTask();Deliver();Assert(Scene.PumpBeforeTask(),"scene proof pending: "+P.LastIssue);}
            internal void Main()
            {
                Cycle();Assert(Scene.CanBeginCountdown,"BlockReady proof");Assert(Scene.BeginCountdown(),"owned countdown");
                double deadline=PC.Now+4;
                while(Task.Phase==Phase.Countdown&&PC.Now<deadline){Cycle();Task.Tick();Scene.AfterTaskMutation();Thread.Sleep(5);}
                Eq(Phase.MainTask,Task.Phase,"actual countdown elapsed");Cycle();
            }
            internal void Present()
            {Scene.Present();Assert(Scene.MayShowTask,"prepared current display");Task.MarkPresented(Task.CurrentTrial.TrialId);Scene.NotePresented(Task.CurrentTrial.TrialId);Scene.AfterTaskMutation();}
            public void Dispose(){Scene.Dispose();P.Dispose();O.Dispose();}
        }
        public static int Main(string[] args)
        {
            int baseline=args.Length>0&&args[0]=="--scene-only"?0:ViveLegacyWozProcessMemoryTests.Main(new string[0]);
            Test("explicit owned BlockReady proof enables only controlled unrecorded countdown",()=>{
                using(var f=new Fixture()){Assert(!f.Scene.CanBeginCountdown,"no proof before round trip");f.Cycle();Assert(f.Scene.CanBeginCountdown,"current memory fixture proof");
                    Assert(!f.Scene.RecordingAllowed&&!f.Scene.HardwareValidated&&!f.P.CanEnterLiveCountdown,"no promotion of general gates");Assert(f.Scene.BeginCountdown(),"actual owner countdown");Eq(Phase.Countdown,f.Task.Phase,"countdown phase");}
            });
            Test("same-context renewal retains original proof until replacement without clearing",()=>{
                using(var f=new Fixture()){f.Cycle();var old=f.P.DisplayPreflight;int clears=f.Sink.Clears;Thread.Sleep(60);
                    Assert(f.Scene.PumpBeforeTask(),"old original-fresh proof retained during renewal");Eq(clears,f.Sink.Clears,"no renewal masking");Eq(old,f.P.DisplayPreflight,"same original evidence");
                    f.Deliver();Assert(f.Scene.MayShowTask,"replacement current");Assert(!Object.ReferenceEquals(old,f.P.DisplayPreflight),"replacement accepted");
                    Assert(f.P.DisplayPreflight.OriginalTaskIssuedParticipantSeconds>old.OriginalTaskIssuedParticipantSeconds,"replacement has distinct actual task issue");}
            });
            Test("actual presentation event keeps onset and wire context but real response invalidates",()=>{
                using(var f=new Fixture()){f.Main();var pose=f.P.DisplayPose;ViveLegacyWozTaskOwner.Binding before;Assert(f.Owner.TryCapture(out before),"capture before onset");
                    f.Present();ViveLegacyWozTaskOwner.Binding after;Assert(f.Owner.TryCapture(out after),"capture after onset");Assert(after.Revision>before.Revision,"actual presentation journal revision");Eq(before.WireRevision,after.WireRevision,"response-free wire context unchanged");
                    Eq(pose,f.P.DisplayPose,"onset does not invalidate pose");Assert(f.Scene.MayShowTask,"no onset masking");double? onset=f.Task.CurrentTrial.Onset;Assert(onset.HasValue,"actual presentation onset");int clears=f.Sink.Clears;Thread.Sleep(60);Assert(f.Scene.PumpBeforeTask(),"renewal during visible trial");Eq(clears,f.Sink.Clears,"visible trial remains uninterrupted");f.Deliver();Eq(onset,f.Task.CurrentTrial.Onset,"original onset unchanged by renewal");
                    string first=f.Task.CurrentTrial.TrialId;ViveLegacyWozProcessMemoryTests.Respond(f.Task);f.Scene.AfterTaskMutation();Assert(f.Task.CurrentTrial.TrialId!=first,"actual response advances trial");Assert(!f.Scene.MayShowTask&&f.P.DisplayPose==null,"old pose rejected for new trial");}
            });
            Test("post-onset short freshness loss pauses instead of redisplaying same trial",()=>{
                using(var f=new Fixture()){f.Main();f.Present();var trial=f.Task.CurrentTrial;Thread.Sleep(170);Assert(!f.Scene.PumpBeforeTask(),"expired visual proof");Eq(Phase.Paused,f.Task.Phase,"actual pause before control timeout");Eq(TrialOutcome.Interrupted,trial.Outcome,"presented trial interrupted");Assert(f.P.Terminal&&f.Scene.Closed&&!f.Scene.MayShowTask&&f.Sink.Output==null,"no automatic redisplay");}
            });
            Test("pre-cull recheck catches stall after prepared frame and clears",()=>{
                using(var f=new Fixture()){f.Main();f.Scene.Present();Thread.Sleep(170);f.Scene.RecheckPresentation();Eq(Phase.Paused,f.Task.Phase,"prepared frame stall pauses");Assert(f.Scene.Closed&&f.Sink.Output==null,"pre-cull masks stale output");}
            });
            Test("apply exception attempts clear and pauses actual owner",()=>{
                using(var f=new Fixture()){f.Main();int clears=f.Sink.Clears;f.Sink.DuringApply=()=>{throw new InvalidOperationException("fixture apply");};f.Scene.Present();Assert(f.Sink.Clears>clears&&f.Sink.Output==null,"clear attempted after partial apply");Eq(Phase.Paused,f.Task.Phase,"actual task paused");Assert(f.P.Terminal,"epoch terminal");}
            });
            Test("uncertain clear suppresses safety ack and replacement owner",()=>{
                using(var f=new Fixture()){f.Cycle();f.Sink.FailClear=true;Assert(f.O.RequestSafetyStop(),"operator stop sent");f.P.Tick();Assert(!f.P.SafetyStopAcknowledged,"no false clear ack");Eq(Phase.Paused,f.Task.Phase,"pause still attempted");bool rejected=false;try{f.Task.CreateLegacyWozTaskOwner(f.PC);}catch(InvalidOperationException){rejected=true;}Assert(rejected,"sticky cleanup barrier");}
            });
            Test("initial pre-onset freshness wait cannot invent onset or automatically pause",()=>{
                using(var f=new Fixture()){f.Main();Assert(!f.Task.CurrentTrial.Onset.HasValue,"not yet presented");Thread.Sleep(170);Assert(!f.Scene.PumpBeforeTask(),"stale initial proof masks");Eq(Phase.MainTask,f.Task.Phase,"initial acquisition may wait");Assert(!f.Task.CurrentTrial.Onset.HasValue,"waiting never fabricates onset");f.Deliver();Assert(f.Scene.PumpBeforeTask(),"new exact proof permits first presentation");}
            });
            Test("readiness exception at render boundary attempts clear and pauses immediately",()=>{
                using(var f=new Fixture()){f.Cycle();int clears=f.Sink.Clears;f.Sink.FailReady=true;f.Scene.RecheckPresentation();Assert(f.Sink.Clears>clears,"read failure clear attempted");Eq(Phase.Paused,f.Task.Phase,"read failure actual pause");Assert(f.P.Terminal&&f.Scene.Closed,"read failure terminal");}
            });
            Test("owner safety mutation during apply cannot leave output or fallback",()=>{
                using(var f=new Fixture()){f.Main();f.Sink.DuringApply=()=>f.Task.OwnerPause(Actor.Owner);f.Scene.Present();Assert(f.Sink.Output==null&&!f.Scene.MayShowTask,"same-frame authority loss cleared");Eq(Phase.Paused,f.Task.Phase,"actual owner pause");Assert(f.P.CurrentPose==null&&f.P.Terminal,"no prior or Automatic pose fallback");}
            });
            Test("full actual retained BCAD session preserves epoch counters and task-only liveness",()=>{
                using(var f=new Fixture())
                {
                    string epoch=null;long lastPose=-1,choice=-1;var conditions=new[]{Condition.B,Condition.C,Condition.A,Condition.D};
                    for(int block=0;block<4;block++)
                    {
                        Eq(block,f.Task.BlockIndex,"actual block");f.Main();Eq(conditions[block],f.Owner.SceneTask.Condition.Value,"actual BCAD order");
                        for(int trial=0;trial<24;trial++)
                        {
                            f.Cycle();f.Present();
                            if(block<3)
                            {
                                var pose=f.P.DisplayPose;Assert(pose!=null,"avatar block pose");Assert(pose.Pose.Sequence>lastPose,"epoch emission does not reset across trial/block");lastPose=pose.Pose.Sequence;
                                if(epoch==null){epoch=pose.Epoch;choice=pose.ChoiceGeneration;}else{Eq(epoch,pose.Epoch,"same paired epoch");Eq(choice,pose.ChoiceGeneration,"retained explicit choice");}
                            }
                            else
                            {
                                Assert(f.P.DisplayPose==null&&f.P.DisplayPreflight==null,"D invents no source evidence");Assert(f.P.DisplayTaskAcknowledged,"D correlated task receipt");Assert(f.Sink.Output!=null&&!f.Sink.Output.AvatarVisible,"D clears avatar only");
                                if(trial==0){int clears=f.Sink.Clears;Thread.Sleep(60);Assert(f.Scene.PumpBeforeTask(),"D retained acknowledgment during renewal");Eq(clears,f.Sink.Clears,"D no routine masking");f.Deliver();}
                            }
                            ViveLegacyWozProcessMemoryTests.Respond(f.Task);f.Scene.AfterTaskMutation();
                        }
                        Eq(Phase.Questionnaire,f.Task.Phase,"real questionnaire");f.Cycle();Assert(f.P.DisplayPreflight==null&&f.P.DisplayPose==null&&f.P.DisplayTaskAcknowledged,"questionnaire task-only liveness");
                        f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);f.Scene.AfterTaskMutation();
                        Assert(!f.Task.QuestionnaireInstructionPending,"actual instruction acknowledged locally");
                        for(int question=0;question<5;question++){f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);f.Scene.AfterTaskMutation();}
                        Eq((block+1)*5,f.Task.RatingCount,"local-only ratings");
                        if(block<3)
                        {
                            Eq(Phase.Rest,f.Task.Phase,"real rest");f.Cycle();Assert(f.P.DisplayTaskAcknowledged&&f.P.DisplayPreflight==null,"rest task-only liveness");
                            f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);f.Scene.AfterTaskMutation();
                            Eq(Phase.BlockReady,f.Task.Phase,"retained next block");Eq(ControlSelection.LiveWoz,f.Task.LockedControl.Value,"retained actual source");
                            Assert(!f.Scene.MayShowTask,"old phase evidence invalidated");
                        }
                    }
                    Eq(Phase.Ended,f.Task.Phase,"actual session ended");Eq(EndReason.Completed,f.Task.EndReason.Value,"completed reason");Eq(104,f.Task.TrialCount,"practice plus all main trials");Eq(20,f.Task.RatingCount,"all local ratings");
                    Assert(f.P.Terminal&&f.Scene.Closed,"completion closes epoch without resuming");
                }
            });
            Console.WriteLine("RESULT scene-memory "+passed+" passed; "+failed+" failed; baseline_exit "+baseline);
            return failed==0&&baseline==0?0:1;
        }
    }
}
