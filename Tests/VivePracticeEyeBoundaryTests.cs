using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Only synthetic simulation and in-memory observations are used here.
    public static class VivePracticeEyeBoundaryTests
    {
        sealed class Clock:IClock
        {
            public double Time;
            public string ClockId {get{return "synthetic-practice-boundary-clock";}}
            public double Now {get{return Time;}}
        }
        sealed class Provider:IViveProEyeProvider
        {
            public ViveProEyePoll Next;
            public ViveProEyePoll Poll(){return Next;}
        }
        sealed class Fixture:IDisposable
        {
            public readonly Clock Clock=new Clock{Time=10};
            public readonly Provider Provider=new Provider();
            public readonly ViveDevelopmentParticipant Task;
            public readonly VivePracticeEyeSource Source;
            public Fixture()
            {
                var allocation=new Allocation("synthetic-boundary-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var config=new SessionConfiguration("synthetic-boundary-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"synthetic-boundary-geometry");
                var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,mapping,.5,true);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                var broker=new ViveExclusiveObservationProvider(Provider,Clock);
                Source=VivePracticeEyeSource.CreateSynthetic(Task,Clock,broker.Acquire(),new ViveProEyeRehearsalCoordinates(),.25,true,true);
                Clock.Time=10.02;Task.OwnerBeginPractice(Actor.Owner);
                Clock.Time=10.04;Check.True(Source.BindPractice());
            }
            public void Dispose(){Source.Dispose();}
        }
        static ViveProEyePoll Poll(int sequence,double eyeTime,double headTime,bool headValid=true)
        {
            var ray=new ViveEyeRay(2,true,true,new Vec3(1,2,3),new Vec3(0,0,1));
            var eye=new ViveEyeFrame(sequence,sequence*10,eyeTime,false,ray,ray,ray);
            var head=new ViveHeadFrame(sequence,headTime,true,headValid,Rotation.Identity);
            return new ViveProEyePoll(head,eye,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,91);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("practice gaze omits head at or before consent, start or epoch boundary",()=>{
                // Consent=10.00, authoritative start=10.02, epoch=10.04.
                foreach(double headTime in new[]{9.99,10.0,10.01,10.02,10.03,10.04})
                using(var f=new Fixture())
                {
                    f.Clock.Time=10.06;var poll=Poll(1,10.05,headTime);f.Provider.Next=poll;
                    VivePracticeEyeCandidate candidate;
                    Check.True(f.Source.TryRead(out candidate),"Valid post-epoch eye should be preserved as raw-only");
                    Check.True(candidate!=null&&!candidate.DerivedAvailable&&!candidate.HeadUsable&&!candidate.MappedDirection.HasValue);
                    Check.True(candidate.RawPoll!=null&&candidate.RawPoll.Head==null,"Pre-boundary head must not escape via raw poll");
                    Check.True(Object.ReferenceEquals(poll.Eye,candidate.RawPoll.Eye),"Raw eye must remain unchanged");
                    Check.Equal(poll.NativeResultCode,candidate.RawPoll.NativeResultCode);Check.Equal(poll.NativeRuntimeStatus,candidate.RawPoll.NativeRuntimeStatus);
                    Check.True(!candidate.CanWrite&&!f.Source.GlobalExclusiveOwnershipProven&&!f.Source.HardwareCaptureTimeVerified);
                }
            });
            suite.Run("practice gaze derives only with valid strictly post-epoch head",()=>{
                using(var f=new Fixture())
                {
                    f.Clock.Time=10.06;f.Provider.Next=Poll(1,10.05,10.045);
                    VivePracticeEyeCandidate candidate;Check.True(f.Source.TryRead(out candidate));
                    Check.True(candidate!=null&&candidate.RawPoll.Head!=null&&candidate.HeadUsable&&candidate.DerivedAvailable&&candidate.MappedDirection.HasValue);
                    Check.True(candidate.RawPoll.Head.ObservedLocalSeconds>candidate.EpochStartedLocalSeconds);
                    Check.True(!candidate.CanWrite&&!candidate.MappingValidatedForApparatus);
                }
                using(var f=new Fixture())
                {
                    f.Clock.Time=10.06;f.Provider.Next=Poll(1,10.05,10.045,false);
                    VivePracticeEyeCandidate candidate;Check.True(f.Source.TryRead(out candidate));
                    Check.True(candidate!=null&&!candidate.HeadUsable&&!candidate.DerivedAvailable&&!candidate.MappedDirection.HasValue);
                    Check.True(candidate.RawPoll.Head==null,"Invalid head must not be exposed as usable context");
                }
            });
            suite.Run("practice raw-only observation can be followed by a fresh post-epoch head",()=>{
                using(var f=new Fixture())
                {
                    f.Clock.Time=10.06;f.Provider.Next=Poll(1,10.05,10.04);VivePracticeEyeCandidate first;
                    Check.True(f.Source.TryRead(out first)&&first.RawPoll.Head==null&&!first.DerivedAvailable);
                    f.Clock.Time=10.09;f.Provider.Next=Poll(2,10.08,10.075);VivePracticeEyeCandidate second;
                    Check.True(f.Source.TryRead(out second)&&second.HeadUsable&&second.DerivedAvailable);
                    Check.True(second.RawPoll.Head.ObservedLocalSeconds>second.EpochStartedLocalSeconds&&!second.CanWrite);
                }
            });
        }
    }
}
