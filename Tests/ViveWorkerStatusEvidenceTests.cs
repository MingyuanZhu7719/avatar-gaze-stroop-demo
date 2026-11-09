using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // All provider values below are synthetic; no device, path or file is used.
    public static class ViveWorkerStatusEvidenceTests
    {
        sealed class Client : IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> values=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false),Returned=new AutoResetEvent(false);
            internal int PollCalls;
            public string Diagnostic {get{return "synthetic";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Interlocked.Increment(ref PollCalls);Waiting.Set();if(!Permit.WaitOne(3000))throw new Exception("synthetic poll timeout");ViveProEyePoll value;lock(values){value=values.Dequeue();}Returned.Set();return value;}
            internal void Send(ViveProEyePoll poll){lock(values)values.Enqueue(poll);Permit.Set();Wait(Waiting);}
            internal void SendForStop(){lock(values)values.Enqueue(null);Permit.Set();}
            public void Dispose(){}
        }
        static void Wait(WaitHandle h){Check.True(h.WaitOne(3000),"synthetic worker wait timed out");}
        static ViveProEyePoll Eye(double time,int frame,bool noUser=false,ViveEyeCalibrationState calibration=ViveEyeCalibrationState.ReportedReady,int nativeStatus=2)
        {
            var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));
            return new ViveProEyePoll(null,new ViveEyeFrame(frame,frame*8,time,noUser,null,null,ray),ViveEyeRuntimeState.Working,calibration,true,0,nativeStatus);
        }
        static ViveProEyeWorker.StatusTicket Reserve(ViveProEyeWorker worker,object run)
        {
            for(int i=0;i<600;i++)
            {
                ViveProEyeWorker.StatusTicket ticket;var status=worker.TryReserveStatusEvidence(run,out ticket);
                if(status==ViveProEyeWorker.StatusDisposition.Event)return ticket;
                Check.True(status==ViveProEyeWorker.StatusDisposition.Empty||status==ViveProEyeWorker.StatusDisposition.Busy,"Unexpected status "+status);
                Thread.Sleep(5);
            }
            throw new Exception("No synthetic status event");
        }
        static void EnableAfterBlockedPoll(ViveProEyeWorker worker,Client client,int capacity,out object run)
        {
            Check.True(worker.TryEnableStatusEvidence(capacity,out run));
            // The first provider call started before enable and returns later.
            // It must not become post-choice status evidence.
            client.Send(null);ViveProEyeWorker.StatusTicket none;
            Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.TryReserveStatusEvidence(run,out none));
        }
        static void Stop(ViveProEyeWorker worker,Client client)
        {
            worker.TryRequestStop(); // never join a blocked fake provider on the owner thread
            client.SendForStop();Check.True(worker.Stop(1000));worker.Dispose();
        }
        // Pure worker fixture: no native provider, persisted eye samples,
        // participant session, file or product start authority.
        static void ExerciseRepeatedFrameWithStatus(int scenario)
        {
            var clock=new MonotonicClock("synthetic-status-duplicate-"+scenario);var client=new Client();
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            var shared=new ViveRawRevocation();object run=null;
            try
            {
                Check.True(worker.Start());Wait(client.Waiting);Check.True(worker.TryEnableStatusEvidence(4,shared,out run));
                client.Send(null);double firstObserved=clock.Now;client.Send(Eye(firstObserved,1));
                var baseline=Reserve(worker,run);Check.True(worker.TryAcknowledgeStatusEvidence(baseline));
                ViveProEyePoll frame;Check.True(worker.TryPollForRecording(out frame));
                ViveProEyeWorker.RecordingFrameTicket initial,ticket;
                Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(frame,out initial));
                ViveProEyePoll consumed;Check.True(worker.TryConsumeRecordingTicket(initial,out consumed));
                Thread.Sleep(2);double later=clock.Now;Check.True(later>firstObserved);client.Send(Eye(later,1));
                Check.True(worker.TryPollForRecording(out frame));
                Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Duplicate,worker.TryIssueRecordingTicket(frame,out ticket));
                Check.True(ticket==null);
                var flags=BindingFlags.NonPublic|BindingFlags.Instance;
                Check.Near(firstObserved,(double)run.GetType().GetField("LastTicketObserved",flags).GetValue(run));
                Check.Near(later,(double)run.GetType().GetField("LastSeenTicketObserved",flags).GetValue(run));
                Check.Equal(ViveProEyeWorker.RecordingEpochResult.Changed,worker.TryCheckRecordingEpoch(initial));
                Check.True(worker.TryPollForRecording(out frame));
                if(scenario==0)
                {
                    client.Send(Eye(clock.Now,1,nativeStatus:3));
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(frame,out ticket));
                    Check.True(ticket==null);var changed=Reserve(worker,run);Check.True(worker.TryAcknowledgeStatusEvidence(changed));
                    Check.True(worker.TryPollForRecording(out frame));
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Duplicate,worker.TryIssueRecordingTicket(frame,out ticket));
                    Check.True(ticket==null);Check.Near(firstObserved,(double)run.GetType().GetField("LastTicketObserved",flags).GetValue(run));
                }
                else
                {
                    if(scenario==1)shared.Revoke();else Check.True(worker.TryRequestStop());
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(frame,out ticket));
                    Check.True(ticket==null&&!worker.TryPollForRecording(out frame));
                }
            }
            finally{Stop(worker,client);}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("status-enabled repeated eye frame stays duplicate through status revision",()=>ExerciseRepeatedFrameWithStatus(0));
            suite.Run("status-enabled repeated eye frame rejects participant revoke",()=>ExerciseRepeatedFrameWithStatus(1));
            suite.Run("status-enabled repeated eye frame rejects stop",()=>ExerciseRepeatedFrameWithStatus(2));
            suite.Run("VIVE status FIFO records only observed state transitions and rejects replay",()=>{
                var clock=new MonotonicClock("status-synthetic-clock");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);object run=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);EnableAfterBlockedPoll(worker,client,4,out run);
                    object ignored;Check.True(!worker.TryEnableStatusEvidence(4,out ignored));
                    client.Send(Eye(clock.Now,1));var first=Reserve(worker,run);
                    Check.Equal(0L,first.Event.Sequence);Check.Equal(ViveProEyeWorker.EyeStatusCause.None,first.Event.CauseFlags);
                    Check.True(first.Event.PollReturnLocalSeconds>=0);Check.Equal((bool?)false,first.Event.NoUser);
                    ViveProEyeWorker.StatusTicket reserved;
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Reserved,worker.TryReserveStatusEvidence(run,out reserved));
                    Check.Equal(ViveProEyeWorker.StatusAckResult.Invalid,worker.TryAcknowledgeStatusEvidenceResult(first,null));
                    var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket blocked;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(poll,out blocked));
                    var forged=new ViveProEyeWorker.StatusTicket(worker,run,first.Event);Check.True(!worker.TryAcknowledgeStatusEvidence(forged));
                    Check.True(worker.TryAcknowledgeStatusEvidence(first));Check.True(!worker.TryAcknowledgeStatusEvidence(first));
                    client.Send(Eye(clock.Now,2));ViveProEyeWorker.StatusTicket none;
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.TryReserveStatusEvidence(run,out none));
                    client.Send(Eye(clock.Now,3,true,ViveEyeCalibrationState.Required));var invalid=Reserve(worker,run);
                    Check.Equal(1L,invalid.Event.Sequence);Check.True(invalid.Event.PollReturnLocalSeconds>=first.Event.PollReturnLocalSeconds);
                    Check.True((invalid.Event.CauseFlags&ViveProEyeWorker.EyeStatusCause.NoEyeData)!=0);
                    Check.True((invalid.Event.CauseFlags&ViveProEyeWorker.EyeStatusCause.CalibrationNotReady)!=0);
                    Check.True(worker.TryAcknowledgeStatusEvidence(invalid));
                    client.Send(Eye(clock.Now,4));var recovered=Reserve(worker,run);
                    Check.Equal(2L,recovered.Event.Sequence);Check.Equal(ViveProEyeWorker.EyeStatusCause.None,recovered.Event.CauseFlags);
                    Check.True(worker.TryAcknowledgeStatusEvidence(recovered));
                }
                finally{Stop(worker,client);}
            });
            suite.Run("VIVE status FIFO preserves unknowns and faults on overflow",()=>{
                var clock=new MonotonicClock("status-overflow-clock");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);object run=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);EnableAfterBlockedPoll(worker,client,1,out run);
                    client.Send(null);var missing=Reserve(worker,run);Check.Equal(ViveProEyeWorker.EyeStatusCause.PollMissing,missing.Event.CauseFlags);
                    Check.Equal((bool?)null,missing.Event.NoUser);Check.Equal((ulong?)null,missing.Event.CombinedValidityMask);
                    client.Send(Eye(clock.Now,1));ViveProEyeWorker.StatusTicket unused;
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Faulted,worker.TryReserveStatusEvidence(run,out unused));
                    Check.True(!worker.TryAcknowledgeStatusEvidence(missing));
                    var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket denied;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(poll,out denied));
                }
                finally{Stop(worker,client);}
            });
            suite.Run("VIVE status changes invalidate an in-flight frame and preserve intermediate transitions",()=>{
                var clock=new MonotonicClock("status-interleaving-clock");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);object run=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);EnableAfterBlockedPoll(worker,client,4,out run);
                    client.Send(Eye(clock.Now,1));var baseline=Reserve(worker,run);Check.True(worker.TryAcknowledgeStatusEvidence(baseline));
                    var polled=worker.Poll();ViveProEyeWorker.RecordingFrameTicket old;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(polled,out old));
                    client.Send(Eye(clock.Now,2,true));client.Send(Eye(clock.Now,3));ViveProEyePoll ignored;
                    Check.True(!worker.TryConsumeRecordingTicket(old,out ignored));
                    var lost=Reserve(worker,run);Check.Equal(1L,lost.Event.Sequence);
                    Check.True((lost.Event.CauseFlags&ViveProEyeWorker.EyeStatusCause.NoEyeData)!=0);
                    Check.True(worker.TryAcknowledgeStatusEvidence(lost));var returned=Reserve(worker,run);
                    Check.Equal(2L,returned.Event.Sequence);Check.Equal(ViveProEyeWorker.EyeStatusCause.None,returned.Event.CauseFlags);
                    Check.True(worker.TryAcknowledgeStatusEvidence(returned));
                    var fresh=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(fresh,out ticket));
                    Check.True(worker.TryStopStatusEvidence(run));Check.True(!worker.TryConsumeRecordingTicket(ticket,out ignored));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Unavailable,worker.TryReserveStatusEvidence(run,out returned));
                    var later=worker.Poll();Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(later,out ticket));
                }
                finally{Stop(worker,client);}
            });
            suite.Run("VIVE status revision captured by poll forbids an older valid frame after newer status",()=>{
                var clock=new MonotonicClock("status-old-frame-clock");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);object run=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);EnableAfterBlockedPoll(worker,client,4,out run);
                    client.Send(Eye(clock.Now,1));var initial=Reserve(worker,run);Check.True(worker.TryAcknowledgeStatusEvidence(initial));
                    var old=worker.Poll();client.Send(Eye(clock.Now,2,nativeStatus:3));
                    var changed=Reserve(worker,run);Check.Equal((int?)3,changed.Event.NativeRuntimeStatus);Check.True(worker.TryAcknowledgeStatusEvidence(changed));
                    ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(old,out ticket));
                    var fresh=worker.Poll();Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(fresh,out ticket));
                }
                finally{Stop(worker,client);}
            });
            suite.Run("VIVE recording epoch separates busy from status change and rejects old tickets",()=>{
                var clock=new MonotonicClock("status-epoch-clock");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);object run=null;
                var held=new ManualResetEvent(false);var release=new ManualResetEvent(false);Thread holder=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);EnableAfterBlockedPoll(worker,client,4,out run);
                    client.Send(Eye(clock.Now,1));var baseline=Reserve(worker,run);Check.True(worker.TryAcknowledgeStatusEvidence(baseline));
                    var frame=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket;ViveProEyePoll consumed;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(frame,out ticket));
                    Check.True(worker.TryConsumeRecordingTicket(ticket,out consumed));
                    Check.Equal(ViveProEyeWorker.RecordingEpochResult.Current,worker.TryCheckRecordingEpoch(ticket));
                    object gate=typeof(ViveProEyeWorker).GetField("gate",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(worker);
                    holder=new Thread(()=>{lock(gate){held.Set();release.WaitOne();}}){IsBackground=true};holder.Start();Wait(held);
                    Check.Equal(ViveProEyeWorker.RecordingEpochResult.Busy,worker.TryCheckRecordingEpoch(ticket));
                    release.Set();Check.True(holder.Join(1000));
                    Check.Equal(ViveProEyeWorker.RecordingEpochResult.Current,worker.TryCheckRecordingEpoch(ticket));
                    client.Send(Eye(clock.Now,2,true));
                    Check.Equal(ViveProEyeWorker.RecordingEpochResult.Changed,worker.TryCheckRecordingEpoch(ticket));
                }
                finally{release.Set();if(holder!=null)holder.Join(1000);Stop(worker,client);}
            });
            suite.Run("VIVE status tickets bind their worker run and remain invalid after stop",()=>{
                var clock=new MonotonicClock("status-run-clock");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);object run=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);EnableAfterBlockedPoll(worker,client,2,out run);
                    client.Send(new ViveProEyePoll(null,null,ViveEyeRuntimeState.Error,ViveEyeCalibrationState.Unknown,false,-7,null));
                    var status=Reserve(worker,run);Check.Equal((bool?)null,status.Event.NoUser);Check.Equal((ulong?)null,status.Event.CombinedValidityMask);
                    Check.True((status.Event.CauseFlags&ViveProEyeWorker.EyeStatusCause.NativeResultFailed)!=0);
                    ViveProEyeWorker.StatusTicket wrong;Check.Equal(ViveProEyeWorker.StatusDisposition.Unavailable,worker.TryReserveStatusEvidence(new object(),out wrong));
                    Check.True(worker.TryRequestStop());Check.True(!worker.TryAcknowledgeStatusEvidence(status));
                }
                finally{Stop(worker,client);}
            });
            suite.Run("VIVE status revoke from a foreign thread makes outstanding evidence unusable",()=>{
                var clock=new MonotonicClock("status-revoke-clock");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);object run=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);EnableAfterBlockedPoll(worker,client,2,out run);
                    client.Send(Eye(clock.Now,1));var ticket=Reserve(worker,run);
                    var revoked=new Thread(worker.RevokeStatusEvidence){IsBackground=true};revoked.Start();Check.True(revoked.Join(1000));
                    Check.True(!worker.TryAcknowledgeStatusEvidence(ticket));ViveProEyeWorker.StatusTicket again;
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Unavailable,worker.TryReserveStatusEvidence(run,out again));
                    var polled=worker.Poll();ViveProEyeWorker.RecordingFrameTicket denied;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(polled,out denied));
                }
                finally{Stop(worker,client);}
            });
            suite.Run("VIVE shared participant revoke blocks status even when admission is suspended",()=>{
                var clock=new MonotonicClock("status-shared-revoke-clock");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                var shared=new ViveRawRevocation();object run=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);
                    Check.True(worker.TryEnableStatusEvidence(2,shared,out run));
                    client.Send(null);client.Send(Eye(clock.Now,1));var ticket=Reserve(worker,run);
                    // Only the participant's shared bit is changed; no call to
                    // admission.Withdraw or worker.RevokeStatusEvidence occurs.
                    var withdraw=new Thread(shared.Revoke){IsBackground=true};withdraw.Start();Check.True(withdraw.Join(1000));
                    Check.True(!worker.TryAcknowledgeStatusEvidence(ticket));ViveProEyeWorker.StatusTicket status;
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Unavailable,worker.TryReserveStatusEvidence(run,out status));
                    ViveProEyePoll raw;Check.True(!worker.TryPollForRecording(out raw)&&raw==null);
                    client.Send(Eye(clock.Now,2,true));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Unavailable,worker.TryReserveStatusEvidence(run,out status));
                    var polled=worker.Poll();ViveProEyeWorker.RecordingFrameTicket denied;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(polled,out denied));
                }
                finally{Stop(worker,client);}
            });
            suite.Run("VIVE status seal proves only one drained registered queue and rejects replay",()=>{
                var clock=new MonotonicClock("status-seal-clock");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);object run=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);EnableAfterBlockedPoll(worker,client,4,out run);
                    client.Send(Eye(clock.Now,1));var first=Reserve(worker,run);
                    ViveProEyeWorker.StatusSeal seal;Check.True(worker.TrySealStatusEvidence(run,out seal));
                    Check.Equal(0L,seal.FinalSequence);Check.True(seal.InFlightPollAndTailUnknown);
                    Check.True(!seal.TryConsume(run));Check.True(worker.TryAcknowledgeStatusEvidence(first));
                    // This poll was already in flight when sealed. Its return
                    // must not create another evidence event or change the seal.
                    client.Send(Eye(clock.Now,2,true));ViveProEyeWorker.StatusTicket none;
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.TryReserveStatusEvidence(run,out none));
                    Check.True(!seal.TryConsume(new object()));Check.True(seal.TryConsume(run));Check.True(!seal.TryConsume(run));
                    var polled=worker.Poll();ViveProEyeWorker.RecordingFrameTicket denied;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(polled,out denied));
                }
                finally{Stop(worker,client);}
            });
            suite.Run("VIVE nonblocking stop reports lock contention and pending native cleanup",()=>{
                var clock=new MonotonicClock("status-stop-clock");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                var held=new ManualResetEvent(false);var release=new ManualResetEvent(false);Thread holder=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);
                    object gate=typeof(ViveProEyeWorker).GetField("gate",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(worker);
                    holder=new Thread(()=>{lock(gate){held.Set();release.WaitOne();}}){IsBackground=true};holder.Start();Wait(held);
                    bool exited;Check.True(!worker.TryRequestStop());Check.True(!worker.TryCheckExited(out exited)&&!exited);
                    release.Set();Check.True(holder.Join(1000));Check.True(worker.TryRequestStop());
                    Check.True(worker.TryCheckExited(out exited)&&!exited);
                    client.SendForStop();Check.True(worker.Stop(1000));Check.True(worker.TryCheckExited(out exited)&&exited);
                }
                finally{release.Set();if(holder!=null)holder.Join(1000);client.SendForStop();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("VIVE recording poll never waits for its worker gate or returns a stale frame",()=>{
                var clock=new MonotonicClock("status-nonblocking-clock");var client=new Client();
                var held=new ManualResetEvent(false);var release=new ManualResetEvent(false);Thread holder=null;
                object gate=null;bool holdDuringHead=false;int reads=0;
                var worker=new ViveProEyeWorker(clock,()=>{
                    reads++;
                    if(holdDuringHead){holder=new Thread(()=>{lock(gate){held.Set();release.WaitOne();}}){IsBackground=true};holder.Start();Wait(held);}
                    return new ViveHeadFrame(reads,clock.Now,true,true,Rotation.Identity);
                },()=>client);object run=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);EnableAfterBlockedPoll(worker,client,3,out run);
                    client.Send(Eye(clock.Now,1));var baseline=Reserve(worker,run);Check.True(worker.TryAcknowledgeStatusEvidence(baseline));
                    ViveProEyePoll first;Check.True(worker.TryPollForRecording(out first)&&first!=null);Check.Equal(1,reads);
                    gate=typeof(ViveProEyeWorker).GetField("gate",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(worker);
                    holdDuringHead=true;ViveProEyePoll absent;
                    Check.True(!worker.TryPollForRecording(out absent)&&absent==null);Check.Equal(2,reads);
                    release.Set();Check.True(holder.Join(1000));holdDuringHead=false;
                    Check.True(worker.TryRequestStop());Check.True(!worker.TryPollForRecording(out absent)&&absent==null);
                }
                finally{release.Set();if(holder!=null)holder.Join(1000);Stop(worker,client);}
            });
            suite.Run("VIVE stop request fences a late provider return even when worker gate is busy",()=>{
                var clock=new MonotonicClock("status-stop-fence-clock");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                var held=new ManualResetEvent(false);var release=new ManualResetEvent(false);Thread holder=null;
                try
                {
                    Check.True(worker.Start());Wait(client.Waiting);
                    object gate=typeof(ViveProEyeWorker).GetField("gate",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(worker);
                    holder=new Thread(()=>{lock(gate){held.Set();release.WaitOne();}}){IsBackground=true};holder.Start();Wait(held);
                    Check.True(!worker.TryRequestStop());client.SendForStop();Wait(client.Returned);Thread.Sleep(20);
                    Check.Equal(1,Volatile.Read(ref client.PollCalls));ViveProEyePoll absent;
                    Check.True(!worker.TryPollForRecording(out absent)&&absent==null);
                    release.Set();Check.True(holder.Join(1000));Check.True(worker.Stop(1000));
                    int frame,timestamp;double observed;Check.True(!worker.TryPeekLatestEyeMetadata(out frame,out timestamp,out observed));
                }
                finally{release.Set();if(holder!=null)holder.Join(1000);client.SendForStop();worker.Stop(1000);worker.Dispose();}
            });
        }
    }
}
