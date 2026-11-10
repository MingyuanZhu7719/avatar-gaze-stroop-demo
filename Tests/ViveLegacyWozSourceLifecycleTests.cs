using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveLegacyWozSourceLifecycleTests
    {
        static int passed,failed;
        static string filter;
        static void Assert(bool value,string message="assertion failed"){if(!value)throw new Exception(message);}
        static void Eq<T>(T expected,T actual){Assert(Object.Equals(expected,actual),"expected "+expected+", got "+actual);}
        static void Throws(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("expected rejection");}
        static void Test(string name,Action action)
        {if(filter!=null&&!name.Contains(filter))return;try{action();passed++;Console.WriteLine("PASS lifecycle "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL lifecycle "+name+" "+e);}}
        static void Neutral(ViveDevelopmentParticipant p){p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));}
        static void Respond(ViveDevelopmentParticipant p)
        {Neutral(p);var t=p.CurrentTrial;p.MarkPresented(t.TrialId);var c=t.Stimulus.Ink;
            p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,c==Colour.Green?1:c==Colour.Yellow?-1:0,c==Colour.Red?1:c==Colour.Blue?-1:0));Neutral(p);}
        static void Confirm(ViveDevelopmentParticipant p)
        {p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);}
        sealed class Fixture:IDisposable
        {
            internal readonly MonotonicClock P=new MonotonicClock("lifecycle-participant"),O=new MonotonicClock("lifecycle-operator");
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveLegacyWozTaskOwner Owner;
            internal readonly ViveLegacyWozObservationOwner.SyntheticObservations Observations;
            internal readonly ViveLegacyWozBridge Bridge;
            long observation;
            internal Fixture(double age=1,bool observed=false)
            {
                var config=new SessionConfiguration("lifecycle-session",42,new Allocation("memory-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=observed?new ViveDevelopmentParticipant(config,P,DevelopmentFixtures.Geometry(),mapping,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096):
                    new ViveDevelopmentParticipant(config,P,DevelopmentFixtures.Geometry(),mapping,.5,true);
                Neutral(Task);Task.OwnerBeginPractice(Actor.Owner);for(int i=0;i<8;i++)Respond(Task);Eq(Phase.BlockPreparation,Task.Phase);
                Owner=Task.CreateLegacyWozTaskOwner(P);Observations=new ViveLegacyWozObservationOwner.SyntheticObservations(O);
                Bridge=ViveLegacyWozBridge.CreateSealedFixture(Owner,P,O,Observations,age);
            }
            internal void Select(){Assert(Bridge.SelectAndConfirmLocalLiveWoz(Actor.Owner,"local-owner"));Eq(ControlSelection.LiveWoz,Task.LockedControl.Value);Eq(Phase.BlockReady,Task.Phase);}
            internal void Grant(){Assert(Bridge.PublishCurrentTask());Assert(Bridge.SendCurrentSourceConfirmation());Assert(Bridge.SourceConfirmedForCurrentTask);}
            internal void Fresh(){observation++;Observations.Fresh(observation,(int)observation,(int)observation);}
            internal void Preflight(){Assert(Bridge.BeginPhasePreflight());Fresh();Assert(Bridge.PollPhasePreflight());Assert(Bridge.CurrentMemoryPreflight!=null);}
            internal void MainTask(){Assert(Bridge.BeginLiveMemoryCountdownForTest(Task));Thread.Sleep(3010);Task.Tick();Eq(Phase.MainTask,Task.Phase);}
            public void Dispose(){Bridge.Dispose();}
        }
        public static int Main(string[] args)
        {
            filter=args.Length==0||String.IsNullOrEmpty(args[0])?null:args[0];
            int baseline=0,codec=0;
            if(filter==null){baseline=ViveLegacyWozBridgeMemoryTests.Main(new string[0]);codec=WozAuthenticatedControlTests.RunAll();}
            Test("network messages cannot select source; explicit real owner choice does",()=>{
                using(var f=new Fixture()){Assert(f.Bridge.PublishCurrentTask());Assert(!f.Bridge.SendCurrentSourceConfirmation());Assert(!f.Bridge.BeginPhasePreflight());
                    Assert(!f.Bridge.LocalLiveWozChoiceCurrent&&!f.Task.LockedControl.HasValue);f.Select();Assert(f.Bridge.LocalLiveWozChoiceCurrent);
                    Assert(!f.Bridge.SourceConfirmedForCurrentTask);f.Grant();Assert(!f.Bridge.SendCurrentSourceConfirmation());
                    Assert(f.Task.CurrentAvatar==null,"Owned Live choice must clear output, not throw or fall back to Automatic");
                    Assert(!f.Bridge.CanEnterLiveCountdown&&!f.Bridge.CanPresentLivePose&&!f.Bridge.CanRecordOrExport);}
            });
            Test("Automatic and foreign actors cannot acquire a LiveWoz choice",()=>{
                using(var f=new Fixture()){f.Task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"automatic-owner");
                    Throws(()=>f.Bridge.SelectAndConfirmLocalLiveWoz(Actor.Owner,"other"));Assert(!f.Bridge.LocalLiveWozChoiceCurrent&&!f.Bridge.SourceConfirmedForCurrentTask);Eq(ControlSelection.Automatic,f.Task.LockedControl.Value);}
                using(var f=new Fixture()){Throws(()=>f.Bridge.SelectAndConfirmLocalLiveWoz(Actor.Participant,"not-owner"));Assert(!f.Task.LockedControl.HasValue);}
            });
            Test("actual Live source selection never opens production countdown",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();f.Preflight();Throws(()=>f.Task.OwnerBeginBlock(Actor.Owner));Eq(Phase.BlockReady,f.Task.Phase);
                    Assert(!f.Bridge.CanEnterLiveCountdown&&!f.Bridge.LocalLiveWozChoiceCurrent);}
            });
            Test("observed participant also owns actual source confirmation",()=>{
                using(var f=new Fixture(observed:true)){f.Select();f.Grant();f.Preflight();Eq(Phase.BlockReady,f.Bridge.CurrentMemoryPreflight.Phase);}
            });
            Test("preflight stores original challenge observations clocks and synthetic origin",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();f.Preflight();var p=f.Bridge.CurrentMemoryPreflight;
                    Assert(p.OriginalHeadObservedOperatorSeconds>p.OriginalChallengeReceivedOperatorSeconds&&p.OriginalEyeObservedOperatorSeconds>p.OriginalChallengeReceivedOperatorSeconds);
                    Assert(p.OriginalReceiptParticipantSeconds>=p.OriginalChallengeIssuedParticipantSeconds);
                    Eq(f.P.ClockId,p.ParticipantClockId);Eq(f.O.ClockId,p.OperatorClockId);Eq(Phase.BlockReady,p.Phase);
                    Assert(p.SyntheticObservationsOnly&&!p.InstalledSdkPath&&!p.ReceiptCapturedBySocket&&!p.HardwareTrackingValidated&&!p.CanEnterLiveCountdown&&!p.CanRecordOrExport);
                    double original=p.OriginalReceiptParticipantSeconds;f.Task.Tick();Eq(original,f.Bridge.CurrentMemoryPreflight.OriginalReceiptParticipantSeconds);
                    Assert(!f.Bridge.PollPhasePreflight());}
            });
            Test("cached observations before challenge cannot satisfy preflight",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();f.Fresh();Assert(f.Bridge.BeginPhasePreflight());Assert(!f.Bridge.PollPhasePreflight());Assert(f.Bridge.CurrentMemoryPreflight==null);
                    f.Fresh();Assert(f.Bridge.PollPhasePreflight());}
            });
            Test("challenge timeout is terminal and pauses actual task",()=>{
                using(var f=new Fixture(.03)){f.Select();f.Grant();Assert(f.Bridge.BeginPhasePreflight());Thread.Sleep(50);f.Fresh();
                    Assert(!f.Bridge.PollPhasePreflight());Assert(f.Bridge.Terminal&&f.Bridge.CurrentMemoryPreflight==null);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("delayed proof cannot refresh original challenge receipt age",()=>{
                using(var f=new Fixture(.03)){f.Select();f.Grant();Assert(f.Bridge.BeginPhasePreflight());f.Fresh();f.Bridge.BeforeControlDeliveryForTest=()=>Thread.Sleep(50);
                    Assert(!f.Bridge.PollPhasePreflight());Assert(f.Bridge.Terminal&&f.Bridge.CurrentMemoryPreflight==null);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("source grant survives harmless Tick but preflight changes with exact task packet",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();f.Preflight();f.Task.Tick();Assert(f.Bridge.SourceConfirmedForCurrentTask);
                    Assert(f.Bridge.PublishCurrentTask());Assert(f.Bridge.CurrentMemoryPreflight==null&&!f.Bridge.SourceConfirmedForCurrentTask);
                    Assert(f.Bridge.LocalLiveWozChoiceCurrent);Assert(f.Bridge.SendCurrentSourceConfirmation());f.Preflight();}
            });
            Test("revoke is local first and safe ack cannot reconfirm or auto resume",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();f.Preflight();Assert(f.Bridge.RevokeAuthenticatedSource());Eq(Phase.Paused,f.Task.Phase);
                    Assert(f.Bridge.SourceRevokeAcknowledged&&f.Bridge.Terminal&&!f.Bridge.LocalLiveWozChoiceCurrent&&!f.Bridge.SourceConfirmedForCurrentTask);
                    Assert(f.Bridge.OperatorTask==null&&f.Bridge.CurrentMemoryPreflight==null&&f.Bridge.CurrentMemoryOutput==null);
                    Assert(f.Task.CurrentAvatar==null,"Paused/revoked Live choice must still clear presentation");
                    Assert(!f.Bridge.SelectAndConfirmLocalLiveWoz(Actor.Owner,"local-owner")&&!f.Bridge.SendCurrentSourceConfirmation());f.Task.Tick();Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("revoke uses stored grant after task generation changes",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();Assert(f.Bridge.BeginLiveMemoryCountdownForTest(f.Task));Eq(Phase.Countdown,f.Task.Phase);
                    Assert(!f.Bridge.SourceConfirmedForCurrentTask);Assert(f.Bridge.RevokeAuthenticatedSource());Eq(Phase.Paused,f.Task.Phase);Assert(f.Bridge.SourceRevokeAcknowledged);}
            });
            Test("revoke before confirmation still pauses without false acknowledgment",()=>{
                using(var f=new Fixture()){f.Select();Assert(!f.Bridge.RevokeAuthenticatedSource());Eq(Phase.Paused,f.Task.Phase);Assert(!f.Bridge.SourceRevokeAcknowledged&&f.Bridge.Terminal);}
            });
            Test("uncertain revoke cleanup blocks ack and fresh owner binding",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();f.Bridge.FailCloseForTest=true;Assert(!f.Bridge.RevokeAuthenticatedSource());
                    Eq(Phase.Paused,f.Task.Phase);Assert(!f.Bridge.SourceRevokeAcknowledged);Throws(()=>f.Task.CreateLegacyWozTaskOwner(f.P));}
            });
            Test("participant pause withdrawal and source lease loss invalidate authority",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();f.Preflight();f.Task.ParticipantPause();Assert(!f.Bridge.SourceConfirmedForCurrentTask);Assert(f.Bridge.Terminal&&f.Bridge.CurrentMemoryPreflight==null);}
                using(var f=new Fixture()){f.Select();f.Grant();f.Task.ParticipantWithdraw();Assert(!f.Bridge.SourceConfirmedForCurrentTask);Eq(Phase.Ended,f.Task.Phase);}
                using(var f=new Fixture()){f.Select();f.Grant();f.Preflight();f.Bridge.ReplaceSourceLeaseForTest();Assert(!f.Bridge.SourceConfirmedForCurrentTask);Eq(Phase.Paused,f.Task.Phase);Assert(f.Bridge.Terminal);}
            });
            Test("tampered replayed and reflected control cause terminal safety",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();var bytes=(byte[])f.Bridge.LastControlBytesForTest.Clone();bytes[bytes.Length-1]^=1;
                    Assert(!f.Bridge.InjectControlForTest(true,bytes));Eq(Phase.Paused,f.Task.Phase);Assert(!f.Bridge.SourceRevokeAcknowledged);}
                using(var f=new Fixture()){f.Select();f.Grant();Assert(!f.Bridge.InjectControlForTest(true,f.Bridge.LastControlBytesForTest));Eq(Phase.Paused,f.Task.Phase);}
                using(var f=new Fixture()){f.Select();f.Grant();Assert(!f.Bridge.InjectControlForTest(false,f.Bridge.LastControlBytesForTest));Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("old grant after newer task cannot restore source authority",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();var old=f.Bridge.LastControlBytesForTest;f.Grant();Assert(!f.Bridge.InjectControlForTest(true,old));
                    Eq(Phase.Paused,f.Task.Phase);Assert(f.Bridge.Terminal&&!f.Bridge.SourceConfirmedForCurrentTask);}
            });
            Test("control exhaustion and choice loss during delivery fail closed",()=>{
                using(var f=new Fixture()){f.Select();Assert(f.Bridge.PublishCurrentTask());f.Bridge.ExhaustControlForTest();Assert(!f.Bridge.SendCurrentSourceConfirmation());Eq(Phase.Paused,f.Task.Phase);}
                using(var f=new Fixture()){f.Select();Assert(f.Bridge.PublishCurrentTask());f.Bridge.BeforeControlDeliveryForTest=()=>f.Task.ParticipantPause();
                    Assert(!f.Bridge.SendCurrentSourceConfirmation());Assert(f.Bridge.Terminal&&!f.Bridge.SourceConfirmedForCurrentTask);}
            });
            Test("actual phases require fresh preflight and complete authorized memory pose",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();f.Preflight();f.MainTask();Assert(f.Bridge.CurrentMemoryPreflight==null);f.Grant();
                    f.Fresh();Assert(!f.Bridge.PollMemoryObservation());f.Preflight();Eq(Phase.MainTask,f.Bridge.CurrentMemoryPreflight.Phase);
                    f.Fresh();Assert(f.Bridge.PollMemoryObservation());var output=f.Bridge.CurrentMemoryOutput;Assert(output!=null&&output.SyntheticObservationsOnly);
                    Eq(ControlSource.Simulator,output.Sample.Source);Assert(f.Bridge.RevokeAuthenticatedSource());Eq(Phase.Paused,f.Task.Phase);Assert(f.Bridge.CurrentMemoryOutput==null);}
            });
            Test("same-trial mutation during preflight polling rejects proof",()=>{
                using(var f=new Fixture()){f.Select();f.MainTask();f.Grant();Assert(f.Bridge.BeginPhasePreflight());f.Fresh();
                    f.Observations.DuringPoll=()=>f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);Assert(!f.Bridge.PollPhasePreflight());Assert(f.Bridge.Terminal&&f.Bridge.CurrentMemoryPreflight==null);}
            });
            Test("preflight challenge replacement during poll cannot issue old proof",()=>{
                using(var f=new Fixture()){f.Select();f.Grant();Assert(f.Bridge.BeginPhasePreflight());f.Fresh();
                    f.Observations.DuringPoll=()=>f.Bridge.BeginPhasePreflight();Assert(!f.Bridge.PollPhasePreflight());
                    Assert(f.Bridge.Terminal&&f.Bridge.CurrentMemoryPreflight==null);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("preflight replacement during pose poll prevents publication",()=>{
                using(var f=new Fixture()){f.Select();f.MainTask();f.Grant();f.Preflight();f.Fresh();
                    f.Observations.DuringPoll=()=>f.Bridge.BeginPhasePreflight();Assert(!f.Bridge.PollMemoryObservation());
                    Assert(f.Bridge.Terminal&&f.Bridge.CurrentMemoryOutput==null);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("preflight replacement during pose delivery prevents receipt issue",()=>{
                using(var f=new Fixture()){f.Select();f.MainTask();f.Grant();f.Preflight();f.Fresh();
                    f.Bridge.BeforePoseDeliveryForTest=()=>f.Bridge.BeginPhasePreflight();Assert(!f.Bridge.PollMemoryObservation());
                    Assert(f.Bridge.Terminal&&f.Bridge.CurrentMemoryOutput==null);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("actual block transition retains local choice but requires new task grant",()=>{
                using(var f=new Fixture()){f.Select();f.MainTask();f.Grant();f.Preflight();var old=f.Bridge.CurrentMemoryPreflight;
                    for(int i=0;i<24;i++)Respond(f.Task);Eq(Phase.Questionnaire,f.Task.Phase);Confirm(f.Task);for(int i=0;i<5;i++)Confirm(f.Task);
                    Eq(Phase.Rest,f.Task.Phase);Confirm(f.Task);Eq(1,f.Task.BlockIndex);Eq(Phase.BlockReady,f.Task.Phase);
                    Assert(f.Bridge.LocalLiveWozChoiceCurrent&&f.Bridge.CurrentMemoryPreflight==null&&!f.Bridge.SourceConfirmedForCurrentTask);
                    f.Grant();f.Preflight();Assert(f.Bridge.CurrentMemoryPreflight.TaskRevision>old.TaskRevision);Eq(Phase.BlockReady,f.Bridge.CurrentMemoryPreflight.Phase);}
            });
            Console.WriteLine("BOUNDARY: actual owner selection and safety, authenticated control and sealed synthetic preflight; no real tracking/readiness, recording, socket or hardware result.");
            Console.WriteLine("LIFECYCLE RESULT "+passed+" passed; "+failed+" failed; baseline_exit="+baseline+"; codec_failures="+codec);
            Console.WriteLine("RESULT "+(filter==null?passed+49+9:passed)+" checks; "+(failed+baseline+codec)+" failures");
            return failed==0&&baseline==0&&codec==0?0:1;
        }
    }
}
