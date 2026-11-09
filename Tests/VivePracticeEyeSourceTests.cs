using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic simulation and in-memory provider only. No eye device, files,
    // player, consent dialog, or research-admission path is touched here.
    public static class VivePracticeEyeSourceTests
    {
        sealed class Clock:IClock
        {
            public double Time;public string Id="synthetic-practice-clock";public Action Hook;
            public double Now {get{var h=Hook;Hook=null;if(h!=null)h();return Time;}}
            public string ClockId {get{return Id;}}
        }
        sealed class Provider:IViveProEyeProvider
        {
            public ViveProEyePoll Next;public int Calls;public bool Throw;public Action Hook;
            public ViveProEyePoll Poll(){Calls++;var h=Hook;Hook=null;if(h!=null)h();if(Throw)throw new InvalidOperationException("synthetic-private-error");return Next;}
        }
        sealed class SyntheticClient:IViveProEyeClient
        {
            public volatile ViveProEyePoll Next;
            public string Diagnostic {get{return "synthetic client";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){return Next;}
            public void Dispose(){}
        }
        static ViveProEyePoll Poll(int sequence,int nativeTime,double localTime,Vec3? direction=null,
            bool valid=true,ulong mask=2,bool noUser=false,ViveEyeRuntimeState runtime=ViveEyeRuntimeState.Working,
            ViveEyeCalibrationState calibration=ViveEyeCalibrationState.ReportedReady,bool connected=true,int? result=0)
        {
            var ray=new ViveEyeRay(mask,true,valid,new Vec3(1,2,3),direction??new Vec3(0,0,1));
            var eye=new ViveEyeFrame(sequence,nativeTime,localTime,noUser,ray,ray,ray);
            return new ViveProEyePoll(new ViveHeadFrame(sequence,localTime,true,true,Rotation.Identity),eye,runtime,calibration,connected,result,2);
        }
        static ViveProEyePoll WithHead(ViveProEyePoll poll,long sequence,double localTime)
        {return new ViveProEyePoll(new ViveHeadFrame(sequence,localTime,true,true,Rotation.Identity),poll.Eye,poll.Runtime,poll.Calibration,poll.EyeDeviceConnected,poll.NativeResultCode,poll.NativeRuntimeStatus);}
        static ViveDevelopmentParticipant Participant(Clock clock,string session="synthetic-practice-session")
        {
            var allocation=new Allocation("synthetic-practice-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},
                GazeTarget.Participant,new[]{0,1,2,3});
            var config=new SessionConfiguration(session,42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"synthetic-practice-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},
                {ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var participant=new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
            participant.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));return participant;
        }
        sealed class Fixture:IDisposable
        {
            public readonly Clock Clock=new Clock{Time=10};
            public readonly Provider Provider=new Provider();
            public readonly ViveDevelopmentParticipant Task;
            public readonly ViveExclusiveObservationProvider Broker;
            public readonly ViveExclusiveObservationProvider.Lease Lease;
            public readonly VivePracticeEyeSource Source;
            public readonly ViveProEyeRehearsalCoordinates Mapper=new ViveProEyeRehearsalCoordinates();
            public Fixture()
            {
                Task=Participant(Clock);Broker=new ViveExclusiveObservationProvider(Provider,Clock);Lease=Broker.Acquire();
                Source=VivePracticeEyeSource.CreateSynthetic(Task,Clock,Lease,Mapper,.25,true,true);
            }
            public void StartPractice()
            {Clock.Time=10.2;Task.OwnerBeginPractice(Actor.Owner);Check.True(Source.BindPractice());}
            public void Dispose(){Source.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("practice eye source refuses either missing opt-in before polling",()=>{
                foreach(bool eye in new[]{false,true})foreach(bool task in new[]{false,true})
                {
                    if(eye&&task)continue;
                    var clock=new Clock{Time=10};var provider=new Provider();var participant=Participant(clock);
                    var broker=new ViveExclusiveObservationProvider(provider,clock);var lease=broker.Acquire();
                    Check.Throws<InvalidOperationException>(()=>VivePracticeEyeSource.CreateSynthetic(participant,clock,lease,new ViveProEyeRehearsalCoordinates(),.25,eye,task));
                    Check.Equal(0,provider.Calls);using(var next=broker.Acquire()){}
                }
            });
            suite.Run("practice eye source requires the identical participant and lease clock",()=>{
                var one=new Clock{Time=10};var sameLabel=new Clock{Time=10};var task=Participant(one);var provider=new Provider();
                var broker=new ViveExclusiveObservationProvider(provider,one);var lease=broker.Acquire();
                Check.Throws<ArgumentException>(()=>VivePracticeEyeSource.CreateSynthetic(task,sameLabel,lease,new ViveProEyeRehearsalCoordinates(),.25,true,true));
                Check.Equal(0,provider.Calls);using(var next=broker.Acquire()){}
                using(var worker=new ViveProEyeWorker(one,()=>null,()=>null))
                    Check.Throws<ArgumentException>(()=>new VivePracticeEyeSource(task,sameLabel,worker,new ViveProEyeRehearsalCoordinates(),.25,true,true));
            });
            suite.Run("practice readiness is not a gaze candidate or study permission",()=>{
                using(var f=new Fixture())
                {
                    f.Clock.Time=10.11;f.Provider.Next=Poll(1,10,10.1);
                    Check.True(f.Source.PollReadiness());Check.Equal(VivePracticeEyeStatus.ReadyCandidate,f.Source.Status);
                    Check.True(!f.Source.PracticeBoundaryWasBound&&!f.Source.CanWrite&&!f.Source.CanEnterCountdown&&
                        !f.Source.GlobalExclusiveOwnershipProven&&!f.Source.ClientClockVerified&&!f.Source.ConsentAuthenticated&&!f.Source.HardwareCaptureTimeVerified);
                    VivePracticeEyeCandidate candidate;Check.True(!f.Source.TryRead(out candidate)&&candidate==null);
                    Check.True(f.Source.RecoveryDecisionRequired);
                }
            });
            suite.Run("practice boundary must arise from the same actual task instance",()=>{
                using(var f=new Fixture())
                {
                    // An independently constructed event and a second task do not alter f.Task.
                    var forged=new JournalEvent(0,10,f.Clock.ClockId,f.Task.SessionId,-1,0,null,"practice_started",new Dictionary<string,string>{{"attempt","1"}});
                    Check.Equal("practice_started",forged.Kind);var other=Participant(f.Clock,f.Task.SessionId);other.OwnerBeginPractice(Actor.Owner);
                    Check.True(!f.Source.BindPractice());Check.True(f.Source.RecoveryDecisionRequired&&!f.Source.PracticeBoundaryWasBound);
                    VivePracticeEyeCandidate candidate;Check.True(!f.Source.TryRead(out candidate)&&candidate==null);
                }
            });
            suite.Run("practice boundary at identical opt-in time is rejected",()=>{
                using(var f=new Fixture())
                {
                    f.Task.OwnerBeginPractice(Actor.Owner);
                    Check.True(!f.Source.BindPractice()&&f.Source.RecoveryDecisionRequired&&!f.Source.PracticeBoundaryWasBound);
                    Check.Equal(0,f.Provider.Calls);
                }
            });
            suite.Run("practice candidate must be strictly newer than opt-in start epoch and readiness",()=>{
                using(var f=new Fixture())
                {
                    f.Clock.Time=10.11;f.Provider.Next=Poll(1,10,10.1);Check.True(f.Source.PollReadiness());
                    f.StartPractice();Check.True(f.Source.PracticeBoundaryWasBound);
                    Check.Equal(VivePracticeEyeStatus.WaitingForFreshFrame,f.Source.Status);Check.True(!f.Source.CanWrite);
                    f.Clock.Time=10.21;VivePracticeEyeCandidate candidate;
                    f.Provider.Next=WithHead(f.Provider.Next,2,10.21);
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null);Check.Equal(VivePracticeEyeStatus.Duplicate,f.Source.Status);
                    f.Provider.Next=WithHead(Poll(2,20,10.2),3,10.21);
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null);Check.Equal(VivePracticeEyeStatus.WaitingForFreshFrame,f.Source.Status);
                    f.Clock.Time=10.23;f.Provider.Next=WithHead(Poll(3,30,10.22),4,10.23);Check.True(f.Source.TryRead(out candidate));
                    Check.True(candidate!=null&&candidate.BlockIndex==-1&&!candidate.Condition.HasValue&&!candidate.MainSourceSelected&&!candidate.CanWrite);
                    Check.Equal(1,candidate.PracticeAttempt);Check.True(candidate.PracticeStartSequence>=0);
                    Check.True(candidate.RawPoll.Eye.ObservedLocalSeconds>candidate.EpochStartedLocalSeconds);
                    Check.True(!candidate.HardwareCaptureTimeVerified&&!candidate.MappingValidatedForApparatus);
                }
            });
            suite.Run("practice source preserves raw gaze and derives only valid mapped direction",()=>{
                using(var f=new Fixture())
                {
                    f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=Poll(1,10,10.3,new Vec3(1.2,0,1.6));
                    VivePracticeEyeCandidate candidate;Check.True(f.Source.TryRead(out candidate));
                    Check.Near(1.2,candidate.RawPoll.Eye.Combined.Direction.X);Check.Near(1.6,candidate.RawPoll.Eye.Combined.Direction.Z);
                    Check.Equal(2UL,candidate.RawPoll.Eye.Combined.ValidityMask);Check.True(candidate.RawPoll.Eye.Combined.DirectionReportedValid);
                    Check.True(candidate.HeadUsable&&candidate.DerivedAvailable&&candidate.MappedDirection.HasValue);
                    Check.Near(-.6,candidate.MappedDirection.Value.X);Check.Near(.8,candidate.MappedDirection.Value.Z);
                    Check.Equal(ViveProEyeRehearsalCoordinates.MappingVersion,candidate.MappingVersion);
                }
            });
            suite.Run("practice source rejects unusable native or derived observations without candidates",()=>{
                var invalid=new[]{Poll(1,10,10.3,valid:false),Poll(1,10,10.3,mask:0),Poll(1,10,10.3,noUser:true),
                    Poll(1,10,10.3,runtime:ViveEyeRuntimeState.Unknown),Poll(1,10,10.3,calibration:ViveEyeCalibrationState.Required),
                    Poll(1,10,10.3,connected:false),Poll(1,10,10.3,result:1),Poll(1,10,10.3,direction:new Vec3(0,0,0))};
                foreach(var poll in invalid)using(var f=new Fixture())
                {f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=poll;VivePracticeEyeCandidate candidate;
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null);Check.True(f.Source.RecoveryDecisionRequired&&!f.Source.CanWrite);}
            });
            suite.Run("practice source never upgrades duplicate or older gaze to new coverage",()=>{
                using(var f=new Fixture())
                {
                    f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=Poll(1,10,10.3);VivePracticeEyeCandidate candidate;
                    Check.True(f.Source.TryRead(out candidate));f.Provider.Next=WithHead(f.Provider.Next,2,10.31);
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null);
                    Check.Equal(VivePracticeEyeStatus.Duplicate,f.Source.Status);
                    f.Clock.Time=10.32;f.Provider.Next=WithHead(Poll(0,9,10.29),3,10.32);Check.True(!f.Source.TryRead(out candidate)&&candidate==null);
                    Check.True(f.Source.RecoveryDecisionRequired);
                }
            });
            suite.Run("practice missing provider and backwards clock fail closed without payload",()=>{
                using(var f=new Fixture())
                {f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=null;VivePracticeEyeCandidate candidate;
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null&&f.Source.RecoveryDecisionRequired);}
                using(var f=new Fixture())
                {f.StartPractice();f.Clock.Time=10.1;f.Provider.Next=Poll(1,10,10.09);VivePracticeEyeCandidate candidate;
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null&&f.Source.RecoveryDecisionRequired);Check.Equal(0,f.Provider.Calls);}
                using(var f=new Fixture())
                {f.StartPractice();f.Clock.Time=10.31;f.Provider.Throw=true;VivePracticeEyeCandidate candidate;
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null&&f.Source.RecoveryDecisionRequired);
                    Check.True(f.Source.LastIssue==null||!f.Source.LastIssue.Contains("synthetic-private-error"));}
            });
            suite.Run("practice candidate that becomes stale in provider callback is not published",()=>{
                using(var f=new Fixture())
                {
                    f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=Poll(1,10,10.3);
                    f.Provider.Hook=()=>f.Clock.Time=10.7;VivePracticeEyeCandidate candidate;
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null&&f.Source.RecoveryDecisionRequired);
                }
            });
            suite.Run("practice wrong-thread call latches source and prevents owner publication",()=>{
                using(var f=new Fixture())
                {
                    f.StartPractice();bool accepted=true;var thread=new Thread(()=>{VivePracticeEyeCandidate value;accepted=f.Source.TryRead(out value);});
                    thread.Start();Check.True(thread.Join(5000));Check.True(!accepted&&f.Source.RecoveryDecisionRequired);
                    f.Clock.Time=10.31;f.Provider.Next=Poll(1,10,10.3);VivePracticeEyeCandidate candidate;
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null);Check.Equal(0,f.Provider.Calls);
                }
            });
            suite.Run("practice production constructor binds a real worker using a synthetic client and same clock",()=>{
                var clock=new Clock{Time=10};var task=Participant(clock);var client=new SyntheticClient{Next=Poll(1,10,10.1)};long head=0;
                using(var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++head,clock.Now,true,true,Rotation.Identity),()=>client))
                using(var source=new VivePracticeEyeSource(task,clock,worker,new ViveProEyeRehearsalCoordinates(),.25,true,true))
                {
                    clock.Time=10.11;Check.True(worker.Start());int seq=0,native=0;double at=0;
                    var deadline=DateTime.UtcNow.AddSeconds(3);
                    while(DateTime.UtcNow<deadline&&!worker.TryPeekLatestEyeMetadata(out seq,out native,out at))Thread.Sleep(2);
                    Check.True(seq==1&&worker.UsesClock(clock));Check.True(source.PollReadiness());
                    clock.Time=10.2;task.OwnerBeginPractice(Actor.Owner);Check.True(source.BindPractice());
                    client.Next=Poll(2,20,10.3);clock.Time=10.31;deadline=DateTime.UtcNow.AddSeconds(3);
                    do{if(worker.TryPeekLatestEyeMetadata(out seq,out native,out at)&&seq==2)break;Thread.Sleep(2);}while(DateTime.UtcNow<deadline);
                    Check.Equal(2,seq);VivePracticeEyeCandidate candidate;Check.True(source.TryRead(out candidate));
                    Check.True(candidate!=null&&!candidate.CanWrite&&!source.GlobalExclusiveOwnershipProven&&!source.ClientClockVerified);
                    Check.True(worker.Stop(1000));
                }
            });
            suite.Run("practice cooperative lease forbids another cooperating consumer",()=>{
                using(var f=new Fixture())
                {
                    Check.Throws<InvalidOperationException>(()=>f.Broker.Acquire());
                    Check.Throws<InvalidOperationException>(()=>f.Lease.Poll());Check.Equal(0,f.Provider.Calls);
                    Check.True(!f.Source.GlobalExclusiveOwnershipProven&&!f.Source.CanWrite);
                }
            });
            suite.Run("practice pause terminates candidate until explicit new recovery context",()=>{
                using(var f=new Fixture())
                {
                    f.StartPractice();f.Task.ParticipantPause();Check.Equal(Phase.Paused,f.Task.Phase);
                    f.Clock.Time=10.31;f.Provider.Next=Poll(1,10,10.3);VivePracticeEyeCandidate candidate;
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null&&f.Source.RecoveryDecisionRequired);
                    Check.Equal(0,f.Provider.Calls);
                }
            });
            suite.Run("practice readiness rejects task changes from provider and post-provider clock callbacks",()=>{
                using(var f=new Fixture())
                {
                    f.Clock.Time=10.11;f.Provider.Next=Poll(1,10,10.1);f.Provider.Hook=()=>f.Task.OwnerBeginPractice(Actor.Owner);
                    Check.True(!f.Source.PollReadiness()&&f.Source.RecoveryDecisionRequired);Check.Equal(Phase.Practice,f.Task.Phase);
                }
                using(var f=new Fixture())
                {
                    f.Clock.Time=10.11;f.Provider.Next=Poll(1,10,10.1);f.Provider.Hook=()=>f.Clock.Hook=()=>f.Task.OwnerBeginPractice(Actor.Owner);
                    Check.True(!f.Source.PollReadiness()&&f.Source.RecoveryDecisionRequired);Check.Equal(Phase.Practice,f.Task.Phase);
                }
            });
            suite.Run("practice bind rejects a clock callback changing the task",()=>{
                using(var f=new Fixture())
                {
                    f.Clock.Time=10.2;f.Task.OwnerBeginPractice(Actor.Owner);f.Clock.Hook=()=>f.Task.ParticipantPause();
                    Check.True(!f.Source.BindPractice()&&f.Source.RecoveryDecisionRequired);Check.Equal(Phase.Paused,f.Task.Phase);
                    Check.True(!f.Source.PracticeBoundaryWasBound);
                }
            });
            suite.Run("practice read rejects pause or withdrawal in provider and clock callbacks",()=>{
                using(var f=new Fixture())
                {f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=Poll(1,10,10.3);f.Provider.Hook=()=>f.Task.ParticipantPause();
                    VivePracticeEyeCandidate candidate;Check.True(!f.Source.TryRead(out candidate)&&candidate==null&&f.Source.RecoveryDecisionRequired);}
                using(var f=new Fixture())
                {f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=Poll(1,10,10.3);f.Provider.Hook=()=>f.Clock.Hook=()=>f.Task.ParticipantWithdraw();
                    VivePracticeEyeCandidate candidate;Check.True(!f.Source.TryRead(out candidate)&&candidate==null&&f.Source.RecoveryDecisionRequired);}
                using(var f=new Fixture())
                {f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=Poll(1,10,10.3);f.Clock.Hook=()=>f.Task.ParticipantPause();
                    VivePracticeEyeCandidate candidate;Check.True(!f.Source.TryRead(out candidate)&&candidate==null&&f.Source.RecoveryDecisionRequired);}
            });
            suite.Run("practice read rejects a trial advance during provider callback",()=>{
                using(var f=new Fixture())
                {
                    f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=Poll(1,10,10.3);string prior=f.Task.CurrentTrial.TrialId;
                    f.Provider.Hook=()=>{
                        var trial=f.Task.CurrentTrial;f.Task.MarkPresented(trial.TrialId);
                        f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));float x=0,y=0;
                        switch(trial.Stimulus.Ink){case Colour.Red:y=1;break;case Colour.Green:x=1;break;case Colour.Blue:y=-1;break;default:x=-1;break;}
                        f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));
                    };
                    VivePracticeEyeCandidate candidate;Check.True(!f.Source.TryRead(out candidate)&&candidate==null&&f.Source.RecoveryDecisionRequired);
                    Check.True(f.Task.CurrentTrial!=null&&f.Task.CurrentTrial.TrialId!=prior);
                }
            });
            suite.Run("practice repeated eye frame cannot mask current runtime device native or head loss",()=>{
                var usable=Poll(1,10,10.3);var faults=new[]{
                    Poll(1,10,10.3,runtime:ViveEyeRuntimeState.Unavailable),
                    Poll(1,10,10.3,calibration:ViveEyeCalibrationState.Required),
                    Poll(1,10,10.3,connected:false),Poll(1,10,10.3,result:null),Poll(1,10,10.3,result:1)};
                foreach(var fault in faults)using(var f=new Fixture())
                {
                    f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=usable;VivePracticeEyeCandidate candidate;
                    Check.True(f.Source.TryRead(out candidate));f.Clock.Time=10.32;f.Provider.Next=WithHead(fault,2,10.32);
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null&&f.Source.RecoveryDecisionRequired);
                    Check.True(f.Source.Status!=VivePracticeEyeStatus.Duplicate);Check.Equal("observation_unusable",f.Source.LastIssue);
                }
                using(var f=new Fixture())
                {
                    f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=usable;VivePracticeEyeCandidate candidate;
                    Check.True(f.Source.TryRead(out candidate));f.Clock.Time=10.32;
                    f.Provider.Next=new ViveProEyePoll(new ViveHeadFrame(2,10.31,false,false,Rotation.Identity),usable.Eye,
                        ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
                    Check.True(!f.Source.TryRead(out candidate)&&candidate==null&&!f.Source.LastHeadUsable);
                    Check.Equal(VivePracticeEyeStatus.HeadUnavailable,f.Source.Status);
                }
            });
            suite.Run("fresh valid practice raw gaze remains distinct from unavailable derived head tracking",()=>{
                var usable=Poll(1,10,10.3,new Vec3(1.2,0,1.6));
                var heads=new[]{(ViveHeadFrame)null,new ViveHeadFrame(1,10.3,false,false,Rotation.Identity),
                    new ViveHeadFrame(1,9,true,true,Rotation.Identity)};
                foreach(var head in heads)using(var f=new Fixture())
                {
                    f.StartPractice();f.Clock.Time=10.31;f.Provider.Next=new ViveProEyePoll(head,usable.Eye,
                        ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
                    VivePracticeEyeCandidate candidate;Check.True(f.Source.TryRead(out candidate));
                    Check.True(candidate!=null&&!candidate.HeadUsable&&!candidate.DerivedAvailable&&!candidate.MappedDirection.HasValue);
                    Check.True(f.Source.LastEyeUsable&&!f.Source.LastHeadUsable&&!f.Source.LastDerivedDirectionUsable);
                    Check.Equal(VivePracticeEyeStatus.AcceptedRawOnlyCandidate,f.Source.Status);
                    Check.Near(1.2,candidate.RawPoll.Eye.Combined.Direction.X);Check.True(!candidate.CanWrite);
                }
            });
        }
    }
}
