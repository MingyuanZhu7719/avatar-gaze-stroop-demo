using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic provider and clock only. No socket, Unity, hardware, file or
    // actual experiment is started. Source-lock booleans are fixture data.
    public static class ViveWozPreflightSamplePumpTests
    {
        sealed class Clock : IClock
        {
            public double Value=10;public bool FailName,FailNow;public Func<double> OnNow;
            public double Now {get{if(FailNow)throw new InvalidOperationException();return OnNow==null?Value:OnNow();}}
            public string ClockId {get{if(FailName)throw new InvalidOperationException();return "op-clock";}}
        }
        sealed class Provider : IViveProEyeProvider
        {
            public ViveProEyePoll Next;public int Calls;public Func<ViveProEyePoll> OnPoll;
            public ViveProEyePoll Poll(){Calls++;return OnPoll==null?Next:OnPoll();}
        }
        sealed class Fixture : IDisposable
        {
            public readonly Clock Clock=new Clock();public readonly Provider Provider=new Provider();
            public readonly ViveExclusiveObservationProvider Broker;public readonly ViveExclusiveObservationProvider.Lease Lease;
            public readonly ViveDevelopmentPreflightChallenge Challenge;public readonly TaskSnapshot Task;
            public readonly ViveWozPreflightSamplePump Pump;
            public Fixture()
            {
                var mapper=new ViveProEyeRehearsalCoordinates();string stream=ViveDevelopmentTransport.OperatorStream(mapper.Version,"epoch");
                Challenge=new ViveDevelopmentPreflightChallenge("session",stream,"op-clock","participant-clock",mapper.Version,"epoch","challenge",0,0,Phase.BlockReady,9);
                Task=new TaskSnapshot("session",0,0,null,null,null,Phase.BlockReady,Condition.A);
                Broker=new ViveExclusiveObservationProvider(Provider,Clock);Lease=Broker.Acquire();
                Pump=new ViveWozPreflightSamplePump(Lease,Clock,mapper,Challenge,10,Status(0,10),.5,true);
            }
            public VivePreflightOperatorTask Status(long sequence,double receipt,TaskSnapshot task=null,bool selected=true)
            {return new VivePreflightOperatorTask(task??Task,true,selected,sequence,receipt);}
            public void Frame(long head,int eye,double observed,bool eyeValid=true,ViveEyeRuntimeState runtime=ViveEyeRuntimeState.Working,bool eyeConnected=true)
            {
                var ray=new ViveEyeRay(eyeValid?2UL:0UL,false,eyeValid,new Vec3(0,0,0),new Vec3(0,0,1));
                Provider.Next=new ViveProEyePoll(new ViveHeadFrame(head,observed,true,true,Rotation.Identity),
                    new ViveEyeFrame(eye,eye*8,observed,false,null,null,ray),runtime,ViveEyeCalibrationState.ReportedReady,eyeConnected,0);
            }
            public bool Read(VivePreflightOperatorTask task,Func<ViveProEyeObservation,bool> proof,out VivePreflightSampleCandidate result)
            {return Pump.TrySample(task,true,proof,out result);}
            public void Dispose(){Pump.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE preflight exclusive broker and claimed lease reject simultaneous consumers",()=>{
                using(var f=new Fixture()){
                    var other=new ViveExclusiveObservationProvider(f.Provider,f.Clock);
                    Check.Throws<InvalidOperationException>(()=>other.Acquire());
                    Check.Throws<InvalidOperationException>(()=>f.Lease.Poll());
                    Check.Throws<InvalidOperationException>(()=>new ViveWozPreflightSamplePump(f.Lease,f.Clock,new ViveProEyeRehearsalCoordinates(),f.Challenge,10,f.Status(0,10),.5,true));
                    Check.Throws<ArgumentException>(()=>new ViveExclusiveObservationProvider(f.Provider,new Clock()));
                }
            });
            suite.Run("VIVE preflight post-challenge frames form a local candidate only",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);VivePreflightSampleCandidate c;
                    Check.True(f.Read(f.Status(0,10),x=>true,out c));Check.Equal("challenge",c.Challenge);Check.Equal(1,f.Provider.Calls);
                    Check.True(!f.Pump.CanEnterCountdown&&!f.Pump.CanResume&&!f.Pump.HardwareCaptureTimeVerified);
                    Check.Near(10,c.ChallengeReceivedLocalSeconds);Check.Equal(0L,c.TaskPacketSequence);Check.Near(10,c.TaskReceivedLocalSeconds);
                }
            });
            suite.Run("VIVE preflight waits for observations strictly after challenge",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10);VivePreflightSampleCandidate c;
                    Check.True(!f.Read(f.Status(0,10),x=>true,out c)&&!f.Pump.RecoveryDecisionRequired);
                    f.Clock.Value=10.2;f.Frame(2,2,10.2);Check.True(f.Read(f.Status(1,10.2),x=>true,out c));
                }
            });
            suite.Run("VIVE preflight duplicate eye waits without extending the deadline",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);VivePreflightSampleCandidate c;int proofs=0;
                    Check.True(f.Read(f.Status(0,10),x=>{proofs++;return true;},out c));
                    f.Clock.Value=10.2;f.Frame(2,1,10.1);Check.True(!f.Read(f.Status(1,10.2),x=>{proofs++;return true;},out c));
                    Check.True(!f.Pump.RecoveryDecisionRequired);Check.Equal(1,proofs);
                    f.Clock.Value=10.61;f.Frame(3,2,10.61);Check.True(!f.Read(f.Status(2,10.61),x=>true,out c));
                    Check.True(f.Pump.RecoveryDecisionRequired);Check.Equal("observation_gap",f.Pump.LastIssue);
                }
            });
            suite.Run("VIVE preflight duplicate invalid status latches immediately",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);VivePreflightSampleCandidate c;Check.True(f.Read(f.Status(0,10),x=>true,out c));
                    f.Clock.Value=10.2;f.Frame(2,1,10.1,false);Check.True(!f.Read(f.Status(1,10.2),x=>true,out c));
                    Check.True(f.Pump.RecoveryDecisionRequired);Check.Equal("explicit_head_or_eye_invalid",f.Pump.LastIssue);
                }
            });
            suite.Run("VIVE preflight fresh task cannot erase an elapsed receipt gap",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.49;f.Frame(1,1,10.49);VivePreflightSampleCandidate c;Check.True(f.Read(f.Status(0,10),x=>true,out c));int reads=f.Provider.Calls;
                    f.Clock.Value=10.51;f.Frame(2,2,10.51);Check.True(!f.Read(f.Status(1,10.51),x=>true,out c));
                    Check.Equal("authoritative_task_gap",f.Pump.LastIssue);Check.Equal(reads,f.Provider.Calls);
                }
            });
            suite.Run("VIVE preflight local proof rejection never refreshes freshness",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);VivePreflightSampleCandidate c;
                    Check.True(!f.Read(f.Status(0,10),x=>false,out c));Check.Equal("local_proof_not_constructed",f.Pump.LastIssue);
                    f.Clock.Value=10.51;f.Frame(2,2,10.51);Check.True(!f.Read(f.Status(1,10.51),x=>true,out c));
                    Check.Equal("observation_gap",f.Pump.LastIssue);
                }
            });
            suite.Run("VIVE preflight phase and source changes latch without polling",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);VivePreflightSampleCandidate c;
                    var next=new TaskSnapshot("session",0,0,null,null,null,Phase.Countdown,Condition.A);
                    Check.True(!f.Read(f.Status(1,10.1,next),x=>true,out c));Check.True(f.Pump.RecoveryDecisionRequired);Check.Equal(0,f.Provider.Calls);
                }
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;VivePreflightSampleCandidate c;
                    Check.True(!f.Read(f.Status(1,10.1,null,false),x=>true,out c));Check.True(f.Pump.RecoveryDecisionRequired);Check.Equal(0,f.Provider.Calls);
                }
            });
            suite.Run("VIVE preflight clock getter failures and rollback latch",()=>{
                using(var f=new Fixture()){
                    f.Clock.FailName=true;VivePreflightSampleCandidate c;Check.True(!f.Read(f.Status(0,10),x=>true,out c));Check.True(f.Pump.RecoveryDecisionRequired);
                }
                using(var f=new Fixture()){
                    f.Clock.FailNow=true;VivePreflightSampleCandidate c;Check.True(!f.Read(f.Status(0,10),x=>true,out c));Check.True(f.Pump.RecoveryDecisionRequired);
                }
                using(var f=new Fixture()){
                    f.Clock.Value=9.9;VivePreflightSampleCandidate c;Check.True(!f.Read(f.Status(0,10),x=>true,out c));Check.Equal("clock_fault",f.Pump.LastIssue);
                }
            });
            suite.Run("VIVE preflight provider loss and reentrant consumer latch",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;VivePreflightSampleCandidate c;Check.True(!f.Pump.TrySample(f.Status(0,10),false,x=>true,out c));
                    Check.Equal("source_unavailable",f.Pump.LastIssue);Check.Equal(0,f.Provider.Calls);
                }
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);VivePreflightSampleCandidate c;
                    Check.True(!f.Read(f.Status(0,10),x=>{VivePreflightSampleCandidate other;return f.Read(f.Status(0,10),v=>true,out other);},out c));
                    Check.Equal("reentrant_observation",f.Pump.LastIssue);Check.Equal(1,f.Provider.Calls);
                }
            });
            suite.Run("VIVE preflight proof callback cannot outlive deadline or worker lease",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);VivePreflightSampleCandidate c;
                    Check.True(!f.Read(f.Status(0,10),x=>{f.Clock.Value=10.6;return true;},out c));
                    Check.Equal("clock_or_observation_gap_during_proof",f.Pump.LastIssue);
                }
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);VivePreflightSampleCandidate c;
                    Check.True(!f.Read(f.Status(0,10),x=>{f.Pump.Dispose();return true;},out c));
                    Check.True(c==null);
                }
            });
            suite.Run("VIVE preflight reordered and future sensor observations latch",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,5,10.1);VivePreflightSampleCandidate c;Check.True(f.Read(f.Status(0,10),x=>true,out c));
                    f.Clock.Value=10.2;f.Frame(2,4,10.2);Check.True(!f.Read(f.Status(1,10.2),x=>true,out c));
                    Check.Equal("stale_out_of_order_or_invalid_frame",f.Pump.LastIssue);
                }
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.2);VivePreflightSampleCandidate c;
                    Check.True(!f.Read(f.Status(0,10),x=>true,out c));Check.Equal("stale_out_of_order_or_invalid_frame",f.Pump.LastIssue);
                }
            });
            suite.Run("VIVE preflight duplicate frames still report runtime and device faults",()=>{
                foreach(int fault in new[]{0,1})using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);VivePreflightSampleCandidate c;Check.True(f.Read(f.Status(0,10),x=>true,out c));
                    f.Clock.Value=10.2;f.Frame(2,1,10.1,true,fault==0?ViveEyeRuntimeState.Error:ViveEyeRuntimeState.Working,fault!=1);
                    Check.True(!f.Read(f.Status(1,10.2),x=>true,out c));Check.Equal("explicit_head_or_eye_invalid",f.Pump.LastIssue);
                }
            });
            suite.Run("VIVE preflight cached task cannot acquire a later receipt",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);VivePreflightSampleCandidate c;
                    Check.True(!f.Read(f.Status(0,10.1),x=>true,out c));Check.Equal("task_receipt_regressed_or_changed",f.Pump.LastIssue);
                    Check.Equal(0,f.Provider.Calls);
                }
            });
            suite.Run("VIVE preflight provider reentry cannot release or reacquire an active worker",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);var other=new ViveExclusiveObservationProvider(f.Provider,f.Clock);
                    int denied=0;f.Provider.OnPoll=()=>{
                        try{f.Lease.Dispose();}catch(InvalidOperationException){denied++;}
                        try{other.Acquire();}catch(InvalidOperationException){denied++;}
                        try{f.Lease.Poll();}catch(InvalidOperationException){denied++;}
                        return f.Provider.Next;
                    };
                    VivePreflightSampleCandidate c;Check.True(f.Read(f.Status(0,10),x=>true,out c));
                    Check.Equal(3,denied);Check.Equal(1,f.Provider.Calls);
                }
            });
            suite.Run("VIVE preflight cannot claim an unclaimed lease during an active poll",()=>{
                using(var f=new Fixture()){
                    f.Pump.Dispose();var lease=f.Broker.Acquire();int denied=0;
                    f.Frame(1,1,10);f.Provider.OnPoll=()=>{
                        try{new ViveWozPreflightSamplePump(lease,f.Clock,new ViveProEyeRehearsalCoordinates(),f.Challenge,10,f.Status(0,10),.5,true);}
                        catch(InvalidOperationException){denied++;}
                        return f.Provider.Next;
                    };
                    lease.Poll();Check.Equal(1,denied);Check.Equal(1,f.Provider.Calls);lease.Dispose();
                }
            });
            suite.Run("VIVE preflight blocking poll cannot construct a proof after the deadline",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.4;f.Frame(1,1,10.4);int proofs=0;
                    f.Provider.OnPoll=()=>{f.Clock.Value=10.6;return f.Provider.Next;};
                    VivePreflightSampleCandidate c;Check.True(!f.Read(f.Status(0,10),x=>{proofs++;return true;},out c));
                    Check.Equal(0,proofs);Check.Equal("clock_or_observation_gap_after_poll",f.Pump.LastIssue);
                }
            });
            suite.Run("VIVE preflight catches callback rollback or delay even when proof is rejected",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);
                    f.Provider.OnPoll=()=>{f.Clock.Value=10.2;return f.Provider.Next;};
                    VivePreflightSampleCandidate c;Check.True(!f.Read(f.Status(0,10),x=>{f.Clock.Value=10.15;return false;},out c));
                    Check.Equal("clock_or_observation_gap_during_proof",f.Pump.LastIssue);
                }
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);
                    VivePreflightSampleCandidate c;Check.True(!f.Read(f.Status(0,10),x=>{f.Clock.Value=10.6;return false;},out c));
                    Check.Equal("clock_or_observation_gap_during_proof",f.Pump.LastIssue);
                }
            });
            suite.Run("VIVE preflight duplicate poll cannot hide a gap or clock rollback",()=>{
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);VivePreflightSampleCandidate c;Check.True(f.Read(f.Status(0,10),x=>true,out c));
                    f.Clock.Value=10.4;f.Frame(2,1,10.1);int proofs=0;f.Provider.OnPoll=()=>{f.Clock.Value=10.7;return f.Provider.Next;};
                    Check.True(!f.Read(f.Status(1,10.4),x=>{proofs++;return true;},out c));
                    Check.Equal(0,proofs);Check.Equal("clock_or_observation_gap_after_poll",f.Pump.LastIssue);
                }
                using(var f=new Fixture()){
                    f.Clock.Value=10.1;f.Frame(1,1,10.1);int reads=0;
                    f.Provider.OnPoll=()=>{f.Clock.OnNow=()=>++reads==1?10.2:10.15;return f.Provider.Next;};
                    VivePreflightSampleCandidate c;Check.True(!f.Read(f.Status(0,10),x=>true,out c));
                    Check.Equal("clock_or_observation_gap_after_poll",f.Pump.LastIssue);
                }
            });
        }
    }
}
