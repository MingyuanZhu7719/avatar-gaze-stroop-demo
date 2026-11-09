using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // In-memory, explicitly configured VIVE DEVELOPMENT TASK. This policy does
    // not connect an eye source, send network packets or persist task/eye data.
    // The Experiment remains Simulation and the only avatar source is Automatic.
    // Readiness is supplied by the host from current headset and device samples;
    // it is never a claim of physical calibration or apparatus validation.
    public sealed class ViveDevelopmentParticipant
    {
        readonly Experiment experiment;
        readonly ViveRawSessionContext rawContext;
        internal ViveRawSessionContext RawContext {get{return rawContext;}}
        readonly VivePreviewTrackpadInput trackpad;
        readonly AutomaticGazeGeometry geometry;
        readonly string eventClockId;
        readonly IClock eventClock;
        readonly ViveSyntheticAggregatePump observation;
        readonly int observationOwnerThread;
        readonly int namedViewOwnerThread=System.Threading.Thread.CurrentThread.ManagedThreadId;
        readonly object namedViewGate=new object();
        bool namedViewWithdrawn;
        // V3 task observation bookkeeping only. Task actions run outside this
        // lock; it is not consent, hardware identity, a writer or study entry.
        readonly object namedRecordingGate=new object();
        // One irrevocable bit exists before any recording admission attaches;
        // a concurrent attach cannot obtain a fresh live I/O authority after
        // the participant has requested withdrawal.
        readonly ViveRawRevocation namedRawRevocation=new ViveRawRevocation();
        internal ViveRawRevocation NamedRawRevocation {get{return namedRawRevocation;}}
        ViveNamedSessionFrameAdmission namedFrameAdmission;
        int namedFrameAdmissionRevoked;
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        internal Action BeforeNamedAdmissionWitnessForTest;
#endif
        long namedRecordingMutationGeneration;
        bool namedRecordingMutationActive,namedRecordingFaulted,namedRecordingWithdrawnApplied,namedRecordingFaultPauseAttempted;
        bool namedRecordingWithdrawn
        {get{return namedRawRevocation.ParticipantWithdrawalRequested||namedRecordingWithdrawnApplied;}
            set{namedRecordingWithdrawnApplied=value;}}
        int namedRecordingPendingSafety;
        string namedRecordingPendingTechnicalReason;
        // Synthetic test hook only; always invoked before any authority lock.
        internal Action BeforeNamedAuxiliaryReservationForTest {get;set;}
        volatile bool observationPending;
        string observationFailure;
        // 0 idle, 1 ordinary action has begun, 2 terminal fault. No lock is
        // held across an Experiment call or its clock callbacks.
        int observationActionState;
        // The proof lock is held only for bookkeeping, never over a task,
        // device, clock, mapper, pump or other caller-controlled callback.
        readonly object observationFenceGate;
        long observationActionGeneration,retryInputRevision;
        int observationFenceDepth;
        // One shared slot prevents an ordinary eye/practice fence and a
        // terminal task-drain fence from being outstanding simultaneously.
        object outstandingPracticeFence;
        PracticeTaskDrainFence issuedPracticeTaskDrainFence;
        int issuedPracticeTaskDrainState; // 0 invalid, 1 outstanding, 2 consumed
        bool practiceStartRevisionReserved,practiceConsentWithdrawn;
        VivePracticeConsentAuthority practiceConsent;
        bool inputReady, auxSampled, previousConfirm, previousPause, previousWithdraw, ratingArmed;
        bool retryTrackpadNeutralObserved,retryAuxNeutralObserved;
        bool previousDisplayReady, previousInstruction;
        Phase auxiliaryPhase;
        int auxiliaryItem=-1, candidate=3;
        AvatarController avatar;
        int avatarBlock=-1,avatarSegment=-1;

        public ViveDevelopmentParticipant(SessionConfiguration configuration,IClock clock,AutomaticGazeGeometry geometry,
            IDictionary<ViveTrackpadDirection,Colour> explicitMapping,double sectorThreshold,bool ownerExplicitlyConfigured)
            :this(configuration,clock,geometry,explicitMapping,sectorThreshold,ownerExplicitlyConfigured,null,4096)
        {}
        // Opt-in development observation only. The original constructor and
        // simulation behavior remain available without constructing a journal.
        public ViveDevelopmentParticipant(SessionConfiguration configuration,IClock clock,AutomaticGazeGeometry geometry,
            IDictionary<ViveTrackpadDirection,Colour> explicitMapping,double sectorThreshold,bool ownerExplicitlyConfigured,
            ExperimentIncrementalOptions observationOptions,int observationTraceLimit)
        {
            if(!ownerExplicitlyConfigured)throw new InvalidOperationException("Explicit owner configuration is required");
            if(configuration==null)throw new ArgumentNullException("configuration");
            if(configuration.RunKind!=RunKind.Simulation)throw new InvalidOperationException("Apparatus sessions remain unavailable");
            rawContext=configuration.HasRawRecordingIntent?ViveRawSessionContext.FromConfiguration(configuration):null;
            if(rawContext!=null&&rawContext.IsLegacy)
            {
                if(observationOptions!=null||geometry==null||configuration.LegacyRecordingDesign.GeometryVersion!=geometry.Version||
                    configuration.LegacyRecordingDesign.MappingVersion!=ViveFixedTrackpadMapping.Version||sectorThreshold!=ViveFixedTrackpadMapping.SectorThreshold||
                    !ViveFixedTrackpadMapping.Matches(explicitMapping))
                    throw new ArgumentException("Legacy raw recording requires the exact configured geometry and fixed controller mapping, without legacy observation.");
            }
            bool named=configuration.IsNamedDesign;
            if(configuration.CountdownSeconds!=3||configuration.MinimumRestSeconds!=30||!configuration.SkipFinalRest||
                configuration.QuestionnaireInstructionSeconds!=0||!configuration.QuestionnaireInstructionRequiresAcknowledgement||
                !configuration.ParticipantPacedRest||(named?configuration.RetainControlAcrossBlocks:!configuration.RetainControlAcrossBlocks))
                throw new ArgumentException("Use the existing participant-paced preview timing and flow", "configuration");
            if(geometry==null)throw new ArgumentNullException("geometry");
            if(named)
            {
                if(observationOptions!=null)throw new InvalidOperationException("Legacy observation cannot represent named-option provenance");
                if(configuration.NamedPlan!=null)
                {
                    foreach(var block in configuration.NamedPlan.Blocks)
                    {
                        if(block.Selection.Option==ViveNamedOption.Automatic)
                        {if(block.Selection.GeometryVersion!=geometry.Version)throw new ArgumentException("Named option and actual geometry versions must match");ViveAutomaticCondition.Evaluate(0,geometry);}
                        else if(block.Selection.Option!=ViveNamedOption.NoAvatar)throw new InvalidOperationException("Named option is not implemented");
                    }
                }
                else if(configuration.NamedOption.Option==ViveNamedOption.Automatic)
                {
                    if(configuration.NamedOption.GeometryVersion!=geometry.Version)throw new ArgumentException("Named option and actual geometry versions must match");
                    // This validates development coordinates, not physical calibration.
                    ViveAutomaticCondition.Evaluate(0,geometry);
                }
                else if(configuration.NamedOption.Option!=ViveNamedOption.NoAvatar)
                    throw new InvalidOperationException("Named option is not implemented");
            }
            // Constructor validates a complete distinct four-colour mapping and a
            // caller-selected threshold; there is deliberately no default map.
            trackpad=new VivePreviewTrackpadInput(explicitMapping,sectorThreshold);
            experiment=observationOptions==null?new Experiment(configuration,clock):new Experiment(configuration,clock,observationOptions);
            this.geometry=geometry;eventClock=clock;eventClockId=clock.ClockId;
            if(observationOptions!=null)
            {
                observationFenceGate=new object();
                observationOwnerThread=System.Threading.Thread.CurrentThread.ManagedThreadId;
                observation=new ViveSyntheticAggregatePump(experiment,observationTraceLimit);
                observationPending=true;SynchronizeObservation();
            }
        }

        // The pump is diagnostic simulation evidence, not an admission or
        // recording receipt. Only this owner thread may mutate the Experiment.
        public bool ObservationEnabled {get{return observation!=null;}}
        public bool ObservationFaulted {get{return System.Threading.Volatile.Read(ref observationActionState)==2||System.Threading.Volatile.Read(ref observationFailure)!=null||(observation!=null&&observation.Terminal);}}
        public bool ObservationPending {get{return observation!=null&&observationPending&&!ObservationFaulted;}}
        // A head checked just before an action is no longer evidence while that
        // action is running, including during a reentrant clock callback.
        public bool ObservationBlocked {get{return observation!=null&&(ObservationFaulted||observationPending||System.Threading.Volatile.Read(ref observationActionState)==1||System.Threading.Volatile.Read(ref observationFenceDepth)!=0);}}
        public string ObservationIssue {get{return System.Threading.Volatile.Read(ref observationFailure)??(observation==null?null:observation.LastIssue)??(System.Threading.Volatile.Read(ref observationActionState)==2?"observation_fault_latched":observationPending?"observation_backlog_pending":null);}}
        void LatchObservationFailure(string fallback)
        {
            string cause=observation==null?null:observation.LastIssue;
            System.Threading.Interlocked.CompareExchange(ref observationFailure,cause??fallback,null);
        }
        void FaultObservation(string reason)
        {
            bool changed=false;
            lock(observationFenceGate)
            {
                outstandingPracticeFence=null;
                int prior=System.Threading.Volatile.Read(ref observationActionState);
                while(prior!=2)
                {
                    int seen=System.Threading.Interlocked.CompareExchange(ref observationActionState,2,prior);
                    if(seen==prior){changed=true;break;}
                    prior=seen;
                }
            }
            if(changed)LatchObservationFailure(reason);
        }
        void EnterFenceAction()
        {
            bool overflow=false;
            lock(observationFenceGate)
            {
                outstandingPracticeFence=null;
                if(observationActionGeneration==Int64.MaxValue)overflow=true;
                else observationActionGeneration++;
                if(observationFenceDepth==Int32.MaxValue)overflow=true;
                else observationFenceDepth++;
            }
            if(overflow)FaultObservation("observation_action_generation_exhausted");
        }
        void ExitFenceAction()
        {lock(observationFenceGate){if(observationFenceDepth>0)observationFenceDepth--;}}

        // An in-memory, single-use fixture fence. This does not prove consent,
        // hardware identity, global worker ownership, or recording admission.
        // It proves only a current snapshot from this participant's sole pump
        // across wrapped public task actions on the owner thread.
        internal sealed class PracticeObservationFence
        {
            readonly ViveDevelopmentParticipant participant;
            readonly ViveSyntheticAggregateHead head;
            readonly long generation,revision,publication;
            // Access only under the owning participant's observation lock.
            internal bool Consumed,PreviewUsed;
            internal ExperimentIncrementalSnapshot Snapshot {get{return head.Snapshot;}}
            // These are the immutable steps of the same published head, not
            // a second journal consumer or proof that original events persisted.
            internal IReadOnlyList<ViveSyntheticStepTrace> StepTrace {get{return head.StepTrace;}}
            internal long MutationStepsObserved {get{return head.MutationStepsObserved;}}
            internal long Revision {get{return revision;}}
            internal bool GenerationMatches(long candidate){return generation==candidate;}
            internal bool BelongsTo(ViveDevelopmentParticipant candidate){return Object.ReferenceEquals(participant,candidate);}
            internal PracticeObservationFence(ViveDevelopmentParticipant participant,ViveSyntheticAggregateHead head,long generation)
            {this.participant=participant;this.head=head;this.generation=generation;revision=head.Revision;publication=head.Publication;}
            internal bool Matches(ViveDevelopmentParticipant candidate,ViveSyntheticAggregateHead current,long currentGeneration)
            {return Object.ReferenceEquals(participant,candidate)&&generation==currentGeneration&&Object.ReferenceEquals(head,current)&&
                revision==current.Revision&&publication==current.Publication;}
        }
        static bool PracticeFencePhase(Phase phase)
        {return phase==Phase.Preparation||phase==Phase.Familiarization||phase==Phase.PracticeRetry||phase==Phase.Practice||phase==Phase.PracticeCorrection;}
        internal bool TryAcquirePracticeObservationFence(IClock expectedClock,out PracticeObservationFence fence)
        {
            fence=null;if(observation==null)return false;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)
            {FaultObservation("observation_owner_thread_violation");return false;}
            if(!Object.ReferenceEquals(eventClock,expectedClock))return false;
            long generation;
            lock(observationFenceGate)
            {
                if(ObservationBlocked||observationFenceDepth!=0||outstandingPracticeFence!=null)return false;
                generation=observationActionGeneration;
            }
            ViveSyntheticAggregateHead head;
            try{if(!observation.TryGet(out head)||head==null||head.Snapshot==null||head.Snapshot.Task==null||!PracticeFencePhase(head.Snapshot.Task.Phase))return false;}
            catch(Exception error){FaultObservation("observation_fence_acquire:"+error.GetType().Name);return false;}
            lock(observationFenceGate)
            {
                if(ObservationBlocked||observationFenceDepth!=0||outstandingPracticeFence!=null||generation!=observationActionGeneration)return false;
                fence=new PracticeObservationFence(this,head,generation);outstandingPracticeFence=fence;return true;
            }
        }
        internal bool TryConsumePracticeObservationFence(PracticeObservationFence fence)
        {
            if(observation==null||fence==null)return false;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)
            {FaultObservation("observation_owner_thread_violation");return false;}
            long generation;
            lock(observationFenceGate)
            {
                if(!Object.ReferenceEquals(outstandingPracticeFence,fence))return false;
                outstandingPracticeFence=null; // all attempts are single-use
                if(ObservationBlocked||observationFenceDepth!=0)return false;
                generation=observationActionGeneration;
            }
            ViveSyntheticAggregateHead head;
            try{if(!observation.TryGet(out head)||head==null)return false;}
            catch(Exception error){FaultObservation("observation_fence_consume:"+error.GetType().Name);return false;}
            lock(observationFenceGate)
            {
                bool valid=!ObservationBlocked&&observationFenceDepth==0&&fence.Matches(this,head,generation)&&generation==observationActionGeneration;
                if(valid)fence.Consumed=true;
                return valid;
            }
        }
        // Choice -> participant -> sealed preview is the only lock order.
        // This status-only seam performs no device, stream, clock or callback I/O.
        internal bool CommitPreviewUnderChoiceLock(VivePracticeConsentAuthority authority,VivePracticePreviewCoordinator preview,
            PracticeObservationFence fence,long generation)
        {
            if(observation==null||authority==null||preview==null||fence==null||!authority.StateLockHeld||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                if(practiceConsentWithdrawn||!Object.ReferenceEquals(practiceConsent,authority)||ObservationBlocked||
                    observationFenceDepth!=0||!fence.BelongsTo(this)||!fence.GenerationMatches(observationActionGeneration)||
                    !fence.Consumed||fence.PreviewUsed)return false;
                fence.PreviewUsed=true; // attempts are single-use, including failures
                return preview.PublishUnderParticipantLock(authority,this,fence,generation);
            }
        }
        internal void AbandonPracticeObservationFence(PracticeObservationFence fence)
        {
            if(observation==null)return;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)
            {FaultObservation("observation_owner_thread_violation");return;}
            lock(observationFenceGate){if(Object.ReferenceEquals(outstandingPracticeFence,fence))outstandingPracticeFence=null;}
        }
        // Task-only terminal evidence. It is intentionally unrelated to
        // PracticeObservationFence: it cannot be handed to the eye source and
        // does not authorize recording, I/O, or resumption of an epoch.
        internal sealed class PracticeTaskDrainFence
        {
            readonly ViveDevelopmentParticipant participant;
            readonly VivePracticeCursorAnchor anchor;
            readonly ViveSyntheticAggregateHead head;
            readonly long generation,revision,publication;
            internal readonly long RetryInputRevision;
            internal ExperimentIncrementalSnapshot Snapshot {get{return head.Snapshot;}}
            internal IReadOnlyList<ViveSyntheticStepTrace> StepTrace {get{return head.StepTrace;}}
            internal long MutationStepsObserved {get{return head.MutationStepsObserved;}}
            internal long Revision {get{return revision;}}
            internal PracticeTaskDrainFence(ViveDevelopmentParticipant participant,VivePracticeCursorAnchor anchor,
                ViveSyntheticAggregateHead head,long generation,long retryInputRevision)
            {this.participant=participant;this.anchor=anchor;this.head=head;this.generation=generation;RetryInputRevision=retryInputRevision;revision=head.Revision;publication=head.Publication;}
            internal bool BelongsTo(ViveDevelopmentParticipant candidate,VivePracticeCursorAnchor candidateAnchor)
            {return Object.ReferenceEquals(participant,candidate)&&Object.ReferenceEquals(anchor,candidateAnchor);}
            internal bool Matches(ViveDevelopmentParticipant candidate,VivePracticeCursorAnchor candidateAnchor,
                ViveSyntheticAggregateHead current,long candidateGeneration)
            {return BelongsTo(candidate,candidateAnchor)&&generation==candidateGeneration&&Object.ReferenceEquals(head,current)&&
                revision==current.Revision&&publication==current.Publication;}
            internal bool GenerationMatches(long candidate){return generation==candidate;}
        }
        static bool PracticeTaskDrainState(ExperimentIncrementalSnapshot snapshot,VivePracticeCursorAnchor anchor)
        {
            if(snapshot==null||snapshot.Task==null||anchor==null||anchor.Context==null||anchor.StartSnapshot==null||
                anchor.StartSnapshot.Task==null||snapshot.Task.SessionId!=anchor.Context.SessionId||
                snapshot.Scalars.PracticeAttempt!=anchor.Context.PracticeAttempt||snapshot.Task.Segment!=anchor.StartSnapshot.Task.Segment)
                return false;
            if(snapshot.Task.Phase==Phase.BlockPreparation)return snapshot.Task.BlockIndex==0;
            if(snapshot.Task.Phase==Phase.PracticeRetry)return snapshot.Task.BlockIndex==-1;
            if(snapshot.Task.Phase==Phase.Ended)return anchor.Context.PracticeAttempt==2&&snapshot.Task.BlockIndex==-1&&
                snapshot.Scalars.EndReason==AvatarStroop.Core.EndReason.PracticeFailed;
            return snapshot.Task.Phase==Phase.Paused&&snapshot.Task.BlockIndex==-1&&
                (snapshot.PausedFrom==Phase.Practice||snapshot.PausedFrom==Phase.PracticeCorrection);
        }
        internal bool TryAcquirePracticeTaskDrainFence(VivePracticeCursorAnchor anchor,IClock expectedClock,out PracticeTaskDrainFence fence)
        {
            fence=null;if(observation==null||anchor==null||anchor.Issuer==null||
                !Object.ReferenceEquals(anchor.Participant,this)||!Object.ReferenceEquals(anchor.Clock,eventClock)||
                !Object.ReferenceEquals(expectedClock,eventClock))return false;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)
            {FaultObservation("task_drain_owner_thread_violation");return false;}
            // Never call issuer/choice or pump while holding the participant lock.
            if(!anchor.Issuer.IsCurrentForCursor(anchor))return false;
            long generation;
            lock(observationFenceGate)
            {
                if(practiceConsentWithdrawn||!Object.ReferenceEquals(practiceConsent,anchor.Choices)||ObservationBlocked||
                    observationFenceDepth!=0||outstandingPracticeFence!=null)return false;
                generation=observationActionGeneration;
            }
            ViveSyntheticAggregateHead head;
            try{if(!observation.TryGet(out head)||head==null||!PracticeTaskDrainState(head.Snapshot,anchor))return false;}
            catch(Exception error){FaultObservation("task_drain_fence_acquire:"+error.GetType().Name);return false;}
            lock(observationFenceGate)
            {
                if(practiceConsentWithdrawn||!Object.ReferenceEquals(practiceConsent,anchor.Choices)||ObservationBlocked||
                    observationFenceDepth!=0||outstandingPracticeFence!=null||generation!=observationActionGeneration)return false;
                fence=new PracticeTaskDrainFence(this,anchor,head,generation,retryInputRevision);issuedPracticeTaskDrainFence=fence;
                issuedPracticeTaskDrainState=1;outstandingPracticeFence=fence;
            }
            try{if(anchor.Issuer.IsCurrentForCursor(anchor))return true;}
            catch(Exception error){FaultObservation("task_drain_authority:"+error.GetType().Name);}
            AbandonPracticeTaskDrainFence(fence);fence=null;return false;
        }
        internal bool TryConsumePracticeTaskDrainFence(VivePracticeCursorAnchor anchor,PracticeTaskDrainFence fence)
        {
            if(observation==null||anchor==null||fence==null||anchor.Issuer==null)return false;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)
            {FaultObservation("task_drain_owner_thread_violation");return false;}
            long generation;
            lock(observationFenceGate)
            {
                if(!Object.ReferenceEquals(outstandingPracticeFence,fence)||!Object.ReferenceEquals(issuedPracticeTaskDrainFence,fence)||
                    issuedPracticeTaskDrainState!=1||!fence.BelongsTo(this,anchor))return false;
                outstandingPracticeFence=null;issuedPracticeTaskDrainState=0;
                if(practiceConsentWithdrawn||!Object.ReferenceEquals(practiceConsent,anchor.Choices)||ObservationBlocked||observationFenceDepth!=0)return false;
                generation=observationActionGeneration;
            }
            ViveSyntheticAggregateHead head;
            try{if(!observation.TryGet(out head)||head==null||!PracticeTaskDrainState(head.Snapshot,anchor))return false;}
            catch(Exception error){FaultObservation("task_drain_fence_consume:"+error.GetType().Name);return false;}
            // The old target receipt is intentionally invalid once an append
            // starts. Revalidate choices/target atomically in the caller's
            // sealed completion chain, rather than checking that receipt here.
            lock(observationFenceGate)
            {
                if(practiceConsentWithdrawn||!Object.ReferenceEquals(practiceConsent,anchor.Choices)||ObservationBlocked||observationFenceDepth!=0||
                    !fence.Matches(this,anchor,head,generation)||generation!=observationActionGeneration)return false;
                if(!Object.ReferenceEquals(issuedPracticeTaskDrainFence,fence)||issuedPracticeTaskDrainState!=0||outstandingPracticeFence!=null)return false;
                issuedPracticeTaskDrainState=2;return true;
            }
        }
        internal void AbandonPracticeTaskDrainFence(PracticeTaskDrainFence fence)
        {
            if(observation==null||fence==null)return;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)
            {FaultObservation("task_drain_owner_thread_violation");return;}
            lock(observationFenceGate)
            {if(Object.ReferenceEquals(outstandingPracticeFence,fence)){outstandingPracticeFence=null;issuedPracticeTaskDrainState=0;}}
        }
        // Caller must hold the issuer completion and choice locks; used only
        // as a component of a later sealed task-drain commit. A consumed fence
        // is instantaneous evidence, never an independently usable permit.
        internal bool IsPracticeTaskDrainCurrentUnderChoiceLock(VivePracticeCursorAnchor anchor,PracticeTaskDrainFence fence,bool requireOutstanding)
        {
            if(observation==null||anchor==null||fence==null||anchor.Issuer==null||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
                return anchor.Issuer.CursorPreTargetLocksHeld&&Object.ReferenceEquals(issuedPracticeTaskDrainFence,fence)&&
                    fence.BelongsTo(this,anchor)&&Object.ReferenceEquals(anchor.Participant,this)&&Object.ReferenceEquals(anchor.Clock,eventClock)&&
                    Object.ReferenceEquals(practiceConsent,anchor.Choices)&&!practiceConsentWithdrawn&&!ObservationBlocked&&observationFenceDepth==0&&
                    fence.GenerationMatches(observationActionGeneration)&&PracticeTaskDrainState(fence.Snapshot,anchor)&&
                    (requireOutstanding?(issuedPracticeTaskDrainState==1&&Object.ReferenceEquals(outstandingPracticeFence,fence)):
                        (issuedPracticeTaskDrainState==2&&outstandingPracticeFence==null));
        }
        // One reservation per observed participant, only for the initial
        // practice start candidate. This is not a recording/consent permit.
        // Failed or abandoned handoffs do not silently reissue the boundary.
        bool practiceRetryRevisionReserved;
        // These paths are called only within the old cursor -> reservation ->
        // choice lock chain. They do not grant any eye or file capability.
        internal bool TryIssueRetryContinuation(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            PracticeTaskDrainFence fence,out VivePracticeRetryContinuation continuation)
        {
            continuation=null;if(observation==null||anchor==null||cursor==null||fence==null||anchor.Issuer==null||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                if(!inputReady||practiceRetryRevisionReserved||!IsPracticeTaskDrainCurrentUnderChoiceLock(anchor,fence,false))return false;
                return anchor.Issuer.IssueRetryUnderParticipantLock(anchor,cursor,fence,out continuation);
            }
        }
        internal bool TryClaimRetryContinuation(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,VivePracticeStartBinding.Ticket ticket,VivePracticeContextV3 context)
        {
            if(observation==null||anchor==null||cursor==null||continuation==null||ticket==null||ticket.Fence==null||ticket.Issuer==null||anchor.Issuer==null||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                var fence=ticket.Fence;
                if(!inputReady||retryInputRevision!=continuation.IssueFence.RetryInputRevision||!anchor.Issuer.CursorPreTargetLocksHeld||practiceRetryRevisionReserved||!practiceStartRevisionReserved||
                    practiceConsentWithdrawn||ObservationBlocked||observationFenceDepth!=0||
                    !Object.ReferenceEquals(practiceConsent,anchor.Choices)||!Object.ReferenceEquals(anchor.Participant,this)||
                    !Object.ReferenceEquals(outstandingPracticeFence,fence)||!fence.BelongsTo(this)||
                    !fence.GenerationMatches(observationActionGeneration)||!ticket.Issuer.OwnsOutstandingRetryTicket(continuation,ticket)||
                    ticket.Batch==null||ticket.Batch.Attempt!=2||ticket.Batch.Revision<=continuation.IssueRevision||fence.Revision!=ticket.Batch.Revision)return false;
                var trace=fence.StepTrace;
                if(trace==null||trace.Count<2||trace.Count>4096||fence.MutationStepsObserved!=trace.Count||
                    !Object.ReferenceEquals(trace[trace.Count-1].After,fence.Snapshot))return false;
                int first=trace.Count-1;while(first>0&&trace[first-1].Revision==fence.Revision)first--;
                if(first<1||trace[first].Index!=0||!VivePracticeRetryReleaseValidator.IsReleaseOnly(trace,fence.MutationStepsObserved,
                    continuation.IssueTraceCount,continuation.IssueStep,continuation.IssueSnapshot,first,trace[first].Before))return false;
                practiceRetryRevisionReserved=true;
                return anchor.Issuer.ConsumeRetryUnderParticipantLock(anchor,cursor,continuation,ticket,context);
            }
        }
        internal bool TryReserveRetryBegin(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,PracticeTaskDrainFence fence)
        {
            if(observation==null||anchor==null||cursor==null||continuation==null||fence==null||anchor.Issuer==null||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
                return inputReady&&retryTrackpadNeutralObserved&&retryAuxNeutralObserved&&!practiceRetryRevisionReserved&&IsPracticeTaskDrainCurrentUnderChoiceLock(anchor,fence,false)&&
                    anchor.Issuer.ReserveRetryBeginUnderParticipantLock(anchor,cursor,continuation,fence);
        }
        internal bool TryFinalizeRetryClaim(VivePracticeCursorAnchor anchor,VivePracticeRetryContinuation continuation,VivePracticeStartBinding.Ticket ticket)
        {
            if(observation==null||anchor==null||continuation==null||ticket==null||ticket.Issuer==null||anchor.Issuer==null||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
                return inputReady&&practiceRetryRevisionReserved&&!practiceConsentWithdrawn&&!ObservationBlocked&&observationFenceDepth==0&&
                    Object.ReferenceEquals(practiceConsent,anchor.Choices)&&Object.ReferenceEquals(anchor.Participant,this)&&
                    Object.ReferenceEquals(outstandingPracticeFence,ticket.Fence)&&ticket.Fence.GenerationMatches(observationActionGeneration)&&
                    anchor.Issuer.IsRetryConsumedUnderParticipantLock(continuation)&&ticket.Issuer.MarkRetryClaimed(continuation,ticket);
        }
        internal bool TryFinishRetryStart(VivePracticeStartReservation newReservation,VivePracticeOwnedMemoryStart newHost,
            VivePracticeRetryContinuation continuation,VivePracticeConsentAuthority.StartReservation start,PracticeObservationFence fence)
        {
            if(observation==null||newReservation==null||newHost==null||continuation==null||continuation.Anchor==null||continuation.Issuer==null||start==null||fence==null||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                if(!inputReady||practiceConsentWithdrawn||ObservationBlocked||observationFenceDepth!=0||
                    !Object.ReferenceEquals(practiceConsent,continuation.Anchor.Choices)||!Object.ReferenceEquals(continuation.Anchor.Participant,this)||
                    !Object.ReferenceEquals(continuation.Anchor.Clock,eventClock)||!fence.BelongsTo(this)||
                    !fence.GenerationMatches(observationActionGeneration))return false;
                return continuation.Issuer.TryPublishRetryUnderParticipantLock(newReservation,newHost,continuation,start,fence);
            }
        }
        internal bool TryReservePracticeStartRevision(PracticeObservationFence fence,long revision)
        {
            if(observation==null||fence==null)return false;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)
            {FaultObservation("observation_owner_thread_violation");return false;}
            lock(observationFenceGate)
            {
                if(ObservationBlocked||observationFenceDepth!=0||practiceStartRevisionReserved||
                    !Object.ReferenceEquals(outstandingPracticeFence,fence)||!fence.GenerationMatches(observationActionGeneration)||
                    fence.Revision!=revision)return false;
                practiceStartRevisionReserved=true;return true;
            }
        }
        // Recheck an already consumed fence's action generation. Only useful
        // as a local, instantaneous completion check; it grants no I/O right.
        internal bool PracticeGenerationStillCurrent(PracticeObservationFence fence)
        {
            if(observation==null||fence==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)return fence.BelongsTo(this)&&!practiceConsentWithdrawn&&!ObservationBlocked&&observationFenceDepth==0&&fence.GenerationMatches(observationActionGeneration);
        }
        internal bool ObservationFenceLockHeld {get{return observationFenceGate!=null&&System.Threading.Monitor.IsEntered(observationFenceGate);}}
        // Sealed fake-memory publication only. No clock, callback, stream,
        // device, network or other externally supplied code runs under locks.
        internal bool TryPublishSyntheticEye(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeSyntheticEyeJoin join,
            PracticeObservationFence fence,VivePracticeSyntheticTicket ticket)
        {
            if(observation==null||anchor==null||cursor==null||join==null||fence==null||ticket==null||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                if(practiceConsentWithdrawn||ObservationBlocked||observationFenceDepth!=0||outstandingPracticeFence!=null||
                    !Object.ReferenceEquals(anchor.Participant,this)||!fence.BelongsTo(this)||
                    !fence.GenerationMatches(observationActionGeneration)||!Object.ReferenceEquals(fence.Snapshot,ticket.Snapshot))return false;
                return anchor.Issuer!=null&&anchor.Issuer.TryPublishSyntheticEyeUnderParticipantLock(anchor,cursor,join,fence,ticket);
            }
        }
        // Called only while the choice-state lock is held. Publish a prebuilt
        // in-memory reference without invoking external code or doing I/O.
        internal bool TryPublishPracticeMemoryStage(PracticeObservationFence fence,VivePracticeMemoryPublication publication)
        {
            if(observation==null||fence==null||publication==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                if(!fence.BelongsTo(this)||practiceConsentWithdrawn||ObservationBlocked||observationFenceDepth!=0||
                    !fence.GenerationMatches(observationActionGeneration)||publication.Pending==null||publication.Committed!=null)return false;
                publication.Committed=publication.Pending;return true;
            }
        }
        // Only the sealed fake target may supply this local receipt. The
        // reference assignment itself is not a durable or eye-data receipt.
        internal bool TryPublishOwnedPracticeStart(PracticeObservationFence fence,VivePracticeOwnedStartReceipt receipt)
        {
            if(observation==null||fence==null||receipt==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                if(!fence.BelongsTo(this)||!Object.ReferenceEquals(receipt.Participant,this)||practiceConsentWithdrawn||ObservationBlocked||observationFenceDepth!=0||
                    !fence.GenerationMatches(observationActionGeneration)||receipt.Issuer==null)return false;
                return receipt.Issuer.TryPublishReceiptUnderParticipantLock(receipt,fence);
            }
        }
        internal bool TryUseOwnedPracticeStart(VivePracticeOwnedStartReceipt receipt,bool consume)
        {
            if(observation==null||receipt==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                if(!Object.ReferenceEquals(receipt.Participant,this)||practiceConsentWithdrawn||ObservationBlocked||observationFenceDepth!=0||receipt.Issuer==null)return false;
                return receipt.Issuer.TryUseReceiptUnderParticipantLock(receipt,this,observationActionGeneration,consume);
            }
        }
        internal bool IsOwnedPracticeCursorCurrent(VivePracticeCursorAnchor anchor,PracticeObservationFence pendingFence=null)
        {
            if(observation==null||anchor==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                if(!Object.ReferenceEquals(anchor.Participant,this)||practiceConsentWithdrawn||ObservationBlocked||observationFenceDepth!=0||anchor.Issuer==null||
                    (pendingFence!=null&&(!pendingFence.BelongsTo(this)||!pendingFence.GenerationMatches(observationActionGeneration))))return false;
                return anchor.Issuer.IsCursorAnchorUnderParticipantLock(anchor);
            }
        }
        // Called through reservation -> choice. This is only a current-head
        // task proof; it cannot be supplied to an eye consumer.
        internal bool IsOwnedPracticeCursorDrainCurrent(VivePracticeCursorAnchor anchor,PracticeTaskDrainFence fence,bool requireOutstanding)
        {
            if(observation==null||anchor==null||fence==null||anchor.Issuer==null||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
                return IsPracticeTaskDrainCurrentUnderChoiceLock(anchor,fence,requireOutstanding)&&
                    anchor.Issuer.IsCursorAnchorUnderParticipantLock(anchor);
        }
        internal bool TryBeginOwnedCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease,VivePracticeMultiWriter.Prepared prepared,byte[] digest,int length,
            out ViveExclusiveMemoryTarget.Receipt oldReceipt)
        {
            oldReceipt=null;if(observation==null||anchor==null||lease==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                if(!Object.ReferenceEquals(anchor.Participant,this)||practiceConsentWithdrawn||ObservationBlocked||observationFenceDepth!=0||anchor.Issuer==null)return false;
                bool ordinary=lease.Fence!=null&&lease.DrainFence==null&&Object.ReferenceEquals(outstandingPracticeFence,lease.Fence)&&
                    lease.Fence.BelongsTo(this)&&lease.Fence.GenerationMatches(observationActionGeneration);
                bool drain=lease.Fence==null&&lease.DrainFence!=null&&IsPracticeTaskDrainCurrentUnderChoiceLock(anchor,lease.DrainFence,true);
                if(!ordinary&&!drain)return false;
                return anchor.Issuer.BeginCursorUnderParticipantLock(anchor,cursor,lease,prepared,digest,length,out oldReceipt);
            }
        }
        internal bool CanContinueOwnedCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeTaskEventCursor.CursorAppendLease lease)
        {
            if(observation==null||anchor==null||lease==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                if(!Object.ReferenceEquals(anchor.Participant,this)||practiceConsentWithdrawn||ObservationBlocked||observationFenceDepth!=0||anchor.Issuer==null)return false;
                bool ordinary=lease.Fence!=null&&lease.DrainFence==null&&Object.ReferenceEquals(outstandingPracticeFence,lease.Fence)&&
                    lease.Fence.BelongsTo(this)&&lease.Fence.GenerationMatches(observationActionGeneration);
                bool drain=lease.Fence==null&&lease.DrainFence!=null&&IsPracticeTaskDrainCurrentUnderChoiceLock(anchor,lease.DrainFence,true);
                return (ordinary||drain)&&anchor.Issuer.ContinueCursorUnderParticipantLock(anchor,cursor,lease);
            }
        }
        internal bool TryFinalizeOwnedCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease,ViveExclusiveMemoryTarget.Receipt receipt,PracticeObservationFence completedFence)
        {
            if(observation==null||anchor==null||lease==null||completedFence==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
                return Object.ReferenceEquals(anchor.Participant,this)&&!practiceConsentWithdrawn&&!ObservationBlocked&&observationFenceDepth==0&&
                    lease.DrainFence==null&&Object.ReferenceEquals(lease.Fence,completedFence)&&completedFence.BelongsTo(this)&&completedFence.GenerationMatches(observationActionGeneration)&&
                    anchor.Issuer!=null&&anchor.Issuer.FinalizeCursorUnderParticipantLock(anchor,cursor,lease,receipt);
        }
        internal bool TryFinalizeOwnedCursorDrain(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease,ViveExclusiveMemoryTarget.Receipt receipt,PracticeTaskDrainFence completedFence)
        {
            if(observation==null||anchor==null||lease==null||completedFence==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
                return lease.Fence==null&&Object.ReferenceEquals(lease.DrainFence,completedFence)&&
                    IsPracticeTaskDrainCurrentUnderChoiceLock(anchor,completedFence,false)&&anchor.Issuer!=null&&
                    anchor.Issuer.FinalizeCursorUnderParticipantLock(anchor,cursor,lease,receipt);
        }
        // Caller already owns the associated choice-state lock. No callback is
        // made under either lock; only this rehearsal marker is changed.
        internal bool TryMarkPracticeStartInFlight(PracticeObservationFence fence,VivePracticeConsentAuthority.StartReservation reservation)
        {
            if(observation==null||fence==null||reservation==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)
            {
                if(practiceConsentWithdrawn||ObservationBlocked||observationFenceDepth!=0||!Object.ReferenceEquals(outstandingPracticeFence,fence)||
                    !fence.GenerationMatches(observationActionGeneration))return false;
                reservation.InFlight=true;return true;
            }
        }
        internal bool PracticeFenceStillOutstanding(PracticeObservationFence fence)
        {
            if(observation==null||fence==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)return false;
            lock(observationFenceGate)return !practiceConsentWithdrawn&&!ObservationBlocked&&observationFenceDepth==0&&
                Object.ReferenceEquals(outstandingPracticeFence,fence)&&fence.GenerationMatches(observationActionGeneration);
        }
        // Only the explicitly observed development participant may attach one
        // application-choice state. It is not an apparatus/user consent grant.
        internal bool TryAttachPracticeConsent(VivePracticeConsentAuthority authority,IClock clock)
        {
            if(observation==null||authority==null||!Object.ReferenceEquals(eventClock,clock))return false;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)
            {FaultObservation("observation_owner_thread_violation");return false;}
            lock(observationFenceGate)
            {
                if(practiceConsent!=null||practiceConsentWithdrawn||ObservationBlocked)return false;
                System.Threading.Volatile.Write(ref practiceConsent,authority);return true;
            }
        }
        internal bool PracticeConsentWithdrawalRequested
        {
            get
            {
                if(observation==null)return true;
                lock(observationFenceGate)return practiceConsentWithdrawn;
            }
        }
        // A short, in-memory revocation is first; never wait for a recorder or
        // perform I/O on a withdrawal path. In-flight I/O remains uncertain.
        void RevokePracticeConsent()
        {
            MarkNamedRecordingWithdrawn();
            MarkNamedViewWithdrawn();
            if(observation==null)return;
            VivePracticeConsentAuthority attached;
            lock(observationFenceGate){practiceConsentWithdrawn=true;attached=practiceConsent;}
            if(attached!=null)attached.Withdraw();
        }
        internal bool TryAttachNamedFrameAdmission(ViveNamedSessionFrameAdmission candidate)
        {
            if(candidate==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=namedViewOwnerThread||
                System.Threading.Volatile.Read(ref namedFrameAdmissionRevoked)!=0)return false;
            if(System.Threading.Interlocked.CompareExchange(ref namedFrameAdmission,candidate,null)!=null)return false;
            try{
#if VIVE_SYNTHETIC_BATCH_FIXTURE
                var hook=BeforeNamedAdmissionWitnessForTest;if(hook!=null)hook();
#endif
                experiment.RequireRawCountdownSoftwareWitness();}
            catch(Exception e) when(!(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException))
            {System.Threading.Interlocked.Exchange(ref namedFrameAdmissionRevoked,1);
                if(namedRawRevocation.ParticipantWithdrawalRequested)candidate.Withdraw();else candidate.AbortForTechnicalFailure();return false;}
            // A concurrent withdraw may have passed its read before the CAS.
            // Recheck after publication so that it can never leave a live queue.
            if(System.Threading.Volatile.Read(ref namedFrameAdmissionRevoked)!=0){candidate.Withdraw();return false;}
            return true;
        }
        // Only the attached named-raw path waits for a caller-declared software
        // camera witness. No claim about HMD visibility or durability follows.
        internal bool RawCountdownAwaitingSoftwareWitness
        {get{return System.Threading.Volatile.Read(ref namedFrameAdmission)!=null&&!namedRawRevocation.IsRevoked&&experiment.RawCountdownAwaitingSoftwareWitness;}}
        internal bool NamedRawCountdownAwaitingSoftwareWitness {get{return rawContext!=null&&!rawContext.IsLegacy&&RawCountdownAwaitingSoftwareWitness;}}
        internal bool TryStartNamedRawCountdownFromSoftwareWitness(int frame,long revision,double unitySeconds)
        {return rawContext!=null&&!rawContext.IsLegacy&&TryStartRawCountdownFromSoftwareWitness(frame,revision,unitySeconds);}
        internal bool TryStartLegacyRawCountdownFromSoftwareWitness(int frame,long revision,double unitySeconds)
        {return rawContext!=null&&rawContext.IsLegacy&&TryStartRawCountdownFromSoftwareWitness(frame,revision,unitySeconds);}
        internal bool TryStartRawCountdownFromSoftwareWitness(int frame,long revision,double unitySeconds)
        {
            var admission=System.Threading.Volatile.Read(ref namedFrameAdmission);
            if(admission==null||System.Threading.Thread.CurrentThread.ManagedThreadId!=namedViewOwnerThread||
                namedRawRevocation.IsRevoked||admission.State!=0||!inputReady||!experiment.RawCountdownAwaitingSoftwareWitness||
                frame<0||revision<0||Double.IsNaN(unitySeconds)||Double.IsInfinity(unitySeconds)||unitySeconds<0)return false;
            // The installed path reserves the event's real queue slot and a
            // conservative byte bound before the task state can change. The
            // unbudgeted branch exists only for old synthetic state fixtures.
            ViveNamedSessionFrameAdmission.CountdownWitnessReservation reservation=null;
            bool needsReservation=!admission.SyntheticWithoutQueueBudget;
            if(needsReservation&&!admission.TryReserveCountdownWitness(frame,revision,unitySeconds,out reservation))return false;
            bool started=false;
            try
            {
                RunNamedRecordingMutation(()=>{
                    if(!Object.ReferenceEquals(System.Threading.Volatile.Read(ref namedFrameAdmission),admission)||
                        namedRawRevocation.IsRevoked||(!needsReservation&&admission.State!=0)||!inputReady)return;
                    started=experiment.TryStartRawCountdownFromSoftwareWitness(frame,revision,unitySeconds);
                });
            }
            catch(Exception e) when(!(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException))
            {
                // The clock or journal can fail after the countdown start has
                // partly mutated. Revoke this exact admission as a technical
                // fault; the caller must treat that interval as UNKNOWN, not a
                // rollback or participant-requested withdrawal.
                admission.ConsumerFailed(null);return false;
            }
            if(needsReservation)
            {
                if(!started){admission.CancelCountdownWitness(reservation);return false;}
                if(!admission.TryCommitCountdownWitness(reservation))return false;
            }
            return started&&!namedRawRevocation.IsRevoked&&admission.State==0;
        }
        void MarkNamedRecordingWithdrawn()
        {
            if(!experiment.Configuration.HasRawRecordingIntent)return;
            namedRawRevocation.RevokeForParticipant();
            System.Threading.Interlocked.Exchange(ref namedFrameAdmissionRevoked,1);
            var attached=System.Threading.Volatile.Read(ref namedFrameAdmission);
            if(attached!=null)attached.Withdraw();
            lock(namedRecordingGate)namedRecordingWithdrawn=true;
        }
        // 0 ordinary, 1 participant pause, 2 owner pause, 3 technical end,
        // 4 withdraw. The larger value takes priority if requests overlap.
        // A safety request arriving during an action is retained and applied
        // on the owner thread at its next safe point; it is never reported by
        // this bookkeeping as already applied.
        void RunNamedRecordingMutation(Action action,int safety=0,string technicalReason=null,bool compoundInput=false)
        {
            if(!experiment.Configuration.HasRawRecordingIntent){action();return;}
            bool pendingFirst=false;
            lock(namedRecordingGate)
            {
                if(System.Threading.Thread.CurrentThread.ManagedThreadId!=namedViewOwnerThread||namedRecordingMutationActive)
                {
                    namedRecordingFaulted=true;
                    if(safety>0){QueueNamedSafetyUnderLock(safety,technicalReason);return;}
                    throw new InvalidOperationException("Named recording mutation owner or generation unavailable.");
                }
                if(compoundInput&&(namedRecordingFaulted||namedRecordingWithdrawn||namedRecordingPendingSafety!=0))
                {
                    if(safety>0){QueueNamedSafetyUnderLock(safety,technicalReason);pendingFirst=true;}
                    else if(namedRecordingPendingSafety!=0)pendingFirst=true;
                    else throw new InvalidOperationException("Named recording authority unavailable; ordinary input blocked.");
                }
                else if(namedRecordingPendingSafety>0)
                {if(safety>0)QueueNamedSafetyUnderLock(safety,technicalReason);pendingFirst=true;}
                else
                {
                    if((namedRecordingFaulted||namedRecordingWithdrawn)&&safety==0&&(experiment.Configuration.RawTaskRequested||experiment.Configuration.RawEyeRequested))
                        throw new InvalidOperationException("Named recording authority faulted; ordinary task action blocked.");
                    if(namedRecordingMutationGeneration>=Int64.MaxValue-1)
                    {
                        namedRecordingFaulted=true;
                        if(safety>0){QueueNamedSafetyUnderLock(safety,technicalReason);pendingFirst=true;}
                        else throw new InvalidOperationException("Named recording mutation generation exhausted.");
                    }
                    else{namedRecordingMutationGeneration++;namedRecordingMutationActive=true;}
                }
            }
            if(pendingFirst){DrainNamedRecordingSafety();if(safety==0)throw new InvalidOperationException("Pending safety action handled; ordinary action was not run.");return;}
            bool completed=false;
            try{action();completed=true;}
            finally
            {
                lock(namedRecordingGate)
                {namedRecordingMutationActive=false;namedRecordingMutationGeneration++;if(!completed)namedRecordingFaulted=true;}
                DrainNamedRecordingSafety();
            }
        }
        void QueueNamedSafetyUnderLock(int safety,string technicalReason)
        {
            if(safety>namedRecordingPendingSafety)
            {namedRecordingPendingSafety=safety;namedRecordingPendingTechnicalReason=safety==3?technicalReason:null;}
        }
        void DrainNamedRecordingSafety()
        {
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=namedViewOwnerThread)return;
            // Bound reentrant callbacks; any excess remains pending, and the
            // recording proof is already permanently faulted.
            for(int pass=0;pass<2;pass++)
            {
                int pending;bool countable;string technicalReason;
                lock(namedRecordingGate)
                {
                    if(namedRecordingMutationActive||namedRecordingPendingSafety==0)return;
                    countable=namedRecordingMutationGeneration<Int64.MaxValue-1;
                    if(!countable)namedRecordingFaulted=true;
                    pending=namedRecordingPendingSafety;technicalReason=namedRecordingPendingTechnicalReason;
                    namedRecordingPendingSafety=0;namedRecordingPendingTechnicalReason=null;namedRecordingMutationActive=true;if(countable)namedRecordingMutationGeneration++;
                }
                bool success=false;
                try
                {
                    if(pending==4)ParticipantWithdrawCore();else if(pending==3)EndTechnicalCore(Actor.Owner,technicalReason);
                    else if(pending==2)OwnerPauseCore(Actor.Owner);else ParticipantPauseCore();
                    success=true;
                }
                catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
                {/* Caller observes actual phase; no safety success is claimed here. */}
                finally{lock(namedRecordingGate){namedRecordingMutationActive=false;if(countable)namedRecordingMutationGeneration++;if(!success)namedRecordingFaulted=true;}}
            }
        }
        internal sealed class NamedRecordingTaskStamp
        {
            internal readonly ViveDevelopmentParticipant Owner;internal readonly IClock Clock;
            internal readonly ViveRawSessionContext Context;
            internal readonly SessionConfiguration Configuration;
            internal readonly ViveNamedSimulationPlan Plan;internal readonly ViveNamedRecordingIntent Intent;
            internal readonly long Generation,EventCursor;internal readonly string Session,TrialId;
            internal readonly Phase Phase;internal readonly int Block,Segment;
            internal NamedRecordingTaskStamp(ViveDevelopmentParticipant owner,IClock clock,ViveNamedSimulationPlan plan,ViveNamedRecordingIntent intent,
                long generation,long cursor,string session,Phase phase,int block,int segment,string trial)
            {Owner=owner;Clock=clock;Context=owner==null?null:owner.rawContext;Configuration=owner==null?null:owner.experiment.Configuration;Plan=plan;Intent=intent;Generation=generation;EventCursor=cursor;Session=session;Phase=phase;Block=block;Segment=segment;TrialId=trial;}
        }
        internal object NamedRecordingGate {get{return namedRecordingGate;}}
        internal enum NamedRawSafetyResult { Pending, Applied, NotApplied, Uncertain }
        internal enum NamedRawSafetyCause { ParticipantPause, OwnerPause, TechnicalFault, Withdrawal }
        // This owner-thread-only path is limited to the named Simulation with
        // legacy observation disabled. It never waits for either safety lock.
        // The caller must first revoke the pump and block presentation; this
        // return value does not assert that any in-flight I/O has stopped.
        internal NamedRawSafetyResult TryApplyNamedRawSafety(bool withdraw)
        {return TryApplyNamedRawSafetyForCause(withdraw?NamedRawSafetyCause.Withdrawal:NamedRawSafetyCause.ParticipantPause);}
        internal NamedRawSafetyResult TryApplyNamedRawSafetyForCause(NamedRawSafetyCause cause)
        {
            bool withdraw=cause==NamedRawSafetyCause.Withdrawal;
            if(!Enum.IsDefined(typeof(NamedRawSafetyCause),cause))return NamedRawSafetyResult.NotApplied;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=namedViewOwnerThread||observation!=null||
                experiment.Configuration.RunKind!=RunKind.Simulation||!RawRecordingRequested)return NamedRawSafetyResult.NotApplied;
            if(!System.Threading.Monitor.TryEnter(namedRecordingGate))return NamedRawSafetyResult.Pending;
            bool viewHeld=false;
            try
            {
                if(namedRecordingMutationActive)return NamedRawSafetyResult.Pending;
                if(withdraw)
                {
                    if(!System.Threading.Monitor.TryEnter(namedViewGate))return NamedRawSafetyResult.Pending;
                    viewHeld=true;
                }
                if(namedRecordingPendingSafety!=0)DrainNamedRecordingSafety();
                if(withdraw)
                {
                    if(experiment.Phase==Phase.Ended)
                        return experiment.EndReason==Core.EndReason.Withdrawn?NamedRawSafetyResult.Applied:NamedRawSafetyResult.NotApplied;
                    ParticipantWithdraw();
                    return experiment.Phase==Phase.Ended&&experiment.EndReason==Core.EndReason.Withdrawn?NamedRawSafetyResult.Applied:NamedRawSafetyResult.Uncertain;
                }
                if(experiment.Phase==Phase.Paused||experiment.Phase==Phase.Ended)return NamedRawSafetyResult.Applied;
                if(!Pausable(experiment.Phase))return NamedRawSafetyResult.NotApplied;
                // There is no technical actor in the protocol. Keep the
                // presentation blocked rather than inventing a voluntary pause.
                if(cause==NamedRawSafetyCause.TechnicalFault)return NamedRawSafetyResult.NotApplied;
                if(cause==NamedRawSafetyCause.OwnerPause)OwnerPause(Actor.Owner);else ParticipantPause();
                return experiment.Phase==Phase.Paused||experiment.Phase==Phase.Ended?NamedRawSafetyResult.Applied:NamedRawSafetyResult.Uncertain;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {return NamedRawSafetyResult.Uncertain;}
            finally{if(viewHeld)System.Threading.Monitor.Exit(namedViewGate);System.Threading.Monitor.Exit(namedRecordingGate);}
        }
        public bool NamedRecordingAuthorityFaulted
        {get{lock(namedRecordingGate)return namedRecordingFaulted||namedRecordingWithdrawn||namedRecordingPendingSafety!=0;}}
        public bool NamedRecordingWithdrawalRequested
        {get{return namedRawRevocation.ParticipantWithdrawalRequested;}}
        public bool NamedRecordingRequested
        {get{var intent=experiment.Configuration.NamedRecordingIntent;return intent!=null&&(intent.TaskRequested||intent.EyeRequested);}}
        public bool LegacyRecordingRequested
        {get{return rawContext!=null&&rawContext.IsLegacy&&(rawContext.TaskRequested||rawContext.EyeRequested);}}
        internal bool RawRecordingRequested
        {get{return rawContext!=null&&(rawContext.TaskRequested||rawContext.EyeRequested);}}
        // Called by the actual owner even while rendering is blocked. A
        // foreign safety request is not reported as complete until phase is
        // checked after the owner drains it. Fault-induced pause is attempted
        // once; a failure remains unconfirmed rather than silently retried.
        public bool PumpNamedRecordingSafety()
        {
            if(!experiment.Configuration.HasRawRecordingIntent)return true;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=namedViewOwnerThread)return false;
            bool pausable=Pausable(experiment.Phase);
            lock(namedRecordingGate)
            {
                if(namedRecordingFaulted&&RawRecordingRequested&&!namedRecordingWithdrawn&&pausable&&
                    !namedRecordingFaultPauseAttempted&&namedRecordingPendingSafety==0)
                {namedRecordingFaultPauseAttempted=true;QueueNamedSafetyUnderLock(1,null);}
            }
            DrainNamedRecordingSafety();
            lock(namedRecordingGate)
                return namedRecordingPendingSafety==0&&!namedRecordingMutationActive&&
                    (experiment.Phase==Phase.Paused||experiment.Phase==Phase.Ended||!pausable);
        }
        internal bool NamedRecordingStampCurrentUnderLock(NamedRecordingTaskStamp stamp)
        {
            return System.Threading.Monitor.IsEntered(namedRecordingGate)&&stamp!=null&&Object.ReferenceEquals(stamp.Owner,this)&&
                Object.ReferenceEquals(stamp.Clock,eventClock)&&Object.ReferenceEquals(stamp.Context,rawContext)&&Object.ReferenceEquals(stamp.Configuration,experiment.Configuration)&&rawContext!=null&&Object.ReferenceEquals(stamp.Plan,experiment.Configuration.NamedPlan)&&
                Object.ReferenceEquals(stamp.Intent,experiment.Configuration.NamedRecordingIntent)&&!namedRecordingFaulted&&!namedRecordingWithdrawn&&
                !namedRecordingMutationActive&&namedRecordingPendingSafety==0&&stamp.Generation==namedRecordingMutationGeneration;
        }
        internal bool TryCaptureNamedRecordingTaskStamp(IClock clock,out NamedRecordingTaskStamp stamp)
        {
            stamp=null;if(rawContext==null||rawContext.IsLegacy)return false;
            return TryCaptureRawRecordingTaskStamp(clock,out stamp);
        }
        internal bool TryCaptureLegacyRecordingTaskStamp(IClock clock,out NamedRecordingTaskStamp stamp)
        {
            stamp=null;if(rawContext==null||!rawContext.IsLegacy)return false;
            return TryCaptureRawRecordingTaskStamp(clock,out stamp);
        }
        internal bool TryCaptureRawRecordingTaskStamp(IClock clock,out NamedRecordingTaskStamp stamp)
        {
            stamp=null;if(rawContext==null||!Object.ReferenceEquals(clock,eventClock)||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=namedViewOwnerThread)return false;
            long generation;
            if(!System.Threading.Monitor.TryEnter(namedRecordingGate))return false;
            try{if(namedRecordingFaulted||namedRecordingWithdrawn||namedRecordingMutationActive)return false;generation=namedRecordingMutationGeneration;}
            finally{System.Threading.Monitor.Exit(namedRecordingGate);}
            var trial=experiment.CurrentTrial;
            var candidate=new NamedRecordingTaskStamp(this,eventClock,experiment.Configuration.NamedPlan,experiment.Configuration.NamedRecordingIntent,
                generation,experiment.Events.Count-1,experiment.Configuration.SessionId,experiment.Phase,experiment.BlockIndex,experiment.Segment,trial==null?null:trial.TrialId);
            if(!System.Threading.Monitor.TryEnter(namedRecordingGate))return false;
            try{if(!NamedRecordingStampCurrentUnderLock(candidate))return false;stamp=candidate;return true;}
            finally{System.Threading.Monitor.Exit(namedRecordingGate);}
        }

        // Local development context only. Its contents are declarations, not
        // a source authorization, remote identity or permission to render/send.
        // The caller must revalidate before each use; copied values cannot be
        // recalled from a caller after a later withdrawal or task mutation.
        internal bool TryCaptureNamedWozContext(IClock clock,out ViveWozParticipantHost.NamedContext context)
        {
            context=null;NamedRecordingTaskStamp stamp;
            if(observation!=null||!experiment.Configuration.IsNamedDesign||experiment.Configuration.RunKind!=RunKind.Simulation||
                namedRawRevocation.IsRevoked||!TryCaptureNamedRecordingTaskStamp(clock,out stamp))return false;
            var selection=experiment.CurrentNamedSelection;var block=experiment.CurrentNamedPlanBlock;
            var candidate=new ViveWozParticipantHost.NamedContext(this,stamp,stamp.Session,stamp.Phase,stamp.Block,stamp.Segment,
                stamp.Plan==null?null:stamp.Plan.PlanId,block==null?(int?)null:block.Index,
                selection==null?(ViveNamedOption?)null:selection.Option,selection==null?(int?)null:selection.ListIndex,
                experiment.NamedOptionConfirmed);
            if(!IsNamedWozContextCurrent(candidate))return false;
            context=candidate;return true;
        }
        internal bool IsNamedWozContextCurrent(ViveWozParticipantHost.NamedContext candidate)
        {
            if(candidate==null||!Object.ReferenceEquals(candidate.Owner,this)||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=namedViewOwnerThread||namedRawRevocation.IsRevoked||
                !System.Threading.Monitor.TryEnter(namedRecordingGate))return false;
            try
            {
                if(namedRawRevocation.IsRevoked||!NamedRecordingStampCurrentUnderLock(candidate.Stamp))return false;
                var selection=experiment.CurrentNamedSelection;var block=experiment.CurrentNamedPlanBlock;
                return candidate.Session==experiment.Configuration.SessionId&&candidate.Phase==experiment.Phase&&
                    candidate.Block==experiment.BlockIndex&&candidate.Segment==experiment.Segment&&
                    candidate.PlanId==(experiment.Configuration.NamedPlan==null?null:experiment.Configuration.NamedPlan.PlanId)&&
                    candidate.PlanBlock==(block==null?(int?)null:block.Index)&&
                    candidate.DeclaredOption==(selection==null?(ViveNamedOption?)null:selection.Option)&&
                    candidate.ListIndex==(selection==null?(int?)null:selection.ListIndex)&&candidate.PlanConfirmed==experiment.NamedOptionConfirmed&&
                    !namedRawRevocation.IsRevoked;
            }
            finally{System.Threading.Monitor.Exit(namedRecordingGate);}
        }
        bool BeginOrdinaryObservedAction()
        {
            int prior=System.Threading.Interlocked.CompareExchange(ref observationActionState,1,0);
            if(prior==0)return true;
            if(prior==1)FaultObservation("observation_action_reentrancy");
            return false;
        }
        void EndOrdinaryObservedAction()
        {System.Threading.Interlocked.CompareExchange(ref observationActionState,0,1);}
        // Advance at most 16 complete commits per call. A false return can mean
        // either bounded backlog or a terminal fault; callers inspect status.
        public bool SynchronizeObservation()
        {
            if(observation==null)return true;
            try
            {
                if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)
                {FaultObservation("observation_owner_thread_violation");return false;}
                if(ObservationFaulted)return false;
                ViveSyntheticAggregateHead head;
                bool current=observation.Advance(16)&&observation.TryGet(out head);
                observationPending=!current;
                return current;
            }
            catch(Exception error)
            {FaultObservation("observation_sync:"+error.GetType().Name);observationPending=true;return false;}
        }
        void Observed(Action action,bool safety)
        {
            if(observation==null){action();return;}
            EnterFenceAction();
            try{ObservedCore(action,safety);}finally{ExitFenceAction();}
        }
        void ObservedCore(Action action,bool safety)
        {
            // Even emergency actions cannot safely mutate Experiment from an
            // unowned thread. Their owner-thread path remains available on fault.
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)
            {SynchronizeObservation();throw new InvalidOperationException("Observation owner thread required.");}
            bool ready=SynchronizeObservation();
            if(!ready&&!safety)throw new InvalidOperationException("Development observation unavailable; action was not applied.");
            if(!safety&&!BeginOrdinaryObservedAction())throw new InvalidOperationException("Development observation unavailable; action was not applied.");
            try{action();}
            finally{if(!safety)EndOrdinaryObservedAction();SynchronizeObservation();}
        }
        // A public method can contain several distinct Experiment transactions.
        // Recheck between them so a completed action with a failed observer
        // cannot be followed by a second ordinary mutation in the same call.
        bool MayContinueOrdinaryMutation()
        {
            var namedIntent=rawContext;
            if(namedIntent!=null)
            {
                lock(namedRecordingGate)return !namedRecordingWithdrawn&&namedRecordingPendingSafety==0&&
                    (!namedRecordingFaulted||(!namedIntent.TaskRequested&&!namedIntent.EyeRequested));
            }
            if(observation==null)return true;
            EndOrdinaryObservedAction();
            return SynchronizeObservation()&&BeginOrdinaryObservedAction();
        }

        public string SessionId {get{return experiment.Configuration.SessionId;}}
        public bool HasNamedOption {get{return experiment.Configuration.IsNamedDesign;}}
        public ViveNamedSimulationPlan NamedPlan {get{return experiment.Configuration.NamedPlan;}}
        public ViveNamedSimulationBlock CurrentNamedPlanBlock {get{return experiment.CurrentNamedPlanBlock;}}
        public ViveNamedOptionProvenance CurrentNamedSelection {get{return experiment.CurrentNamedSelection;}}
        void MarkNamedViewWithdrawn(){lock(namedViewGate)namedViewWithdrawn=true;}
        public bool NamedViewWithdrawalRequested {get{lock(namedViewGate)return namedViewWithdrawn;}}
        bool NamedViewEligibleUnderLock()
        {
            return !namedViewWithdrawn&&experiment.Configuration.IsNamedDesign&&
                experiment.Configuration.RunKind==RunKind.Simulation&&experiment.Phase==Phase.Ended&&
                experiment.EndReason.HasValue&&experiment.EndReason.Value!=Core.EndReason.Withdrawn;
        }
        internal bool CanStartNamedView()
        {lock(namedViewGate)return System.Threading.Thread.CurrentThread.ManagedThreadId==namedViewOwnerThread&&NamedViewEligibleUnderLock();}
        internal bool TryPublishNamedView(ViveNamedTaskMemoryView candidate,out ViveNamedTaskMemoryView result)
        {
            result=null;lock(namedViewGate)
            {
                if(System.Threading.Thread.CurrentThread.ManagedThreadId!=namedViewOwnerThread||!NamedViewEligibleUnderLock()||
                    candidate==null||!candidate.BelongsTo(this))return false;
                result=candidate;return true;
            }
        }
        internal bool NamedViewStillAllowed(ViveNamedTaskMemoryView candidate)
        {lock(namedViewGate)return candidate!=null&&candidate.BelongsTo(this)&&NamedViewEligibleUnderLock();}
        // Copy only immutable values from this owner's ended Simulation task.
        // Do not expose mutable TrialRecord references. The caller must still
        // publish via TryPublishNamedView, which serializes against withdrawal.
        internal bool TryCopyEndedNamedTaskRecords(out ViveNamedTaskRecordSnapshot records)
        {
            records=null;if(!CanStartNamedView())return false;
            int events=experiment.Events.Count,trials=experiment.Trials.Count,ratings=experiment.Ratings.Count;
            var copy=new ViveNamedTaskRecordSnapshot(experiment.Trials,experiment.Ratings);
            if(!CanStartNamedView()||events!=experiment.Events.Count||trials!=experiment.Trials.Count||ratings!=experiment.Ratings.Count)return false;
            records=copy;return true;
        }
        public ViveNamedOptionProvenance NamedOption {get{return experiment.Configuration.NamedOption;}}
        public int AuthoritativeEventCount {get{return experiment.Events.Count;}}
        public string EventClockId {get{return eventClockId;}}
        public bool UsesEventClock(IClock candidate){return Object.ReferenceEquals(eventClock,candidate);}
        // Bounded immutable copies of authoritative events; no inference from
        // the latest phase. The caller owns the cursor and must route all epochs.
        public IReadOnlyList<JournalEvent> ReadEventsAfter(long sequenceExclusive,int maximumCount)
        {
            if(sequenceExclusive< -1)throw new ArgumentOutOfRangeException("sequenceExclusive");
            if(maximumCount<1||maximumCount>4096)throw new ArgumentOutOfRangeException("maximumCount");
            bool v3=experiment.Configuration.HasRawRecordingIntent;long generation=-1;
            if(v3)
            {
                if(!System.Threading.Monitor.TryEnter(namedRecordingGate))throw new InvalidOperationException("Named recording task events unavailable.");
                try
                {
                    if(System.Threading.Thread.CurrentThread.ManagedThreadId!=namedViewOwnerThread||namedRecordingFaulted||namedRecordingWithdrawn||namedRecordingMutationActive)
                        throw new InvalidOperationException("Named recording task events unavailable.");
                    generation=namedRecordingMutationGeneration;
                }
                finally{System.Threading.Monitor.Exit(namedRecordingGate);}
            }
            var source=experiment.Events;
            if(sequenceExclusive>=source.Count)throw new ArgumentOutOfRangeException("sequenceExclusive","The cursor cannot skip unseen events");
            var copies=new List<JournalEvent>();
            // Experiment assigns its append-only event sequence from the list
            // index. Start at the cursor rather than rescanning the session.
            for(int index=(int)(sequenceExclusive+1);index<source.Count&&copies.Count<maximumCount;index++)
            {
                var item=source[index];
                if(item.Sequence!=index)throw new InvalidOperationException("Named recording task event sequence unavailable.");
                var fields=new Dictionary<string,string>();foreach(var field in item.Fields)fields.Add(field.Key,field.Value);
                copies.Add(new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,item.TrialId,item.Kind,fields,item.NamedOption,item.NamedPlan,item.NamedBlock,item.NamedRecordingIntent,item.LegacyRecordingDesign,item.LegacyRecordingIntent));
            }
            if(v3)
            {
                if(!System.Threading.Monitor.TryEnter(namedRecordingGate))throw new InvalidOperationException("Named recording task events unavailable.");
                try{if(namedRecordingFaulted||namedRecordingWithdrawn||namedRecordingMutationActive||generation!=namedRecordingMutationGeneration)
                    throw new InvalidOperationException("Named recording task changed during event read.");}
                finally{System.Threading.Monitor.Exit(namedRecordingGate);}
            }
            return new ReadOnlyCollection<JournalEvent>(copies);
        }

        public bool InputReady {get{return inputReady;}}
        public bool RecordsEyeSamples {get{return false;}}
        public bool RecordsTaskFiles {get{return false;}}
        public bool UsesNetwork {get{return false;}}
        public bool IsResearchSession {get{return false;}}
        public string ScopeLabel {get{return "SIMULATED DEVELOPMENT TASK - NO RESEARCH DATA OR LIVE WOZ";}}
        public Phase Phase {get{return experiment.Phase;}}
        // UI-only first-cause diagnostics, not journal events or research data.
        // A repeated request while already paused never replaces the cause.
        public long PauseDiagnosticRevision {get;private set;}
        public string LastPauseDiagnostic {get;private set;}
        void PauseWithDiagnostic(Actor actor,string reason)
        {
            Phase before=experiment.Phase;long beforeRevision=PauseDiagnosticRevision;
            try{experiment.Pause(actor);}
            finally
            {
                if(before!=Phase.Paused&&experiment.Phase==Phase.Paused&&PauseDiagnosticRevision==beforeRevision)
                {LastPauseDiagnostic=reason;if(PauseDiagnosticRevision<Int64.MaxValue)PauseDiagnosticRevision++;}
            }
        }
        public int BlockIndex {get{return experiment.BlockIndex;}}
        public int Segment {get{return experiment.Segment;}}
        public int QuestionIndex {get{return experiment.QuestionIndex;}}
        public int CandidateRating {get{return candidate;}}
        public bool QuestionnaireInstructionPending {get{return experiment.QuestionnaireInstructionPending;}}
        public bool QuestionnaireInputReady {get{return experiment.QuestionnaireInputReady;}}
        public bool ReadyEnabled {get{return experiment.ReadyEnabled;}}
        public bool SourceContinuationPending {get{return experiment.RetainedSourceContinuationPending;}}
        public ControlSelection? LockedControl {get{return experiment.LockedControl;}}
        public EndReason? EndReason {get{return experiment.EndReason;}}
        public bool Completed {get{return experiment.Phase==Phase.Ended&&experiment.EndReason==Core.EndReason.Completed;}}
        public int TrialCount {get{return experiment.Trials.Count;}}
        public int RatingCount {get{return experiment.Ratings.Count;}}
        public TrialRecord CurrentTrial {get{return experiment.CurrentTrial;}}
        public double CountdownRemainingSeconds {get{return experiment.CountdownRemainingSeconds;}}
        public double RestRemainingSeconds {get{return experiment.RestRemainingSeconds;}}
        public double RestDurationSeconds {get{return experiment.Configuration.MinimumRestSeconds;}}
        public double CountdownDurationSeconds {get{return experiment.Configuration.CountdownSeconds;}}
        public TaskView ParticipantView {get{return experiment.View(Actor.Participant);}}
        public TaskView OperatorView {get{return experiment.View(Actor.Operator);}}

        // Internal, simulation-only test seam around THIS participant's real
        // Experiment and its existing single observation consumer. It is not
        // exposed by either Unity bootstrap and never grants a Live countdown,
        // starts a device/network, or authenticates an owner or remote peer.
        internal void LockSyntheticWozForTest(Actor actor,string ownerId)
        {
            if(observation==null)throw new InvalidOperationException("Incremental simulation observation is required");
            Observed(()=>{RequireOwner(actor);RequireReady();experiment.LockBlock(actor,ControlSelection.LiveWoz,ownerId);},false);
        }
        internal bool ConfirmSyntheticWozForTest(Actor actor)
        {
            if(observation==null)throw new InvalidOperationException("Incremental simulation observation is required");
            RequireOwner(actor);
            if(!SynchronizeObservation()||ObservationBlocked)return false;
            return observation.ConfirmSyntheticOwnerChoice(actor);
        }
        internal SimulationWozAuthority CreateSyntheticWozAuthority(Action clearLivePose)
        {
            if(observation==null||clearLivePose==null)throw new ArgumentException("Observed simulation and clear callback required");
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)throw new InvalidOperationException("Observation owner thread required");
            return new SimulationWozAuthority(this,clearLivePose);
        }
        internal sealed class SimulationWozAuthority : IVivePreflightBoundParticipantHost,IViveWozSourceStateReader
        {
            readonly ViveDevelopmentParticipant participant;readonly Action clear;readonly int owner;
            internal SimulationWozAuthority(ViveDevelopmentParticipant participant,Action clear)
            {this.participant=participant;this.clear=clear;owner=System.Threading.Thread.CurrentThread.ManagedThreadId;}
            void Owner(){if(System.Threading.Thread.CurrentThread.ManagedThreadId!=owner)throw new InvalidOperationException("Simulation authority belongs to its owner thread");}
            public string BoundSessionId {get{Owner();return participant.SessionId;}}
            public bool LiveSourceSelected {get{Owner();return participant.experiment.LockedControl==ControlSelection.LiveWoz;}}
            public TaskSnapshot ReadAuthoritativeTask()
            {
                Owner();if(participant.ObservationBlocked)return null;
                ViveSyntheticAggregateHead head;
                if(!participant.observation.TryGet(out head)||head==null||head.Task==null)return null;
                var e=participant.experiment;var view=e.View(Actor.Operator);var stimulus=view.Stimulus;
                return new TaskSnapshot(e.Configuration.SessionId,e.BlockIndex,e.Segment,view.TrialId,
                    stimulus==null?(Colour?)null:stimulus.Word,stimulus==null?(Colour?)null:stimulus.Ink,e.Phase,e.CurrentCondition);
            }
            public ViveWozSourceLocalState ReadCurrent()
            {
                Owner();if(participant.ObservationBlocked)return null;ViveSyntheticAggregateHead head;
                if(!participant.observation.TryGet(out head)||head==null||head.Snapshot==null||head.Task==null)return null;
                var s=head.Snapshot;
                return new ViveWozSourceLocalState(participant.SessionId,s.SourceOwner,head.Task,s.Selection,
                    head.ChoiceState==ViveSyntheticChoiceState.ActiveCandidate,true,head.Revision);
            }
            public void ClearLivePose(){Owner();clear();}
            public void ApplyLivePose(ViveDevelopmentRemotePose pose)
            {Owner();clear();throw new InvalidOperationException("Simulation seam does not admit v2 live presentation");}
            public void PauseByOperator(){Owner();participant.SyntheticPause(Actor.Operator);clear();}
            public void PauseForTransportLoss(){Owner();participant.SyntheticPause(Actor.Owner);clear();}
        }
        void SyntheticPause(Actor actor)
        {
            if(observation==null)throw new InvalidOperationException("Observed simulation required");
            Observed(()=>{Release(trackpad.Reset());ResetAuxiliary();PauseWithDiagnostic(actor,"Simulation operator or transport safety request.");},true);
        }

        public AvatarOutput CurrentAvatar
        {
            get
            {
                if(experiment.Configuration.IsNamedDesign&&experiment.CurrentNamedSelection!=null&&experiment.CurrentNamedSelection.Option==ViveNamedOption.NoAvatar)
                    return null; // structurally absent, not a missing or invalid tracked sample
                if(!experiment.LockedControl.HasValue||experiment.BlockIndex<0)return null;
                if(experiment.LockedControl.Value!=ControlSelection.Automatic)throw new InvalidOperationException("Only the automatic development source is supported");
                if(experiment.Configuration.IsNamedDesign)
                {
                    var named=experiment.CurrentNamedSelection;
                    var trajectory=ViveAutomaticCondition.Evaluate(experiment.ActiveSeconds,geometry);
                    var reference=new ReferenceGaze{Pose=trajectory.Pose,Phase=trajectory.Phase,FromTarget=trajectory.FromTarget,
                        ToTarget=trajectory.ToTarget,TransitionFraction=trajectory.TransitionFraction,ActiveSeconds=trajectory.ActiveSeconds,
                        CycleSeconds=trajectory.CycleSeconds,GeometryVersion=trajectory.GeometryVersion};
                    return new AvatarOutput{NamedOption=named,NamedBlock=experiment.CurrentNamedPlanBlock,AvatarVisible=experiment.Phase==Phase.MainTask,
                        SelectedSource=ControlSource.Automatic,ActualSource=ControlSource.Automatic,ActualPose=trajectory.Pose,
                        Reference=reference,HeadValid=true,EyeValid=true,Status=GazeOutputStatus.Automatic,
                        MappingVersion=geometry.Version};
                }
                if(avatar==null||avatarBlock!=experiment.BlockIndex||avatarSegment!=experiment.Segment)
                {
                    avatarBlock=experiment.BlockIndex;avatarSegment=experiment.Segment;
                    avatar=new AvatarController(experiment.Configuration.SessionId,RunKind.Simulation,avatarBlock,avatarSegment,
                        ControlSelection.Automatic,experiment.CurrentCondition.Value,experiment.Configuration.Allocation.BInitialTarget,geometry);
                }
                return avatar.Render(experiment.Phase,experiment.ActiveSeconds);
            }
        }

        static void RequireOwner(Actor actor)
        {if(actor!=Actor.Owner)throw new UnauthorizedAccessException("Owner action required");}
        void RequireReady()
        {if(!inputReady)throw new InvalidOperationException("Current headset and configured trackpad must be ready");}
        // Input edges/readiness are not all represented by Experiment steps.
        // Once Retry is reached, remember any non-neutral sample or device
        // loss even if it leaves no task event. This is only a development
        // fence and does not authenticate an actual headset or controller.
        void MarkRetryInputHazard(bool unsafeInput)
        {
            if(!unsafeInput||observation==null||experiment.Phase!=Phase.PracticeRetry)return;
            bool overflow=false;
            lock(observationFenceGate)
            {if(retryInputRevision==Int64.MaxValue)overflow=true;else retryInputRevision++;}
            if(overflow)FaultObservation("retry_input_revision_overflow");
        }
        void Release(IEnumerable<Colour> colours)
        {foreach(var colour in colours)experiment.ButtonChanged(colour,false);}
        static bool Pausable(Phase phase)
        {return phase==Phase.Practice||phase==Phase.PracticeCorrection||phase==Phase.Countdown||phase==Phase.MainTask||phase==Phase.Questionnaire||phase==Phase.Rest;}

        // A connected device may legitimately be waiting for held controls to
        // return neutral at a phase boundary. Suppress actions and release the
        // previous colour, but do not reclassify that wait as device loss. The
        // host must call UpdateTrackpad with DeviceReady=false for a disconnect
        // or malformed sample; a false headset signal here remains a true loss.
        public void UpdateNeutralPending(bool headReady)
        {if(observation==null){RunNamedRecordingMutation(()=>UpdateNeutralPendingCore(headReady));return;}if(System.Threading.Thread.CurrentThread.ManagedThreadId==observationOwnerThread&&experiment.Phase==Phase.PracticeRetry)retryTrackpadNeutralObserved=false;MarkRetryInputHazard(!headReady);Observed(()=>UpdateNeutralPendingCore(headReady),!headReady);}
        void UpdateNeutralPendingCore(bool headReady)
        {
            Release(trackpad.Reset());inputReady=false;ResetAuxiliary();
            if(!headReady&&Pausable(experiment.Phase))PauseWithDiagnostic(Actor.Participant,"Automatic safety: head/input readiness lost.");
        }

        // The host calls this once per current sample. Loss invalidates edges,
        // forwards any held release and explicitly pauses an active phase. A
        // later healthy sample does not resume or replay the interrupted trial.
        public void UpdateTrackpad(bool headReady,VivePreviewTrackpadSample sample)
        {
            if(observation==null){RunNamedRecordingMutation(()=>UpdateTrackpadCore(headReady,sample));return;}
            if(System.Threading.Thread.CurrentThread.ManagedThreadId==observationOwnerThread&&experiment.Phase==Phase.PracticeRetry)
            {
                bool ready=headReady&&sample!=null&&sample.Usable;
                MarkRetryInputHazard(!ready||(sample.Pressed&&!trackpad.WasPressed));
                retryTrackpadNeutralObserved=ready&&!sample.Pressed;
            }
            Observed(()=>UpdateTrackpadCore(headReady,sample),!headReady||(sample!=null&&!sample.Usable));
        }
        void UpdateTrackpadCore(bool headReady,VivePreviewTrackpadSample sample)
        {
            if(sample==null)throw new ArgumentNullException("sample");
            bool ready=headReady&&sample.Usable;
            if(!ready)
            {
                Release(trackpad.Reset());inputReady=false;ResetAuxiliary();
                if(Pausable(experiment.Phase))PauseWithDiagnostic(Actor.Participant,headReady?"Automatic safety: controller sample unavailable.":"Automatic safety: head/input readiness lost.");
                return;
            }
            inputReady=true;
            var frame=trackpad.Update(experiment.Phase,sample);
            Release(frame.ReleasedColours);
            if(frame.PressedColour.HasValue&&MayContinueOrdinaryMutation())experiment.ButtonChanged(frame.PressedColour.Value,true);
        }
        void ResetAuxiliary()
        {
            if(observation!=null&&experiment.Phase==Phase.PracticeRetry)
            {retryTrackpadNeutralObserved=false;retryAuxNeutralObserved=false;}
            auxSampled=false;ratingArmed=false;previousConfirm=previousPause=previousWithdraw=false;previousDisplayReady=false;previousInstruction=false;auxiliaryItem=-1;candidate=3;
        }

        // Semantics only: the host decides which physical bindings implement
        // confirm/pause/withdraw and rating direction; no VIVE controls are guessed.
        // ratingDirection -1 means UP/lower, +1 means DOWN/higher; it must return
        // to zero between adjustments. Display readiness must come from the view.
        public void UpdateAuxiliary(bool confirm,bool pause,bool withdraw,int ratingDirection,bool phaseDisplayReady)
        {
            if(withdraw){MarkNamedRecordingWithdrawn();MarkNamedViewWithdrawn();}
            if(experiment.Configuration.HasRawRecordingIntent)
            {var hook=BeforeNamedAuxiliaryReservationForTest;if(hook!=null)hook();}
            if(observation==null){RunNamedRecordingMutation(()=>UpdateAuxiliaryCore(confirm,pause,withdraw,ratingDirection,phaseDisplayReady),withdraw?4:pause?1:0,null,true);return;}
            if(System.Threading.Thread.CurrentThread.ManagedThreadId==observationOwnerThread&&experiment.Phase==Phase.PracticeRetry)
            {
                bool neutral=!confirm&&!pause&&!withdraw&&ratingDirection==0&&phaseDisplayReady&&inputReady;
                MarkRetryInputHazard(!neutral);retryAuxNeutralObserved=neutral;
            }
            // A foreign thread cannot apply input edges to Experiment. Still
            // conservatively revoke the recording choice on a reported
            // withdraw signal before the normal owner-thread rejection.
            if(withdraw&&System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)RevokePracticeConsent();
            EnterFenceAction();
            try{UpdateAuxiliaryObserved(confirm,pause,withdraw,ratingDirection,phaseDisplayReady);}finally{ExitFenceAction();}
        }
        void UpdateAuxiliaryObserved(bool confirm,bool pause,bool withdraw,int ratingDirection,bool phaseDisplayReady)
        {
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=observationOwnerThread)
            {SynchronizeObservation();throw new InvalidOperationException("Observation owner thread required.");}
            bool ready=SynchronizeObservation();
            bool began=ready&&BeginOrdinaryObservedAction();
            try
            {
                if(began)UpdateAuxiliaryCore(confirm,pause,withdraw,ratingDirection,phaseDisplayReady);
                else UpdateAuxiliarySafetyOnly(confirm,pause,withdraw);
            }
            finally{if(began)EndOrdinaryObservedAction();SynchronizeObservation();}
        }
        // When observation is unavailable, maintain the physical edge state
        // but allow only emergency actions; confirm and rating cannot leak
        // through the same input sample or be replayed when backlog clears.
        void UpdateAuxiliarySafetyOnly(bool confirm,bool pause,bool withdraw)
        {
            bool pauseEdge=pause&&!previousPause,withdrawEdge=withdraw&&!previousWithdraw;
            previousConfirm=confirm;previousPause=pause;previousWithdraw=withdraw;auxSampled=false;ratingArmed=false;
            if(withdrawEdge){RevokePracticeConsent();experiment.Withdraw();return;}
            if(pauseEdge&&experiment.Phase!=Phase.Paused&&experiment.Phase!=Phase.Ended&&experiment.Phase!=Phase.NeedsProtocolDecision)
                PauseWithDiagnostic(Actor.Participant,"Participant semantic pause request.");
        }
        void UpdateAuxiliaryCore(bool confirm,bool pause,bool withdraw,int ratingDirection,bool phaseDisplayReady)
        {
            if(ratingDirection < -1||ratingDirection > 1)throw new ArgumentOutOfRangeException("ratingDirection");
            if(!inputReady){ResetAuxiliary();return;}
            Phase phase=experiment.Phase;int item=phase==Phase.Questionnaire?experiment.QuestionIndex:-1;
            bool instruction=phase==Phase.Questionnaire&&experiment.QuestionnaireInstructionPending;
            bool context=!auxSampled||phase!=auxiliaryPhase||item!=auxiliaryItem;
            bool boundary=context||phaseDisplayReady!=previousDisplayReady||instruction!=previousInstruction;
            bool confirmEdge=confirm&&!previousConfirm,pauseEdge=pause&&!previousPause,withdrawEdge=withdraw&&!previousWithdraw;
            previousConfirm=confirm;previousPause=pause;previousWithdraw=withdraw;
            auxiliaryPhase=phase;auxiliaryItem=item;auxSampled=true;previousDisplayReady=phaseDisplayReady;previousInstruction=instruction;
            if(context)candidate=3;
            // A phase/panel visibility boundary must not consume an explicit
            // safety edge in the attached raw path. Ordinary confirmation
            // still waits for a later neutral sample and a fresh edge.
            bool rawAdmissionAttached=System.Threading.Volatile.Read(ref namedFrameAdmission)!=null;
            if(rawAdmissionAttached)
            {
                if(withdrawEdge){RevokePracticeConsent();experiment.Withdraw();return;}
                if(pauseEdge&&phase!=Phase.Paused&&phase!=Phase.Ended&&phase!=Phase.NeedsProtocolDecision)
                {PauseWithDiagnostic(Actor.Participant,"Participant semantic pause request.");return;}
            }
            if(boundary){ratingArmed=ratingDirection==0;return;}
            if(!rawAdmissionAttached&&withdrawEdge){RevokePracticeConsent();experiment.Withdraw();return;}
            if(!rawAdmissionAttached&&pauseEdge&&phase!=Phase.Paused&&phase!=Phase.Ended&&phase!=Phase.NeedsProtocolDecision)
            {PauseWithDiagnostic(Actor.Participant,"Participant semantic pause request.");return;}
            // The attached named-raw scene also requires an actual software
            // panel witness before correction or resume confirmation. Pause
            // and withdrawal above remain independent of that witness.
            if(!phaseDisplayReady&&(phase==Phase.Questionnaire||phase==Phase.Rest||
                (rawAdmissionAttached&&(phase==Phase.PracticeCorrection||phase==Phase.Paused))))return;
            if(phase==Phase.Questionnaire)
            {
                if(instruction){if(confirmEdge)experiment.AcknowledgeQuestionnaireInstruction();return;}
                if(ratingDirection==0)ratingArmed=true;
                else if(ratingArmed){candidate=Math.Max(1,Math.Min(5,candidate+ratingDirection));ratingArmed=false;return;}
                if(confirmEdge&&experiment.QuestionnaireInputReady)experiment.Rate(candidate);
                return;
            }
            if(!confirmEdge)return;
            if(phase==Phase.PracticeCorrection)experiment.ContinuePractice();
            else if(phase==Phase.Rest)experiment.Ready();
            else if(phase==Phase.Paused)
            {
                bool noAvatar=experiment.Configuration.IsNamedDesign&&experiment.CurrentNamedSelection!=null&&experiment.CurrentNamedSelection.Option==ViveNamedOption.NoAvatar;
                bool ready=inputReady&&(noAvatar?experiment.NamedOptionConfirmed:
                    experiment.LockedControl.GetValueOrDefault(ControlSelection.Automatic)==ControlSelection.Automatic);
                experiment.Resume(ready);
            }
        }
        public void OwnerBeginPractice(Actor actor)
        {
            if(System.Threading.Volatile.Read(ref namedFrameAdmission)!=null)throw new InvalidOperationException("A matching block plan write receipt is required.");
            if(observation==null){RunNamedRecordingMutation(()=>OwnerBeginPracticeCore(actor));return;}Observed(()=>OwnerBeginPracticeCore(actor),false);
        }
        // Prepares the Experiment's actual list; it does not authorize a
        // participant, start a phase, write a file or open the recording gate.
        internal JournalEvent PrepareNamedRecordingBlockPlan(Actor actor,bool practiceScope)
        {if(rawContext==null||rawContext.IsLegacy)throw new InvalidOperationException("Named recording context required.");return PrepareRawRecordingBlockPlan(actor,practiceScope);}
        internal JournalEvent PrepareLegacyRecordingBlockPlan(Actor actor,bool practiceScope)
        {if(rawContext==null||!rawContext.IsLegacy)throw new InvalidOperationException("Legacy recording context required.");return PrepareRawRecordingBlockPlan(actor,practiceScope);}
        internal bool MatchesNamedRecordingBlockPlan(JournalEvent prepared)
        {return rawContext!=null&&!rawContext.IsLegacy&&MatchesRawRecordingBlockPlan(prepared);}
        internal JournalEvent PrepareRawRecordingBlockPlan(Actor actor,bool practiceScope)
        {
            JournalEvent prepared=null;
            RunNamedRecordingMutation(()=>{RequireOwner(actor);prepared=experiment.PrepareRawRecordingBlockPlan(actor,practiceScope);});
            return prepared;
        }
        internal bool MatchesRawRecordingBlockPlan(JournalEvent prepared)
        {return experiment.MatchesRawRecordingBlockPlan(prepared);}
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        internal bool SetSyntheticOnsetHookForFixture(ViveNamedSessionFrameAdmission admission,Action hook)
        {
            if(admission==null||!admission.SyntheticOnly||!Object.ReferenceEquals(System.Threading.Volatile.Read(ref namedFrameAdmission),admission))return false;
            experiment.SyntheticAfterOnsetForFixture=hook;return true;
        }
        // Exists only in the fixture build. This simulates plan acceptance;
        // it cannot be passed to the product receipt/start method.
        internal bool TryBeginSyntheticLegacyBlockForFixture(ViveNamedSessionFrameAdmission admission,ViveBlockPlanRequest request,
            ViveNamedSessionFrameAdmission.SyntheticPlanProof proof)
        {
            var sharedClock=eventClock as MonotonicClock;
            if(sharedClock==null||rawContext==null||!rawContext.IsLegacy||admission==null||!admission.SyntheticOnly||!admission.IsLegacy||
                !Object.ReferenceEquals(System.Threading.Volatile.Read(ref namedFrameAdmission),admission)||
                request==null||request.PracticeScope||!admission.ConsumeSyntheticPlanProof(proof,request))return false;
            bool started=false;
            RunNamedRecordingMutation(()=>{if(namedRawRevocation.IsRevoked||!request.PlanMatchesInsideMutation(this,sharedClock))return;
                OwnerBeginBlockCore(Actor.Owner);started=true;});
            return started&&!namedRawRevocation.IsRevoked;
        }
        internal bool TryBeginSyntheticPracticeForFixture(ViveNamedSessionFrameAdmission admission,ViveBlockPlanRequest request,
            ViveNamedSessionFrameAdmission.SyntheticPlanProof proof)
        {
            var sharedClock=eventClock as MonotonicClock;
            if(sharedClock==null||admission==null||!admission.SyntheticOnly||
                !Object.ReferenceEquals(System.Threading.Volatile.Read(ref namedFrameAdmission),admission)||
                request==null||!request.PracticeScope||!admission.ConsumeSyntheticPlanProof(proof,request))return false;
            bool started=false;
            RunNamedRecordingMutation(()=>{if(namedRawRevocation.IsRevoked||!request.PlanMatchesInsideMutation(this,sharedClock))return;
                OwnerBeginPracticeCore(Actor.Owner);started=true;});
            return started&&!namedRawRevocation.IsRevoked;
        }
