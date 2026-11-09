using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Dormant adapter: constructing it does not open storage, poll hardware or
    // make either recording choice. One owner thread owns the whole lifecycle.
    public sealed class ViveRecordingSinkPort : IViveRecordingSegmentPort
    {
        readonly string path;
        readonly ViveParticipantEyeSource source;
        readonly IClock clock;
        readonly ViveDevelopmentStreamIdentity identity;
        readonly ViveDevelopmentParticipant task;
        readonly ViveDevelopmentDataSource dataSource;
        readonly bool eyeOptIn,taskOptIn;
        readonly IViveJournalSinkFactory factory;
        readonly int ownerThread;
        ViveDevelopmentJournalSink sink;
        bool openAttempted,terminal;
        public string SessionId {get{return identity.SessionId;}}
        public string StreamId {get{return identity.StreamId;}}
        public string DeviceId {get{return identity.DeviceId;}}
        public string MappingVersion {get{return identity.MappingVersion;}}
        public string ClockId {get{return identity.LocalObservationClockId;}}
        public int BlockIndex {get{return source.BlockIndex;}}
        public int Segment {get{return source.Segment;}}
        public bool IsDormant {get{return !openAttempted&&!terminal;}}
        public bool UsesClock(IClock candidate){return Object.ReferenceEquals(clock,candidate);}
        public bool UsesSource(ViveParticipantEyeSource candidate){return Object.ReferenceEquals(source,candidate);}
        public ViveRecordingSinkPort(string absolutePath,ViveParticipantEyeSource source,IClock clock,ViveDevelopmentStreamIdentity identity,
            ViveDevelopmentParticipant task,ViveDevelopmentDataSource dataSource,bool explicitEyeOptIn,bool explicitTaskOptIn)
            :this(absolutePath,source,clock,identity,task,dataSource,explicitEyeOptIn,explicitTaskOptIn,new VivePhysicalJournalSinkFactory()){}
        internal ViveRecordingSinkPort(string absolutePath,ViveParticipantEyeSource source,IClock clock,ViveDevelopmentStreamIdentity identity,
            ViveDevelopmentParticipant task,ViveDevelopmentDataSource dataSource,bool explicitEyeOptIn,bool explicitTaskOptIn,IViveJournalSinkFactory factory)
        {
            if(!explicitEyeOptIn||!explicitTaskOptIn)throw new InvalidOperationException("Both recording choices are required");
            if(source==null||clock==null||identity==null||task==null||factory==null)throw new ArgumentNullException("source/clock/identity/task/factory");
            path=ViveDevelopmentJournalSink.ValidateAbsolutePath(absolutePath);this.source=source;this.clock=clock;this.identity=identity;
            this.task=task;this.dataSource=dataSource;eyeOptIn=explicitEyeOptIn;taskOptIn=explicitTaskOptIn;this.factory=factory;
            ownerThread=Thread.CurrentThread.ManagedThreadId;
        }
        void RequireOwner(){if(Thread.CurrentThread.ManagedThreadId!=ownerThread)throw new InvalidOperationException("Sink port requires its owner thread");}
        public bool OpenSegmentAndBeginTaskEvents(long firstSequence,ViveDevelopmentTaskOrigin origin)
        {
            RequireOwner();if(!IsDormant)return false;openAttempted=true;
            sink=ViveDevelopmentJournalSink.OpenV2WithIdentityFactory(path,source,clock,identity,dataSource,source.BlockIndex,source.Segment,
                eyeOptIn,taskOptIn,factory,task);
            return sink.BeginAuthoritativeEvents(firstSequence,true,origin);
        }
        public bool AcknowledgeSourceRead(long generation,ViveParticipantReadStatus status)
        {RequireOwner();return !terminal&&sink!=null&&sink.AcknowledgeExternalSourceRead(generation,status);}
        public bool RecordUnknownInitialCoverage(double start,double end)
        {RequireOwner();return !terminal&&sink!=null&&sink.RecordInitialUnknownCoverage(start,end);}
        public bool RecordGap(double start,double end,ViveDevelopmentGapReason reason)
        {RequireOwner();return !terminal&&sink!=null&&sink.RecordRecoveryUnknownCoverage(start,end,reason);}
        public bool RecordObservation(ViveParticipantEyeObservation observation)
        {RequireOwner();return !terminal&&sink!=null&&sink.CommitExternalObservation(observation);}
        public bool RecordAuthoritativeTaskEvent(JournalEvent item)
        {RequireOwner();return !terminal&&sink!=null&&sink.RecordAuthoritativeTaskEvent(item);}
        public bool CloseSegment()
        {RequireOwner();if(terminal||sink==null)return false;terminal=true;return sink.Close();}
        public bool AbortUnclean(string reason)
        {
            RequireOwner();if(String.IsNullOrWhiteSpace(reason))throw new ArgumentException("Abort reason required","reason");
            if(terminal)return false;terminal=true;
            // A failed/uncertain open may have created a partial file; we cannot
            // claim disposal or retry when no handle was returned to the port.
            if(sink==null)return !openAttempted;
            return sink.AbortUnclean();
        }
    }
}
