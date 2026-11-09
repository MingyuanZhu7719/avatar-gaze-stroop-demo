using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic callback adversary only: never opens XR, a worker or a file.
    public static class VivePracticeClockMapperBoundaryTests
    {
        sealed class Clock:IClock
        {
            public double Time=10;public bool Armed,TriggerOnId;public int Skip;public Action Change;
            void Trigger(bool onId){if(Armed&&TriggerOnId==onId){if(Skip>0){Skip--;return;}Armed=false;Change();}}
            public string ClockId {get{Trigger(true);return "synthetic-clock-mapper";}}
            public double Now {get{Trigger(false);return Time;}}
        }
        sealed class Provider:IViveProEyeProvider
        {
            public ViveProEyePoll Next;public Action Hook;
            public ViveProEyePoll Poll(){var hook=Hook;Hook=null;if(hook!=null)hook();return Next;}
        }
        static ViveDevelopmentParticipant Task(Clock clock)
        {
            var allocation=new Allocation("synthetic-mapper-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var config=new SessionConfiguration("synthetic-mapper-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"synthetic-mapper-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var task=new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));return task;
        }
        static ViveProEyePoll Poll(double time)
        {
            var ray=new ViveEyeRay(2,true,true,new Vec3(1,2,3),new Vec3(1.2,0,1.6));
            return new ViveProEyePoll(new ViveHeadFrame(1,time,true,true,Rotation.Identity),
                new ViveEyeFrame(1,10,time,false,ray,ray,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
        }
        sealed class Fixture:IDisposable
        {
            public readonly Clock Clock=new Clock();public readonly Provider Provider=new Provider();public readonly ViveProEyeRehearsalCoordinates Mapper=new ViveProEyeRehearsalCoordinates();
            public readonly ViveDevelopmentParticipant Task;public readonly VivePracticeEyeSource Reference;public readonly VivePracticeEyeFencedSource Fenced;
            public Fixture(bool fenced,bool triggerOnId)
            {
                Clock.TriggerOnId=triggerOnId;Clock.Change=()=>Clock.Time=10.7;
                Task=VivePracticeClockMapperBoundaryTests.Task(Clock);
                var lease=new ViveExclusiveObservationProvider(Provider,Clock).Acquire();
                if(fenced)Fenced=VivePracticeEyeFencedSource.CreateSynthetic(Task,Clock,lease,Mapper,.25,true,true);
                else Reference=VivePracticeEyeSource.CreateSynthetic(Task,Clock,lease,Mapper,.25,true,true);
            }
            public void Start()
            {
                Clock.Time=10.2;Task.OwnerBeginPractice(Actor.Owner);
                if(Reference!=null)Check.True(Reference.BindPractice());
                else
                {
                    var context=new VivePracticeContextV3(Task.SessionId,"synthetic-participant","synthetic-stream","synthetic-device",Clock.ClockId,
                        Mapper.Version,1,"synthetic-epoch",0,null,VivePracticeEpochReasonV3.Initial,10.2);
                    Check.True(Fenced.BindPractice(context));
                }
            }
            public bool Ready(){return Reference!=null?Reference.PollReadiness():Fenced.PollReadiness();}
            public bool Read(out VivePracticeEyeCandidate candidate){return Reference!=null?Reference.TryRead(out candidate):Fenced.TryRead(out candidate);}
            // Provider arms the second post-poll clock access, at the final
            // publication check, without a mapper callback.
            public void ArmFinalClock(){Provider.Hook=()=>{Clock.Skip=1;Clock.Armed=true;};}
            public void Dispose(){if(Reference!=null)Reference.Dispose();if(Fenced!=null)Fenced.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("practice final clock callback cannot publish stale candidate",()=>{
                var errors=new List<string>();
                foreach(bool fenced in new[]{false,true})foreach(bool clockId in new[]{false,true})using(var f=new Fixture(fenced,clockId))
                {
                    f.Start();f.Clock.Time=10.31;f.Provider.Next=Poll(10.3);f.ArmFinalClock();VivePracticeEyeCandidate candidate;
                    if(f.Read(out candidate)||candidate!=null)errors.Add((fenced?"fenced":"reference")+"/"+(clockId?"ClockId":"Now"));
                    Check.True(f.Clock.Time==10.7&&!f.Clock.Armed,"Final clock callback must have run");
                }
                Check.True(errors.Count==0,"Stale frame published: "+String.Join(", ",errors.ToArray()));
            });
            suite.Run("practice final clock callback cannot assert stale readiness",()=>{
                var errors=new List<string>();
                foreach(bool fenced in new[]{false,true})foreach(bool clockId in new[]{false,true})using(var f=new Fixture(fenced,clockId))
                {
                    f.Clock.Time=10.11;f.Provider.Next=Poll(10.1);f.ArmFinalClock();
                    if(f.Ready())errors.Add((fenced?"fenced":"reference")+"/"+(clockId?"ClockId":"Now"));
                    Check.True(f.Clock.Time==10.7&&!f.Clock.Armed,"Final clock callback must have run");
                }
                Check.True(errors.Count==0,"Stale readiness: "+String.Join(", ",errors.ToArray()));
            });
        }
    }
}
