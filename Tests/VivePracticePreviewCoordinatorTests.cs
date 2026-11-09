using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Entirely synthetic; no provider, native runtime, files or headset.
    public static class VivePracticePreviewCoordinatorTests
    {
        sealed class Clock:IClock
        {public double Time=10;public string ClockId {get{return "preview-test-clock";}}public double Now {get{return Time;}}
        }
        sealed class Fixture
        {
            public readonly Clock Clock=new Clock();
            public readonly ViveDevelopmentParticipant Task;
            public readonly VivePracticePreviewCoordinator Preview;
            public Fixture()
            {
                var allocation=new Allocation("preview-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var config=new SessionConfiguration("preview-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"preview-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Preview=new VivePracticePreviewCoordinator(Task,Clock,.2);
            }
            public void Start()
            {
                Check.True(Preview.ConfirmEye());Clock.Time=10.1;Check.True(Preview.ConfirmTask());
                Clock.Time=10.2;Task.OwnerBeginPractice(Actor.Owner);Check.True(Preview.OnPracticeStarted());
            }
            public ViveProEyePoll Frame(int sequence,double eyeTime,bool eyeValid=true,bool head=true)
            {
                var ray=new ViveEyeRay(eyeValid?2UL:0UL,true,eyeValid,new Vec3(0,0,0),new Vec3(0,0,1));
                var eye=new ViveEyeFrame(sequence,sequence*10,eyeTime,false,ray,ray,ray);
                var h=head?new ViveHeadFrame(sequence,eyeTime,true,true,Rotation.Identity):null;
                return new ViveProEyePoll(h,eye,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("practice preview requires two choices and observed first start",()=>{
                var f=new Fixture();Check.True(!f.Preview.CanObserve&&!f.Preview.IsActive&&f.Preview.CanChoose);
                Check.True(f.Preview.ConfirmEye()&&!f.Preview.IsActive);f.Clock.Time=10.1;Check.True(f.Preview.ConfirmTask()&&f.Preview.IsActive);
                f.Clock.Time=10.2;f.Task.OwnerBeginPractice(Actor.Owner);Check.True(f.Preview.OnPracticeStarted());
                Check.True(f.Preview.CanObserve&&!f.Preview.CanChoose&&!f.Preview.OnPracticeStarted());
                Check.True(!f.Preview.SavesData&&!f.Preview.ResearchAdmission&&!f.Preview.CoverageProven&&!f.Preview.SourceAuthenticated);
            });
            suite.Run("practice preview display reflects choice revocation before start",()=>{
                var f=new Fixture();Check.True(f.Preview.ConfirmEye()&&f.Preview.ConfirmTask());
                Check.True(f.Preview.Status.Contains("Choices confirmed"));
                var field=typeof(VivePracticePreviewCoordinator).GetField("choices",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var authority=(VivePracticeConsentAuthority)field.GetValue(f.Preview);authority.Withdraw();
                Check.True(!f.Preview.IsActive&&f.Preview.Status.Contains("withdrawn"));
                f.Task.OwnerBeginPractice(Actor.Owner);Check.True(!f.Preview.OnPracticeStarted()&&!f.Preview.CanObserve);
            });
            suite.Run("practice preview counts fresh same-frame statuses without payload retention",()=>{
                var f=new Fixture();f.Start();f.Clock.Time=10.25;
                var first=f.Frame(1,10.24);Check.True(f.Preview.ObserveFrame(first));Check.Equal(1L,f.Preview.AcceptedFrameCount);Check.Equal(1L,f.Preview.HeadValidFrameCount);
                Check.True(!f.Preview.ObserveFrame(first)&&f.Preview.CanObserve);Check.Equal(1L,f.Preview.AcceptedFrameCount);
                f.Clock.Time=10.3;Check.True(f.Preview.ObserveFrame(f.Frame(2,10.29)));
                Check.Equal(2L,f.Preview.AcceptedFrameCount);Check.Equal(2L,f.Preview.HeadValidFrameCount);
            });
            suite.Run("practice preview waits for first frame newer than both choices and start",()=>{
                var f=new Fixture();f.Start();f.Clock.Time=10.21;
                Check.True(!f.Preview.ObserveFrame(f.Frame(1,10.19))&&f.Preview.CanObserve);
                Check.Equal(0L,f.Preview.AcceptedFrameCount);f.Clock.Time=10.23;
                Check.True(f.Preview.ObserveFrame(f.Frame(2,10.22)));Check.Equal(1L,f.Preview.AcceptedFrameCount);
            });
            suite.Run("old boundary frame cannot hide current runtime or head loss",()=>{
                var a=new Fixture();a.Start();a.Clock.Time=10.21;
                Check.True(!a.Preview.ObserveFrame(a.Frame(1,10.19,true,false))&&a.Preview.CanResumeObservation);
                Check.True(a.Preview.Status.Contains("Head validity"));Check.Equal(0L,a.Preview.AcceptedFrameCount);
                Check.True(a.Preview.ResumeObservation(),a.Preview.Status);Check.Equal(1,a.Preview.UnknownIntervals);
                var b=new Fixture();b.Start();b.Clock.Time=10.21;var old=b.Frame(1,10.19);
                var failed=new ViveProEyePoll(old.Head,old.Eye,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,false,0,2);
                Check.True(!b.Preview.ObserveFrame(failed)&&b.Preview.CanResumeObservation);
                Check.True(b.Preview.Status.Contains("Eye validity"));Check.Equal(0L,b.Preview.AcceptedFrameCount);
            });
            suite.Run("practice preview stops on missing invalid stale and reordered frames",()=>{
                var a=new Fixture();a.Start();Check.True(!a.Preview.ObserveFrame(null)&&!a.Preview.CanObserve);
                var b=new Fixture();b.Start();b.Clock.Time=10.25;Check.True(!b.Preview.ObserveFrame(b.Frame(1,10.24,false))&&!b.Preview.CanObserve);
                var c=new Fixture();c.Start();c.Clock.Time=11;Check.True(!c.Preview.ObserveFrame(c.Frame(1,10.21))&&!c.Preview.CanObserve);
                var d=new Fixture();d.Start();d.Clock.Time=10.25;Check.True(d.Preview.ObserveFrame(d.Frame(2,10.24)));
                d.Clock.Time=10.26;Check.True(!d.Preview.ObserveFrame(d.Frame(1,10.25))&&!d.Preview.CanObserve);
                var e=new Fixture();e.Start();e.Clock.Time=10.25;Check.True(!e.Preview.ObserveFrame(e.Frame(1,10.24,true,false))&&!e.Preview.CanObserve);
            });
            suite.Run("repeated eye frame cannot hide current head or eye runtime loss",()=>{
                var a=new Fixture();a.Start();a.Clock.Time=10.25;Check.True(a.Preview.ObserveFrame(a.Frame(1,10.24)));
                a.Clock.Time=10.26;Check.True(!a.Preview.ObserveFrame(a.Frame(1,10.24,true,false))&&a.Preview.CanResumeObservation);
                Check.True(a.Preview.Status.Contains("Head validity"));Check.Equal(1L,a.Preview.AcceptedFrameCount);
                var b=new Fixture();b.Start();b.Clock.Time=10.25;Check.True(b.Preview.ObserveFrame(b.Frame(1,10.24)));
                b.Clock.Time=10.26;Check.True(!b.Preview.ObserveFrame(b.Frame(1,10.24,false))&&b.Preview.CanResumeObservation);
                Check.True(b.Preview.Status.Contains("Eye validity"));Check.Equal(1L,b.Preview.AcceptedFrameCount);
            });
            suite.Run("practice preview withdrawal and foreign thread stop observation",()=>{
                var a=new Fixture();a.Start();a.Clock.Time=10.25;Check.True(a.Preview.ObserveFrame(a.Frame(1,10.24)));a.Preview.Withdraw();
                Check.Equal(0L,a.Preview.AcceptedFrameCount);Check.Equal(0L,a.Preview.HeadValidFrameCount);
                Check.True(!a.Preview.ObserveFrame(a.Frame(1,10.24))&&!a.Preview.CanObserve&&!a.Preview.CanChoose);
                var b=new Fixture();b.Start();bool result=true;var t=new Thread(()=>result=b.Preview.ObserveFrame(null));
                t.Start();Check.True(t.Join(5000));Check.True(!result&&!b.Preview.CanObserve);
            });
            suite.Run("practice status publication fails closed on concurrent participant mutation",()=>{
                foreach(int mode in new[]{0,1,2,3,4})
                {
                    var f=new Fixture();
                    if(mode==0)
                    {Check.True(f.Preview.ConfirmEye()&&f.Preview.ConfirmTask());f.Clock.Time=10.2;f.Task.OwnerBeginPractice(Actor.Owner);}
                    else
                    {
                        f.Start();f.Clock.Time=10.25;Check.True(f.Preview.ObserveFrame(f.Frame(1,10.24)));
                        if(mode==1){f.Preview.SuspendUnavailable();f.Clock.Time=10.26;}
                    }
                    long before=f.Preview.AcceptedFrameCount;bool invoked=false;
                    f.Preview.BeforeStatusCommitForTest=()=>{
                        invoked=true;var thread=new Thread(()=>{try{f.Task.ParticipantPause();}catch(Exception){}});
                        thread.Start();Check.True(thread.Join(5000));
                    };
                    bool result;
                    if(mode==0)result=f.Preview.OnPracticeStarted();
                    else if(mode==1)result=f.Preview.ResumeObservation();
                    else if(mode==2){f.Clock.Time=10.26;result=f.Preview.ObserveFrame(f.Frame(2,10.25));}
                    else if(mode==3){f.Clock.Time=10.26;result=f.Preview.ObserveFrame(f.Frame(1,10.24));}
                    else
                    {
                        // New start with no accepted frame exercises the
                        // pre-epoch waiting publication separately.
                        var x=new Fixture();x.Start();x.Clock.Time=10.21;
                        x.Preview.BeforeStatusCommitForTest=()=>{invoked=true;var thread=new Thread(()=>{try{x.Task.ParticipantPause();}catch(Exception){}});thread.Start();Check.True(thread.Join(5000));};
                        result=x.Preview.ObserveFrame(x.Frame(1,10.19));
                        Check.True(!result&&!x.Preview.CanObserve);Check.Equal(0L,x.Preview.AcceptedFrameCount);continue;
                    }
                    Check.True(invoked&&!result&&!f.Preview.CanObserve);Check.Equal(before,f.Preview.AcceptedFrameCount);
                }
            });
            suite.Run("practice status publication honours concurrent preview withdrawal",()=>{
                var f=new Fixture();f.Start();f.Clock.Time=10.25;Check.True(f.Preview.ObserveFrame(f.Frame(1,10.24)));
                f.Preview.BeforeStatusCommitForTest=()=>{var thread=new Thread(f.Preview.Withdraw);thread.Start();Check.True(thread.Join(5000));};
                f.Clock.Time=10.26;Check.True(!f.Preview.ObserveFrame(f.Frame(2,10.25))&&!f.Preview.CanObserve);
                Check.Equal(0L,f.Preview.AcceptedFrameCount);Check.Equal(0,f.Preview.UnknownIntervals);Check.True(f.Preview.Status.Contains("withdrawn"));
            });
            suite.Run("practice status publication honours concurrent participant withdrawal",()=>{
                var f=new Fixture();f.Start();f.Clock.Time=10.25;
                f.Preview.BeforeStatusCommitForTest=()=>{var thread=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(Exception){}});
                    thread.Start();Check.True(thread.Join(5000));};
                Check.True(!f.Preview.ObserveFrame(f.Frame(1,10.24))&&!f.Preview.CanObserve);Check.Equal(0L,f.Preview.AcceptedFrameCount);
            });
            suite.Run("practice status publication rejects withdrawn choices and fence replay",()=>{
                var a=new Fixture();a.Start();a.Clock.Time=10.25;
                var choiceField=typeof(VivePracticePreviewCoordinator).GetField("choices",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var pendingField=typeof(VivePracticePreviewCoordinator).GetField("pending",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                Check.True(choiceField!=null&&pendingField!=null);
                var authority=(VivePracticeConsentAuthority)choiceField.GetValue(a.Preview);
                a.Preview.BeforeStatusCommitForTest=()=>{var thread=new Thread(authority.Withdraw);thread.Start();Check.True(thread.Join(5000));};
                Check.True(!a.Preview.ObserveFrame(a.Frame(1,10.24))&&!a.Preview.CanObserve);Check.Equal(0L,a.Preview.AcceptedFrameCount);
                var b=new Fixture();b.Start();b.Clock.Time=10.25;ViveDevelopmentParticipant.PracticeObservationFence captured=null;
                var bAuthority=(VivePracticeConsentAuthority)choiceField.GetValue(b.Preview);
                b.Preview.BeforeStatusCommitForTest=()=>{var item=pendingField.GetValue(b.Preview);
                    var field=item.GetType().GetField("Fence",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                    captured=(ViveDevelopmentParticipant.PracticeObservationFence)field.GetValue(item);};
                Check.True(b.Preview.ObserveFrame(b.Frame(1,10.24)));Check.True(captured!=null&&captured.Consumed&&captured.PreviewUsed);
                Check.True(!bAuthority.TryCommitPracticePreview(b.Preview,captured,bAuthority.Generation));Check.Equal(1L,b.Preview.AcceptedFrameCount);
                b.Preview.Withdraw();Check.Equal(0L,b.Preview.AcceptedFrameCount);
            });
            suite.Run("practice preview terminates observation when a choice is missing",()=>{
                var f=new Fixture();Check.True(f.Preview.ConfirmEye());f.Clock.Time=10.2;f.Task.OwnerBeginPractice(Actor.Owner);
                Check.True(!f.Preview.OnPracticeStarted()&&!f.Preview.IsActive&&!f.Preview.CanObserve);
                Check.True(f.Preview.Status.Contains("both choices"));Check.Equal(Phase.Practice,f.Task.Phase);
            });
            suite.Run("practice preview detects pause even if practice later resumes",()=>{
                var f=new Fixture();f.Start();f.Task.OwnerPause(Actor.Owner);
                f.Clock.Time=10.25;Check.True(!f.Preview.ObserveFrame(f.Frame(1,10.24))&&!f.Preview.CanObserve);
            });
            suite.Run("practice preview explicitly resumes after an observed pause with unknown gap",()=>{
                var f=new Fixture();f.Start();f.Clock.Time=10.25;Check.True(f.Preview.ObserveFrame(f.Frame(1,10.24)));
                f.Clock.Time=10.26;f.Task.ParticipantPause();Check.True(!f.Preview.ObserveFrame(null)&&f.Preview.CanResumeObservation);
                Check.True(!f.Preview.ResumeObservation()&&f.Preview.CanResumeObservation);
                f.Clock.Time=10.27;f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);
                Check.Equal(Phase.Practice,f.Task.Phase);f.Clock.Time=10.28;
                Check.True(f.Preview.ResumeObservation(),f.Preview.Status);Check.Equal(1,f.Preview.UnknownIntervals);
                Check.True(!f.Preview.ObserveFrame(f.Frame(1,10.24))&&f.Preview.CanObserve);
                f.Clock.Time=10.30;Check.True(f.Preview.ObserveFrame(f.Frame(2,10.29)));
                Check.Equal(2L,f.Preview.AcceptedFrameCount);Check.True(!f.Preview.ResumeObservation());
            });
            suite.Run("practice preview detects pause resume between frames before explicit observation recovery",()=>{
                var f=new Fixture();f.Start();f.Clock.Time=10.25;Check.True(f.Preview.ObserveFrame(f.Frame(1,10.24)));
                f.Clock.Time=10.26;f.Task.ParticipantPause();f.Clock.Time=10.27;
                f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);
                Check.Equal(Phase.Practice,f.Task.Phase);f.Clock.Time=10.28;
                Check.True(!f.Preview.ObserveFrame(f.Frame(2,10.27))&&f.Preview.CanResumeObservation);
                Check.True(f.Preview.ResumeObservation(),f.Preview.Status);Check.Equal(1,f.Preview.UnknownIntervals);
            });
            suite.Run("practice preview source loss requires explicit new epoch and does not fabricate continuity",()=>{
                var f=new Fixture();f.Start();f.Clock.Time=10.25;Check.True(f.Preview.ObserveFrame(f.Frame(1,10.24)));
                f.Clock.Time=10.26;Check.True(!f.Preview.ObserveFrame(null)&&f.Preview.CanResumeObservation);
                Check.Equal(Phase.Practice,f.Task.Phase);f.Clock.Time=10.30;
                Check.True(f.Preview.ResumeObservation(),f.Preview.Status);Check.Equal(1,f.Preview.UnknownIntervals);
                Check.True(!f.Preview.ObserveFrame(f.Frame(1,10.24))&&f.Preview.CanObserve);
                f.Clock.Time=10.32;Check.True(f.Preview.ObserveFrame(f.Frame(2,10.31)));Check.Equal(2L,f.Preview.AcceptedFrameCount);
                f.Preview.SuspendUnavailable();Check.True(f.Preview.CanResumeObservation);f.Preview.Withdraw();
                Check.True(!f.Preview.CanResumeObservation&&!f.Preview.ResumeObservation());Check.Equal(0L,f.Preview.AcceptedFrameCount);Check.Equal(0,f.Preview.UnknownIntervals);
            });
            suite.Run("practice preview marks a persistently old pre-start frame unavailable",()=>{
                var f=new Fixture();f.Start();f.Clock.Time=11;
                Check.True(!f.Preview.ObserveFrame(f.Frame(1,10.19))&&f.Preview.CanResumeObservation);
                Check.Equal(0L,f.Preview.AcceptedFrameCount);Check.Equal(0,f.Preview.UnknownIntervals);
                Check.True(f.Preview.ResumeObservation(),f.Preview.Status);Check.Equal(1,f.Preview.UnknownIntervals);
            });
            suite.Run("practice preview cannot observe a second attempt without new evidence",()=>{
                var f=new Fixture();f.Start();
                for(int i=0;i<8;i++)
                {
                    f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));f.Clock.Time+=1;
                    f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
                    var ink=f.Task.CurrentTrial.Stimulus.Ink;
                    var press=ink==Colour.Red?new VivePreviewTrackpadSample(true,true,1,0):new VivePreviewTrackpadSample(true,true,0,1);
                    f.Task.UpdateTrackpad(true,press);Check.Equal(Phase.PracticeCorrection,f.Task.Phase);
                    f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);
                }
                Check.Equal(Phase.PracticeRetry,f.Task.Phase);
                Check.True(!f.Preview.ObserveFrame(f.Frame(10,f.Clock.Time))&&!f.Preview.CanObserve);
            });
            suite.Run("practice preview opens a separate unknown-gap epoch after verified retry",()=>{
                var f=new Fixture();f.Start();f.Clock.Time=10.25;int sequence=1;Check.True(f.Preview.ObserveFrame(f.Frame(sequence,10.24)));
                for(int i=0;i<8;i++)
                {
                    f.Clock.Time+=.02;f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                    f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);var ink=f.Task.CurrentTrial.Stimulus.Ink;
                    var press=ink==Colour.Red?new VivePreviewTrackpadSample(true,true,1,0):new VivePreviewTrackpadSample(true,true,0,1);
                    f.Task.UpdateTrackpad(true,press);Check.Equal(Phase.PracticeCorrection,f.Task.Phase);
                    f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);
                    if(i<7){f.Clock.Time+=.01;sequence++;Check.True(f.Preview.ObserveFrame(f.Frame(sequence,f.Clock.Time-.001)),f.Preview.Status);}
                }
                Check.Equal(Phase.PracticeRetry,f.Task.Phase);Check.True(!f.Preview.ObserveFrame(null)&&f.Preview.CanResumeObservation);
                f.Clock.Time+=.02;f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.OwnerBeginPractice(Actor.Owner);Check.Equal(Phase.Practice,f.Task.Phase);
                f.Clock.Time+=.01;Check.True(f.Preview.ResumeObservation(),f.Preview.Status);Check.Equal(1,f.Preview.UnknownIntervals);
                f.Clock.Time+=.01;sequence++;Check.True(f.Preview.ObserveFrame(f.Frame(sequence,f.Clock.Time-.001)),f.Preview.Status);
            });
            suite.Run("practice retry entirely between frames still requires verified explicit recovery",()=>{
                var f=new Fixture();f.Start();f.Clock.Time=10.25;int sequence=1;Check.True(f.Preview.ObserveFrame(f.Frame(sequence,10.24)));
                for(int i=0;i<8;i++)
                {
                    f.Clock.Time+=.02;f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                    f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);var ink=f.Task.CurrentTrial.Stimulus.Ink;
                    var press=ink==Colour.Red?new VivePreviewTrackpadSample(true,true,1,0):new VivePreviewTrackpadSample(true,true,0,1);
                    f.Task.UpdateTrackpad(true,press);f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);
                    if(i<7){f.Clock.Time+=.01;sequence++;Check.True(f.Preview.ObserveFrame(f.Frame(sequence,f.Clock.Time-.001)),f.Preview.Status);}
                }
                Check.Equal(Phase.PracticeRetry,f.Task.Phase);f.Clock.Time+=.02;
                f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.OwnerBeginPractice(Actor.Owner);Check.Equal(Phase.Practice,f.Task.Phase);
                f.Clock.Time+=.01;sequence++;Check.True(!f.Preview.ObserveFrame(f.Frame(sequence,f.Clock.Time-.001))&&f.Preview.CanResumeObservation);
                Check.True(f.Preview.ResumeObservation(),f.Preview.Status);Check.Equal(1,f.Preview.UnknownIntervals);
                f.Clock.Time+=.01;sequence++;Check.True(f.Preview.ObserveFrame(f.Frame(sequence,f.Clock.Time-.001)),f.Preview.Status);
            });
            suite.Run("practice preview rejects missed start without restarting the task",()=>{
                var f=new Fixture();Check.True(f.Preview.ConfirmEye()&&f.Preview.ConfirmTask());f.Clock.Time=10.2;
                f.Task.OwnerBeginPractice(Actor.Owner);f.Clock.Time=10.3;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
                Check.True(!f.Preview.OnPracticeStarted()&&!f.Preview.CanObserve);Check.Equal(Phase.Practice,f.Task.Phase);
            });
        }
    }
}
