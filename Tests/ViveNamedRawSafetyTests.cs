using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveNamedRawSafetyTests
    {
        static ViveDevelopmentParticipant Task()
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"raw-safety-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"raw-safety-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"raw-safety-intent",true,true);
            var config=SessionConfiguration.NamedRecordingDevelopment("synthetic-raw-safety-session",42,plan,intent);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"raw-safety-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var task=new ViveDevelopmentParticipant(config,new MonotonicClock("raw-safety-clock"),geometry,map,.5,true);
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));task.OwnerBeginPractice(Actor.Owner);
            return task;
        }
        static void Hold(object gate,Action blocked)
        {
            using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            {
                var thread=new Thread(()=>{lock(gate){entered.Set();release.WaitOne(3000);}});thread.IsBackground=true;thread.Start();
                try{Check.True(entered.WaitOne(3000));blocked();}
                finally{release.Set();Check.True(thread.Join(3000));}
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("named raw safety preserves actual pause actor",()=>{
                foreach(var cause in new[]{ViveDevelopmentParticipant.NamedRawSafetyCause.OwnerPause,ViveDevelopmentParticipant.NamedRawSafetyCause.ParticipantPause})
                {
                    var task=Task();
                    Check.Equal(ViveDevelopmentParticipant.NamedRawSafetyResult.Applied,task.TryApplyNamedRawSafetyForCause(cause));
                    JournalEvent found=null;foreach(var ev in task.ReadEventsAfter(-1,4096))if(ev.Kind=="explicit_pause")found=ev;
                    Check.True(found!=null);Check.Equal(cause==ViveDevelopmentParticipant.NamedRawSafetyCause.OwnerPause?"Owner":"Participant",found.Fields["actor"]);
                }
            });
            suite.Run("named raw technical fault does not invent voluntary pause",()=>{
                var task=Task();int before=task.ReadEventsAfter(-1,4096).Count;
                Check.Equal(ViveDevelopmentParticipant.NamedRawSafetyResult.NotApplied,task.TryApplyNamedRawSafetyForCause(ViveDevelopmentParticipant.NamedRawSafetyCause.TechnicalFault));
                Check.Equal(Phase.Practice,task.Phase);Check.Equal(before,task.ReadEventsAfter(-1,4096).Count);
            });
            suite.Run("named raw task stamp detects same phase mutation",()=>{
                var task=Task();var clock=(IClock)typeof(ViveDevelopmentParticipant).GetField("eventClock",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(task);
                ViveDevelopmentParticipant.NamedRecordingTaskStamp before,after;
                Check.True(task.TryCaptureNamedRecordingTaskStamp(clock,out before));
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Check.True(task.TryCaptureNamedRecordingTaskStamp(clock,out after));
                Check.Equal(before.Phase,after.Phase);Check.True(before.Generation!=after.Generation);
            });
            suite.Run("named raw safety lock contention stays pending without task mutation",()=>{
                var task=Task();var trial=task.CurrentTrial;
                Hold(task.NamedRecordingGate,()=>{
                    var watch=Stopwatch.StartNew();var result=task.TryApplyNamedRawSafety(false);
                    Check.Equal(ViveDevelopmentParticipant.NamedRawSafetyResult.Pending,result);Check.True(watch.ElapsedMilliseconds<500);
                    Check.Equal(Phase.Practice,task.Phase);Check.True(Object.ReferenceEquals(trial,task.CurrentTrial));
                });
                Check.Equal(ViveDevelopmentParticipant.NamedRawSafetyResult.Applied,task.TryApplyNamedRawSafety(false));
                Check.Equal(Phase.Paused,task.Phase);Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
                Check.Equal(ViveDevelopmentParticipant.NamedRawSafetyResult.Applied,task.TryApplyNamedRawSafety(false));
            });
            suite.Run("named raw withdrawal waits without blocking on view lock then ends once",()=>{
                var task=Task();var trial=task.CurrentTrial;
                var field=typeof(ViveDevelopmentParticipant).GetField("namedViewGate",BindingFlags.Instance|BindingFlags.NonPublic);
                Check.True(field!=null);object gate=field.GetValue(task);
                Hold(gate,()=>{
                    var watch=Stopwatch.StartNew();var result=task.TryApplyNamedRawSafety(true);
                    Check.Equal(ViveDevelopmentParticipant.NamedRawSafetyResult.Pending,result);Check.True(watch.ElapsedMilliseconds<500);
                    Check.Equal(Phase.Practice,task.Phase);
                });
                Check.Equal(ViveDevelopmentParticipant.NamedRawSafetyResult.Applied,task.TryApplyNamedRawSafety(true));
                Check.Equal(Phase.Ended,task.Phase);Check.Equal(EndReason.Withdrawn,task.EndReason.Value);
                Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
                Check.Equal(ViveDevelopmentParticipant.NamedRawSafetyResult.Applied,task.TryApplyNamedRawSafety(true));
            });
            suite.Run("named raw task safety refuses a foreign owner thread",()=>{
                var task=Task();ViveDevelopmentParticipant.NamedRawSafetyResult result=ViveDevelopmentParticipant.NamedRawSafetyResult.Pending;
                var thread=new Thread(()=>result=task.TryApplyNamedRawSafety(true));thread.Start();Check.True(thread.Join(3000));
                Check.Equal(ViveDevelopmentParticipant.NamedRawSafetyResult.NotApplied,result);Check.Equal(Phase.Practice,task.Phase);
            });
        }
    }
}
