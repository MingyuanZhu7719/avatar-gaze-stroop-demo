using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveLegacyWozBridge
    {
        WozAuthenticatedControlEndpoint participantControl,operatorControl;
        ViveLegacyWozTaskOwner.SourceChoice sourceChoice;
        WozControlBinding grantBinding;
        WozLanTaskContext grantedTask;
        long participantControlSequence,operatorControlSequence;
        bool controlMode,sourceConfirmed;
        string grantId,challenge;
        double challengeIssued,challengeReceived;
        PreflightMemoryReceipt preflight;
        public bool SourceRevokeAcknowledged {get;private set;}
        public bool LocalLiveWozChoiceCurrent {get{return OwnerThread()&&!terminal&&owner.IsSourceChoiceCurrent(sourceChoice);}}
        public bool SourceConfirmedForCurrentTask
        {get{return ControlCurrent()&&sourceConfirmed&&Object.ReferenceEquals(grantedTask,published)&&CausalReturnFresh();}}
        public PreflightMemoryReceipt CurrentMemoryPreflight
        {get{if(!PreflightCurrent())preflight=null;return preflight;}}

        // Only local software choice and authenticated memory evidence. These
        // facts are deliberately distinct from installed acquisition, calibration,
        // physical identity, actual socket origin, countdown or recording consent.
        public sealed class PreflightMemoryReceipt
        {
            public string Epoch {get;private set;}
            public string SourceBinding {get;private set;}
            public string Challenge {get;private set;}
            public long ChoiceGeneration {get;private set;}
            public long SourceGeneration {get;private set;}
            public long TaskRevision {get;private set;}
            public long TaskPacketSequence {get;private set;}
            public Phase Phase {get;private set;}
            public string ParticipantClockId {get;private set;}
            public string OperatorClockId {get;private set;}
            public double OriginalChallengeIssuedParticipantSeconds {get;private set;}
            public double OriginalChallengeReceivedOperatorSeconds {get;private set;}
            public double OriginalHeadObservedOperatorSeconds {get;private set;}
            public double OriginalEyeObservedOperatorSeconds {get;private set;}
            public double OriginalReceiptParticipantSeconds {get;private set;}
            public bool SyntheticObservationsOnly {get{return true;}}
            public bool PlaintextSharedKeyPossession {get{return true;}}
            public PeerRole SenderRole {get{return PeerRole.Operator;}}
            public PeerRole ReceiverRole {get{return PeerRole.Participant;}}
            public bool ReceiptCapturedBySocket {get{return false;}}
            public bool InstalledSdkPath {get{return false;}}
            public bool HardwareTrackingValidated {get{return false;}}
            public bool CanEnterLiveCountdown {get{return false;}}
            public bool CanRecordOrExport {get{return false;}}
            private PreflightMemoryReceipt(ViveLegacyWozBridge bridge,ViveLegacyWozObservationOwner.Candidate observation,double received)
            {
                Epoch=bridge.Epoch;SourceBinding=bridge.context.Operator.SourceId;Challenge=bridge.challenge;
                ChoiceGeneration=bridge.sourceChoice.Generation;TaskRevision=bridge.published.Revision;TaskPacketSequence=bridge.published.ParticipantSequence;
                SourceGeneration=bridge.sourceChoice.SourceGeneration;
                Phase=bridge.published.Task.Phase;ParticipantClockId=bridge.participantClock.ClockId;OperatorClockId=bridge.operatorClock.ClockId;
                OriginalChallengeIssuedParticipantSeconds=bridge.challengeIssued;OriginalChallengeReceivedOperatorSeconds=bridge.challengeReceived;
                OriginalHeadObservedOperatorSeconds=observation.HeadObserved;OriginalEyeObservedOperatorSeconds=observation.EyeObserved;
                OriginalReceiptParticipantSeconds=received;
            }
            internal static PreflightMemoryReceipt Issue(ViveLegacyWozBridge bridge,object token,ViveLegacyWozObservationOwner.Candidate observation,double received)
            {
                if(!Object.ReferenceEquals(token,bridge.receiptToken)||!bridge.SourceConfirmedForCurrentTask||bridge.challenge==null)
                    throw new InvalidOperationException("Owned phase-bound memory evidence required.");
                return new PreflightMemoryReceipt(bridge,observation,received);
            }
        }
        void InitializeControlEndpoints(byte[] key)
        {
            participantControl=new WozAuthenticatedControlEndpoint(context,PeerRole.Participant,key,maxAge);
            operatorControl=new WozAuthenticatedControlEndpoint(context,PeerRole.Operator,key,maxAge);
        }
        void InvalidatePhaseControl()
        {sourceConfirmed=false;grantedTask=null;challenge=null;preflight=null;}
        void TerminateControl()
        {
            InvalidatePhaseControl();owner.RevokeSourceChoice(sourceChoice);
            if(participantControl!=null)participantControl.Dispose();if(operatorControl!=null)operatorControl.Dispose();
        }
        bool ControlCurrent()
        {
            if(!OwnerThread()||!controlMode||terminal)return false;
            if(!owner.IsSourceChoiceCurrent(sourceChoice)){Terminate("local_source_choice_lost");return false;}
            if(!Active())return false;
            return published!=null&&owner.IsCurrent(binding)&&sourceChoice.Epoch==Epoch&&
                sourceChoice.Stream==context.Operator.SourceId&&sourceChoice.Mapping==context.Mapping;
        }
        bool PreflightCurrent()
        {
            return SourceConfirmedForCurrentTask&&preflight!=null&&preflight.TaskRevision==published.Revision&&
                preflight.TaskPacketSequence==published.ParticipantSequence&&preflight.Phase==published.Task.Phase&&
                participantClock.Now-preflight.OriginalChallengeIssuedParticipantSeconds<=maxAge&&
                operatorClock.Now-preflight.OriginalHeadObservedOperatorSeconds<=maxAge&&
                operatorClock.Now-preflight.OriginalEyeObservedOperatorSeconds<=maxAge;
        }
        bool AuthorizedMemoryPoseCurrent()
        {return PreflightCurrent()&&published.Task.Phase==Phase.MainTask;}
        bool NextControl(bool participant,out long sequence)
        {
            sequence=participant?participantControlSequence:operatorControlSequence;
            if(sequence==Int64.MaxValue){Terminate("control_sequence_exhausted");return false;}
            if(participant)participantControlSequence++;else operatorControlSequence++;return true;
        }
        // Explicit local UI/owner operation. A received message cannot call this
        // method or construct its registered SourceChoice. Automatic is rejected.
        public bool SelectAndConfirmLocalLiveWoz(Actor actor,string sourceOwner)
        {
            if(!Active()||controlMode)return false;
            sourceChoice=owner.SelectAndConfirmLocalSource(actor,sourceOwner,Epoch,context.Operator.SourceId,context.Mapping);
            controlMode=true;grantId=Guid.NewGuid().ToString("N");InvalidatePhaseControl();output=null;mirror=null;published=null;binding=null;
            return owner.IsSourceChoiceCurrent(sourceChoice);
        }
        static bool PreflightPhase(TaskSnapshot task)
        {return task!=null&&task.BlockIndex>=0&&task.Condition.HasValue&&task.Condition!=Condition.D&&
            (task.Phase==Phase.BlockReady||task.Phase==Phase.Countdown||task.Phase==Phase.MainTask);}
        public bool SendCurrentSourceConfirmation()
        {
            if(!ControlCurrent()||!PreflightPhase(published.Task)||!CausalReturnFresh()||sourceConfirmed)return false;
            var candidate=new WozControlBinding(published,sourceChoice.SourceOwner,sourceChoice.SourceGeneration,sourceChoice.Generation);
            long sequence;if(!NextControl(true,out sequence))return false;
            byte[] bytes;string error;
            if(!participantControl.TryProtect(new WozControlMessage(WozControlKind.SourceConfirm,sequence,participantClock.Now,candidate,grantId),out bytes,out error))
            {Terminate(error);return false;}
#if STROOP_TEST_BUILD
            LastControlBytesForTest=(byte[])bytes.Clone();InvokeControlHookForTest();
#endif
            if(!ControlCurrent()||!CausalReturnFresh()){Terminate("source_confirmation_stale");return false;}
            double receipt=operatorClock.Now;WozAuthenticatedControlEndpoint.VerifiedControl verified;
            if(!operatorControl.TryReceive(bytes,receipt,operatorClock.Now,candidate,out verified,out error)||
                verified.Message.Kind!=WozControlKind.SourceConfirm||verified.Message.Challenge!=grantId)
            {Terminate("source_confirmation_rejected:"+error);return false;}
            if(!ControlCurrent()||!CausalReturnFresh()){Terminate("source_choice_changed_during_confirmation");return false;}
            grantBinding=candidate;grantedTask=published;sourceConfirmed=true;return true;
        }
        public bool BeginPhasePreflight()
        {
            if(!SourceConfirmedForCurrentTask||!PreflightPhase(published.Task)||source==null)return false;
            preflight=null;output=null;challenge=Guid.NewGuid().ToString("N");challengeIssued=participantClock.Now;
            long sequence;if(!NextControl(true,out sequence))return false;
            byte[] bytes;string error;
            if(!participantControl.TryProtect(new WozControlMessage(WozControlKind.PreflightChallenge,sequence,challengeIssued,grantBinding,challenge),out bytes,out error))
            {Terminate(error);return false;}
#if STROOP_TEST_BUILD
            LastControlBytesForTest=(byte[])bytes.Clone();InvokeControlHookForTest();
#endif
            if(!SourceConfirmedForCurrentTask){Terminate("preflight_challenge_context_lost");return false;}
            double receipt=operatorClock.Now;WozAuthenticatedControlEndpoint.VerifiedControl verified;
            if(!operatorControl.TryReceive(bytes,receipt,operatorClock.Now,grantBinding,out verified,out error)||
                verified.Message.Kind!=WozControlKind.PreflightChallenge||verified.Message.Challenge!=challenge)
            {Terminate("preflight_challenge_rejected:"+error);return false;}
            challengeReceived=receipt;return true;
        }
        public bool PollPhasePreflight()
        {
            if(!ControlCurrent()||challenge==null||source==null||!sourceConfirmed)return false;
            preflight=null;output=null;
            string pendingChallenge=challenge;var pendingGrant=grantBinding;
            if(!CausalReturnFresh()||participantClock.Now-challengeIssued>maxAge||operatorClock.Now-challengeReceived>maxAge)
            {Terminate("preflight_timeout");return false;}
            ViveLegacyWozObservationOwner.Candidate candidate;
            if(!source.TryReadPreflight(published.Task,Math.Max(challengeReceived,mirrorReceived),out candidate))
            {if(source.Terminal)Terminate(source.LastIssue);return false;}
            if(challenge!=pendingChallenge||!Object.ReferenceEquals(grantBinding,pendingGrant))
            {Terminate("preflight_challenge_replaced_during_poll");return false;}
            if(!SourceConfirmedForCurrentTask){Terminate("preflight_task_changed_during_poll");return false;}
            long sequence;if(!NextControl(false,out sequence))return false;
            byte[] bytes;string error;
            var message=new WozControlMessage(WozControlKind.PreflightObservation,sequence,operatorClock.Now,grantBinding,challenge,
                challengeReceived,candidate.HeadObserved,candidate.EyeObserved);
            if(!operatorControl.TryProtect(message,out bytes,out error)){Terminate(error);return false;}
#if STROOP_TEST_BUILD
            LastControlBytesForTest=(byte[])bytes.Clone();InvokeControlHookForTest();
#endif
            if(!SourceConfirmedForCurrentTask||participantClock.Now-challengeIssued>maxAge||
                operatorClock.Now-candidate.HeadObserved>maxAge||operatorClock.Now-candidate.EyeObserved>maxAge)
            {Terminate("preflight_original_observation_expired");return false;}
            double receipt=participantClock.Now;WozAuthenticatedControlEndpoint.VerifiedControl verified;
            if(!participantControl.TryReceive(bytes,receipt,participantClock.Now,grantBinding,out verified,out error)||
                verified.Message.Kind!=WozControlKind.PreflightObservation||verified.Message.Challenge!=challenge||
                verified.Message.ChallengeReceivedOperatorSeconds!=challengeReceived||verified.Message.HeadObservedOperatorSeconds!=candidate.HeadObserved||
                verified.Message.EyeObservedOperatorSeconds!=candidate.EyeObserved)
            {Terminate("preflight_observation_rejected:"+error);return false;}
            if(!SourceConfirmedForCurrentTask||participantClock.Now-challengeIssued>maxAge)
            {Terminate("preflight_lost_before_issue");return false;}
            preflight=PreflightMemoryReceipt.Issue(this,receiptToken,candidate,receipt);challenge=null;return true;
        }
        public bool RevokeAuthenticatedSource()
        {
            if(!OwnerThread())throw new InvalidOperationException("Source revocation requires the task owner thread.");
            if(terminal||!controlMode)return false;
            // Negative authority is local and immediate, BEFORE any clock/MAC.
            owner.RevokeSourceChoice(sourceChoice);InvalidatePhaseControl();output=null;mirror=null;published=null;binding=null;
            bool paused=owner.PauseAuthoritativeTask(),clean=CloseSource();
            if(!paused||!clean){owner.MarkCleanupUncertain();Terminate("revoke_safety_or_cleanup_uncertain");return false;}
            if(grantBinding==null){Terminate("revoked_before_remote_confirmation");return false;}
            long sequence;if(!NextControl(true,out sequence))return false;
            byte[] bytes;string error;string revokeId=Guid.NewGuid().ToString("N");
            if(!participantControl.TryProtect(new WozControlMessage(WozControlKind.SourceRevoke,sequence,participantClock.Now,grantBinding,revokeId),out bytes,out error))
            {Terminate(error);return false;}
            // Stored grant binding, not the now-paused/newer task. A matching
            // negative grant can never be discarded as an obsolete task update.
            double receipt=operatorClock.Now;WozAuthenticatedControlEndpoint.VerifiedControl verified;
            if(!operatorControl.TryReceive(bytes,receipt,operatorClock.Now,grantBinding,out verified,out error)||verified.Message.Kind!=WozControlKind.SourceRevoke)
            {Terminate("source_revoke_rejected:"+error);return false;}
            if(!NextControl(false,out sequence))return false;
            if(!operatorControl.TryProtect(new WozControlMessage(WozControlKind.SourceRevokeAck,sequence,operatorClock.Now,grantBinding,revokeId),out bytes,out error))
            {Terminate(error);return false;}
            receipt=participantClock.Now;
            bool acknowledged=participantControl.TryReceive(bytes,receipt,participantClock.Now,grantBinding,out verified,out error)&&
                verified.Message.Kind==WozControlKind.SourceRevokeAck&&verified.Message.Challenge==revokeId;
            SourceRevokeAcknowledged=acknowledged;Terminate(acknowledged?"source_revoked_safe_ack":"source_revoke_ack_failed");return acknowledged;
        }
#if STROOP_TEST_BUILD
        internal byte[] LastControlBytesForTest;
        internal Action BeforeControlDeliveryForTest;
        void InvokeControlHookForTest(){var hook=BeforeControlDeliveryForTest;BeforeControlDeliveryForTest=null;if(hook!=null)hook();}
        internal bool InjectControlForTest(bool participantSender,byte[] bytes)
        {
            if(!Active()||grantBinding==null)return false;
            var endpoint=participantSender?operatorControl:participantControl;var clock=participantSender?operatorClock:participantClock;
            WozAuthenticatedControlEndpoint.VerifiedControl ignored;string error;double receipt=clock.Now;
            bool accepted=endpoint.TryReceive(bytes,receipt,clock.Now,grantBinding,out ignored,out error);
            // Test injection cannot mint positive runtime evidence. All unexpected
            // control, including a stale/replayed revoke, causes safe termination.
            Terminate("unexpected_control:"+(error??"unmatched_lifecycle"));return accepted;
        }
        internal void ExhaustControlForTest(){participantControlSequence=Int64.MaxValue;}
        internal void ReplaceSourceLeaseForTest(){source.Dispose();}
        internal bool BeginLiveMemoryCountdownForTest(ViveDevelopmentParticipant participant)
        {return participant.BeginLegacyWozMemoryCountdown(owner,sourceChoice);}
#endif
    }
}
