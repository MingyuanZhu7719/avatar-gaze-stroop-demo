using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveLegacyWozProcessMemoryTests
    {
        static int passed,failed;static string filter;
        static void Assert(bool value,string message="assertion failed"){if(!value)throw new Exception(message);}
        static void Eq<T>(T expected,T actual){Assert(Object.Equals(expected,actual),"expected "+expected+", got "+actual);}
        static void Throws(Action action){try{action();}catch(InvalidOperationException){return;}catch(ArgumentException){return;}throw new Exception("expected rejection");}
        static void Test(string name,Action action)
        {if(filter!=null&&!name.Contains(filter))return;try{action();passed++;Console.WriteLine("PASS process-memory "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL process-memory "+name+" "+e);}}
        static void Neutral(ViveDevelopmentParticipant p){p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));}
        internal static void Respond(ViveDevelopmentParticipant p)
        {Neutral(p);var t=p.CurrentTrial;p.MarkPresented(t.TrialId);var c=t.Stimulus.Ink;
            p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,c==Colour.Green?1:c==Colour.Yellow?-1:0,c==Colour.Red?1:c==Colour.Blue?-1:0));Neutral(p);}
        internal static ViveDevelopmentParticipant Prepare(MonotonicClock clock)
        {
            var config=new SessionConfiguration("process-session",42,new Allocation("process-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var task=new ViveDevelopmentParticipant(config,clock,DevelopmentFixtures.Geometry(),map,.5,true);
            Neutral(task);task.OwnerBeginPractice(Actor.Owner);for(int i=0;i<8;i++)Respond(task);Eq(Phase.BlockPreparation,task.Phase);return task;
        }
        sealed class Fixture:IDisposable
        {
            internal readonly MonotonicClock PC=new MonotonicClock("process-p-"+Guid.NewGuid().ToString("N")),OC=new MonotonicClock("process-o-"+Guid.NewGuid().ToString("N"));
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveLegacyWozTaskOwner Owner;
            internal readonly ViveLegacyWozObservationOwner.SyntheticObservations Observations;
            internal readonly ViveLegacyWozParticipantProcess P;
            internal readonly ViveLegacyWozOperatorProcess O;
            internal readonly ViveLegacyWozProcessTransport.MemorySocket PS=new ViveLegacyWozProcessTransport.MemorySocket(),OS=new ViveLegacyWozProcessTransport.MemorySocket();
            internal readonly IPEndPoint PE=new IPEndPoint(IPAddress.Loopback,45171),OE=new IPEndPoint(IPAddress.Loopback,45172);
            long sequence;
            internal Fixture(double age=1,bool consent=true)
            {
                Task=Prepare(PC);Owner=Task.CreateLegacyWozTaskOwner(PC);Observations=new ViveLegacyWozObservationOwner.SyntheticObservations(OC);PS.Other=OS;OS.Other=PS;
                using(var offer=ViveLegacyWozProcessPairing.CreateSyntheticOffer(Owner,"local-owner",OC.ClockId))
                {
                    char[] token=offer.RevealToken();try{using(var imported=ViveLegacyWozProcessPairing.Parse(token))
                    {P=ViveLegacyWozParticipantProcess.Memory(offer,Owner,PE,OE,age,PS);O=ViveLegacyWozOperatorProcess.Synthetic(imported,OC,Observations,OE,PE,age,OS);}}
                    finally{Array.Clear(token,0,token.Length);}
                }
                Assert(P.Start(),P.LastIssue);Assert(O.Start(),O.LastIssue);if(consent)Assert(O.ConfirmLocalOperatorSource());
            }
            internal void Grant(){Assert(P.PublishCurrentTask(),P.LastIssue);Assert(P.ConfirmCurrentSource(),P.LastIssue);O.Tick();Assert(O.SourceConfirmed,O.LastIssue);}
            internal void Fresh(){sequence++;Observations.Fresh(sequence,(int)sequence,(int)sequence);}
            internal void Preflight(){Assert(P.BeginPhasePreflight(),P.LastIssue);O.Tick();Fresh();Assert(O.PollPhasePreflight(),O.LastIssue);P.Tick();Assert(P.CurrentPreflight!=null,P.LastIssue);}
            internal void Main(){Assert(P.BeginCountdownForTest(Task));Thread.Sleep(3010);Task.Tick();Eq(Phase.MainTask,Task.Phase);Grant();Preflight();}
            internal void Pose(){Fresh();Assert(O.PollObservation(),O.LastIssue);P.Tick();Assert(P.CurrentPose!=null,P.LastIssue);}
            public void Dispose(){P.Dispose();O.Dispose();}
        }
        public static int Main(string[] args)
        {
            if(args.Length==1&&args[0]=="--two-process-loopback")return ViveLegacyWozTwoProcessLoopback.Coordinate();
            if(args.Length==4&&args[0]=="--role")return ViveLegacyWozTwoProcessLoopback.Role(args[1],args[2],args[3]);
            filter=args.Length==0||String.IsNullOrEmpty(args[0])?null:args[0];int baseline=0;
            if(filter==null)baseline=ViveLegacyWozSourceLifecycleTests.Main(new string[0]);
            Test("actual owner clocks remain role local and imported token cannot select participant",()=>{
                var pc=new MonotonicClock("pair-p");var oc=new MonotonicClock("pair-o");var task=Prepare(pc);var owner=task.CreateLegacyWozTaskOwner(pc);
                using(var offer=ViveLegacyWozProcessPairing.CreateSyntheticOffer(owner,"owner",oc.ClockId)){
                    Eq(pc.ClockId,offer.Context.Participant.ClockId);Eq(oc.ClockId,offer.Context.Operator.ClockId);Eq(task.SessionId,offer.Context.Participant.SessionId);
                    char[] token=offer.RevealToken();try{using(var imported=ViveLegacyWozProcessPairing.Parse(token)){Throws(()=>imported.ClaimParticipant(owner));}}
                    finally{Array.Clear(token,0,token.Length);}}
                Eq(Phase.Paused,task.Phase);
            });
            Test("reimported operator epoch cannot reset replay highs",()=>{
                var pc=new MonotonicClock("spent-p");var oc=new MonotonicClock("spent-o");var task=Prepare(pc);var owner=task.CreateLegacyWozTaskOwner(pc);
                using(var offer=ViveLegacyWozProcessPairing.CreateSyntheticOffer(owner,"owner",oc.ClockId)){
                    char[] token=offer.RevealToken();try{using(var a=ViveLegacyWozProcessPairing.Parse(token))using(var b=ViveLegacyWozProcessPairing.Parse(token))
                    {a.ClaimOperator(oc);Throws(()=>b.ClaimOperator(oc));}}finally{Array.Clear(token,0,token.Length);}}
            });
            Test("role controllers do not hold remote owner or remote clock",()=>{
                foreach(var field in typeof(ViveLegacyWozParticipantProcess).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))
                    Assert(field.FieldType!=typeof(ViveLegacyWozObservationOwner)&&field.FieldType!=typeof(ViveProEyeWorker)&&field.FieldType!=typeof(ViveLegacyWozProcessPairing),field.Name);
                foreach(var field in typeof(ViveLegacyWozOperatorProcess).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))
                    Assert(field.FieldType!=typeof(ViveLegacyWozTaskOwner)&&field.FieldType!=typeof(ViveDevelopmentParticipant)&&field.FieldType!=typeof(ViveLegacyWozProcessPairing),field.Name);
            });
            Test("rejected offer preserves existing Automatic task and owner",()=>{
                var pc=new MonotonicClock("reject-p");var task=Prepare(pc);var owner=task.CreateLegacyWozTaskOwner(pc);
                task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"automatic");
                Throws(()=>ViveLegacyWozProcessPairing.CreateSyntheticOffer(owner,"owner","reject-o"));
                Eq(Phase.BlockReady,task.Phase);Eq(ControlSelection.Automatic,task.LockedControl.Value);Assert(!owner.Terminal);Assert(task.CurrentAvatar!=null);owner.Dispose();
            });
            Test("endpoint copies remain exact and mixed or nonexact loopback is rejected",()=>{
                using(var f=new Fixture()){f.PE.Port=45991;f.OE.Port=45992;f.Grant();f.Preflight();Assert(!f.P.Terminal);}
                var pc=new MonotonicClock("endpoint-p");var task=Prepare(pc);var owner=task.CreateLegacyWozTaskOwner(pc);
                using(var p=ViveLegacyWozProcessPairing.CreateSyntheticOffer(owner,"owner","endpoint-o")){
                    Throws(()=>ViveLegacyWozParticipantProcess.Memory(p,owner,new IPEndPoint(IPAddress.Loopback,45171),new IPEndPoint(IPAddress.Parse("127.0.0.2"),45172),1,new ViveLegacyWozProcessTransport.MemorySocket()));
                    Throws(()=>ViveLegacyWozParticipantProcess.Memory(p,owner,new IPEndPoint(IPAddress.Loopback,45171),new IPEndPoint(IPAddress.Parse("192.168.1.2"),45172),1,new ViveLegacyWozProcessTransport.MemorySocket()));
                    Assert(!owner.Terminal);}
            });
            Test("explicit local operator consent is independent of authenticated source confirm",()=>{
                using(var f=new Fixture(consent:false)){Assert(f.P.PublishCurrentTask());Assert(f.P.ConfirmCurrentSource());f.O.Tick();Assert(f.O.Terminal&&!f.O.SourceConfirmed);Assert(f.P.CurrentPreflight==null);}
            });
            Test("BlockReady preflight has truthful memory origin and original role times",()=>{
                using(var f=new Fixture()){f.Grant();f.Preflight();var p=f.P.CurrentPreflight;Assert(p is ViveLegacyWozParticipantProcess.MemoryEvidence);
                    Assert(!p.ReceiptCapturedBySocket&&p.SyntheticObservationsOnly&&p.PlaintextSharedKeyPossession&&!p.IsExperimentAdmission&&!p.HardwareTrackingValidated);
                    Eq(f.PC.ClockId,p.ParticipantClockId);Eq(f.OC.ClockId,p.OperatorClockId);Eq(Phase.BlockReady,p.Phase);
                    Assert(p.OriginalReceiptParticipantSeconds>=p.OriginalChallengeIssuedParticipantSeconds);Assert(f.Task.CurrentAvatar==null);
                    f.Task.Tick();Eq(p,f.P.CurrentPreflight);Assert(!f.P.CanEnterLiveCountdown&&!f.P.CanPresentLivePose&&!f.P.CanRecordOrExport);}
            });
            Test("Countdown positive preflight is actual participant phase without readiness promotion",()=>{
                using(var f=new Fixture()){Assert(f.P.BeginCountdownForTest(f.Task));Eq(Phase.Countdown,f.Task.Phase);f.Grant();f.Preflight();Eq(Phase.Countdown,f.P.CurrentPreflight.Phase);Assert(!f.P.CanEnterLiveCountdown);}
            });
            Test("MainTask pose requires fresh preflight and survives harmless Tick",()=>{
                using(var f=new Fixture(5)){f.Main();f.Task.Tick();f.Pose();Eq(ControlSource.Simulator,f.P.CurrentPose.Pose.Source);Eq(1L,f.P.CurrentPose.Pose.Sequence);
                    Assert(!f.O.PollObservation(),"repeated native observation accepted");}
            });
            Test("same task challenge replacement rejects delayed prior pose",()=>{
                using(var f=new Fixture(5)){f.Main();f.Fresh();Assert(f.O.PollObservation());var old=f.PS.Queue.Dequeue();
                    f.Preflight();f.PS.Queue.Enqueue(old);f.P.Tick();Assert(f.P.Terminal&&f.P.CurrentPose==null);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("participant original task age rejects delayed remote proof",()=>{
                using(var f=new Fixture(.08)){f.Grant();Assert(f.P.BeginPhasePreflight());f.O.Tick();f.Fresh();Assert(f.O.PollPhasePreflight());Thread.Sleep(100);f.P.Tick();
                    Assert(f.P.Terminal&&f.P.CurrentPreflight==null);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("operator original observations cannot be refreshed after poll delay",()=>{
                using(var f=new Fixture(.08)){f.Grant();Assert(f.P.BeginPhasePreflight());f.O.Tick();f.Fresh();f.Observations.DuringPoll=()=>Thread.Sleep(100);
                    Assert(!f.O.PollPhasePreflight());Assert(f.P.CurrentPreflight==null);}
            });
            Test("local revoke pauses before send and ack means remote source cleanup",()=>{
                using(var f=new Fixture()){f.Grant();f.Preflight();f.PS.DuringSend=()=>{Eq(Phase.Paused,f.Task.Phase);Assert(f.P.CurrentPose==null);};
                    Assert(f.P.RevokeSource(),f.P.LastIssue);Assert(!f.P.SourceRevokeAcknowledged);f.O.Tick();Assert(f.O.SourceRevokeAcknowledged,f.O.LastIssue);
                    f.P.Tick();Assert(f.P.SourceRevokeAcknowledged&&f.P.Terminal);Eq(Phase.Paused,f.Task.Phase);Assert(!f.P.Start());}
            });
            Test("matching grant revoke survives newer task publication",()=>{
                using(var f=new Fixture()){f.Grant();Assert(f.P.BeginCountdownForTest(f.Task));Assert(f.P.PublishCurrentTask());f.O.Tick();Assert(!f.O.SourceConfirmed);
                    Assert(f.P.RevokeSource());f.O.Tick();f.P.Tick();Assert(f.P.SourceRevokeAcknowledged&&f.O.SourceRevokeAcknowledged);}
            });
            Test("in-flight authenticated pose cannot cancel pending source revoke acknowledgment",()=>{
                using(var f=new Fixture(5)){f.Main();f.Fresh();Assert(f.O.PollObservation());Assert(f.P.RevokeSource());f.O.Tick();f.P.Tick();
                    Assert(f.P.SourceRevokeAcknowledged&&f.P.CurrentPose==null);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("operator stop actual participant pause precedes ack",()=>{
                using(var f=new Fixture()){f.Grant();f.Preflight();Assert(f.O.RequestSafetyStop());Assert(!f.O.ParticipantStopAcknowledged);
                    Assert(f.O.CurrentTask==null&&!f.O.PhasePreflightCurrent&&!f.O.Terminal,"negative wait getters closed pending stop");
                    f.PS.DuringSend=()=>Eq(Phase.Paused,f.Task.Phase);f.P.Tick();f.O.Tick();Assert(f.P.SafetyStopAcknowledged&&f.O.ParticipantStopAcknowledged);}
            });
            Test("in-flight task confirm and challenge cannot cancel pending operator stop ack",()=>{
                using(var f=new Fixture()){f.Grant();Assert(f.P.PublishCurrentTask());Assert(f.P.ConfirmCurrentSource());Assert(f.P.BeginPhasePreflight());
                    Assert(f.O.RequestSafetyStop());f.P.Tick();f.O.Tick();Assert(f.O.ParticipantStopAcknowledged,f.O.LastIssue);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("operator uncertain source close suppresses revoke acknowledgment",()=>{
                using(var f=new Fixture(.08)){f.Grant();f.O.FailSourceCloseForTest=true;Assert(f.P.RevokeSource());f.O.Tick();
                    Assert(f.O.Terminal&&f.O.CleanupUncertain&&!f.O.SourceRevokeAcknowledged);Thread.Sleep(100);f.P.Tick();Assert(!f.P.SourceRevokeAcknowledged);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("preflight replaced during source poll cannot publish old proof",()=>{
                using(var f=new Fixture()){f.Grant();Assert(f.P.BeginPhasePreflight());f.O.Tick();f.Fresh();
                    f.Observations.DuringPoll=()=>{Assert(f.P.BeginPhasePreflight());f.O.Tick();};Assert(!f.O.PollPhasePreflight());Assert(f.O.Terminal&&f.P.CurrentPreflight==null);}
            });
            Test("missing revoke acknowledgment remains explicit terminal timeout",()=>{
                using(var f=new Fixture(.08)){f.Grant();Assert(f.P.RevokeSource());Thread.Sleep(100);f.P.Tick();Assert(f.P.Terminal&&!f.P.SourceRevokeAcknowledged);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("tamper replay reflection and wrong epoch produce no positive evidence",()=>{
                using(var f=new Fixture()){f.Grant();f.Preflight();var raw=(byte[])f.OS.LastSent.Clone();raw[raw.Length-1]^=1;f.PS.Queue.Enqueue(Tuple.Create(raw,f.OE));f.P.Tick();Assert(f.P.Terminal);Eq(Phase.Paused,f.Task.Phase);}
                using(var f=new Fixture()){f.Grant();f.Preflight();f.PS.Queue.Enqueue(Tuple.Create((byte[])f.OS.LastSent.Clone(),f.OE));f.P.Tick();Assert(f.P.Terminal);}
                using(var f=new Fixture()){f.Grant();f.OS.Queue.Enqueue(Tuple.Create((byte[])f.PS.LastSent.Clone(),f.PE));f.O.Tick();Assert(f.O.Terminal);}
                using(var f=new Fixture()){f.Grant();f.PS.Queue.Enqueue(Tuple.Create((byte[])f.PS.LastSent.Clone(),f.OE));f.P.Tick();Assert(f.P.Terminal,"reflected local confirm accepted");}
                byte[] foreign;using(var a=new Fixture()){a.Grant();a.Preflight();foreign=(byte[])a.OS.LastSent.Clone();}
                using(var f=new Fixture()){f.Grant();f.PS.Queue.Enqueue(Tuple.Create(foreign,f.OE));f.P.Tick();Assert(f.P.Terminal&&f.P.CurrentPreflight==null);}
            });
            Test("foreign endpoints are bounded ignored not authenticated stop instructions",()=>{
                using(var f=new Fixture()){f.Grant();f.P.Tick();Assert(f.P.CurrentTaskAcknowledged,"correlated task receipt consumed before foreign drain test");Eq(0,f.PS.Queue.Count);
                    for(int i=0;i<20;i++)f.PS.Queue.Enqueue(Tuple.Create(new byte[]{1},new IPEndPoint(IPAddress.Loopback,45999)));
                    f.P.Tick();Eq(4,f.PS.Queue.Count);Assert(!f.P.Terminal);Eq(Phase.BlockReady,f.Task.Phase);f.P.Tick();Eq(0,f.PS.Queue.Count);}
            });
            Test("oversize configured peer datagram fails closed",()=>{
                using(var f=new Fixture()){f.Grant();f.PS.Queue.Enqueue(Tuple.Create(new byte[4097],f.OE));f.P.Tick();Assert(f.P.Terminal);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("source lease loss does not fall back or preserve positive authority",()=>{
                using(var f=new Fixture()){f.Grant();f.Preflight();f.O.ReplaceLeaseForTest();Assert(!f.O.PhasePreflightCurrent&&f.O.Terminal);Assert(f.Task.CurrentAvatar==null);}
            });
            Test("participant stopped sealed clock pauses immediately in same operation",()=>{
                using(var f=new Fixture()){f.Grant();var timer=(Stopwatch)typeof(MonotonicClock).GetField("timer",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f.PC);timer.Stop();
                    Assert(!f.P.BeginPhasePreflight());Assert(f.P.Terminal);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("uncertain transport cleanup blocks fresh participant ownership",()=>{
                using(var f=new Fixture()){f.Grant();f.PS.FailClose=true;f.P.Dispose();Eq(Phase.Paused,f.Task.Phase);Throws(()=>f.Task.CreateLegacyWozTaskOwner(f.PC));}
            });
            Test("failed send keeps sanitized operation and numeric diagnostics",()=>{
                using(var f=new Fixture()){f.PS.FailSend=true;Assert(!f.P.PublishCurrentTask());Assert(f.P.Terminal&&f.P.LastTransportFailure!=null);
                    Eq("send",f.P.LastTransportFailure.Operation);Assert(f.P.LastTransportFailure.HResult!=0);Eq(Phase.Paused,f.Task.Phase);}
            });
            Test("participant withdrawal ends epoch without automatic resume",()=>{
                using(var f=new Fixture()){f.Grant();f.Preflight();f.Task.ParticipantWithdraw();f.P.Tick();Assert(f.P.Terminal&&f.P.CurrentPreflight==null);Eq(Phase.Ended,f.Task.Phase);}
            });
            Console.WriteLine("BOUNDARY: role-local actual owner, exact independent sealed clocks, memory queues only; no socket, SDK, GUI, recording or live presentation execution.");
            Console.WriteLine("RESULT process-memory "+passed+" passed; "+failed+" failed; baseline_exit "+baseline);return failed==0&&baseline==0?0:1;
        }
    }
}
