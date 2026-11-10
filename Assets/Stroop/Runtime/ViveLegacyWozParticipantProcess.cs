using System;
using System.Net;
using System.Collections.Generic;
using System.Globalization;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // The participant process holds ONLY its actual task owner/local clock.
    // Remote acquisition facts are authenticated assertions, never local hardware
    // evidence. Neither an imported token nor a packet creates local consent.
    public sealed class ViveLegacyWozParticipantProcess : IDisposable
    {
        internal const double ControlledVisualAge=.15,ControlledObservationAge=.10,ControlledRenewalInterval=.05;
        readonly ViveLegacyWozTaskOwner owner;
        readonly ViveLegacyWozTaskOwner.SourceChoice choice;
        readonly ViveLegacyWozProcessTransport transport;
        readonly double maxAge;
        ViveLegacyWozTaskOwner.Binding binding;
        WozLanTaskContext task;
        WozControlBinding grant;
        WozLanTaskContext retiredTask;
        WozControlBinding retiredGrant;
        Evidence retainedPreflight,retainedPose;
        long retainedRevision=-1;
        bool retainedAcknowledged;
        bool retainedNeedsSource;
        double retainedTaskIssued;
        bool maintained,pendingCycle;
        double cycleDeadline;
        ViveLegacyWozControlledScene scene;
        string challenge,revoke;
        string confirmChallenge;
        bool taskAcknowledged;
        double challengeIssued,revokeIssued;
        Evidence preflight,pose;
        bool terminal,locallyPaused;
        public bool Terminal {get{return terminal;}}
        public string LastIssue {get;private set;}
        public WozDiagnosticFailure LastTransportFailure {get{return transport.Failure;}}
        public bool SourceRevokeAcknowledged {get;private set;}
        public bool SafetyStopAcknowledged {get;private set;}
        public bool CanEnterLiveCountdown {get{return false;}}
        public bool CanPresentLivePose {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public bool LocalChoiceCurrent {get{return !terminal&&owner.IsSourceChoiceCurrent(choice);}}
        internal ViveLegacyWozTaskOwner ActualOwner {get{return owner;}}
        internal bool IsInstalledChannel {get{return transport.Context.Operator.Source==ControlSource.LiveOperator;}}
        internal bool UsesOwnedSystemSocket {get{return transport.UsesOwnedSystemSocket;}}
        internal string Mapping {get{return transport.Context.Mapping;}}
        internal bool CurrentTaskContext {get{return Guard(Current);}}
        internal bool CurrentTaskAcknowledged {get{return Guard(()=>Current()&&taskAcknowledged);}}
        bool RetainedCurrent
        {get{return maintained&&Guard(()=>Current()&&binding.WireRevision==retainedRevision&&retainedAcknowledged&&
            transport.Now-retainedTaskIssued<=ControlledVisualAge&&(!retainedNeedsSource||ControlledEvidenceFresh(retainedPreflight)));}}
        internal bool DisplayTaskAcknowledged {get{return CurrentTaskAcknowledged||RetainedCurrent;}}
        internal Evidence DisplayPreflight {get{var current=CurrentPreflight;return ControlledEvidenceFresh(current)?current:RetainedCurrent?retainedPreflight:null;}}
        internal Evidence DisplayPose {get{var current=CurrentPose;return ControlledEvidenceFresh(current)?current:
            RetainedCurrent&&ControlledEvidenceFresh(retainedPose)?retainedPose:null;}}
        internal void AttachScene(ViveLegacyWozControlledScene value)
        {OwnerThread();if(scene!=null||value==null||!Object.ReferenceEquals(value.Owner,owner))throw new InvalidOperationException("One exact owned scene required");scene=value;}
        internal bool ControlledPhaseProofCurrent
        {get{var proof=CurrentPreflight;return IsInstalledChannel&&proof is SocketEvidence&&!proof.SyntheticObservationsOnly&&ControlledEvidenceFresh(proof);}}
        internal bool ControlledEvidenceFresh(Evidence value)
        {return value!=null&&Guard(()=>transport.Now-value.OriginalTaskIssuedParticipantSeconds<=ControlledVisualAge&&
            transport.Now-value.OriginalReceiptParticipantSeconds<=ControlledVisualAge&&
            value.OriginalSendOperatorSeconds-value.OriginalHeadObservedOperatorSeconds<=ControlledObservationAge&&
            value.OriginalSendOperatorSeconds-value.OriginalEyeObservedOperatorSeconds<=ControlledObservationAge);}
        public Evidence CurrentPreflight {get{if(!Guard(()=>Current()&&FreshPreflight()))preflight=null;return preflight;}}
        public Evidence CurrentPose {get{if(!Guard(()=>Current()&&FreshPreflight()&&task.Task.Phase==Phase.MainTask))pose=null;return pose;}}

        // Immutable historical evidence. Socket and memory origins cannot be
        // interchanged. No public timestamp/provider/bool constructor exists.
        public abstract class Evidence
        {
            internal readonly ViveLegacyWozProcessTransport.Receipt OriginalReceipt;
            public string Epoch {get;private set;}
            public string ParticipantClockId {get;private set;}
            public string OperatorClockId {get;private set;}
            public string SourceBinding {get;private set;}
            public long TaskRevision {get;private set;}
            public long TaskPacketSequence {get;private set;}
            public long ChoiceGeneration {get;private set;}
            public Phase Phase {get;private set;}
            public double OriginalTaskIssuedParticipantSeconds {get;private set;}
            public double OriginalChallengeIssuedParticipantSeconds {get;private set;}
            public double OriginalReceiptParticipantSeconds {get;private set;}
            public double OriginalHeadObservedOperatorSeconds {get;private set;}
            public double OriginalEyeObservedOperatorSeconds {get;private set;}
            public double OriginalSendOperatorSeconds {get;private set;}
            public HeadEyeSample Pose {get;private set;}
            public bool PlaintextSharedKeyPossession {get{return true;}}
            public bool SyntheticObservationsOnly {get;private set;}
            public abstract bool ReceiptCapturedBySocket {get;}
            public bool InstalledSdkPathVerifiedAtParticipant {get{return false;}}
            public bool HardwareTrackingValidated {get{return false;}}
            public bool IsExperimentAdmission {get{return false;}}
            internal Evidence(ViveLegacyWozParticipantProcess process,ViveLegacyWozProcessTransport.Receipt receipt)
            {
                if(!process.transport.Owns(receipt)||!process.Current())throw new InvalidOperationException("Owned current receipt required");
                OriginalReceipt=receipt;
                var context=process.transport.Context;
                Epoch=context.Epoch;ParticipantClockId=context.Participant.ClockId;OperatorClockId=context.Operator.ClockId;
                SourceBinding=context.Operator.SourceId;SyntheticObservationsOnly=context.Operator.Source==ControlSource.Simulator;
                TaskRevision=process.task.Revision;TaskPacketSequence=process.task.ParticipantSequence;ChoiceGeneration=process.choice.Generation;
                Phase=process.task.Task.Phase;OriginalTaskIssuedParticipantSeconds=process.task.ParticipantSentSeconds;
                OriginalChallengeIssuedParticipantSeconds=process.challengeIssued;OriginalReceiptParticipantSeconds=receipt.OriginalReceiptSeconds;
                if(receipt.Control!=null)
                {var m=receipt.Control.Message;OriginalSendOperatorSeconds=m.SentSeconds;OriginalHeadObservedOperatorSeconds=m.HeadObservedOperatorSeconds.Value;OriginalEyeObservedOperatorSeconds=m.EyeObservedOperatorSeconds.Value;}
                else
                {Pose=receipt.Data.Packet.Sample;OriginalSendOperatorSeconds=receipt.Data.Packet.SentLocalSeconds;OriginalHeadObservedOperatorSeconds=receipt.Data.PoseObservation.HeadObservedOperatorSeconds;
                    OriginalEyeObservedOperatorSeconds=receipt.Data.PoseObservation.EyeObservedOperatorSeconds;}
            }
        }
        public sealed class SocketEvidence:Evidence
        {
            public override bool ReceiptCapturedBySocket {get{return true;}}
            internal SocketEvidence(ViveLegacyWozParticipantProcess p,ViveLegacyWozProcessTransport.SocketReceipt r):base(p,r){}
        }
        public sealed class MemoryEvidence:Evidence
        {
            public override bool ReceiptCapturedBySocket {get{return false;}}
            internal MemoryEvidence(ViveLegacyWozParticipantProcess p,ViveLegacyWozProcessTransport.MemoryReceipt r):base(p,r){}
        }
        Evidence Issue(ViveLegacyWozProcessTransport.Receipt receipt)
        {var socket=receipt as ViveLegacyWozProcessTransport.SocketReceipt;return socket!=null?(Evidence)new SocketEvidence(this,socket):new MemoryEvidence(this,(ViveLegacyWozProcessTransport.MemoryReceipt)receipt);}

        ViveLegacyWozParticipantProcess(ViveLegacyWozProcessPairing pairing,ViveLegacyWozTaskOwner owner,ViveLegacyWozProcessTransport transport,double age)
        {
            this.owner=owner;this.transport=transport;maxAge=age;
            try{if(owner==null||!transport.UsesClock(owner.Clock))throw new InvalidOperationException("Exact actual owner clock required");choice=pairing.ClaimParticipant(owner);
                if(owner.RecordingBinding!=null)owner.RecordingBinding.AttachProcess(this);}
            catch{transport.Dispose();if(choice!=null){if(!owner.PauseAuthoritativeTask())owner.MarkCleanupUncertain();owner.Dispose();}throw;}
        }
        public static ViveLegacyWozParticipantProcess Create(ViveLegacyWozProcessPairing pairing,ViveLegacyWozTaskOwner owner,IPEndPoint local,IPEndPoint remote,double maximumAge)
        {
            if(owner==null)throw new ArgumentNullException("owner");
            return new ViveLegacyWozParticipantProcess(pairing,owner,new ViveLegacyWozProcessTransport(pairing,PeerRole.Participant,owner.Clock,local,remote,maximumAge),maximumAge);
        }
        void OwnerThread(){if(!owner.OnOwnerThread)throw new InvalidOperationException("Participant owner thread required");}
        bool Guard(Func<bool> action)
        {OwnerThread();try{return action();}catch(Exception e){End("local_failure:"+e.GetType().Name);return false;}}
        public bool Start(){return Guard(()=>{if(terminal)return false;if(!transport.Start()){End("transport_start_failed");return false;}return true;});}
        bool Active()
        {
            OwnerThread();if(terminal||locallyPaused)return false;
            // Actual Completed has no positive network authority. Hold only
            // until the exact raw owner requests its final drain; other Ended
            // states still take the ordinary irrevocable safety path.
            if(owner.RecordingBinding!=null&&owner.RecordingBinding.CompletedTaskObserved)return false;
            if(owner.RecordingBinding!=null&&!owner.RecordingBinding.SceneAuthorityCurrent){End("local_recording_authority_lost");return false;}
            if(transport.Terminal){End(transport.LastIssue);return false;}
            if(!owner.IsSourceChoiceCurrent(choice)){End("local_choice_lost");return false;}return transport.Running;
        }
        bool Current()
        {
            if(!Active()||task==null||!RebindSameWireContext()){preflight=null;pose=null;return false;}
            if(transport.Now-task.ParticipantSentSeconds>maxAge){End("original_task_age_expired");return false;}return true;
        }
        bool RebindSameWireContext()
        {
            if(owner.IsCurrent(binding))return true;
            if(binding==null||!owner.MutationIdle)return false;
            ViveLegacyWozTaskOwner.Binding next;
            if(!owner.TryCapture(out next)||next.WireRevision!=binding.WireRevision||next.SourceGeneration!=binding.SourceGeneration||
                next.SourceOwner!=binding.SourceOwner||next.Selection!=binding.Selection)return false;
            binding=next;return true;
        }
        bool FreshPreflight()
        {return preflight!=null&&transport.Now-preflight.OriginalChallengeIssuedParticipantSeconds<=maxAge&&preflight.TaskPacketSequence==task.ParticipantSequence;}
        static bool PhaseAllowed(Phase phase){return phase==Phase.BlockReady||phase==Phase.Countdown||phase==Phase.MainTask||phase==Phase.Questionnaire||phase==Phase.Rest;}
        internal static bool NeedsSource(TaskSnapshot value)
        {return value!=null&&value.Condition!=Condition.D&&(value.Phase==Phase.BlockReady||value.Phase==Phase.Countdown||value.Phase==Phase.MainTask);}
        public bool PublishCurrentTask()
        {return Guard(PublishCore);}
        bool PublishCore()
        {
            if(maintained&&binding!=null&&RebindSameWireContext()&&taskAcknowledged)
            {retainedRevision=binding.WireRevision;retainedPreflight=preflight;retainedPose=pose;retainedAcknowledged=true;
                retainedTaskIssued=task.ParticipantSentSeconds;retainedNeedsSource=NeedsSource(task.Task);}
            if(!Active())return false;retiredTask=task;retiredGrant=grant;preflight=null;pose=null;challenge=null;taskAcknowledged=false;
            ViveLegacyWozTaskOwner.Binding current;if(!owner.TryCapture(out current)||!PhaseAllowed(current.Task.Phase))return false;
            WozLanTaskContext sent;if(!transport.SendTask(current,out sent)){End("task_send_failed");return false;}
            if(!owner.IsCurrent(current)){End("task_changed_during_send");return false;}binding=current;task=sent;return true;
        }
        public bool ConfirmCurrentSource()
        {return Guard(ConfirmCore);}
        bool ConfirmCore()
        {
            if(!Current())return false;
            var candidate=new WozControlBinding(task,choice.SourceOwner,choice.SourceGeneration,choice.Generation);
            preflight=null;pose=null;challenge=null;
            confirmChallenge=Guid.NewGuid().ToString("N");
            if(!transport.SendControl(WozControlKind.SourceConfirm,candidate,confirmChallenge)||!Current())
            {End("source_confirm_send_failed");return false;}grant=candidate;return true;
        }
        public bool BeginPhasePreflight()
        {return Guard(PreflightCore);}
        bool PreflightCore()
        {
            if(!Current()||!NeedsSource(task.Task)||grant==null||!Object.ReferenceEquals(grant.TaskContext,task))return false;
            preflight=null;pose=null;challenge=Guid.NewGuid().ToString("N");challengeIssued=transport.Now;
            if(!transport.SendControl(WozControlKind.PreflightChallenge,grant,challenge)||!Current())
            {End("preflight_send_failed");return false;}return true;
        }
        // Pump only on the owner thread. A configured-peer integrity failure
        // terminates the epoch; foreign endpoints consume bounded drain slots.
        public void Tick()
        {Guard(()=>{TickCore();return !terminal;});}
        void TickCore()
        {
            OwnerThread();if(terminal)return;
            if(revoke==null&&!Active())return;
            if(task!=null&&revoke==null&&transport.Now-task.ParticipantSentSeconds>maxAge){End("original_task_age_expired");return;}
            if(challenge!=null&&transport.Now-challengeIssued>maxAge){End("original_challenge_age_expired");return;}
            if(revoke!=null&&transport.Now-revokeIssued>maxAge){End("source_revoke_ack_timeout");return;}
            for(int i=0;i<ViveLegacyWozProcessTransport.MaximumDrain&&!terminal;i++)
            {
                ViveLegacyWozProcessTransport.Receipt receipt;
                if(!transport.Receive(revoke!=null&&grant!=null?grant.TaskContext:task,grant,grant,out receipt,retiredTask,retiredGrant))break;if(receipt==null)continue;
                if(revoke==null&&task!=null&&((receipt.Control!=null&&(receipt.Control.Message.Kind==WozControlKind.PreflightObservation||receipt.Control.Message.Kind==WozControlKind.TaskReceipt)&&
                    receipt.Control.Message.Binding.TaskContext.ParticipantSequence!=task.ParticipantSequence)||
                    (receipt.Data!=null&&receipt.Data.Packet.Kind==WireKind.HeadEye&&receipt.Data.TaskContext.ParticipantSequence!=task.ParticipantSequence)))continue;
                // Already in the irrevocable negative state: authenticated
                // in-flight positives from the stored grant cannot restore it
                // and must not prevent receiving the later cleanup ack.
                if(revoke!=null&&((receipt.Data!=null&&receipt.Data.Packet.Kind==WireKind.HeadEye)||
                    (receipt.Control!=null&&(receipt.Control.Message.Kind==WozControlKind.PreflightObservation||receipt.Control.Message.Kind==WozControlKind.TaskReceipt))))continue;
                if(receipt.Control!=null)
                {
                    var m=receipt.Control.Message;
                    if(m.Kind==WozControlKind.SourceRevokeAck&&revoke!=null&&m.Challenge==revoke&&locallyPaused)
                    {SourceRevokeAcknowledged=true;End("source_revoked_safe_ack");break;}
                    if(m.Kind==WozControlKind.TaskReceipt&&m.Challenge==confirmChallenge&&Current())
                    {if(!RecordNetworkEvidence("TaskReceipt",receipt,null)){End("recording_evidence_failed");break;}
                        taskAcknowledged=true;if(!NeedsSource(task.Task))pendingCycle=false;continue;}
                    if(m.Kind!=WozControlKind.PreflightObservation||challenge==null||m.Challenge!=challenge||!Current()||transport.Now-challengeIssued>maxAge)
                    {End("unexpected_or_stale_preflight");break;}
                    var accepted=Issue(receipt);
                    if(!RecordNetworkEvidence("Preflight",receipt,accepted)){End("recording_evidence_failed");break;}
                    preflight=accepted;challenge=null;pendingCycle=false;
                }
                else if(receipt.Data.Packet.Kind==WireKind.SafetyStopRequest)
                {
                    string request=receipt.Data.Packet.StopRequestId;
                    // Safety does not enter or wait for the ordinary recording
                    // reservation. Revocation may discard the negative tail.
                    if(SafeLocal())SafetyStopAcknowledged=transport.SendStop(true,request);
                    End(SafetyStopAcknowledged?"operator_stop_safe_ack":"operator_stop_unconfirmed");
                }
                else if(receipt.Data.Packet.Kind==WireKind.HeadEye&&Current()&&FreshPreflight()&&task.Task.Phase==Phase.MainTask&&
                    receipt.Data.PoseObservation.HeadObservedOperatorSeconds>preflight.OriginalHeadObservedOperatorSeconds&&
                    receipt.Data.PoseObservation.EyeObservedOperatorSeconds>preflight.OriginalEyeObservedOperatorSeconds)
                {var accepted=Issue(receipt);if(!RecordNetworkEvidence("Pose",receipt,accepted)){End("recording_evidence_failed");break;}pose=accepted;}
                else End("unexpected_or_unauthorized_data");
            }
            if(!terminal&&transport.Terminal)End(transport.LastIssue);
        }
        public bool RevokeSource()
        {return Guard(RevokeCore);}
        bool RevokeCore()
        {
            OwnerThread();if(terminal||revoke!=null)return false;
            if(!SafeLocal()){End("local_pause_uncertain");return false;}
            if(grant==null){End("revoked_before_grant");return false;}
            revoke=Guid.NewGuid().ToString("N");revokeIssued=transport.Now;
            if(!transport.SendControl(WozControlKind.SourceRevoke,grant,revoke)){End("source_revoke_send_failed");return false;}return true;
        }
        bool SafeLocal()
        {
            bool recordingStopped=true;
            try{if(owner.RecordingBinding!=null)owner.RecordingBinding.Stop();}
            catch{recordingStopped=false;owner.MarkCleanupUncertain();}
            preflight=null;pose=null;retainedPreflight=null;retainedPose=null;challenge=null;binding=null;task=null;owner.RevokeSourceChoice(choice);
            if(!locallyPaused)locallyPaused=owner.PauseAuthoritativeTask();
            bool cleared=scene==null||scene.ClearForSafety();
            if(!locallyPaused||!cleared)owner.MarkCleanupUncertain();return recordingStopped&&locallyPaused&&cleared;
        }
        void End(string reason)
        {
            if(terminal)return;OwnerThread();LastIssue=reason;
            SafeLocal();terminal=true;transport.Dispose();
            if(transport.CleanupUncertain)owner.MarkCleanupUncertain();owner.Dispose();
        }
        public void Dispose(){OwnerThread();End("disposed");}
        internal bool CloseForRecordedCompletedDrain(ViveLegacyWozRecordingBinding recording)
        {
            OwnerThread();if(terminal||!Object.ReferenceEquals(recording,owner.RecordingBinding)||!recording.CompletedTaskObserved||!owner.Participant.Completed)return false;
            preflight=null;pose=null;retainedPreflight=null;retainedPose=null;challenge=null;binding=null;task=null;owner.RevokeSourceChoice(choice);
            if(scene!=null&&!scene.ClearForSafety()){owner.MarkCleanupUncertain();return false;}
            transport.Dispose();if(transport.CleanupUncertain){owner.MarkCleanupUncertain();return false;}
            terminal=true;LastIssue="actual_completed_recording_drain_pending";return true;
        }
        // Local journal projection only: never serialize packet bytes, tokens,
        // keys or participant measurements. Absence is explicit; role clocks
        // retain their original values and are never subtracted across roles.
        bool RecordNetworkEvidence(string kind,ViveLegacyWozProcessTransport.Receipt receipt,Evidence value,string failure="NOT_APPLICABLE")
        {
            var recording=owner.RecordingBinding;if(recording==null)return true;
            var fields=NetworkEvidenceFields(kind,receipt,value,failure);
            return fields!=null&&recording.Record(this,fields);
        }
        internal IReadOnlyDictionary<string,string> CapturePresentationFields(Evidence value)
        {return value!=null&&Object.ReferenceEquals(value,DisplayPose)?NetworkEvidenceFields("PoseUsed",value.OriginalReceipt,value,"NOT_APPLICABLE"):null;}
        Dictionary<string,string> NetworkEvidenceFields(string kind,ViveLegacyWozProcessTransport.Receipt receipt,Evidence value,string failure)
        {
            if(receipt!=null&&!transport.Owns(receipt))return null;
            var c=transport.Context;const string missing="NOT_OBSERVED";
            var fields=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(string name in new[]{"schema","event","epoch","source_binding","mapping","participant_clock_id","operator_clock_id",
                "task_revision","task_packet_sequence","choice_generation","source_generation","remote_sequence","receipt_origin","control_source",
                "authentication","hardware_identity","clock_relation","synthetic","task_issued_participant_time","challenge_issued_participant_time",
                "receipt_participant_time","send_operator_time","head_operator_time","eye_operator_time","epoch_sample_sequence",
                "head_qx","head_qy","head_qz","head_qw","eye_dx","eye_dy","eye_dz","failure_reason"})fields.Add("woz_"+name,missing);
            fields["woz_schema"]="vive-woz-local-evidence-v1";fields["woz_event"]=kind;fields["woz_epoch"]=c.Epoch;
            fields["woz_source_binding"]=c.Operator.SourceId;fields["woz_mapping"]=c.Mapping;
            fields["woz_participant_clock_id"]=c.Participant.ClockId;fields["woz_operator_clock_id"]=c.Operator.ClockId;
            fields["woz_choice_generation"]=choice.Generation.ToString(CultureInfo.InvariantCulture);
            fields["woz_source_generation"]=choice.SourceGeneration.ToString(CultureInfo.InvariantCulture);
            fields["woz_control_source"]=c.Operator.Source.ToString();fields["woz_hardware_identity"]="NOT_VERIFIED";
            fields["woz_clock_relation"]="INDEPENDENT_NOT_SYNCHRONIZED";fields["woz_failure_reason"]=failure;
            fields["woz_receipt_origin"]=receipt==null?"LOCAL_ONLY":receipt.SocketCaptured?"SOCKET":"MEMORY";
            fields["woz_authentication"]=receipt==null?"NONE":"HMAC_SHARED_KEY_POSSESSION";
            fields["woz_synthetic"]=receipt==null?"NOT_APPLICABLE":c.Operator.Source==ControlSource.Simulator?"TRUE":"FALSE";
            if(task!=null){fields["woz_task_revision"]=task.Revision.ToString(CultureInfo.InvariantCulture);
                fields["woz_task_packet_sequence"]=task.ParticipantSequence.ToString(CultureInfo.InvariantCulture);}
            if(receipt!=null)
            {
                fields["woz_remote_sequence"]=(receipt.Control!=null?receipt.Control.Message.Sequence:receipt.Data.Packet.Sequence).ToString(CultureInfo.InvariantCulture);
                fields["woz_receipt_participant_time"]=ViveLegacyWozRecordingSchema.EncodeTime(receipt.OriginalReceiptSeconds);
                fields["woz_send_operator_time"]=ViveLegacyWozRecordingSchema.EncodeTime(receipt.Control!=null?receipt.Control.Message.SentSeconds:receipt.Data.Packet.SentLocalSeconds);
                if(task!=null)fields["woz_task_issued_participant_time"]=ViveLegacyWozRecordingSchema.EncodeTime(task.ParticipantSentSeconds);
            }
            if(value!=null)
            {
                // Retained display evidence may belong to the preceding task
                // packet of the same semantic context. Never relabel it with
                // the newer packet or refresh its original issued time.
                fields["woz_task_revision"]=value.TaskRevision.ToString(CultureInfo.InvariantCulture);
                fields["woz_task_packet_sequence"]=value.TaskPacketSequence.ToString(CultureInfo.InvariantCulture);
                fields["woz_task_issued_participant_time"]=ViveLegacyWozRecordingSchema.EncodeTime(value.OriginalTaskIssuedParticipantSeconds);
                fields["woz_challenge_issued_participant_time"]=ViveLegacyWozRecordingSchema.EncodeTime(value.OriginalChallengeIssuedParticipantSeconds);
                fields["woz_head_operator_time"]=ViveLegacyWozRecordingSchema.EncodeTime(value.OriginalHeadObservedOperatorSeconds);
                fields["woz_eye_operator_time"]=ViveLegacyWozRecordingSchema.EncodeTime(value.OriginalEyeObservedOperatorSeconds);
                if(value.Pose!=null){var p=value.Pose;fields["woz_epoch_sample_sequence"]=p.Sequence.ToString(CultureInfo.InvariantCulture);
                    fields["woz_head_qx"]=ViveLegacyWozRecordingSchema.EncodeNumber(p.RawHeadRotation.X);fields["woz_head_qy"]=ViveLegacyWozRecordingSchema.EncodeNumber(p.RawHeadRotation.Y);
                    fields["woz_head_qz"]=ViveLegacyWozRecordingSchema.EncodeNumber(p.RawHeadRotation.Z);fields["woz_head_qw"]=ViveLegacyWozRecordingSchema.EncodeNumber(p.RawHeadRotation.W);
                    fields["woz_eye_dx"]=ViveLegacyWozRecordingSchema.EncodeNumber(p.RawEyeDirection.X);fields["woz_eye_dy"]=ViveLegacyWozRecordingSchema.EncodeNumber(p.RawEyeDirection.Y);
                    fields["woz_eye_dz"]=ViveLegacyWozRecordingSchema.EncodeNumber(p.RawEyeDirection.Z);}
            }
            return fields;
        }
        // Explicitly called by the scene after its local start action. No
        // automatic connection, choice, resume or age refresh of old evidence.
        // A never-answered cycle keeps its original deadline across mutations.
        public void TickMaintainedPreflight()
        {Guard(()=>
        {
            if(terminal)return false;
            if(revoke!=null){TickCore();return !terminal;}
            if(!Active())return false;
            double now=transport.Now;
            if(maintained&&pendingCycle&&now>cycleDeadline){End("unanswered_preflight_cycle_timeout");return false;}
            bool changed=task==null||!RebindSameWireContext();
            bool renew=!pendingCycle&&task!=null&&now-task.ParticipantSentSeconds>=Math.Min(maxAge/3,ControlledRenewalInterval);
            if(!maintained||changed||renew)
            {
                if(!pendingCycle){cycleDeadline=now+maxAge;pendingCycle=true;}
                maintained=true;
                if(!PublishCore()||!ConfirmCore()){End("maintained_cycle_failed");return false;}
                if(NeedsSource(task.Task)){if(!PreflightCore()){End("maintained_preflight_failed");return false;}}
                // D and local ratings/rest wait for correlated TaskReceipt,
                // never invented eye evidence or local-send success.
            }
            TickCore();return !terminal;
        });}
#if STROOP_TEST_BUILD
        internal static ViveLegacyWozParticipantProcess Memory(ViveLegacyWozProcessPairing p,ViveLegacyWozTaskOwner owner,IPEndPoint local,IPEndPoint remote,double age,ViveLegacyWozProcessTransport.MemorySocket socket)
        {return new ViveLegacyWozParticipantProcess(p,owner,ViveLegacyWozProcessTransport.Memory(p,PeerRole.Participant,owner.Clock,local,remote,age,socket),age);}
        internal bool BeginCountdownForTest(ViveDevelopmentParticipant participant){return participant.BeginLegacyWozMemoryCountdown(owner,choice);}
#endif
    }
}
