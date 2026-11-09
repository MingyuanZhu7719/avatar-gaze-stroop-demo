using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum ViveParticipantRecordingPoll { Recorded, RecordedUnavailable, NoNewEyeFrame, SourceUnavailable, ContextClosed, Stopped, Faulted }
    // The only task provenance currently supported is the existing simulated
    // development task. Real eye data never upgrades this label to apparatus.
    public enum ViveDevelopmentTaskOrigin { SimulationDevelopment=1 }
    public sealed class ViveAuthoritativeTaskPayload
    {
        public ViveDevelopmentTaskOrigin TaskOrigin {get;private set;}
        public long Sequence {get;private set;}
        public string Kind {get;private set;}
        public IReadOnlyDictionary<string,string> Fields {get;private set;}
        internal ViveAuthoritativeTaskPayload(ViveDevelopmentTaskOrigin taskOrigin,long sequence,string kind,IDictionary<string,string> fields)
        {TaskOrigin=taskOrigin;Sequence=sequence;Kind=kind;Fields=new ReadOnlyDictionary<string,string>(new Dictionary<string,string>(fields));}
    }
    // In-memory scope manifest, not an evidence bundle or durability receipt.
    // No subject identity or raw samples are included in this object.
    public sealed class ViveParticipantRecordingCoverage
    {
        public string Scope {get{return "ONE_BLOCK_SEGMENT_ONLY";}}
        public int BlockIndex {get;private set;}
        public int Segment {get;private set;}
        public ViveDevelopmentDataSource EyeSource {get;private set;}
        public ViveDevelopmentTaskOrigin? TaskOrigin {get;private set;}
        public long? FirstTaskSequence {get;private set;}
        public long? LastTaskSequence {get;private set;}
        public long ObservationCount {get;private set;}
        public long ExplicitGapCount {get;private set;}
        public long RecoveryUnknownCoverageCount {get;private set;}
        public bool KnownSegmentEventsReconciledAtClose {get;private set;}
        public bool CleanCloseAcknowledged {get;private set;}
        public bool Faulted {get;private set;}
        internal ViveParticipantRecordingCoverage(int blockIndex,int segment,ViveDevelopmentDataSource source,ViveDevelopmentTaskOrigin? taskOrigin,
            long? first,long? last,long observations,long gaps,bool reconciled,bool cleanClose,bool faulted,long recoveryUnknownCount=0)
        {BlockIndex=blockIndex;Segment=segment;EyeSource=source;TaskOrigin=taskOrigin;FirstTaskSequence=first;LastTaskSequence=last;
            ObservationCount=observations;ExplicitGapCount=gaps;RecoveryUnknownCoverageCount=recoveryUnknownCount;KnownSegmentEventsReconciledAtClose=reconciled;CleanCloseAcknowledged=cleanClose;Faulted=faulted;}
        public bool PracticeCovered {get{return false;}}
        public bool EventsOutsideSegmentCovered {get{return false;}}
        public bool OtherSegmentsCovered {get{return false;}}
        public bool ContinuousCoverageProven {get{return false;}}
        public bool FirstObservationReceived {get{return ObservationCount>0;}}
        public bool BeforeFirstObservationCovered {get{return false;}}
        public bool RecordsLeftRightRays {get{return false;}}
        public bool RecordsNativeOrigins {get{return false;}}
        public bool RecordsHeadPose {get{return false;}}
        public bool CompleteSessionProven {get{return false;}}
        public bool ResearchExportAllowed {get{return false;}}
        public bool HardwareDurabilityProven {get{return false;}}
    }

    // Development-only, one explicitly declared participant stream and one
    // block/segment epoch. The caller owns the stream, source lifecycle and clock.
    // This class does not open devices/files, start capture, infer gaps, classify
    // AOIs, admit apparatus, or claim hardware capture time or durable storage.
    // The host must stop/pause on faults and supply genuine measured gap intervals
    // and authoritative task events for this fixed block/segment; polling alone is not proof
    // of continuous valid data. Unknown/duplicate polls are not invented gaps.
    public sealed class ViveParticipantRecordingBridge
    {
        readonly ViveParticipantEyeSource source;
        readonly IClock clock;
        readonly ViveDevelopmentStreamIdentity identity;
        readonly ViveDevelopmentDataSource dataSource;
        readonly bool versionTwo;
        readonly ViveDevelopmentJournal journal;
        readonly ViveDevelopmentParticipant authoritativeTask;
        readonly int blockIndex,segment,ownerThread;
        long nextOrdinal;
        long expectedReadAttemptGeneration;
        ViveParticipantEyeObservation pendingExternalObservation;
        bool initialUnknownCoverageWritten,externalMode,externalGapPending;
        ViveDevelopmentGapReason externalGapReason;
        long nextAuthoritativeEventSequence;
        long firstAuthoritativeEventSequence;
        double lastNow,lastTimeline=-1,lastTaskTime=-1;
        double epochStartLocalSeconds=-1;
        ViveDevelopmentProvenance lastObservationProvenance;
        bool closed,authoritativeEventsStarted;
        public bool IsFaulted {get;private set;}
        public string Error {get;private set;}
        public long ObservationsWritten {get;private set;}
        public long GapsWritten {get;private set;}
        public long RecoveryUnknownWindowsWritten {get;private set;}
        public long TaskEventsWritten {get;private set;}
        public long LastWrittenSequence {get{return journal.LastWrittenSequence;}}
        public long LastFlushAcknowledgedSequence {get{return journal.LastFlushAcknowledgedSequence;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool ApparatusMappingValidated {get{return false;}}
        public bool ContinuousCoverageEstablished {get{return false;}}
        public ViveParticipantReadStatus LastSourceStatus {get{return source.LastReadStatus;}}
        public ViveParticipantReadStatus LastEyeUsability {get;private set;}
        public bool ContextOpen {get{return authoritativeTask==null||
            (authoritativeTask.Phase!=Phase.Ended&&authoritativeTask.BlockIndex==blockIndex&&authoritativeTask.Segment==segment);}}
        public ViveParticipantRecordingCoverage CoverageSnapshot()
        {
            return new ViveParticipantRecordingCoverage(blockIndex,segment,dataSource,
                authoritativeEventsStarted?(ViveDevelopmentTaskOrigin?)ViveDevelopmentTaskOrigin.SimulationDevelopment:null,
                authoritativeEventsStarted?(long?)firstAuthoritativeEventSequence:null,
                authoritativeEventsStarted&&TaskEventsWritten>0?(long?)(nextAuthoritativeEventSequence-1):null,
                ObservationsWritten,GapsWritten,closed&&!IsFaulted&&authoritativeTask!=null,closed&&!IsFaulted,IsFaulted,RecoveryUnknownWindowsWritten);
        }

        // Two independent affirmative choices are required. They do not replace
        // the vendor's own eye-data consent or research protocol approval.
        // Header provenance contains identities, not observation timestamps;
        // no provider poll is performed here.
        public ViveParticipantRecordingBridge(ViveParticipantEyeSource source,IClock clock,
            ViveDevelopmentProvenance identity,ViveDevelopmentDataSource dataSource,int blockIndex,int segment,
            Stream newEmptyStream,Action flushCallback,bool explicitEyeRecordingOptIn,bool explicitTaskRecordingOptIn,
            ViveDevelopmentParticipant authoritativeTask=null)
            :this(source,clock,LegacyIdentity(identity,explicitEyeRecordingOptIn,explicitTaskRecordingOptIn),dataSource,blockIndex,segment,
                newEmptyStream,flushCallback,explicitEyeRecordingOptIn,explicitTaskRecordingOptIn,authoritativeTask,false)
        {}

        // Named factory accepts immutable identity before any actual observation;
        // neither this method nor journal construction polls an eye/head source.
        public static ViveParticipantRecordingBridge CreateV2WithIdentity(ViveParticipantEyeSource source,IClock clock,
            ViveDevelopmentStreamIdentity identity,ViveDevelopmentDataSource dataSource,int blockIndex,int segment,
            Stream newEmptyStream,Action flushCallback,bool explicitEyeRecordingOptIn,bool explicitTaskRecordingOptIn,
            ViveDevelopmentParticipant authoritativeTask=null)
        {return new ViveParticipantRecordingBridge(source,clock,identity,dataSource,blockIndex,segment,newEmptyStream,flushCallback,
            explicitEyeRecordingOptIn,explicitTaskRecordingOptIn,authoritativeTask,true);}

        internal static ViveParticipantRecordingBridge CreateLegacyWithIdentity(ViveParticipantEyeSource source,IClock clock,
            ViveDevelopmentStreamIdentity identity,ViveDevelopmentDataSource dataSource,int blockIndex,int segment,
            Stream newEmptyStream,Action flushCallback,bool explicitEyeRecordingOptIn,bool explicitTaskRecordingOptIn,
            ViveDevelopmentParticipant authoritativeTask=null)
        {return new ViveParticipantRecordingBridge(source,clock,identity,dataSource,blockIndex,segment,newEmptyStream,flushCallback,
            explicitEyeRecordingOptIn,explicitTaskRecordingOptIn,authoritativeTask,false);}

        static ViveDevelopmentStreamIdentity LegacyIdentity(ViveDevelopmentProvenance observation,bool eyeOptIn,bool taskOptIn)
        {
            if(!eyeOptIn||!taskOptIn)throw new InvalidOperationException("Explicit eye and task recording opt-ins are both required");
            if(observation!=null&&observation.IsVersionTwoIdentity)throw new InvalidOperationException("A v2 observation cannot initialize a v1 bridge");
            return ViveDevelopmentStreamIdentity.FromObservation(observation);
        }

        ViveParticipantRecordingBridge(ViveParticipantEyeSource source,IClock clock,
            ViveDevelopmentStreamIdentity identity,ViveDevelopmentDataSource dataSource,int blockIndex,int segment,
            Stream newEmptyStream,Action flushCallback,bool explicitEyeRecordingOptIn,bool explicitTaskRecordingOptIn,
            ViveDevelopmentParticipant authoritativeTask,bool useVersionTwo)
        {
            if(!explicitEyeRecordingOptIn||!explicitTaskRecordingOptIn)
                throw new InvalidOperationException("Explicit eye and task recording opt-ins are both required");
            if(authoritativeTask!=null&&(authoritativeTask.HasNamedOption||authoritativeTask.RawContext!=null))
                throw new InvalidOperationException("Legacy recording bridge cannot represent versioned raw or named-option provenance");
            if(source==null||clock==null||identity==null||newEmptyStream==null||flushCallback==null)
                throw new ArgumentNullException("source/clock/identity/stream/flush");
            if(identity.MeasurementRole!=ViveDevelopmentMeasurementRole.Participant||
                source.StreamId!=identity.StreamId||source.DeviceId!=identity.DeviceId||clock.ClockId!=identity.LocalObservationClockId||
                !source.UsesClock(clock)||source.SessionId!=identity.SessionId||source.ClockId!=identity.LocalObservationClockId||
                source.BlockIndex!=blockIndex||source.Segment!=segment||source.MappingVersion!=identity.MappingVersion||source.OperatorStatus!=identity.OperatorStatus)
                throw new ArgumentException("Explicit matching participant stream, device, role and clock required");
            if(!Enum.IsDefined(typeof(ViveDevelopmentDataSource),dataSource))throw new ArgumentOutOfRangeException("dataSource");
            if(blockIndex<0||blockIndex>3||segment<0)throw new ArgumentOutOfRangeException("blockIndex");
            if((dataSource==ViveDevelopmentDataSource.RealVive&&authoritativeTask==null)||
                (authoritativeTask!=null&&(authoritativeTask.SessionId!=identity.SessionId||
                    authoritativeTask.EventClockId!=identity.LocalObservationClockId||!authoritativeTask.UsesEventClock(clock))))
                throw new ArgumentException("Real-eye recording requires the actual development task and its same clock instance");
            double now=clock.Now;
            if(Double.IsNaN(now)||Double.IsInfinity(now)||now<0)throw new ArgumentException("Valid local observation clock required","clock");
            this.source=source;this.clock=clock;this.identity=identity;this.dataSource=dataSource;versionTwo=useVersionTwo;
            this.blockIndex=blockIndex;this.segment=segment;this.authoritativeTask=authoritativeTask;
            ownerThread=System.Threading.Thread.CurrentThread.ManagedThreadId;lastNow=now;
            expectedReadAttemptGeneration=source.ReadAttemptGeneration;
            if(source.ReadAttemptGenerationExhausted)throw new InvalidOperationException("Participant read attempt counter exhausted");
            if(authoritativeTask!=null)
            {
                if(authoritativeTask.Phase==Phase.Ended||authoritativeTask.BlockIndex!=blockIndex||authoritativeTask.Segment!=segment)
                    throw new ArgumentException("The bound task must currently be in this segment");
                var boundary=FirstEpochEvent();
                if(boundary==null||boundary.SessionId!=identity.SessionId||boundary.ClockId!=identity.LocalObservationClockId||
                    Double.IsNaN(boundary.Time)||Double.IsInfinity(boundary.Time)||boundary.Time<0||boundary.Time>now)
                    throw new ArgumentException("A valid observed task-segment boundary is required");
                epochStartLocalSeconds=boundary.Time;
            }
            // Leave stream ownership with caller, including after fault. The
            // caller must preserve partial evidence and decide how to close it.
            journal=useVersionTwo?ViveDevelopmentJournal.CreateV2WithIdentity(newEmptyStream,flushCallback,identity,dataSource,true,true):
                ViveDevelopmentJournal.CreateLegacyWithIdentity(newEmptyStream,flushCallback,identity,dataSource,true,true);
            if(journal.IsFaulted)Fault("Journal initialization failed: "+journal.Error);
        }

        double Now()
        {
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=ownerThread)
                throw new InvalidOperationException("Recording bridge requires its owner thread");
            double now=clock.Now;
            if(clock.ClockId!=identity.LocalObservationClockId||Double.IsNaN(now)||Double.IsInfinity(now)||now<lastNow)
                throw new InvalidOperationException("Local observation clock failed; a new stream epoch is required");
            lastNow=now;return now;
        }
        bool Ready()
        {
            if(closed||IsFaulted)return false;
            try{
                Now();
                if(source.ReadAttemptGenerationExhausted||source.ReadAttemptGeneration!=expectedReadAttemptGeneration)
                    throw new InvalidOperationException("Unconsumed participant source read or exhausted counter");
                return true;
            }catch(Exception e){Fault("Recording clock, owner or source continuity failure: "+e.GetType().Name);return false;}
        }
        void Fault(string reason){if(!IsFaulted){IsFaulted=true;Error=reason;}}

        public ViveParticipantRecordingPoll Poll()
        {
            if(closed)return ViveParticipantRecordingPoll.Stopped;
            if(!Ready())return ViveParticipantRecordingPoll.Faulted;
            // Once the task leaves this block/segment, never poll and mislabel
            // new measurements as belonging to the old segment. Draining that
            // segment's remaining task events and a structural close remain safe.
            if(!ContextOpen)return ViveParticipantRecordingPoll.ContextClosed;
            if(pendingExternalObservation!=null){Fault("A source observation is awaiting commit");return ViveParticipantRecordingPoll.Faulted;}
            try
            {
                ViveParticipantEyeObservation observation;
                bool read=source.TryRead(out observation);
                if(source.ReadAttemptGenerationExhausted||expectedReadAttemptGeneration>=Int64.MaxValue-1||
                    source.ReadAttemptGeneration!=expectedReadAttemptGeneration+1)
                    throw new InvalidOperationException("Unexpected participant source read attempt");
                expectedReadAttemptGeneration++;
                if(!read)
                {
                    if(source.LastReadStatus==ViveParticipantReadStatus.Duplicate)return ViveParticipantRecordingPoll.NoNewEyeFrame;
                    if(source.LastReadStatus==ViveParticipantReadStatus.ProviderFailure||source.LastReadStatus==ViveParticipantReadStatus.ClockFailure||
                        source.LastReadStatus==ViveParticipantReadStatus.MappingFailure||source.LastReadStatus==ViveParticipantReadStatus.UnknownFailure)
                        throw new InvalidOperationException("Participant source failed");
                    // The host must evaluate this unavailable state immediately;
                    // no interval duration or specific gap is invented here.
                    return ViveParticipantRecordingPoll.SourceUnavailable;
                }
                return AppendObservation(observation);
            }
            catch(Exception e){Fault("Participant recording failed: "+e.GetType().Name);return ViveParticipantRecordingPoll.Faulted;}
        }

        ViveParticipantRecordingPoll AppendObservation(ViveParticipantEyeObservation observation)
        {
            if(observation==null||observation.Sample==null||observation.RawPoll==null||observation.RawPoll.Eye==null||
                observation.EyeDisposition!=ViveFrameDisposition.Accepted||observation.HardwareCaptureTimeVerified||observation.MappingValidatedForApparatus)
                throw new InvalidOperationException("Invalid participant observation");
            LastEyeUsability=observation.UsabilityStatus;
            var sample=observation.Sample;var poll=observation.RawPoll;var eye=poll.Eye;var combined=eye.Combined;
            if(combined==null)throw new InvalidOperationException("Combined eye ray absence is not representable by journal v1");
            if(sample.Source!=ParticipantEyeSource.RealDevice||sample.Role!=StreamRole.ParticipantMeasurement||
                sample.SessionId!=identity.SessionId||sample.StreamId!=identity.StreamId||sample.DeviceId!=identity.DeviceId||
                sample.ClockId!=identity.LocalObservationClockId||sample.BlockIndex!=blockIndex||sample.Segment!=segment||
                sample.CaptureLocalSeconds!=eye.ObservedLocalSeconds||eye.ObservedLocalSeconds<lastTimeline||
                (authoritativeTask!=null&&eye.ObservedLocalSeconds<epochStartLocalSeconds))
                throw new InvalidOperationException("Participant identity, segment, or observation order changed");
            if(sample.DerivedEyeValid&&sample.DerivedDirectionMappingVersion!=identity.MappingVersion)
                throw new InvalidOperationException("Derived mapping version changed");
            if(nextOrdinal==Int64.MaxValue)throw new InvalidOperationException("Recording ordinal exhausted");
            var provenance=versionTwo?ViveDevelopmentProvenance.FromObserved(identity,eye.Timestamp,observation.PollStartedLocalSeconds,observation.PollCompletedLocalSeconds,
                poll.Head==null?(double?)null:poll.Head.ObservedLocalSeconds,eye.ObservedLocalSeconds,true):
                new ViveDevelopmentProvenance(identity.SessionId,identity.ParticipantId,identity.OperatorId,ViveDevelopmentMeasurementRole.Participant,
                    identity.StreamId,identity.DeviceId,identity.LocalObservationClockId,identity.MappingVersion,eye.Timestamp,
                    observation.PollStartedLocalSeconds,observation.PollCompletedLocalSeconds,poll.Head==null?(double?)null:poll.Head.ObservedLocalSeconds,
                    eye.ObservedLocalSeconds,true);
            var record=ViveDevelopmentExportRecord.Observation(provenance,dataSource,nextOrdinal,blockIndex,segment,
                eye.FrameSequence,combined==null?0UL:combined.ValidityMask,sample.RawEyeDirection,
                observation.NativeDirectionReportedValid,sample.DerivedEyeValid?sample.DerivedEyeDirection:null,
                sample.DerivedEyeValid?sample.DerivedDirectionCoordinateSpace:null,sample.DerivedEyeValid);
            if(!journal.TryAppendObservation(record))throw new IOException("Journal append failed");
            nextOrdinal=checked(nextOrdinal+1);ObservationsWritten=checked(ObservationsWritten+1);
            lastTimeline=eye.ObservedLocalSeconds;lastObservationProvenance=provenance;
            // Preserve the actual invalid raw observation, but do not tell
            // the host that the source remains usable for presentation.
            return sample.DerivedEyeValid?ViveParticipantRecordingPoll.Recorded:ViveParticipantRecordingPoll.RecordedUnavailable;
        }

        // Coordinator acknowledgment does not append an observation.
        public bool AcknowledgeExternalSourceRead(long generation,ViveParticipantReadStatus status)
        {
            if(closed||IsFaulted)return false;
            try
            {
                Now();
                if(!versionTwo||pendingExternalObservation!=null||source.ReadAttemptGenerationExhausted||
                    expectedReadAttemptGeneration>=Int64.MaxValue-1||generation!=expectedReadAttemptGeneration+1||
                    source.ReadAttemptGeneration!=generation||source.LastReadStatus!=status||status==ViveParticipantReadStatus.NotRead||
                    status==ViveParticipantReadStatus.ReadAttemptGenerationExhausted)
                    throw new InvalidOperationException("Source read generation or disposition mismatch");
                if(status==ViveParticipantReadStatus.Accepted)
                {
                    if(source.LastObservation==null)throw new InvalidOperationException("Accepted source observation missing");
                    pendingExternalObservation=source.LastObservation;
                }
                else
                {
                    if(source.LastObservation!=null)throw new InvalidOperationException("Failed read must not retain an observation");
                    if(status==ViveParticipantReadStatus.ProviderFailure||status==ViveParticipantReadStatus.ClockFailure||
                        status==ViveParticipantReadStatus.MappingFailure||status==ViveParticipantReadStatus.UnknownFailure)
                        throw new InvalidOperationException("Participant source failed");
                    if(status!=ViveParticipantReadStatus.Duplicate)
                    {
                        if(ObservationsWritten==0)throw new InvalidOperationException("Initial unavailability has no representable observation");
                        var reason=ExternalGapReason(status);
                        if(externalGapPending&&reason!=externalGapReason)throw new InvalidOperationException("Unavailability reason changed");
                        externalGapPending=true;externalGapReason=reason;
                    }
                }
                externalMode=true;
                expectedReadAttemptGeneration=generation;return true;
            }
            catch(Exception e){Fault("External source acknowledgement failed: "+e.GetType().Name);return false;}
        }
        static ViveDevelopmentGapReason ExternalGapReason(ViveParticipantReadStatus status)
        {
            switch(status)
            {
                case ViveParticipantReadStatus.Missing:return ViveDevelopmentGapReason.Missing;
                case ViveParticipantReadStatus.Stale:return ViveDevelopmentGapReason.Stale;
                case ViveParticipantReadStatus.OutOfOrder:return ViveDevelopmentGapReason.OutOfOrder;
                case ViveParticipantReadStatus.DeviceUnavailable:return ViveDevelopmentGapReason.DeviceUnavailable;
                case ViveParticipantReadStatus.CalibrationUnavailable:return ViveDevelopmentGapReason.CalibrationUnavailable;
                case ViveParticipantReadStatus.RuntimeUnavailable:
                case ViveParticipantReadStatus.NativeFailure:return ViveDevelopmentGapReason.RuntimeFailure;
                default:return ViveDevelopmentGapReason.Other;
            }
        }
        public bool RecordInitialUnknownCoverage(double start,double end)
        {
            if(!Ready())return false;
            try
            {
                if(!versionTwo||authoritativeTask==null||!ContextOpen||initialUnknownCoverageWritten||ObservationsWritten!=0||
                    pendingExternalObservation==null||pendingExternalObservation.RawPoll==null||pendingExternalObservation.RawPoll.Eye==null||
                    start!=epochStartLocalSeconds||end!=pendingExternalObservation.RawPoll.Eye.ObservedLocalSeconds||end>lastNow||end<start)
                    throw new InvalidOperationException("Invalid initial unknown coverage");
                if(!journal.TryAppendInitialUnknownCoverage(blockIndex,segment,start,end))throw new IOException("Coverage append failed");
                initialUnknownCoverageWritten=true;return true;
            }
            catch(Exception e){Fault("Initial coverage recording failed: "+e.GetType().Name);return false;}
        }
        public bool CommitExternalObservation(ViveParticipantEyeObservation observation)
        {
            if(!Ready())return false;
            try
            {
                if(!versionTwo||!ContextOpen||pendingExternalObservation==null||!Object.ReferenceEquals(pendingExternalObservation,observation)||
                    !Object.ReferenceEquals(source.LastObservation,observation)||externalGapPending||(!initialUnknownCoverageWritten&&ObservationsWritten==0))
                    throw new InvalidOperationException("The pending observation and initial coverage are required");
                var result=AppendObservation(observation);
                pendingExternalObservation=null;
                return result==ViveParticipantRecordingPoll.Recorded||result==ViveParticipantRecordingPoll.RecordedUnavailable;
            }
            catch(Exception e){Fault("External observation commit failed: "+e.GetType().Name);return false;}
        }
        // A gap is never inferred from a duplicate/no-new-frame poll. Caller
        // supplies a confirmed positive interval on the same local clock. Until
        // an actual participant observation exists, this v1 schema cannot state
        // a gap without inventing observation provenance, so recording stops.
        public bool RecordGap(double startLocalSeconds,double endLocalSeconds,ViveDevelopmentGapReason reason)
        {
            if(!Ready())return false;
            if(!ContextOpen)return false;
            try
            {
                if(lastObservationProvenance==null||startLocalSeconds<lastTimeline||endLocalSeconds>lastNow||
                    (authoritativeTask!=null&&startLocalSeconds<epochStartLocalSeconds))
                    throw new InvalidOperationException("Known prior observation and ordered local gap required");
                if(externalMode)throw new InvalidOperationException("Externally coordinated recovery requires explicit unknown coverage");
                if(nextOrdinal==Int64.MaxValue)throw new InvalidOperationException("Recording ordinal exhausted");
                var gap=ViveDevelopmentExportRecord.Gap(lastObservationProvenance,dataSource,nextOrdinal,blockIndex,segment,startLocalSeconds,endLocalSeconds,reason);
                if(!journal.TryAppendObservation(gap))throw new IOException("Journal gap append failed");
                nextOrdinal=checked(nextOrdinal+1);GapsWritten=checked(GapsWritten+1);lastTimeline=endLocalSeconds;externalGapPending=false;return true;
            }
            catch(Exception e){Fault("Gap recording failed: "+e.GetType().Name);return false;}
        }

        // A detection and later recovery bound an unknown interval; they do not
        // establish the physical start/end of a device outage.
        public bool RecordRecoveryUnknownCoverage(double start,double end,ViveDevelopmentGapReason reason)
        {
            if(!Ready())return false;
            try
            {
                if(!versionTwo||!externalMode||!ContextOpen||!externalGapPending||pendingExternalObservation==null||
                    pendingExternalObservation.RawPoll==null||pendingExternalObservation.RawPoll.Eye==null||
                    pendingExternalObservation.Sample==null||!pendingExternalObservation.Sample.DerivedEyeValid||reason!=externalGapReason||
                    start!=lastTimeline||end!=pendingExternalObservation.RawPoll.Eye.ObservedLocalSeconds||end>lastNow||end<=start)
                    throw new InvalidOperationException("Recovery window requires pending source recovery and last observation");
                if(!journal.TryAppendRecoveryUnknownCoverage(blockIndex,segment,start,end,reason))throw new IOException("Recovery coverage append failed");
                RecoveryUnknownWindowsWritten=checked(RecoveryUnknownWindowsWritten+1);lastTimeline=end;externalGapPending=false;return true;
            }
            catch(Exception e){Fault("Recovery coverage recording failed: "+e.GetType().Name);return false;}
        }

        // Low-level event API; a host capturing an authoritative stream should
        // instead use BeginAuthoritativeEvents / RecordAuthoritativeTaskEvent.
        public bool RecordTaskEvent(ViveDevelopmentTaskEventKind kind,double eventLocalSeconds,int eventBlock,int eventSegment,
            string phase,string trialId,string detail)
        {
            if(!Ready())return false;
            try
            {
                if(dataSource==ViveDevelopmentDataSource.RealVive||authoritativeEventsStarted||kind==ViveDevelopmentTaskEventKind.AuthoritativeEvent)
                    throw new InvalidOperationException("Real-eye sessions require explicitly labelled authoritative task events");
                if(eventBlock!=blockIndex||eventSegment!=segment||eventLocalSeconds<lastTaskTime||eventLocalSeconds>lastNow)
                    throw new InvalidOperationException("Task event is outside this stream context or local clock");
                if(!journal.TryAppendTaskEvent(kind,eventLocalSeconds,eventBlock,eventSegment,phase,trialId,detail))
                    throw new IOException("Journal task append failed");
                TaskEventsWritten=checked(TaskEventsWritten+1);lastTaskTime=eventLocalSeconds;return true;
            }
            catch(Exception e){Fault("Task recording failed: "+e.GetType().Name);return false;}
        }

        // Per-segment cursor is explicit: callers must route earlier/later
        // events to their own session/segment evidence; it does not prove that
        // events outside this fixed segment have been captured.
        public bool BeginAuthoritativeEvents(long firstSequence,bool explicitStart,ViveDevelopmentTaskOrigin taskOrigin)
        {
            if(!Ready())return false;
            try
            {
                if(!explicitStart||taskOrigin!=ViveDevelopmentTaskOrigin.SimulationDevelopment||authoritativeEventsStarted||TaskEventsWritten!=0||firstSequence<0||firstSequence==Int64.MaxValue)
                    throw new InvalidOperationException("An explicit first authoritative event sequence is required");
                if(authoritativeTask!=null)
                {
                    if(authoritativeTask.BlockIndex!=blockIndex||authoritativeTask.Segment!=segment)
                        throw new InvalidOperationException("Task is not in the recording segment");
                    var first=FirstEpochEvent();
                    if(first==null||first.Sequence!=firstSequence)throw new InvalidOperationException("The first event must start at this segment's earliest authoritative event");
                }
                firstAuthoritativeEventSequence=nextAuthoritativeEventSequence=firstSequence;authoritativeEventsStarted=true;return true;
            }
            catch(Exception e){Fault("Task event setup failed: "+e.GetType().Name);return false;}
        }

        // Preserves every supplied event field without guessing phase or changing
        // original values. The opaque UTF-8/base64 detail has a version and
        // ordered key/value pairs; its size is bounded by journal v1's 1024 limit.
        // Journal append order may interleave eye/task samples: timestamps are
        // per-channel local observation times, not hardware capture timestamps.
        public bool RecordAuthoritativeTaskEvent(JournalEvent item)
        {
            if(item!=null&&(item.CarriesNamedProvenance||item.CarriesLegacyRawProvenance)){Fault("Legacy journal cannot represent versioned raw or named-option provenance");return false;}
            if(!Ready())return false;
            try
            {
                if(!authoritativeEventsStarted||item==null||item.SessionId!=identity.SessionId||item.ClockId!=identity.LocalObservationClockId||
                    item.BlockIndex!=blockIndex||item.Segment!=segment||item.Sequence!=nextAuthoritativeEventSequence||item.Sequence==Int64.MaxValue||
                    item.Time<lastTaskTime||item.Time>lastNow)
                    throw new InvalidOperationException("Authoritative task identity, sequence, context or time mismatch");
                if(authoritativeTask!=null&&!MatchesActualEvent(item))throw new InvalidOperationException("Task event is not the authoritative task snapshot");
                if(String.IsNullOrWhiteSpace(item.Kind)||item.Fields==null)throw new InvalidOperationException("Explicit task event kind and fields required");
                var keys=new List<string>();foreach(var field in item.Fields)keys.Add(field.Key);
                if(keys.Count>256)throw new InvalidOperationException("Too many authoritative event fields");
                keys.Sort(StringComparer.Ordinal);
                var detail=new StringBuilder("AE2;SIMULATION_DEVELOPMENT;");detail.Append(item.Sequence.ToString(CultureInfo.InvariantCulture));
                detail.Append(';').Append(Encode(item.Kind)).Append(';').Append(keys.Count.ToString(CultureInfo.InvariantCulture));
                foreach(string key in keys)
                {
                    if(String.IsNullOrEmpty(key))throw new InvalidOperationException("Empty task field key");
                    string value=item.Fields[key];detail.Append(';').Append(Encode(key)).Append(';').Append(value==null?"~":Encode(value));
                }
                if(detail.Length>1024)throw new InvalidOperationException("Authoritative event exceeds journal v1 detail limit");
                // The event does not store its exact phase except in some event
                // fields. Do not fill the journal's phase column from a later view.
                if(!journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,item.Time,item.BlockIndex,item.Segment,
                    "UNRESOLVED",item.TrialId,detail.ToString()))throw new IOException("Journal task append failed");
                nextAuthoritativeEventSequence=checked(nextAuthoritativeEventSequence+1);
                TaskEventsWritten=checked(TaskEventsWritten+1);lastTaskTime=item.Time;return true;
            }
            catch(Exception e){Fault("Authoritative task recording failed: "+e.GetType().Name);return false;}
        }
        static string Encode(string value)
        {return Convert.ToBase64String(new UTF8Encoding(false,true).GetBytes(value));}

        // Extractors must use this parser instead of treating arbitrary detail as
        // an authoritative event. Missing/altered provenance labels, malformed
        // values and duplicate fields fail closed. The journal hash chain must be
        // independently verified before interpreting any returned record.
        public static bool TryDecodeAuthoritativeTaskPayload(string detail,out ViveAuthoritativeTaskPayload payload)
        {
            payload=null;
            try
            {
                if(String.IsNullOrEmpty(detail)||detail.Length>1024)return false;
                string[] parts=detail.Split(';');long sequence;int count;
                if(parts.Length<5||parts[0]!="AE2"||parts[1]!="SIMULATION_DEVELOPMENT"||
                    !Int64.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out sequence)||sequence<0||
                    sequence.ToString(CultureInfo.InvariantCulture)!=parts[2]||
                    !Int32.TryParse(parts[4],NumberStyles.None,CultureInfo.InvariantCulture,out count)||count<0||count>256||
                    count.ToString(CultureInfo.InvariantCulture)!=parts[4]||parts.Length!=5+2*count)return false;
                string kind=Decode(parts[3]);if(String.IsNullOrWhiteSpace(kind))return false;
                var fields=new Dictionary<string,string>(StringComparer.Ordinal);
                string previous=null;
                for(int i=0;i<count;i++)
                {
                    string key=Decode(parts[5+2*i]);if(String.IsNullOrEmpty(key)||fields.ContainsKey(key)||
                        (previous!=null&&StringComparer.Ordinal.Compare(previous,key)>=0))return false;
                    string value=parts[6+2*i]=="~"?null:Decode(parts[6+2*i]);fields.Add(key,value);
                    previous=key;
                }
                payload=new ViveAuthoritativeTaskPayload(ViveDevelopmentTaskOrigin.SimulationDevelopment,sequence,kind,fields);return true;
            }
            catch(Exception e) when(e is FormatException||e is DecoderFallbackException||e is ArgumentException){return false;}
        }
        static string Decode(string value)
        {
            byte[] bytes=Convert.FromBase64String(value);
            if(Convert.ToBase64String(bytes)!=value)throw new FormatException("Noncanonical base64");
            return new UTF8Encoding(false,true).GetString(bytes);
        }
        JournalEvent FirstEpochEvent()
        {
            long cursor=-1;
            while(true)
            {
                var page=authoritativeTask.ReadEventsAfter(cursor,4096);
                if(page.Count==0)return null;
                foreach(var item in page)
                {
                    if(item.BlockIndex==blockIndex&&item.Segment==segment)return item;
                    cursor=item.Sequence;
                }
                if(page.Count<4096)return null;
            }
        }
        bool MatchesActualEvent(JournalEvent supplied)
        {
            var page=authoritativeTask.ReadEventsAfter(supplied.Sequence-1,1);
            if(page.Count!=1)return false;var actual=page[0];
            if(actual.Sequence!=supplied.Sequence||actual.Time!=supplied.Time||actual.ClockId!=supplied.ClockId||actual.SessionId!=supplied.SessionId||
                actual.BlockIndex!=supplied.BlockIndex||actual.Segment!=supplied.Segment||actual.TrialId!=supplied.TrialId||
                actual.Kind!=supplied.Kind||actual.Fields.Count!=supplied.Fields.Count)return false;
            foreach(var field in actual.Fields){string value;if(!supplied.Fields.TryGetValue(field.Key,out value)||value!=field.Value)return false;}
            return true;
        }
        bool EpochHasUndrainedEvents()
        {
            long cursor=nextAuthoritativeEventSequence-1;
            while(true)
            {
                var page=authoritativeTask.ReadEventsAfter(cursor,4096);if(page.Count==0)return false;
                foreach(var item in page){if(item.BlockIndex==blockIndex&&item.Segment==segment)return true;cursor=item.Sequence;}
                if(page.Count<4096)return false;
            }
        }

        // A flush is an acknowledgment from the caller's callback, not proof
        // of hardware durability. Close is structural, not study completion.
        public bool Flush()
        {
            if(!Ready())return false;
            try{if(journal.Flush())return true;throw new IOException("Journal flush failed");}
            catch(Exception e){Fault("Recording flush failed: "+e.GetType().Name);return false;}
        }
        public bool Close()
        {
            if(!Ready())return false;
            try
            {
                if(pendingExternalObservation!=null||externalGapPending||(externalMode&&ObservationsWritten==0))
                    throw new InvalidOperationException("Pending source observation or unresolved gap cannot be closed");
                if(authoritativeTask!=null&&(!authoritativeEventsStarted||EpochHasUndrainedEvents()||
                    (authoritativeTask.BlockIndex==blockIndex&&authoritativeTask.Segment==segment&&authoritativeTask.Phase!=Phase.Ended)))
                    throw new InvalidOperationException("The authoritative segment has not ended or its events have not been captured");
                if(!journal.Close())throw new IOException("Journal close failed");closed=true;return true;
            }
            catch(Exception e){Fault("Recording close failed: "+e.GetType().Name);return false;}
        }
    }
}
