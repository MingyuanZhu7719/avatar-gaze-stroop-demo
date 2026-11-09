using System;
using System.IO;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Internal injection seam: tests provide a memory stream and never create a
    // real journal file. Production uses the fixed physical implementation below.
    internal interface IViveJournalSinkHandle : IDisposable
    {
        Stream Stream {get;}
        void FlushToStorage();
    }
    internal interface IViveJournalSinkFactory
    {
        IViveJournalSinkHandle Open(string absolutePath,FileMode mode);
    }
    internal sealed class VivePhysicalJournalSinkFactory : IViveJournalSinkFactory
    {
        sealed class Handle : IViveJournalSinkHandle
        {
            readonly FileStream file;
            internal Handle(FileStream file){this.file=file;}
            public Stream Stream {get{return file;}}
            // Ask the OS to flush data to storage. This API's success is not a
            // guarantee against power loss, device caches or hardware failure.
            public void FlushToStorage(){file.Flush(true);}
            public void Dispose(){file.Dispose();}
        }
        public IViveJournalSinkHandle Open(string absolutePath,FileMode mode)
        {
            if(mode!=FileMode.CreateNew)throw new InvalidOperationException("Only non-overwriting creation is supported");
            string parent=Path.GetDirectoryName(absolutePath);
            if(String.IsNullOrEmpty(parent)||!Directory.Exists(parent))throw new DirectoryNotFoundException("An existing journal parent directory is required");
            // Reject linked parent directories before creation. This ordinary
            // path check is not a defense against an adversarial concurrent
            // replacement of directory entries between check and open.
            for(string cursor=parent;cursor!=null;)
            {
                if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked journal parent directories are not supported");
                var next=Directory.GetParent(cursor);cursor=next==null?null:next.FullName;
            }
            return new Handle(new FileStream(absolutePath,FileMode.CreateNew,FileAccess.Write,FileShare.Read));
        }
    }

    // Owns exactly one new stream and one participant recording bridge. It never
    // starts a device, opens an existing file, deletes a partial file, relabels an
    // interrupted session as complete, or admits an apparatus run. Recording is
    // explicitly enabled separately for eye and task data. This remains unbound
    // to the Unity task until the application supplies a verified lifecycle.
    public sealed class ViveDevelopmentJournalSink : IDisposable
    {
        readonly IViveJournalSinkHandle handle;
        readonly ViveParticipantRecordingBridge bridge;
        readonly int ownerThread;
        bool closeAttempted,handleDisposed,closeAcknowledged,faulted;
        string error;
        public string Path {get;private set;}
        public bool IsFaulted {get{return faulted||bridge.IsFaulted;}}
        public string Error {get{return error??bridge.Error;}}
        // A disposal exception leaves the handle's final state uncertain; never
        // report it closed or retry the uncertain disposal automatically.
        public bool IsClosed {get{return closeAttempted&&handleDisposed;}}
        public bool CloseAttempted {get{return closeAttempted;}}
        public bool JournalCloseAcknowledged {get{return closeAcknowledged;}}
        public long LastWrittenSequence {get{return bridge.LastWrittenSequence;}}
        public long LastFlushAcknowledgedSequence {get{return bridge.LastFlushAcknowledgedSequence;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool ApparatusMappingValidated {get{return false;}}
        public bool PowerLossDurabilityGuaranteed {get{return false;}}
        // This describes only one block/segment, not a complete session or
        // hardware durability. The bridge verifies authoritative event coverage.
        public ViveParticipantRecordingCoverage CoverageSnapshot()
        {
            RequireOwner();
            var coverage=bridge.CoverageSnapshot();
            // Journal close/flush and stream disposal are separate outcomes.
            // Preserve the bridge's close acknowledgment while also exposing a
            // sink-level disposal failure in the aggregate fault flag.
            return new ViveParticipantRecordingCoverage(coverage.BlockIndex,coverage.Segment,coverage.EyeSource,coverage.TaskOrigin,
                coverage.FirstTaskSequence,coverage.LastTaskSequence,coverage.ObservationCount,coverage.ExplicitGapCount,
                coverage.KnownSegmentEventsReconciledAtClose,coverage.CleanCloseAcknowledged,coverage.Faulted||faulted,coverage.RecoveryUnknownCoverageCount);
        }

        ViveDevelopmentJournalSink(string path,IViveJournalSinkHandle handle,ViveParticipantRecordingBridge bridge)
        {Path=path;this.handle=handle;this.bridge=bridge;ownerThread=Thread.CurrentThread.ManagedThreadId;}

        public static ViveDevelopmentJournalSink OpenNew(string absolutePath,ViveParticipantEyeSource source,IClock clock,
            ViveDevelopmentProvenance identity,ViveDevelopmentDataSource dataSource,int blockIndex,int segment,
            bool explicitEyeRecordingOptIn,bool explicitTaskRecordingOptIn,ViveDevelopmentParticipant authoritativeTask=null)
        {
            return OpenWithFactory(absolutePath,source,clock,identity,dataSource,blockIndex,segment,
                explicitEyeRecordingOptIn,explicitTaskRecordingOptIn,new VivePhysicalJournalSinkFactory(),authoritativeTask);
        }

        // Explicit identity-only path can open a new header before the first
        // frame. It does not start or poll the participant source.
        public static ViveDevelopmentJournalSink OpenNewV2WithIdentity(string absolutePath,ViveParticipantEyeSource source,IClock clock,
            ViveDevelopmentStreamIdentity identity,ViveDevelopmentDataSource dataSource,int blockIndex,int segment,
            bool explicitEyeRecordingOptIn,bool explicitTaskRecordingOptIn,ViveDevelopmentParticipant authoritativeTask=null)
        {
            return OpenV2WithIdentityFactory(absolutePath,source,clock,identity,dataSource,blockIndex,segment,
                explicitEyeRecordingOptIn,explicitTaskRecordingOptIn,new VivePhysicalJournalSinkFactory(),authoritativeTask);
        }
        internal static ViveDevelopmentJournalSink OpenWithFactory(string absolutePath,ViveParticipantEyeSource source,IClock clock,
            ViveDevelopmentProvenance identity,ViveDevelopmentDataSource dataSource,int blockIndex,int segment,
            bool explicitEyeRecordingOptIn,bool explicitTaskRecordingOptIn,IViveJournalSinkFactory factory,ViveDevelopmentParticipant authoritativeTask=null)
        {
            if(factory is VivePhysicalJournalSinkFactory)
                throw new InvalidOperationException("Legacy physical recording is unavailable until authorization and protected storage are verified");
            if(!explicitEyeRecordingOptIn||!explicitTaskRecordingOptIn)
                throw new InvalidOperationException("Explicit eye and task recording opt-ins are both required");
            if(identity!=null&&identity.IsVersionTwoIdentity)throw new InvalidOperationException("A v2 observation cannot initialize a v1 sink");
            return OpenCore(absolutePath,source,clock,ViveDevelopmentStreamIdentity.FromObservation(identity),dataSource,blockIndex,segment,
                explicitEyeRecordingOptIn,explicitTaskRecordingOptIn,factory,authoritativeTask,false);
        }
        internal static ViveDevelopmentJournalSink OpenV2WithIdentityFactory(string absolutePath,ViveParticipantEyeSource source,IClock clock,
            ViveDevelopmentStreamIdentity identity,ViveDevelopmentDataSource dataSource,int blockIndex,int segment,
            bool explicitEyeRecordingOptIn,bool explicitTaskRecordingOptIn,IViveJournalSinkFactory factory,ViveDevelopmentParticipant authoritativeTask=null)
        {return OpenCore(absolutePath,source,clock,identity,dataSource,blockIndex,segment,explicitEyeRecordingOptIn,explicitTaskRecordingOptIn,factory,authoritativeTask,true);}
        static ViveDevelopmentJournalSink OpenCore(string absolutePath,ViveParticipantEyeSource source,IClock clock,
            ViveDevelopmentStreamIdentity identity,ViveDevelopmentDataSource dataSource,int blockIndex,int segment,
            bool explicitEyeRecordingOptIn,bool explicitTaskRecordingOptIn,IViveJournalSinkFactory factory,ViveDevelopmentParticipant authoritativeTask,bool useVersionTwo)
        {
            // A caller-provided source label does not prove that observations
            // are synthetic. The legacy physical factory lacks participant
            // authorization and protected-directory guarantees, so refuse it
            // before inspecting a path or opening any file. Internal memory
            // factories remain available for isolated synthetic regression.
            if(factory is VivePhysicalJournalSinkFactory)
                throw new InvalidOperationException("Legacy physical recording is unavailable until authorization and protected storage are verified");
            if(!explicitEyeRecordingOptIn||!explicitTaskRecordingOptIn)
                throw new InvalidOperationException("Explicit eye and task recording opt-ins are both required");
            if(authoritativeTask!=null&&(authoritativeTask.HasNamedOption||authoritativeTask.RawContext!=null))
                throw new InvalidOperationException("Legacy journal sink cannot represent versioned raw or named-option provenance");
            if(source==null||clock==null||identity==null||factory==null)throw new ArgumentNullException("source/clock/identity/factory");
            if(source.ReadAttemptGenerationExhausted)throw new InvalidOperationException("Participant read attempt counter exhausted");
            if(identity.MeasurementRole!=ViveDevelopmentMeasurementRole.Participant||source.StreamId!=identity.StreamId||
                source.DeviceId!=identity.DeviceId||clock.ClockId!=identity.LocalObservationClockId||!source.UsesClock(clock)||
                source.SessionId!=identity.SessionId||source.ClockId!=identity.LocalObservationClockId||source.BlockIndex!=blockIndex||
                source.Segment!=segment||source.MappingVersion!=identity.MappingVersion||source.OperatorStatus!=identity.OperatorStatus)
                throw new ArgumentException("Explicit matching participant stream, device, role and clock required");
            if(!useVersionTwo&&identity.OperatorStatus!=ViveDevelopmentOperatorStatus.Present)
                throw new InvalidOperationException("Journal v1 cannot represent an absent operator");
            if(!Enum.IsDefined(typeof(ViveDevelopmentDataSource),dataSource))throw new ArgumentOutOfRangeException("dataSource");
            if(blockIndex<0||blockIndex>3||segment<0)throw new ArgumentOutOfRangeException("blockIndex");
            // Reject mismatched or absent real-eye task provenance before the
            // factory can create an empty or partial output file. A declared
            // real source/identity alone does not prove the physical HMD.
            if((dataSource==ViveDevelopmentDataSource.RealVive&&authoritativeTask==null)||
                (authoritativeTask!=null&&(authoritativeTask.SessionId!=identity.SessionId||
                    authoritativeTask.EventClockId!=identity.LocalObservationClockId||!authoritativeTask.UsesEventClock(clock))))
                throw new ArgumentException("Real-eye recording requires the actual development task and its same clock instance");
            if(authoritativeTask!=null&&(authoritativeTask.BlockIndex!=blockIndex||authoritativeTask.Segment!=segment||authoritativeTask.Phase==Phase.Ended))
                throw new InvalidOperationException("The authoritative task must already be in this open recording segment");
            double initialNow=clock.Now;
            if(Double.IsNaN(initialNow)||Double.IsInfinity(initialNow)||initialNow<0)throw new ArgumentException("Valid local observation clock required","clock");
            if(authoritativeTask!=null)ValidateSegmentBoundary(authoritativeTask,identity,blockIndex,segment,initialNow);
            string full=ValidateAbsolutePath(absolutePath);
            // Neither the source nor the physical runtime is polled here. If
            // open or initialization fails, never delete or replace evidence.
            IViveJournalSinkHandle handle=null;
            try
            {
                handle=factory.Open(full,FileMode.CreateNew);
                if(handle==null||handle.Stream==null)throw new IOException("Journal stream unavailable");
                var bridge=useVersionTwo?ViveParticipantRecordingBridge.CreateV2WithIdentity(source,clock,identity,dataSource,blockIndex,segment,
                    handle.Stream,handle.FlushToStorage,true,true,authoritativeTask):
                    ViveParticipantRecordingBridge.CreateLegacyWithIdentity(source,clock,identity,dataSource,blockIndex,segment,
                    handle.Stream,handle.FlushToStorage,true,true,authoritativeTask);
                return new ViveDevelopmentJournalSink(full,handle,bridge);
            }
            catch
            {
                if(handle!=null)try{handle.Dispose();}catch(Exception){}
                throw;
            }
        }
        static void ValidateSegmentBoundary(ViveDevelopmentParticipant task,ViveDevelopmentStreamIdentity identity,int block,int segment,double now)
        {
            long cursor=-1;
            while(true)
            {
                var events=task.ReadEventsAfter(cursor,4096);
                foreach(var item in events)
                {
                    if(item.BlockIndex==block&&item.Segment==segment)
                    {
                        if(item.SessionId!=identity.SessionId||item.ClockId!=identity.LocalObservationClockId||
                            Double.IsNaN(item.Time)||Double.IsInfinity(item.Time)||item.Time<0||item.Time>now)
                            throw new ArgumentException("A valid observed task-segment boundary is required");
                        return;
                    }
                    cursor=item.Sequence;
                }
                if(events.Count<4096)break;
            }
            throw new ArgumentException("A valid observed task-segment boundary is required");
        }
        internal static string ValidateAbsolutePath(string path)
        {
            if(String.IsNullOrWhiteSpace(path)||!PathIsFullyQualified(path))throw new ArgumentException("A fully qualified local journal path is required","path");
            string full=System.IO.Path.GetFullPath(path),name=System.IO.Path.GetFileName(full);
            var comparison=System.IO.Path.DirectorySeparatorChar=='\\'?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
            if(!String.Equals(path,full,comparison)||String.IsNullOrWhiteSpace(name)||name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars())>=0)
                throw new ArgumentException("An explicit canonical file path is required","path");
            return full;
        }
        static bool PathIsFullyQualified(string path)
        {
            if(!System.IO.Path.IsPathRooted(path))return false;
            if(System.IO.Path.DirectorySeparatorChar!='\\')return path[0]=='/';
            // Restrict to a local, drive-qualified path. UNC, device paths and
            // drive-relative paths require a separate explicit design/approval.
            return path.Length>=4&&Char.IsLetter(path[0])&&path[1]==':'&&(path[2]=='\\'||path[2]=='/')&&path[3]!='\\'&&path[3]!='/';
        }
        void RequireOwner()
        {if(Thread.CurrentThread.ManagedThreadId!=ownerThread)throw new InvalidOperationException("Journal sink requires its owner thread");}
        void SynchronizeFault(){if(bridge.IsFaulted){faulted=true;if(error==null)error=bridge.Error??"Recording failed";}}
        public ViveParticipantRecordingPoll Poll()
        {
            RequireOwner();if(closeAttempted)return IsFaulted?ViveParticipantRecordingPoll.Faulted:ViveParticipantRecordingPoll.Stopped;
            var result=bridge.Poll();SynchronizeFault();return result;
        }
        public bool AcknowledgeExternalSourceRead(long generation,ViveParticipantReadStatus status)
        {RequireOwner();if(closeAttempted||faulted)return false;bool ok=bridge.AcknowledgeExternalSourceRead(generation,status);SynchronizeFault();return ok;}
        public bool RecordInitialUnknownCoverage(double start,double end)
        {RequireOwner();if(closeAttempted||faulted)return false;bool ok=bridge.RecordInitialUnknownCoverage(start,end);SynchronizeFault();return ok;}
        public bool CommitExternalObservation(ViveParticipantEyeObservation observation)
        {RequireOwner();if(closeAttempted||faulted)return false;bool ok=bridge.CommitExternalObservation(observation);SynchronizeFault();return ok;}
        // Close the owning stream only; bridge/journal Dispose would append a
        // close marker and therefore must never be used for an abort.
        public bool AbortUnclean()
        {
            RequireOwner();if(closeAttempted)return false;closeAttempted=true;faulted=true;error="Recording aborted without close marker";
            try{handle.Dispose();handleDisposed=true;return true;}
            catch(Exception e){error="Abort disposal uncertain: "+e.GetType().Name;return false;}
        }
        public bool RecordGap(double start,double end,ViveDevelopmentGapReason reason)
        {RequireOwner();if(closeAttempted||faulted)return false;bool ok=bridge.RecordGap(start,end,reason);SynchronizeFault();return ok;}
        public bool RecordRecoveryUnknownCoverage(double start,double end,ViveDevelopmentGapReason reason)
        {RequireOwner();if(closeAttempted||faulted)return false;bool ok=bridge.RecordRecoveryUnknownCoverage(start,end,reason);SynchronizeFault();return ok;}
        public bool RecordTaskEvent(ViveDevelopmentTaskEventKind kind,double time,int block,int segment,string phase,string trialId,string detail)
        {RequireOwner();if(closeAttempted||faulted)return false;bool ok=bridge.RecordTaskEvent(kind,time,block,segment,phase,trialId,detail);SynchronizeFault();return ok;}
        // Forward the bridge's explicit authoritative event cursor and original
        // task event, preserving its sequence, origin and fields. Real VIVE eye
        // recordings must use this path rather than the low-level event helper.
        public bool BeginAuthoritativeEvents(long firstSequence,bool explicitStart,ViveDevelopmentTaskOrigin origin)
        {RequireOwner();if(closeAttempted||faulted)return false;bool ok=bridge.BeginAuthoritativeEvents(firstSequence,explicitStart,origin);SynchronizeFault();return ok;}
        public bool RecordAuthoritativeTaskEvent(JournalEvent taskEvent)
        {RequireOwner();if(closeAttempted||faulted)return false;bool ok=bridge.RecordAuthoritativeTaskEvent(taskEvent);SynchronizeFault();return ok;}
        public bool Flush()
        {RequireOwner();if(closeAttempted||faulted)return false;bool ok=bridge.Flush();SynchronizeFault();return ok;}
        public bool Close()
        {
            RequireOwner();if(closeAttempted)return closeAcknowledged&&handleDisposed&&!IsFaulted;
            // Never ask a faulted bridge to create a close marker; its partial
            // file stays available for explicit verified-prefix recovery.
            SynchronizeFault();
            if(!faulted)
            {
                try{if(bridge.Close())closeAcknowledged=true;else{faulted=true;error=bridge.Error??"Journal close was not acknowledged";}}
                catch(Exception e){faulted=true;error="Journal close failed: "+e.GetType().Name;}
            }
            try{handle.Dispose();handleDisposed=true;}catch(Exception e){faulted=true;if(error==null)error="Journal stream close failed: "+e.GetType().Name;}
            closeAttempted=true;return closeAcknowledged&&handleDisposed&&!IsFaulted;
        }
        public void Dispose(){Close();}
    }
}
