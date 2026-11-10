using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Explicit allowlist entry point. No socket, native SDK, file store,
    // installed client, Unity player or global TestRunner is executed.
    public static class ViveLegacyWozBridgeMemoryTests
    {
        static int passed,failed;
        static string filter;
        static void Assert(bool value,string message="assertion failed"){if(!value)throw new Exception(message);}
        static void Eq<T>(T a,T b){Assert(Object.Equals(a,b),"expected "+a+", got "+b);}
        static void Throws(Action action){try{action();}catch(InvalidOperationException){return;}catch(ArgumentException){return;}throw new Exception("expected rejection");}
        static void Test(string name,Action action){if(filter!=null&&!name.Contains(filter))return;try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+" "+e);}}
        static AutomaticGazeGeometry Geometry()
        {return new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"legacy-memory-geometry");}
        static Dictionary<ViveTrackpadDirection,Colour> Mapping()
        {return new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};}
        static SessionConfiguration Configuration()
        {return new SessionConfiguration("legacy-memory-session",42,new Allocation("memory-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),RunKind.Simulation,"en",3,30,true,0,true,true,true);}
        static void Neutral(ViveDevelopmentParticipant p){p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));}
        static void Respond(ViveDevelopmentParticipant p)
        {
            Neutral(p);var trial=p.CurrentTrial;p.MarkPresented(trial.TrialId);var c=trial.Stimulus.Ink;
            p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,c==Colour.Green?1:c==Colour.Yellow?-1:0,c==Colour.Red?1:c==Colour.Blue?-1:0));Neutral(p);
        }
        sealed class Fixture:IDisposable
        {
            internal readonly MonotonicClock P=new MonotonicClock("participant-owned-clock"),O=new MonotonicClock("operator-owned-clock");
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveLegacyWozTaskOwner Owner;
            internal readonly ViveLegacyWozObservationOwner.SyntheticObservations Observations;
            internal readonly ViveLegacyWozBridge Bridge;
            internal Fixture(bool main=false,double maxAge=1)
            {
                Task=new ViveDevelopmentParticipant(Configuration(),P,Geometry(),Mapping(),.5,true);
                if(main){Neutral(Task);Task.OwnerBeginPractice(Actor.Owner);for(int i=0;i<8;i++)Respond(Task);
                    Task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"memory-owner");Task.OwnerBeginBlock(Actor.Owner);
                    Thread.Sleep(3010);Task.Tick();Eq(Phase.MainTask,Task.Phase);}
                Owner=Task.CreateLegacyWozTaskOwner(P);Observations=new ViveLegacyWozObservationOwner.SyntheticObservations(O);
                Bridge=ViveLegacyWozBridge.CreateSealedFixture(Owner,P,O,Observations,maxAge);
            }
            internal void Pose(long n=1){Assert(Bridge.PublishCurrentTask());Observations.Fresh(n,(int)n,(int)n);Assert(Bridge.PollMemoryObservation());}
            public void Dispose(){Bridge.Dispose();}
        }
        static TaskSnapshot ActiveTask(int block,int segment=0)
        {return new TaskSnapshot("component",block,segment,"trial-"+block+"-"+segment,Colour.Red,Colour.Blue,Phase.MainTask,block==0?Condition.B:Condition.C);}
        public static int Main(string[] args)
        {
            filter=args.Length==0||String.IsNullOrEmpty(args[0])?null:args[0];
            Test("actual owner rejects equal-ID foreign clock and Named design",()=>{
                var c=new MonotonicClock("same");var p=new ViveDevelopmentParticipant(Configuration(),c,Geometry(),Mapping(),.5,true);
                Throws(()=>p.CreateLegacyWozTaskOwner(new MonotonicClock("same")));
                var named=SessionConfiguration.NamedPlanDevelopment("named",42,ViveNamedSimulationPlan.Select(Actor.Owner,"plan",ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"legacy-memory-geometry")));
                var n=new ViveDevelopmentParticipant(named,c,Geometry(),Mapping(),.5,true);Throws(()=>n.CreateLegacyWozTaskOwner(c));
            });
            Test("task mirror originates from actual participant and whitelist only",()=>{
                using(var f=new Fixture()){Assert(f.Bridge.PublishCurrentTask());Eq(f.Task.SessionId,f.Bridge.OperatorTask.SessionId);Eq(f.Task.Phase,f.Bridge.OperatorTask.Phase);
                    var fields=Array.ConvertAll(typeof(TaskSnapshot).GetProperties(),p=>p.Name);Array.Sort(fields,StringComparer.Ordinal);
                    Eq("BlockIndex,Condition,Ink,Phase,Segment,SessionId,TrialId,Word",String.Join(",",fields));
                    Assert(!f.Bridge.UsesSockets&&!f.Bridge.CanRecordOrExport&&!f.Bridge.CanEnterLiveCountdown&&!f.Bridge.CanPresentLivePose);
                    Assert(!f.Task.RecordsEyeSamples&&!f.Task.RecordsTaskFiles&&!f.Task.UsesNetwork&&!f.Task.IsResearchSession);}
            });
            Test("harmless Tick preserves semantic binding and revision",()=>{
                using(var f=new Fixture()){ViveLegacyWozTaskOwner.Binding b;Assert(f.Owner.TryCapture(out b));f.Task.Tick();Assert(f.Owner.IsCurrent(b));
                    Assert(f.Bridge.PublishCurrentTask());f.Task.Tick();Assert(f.Bridge.OperatorTask!=null);}
            });
            Test("closed memory transport requires a new owner and fresh random epoch",()=>{
                using(var f=new Fixture()){string epoch=f.Bridge.Epoch;f.Bridge.Dispose();Assert(!f.Bridge.PublishCurrentTask());
                    var next=f.Task.CreateLegacyWozTaskOwner(f.P);using(var b=ViveLegacyWozBridge.CreateTaskMirrorMemoryOnly(next,f.P,f.O)){Assert(epoch!=b.Epoch);Assert(!b.PollMemoryObservation());}}
            });
            Test("foreign thread cannot claim safety cleanup completed",()=>{
                using(var f=new Fixture()){Exception error=null;var t=new Thread(()=>{try{f.Bridge.Revoke();}catch(Exception e){error=e;}});t.Start();Assert(t.Join(2000));
                    Assert(error is InvalidOperationException);Assert(!f.Bridge.Terminal);f.Bridge.Revoke();Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("safety stop pauses actual preparation before acknowledgment",()=>{
                using(var f=new Fixture()){Assert(f.Bridge.PublishCurrentTask());Assert(f.Bridge.RequestOperatorSafetyStop());Eq(Phase.Paused,f.Task.Phase);
                    Assert(f.Bridge.StopAcknowledged&&f.Bridge.Terminal&&f.Bridge.CurrentMemoryOutput==null);Assert(!f.Bridge.PublishCurrentTask());}
            });
            Test("cleanup uncertainty suppresses ack and blocks new participant binding",()=>{
                using(var f=new Fixture()){f.Bridge.FailCloseForTest=true;Assert(!f.Bridge.RequestOperatorSafetyStop());Eq(Phase.Paused,f.Task.Phase);
                    Assert(!f.Bridge.StopAcknowledged&&f.Bridge.Terminal);Eq("source_close_uncertain",f.Bridge.LastIssue);Throws(()=>f.Task.CreateLegacyWozTaskOwner(f.P));}
            });
            Test("withdrawal and obsolete mirror fail closed",()=>{
                using(var f=new Fixture()){Assert(f.Bridge.PublishCurrentTask());f.Task.ParticipantWithdraw();Assert(f.Bridge.OperatorTask==null);
                    Assert(!f.Bridge.PublishCurrentTask());Eq(Phase.Ended,f.Task.Phase);Assert(f.Bridge.Terminal);}
            });
            Test("task mutation between protect and delivery rejects publication",()=>{
                using(var f=new Fixture()){f.Bridge.BeforeTaskDeliveryForTest=()=>f.Task.OwnerPause(Actor.Owner);Assert(!f.Bridge.PublishCurrentTask());Assert(f.Bridge.Terminal&&f.Bridge.OperatorTask==null);}
            });
            Test("packet exhaustion terminates instead of renumbering",()=>{
                using(var f=new Fixture()){f.Bridge.ExhaustPacketsForTest();Assert(!f.Bridge.RequestOperatorSafetyStop());Assert(f.Bridge.Terminal&&!f.Bridge.StopAcknowledged);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("actual MainTask HMAC vertical path preserves origin and keeps gates shut",()=>{
                using(var f=new Fixture(true)){f.Pose();var output=f.Bridge.CurrentMemoryOutput;Assert(output!=null);
                    Eq(f.Task.CurrentTrial.TrialId,f.Bridge.OperatorTask.TrialId);Eq(0L,output.Sample.Sequence);Eq(f.P.ClockId,output.ReceiptClockId);
                    Eq(ControlSource.Simulator,output.Sample.Source);Assert(output.SourceBinding.StartsWith("simulator/"));
                    Assert(output.PlaintextSharedKeyPossession&&output.SealedMemoryReceipt&&output.SyntheticObservationsOnly&&!output.ReceiptCapturedBySocket&&!output.IsExperimentAdmission);
                    Eq(1L,f.Bridge.LastSourceCandidateForTest.HeadSequence);Eq(1,f.Bridge.LastSourceCandidateForTest.NativeEyeSequence);Eq(0L,f.Bridge.LastSourceCandidateForTest.SourceSequence);
                    f.Task.Tick();Assert(f.Bridge.CurrentMemoryOutput!=null);Assert(!f.Bridge.CanPresentLivePose);}
            });
            Test("same-trial presentation revision clears pose and stale mirror",()=>{
                using(var f=new Fixture(true)){f.Pose();string trial=f.Task.CurrentTrial.TrialId;f.Task.MarkPresented(trial);Eq(trial,f.Task.CurrentTrial.TrialId);
                    Assert(f.Bridge.CurrentMemoryOutput==null&&f.Bridge.OperatorTask==null);Assert(f.Bridge.PublishCurrentTask());f.Observations.Fresh(2,2,2);Assert(f.Bridge.PollMemoryObservation());}
            });
            Test("repeated and pre-task observations produce no fallback",()=>{
                using(var f=new Fixture(true)){f.Pose();Assert(!f.Bridge.PollMemoryObservation());Assert(f.Bridge.CurrentMemoryOutput==null);
                    f.Observations.Fresh(2,2,2);Assert(f.Bridge.PublishCurrentTask());Assert(!f.Bridge.PollMemoryObservation());Assert(f.Bridge.CurrentMemoryOutput==null);}
            });
            Test("same-trial mutation during source polling invalidates sample",()=>{
                using(var f=new Fixture(true)){Assert(f.Bridge.PublishCurrentTask());f.Observations.Fresh(1,1,1);f.Observations.DuringPoll=()=>f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
                    Assert(!f.Bridge.PollMemoryObservation());Assert(f.Bridge.CurrentMemoryOutput==null&&f.Bridge.Terminal);}
            });
            Test("original causal return age cannot be refreshed by late receipt",()=>{
                using(var f=new Fixture(true,.03)){Assert(f.Bridge.PublishCurrentTask());f.Observations.Fresh(1,1,1);f.Bridge.BeforePoseDeliveryForTest=()=>Thread.Sleep(50);
                    Assert(!f.Bridge.PollMemoryObservation());Eq("original_observation_expired",f.Bridge.LastIssue);Assert(f.Bridge.CurrentMemoryOutput==null);}
            });
            Test("cached memory receipt expires from original task and observations",()=>{
                using(var f=new Fixture(true,.04)){f.Pose();Thread.Sleep(60);Assert(f.Bridge.CurrentMemoryOutput==null);}
            });
            Test("MAC tampering replay and distinct key epoch reject without output",()=>{
                byte[] old;
                using(var f=new Fixture(true)){f.Pose();old=f.Bridge.LastPoseBytesForTest;Assert(!f.Bridge.ReceiveForTest(old));Eq("replay_or_remote_clock",f.Bridge.LastIssue);
                    var bad=(byte[])old.Clone();bad[bad.Length-1]^=1;Assert(!f.Bridge.ReceiveForTest(bad));Eq("mac_mismatch",f.Bridge.LastIssue);Assert(f.Bridge.CurrentMemoryOutput==null);}
                using(var f=new Fixture(true)){f.Pose();Assert(!f.Bridge.ReceiveForTest(old));Eq("mac_mismatch",f.Bridge.LastIssue);Assert(f.Bridge.CurrentMemoryOutput==null);}
            });
            Test("main task stop clears output and interrupts actual trial before ack",()=>{
                using(var f=new Fixture(true)){f.Pose();Assert(f.Bridge.RequestOperatorSafetyStop());Eq(Phase.Paused,f.Task.Phase);Eq(TrialOutcome.Interrupted,f.Task.CurrentTrial.Outcome);
                    Assert(f.Bridge.CurrentMemoryOutput==null&&f.Bridge.StopAcknowledged&&!f.Bridge.CanPresentLivePose);}
            });
            Test("emission exhaustion is terminal with no wrap or fallback",()=>{
                using(var f=new Fixture(true)){Assert(f.Bridge.PublishCurrentTask());f.Bridge.ExhaustEmissionsForTest();f.Observations.Fresh(1,1,1);
                    Assert(!f.Bridge.PollMemoryObservation());Assert(f.Bridge.Terminal&&f.Bridge.CurrentMemoryOutput==null);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("source counters reset per block but epoch and native highs persist",()=>{
                var clock=new MonotonicClock("component-clock");var provider=new ViveLegacyWozObservationOwner.SyntheticObservations(clock);
                using(var source=ViveLegacyWozObservationOwner.BindSynthetic("component","component-epoch",clock,provider,1)){
                    double received=clock.Now;provider.Fresh(10,-5,-8);ViveLegacyWozObservationOwner.Candidate a,b;
                    Assert(source.TryRead(new WozLanTaskContext(ActiveTask(0),0,0,0),received,out a),"initial block: "+source.LastIssue);Eq(0L,a.Sample.Sequence);Eq(0L,a.SourceSequence);
                    received=clock.Now;provider.Fresh(10,-5,-8);Assert(!source.TryRead(new WozLanTaskContext(ActiveTask(1),1,1,0),received,out b),"native duplicate accepted after block replacement");
                    provider.Fresh(11,-4,-7);Assert(source.TryRead(new WozLanTaskContext(ActiveTask(1),1,1,0),received,out b),"fresh second block: "+source.LastIssue);Eq(1L,b.Sample.Sequence);
                    received=clock.Now;provider.Fresh(12,-3,-6);Assert(source.TryRead(new WozLanTaskContext(ActiveTask(1,1),2,2,0),received,out b),"fresh segment: "+source.LastIssue);Eq(2L,b.Sample.Sequence);Eq(0L,b.SourceSequence);
                    Eq(-3,b.NativeEyeSequence);Eq(-6,b.NativeEyeTimestamp);}
            });
            Test("source clock object mismatch and source restart are rejected",()=>{
                var clock=new MonotonicClock("source-clock");var provider=new ViveLegacyWozObservationOwner.SyntheticObservations(clock);
                Throws(()=>ViveLegacyWozObservationOwner.BindSynthetic("component","epoch",new MonotonicClock("source-clock"),provider,1));
                var source=ViveLegacyWozObservationOwner.BindSynthetic("component","epoch",clock,provider,1);source.Dispose();provider.Fresh(1,1,1);
                ViveLegacyWozObservationOwner.Candidate ignored;Assert(!source.TryRead(new WozLanTaskContext(ActiveTask(0),0,0,0),0,out ignored));Assert(source.Terminal);
            });
            if(filter==null){var regression=new TestSuite();
                ViveDevelopmentParticipantTests.Register(regression);
                ViveWozOperatorSourceFactoryTests.Register(regression);
                ViveWozOfflineLifecycleTests.Register(regression);
                passed+=regression.Passed;failed+=regression.Failed;}
            Console.WriteLine("BOUNDARY: actual Legacy simulation owner and sealed clocks, memory HMAC and synthetic observations only; no live readiness, recording, SDK, GUI or OS sockets.");
            Console.WriteLine("RESULT "+passed+" passed; "+failed+" failed");return failed==0?0:1;
        }
    }
}
