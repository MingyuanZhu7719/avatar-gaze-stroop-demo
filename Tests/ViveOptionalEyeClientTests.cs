using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveOptionalEyeClientTests
    {
        sealed class Client : IViveProEyeClient,IViveProEyeCleanupStatus,IViveProEyeAcquisitionSeal
        {
            internal bool StartResult=true,ThrowStart,UnsafeCleanup,ThrowPoll;
            internal ViveProEyePoll Value;
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            internal readonly ManualResetEvent Disposed=new ManualResetEvent(false);
            public string Diagnostic {get{return "synthetic optional eye client";}}
            public bool RestartSafe {get{return !UnsafeCleanup;}}
            public bool Start(){if(ThrowStart)throw new InvalidOperationException("synthetic start failure");return StartResult;}
            public ViveProEyePoll Poll()
            {
                Waiting.Set();if(!Permit.WaitOne(5000))throw new TimeoutException("synthetic test did not release poll");
                if(ThrowPoll)throw new InvalidOperationException("synthetic client disconnected");return Value;
            }
            internal void Send(ViveProEyePoll value)
            {Value=value;Permit.Set();Check.True(Waiting.WaitOne(3000),"synthetic client did not request next poll");}
            public bool RequestAcquisitionSeal(){return false;}
            public bool AcquisitionSealDrained {get{return false;}}
            public bool HasBufferedAcquisition {get{return false;}}
            public void Dispose(){Disposed.Set();}
        }
        static ViveProEyePoll Eye(MonotonicClock clock,int sequence)
        {
            var ray=new ViveEyeRay(3,true,true,new Vec3(0,0,0),new Vec3(0,0,1));
            return new ViveProEyePoll(null,new ViveEyeFrame(sequence,sequence*8,clock.Now,false,ray,ray,ray),
                ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
        }
        static ViveProEyeWorker.RecordingFrameTicket TaskTicket(ViveProEyeWorker worker,MonotonicClock clock)
        {
            ViveProEyePoll poll;Check.True(worker.TryPollTaskForRecording(out poll));
            ViveProEyeWorker.RecordingFrameTicket ticket;
            Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueIndependentTaskTicket(poll,clock.Now,1,false,out ticket));
            return ticket;
        }
        static int DrainStatus(ViveProEyeWorker worker,object run,out bool missing)
        {
            int count=0;missing=false;ViveProEyeWorker.StatusTicket status;
            while(worker.TryReserveStatusEvidence(run,out status)==ViveProEyeWorker.StatusDisposition.Event)
            {
                if(status.Event.CauseFlags==ViveProEyeWorker.EyeStatusCause.PollMissing)
                {missing=true;Check.True(status.Event.Runtime==null&&status.Event.Calibration==null&&status.Event.NativeResultCode==null);}
                Check.True(worker.TryAcknowledgeStatusEvidence(status));count++;
            }
            return count;
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("Participant native initialization failure retains head authority and records missing eye cause",()=>{
                foreach(int mode in new[]{0,1,2})
                {
                    var clock=new MonotonicClock("optional-eye-init-"+mode);long sequence=0;bool headValid=true;
                    var client=new Client{StartResult=mode!=0,ThrowStart=mode==1};
                    var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++sequence,clock.Now,true,headValid,Rotation.Identity),
                        ()=>{if(mode==2)throw new InvalidOperationException("synthetic missing installed client");return client;});
                    try
                    {
                        Check.True(worker.StartTaskOnly());object run;Check.True(worker.TryEnableStatusEvidence(16,new ViveRawRevocation(),out run,true));
                        Check.True(worker.RequestFormalEyeStart());Check.True(SpinWait.SpinUntil(()=>worker.EyeClientFailed,3000));
                        Check.Equal(ViveProEyeWorker.EyeClientFailureKind.InitializationFailed,worker.ParticipantEyeFailure);
                        Check.True(worker.IsTaskOnlyRecordingRun&&!worker.Started&&!worker.Stopping&&!worker.CanStart);
                        Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadStatusBacklog(run));
                        Check.True(!worker.ReadRecordingStartReadiness().Ready);
                        Check.Equal(ViveProEyeWorker.RecordingStartReadinessState.Ready,worker.TryEnableFormalEyePersistence(run,clock.Now));
                        bool missing;Check.Equal(1,DrainStatus(worker,run,out missing));Check.True(missing);
                        var ticket=TaskTicket(worker,clock);Check.True(ticket.HeadOnly&&!ticket.EyeNotRequested&&ticket.Frame.Eye==null);
                        Check.Equal(ViveRawEyeObservation.Reason.ClientInitializationFailed,ticket.EyeUnavailable.Value);
                        ViveProEyePoll consumed;Check.True(worker.TryConsumeRecordingTicket(ticket,out consumed));
                        Check.Equal(ViveProEyeWorker.RecordingEpochResult.Current,worker.TryCheckRecordingEpoch(ticket));
                        headValid=false;ViveProEyePoll invalid;Check.True(worker.TryPollTaskForRecording(out invalid));
                        Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueIndependentTaskTicket(invalid,clock.Now,1,false,out ticket));
                        Check.True(worker.TryRequestStop());Check.True(!worker.TryPollTaskForRecording(out invalid));Check.True(worker.Stop(1000));
                    }
                    finally{worker.Dispose();client.Permit.Set();worker.Stop(1000);}
                }
            });
            suite.Run("Participant eye client failure preserves admitted status and acquisition tail but never repeats gaze",()=>{
                var clock=new MonotonicClock("optional-eye-disconnect");long sequence=0;var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++sequence,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.StartTaskOnly());object run;Check.True(worker.TryEnableStatusEvidence(16,new ViveRawRevocation(),out run,true));
                    Check.True(worker.RequestFormalEyeStart()&&client.Waiting.WaitOne(3000));
                    Check.Equal(ViveProEyeWorker.RecordingStartReadinessState.Ready,worker.TryEnableFormalEyePersistence(run,clock.Now));
                    client.Send(Eye(clock,1));client.Send(Eye(clock,2));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Event,worker.ReadAcquisitionBacklog(run));
                    client.ThrowPoll=true;client.Permit.Set();Check.True(client.Disposed.WaitOne(3000));
                    Check.True(SpinWait.SpinUntil(()=>worker.EyeClientFailed,3000));
                    Check.Equal(ViveProEyeWorker.EyeClientFailureKind.ClientFaulted,worker.ParticipantEyeFailure);
                    Check.True(worker.IsAttachedTaskRunCurrent(run));bool missing;Check.Equal(2,DrainStatus(worker,run,out missing));Check.True(missing);
                    ViveRawEyeAcquisition acquired;Check.Equal(ViveProEyeWorker.StatusDisposition.Event,worker.TryDequeueAcquisition(run,out acquired));
                    Check.Equal(2,acquired.Poll.Eye.FrameSequence);
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadAcquisitionBacklog(run));
                    var ticket=TaskTicket(worker,clock);Check.Equal(ViveRawEyeObservation.Reason.ClientFaulted,ticket.EyeUnavailable.Value);Check.True(ticket.Frame.Eye==null);
                    ViveProEyePoll consumed;Check.True(worker.TryConsumeRecordingTicket(ticket,out consumed));
                    Check.Equal(ViveProEyeWorker.RecordingEpochResult.Current,worker.TryCheckRecordingEpoch(ticket));
                    Check.True(SpinWait.SpinUntil(()=>worker.PrepareAcquisitionSeal(run)==ViveProEyeWorker.AcquisitionSealDisposition.Ready,3000));
                    ViveProEyeWorker.StatusSeal seal;Check.True(worker.TrySealStatusEvidence(run,out seal,true));
                    Check.True(!worker.IsAttachedTaskRunCurrent(run));Check.True(worker.Stop(1000));
                }
                finally{worker.Dispose();client.Permit.Set();worker.Stop(1000);}
            });
            suite.Run("Unrecorded participant keeps real head after failed eye cleanup but native restart stays blocked",()=>{
                var clock=new MonotonicClock("optional-eye-unsafe-cleanup");long sequence=0;
                var client=new Client{StartResult=false,UnsafeCleanup=true};
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++sequence,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.StartTaskOnly()&&worker.RequestFormalEyeStart());Check.True(client.Disposed.WaitOne(3000));
                    Check.True(worker.IsTaskOnlyRecordingRun&&worker.EyeClientFailed&&!worker.Started);
                    var poll=worker.Poll();Check.True(poll.Head.DeviceConnected&&poll.Head.RotationReportedValid&&poll.Eye==null);
                    Check.Equal(ViveEyeRuntimeState.Unavailable,poll.Runtime);
                    Check.True(worker.Stop(1000));Check.True(!worker.CanStart&&!worker.StartTaskOnly());
                }
                finally{worker.Dispose();worker.Stop(1000);}
            });
            suite.Run("Operator native initialization failure still terminates its native worker",()=>{
                var clock=new MonotonicClock("mandatory-operator-eye");var client=new Client{StartResult=false};
                var worker=new ViveProEyeWorker(clock,()=>null,()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(SpinWait.SpinUntil(()=>worker.HasExited,3000));
                    Check.True(!worker.EyeClientFailed&&!worker.IsTaskOnlyRecordingRun&&!worker.Started);
                }
                finally{worker.Dispose();worker.Stop(1000);}
            });
        }
    }
}
