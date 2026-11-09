using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Pure Simulation and synthetic providers only. This checks the source's
    // local mapping boundary; it does not validate a headset or a study gate.
    public static class VivePracticeSealedMapperTests
    {
        sealed class Clock:IClock
        {
            public double Time=10;
            public string ClockId {get{return "sealed-mapper-test-clock";}}
            public double Now {get{return Time;}}
        }
        sealed class Provider:IViveProEyeProvider
        {
            public ViveProEyePoll Next;
            public ViveProEyePoll Poll(){return Next;}
        }
        sealed class UntrustedMapper:IViveProEyeCoordinateMapper
        {
            readonly Clock clock;
            public bool Trap;
            public int VersionCalls,MapCalls;
            public UntrustedMapper(Clock clock){this.clock=clock;}
            public string Version
            {
                get
                {
                    VersionCalls++;
                    if(Trap){clock.Time+=100;throw new InvalidOperationException("Untrusted getter should not be polled");}
                    return "synthetic-untrusted-mapper";
                }
            }
            public bool TryMapHead(Rotation value,out Rotation mapped){mapped=value;MapCalls++;return true;}
            public bool TryMapEye(Rotation head,Vec3 direction,out Vec3 mapped)
            {MapCalls++;mapped=new Vec3(0,0,1);return true;}
        }
        static ViveDevelopmentParticipant Task(Clock clock)
        {
            var allocation=new Allocation("sealed-test-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},
                GazeTarget.Participant,new[]{0,1,2,3});
            var config=new SessionConfiguration("sealed-test-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"sealed-test-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var task=new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));return task;
        }
        static ViveProEyePoll Poll(int sequence,double time)
        {
            var ray=new ViveEyeRay(2,true,true,new Vec3(0,0,0),new Vec3(0,0,1));
            var eye=new ViveEyeFrame(sequence,sequence*10,time,false,ray,ray,ray);
            var head=new ViveHeadFrame(sequence,time,true,true,Rotation.Identity);
            return new ViveProEyePoll(head,eye,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
        }
        sealed class Fixture:IDisposable
        {
            public readonly Clock Clock=new Clock();
            public readonly Provider Provider=new Provider();
            public readonly IViveProEyeCoordinateMapper Mapper;
            public readonly ViveDevelopmentParticipant Task;
            readonly VivePracticeEyeSource reference;
            readonly VivePracticeEyeFencedSource fenced;
            public Fixture(bool useFenced,bool trusted)
            {
                Mapper=trusted?(IViveProEyeCoordinateMapper)new ViveProEyeRehearsalCoordinates():new UntrustedMapper(Clock);
                Task=VivePracticeSealedMapperTests.Task(Clock);
                var lease=new ViveExclusiveObservationProvider(Provider,Clock).Acquire();
                if(useFenced)fenced=VivePracticeEyeFencedSource.CreateSynthetic(Task,Clock,lease,Mapper,.25,true,true);
                else reference=VivePracticeEyeSource.CreateSynthetic(Task,Clock,lease,Mapper,.25,true,true);
            }
            public bool Readiness(){return fenced==null?reference.PollReadiness():fenced.PollReadiness();}
            public void Start(string mapping)
            {
                Clock.Time=10.2;Task.OwnerBeginPractice(Actor.Owner);
                if(fenced==null){Check.True(reference.BindPractice());return;}
                var context=new VivePracticeContextV3(Task.SessionId,"sealed-test-participant","sealed-test-stream","sealed-test-device",
                    Clock.ClockId,mapping,1,"sealed-test-epoch",0,null,VivePracticeEpochReasonV3.Initial,10.2);
                Check.True(fenced.BindPractice(context));
            }
            public bool Read(out VivePracticeEyeCandidate candidate)
            {return fenced==null?reference.TryRead(out candidate):fenced.TryRead(out candidate);}
            public void Dispose(){if(fenced!=null)fenced.Dispose();if(reference!=null)reference.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("practice custom mapper is never invoked during readiness or sample",()=>{
                foreach(bool fenced in new[]{false,true})using(var f=new Fixture(fenced,false))
                {
                    var mapper=(UntrustedMapper)f.Mapper;int initialCalls=mapper.VersionCalls;mapper.Trap=true;
                    f.Clock.Time=10.11;f.Provider.Next=Poll(1,10.10);
                    Check.True(!f.Readiness(),"An unsupported mapper cannot assert readiness");
                    Check.Equal(initialCalls,mapper.VersionCalls);Check.Equal(0,mapper.MapCalls);Check.Near(10.11,f.Clock.Time);
                    f.Start("synthetic-untrusted-mapper");f.Clock.Time=10.31;f.Provider.Next=Poll(2,10.30);
                    VivePracticeEyeCandidate candidate;Check.True(f.Read(out candidate));
                    Check.True(candidate!=null&&!candidate.CanWrite&&!candidate.DerivedAvailable&&!candidate.MappedDirection.HasValue,
                        "A valid eye frame from an unsupported mapper must remain raw-only");
                    Check.True(candidate.RawPoll!=null&&candidate.RawPoll.Eye!=null);Check.Equal(2,candidate.RawPoll.Eye.FrameSequence);
                    Check.Equal(initialCalls,mapper.VersionCalls);Check.Equal(0,mapper.MapCalls);Check.Near(10.31,f.Clock.Time);
                }
            });
            suite.Run("practice exact sealed reference mapper may provide derived gaze",()=>{
                foreach(bool fenced in new[]{false,true})using(var f=new Fixture(fenced,true))
                {
                    f.Clock.Time=10.11;f.Provider.Next=Poll(1,10.10);Check.True(f.Readiness());
                    f.Start(ViveProEyeRehearsalCoordinates.MappingVersion);f.Clock.Time=10.31;f.Provider.Next=Poll(2,10.30);
                    VivePracticeEyeCandidate candidate;Check.True(f.Read(out candidate));
                    Check.True(candidate!=null&&candidate.HeadUsable&&candidate.DerivedAvailable&&candidate.MappedDirection.HasValue&&!candidate.CanWrite);
                    Check.Equal(ViveProEyeRehearsalCoordinates.MappingVersion,candidate.MappingVersion);
                }
            });
        }
    }
}