#endif
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        // Separate from the installed path: only a fixture-only receipt from
        // the native fixture's actual write/flush can authorize this start.
        internal bool TryBeginNativeFixtureWithPlan(ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink,ViveBlockPlanRequest request,
            ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt,bool practiceScope)
        {
            var sharedClock=eventClock as MonotonicClock;
            if(sharedClock==null||admission==null||!admission.SyntheticOnly||
                !Object.ReferenceEquals(System.Threading.Volatile.Read(ref namedFrameAdmission),admission)||
                sink==null||!sink.IsNativeFixture||!sink.BelongsTo(admission)||request==null||receipt==null||!receipt.FixtureOnly||
                !receipt.BelongsToNativeFixtureForTest(sink)||request.PracticeScope!=practiceScope||
                namedRawRevocation.IsRevoked||!request.TryValidateCurrentForReceipt(this,sharedClock)||
                !receipt.TryConsumeSyntheticForTest(request)||!request.TryValidateCurrentForReceipt(this,sharedClock))return false;
            bool started=false;
            RunNamedRecordingMutation(()=>{
                if(namedRawRevocation.IsRevoked||!request.PlanMatchesInsideMutation(this,sharedClock))return;
                if(practiceScope)OwnerBeginPracticeCore(Actor.Owner);else OwnerBeginBlockCore(Actor.Owner);
                started=true;
            });
            return started&&!namedRawRevocation.IsRevoked;
        }
