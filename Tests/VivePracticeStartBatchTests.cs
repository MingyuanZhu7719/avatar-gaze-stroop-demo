using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Real in-memory Simulation participant; no headset, file, network or consent.
    public static class VivePracticeStartBatchTests
    {
        sealed class Clock:IClock
        {
            public double Time=10;
            public Action IdHook;
            public string ClockId {get{var action=IdHook;IdHook=null;if(action!=null)action();return "batch-synthetic-clock";}}
            public double Now {get{return Time;}}
        }
        static ViveDevelopmentParticipant New(Clock clock,bool observed=true,int traceLimit=4096)
        {
            var config=new SessionConfiguration("batch-synthetic-session",42,
                new Allocation("batch-fixture",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"batch-synthetic-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            if(!observed)return new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
            return new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),traceLimit);
        }
        static ViveDevelopmentParticipant Ready(Clock clock,bool observed=true)
        {var task=New(clock,observed);task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));return task;}
        static ViveDevelopmentParticipant Started(Clock clock)
        {var task=Ready(clock);clock.Time=11;task.OwnerBeginPractice(Actor.Owner);return task;}
        static bool Extract(ViveDevelopmentParticipant task,IClock clock,int attempt,int cap,out VivePracticeStartBatch batch)
        {return VivePracticeStartBatchExtractor.TryExtract(task,clock,attempt,cap,out batch);}
        public static void Register(TestSuite suite)
        {
            suite.Run("practice start batch preserves actual same transaction event values at one tick",()=>{
                var clock=new Clock();var task=Started(clock);VivePracticeStartBatch batch;
                Check.True(Extract(task,clock,1,4096,out batch));Check.True(batch!=null&&!batch.CanWrite);
                Check.Equal(task.SessionId,batch.SessionId);Check.Equal(clock.ClockId,batch.ClockId);Check.Equal(1,batch.Attempt);
                Check.True(batch.Revision>0);Check.Equal(3,batch.Events.Count);
                Check.Equal("practice_started",batch.Events[0].Kind);Check.Equal("phase_changed",batch.Events[1].Kind);
                Check.Equal("trial_planned",batch.Events[2].Kind);
                Check.Equal(batch.Events[0].Sequence+1,batch.Events[1].Sequence);Check.Equal(batch.Events[1].Sequence+1,batch.Events[2].Sequence);
                Check.Equal(11.0,batch.Events[0].Time);Check.Equal(11.0,batch.Events[1].Time);Check.Equal(11.0,batch.Events[2].Time);
                Check.Equal("1",batch.Events[0].Fields["attempt"]);Check.Equal("Familiarization",batch.Events[1].Fields["from"]);
                Check.Equal("Practice",batch.Events[1].Fields["to"]);Check.Equal(task.CurrentTrial.TrialId,batch.Events[2].TrialId);
                var source=task.ReadEventsAfter(batch.Events[0].Sequence-1,3);Check.Equal(3,source.Count);
                for(int i=0;i<3;i++)
                {
                    Check.True(!Object.ReferenceEquals(source[i],batch.Events[i]),"batch owns copied event");
                    Check.Equal(source[i].Sequence,batch.Events[i].Sequence);Check.Equal(source[i].Time,batch.Events[i].Time);
                    Check.Equal(source[i].SessionId,batch.Events[i].SessionId);Check.Equal(source[i].ClockId,batch.Events[i].ClockId);
                    Check.Equal(source[i].BlockIndex,batch.Events[i].BlockIndex);Check.Equal(source[i].Segment,batch.Events[i].Segment);
                    Check.Equal(source[i].TrialId,batch.Events[i].TrialId);Check.Equal(source[i].Fields.Count,batch.Events[i].Fields.Count);
                    foreach(var field in source[i].Fields)Check.Equal(field.Value,batch.Events[i].Fields[field.Key]);
                }
            });
            suite.Run("practice start batch rejects wrong attempt, bounded trace and missing observation",()=>{
                var clock=new Clock();var task=Started(clock);VivePracticeStartBatch batch;
                Check.True(!Extract(task,clock,2,4096,out batch)&&batch==null);
                Check.True(!Extract(task,clock,0,4096,out batch)&&batch==null);
                Check.True(!Extract(task,clock,1,1,out batch)&&batch==null);
                Check.True(!Extract(task,clock,1,0,out batch)&&batch==null);
                var plainClock=new Clock();var plain=Ready(plainClock,false);plain.OwnerBeginPractice(Actor.Owner);
                Check.True(!Extract(plain,plainClock,1,4096,out batch)&&batch==null);
            });
            suite.Run("practice start batch rejects another clock even with same clock label",()=>{
                var clock=new Clock();var task=Started(clock);VivePracticeStartBatch batch;
                var other=new Clock();Check.Equal(clock.ClockId,other.ClockId);
                Check.True(!Extract(task,other,1,4096,out batch)&&batch==null);
            });
            suite.Run("practice start batch rejects changed phase and later same-phase transaction",()=>{
                var aClock=new Clock();var a=Started(aClock);a.ParticipantPause();VivePracticeStartBatch batch;
                Check.True(!Extract(a,aClock,1,4096,out batch)&&batch==null);
                var bClock=new Clock();var b=Started(bClock);b.MarkPresented(b.CurrentTrial.TrialId);
                Check.Equal(Phase.Practice,b.Phase);
                Check.True(!Extract(b,bClock,1,4096,out batch)&&batch==null);
            });
            suite.Run("practice start batch fails closed on reentrant task mutation during clock identity read",()=>{
                var clock=new Clock();var task=Started(clock);bool invoked=false;
                clock.IdHook=()=>{invoked=true;task.ParticipantPause();};VivePracticeStartBatch batch;
                Check.True(!Extract(task,clock,1,4096,out batch)&&batch==null);
                Check.True(invoked);Check.Equal(Phase.Paused,task.Phase);
            });
        }
    }
}
