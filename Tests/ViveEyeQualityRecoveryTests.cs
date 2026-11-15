#if VIVE_SYNTHETIC_BATCH_FIXTURE
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static partial class ViveNamedRawRecordingPumpTests
    {
        // Real task/worker/pump/admission, synthetic hardware and stalled
        // memory writer. Stalling ACK makes the admitted record kinds visible
        // without converting an eye-absence marker into a fake sample.
        sealed class EyeQualityFixture : IDisposable
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("eye-quality-continuation");
            readonly SequencedFrameBoundaryClient client=new SequencedFrameBoundaryClient();
            internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveNamedSessionFrameAdmission Admission;
            internal readonly ViveNamedRawRecordingPump.SyntheticWriter Writer;
            internal readonly ViveNamedRawRecordingPump Pump;
            internal bool HeadValid=true;
            long headSeq;
            int nativeSeq;
            internal EyeQualityFixture(bool practice,bool frozenBeforeBaseline=false)
            {
                Worker=new ViveProEyeWorker(Clock,()=>new ViveHeadFrame(++headSeq,Clock.Now,true,HeadValid,Rotation.Identity),()=>client);
                Check.True(Worker.Start()&&client.Waiting.WaitOne(3000));
                if(frozenBeforeBaseline)Send();
                Task=NewTask(Clock);
                Admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(Task,Worker,Clock,256);
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());
                Check.True(Admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(33554432)));
                Writer=new ViveNamedRawRecordingPump.SyntheticWriter(Admission){Block=true};
                Pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(Task,Worker,Clock,Admission,Writer);
                if(practice)
                {
                    Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                    var e=(Experiment)typeof(ViveDevelopmentParticipant).GetField("experiment",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Task);
                    e.PrepareNamedRecordingBlockPlan(Actor.Owner,true);e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                }
                if(!frozenBeforeBaseline)client.Send(null); // Poll initiated before status authority.
            }
            internal void Send(bool invalid=false,int result=0,bool repeat=false,ViveEyeRuntimeState runtime=ViveEyeRuntimeState.Working)
            {
                int i=repeat?nativeSeq:++nativeSeq;
                var ray=new ViveEyeRay(invalid?0UL:2UL,false,!invalid,new Vec3(0,0,0),new Vec3(.6,0,.8));
                client.Send(new ViveProEyePoll(null,new ViveEyeFrame(i,i*8,Clock.Now,false,null,null,ray),
                    runtime,ViveEyeCalibrationState.ReportedReady,true,result,2));
            }
            internal void SendNoEye(){client.Send(null);}
            internal ViveProEyePoll Poll()
            {
                ViveProEyePoll frame=null;
                Check.True(SpinWait.SpinUntil(()=>Worker.TryPollForRecording(out frame),3000),"owner recording poll unavailable");
                return frame;
            }
            internal void Stage()
            {Check.True(Pump.StageRigFrame(Poll(),1)&&Pump.VerifyStagedEpoch(),"fresh head authority refused: "+Pump.FirstFault);}
            internal object[] Records
            {
                get
                {
                    Check.True(Writer.Entered.WaitOne(3000));
                    return (object[])typeof(ViveNamedSessionFrameAdmission).GetField("ring",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Admission);
                }
            }
            internal int EyeCount {get{return Records.OfType<ViveNamedSessionFrameAdmission.Frame>().Count();}}
            internal bool Has(ViveRawEyeObservation.Reason reason,ViveRawEyeObservation.Disposition disposition)
            {return Records.OfType<ViveRawEyeObservation>().Any(e=>e.State==disposition&&e.Cause==reason);}
            internal void StillRunning(Phase phase)
            {
                Check.Equal(phase,Task.Phase);
                Check.True(!Pump.TrackingGapOpen&&!Admission.IoFence.IsRevoked);
                Check.Equal(ViveNamedRawRecordingPump.Outcome.Running,Pump.State);
                Check.Equal(0,Task.ReadEventsAfter(-1,4096).Count(e=>e.Kind=="tracking_gap_begin"||e.Kind=="tracking_gap_end"||e.Kind=="trial_interrupted"));
            }
            internal void Witness(TrialRecord trial)
            {
                ViveNamedSessionFrameAdmission.PresentationReservation token;
                Check.True(Pump.TryPreparePresentation(42,1,out token),"no head-only presentation reservation: "+Pump.FirstFault);
                Check.True(Pump.CommitPresentation(token,42,1,trial.TrialId,trial.Stimulus.Word,trial.Stimulus.Ink,Clock.Now),"no software onset: "+Pump.FirstFault);
                Check.True(trial.Onset.HasValue);
            }
            internal void Respond(TrialRecord trial)
            {
                Colour c=trial.Stimulus.Ink;
                Check.True(Pump.TryRunReservedOrdinaryMutation(()=>Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0))),"no neutral control observation");
                Check.True(Pump.TryRunReservedOrdinaryMutation(()=>Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,
                    c==Colour.Green?1:c==Colour.Yellow?-1:0,c==Colour.Red?1:c==Colour.Blue?-1:0))),"response denied: "+Pump.FirstFault);
                Check.True(Pump.DrainTaskEvents());
                var response=Task.ReadEventsAfter(-1,4096).Single(x=>x.Kind=="first_response");
                Check.Equal(trial.TrialId,response.TrialId);
                Check.True(response.Time>trial.Onset.Value);
                Check.True(Double.Parse(response.Fields["rt_seconds"],System.Globalization.CultureInfo.InvariantCulture)>0);
            }
            public void Dispose()
            {
                Pump.Stop();Writer.Release.Set();Worker.TryRequestStop();client.Release();Worker.Stop(1000);Worker.Dispose();
            }
        }
        public static void RegisterEyeQualityRecoveryTests(TestSuite suite)
        {
            suite.Run("after install a hung first status poll never substitutes cached pre-start gaze or blocks onset",()=>{
                using(var f=new EyeQualityFixture(true,frozenBeforeBaseline:true))
                {
                    f.Stage();var trial=f.Task.CurrentTrial;
                    Check.True(f.Has(ViveRawEyeObservation.Reason.NoEye,ViveRawEyeObservation.Disposition.Missing));
                    Check.Equal(0,f.EyeCount);
                    Check.Equal(0,f.Records.OfType<ViveProEyeWorker.ObservedEyeStatus>().Count());
                    f.Witness(trial);f.StillRunning(Phase.Practice);
                }
            });
            suite.Run("an empty status seal requires missing gaze evidence, not a fabricated baseline",()=>{
                foreach(bool observedMissing in new[]{false,true})
                using(var f=new EyeQualityFixture(true,frozenBeforeBaseline:true))
                {
                    if(observedMissing)
                    {
                        f.Stage();
                        Check.True(f.Has(ViveRawEyeObservation.Reason.NoEye,ViveRawEyeObservation.Disposition.Missing));
                        Check.Equal(0,f.EyeCount);
                        Check.True(f.Pump.DrainTaskEvents());
                    }
                    ForceSyntheticCompleted(f.Task); // Terminal plumbing only, not a completed experiment.
                    f.Writer.CompleteResult=true;f.Writer.Release.Set();
                    Check.Equal(observedMissing,f.Pump.RequestCompletedDrain());
                    if(observedMissing)
                    {
                        Check.True(f.Writer.CompleteEntered.WaitOne(3000));
                        Check.True(SpinWait.SpinUntil(()=>f.Pump.HasExited,3000));
                        Check.Equal(ViveNamedRawRecordingPump.Outcome.CloseCallsReturned,f.Pump.State);
                    }
                    else Check.True(f.Admission.IoFence.IsFaulted);
                }
            });
            suite.Run("initial invalid direction records missing gaze without holding Preparation",()=>{
                using(var f=new EyeQualityFixture(false))
                {
                    f.Send(invalid:true);f.Stage();
                    Check.True(f.Has(ViveRawEyeObservation.Reason.InvalidQuality,ViveRawEyeObservation.Disposition.Missing));
                    Check.Equal(0,f.EyeCount);
                    f.StillRunning(Phase.Preparation);
                    Check.True(f.Task.CurrentTrial==null);
                    Check.True(f.Pump.DrainTaskEvents());
                    f.Send();f.Stage();Check.Equal(1,f.EyeCount);
                    f.StillRunning(Phase.Preparation);
                }
            });
            suite.Run("invalid then healthy gaze between owner Updates preserves actual status evidence",()=>{
                using(var f=new EyeQualityFixture(false))
                {
                    f.Send();f.Stage();Check.True(f.Pump.DrainTaskEvents());
                    f.Send(invalid:true);f.Send();f.Stage();
                    var statuses=f.Records.OfType<ViveProEyeWorker.ObservedEyeStatus>().OrderBy(x=>x.Sequence).ToArray();
                    Check.Equal(3,statuses.Length);
                    Check.True((statuses[1].CauseFlags&ViveProEyeWorker.EyeStatusCause.DirectionMaskInvalid)!=0);
                    Check.Equal(ViveProEyeWorker.EyeStatusCause.None,statuses[2].CauseFlags);
                    Check.Equal(2,f.EyeCount);
                    f.StillRunning(Phase.Preparation);
                }
            });
            suite.Run("duplicate then frozen gaze keeps first Practice onset and response with distinct data-quality records",()=>{
                using(var f=new EyeQualityFixture(true))
                {
                    f.Send();f.Stage();Check.True(f.Pump.DrainTaskEvents());var trial=f.Task.CurrentTrial;
                    f.Stage();Check.True(f.Pump.DrainTaskEvents());
                    Check.True(f.Has(ViveRawEyeObservation.Reason.Duplicate,ViveRawEyeObservation.Disposition.NoNewSample));
                    for(int i=0;i<4;i++){f.Stage();Check.True(f.Pump.DrainTaskEvents());}
                    Check.Equal(1,f.Records.OfType<ViveRawEyeObservation>().Count(),"same evidence was not coalesced");
                    Thread.Sleep(1100);
                    f.Send(repeat:true);f.Stage();
                    Check.True(f.Has(ViveRawEyeObservation.Reason.FrozenOrStale,ViveRawEyeObservation.Disposition.Missing));
                    Check.Equal(1,f.EyeCount);
                    f.Witness(trial);double onset=trial.Onset.Value;
                    f.Send(invalid:true,result:-1);f.Stage();
                    Check.True(f.Has(ViveRawEyeObservation.Reason.InvalidQuality,ViveRawEyeObservation.Disposition.Missing));
                    Check.Equal(1,f.EyeCount);Check.True(Object.ReferenceEquals(trial,f.Task.CurrentTrial));
                    f.Respond(trial);Check.Equal(onset,trial.Onset.Value);
                    f.StillRunning(Phase.Practice);
                }
            });
            suite.Run("null eye returns preserve Practice input while true same-head loss still holds",()=>{
                using(var f=new EyeQualityFixture(true))
                {
                    f.SendNoEye();f.Stage();var trial=f.Task.CurrentTrial;
                    Check.True(f.Has(ViveRawEyeObservation.Reason.NoEye,ViveRawEyeObservation.Disposition.Missing));
                    f.Witness(trial);f.StillRunning(Phase.Practice);
                    f.HeadValid=false;f.SendNoEye();Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,f.Pump.PrepareStatus());
                    Check.True(f.Pump.ObserveTrackingHeadLoss(f.Poll(),false));
                    Check.Equal(Phase.Paused,f.Task.Phase);
                    Check.True(f.Pump.TrackingGapOpen&&!f.Admission.IoFence.IsRevoked);
                    Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
                    f.HeadValid=true;f.SendNoEye();f.Stage();
                    Check.True(f.Pump.EndTrackingGap()&&f.Pump.DrainTaskEvents());
                    Check.Equal(Phase.Practice,f.Task.Phase);
                    Check.Equal(trial.PlannedIndex,f.Task.CurrentTrial.PlannedIndex);
                    Check.True(trial.TrialId!=f.Task.CurrentTrial.TrialId&&!f.Task.CurrentTrial.Onset.HasValue);
                    Check.Equal(0,f.EyeCount);
                }
            });
            suite.Run("malformed eye enum is logged but genuine worker revocation is terminal",()=>{
                using(var f=new EyeQualityFixture(true))
                {
                    f.Send(invalid:true,runtime:(ViveEyeRuntimeState)99);f.Stage();
                    Check.True(f.Has(ViveRawEyeObservation.Reason.InvalidQuality,ViveRawEyeObservation.Disposition.Missing));
                    f.StillRunning(Phase.Practice);
                    f.Worker.TryRequestStop();
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Faulted,f.Pump.PrepareStatus());
                    Check.True(f.Admission.IoFence.IsFaulted);
                }
            });
        }
    }
}
#endif
