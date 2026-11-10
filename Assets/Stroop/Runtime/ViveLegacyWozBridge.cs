using System;
using System.Security.Cryptography;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Actual participant -> HMAC task mirror -> owned source candidate -> HMAC
    // current-task memory output. The transport is sealed memory, not a socket,
    // physical peer identity, source confirmation, preflight or live admission.
    public sealed partial class ViveLegacyWozBridge : IDisposable
    {
        readonly Thread thread=Thread.CurrentThread;
        readonly ViveLegacyWozTaskOwner owner;
        readonly MonotonicClock participantClock,operatorClock;
        readonly WozLanContext context;
        readonly WozAuthenticatedLanEndpoint participantEndpoint,operatorEndpoint;
        readonly ViveLegacyWozObservationOwner source;
        readonly object receiptToken=new object();
        readonly double maxAge;
        ViveLegacyWozTaskOwner.Binding binding;
        WozLanTaskContext published,mirror;
        double mirrorReceived;
        long participantSequence,operatorSequence;
        bool terminal,closed;
        HmacMemoryReceipt output;
        ViveLegacyWozObservationOwner.Candidate protectedCandidate;
        public bool Terminal {get{return terminal;}}
        public string LastIssue {get;private set;}
        public bool CanEnterLiveCountdown {get{return false;}}
        public bool CanPresentLivePose {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public bool UsesSockets {get{return false;}}
        public string Epoch {get{return context.Epoch;}}
        public TaskSnapshot OperatorTask {get{return !OwnerThread()||terminal||mirror==null||!owner.IsCurrent(binding)?null:mirror.Task;}}
        public HmacMemoryReceipt CurrentMemoryOutput
        {
            get
            {
                if(!OwnerThread())return null;
                if(controlMode&&!AuthorizedMemoryPoseCurrent()){output=null;return null;}
                if(!Active()||!owner.IsCurrent(binding)||output==null||!CausalReturnFresh()||!OriginalObservationsFresh()||
                    participantClock.Now-output.OriginalLocalReceiptSeconds>maxAge){output=null;return null;}
                return output;
            }
        }
        // The separate provenance type deliberately cannot be converted into
        // live/recording evidence. No public constructor or timestamp input.
        public sealed class HmacMemoryReceipt
        {
            public HeadEyeSample Sample {get;private set;}
            public string Epoch {get;private set;}
            public string SourceBinding {get;private set;}
            public string ReceiptClockId {get;private set;}
            public long TaskRevision {get;private set;}
            public double OriginalLocalReceiptSeconds {get;private set;}
            public PeerRole SenderRole {get{return PeerRole.Operator;}}
            public PeerRole ReceiverRole {get{return PeerRole.Participant;}}
            public bool PlaintextSharedKeyPossession {get{return true;}}
            public bool ReceiptCapturedBySocket {get{return false;}}
            public bool SealedMemoryReceipt {get{return true;}}
            public bool SyntheticObservationsOnly {get{return true;}}
            public bool IsExperimentAdmission {get{return false;}}
            // Native/source diagnostics stay operator-local, not on this receipt.
            private HmacMemoryReceipt(ViveLegacyWozBridge bridge,HeadEyeSample sample,double received)
            {Sample=sample;Epoch=bridge.Epoch;SourceBinding=bridge.context.Operator.SourceId;ReceiptClockId=bridge.participantClock.ClockId;
                TaskRevision=bridge.published.Revision;OriginalLocalReceiptSeconds=received;}
            internal static HmacMemoryReceipt Issue(ViveLegacyWozBridge bridge,object token,HeadEyeSample sample,double received)
            {
                if(bridge==null||!Object.ReferenceEquals(token,bridge.receiptToken)||bridge.terminal||!bridge.owner.IsCurrent(bridge.binding))
                    throw new InvalidOperationException("Owned memory receipt capability required.");
                return new HmacMemoryReceipt(bridge,sample,received);
            }
        }
        // Production-compiled task mirror only. It cannot acquire sensor data.
        public static ViveLegacyWozBridge CreateTaskMirrorMemoryOnly(ViveLegacyWozTaskOwner owner,MonotonicClock participantClock,MonotonicClock operatorClock)
        {return new ViveLegacyWozBridge(owner,participantClock,operatorClock,null,1);}
        ViveLegacyWozBridge(ViveLegacyWozTaskOwner owner,MonotonicClock participantClock,MonotonicClock operatorClock,
            Func<string,ViveLegacyWozObservationOwner> fixtureSource,double maximumAge)
        {
            if(owner==null||participantClock==null||operatorClock==null||!owner.UsesClock(participantClock)||
                Object.ReferenceEquals(participantClock,operatorClock)||participantClock.ClockId==operatorClock.ClockId||
                !WireCodec.Finite(maximumAge)||maximumAge<=0||!owner.OnOwnerThread||owner.Terminal)
                throw new ArgumentException("Actual owner and distinct exact sealed clocks required.");
            this.owner=owner;this.participantClock=participantClock;this.operatorClock=operatorClock;maxAge=maximumAge;
            byte[] epoch=new byte[16],key=new byte[32];
            using(var rng=RandomNumberGenerator.Create()){rng.GetBytes(epoch);rng.GetBytes(key);}
            string epochLabel=BitConverter.ToString(epoch).Replace("-","");
            try
            {
                if(fixtureSource!=null)source=fixtureSource(epochLabel);
                string sourceStream=ViveDevelopmentTransport.OperatorStream(ViveProEyeRehearsalCoordinates.MappingVersion,epochLabel);
                if(source!=null&&(!source.UsesClock(operatorClock)||source.StreamId!=sourceStream))throw new ArgumentException("Owned source differs.");
                string stream="simulator/legacy-memory/"+epochLabel;
                context=new WozLanContext(new PeerIdentity(owner.SessionId,"legacy-participant",PeerRole.Participant,"legacy-task",ControlSource.Simulator,participantClock.ClockId),
                    new PeerIdentity(owner.SessionId,"legacy-operator",PeerRole.Operator,stream,ControlSource.Simulator,operatorClock.ClockId),
                    epoch,ViveProEyeRehearsalCoordinates.MappingVersion,"vive-pro-eye-no-serial");
                participantEndpoint=new WozAuthenticatedLanEndpoint(context,PeerRole.Participant,key,maximumAge);
                operatorEndpoint=new WozAuthenticatedLanEndpoint(context,PeerRole.Operator,key,maximumAge);
                InitializeControlEndpoints(key);
            }
            catch{if(source!=null)source.Dispose();throw;}
            finally{Array.Clear(key,0,key.Length);}
        }
        bool OwnerThread(){return Object.ReferenceEquals(thread,Thread.CurrentThread);}
        bool Active()
        {
            if(!OwnerThread())return false;
            if(terminal)return false;
            if(owner.Terminal||(source!=null&&!source.BindingCurrent)){Terminate("owner_or_source_lost");return false;}
            return true;
        }
        // Both clocks are owned in this in-process transport. This check is
        // NOT a remote clock mapping, and cannot be copied into a socket receipt.
        bool OriginalObservationsFresh()
        {return protectedCandidate!=null&&operatorClock.Now-protectedCandidate.HeadObserved<=maxAge&&
            operatorClock.Now-protectedCandidate.EyeObserved<=maxAge;}
        // Conservative causal age using ONLY the participant's original task
        // issue time. Exact task sequence is MAC-bound; the owned sender admits
        // only observations after receiving that task. No cross-clock subtraction
        // or replacement of an original time with receipt time is used.
        bool CausalReturnFresh()
        {return published!=null&&participantClock.Now-published.ParticipantSentSeconds<=maxAge;}
        bool Next(bool participant,out long sequence)
        {
            sequence=participant?participantSequence:operatorSequence;
            if(sequence==Int64.MaxValue){Terminate("packet_sequence_exhausted");return false;}
            if(participant)participantSequence++;else operatorSequence++;return true;
        }
        public bool PublishCurrentTask()
        {
            output=null;if(!Active())return false;
            ViveLegacyWozTaskOwner.Binding captured;
            if(!owner.TryCapture(out captured)){Terminate("task_authority_lost");return false;}
            if(captured.Task.Phase==Phase.Paused||captured.Task.Phase==Phase.Ended){Terminate("task_paused_or_ended");return false;}
            long sequence;if(!Next(true,out sequence))return false;
            var task=new WozLanTaskContext(captured.Task,captured.Revision,sequence,participantClock.Now);
            byte[] bytes;string error;
            if(!owner.IsCurrent(captured)||!participantEndpoint.TryProtect(WirePacket.Task(context.Participant,sequence,task.ParticipantSentSeconds,task.Task),task,null,out bytes,out error))
            {Terminate("task_publication_failed");return false;}
#if STROOP_TEST_BUILD
            var hook=BeforeTaskDeliveryForTest;BeforeTaskDeliveryForTest=null;if(hook!=null)hook();
#endif
            if(!owner.IsCurrent(captured)){Terminate("task_changed_before_publication");return false;}
            double received=operatorClock.Now;WozAuthenticatedLanEndpoint.VerifiedMessage verified;
            if(!operatorEndpoint.TryReceive(bytes,received,operatorClock.Now,null,out verified,out error)){Terminate(error);return false;}
            if(!owner.IsCurrent(captured)){Terminate("task_changed_during_receipt");return false;}
            binding=captured;published=task;mirror=verified.TaskContext;mirrorReceived=received;
            InvalidatePhaseControl();
            return true;
        }
        public bool PollMemoryObservation()
        {
            output=null;if(!Active()||source==null||mirror==null)return false;
            if(controlMode&&!AuthorizedMemoryPoseCurrent())return false;
            if(!owner.IsCurrent(binding)){Terminate("task_revision_changed");return false;}
            if(mirror.Task.Phase!=Phase.MainTask||mirror.Task.Condition==Condition.D)return false;
            if(operatorClock.Now-mirrorReceived>maxAge)return false;
            ViveLegacyWozObservationOwner.Candidate candidate;
            if(!source.TryRead(mirror,mirrorReceived,out candidate))
            {if(source.Terminal)Terminate(source.LastIssue);return false;}
            if(!owner.IsCurrent(binding)){Terminate("task_changed_during_poll");return false;}
            if(controlMode&&!AuthorizedMemoryPoseCurrent()){Terminate("preflight_changed_during_pose_poll");return false;}
            long sequence;if(!Next(false,out sequence))return false;
            string error;byte[] bytes;
            var observations=new WozLanPoseObservation(mirrorReceived,candidate.HeadObserved,candidate.EyeObserved);
            var local=candidate.Sample;
            // Keep fixture origin explicit even inside the authenticated bytes.
            // The local source adapter's channel label is not installed evidence.
            var synthetic=new HeadEyeSample(local.SessionId,context.Operator.SourceId,local.DeviceId,local.ClockId,local.BlockIndex,local.Segment,
                local.Sequence,local.CaptureLocalSeconds,local.SendLocalSeconds,ControlSource.Simulator,local.Role,
                local.RawHeadRotation,local.RawEyeDirection,local.HeadReportedValid,local.EyeReportedValid);
            if(!operatorEndpoint.TryProtect(WirePacket.Tracking(context.Operator,sequence,synthetic),mirror,observations,out bytes,out error))
            {Terminate(error);return false;}
            protectedCandidate=candidate;
#if STROOP_TEST_BUILD
            LastSourceCandidateForTest=candidate;LastPoseBytesForTest=(byte[])bytes.Clone();
            var hook=BeforePoseDeliveryForTest;BeforePoseDeliveryForTest=null;if(hook!=null)hook();
#endif
            return ReceiveMemoryPose(bytes);
        }
        bool ReceiveMemoryPose(byte[] bytes)
        {
            output=null;if(!Active())return false;
            if(!owner.IsCurrent(binding)){Terminate("task_changed_before_pose_use");return false;}
            if(controlMode&&!AuthorizedMemoryPoseCurrent()){Terminate("preflight_changed_before_pose_use");return false;}
            if(!CausalReturnFresh()||!OriginalObservationsFresh()){LastIssue="original_observation_expired";return false;}
            double received=participantClock.Now;WozAuthenticatedLanEndpoint.VerifiedMessage verified;string error;
            if(!participantEndpoint.TryReceive(bytes,received,participantClock.Now,published,out verified,out error)){LastIssue=error;return false;}
            if(verified.Packet.Kind!=WireKind.HeadEye||!owner.IsCurrent(binding)||!CausalReturnFresh()||!OriginalObservationsFresh()||
                verified.Packet.Sample.Sequence!=protectedCandidate.Sample.Sequence){Terminate("unexpected_memory_pose");return false;}
            if(controlMode&&!AuthorizedMemoryPoseCurrent()){Terminate("preflight_lost_before_pose_issue");return false;}
            output=HmacMemoryReceipt.Issue(this,receiptToken,verified.Packet.Sample,received);return true;
        }
        // Acknowledgment proves the owned task is actually paused/ended and
        // this memory output is cleared. There is no external renderer callback.
        public bool RequestOperatorSafetyStop()
        {
            output=null;if(!Active())return false;
            long sequence;if(!Next(false,out sequence))return false;
            string request=Guid.NewGuid().ToString("N"),error;byte[] bytes;
            if(!operatorEndpoint.TryProtect(WirePacket.SafetyStopRequest(context.Operator,sequence,operatorClock.Now,request),null,null,out bytes,out error))
            {Terminate(error);return false;}
            WozAuthenticatedLanEndpoint.VerifiedMessage verified;double received=participantClock.Now;
            if(!participantEndpoint.TryReceive(bytes,received,participantClock.Now,null,out verified,out error)||verified.Packet.Kind!=WireKind.SafetyStopRequest)
            {Terminate("stop_authentication_failed");return false;}
            bool paused=owner.PauseAuthoritativeTask();output=null;
            bool sourceClosed=CloseSource();
            if(!paused||!sourceClosed){Terminate("stop_or_cleanup_not_confirmed");return false;}
            if(!Next(true,out sequence))return false;
            if(!participantEndpoint.TryProtect(WirePacket.SafetyStopAck(context.Participant,sequence,participantClock.Now,request),null,null,out bytes,out error))
            {Terminate(error);return false;}
            received=operatorClock.Now;
            bool acknowledged=operatorEndpoint.TryReceive(bytes,received,operatorClock.Now,null,out verified,out error)&&
                verified.Packet.Kind==WireKind.SafetyStopAck&&verified.Packet.StopRequestId==request;
            StopAcknowledged=acknowledged;Terminate(acknowledged?"operator_stop_applied":"stop_ack_failed");return acknowledged;
        }
        public bool StopAcknowledged {get;private set;}
        bool CloseSource()
        {
            if(closed)return true;
            try
            {
#if STROOP_TEST_BUILD
                if(FailCloseForTest)throw new InvalidOperationException("Sealed cleanup failure fixture");
#endif
                if(source!=null)source.Dispose();closed=true;return true;
            }
            catch{return false;}
        }
        public void Revoke(){Terminate("authorization_revoked");}
        void Terminate(string reason)
        {
            if(!OwnerThread())throw new InvalidOperationException("Legacy bridge safety and cleanup must run on the task owner thread.");
            if(terminal)return;
            terminal=true;LastIssue=reason;output=null;mirror=null;published=null;binding=null;
            TerminateControl();
            if(!owner.PauseAuthoritativeTask()){LastIssue="authoritative_pause_not_confirmed";owner.MarkCleanupUncertain();}
            owner.Dispose();
            if(!CloseSource()){LastIssue="source_close_uncertain";owner.MarkCleanupUncertain();}
            participantEndpoint.Dispose();operatorEndpoint.Dispose();
        }
        public void Dispose(){Terminate("closed_requires_fresh_pairing");}
#if STROOP_TEST_BUILD
        internal Action BeforeTaskDeliveryForTest,BeforePoseDeliveryForTest;
        internal bool FailCloseForTest;
        internal byte[] LastPoseBytesForTest;
        internal ViveLegacyWozObservationOwner.Candidate LastSourceCandidateForTest;
        internal bool ReceiveForTest(byte[] bytes){return ReceiveMemoryPose(bytes);}
        internal void ExhaustPacketsForTest(){operatorSequence=Int64.MaxValue;}
        internal void ExhaustEmissionsForTest(){source.ExhaustForTest();}
        internal static ViveLegacyWozBridge CreateSealedFixture(ViveLegacyWozTaskOwner owner,MonotonicClock participantClock,
            MonotonicClock operatorClock,ViveLegacyWozObservationOwner.SyntheticObservations observations,double maximumAge)
        {return new ViveLegacyWozBridge(owner,participantClock,operatorClock,
            epoch=>ViveLegacyWozObservationOwner.BindSynthetic(owner.SessionId,epoch,operatorClock,observations,maximumAge),maximumAge);}
#endif
    }
}
