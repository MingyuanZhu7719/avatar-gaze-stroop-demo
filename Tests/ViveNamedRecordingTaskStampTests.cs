using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveNamedRecordingTaskStampTests
    {
        sealed class Clock:IClock
        {public double Time=0;public Action Hook;public string ClockId {get{return "task-stamp-clock";}}
            public double Now {get{var h=Hook;Hook=null;if(h!=null)h();return Time;}}}
        static ViveDevelopmentParticipant New(Clock clock,bool requested=true)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"stamp-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"stamp-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"stamp-intent",requested,requested);
            var config=SessionConfiguration.NamedRecordingDevelopment("stamp-session",37,plan,intent);
            var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"stamp-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
        }
        static bool Current(ViveDevelopmentParticipant p,ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp)
        {lock(p.NamedRecordingGate)return p.NamedRecordingStampCurrentUnderLock(stamp);}
        static ViveDevelopmentParticipant.NamedRecordingTaskStamp Capture(ViveDevelopmentParticipant p,Clock c)
        {ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(p.TryCaptureNamedRecordingTaskStamp(c,out stamp));return stamp;}
        public static void Register(TestSuite suite)
        {
            suite.Run("named v3 task stamp binds participant plan intent clock and action generation",()=>{
                var c=new Clock();var p=New(c);var initial=Capture(p,c);Check.True(Current(p,initial));
                Check.True(Object.ReferenceEquals(p,initial.Owner)&&Object.ReferenceEquals(c,initial.Clock)&&Object.ReferenceEquals(p.NamedPlan,initial.Plan));
                Check.Equal(Phase.Preparation,initial.Phase);Check.Equal(-1,initial.Block);Check.Equal<string>(null,initial.TrialId);
                p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Check.True(!Current(p,initial));
                var before=Capture(p,c);p.OwnerBeginPractice(Actor.Owner);Check.True(!Current(p,before));
                var started=Capture(p,c);Check.Equal(Phase.Practice,started.Phase);Check.True(started.TrialId!=null&&started.EventCursor>before.EventCursor);
                p.MarkPresented(p.CurrentTrial.TrialId);Check.True(!Current(p,started));var presented=Capture(p,c);Check.True(presented.EventCursor>started.EventCursor);
                p.ParticipantPause();Check.True(!Current(p,presented));Check.Equal(Phase.Paused,Capture(p,c).Phase);
                var other=new Clock();ViveDevelopmentParticipant.NamedRecordingTaskStamp ignored;
                Check.True(!p.TryCaptureNamedRecordingTaskStamp(other,out ignored));
            });
            suite.Run("named v3 task stamp invalidates on auxiliary tick and neutral state",()=>{
                var c=new Clock();var p=New(c);var a=Capture(p,c);p.UpdateNeutralPending(true);Check.True(!Current(p,a));
                p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));var b=Capture(p,c);p.UpdateAuxiliary(false,false,false,0,true);Check.True(!Current(p,b));
                var d=Capture(p,c);p.Tick();Check.True(!Current(p,d));
            });
            suite.Run("named v3 task stamp fails closed on reentry and foreign mutation",()=>{
                var c=new Clock();var p=New(c);p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                c.Hook=()=>{try{p.Tick();}catch(InvalidOperationException){}};
                p.OwnerBeginPractice(Actor.Owner);ViveDevelopmentParticipant.NamedRecordingTaskStamp ignored;
                Check.True(!p.TryCaptureNamedRecordingTaskStamp(c,out ignored));
                Check.True(p.NamedRecordingRequested&&p.NamedRecordingAuthorityFaulted);Check.Throws<InvalidOperationException>(()=>p.Tick());
                p.ParticipantPause();Check.Equal(Phase.Paused,p.Phase);p.ParticipantWithdraw();Check.Equal((EndReason?)EndReason.Withdrawn,p.EndReason);
                var other=New(new Clock());Exception error=null;var thread=new Thread(()=>{try{other.Tick();}catch(Exception e){error=e;}});
                thread.Start();Check.True(thread.Join(3000));Check.True(error is InvalidOperationException);
            });
            suite.Run("named v3 defers reentrant withdraw until task action reaches safe point",()=>{
                var c=new Clock();var p=New(c);p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                c.Hook=()=>p.ParticipantWithdraw();p.OwnerBeginPractice(Actor.Owner);
                Check.Equal(Phase.Ended,p.Phase);Check.Equal((EndReason?)EndReason.Withdrawn,p.EndReason);
                var field=typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                foreach(var e in ((Experiment)field.GetValue(p)).Events)Check.True(e.Kind!="practice_started");
                ViveDevelopmentParticipant.NamedRecordingTaskStamp ignored;Check.True(!p.TryCaptureNamedRecordingTaskStamp(c,out ignored));
            });
            suite.Run("named v3 defers reentrant participant pause until current action completes",()=>{
                var c=new Clock();var p=New(c);p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));p.OwnerBeginPractice(Actor.Owner);
                c.Hook=()=>p.ParticipantPause();p.MarkPresented(p.CurrentTrial.TrialId);Check.Equal(Phase.Paused,p.Phase);
                ViveDevelopmentParticipant.NamedRecordingTaskStamp ignored;Check.True(!p.TryCaptureNamedRecordingTaskStamp(c,out ignored));
            });
            suite.Run("named v3 defers reentrant technical end without allowing later ordinary action",()=>{
                var c=new Clock();var p=New(c);p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));p.OwnerBeginPractice(Actor.Owner);
                c.Hook=()=>p.EndTechnical(Actor.Owner,"synthetic technical stop");p.MarkPresented(p.CurrentTrial.TrialId);
                Check.Equal(Phase.Ended,p.Phase);Check.Equal((EndReason?)EndReason.TechnicalEnd,p.EndReason);Check.True(p.NamedRecordingAuthorityFaulted);
                Check.Throws<InvalidOperationException>(()=>p.Tick());
            });
            suite.Run("named v3 generation exhaustion cannot suppress participant withdrawal",()=>{
                var c=new Clock();var p=New(c);p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));p.OwnerBeginPractice(Actor.Owner);
                var field=typeof(ViveDevelopmentParticipant).GetField("namedRecordingMutationGeneration",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                field.SetValue(p,Int64.MaxValue);p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);
                Check.Equal((EndReason?)EndReason.Withdrawn,p.EndReason);Check.True(p.NamedRecordingAuthorityFaulted);
            });
            suite.Run("named v3 foreign withdrawal is revoked immediately and applied before another owner action",()=>{
                var c=new Clock();var p=New(c);p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));p.OwnerBeginPractice(Actor.Owner);
                var thread=new Thread(p.ParticipantWithdraw);thread.Start();Check.True(thread.Join(3000));
                ViveDevelopmentParticipant.NamedRecordingTaskStamp ignored;Check.True(!p.TryCaptureNamedRecordingTaskStamp(c,out ignored));
                Check.Throws<InvalidOperationException>(()=>p.MarkPresented(p.CurrentTrial.TrialId));
                Check.Equal(Phase.Ended,p.Phase);Check.Equal((EndReason?)EndReason.Withdrawn,p.EndReason);
            });
            suite.Run("named v3 blocked-path safety pump applies foreign withdrawal without menu edge",()=>{
                var c=new Clock();var p=New(c);p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));p.OwnerBeginPractice(Actor.Owner);
                var thread=new Thread(p.ParticipantWithdraw);thread.Start();Check.True(thread.Join(3000));Check.Equal(Phase.Practice,p.Phase);
                Check.True(p.NamedRecordingWithdrawalRequested&&p.NamedRecordingAuthorityFaulted);
                Check.True(p.PumpNamedRecordingSafety());Check.Equal(Phase.Ended,p.Phase);Check.Equal((EndReason?)EndReason.Withdrawn,p.EndReason);
            });
            suite.Run("named v3 blocked-path safety pump pauses on authority fault",()=>{
                var c=new Clock();var p=New(c);p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));p.OwnerBeginPractice(Actor.Owner);
                var thread=new Thread(()=>{try{p.Tick();}catch(InvalidOperationException){}});thread.Start();Check.True(thread.Join(3000));
                Check.True(p.NamedRecordingAuthorityFaulted);Check.Equal(Phase.Practice,p.Phase);
                Check.True(p.PumpNamedRecordingSafety());Check.Equal(Phase.Paused,p.Phase);
                Check.True(p.PumpNamedRecordingSafety());Check.Equal(Phase.Paused,p.Phase);
            });
            suite.Run("named v3 faulted auxiliary safety cannot smuggle confirmation or rating",()=>{
                var c=new Clock();var p=New(c);p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));p.OwnerBeginPractice(Actor.Owner);
                var thread=new Thread(()=>{try{p.Tick();}catch(InvalidOperationException){}});thread.Start();Check.True(thread.Join(3000));
                int trials=p.TrialCount,ratings=p.RatingCount;p.UpdateAuxiliary(true,true,false,1,true);
                Check.Equal(Phase.Paused,p.Phase);Check.Equal(trials,p.TrialCount);Check.Equal(ratings,p.RatingCount);
            });
            suite.Run("named v3 concurrent withdrawal before auxiliary reservation blocks compound input",()=>{
                var c=new Clock();var p=New(c);p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));p.OwnerBeginPractice(Actor.Owner);
                int trials=p.TrialCount,ratings=p.RatingCount;
                p.BeforeNamedAuxiliaryReservationForTest=()=>{
                    p.BeforeNamedAuxiliaryReservationForTest=null;var thread=new Thread(p.ParticipantWithdraw);thread.Start();Check.True(thread.Join(3000));};
                p.UpdateAuxiliary(true,true,false,1,true);
                Check.Equal(Phase.Ended,p.Phase);Check.Equal((EndReason?)EndReason.Withdrawn,p.EndReason);
                Check.Equal(trials,p.TrialCount);Check.Equal(ratings,p.RatingCount);
            });
            suite.Run("named v3 withdrawal signal at unready boundary still ends task",()=>{
                var c=new Clock();var p=New(c);p.UpdateAuxiliary(true,false,true,1,true);
                Check.True(p.NamedRecordingWithdrawalRequested&&p.NamedRecordingAuthorityFaulted);Check.Equal(Phase.Ended,p.Phase);
                Check.Equal((EndReason?)EndReason.Withdrawn,p.EndReason);Check.Throws<InvalidOperationException>(()=>p.Tick());
            });
            suite.Run("named v3 task stamp fails closed immediately after withdrawal signal",()=>{
                var c=new Clock();var p=New(c);var stamp=Capture(p,c);p.UpdateAuxiliary(false,false,true,0,true);
                Check.True(!Current(p,stamp));ViveDevelopmentParticipant.NamedRecordingTaskStamp ignored;
                Check.True(!p.TryCaptureNamedRecordingTaskStamp(c,out ignored));
            });
            suite.Run("v3 OFF task stamp is not recording or consent authority",()=>{
                var c=new Clock();var p=New(c,false);var stamp=Capture(p,c);
                Check.True(!stamp.Intent.TaskRequested&&!stamp.Intent.EyeRequested&&!p.RecordsTaskFiles&&!p.RecordsEyeSamples);
            });
        }
    }
}
