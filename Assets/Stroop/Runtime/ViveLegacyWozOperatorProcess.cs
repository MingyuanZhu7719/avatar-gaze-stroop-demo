using System;
using System.Net;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Role-local acquisition and explicit forwarding consent. This object has
    // no participant owner/clock/provider reference, including in memory tests.
    public sealed class ViveLegacyWozOperatorProcess : IDisposable
    {
        readonly Thread thread=Thread.CurrentThread;
        readonly ViveLegacyWozProcessTransport transport;
        readonly ViveLegacyWozObservationOwner source;
        readonly string sourceOwner;
        readonly long sourceGeneration,choiceGeneration;
        readonly double maxAge;
        double ObservationAge {get{return Math.Min(maxAge,ViveLegacyWozParticipantProcess.ControlledObservationAge);}}
        WozLanTaskContext task;
        WozControlBinding expected,grant,retiredExpected,previousExpected;
        string challenge,stop;
        double taskReceived,challengeReceived,stopIssued,proofHead,proofEye;
        bool consent,terminal,sourceClosed,cleanupUncertain,phaseProven;
        public bool Terminal {get{return terminal;}}
        public string LastIssue {get;private set;}
        public WozDiagnosticFailure LastTransportFailure {get{return transport.Failure;}}
        public bool SourceRevokeAcknowledged {get;private set;}
        public bool ParticipantStopAcknowledged {get;private set;}
        public bool CleanupUncertain {get{return cleanupUncertain||transport.CleanupUncertain;}}
        public bool UsesInstalledSdkPath {get;private set;}
        public bool HardwareTrackingValidated {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public bool ParticipantLocalRecordingDeclared {get;private set;}
        public TaskSnapshot CurrentTask {get{return Guard(Current)?task.Task:null;}}
        public bool SourceConfirmed {get{return Guard(()=>Current()&&grant!=null&&Object.ReferenceEquals(grant,expected));}}
        public bool PhasePreflightCurrent {get{return Guard(()=>SourceConfirmed&&phaseProven&&transport.Now-proofHead<=ObservationAge&&transport.Now-proofEye<=ObservationAge);}}
        ViveLegacyWozOperatorProcess(ViveLegacyWozProcessPairing pairing,MonotonicClock clock,ViveLegacyWozObservationOwner source,ViveLegacyWozProcessTransport transport,double age,bool installed)
        {
            this.source=source;this.transport=transport;maxAge=age;
            sourceOwner=pairing.SourceOwner;sourceGeneration=pairing.SourceGeneration;choiceGeneration=pairing.ChoiceGeneration;
            ParticipantLocalRecordingDeclared=pairing.ParticipantLocalRecordingDeclared;
            try
            {
                if(!source.UsesClock(clock)||!transport.UsesClock(clock)||!source.BindingCurrent)throw new InvalidOperationException("Exact local clock and owned source required");
                if(source.Mapping!=pairing.Context.Mapping||source.DeviceId!=pairing.Context.OperatorDevice||
                    (installed&&source.StreamId!=pairing.Context.Operator.SourceId))throw new InvalidOperationException("Owned source differs from pairing");
                pairing.ClaimOperator(clock);UsesInstalledSdkPath=installed;
            }
            catch{try{source.Dispose();}finally{transport.Dispose();}throw;}
        }
        public static ViveLegacyWozOperatorProcess BindInstalled(ViveLegacyWozProcessPairing pairing,MonotonicClock existingClock,ViveProEyeWorker runningInstalledWorker,IPEndPoint local,IPEndPoint remote,double maximumAge)
        {
            if(pairing==null||pairing.Context.Operator.Source!=ControlSource.LiveOperator)throw new InvalidOperationException("Installed process pairing required");
            var transport=new ViveLegacyWozProcessTransport(pairing,PeerRole.Operator,existingClock,local,remote,maximumAge);
            ViveLegacyWozObservationOwner source=null;
            try{source=ViveLegacyWozObservationOwner.BindInstalled(pairing.Context.Participant.SessionId,pairing.EpochLabel,existingClock,runningInstalledWorker,Math.Min(maximumAge,ViveLegacyWozParticipantProcess.ControlledObservationAge));
                return new ViveLegacyWozOperatorProcess(pairing,existingClock,source,transport,maximumAge,true);}
            catch{transport.Dispose();if(source!=null)source.Dispose();throw;}
        }
        void Owner(){if(!Object.ReferenceEquals(thread,Thread.CurrentThread))throw new InvalidOperationException("Operator owner thread required");}
        bool Guard(Func<bool> action)
        {Owner();try{return action();}catch(Exception e){End("local_failure:"+e.GetType().Name);return false;}}
        public bool Start(){return Guard(()=>{if(terminal)return false;if(!transport.Start()){End("transport_start_failed");return false;}return true;});}
        public bool ConfirmLocalOperatorSource()
        {Owner();if(ParticipantLocalRecordingDeclared||terminal||!source.BindingCurrent)return false;consent=true;return true;}
        // Separate explicit action after the recorded-mode disclosure. The old
        // forwarding-only action cannot silently opt into participant storage.
        public bool ConfirmForwardingAndParticipantLocalStorage()
        {Owner();if(!ParticipantLocalRecordingDeclared||terminal||!source.BindingCurrent)return false;consent=true;return true;}
        bool Active()
        {Owner();if(terminal||stop!=null||sourceClosed)return false;if(transport.Terminal||!source.BindingCurrent){End(transport.Terminal?transport.LastIssue:source.LastIssue);return false;}return transport.Running;}
        bool Current()
        {
            if(!Active()||task==null)return false;
            if(transport.Now-taskReceived>maxAge){End("original_task_receipt_expired");return false;}return true;
        }
        static bool PhaseAllowed(Phase phase){return phase==Phase.BlockReady||phase==Phase.Countdown||phase==Phase.MainTask||phase==Phase.Questionnaire||phase==Phase.Rest;}
        void ClearPositive(){expected=null;challenge=null;phaseProven=false;task=null;}
        public void Tick()
        {Guard(()=>{TickCore();return !terminal;});}
        void TickCore()
        {
            Owner();if(terminal)return;
            if(stop==null&&!Active())return;
            if(stop!=null&&transport.Now-stopIssued>maxAge){End("participant_stop_ack_timeout");return;}
            if(task!=null&&transport.Now-taskReceived>maxAge){End("original_task_receipt_expired");return;}
            if(challenge!=null&&transport.Now-challengeReceived>maxAge){End("original_challenge_receipt_expired");return;}
            for(int i=0;i<ViveLegacyWozProcessTransport.MaximumDrain&&!terminal;i++)
            {
                ViveLegacyWozProcessTransport.Receipt receipt;
                if(!transport.Receive(null,stop==null?expected:retiredExpected,grant,out receipt,null,previousExpected))break;if(receipt==null)continue;
                if(stop==null&&receipt.Control!=null&&receipt.Control.Message.Kind!=WozControlKind.SourceRevoke&&expected!=null&&
                    receipt.Control.Message.Binding.TaskContext.ParticipantSequence!=expected.TaskContext.ParticipantSequence)continue;
                if(stop!=null&&receipt.Data!=null&&receipt.Data.Packet.Kind==WireKind.Task)
                {retiredExpected=new WozControlBinding(receipt.Data.TaskContext,sourceOwner,sourceGeneration,choiceGeneration);continue;}
                if(stop!=null&&receipt.Control!=null&&(receipt.Control.Message.Kind==WozControlKind.SourceConfirm||receipt.Control.Message.Kind==WozControlKind.PreflightChallenge))continue;
                if(receipt.Control!=null)
                {
                    var m=receipt.Control.Message;
                    if(m.Kind==WozControlKind.SourceRevoke&&grant!=null)
                    {
                        ClearPositive();consent=false;
                        if(CloseSource())SourceRevokeAcknowledged=transport.SendControl(WozControlKind.SourceRevokeAck,grant,m.Challenge);
                        End(SourceRevokeAcknowledged?"source_revoked_closed_ack":"source_close_unconfirmed");break;
                    }
                    if(!consent||!Current()){End("no_current_local_operator_consent");break;}
                    if(m.Kind==WozControlKind.SourceConfirm)
                    {grant=expected;phaseProven=false;challenge=null;
                        if(!transport.SendControl(WozControlKind.TaskReceipt,grant,m.Challenge))End("task_receipt_send_failed");}
                    else if(m.Kind==WozControlKind.PreflightChallenge&&grant!=null&&Object.ReferenceEquals(grant,expected))
                    {challenge=m.Challenge;challengeReceived=receipt.OriginalReceiptSeconds;phaseProven=false;}
                    else End("unexpected_control_order");
                }
                else if(receipt.Data.Packet.Kind==WireKind.SafetyStopAck&&stop!=null&&receipt.Data.Packet.StopRequestId==stop)
                {ParticipantStopAcknowledged=true;End("participant_paused_ack");}
                else if(receipt.Data.Packet.Kind==WireKind.Task&&stop==null)
                {
                    if(!PhaseAllowed(receipt.Data.TaskContext.Task.Phase)){End("unsupported_task_phase");break;}
                    task=receipt.Data.TaskContext;taskReceived=receipt.OriginalReceiptSeconds;
                    previousExpected=expected;expected=new WozControlBinding(task,sourceOwner,sourceGeneration,choiceGeneration);challenge=null;phaseProven=false;
                    // Retain the last grant for negative revoke even after a new
                    // task. It grants no positive authority for the new task.
                }
                else End("unexpected_data");
            }
            if(!terminal&&transport.Terminal)End(transport.LastIssue);
        }
        public bool PollPhasePreflight()
        {return Guard(PreflightCore);}
        bool PreflightCore()
        {
            if(!SourceConfirmed||challenge==null)return false;
            if(transport.Now-challengeReceived>maxAge){End("preflight_timeout");return false;}
            var priorGrant=grant;string priorChallenge=challenge;ViveLegacyWozObservationOwner.Candidate candidate;
            if(!source.TryReadPreflight(task.Task,Math.Max(taskReceived,challengeReceived),out candidate))
            {if(source.Terminal)End(source.LastIssue);return false;}
            if(!SourceConfirmed||!Object.ReferenceEquals(priorGrant,grant)||priorChallenge!=challenge||
                transport.Now-candidate.HeadObserved>ObservationAge||transport.Now-candidate.EyeObserved>ObservationAge)
            {End("preflight_context_or_observation_lost");return false;}
            if(!transport.SendControl(WozControlKind.PreflightObservation,grant,challenge,challengeReceived,candidate.HeadObserved,candidate.EyeObserved))
            {End("preflight_send_failed");return false;}
            if(!SourceConfirmed||priorChallenge!=challenge){End("preflight_changed_during_send");return false;}
            proofHead=candidate.HeadObserved;proofEye=candidate.EyeObserved;phaseProven=true;challenge=null;return true;
        }
        public bool PollObservation()
        {return Guard(ObservationCore);}
        bool ObservationCore()
        {
            if(!PhasePreflightCurrent||task.Task.Phase!=Phase.MainTask)return false;
            var prior=grant;ViveLegacyWozObservationOwner.Candidate candidate;
            if(!source.TryRead(task,taskReceived,out candidate)){if(source.Terminal)End(source.LastIssue);return false;}
            if(!PhasePreflightCurrent||!Object.ReferenceEquals(prior,grant)||transport.Now-candidate.HeadObserved>ObservationAge||transport.Now-candidate.EyeObserved>ObservationAge)
            {End("pose_context_or_observation_lost");return false;}
            if(!transport.SendPose(candidate,task,taskReceived)||!PhasePreflightCurrent){End("pose_send_failed");return false;}return true;
        }
        public bool RequestSafetyStop()
        {return Guard(StopCore);}
        bool StopCore()
        {
            Owner();if(terminal||stop!=null)return false;retiredExpected=expected;ClearPositive();consent=false;
            if(!CloseSource()){End("source_close_uncertain");return false;}
            stop=Guid.NewGuid().ToString("N");stopIssued=transport.Now;
            if(!transport.SendStop(false,stop)){End("stop_send_failed");return false;}return true;
        }
        bool CloseSource()
        {if(cleanupUncertain)return false;if(sourceClosed)return true;try{source.Dispose();
#if STROOP_TEST_BUILD
            if(FailSourceCloseForTest)throw new InvalidOperationException("Sealed fixture close uncertainty");
#endif
            sourceClosed=true;return true;}catch{cleanupUncertain=true;return false;}}
        void End(string reason)
        {if(terminal)return;Owner();LastIssue=reason;ClearPositive();consent=false;terminal=true;CloseSource();transport.Dispose();}
        public void Dispose(){Owner();End("disposed");}
#if STROOP_TEST_BUILD
        internal bool FailSourceCloseForTest {get;set;}
        internal static ViveLegacyWozOperatorProcess Synthetic(ViveLegacyWozProcessPairing p,MonotonicClock clock,ViveLegacyWozObservationOwner.SyntheticObservations provider,IPEndPoint local,IPEndPoint remote,double age,ViveLegacyWozProcessTransport.MemorySocket socket)
        {
            if(p.Context.Operator.Source!=ControlSource.Simulator)throw new InvalidOperationException("Fixture requires explicit Simulator context");
            var transport=socket==null?new ViveLegacyWozProcessTransport(p,PeerRole.Operator,clock,local,remote,age):ViveLegacyWozProcessTransport.Memory(p,PeerRole.Operator,clock,local,remote,age,socket);
            ViveLegacyWozObservationOwner source=null;
            try{source=ViveLegacyWozObservationOwner.BindSynthetic(p.Context.Participant.SessionId,p.EpochLabel,clock,provider,age);
                return new ViveLegacyWozOperatorProcess(p,clock,source,transport,age,false);}
            catch{transport.Dispose();if(source!=null)source.Dispose();throw;}
        }
        internal void ReplaceLeaseForTest(){source.Dispose();}
#endif
    }
}