#endif
        // The only recording-attached start path consumes the exact sink's
        // one-use API-return receipt. This is not research authorization or
        // crash durability, and production assembly is still blocked.
        internal bool TryBeginNamedRecordingWithPlan(Actor actor,ViveBlockPlanRequest request,ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt,bool practiceScope)
        {return rawContext!=null&&!rawContext.IsLegacy&&TryBeginRawRecordingWithPlan(actor,request,receipt,practiceScope);}
        internal bool TryBeginLegacyRecordingWithPlan(Actor actor,ViveBlockPlanRequest request,ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt,bool practiceScope)
        {return rawContext!=null&&rawContext.IsLegacy&&TryBeginRawRecordingWithPlan(actor,request,receipt,practiceScope);}
        internal bool TryBeginRawRecordingWithPlan(Actor actor,ViveBlockPlanRequest request,
            ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt,bool practiceScope)
        {
            var sharedClock=eventClock as MonotonicClock;if(sharedClock==null)return false;
            if(actor!=Actor.Owner||request==null||receipt==null||request.PracticeScope!=practiceScope||
                System.Threading.Volatile.Read(ref namedFrameAdmission)==null||namedRawRevocation.IsRevoked||
                !request.TryValidateCurrentForReceipt(this,sharedClock)||!receipt.TryConsume(request)||
                !request.TryValidateCurrentForReceipt(this,sharedClock))return false;
            bool started=false;
            RunNamedRecordingMutation(()=>{
                if(System.Threading.Volatile.Read(ref namedFrameAdmission)==null||namedRawRevocation.IsRevoked||
                    !request.PlanMatchesInsideMutation(this,sharedClock))return;
                if(practiceScope)OwnerBeginPracticeCore(actor);else OwnerBeginBlockCore(actor);
                started=true;
            });
            return started&&!namedRawRevocation.IsRevoked;
        }
        void OwnerBeginPracticeCore(Actor actor)
        {
            RequireOwner(actor);RequireReady();
            if(experiment.Phase==Phase.Preparation)
            {experiment.BeginFamiliarization(actor);if(!MayContinueOrdinaryMutation())return;}
            experiment.BeginPractice(actor);
        }
        public void OwnerLockSource(Actor actor,ControlSelection source,string ownerId)
        {if(observation==null){RunNamedRecordingMutation(()=>OwnerLockSourceCore(actor,source,ownerId));return;}Observed(()=>OwnerLockSourceCore(actor,source,ownerId),false);}
        void OwnerLockSourceCore(Actor actor,ControlSelection source,string ownerId)
        {
            RequireOwner(actor);RequireReady();
            if(source!=ControlSelection.Automatic)throw new InvalidOperationException("Only Automatic is available in this development task; live WoZ is not connected");
            experiment.LockBlock(actor,source,ownerId);
        }
        public void OwnerConfirmNamedNoAvatar(Actor actor,string ownerId)
        {RunNamedRecordingMutation(()=>{RequireOwner(actor);RequireReady();experiment.ConfirmNamedNoAvatar(actor,ownerId);});}
        public void OwnerConfirmNamedPlan(Actor actor,string ownerId)
        {RunNamedRecordingMutation(()=>{RequireOwner(actor);RequireReady();experiment.ConfirmNamedPlan(actor,ownerId);});}
        // Called through the raw pump's reserved ordinary mutation only.
        // The recorded times are controller-poll observations, not hardware
        // event times, study consent, or proof of durable storage.
        internal void RecordNamedSoftwareChoices(MonotonicClock clock,double taskPoll,double eyePoll)
        {if(rawContext==null||rawContext.IsLegacy)throw new InvalidOperationException("Named recording context required.");RecordRawSoftwareChoices(clock,taskPoll,eyePoll);}
        internal void RecordLegacySoftwareChoices(MonotonicClock clock,double taskPoll,double eyePoll)
        {
            var attached=System.Threading.Volatile.Read(ref namedFrameAdmission);
            if(rawContext==null||!rawContext.IsLegacy||attached==null||!attached.MatchesLegacyObservedChoices(clock,taskPoll,eyePoll))
                throw new InvalidOperationException("Exact legacy choice observations required.");
            RecordRawSoftwareChoices(clock,taskPoll,eyePoll);
        }
        void RecordRawSoftwareChoices(MonotonicClock clock,double taskPoll,double eyePoll)
        {
            RunNamedRecordingMutation(()=>{
                if(clock==null||!Object.ReferenceEquals(clock,eventClock)||
                    System.Threading.Volatile.Read(ref namedFrameAdmission)==null||namedRawRevocation.IsRevoked)
                    throw new InvalidOperationException("Software choice provenance cannot be recorded.");
                experiment.RecordRawSoftwareChoices(clock.ClockId,taskPoll,eyePoll);
            });
        }
        public void OwnerBeginBlock(Actor actor)
        {
            if(System.Threading.Volatile.Read(ref namedFrameAdmission)!=null)throw new InvalidOperationException("A matching block plan write receipt is required.");
            if(observation==null){RunNamedRecordingMutation(()=>OwnerBeginBlockCore(actor));return;}Observed(()=>OwnerBeginBlockCore(actor),false);
        }
        void OwnerBeginBlockCore(Actor actor)
        {RequireOwner(actor);RequireReady();bool noAvatar=experiment.Configuration.IsNamedDesign&&experiment.CurrentNamedSelection!=null&&experiment.CurrentNamedSelection.Option==ViveNamedOption.NoAvatar;
            experiment.BeginCountdown(actor,noAvatar?experiment.NamedOptionConfirmed:experiment.LockedControl==ControlSelection.Automatic);}
        public void MarkPresented(string trialId)
        {if(observation==null){RunNamedRecordingMutation(()=>MarkPresentedCore(trialId));return;}Observed(()=>MarkPresentedCore(trialId),false);}
        void MarkPresentedCore(string trialId)
        {RequireReady();experiment.MarkPresented(trialId);}
        // Internal named raw path only; the caller must have verified its
        // separate queue and source reservation before invoking it.
        internal void MarkPresentedWithSoftwareProxy(string trialId,double unitySeconds,int frame,long revision)
        {
            if(observation!=null)throw new InvalidOperationException("Software proxy is unavailable with legacy observation.");
            RunNamedRecordingMutation(()=>{RequireReady();experiment.MarkPresentedWithSoftwareProxy(trialId,unitySeconds,frame,revision);});
        }
        public void Tick()
        {if(observation==null){RunNamedRecordingMutation(TickCore);return;}Observed(TickCore,false);}
        void TickCore()
        {
            if(!inputReady)return;
            // A retained source may automatically start the next countdown in
            // the same frame as Rest confirmation. With a recording admission,
            // the block plan must be prepared and acknowledged first.
            if(System.Threading.Volatile.Read(ref namedFrameAdmission)!=null&&experiment.RetainedSourceContinuationPending)return;
            if(experiment.RetainedSourceContinuationPending)
            {bool noAvatar=experiment.Configuration.NamedPlan!=null&&experiment.CurrentNamedSelection.Option==ViveNamedOption.NoAvatar;
                experiment.TryBeginRetainedBlockCountdown(noAvatar?experiment.NamedOptionConfirmed:experiment.LockedControl==ControlSelection.Automatic);if(!MayContinueOrdinaryMutation())return;}
            experiment.Tick();
        }
        public void EndTechnical(Actor actor,string reason)
        {if(observation==null){RunNamedRecordingMutation(()=>EndTechnicalCore(actor,reason),actor==Actor.Owner?3:0,reason);return;}Observed(()=>EndTechnicalCore(actor,reason),true);}
        void EndTechnicalCore(Actor actor,string reason)
        {RequireOwner(actor);Release(trackpad.Reset());inputReady=false;ResetAuxiliary();experiment.EndTechnical(actor,reason);}
        // Safety actions remain reachable even when a device is disconnected or
        // a phase-boundary neutral wait is still pending.
        public void OwnerPause(Actor actor)
        {if(observation==null){RunNamedRecordingMutation(()=>OwnerPauseCore(actor),actor==Actor.Owner?2:0);return;}Observed(()=>OwnerPauseCore(actor),true);}
        void OwnerPauseCore(Actor actor)
        {RequireOwner(actor);Release(trackpad.Reset());ResetAuxiliary();PauseWithDiagnostic(Actor.Owner,"Owner or software safety request.");}
        // Semantic safety path, including recording authority failures.
        // The VIVE controller Menu is not routed here. Callable after observer failure.
        public void ParticipantPause()
        {if(observation==null){RunNamedRecordingMutation(ParticipantPauseCore,1);return;}Observed(ParticipantPauseCore,true);}
        void ParticipantPauseCore()
        {Release(trackpad.Reset());ResetAuxiliary();PauseWithDiagnostic(Actor.Participant,"Task or recording safety request.");}
        public void ParticipantWithdraw()
        {RevokePracticeConsent();if(observation==null){RunNamedRecordingMutation(ParticipantWithdrawCore,4);return;}Observed(ParticipantWithdrawCore,true);}
        void ParticipantWithdrawCore()
        {Release(trackpad.Reset());inputReady=false;ResetAuxiliary();experiment.Withdraw();}
    }
}
