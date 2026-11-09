using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Read-only, UI-neutral evidence summary for explicitly supplied streams.
    // Hashes validate framing, not authenticity. No data here proves absent
    // files, whole-session completion, durable storage or scientific validity.
    public sealed class ViveSessionObservedBlock
    {
        public int BlockIndex {get;internal set;}
        public bool ConfigurationMarkerSeen {get;internal set;}
        public bool TaskEndMarkerSeen {get;internal set;}
        public bool QuestionnaireEndMarkerSeen {get;internal set;}
        public int DistinctKnownRatingItemsSeen {get;internal set;}
        public int DistinctSegmentContextsSeen {get;internal set;}
        public bool DuplicateRatingItemSeen {get;internal set;}
    }
    public sealed class ViveSessionObservedSummary
    {
        public int JournalsAccepted {get;internal set;}
        public int CleanCloseJournals {get;internal set;}
        public int RecoveredPrefixJournals {get;internal set;}
        public int JournalsWithoutContext {get;internal set;}
        public long InitialUnknownWindowsSeen {get;internal set;}
        public long RecoveryUnknownWindowsSeen {get;internal set;}
        public long ExplicitGapsSeen {get;internal set;}
        public long UnclassifiedTaskEventsSeen {get;internal set;}
        public bool PracticeStartMarkerSeen {get;internal set;}
        public bool PracticeEndMarkerSeen {get;internal set;}
        public bool SessionEndMarkerSeen {get;internal set;}
        public string SessionEndReasonCategory {get;internal set;}
        // RealVive describes only the eye source. Current AE2 task markers are
        // explicitly simulation-development, never evidence of a formal study.
        public ViveDevelopmentDataSource? DeclaredEyeDataSource {get;internal set;}
        public ViveDevelopmentTaskOrigin? DecodedAuthoritativeEventOrigin {get;internal set;}
        public bool UnclassifiedTaskEventOriginUnknown {get{return UnclassifiedTaskEventsSeen>0;}}
        // Sanitized status of the most recent attempted addition; a prior
        // successful summary is never presented as the failed input's result.
        public string LastAddIssue {get;internal set;}
        public IReadOnlyList<ViveSessionObservedBlock> Blocks {get;internal set;}
        public string Scope {get{return "Self-declared markers from selected sources only; even a Completed marker or clean close does not prove completion. AE2 origin applies only to decoded markers; unclassified event origins, unobserved practice, missing files/segments and overall coverage are unknown.";}}
        public bool CompleteSessionProven {get{return false;}}
        public bool ContinuousCoverageProven {get{return false;}}
        public bool DurableStorageProven {get{return false;}}
        public bool IdentityAuthenticated {get{return false;}}
        public bool ResearchValidityProven {get{return false;}}
        public bool DiskExportAvailable {get{return false;}}
    }

    // Caller owns every stream and must choose it explicitly. This type opens no
    // path, performs no write, logs no content, and publishes only marker counts;
    // raw gaze, questionnaire responses, identifiers and paths are never exposed.
    public sealed class ViveSessionEvidenceAnalyzer
    {
        sealed class BlockState
        {internal bool Configuration,End,QuestionnaireEnd,DuplicateRating;internal int RatingMask;internal readonly HashSet<int> Segments=new HashSet<int>();}
        sealed class EventStub
        {internal long Sequence;internal string Kind,RatingItem,EndReason;internal int Block;}
        sealed class Pending
        {
            internal string IdentityHash,StreamHash;internal bool Header,HasContext,HasTaskSequence;internal int Block,Segment,Records;
            internal ViveDevelopmentDataSource DataSource;internal long LastTaskSequence;
            internal long InitialUnknown,RecoveryUnknown,Gaps,Unclassified;
            internal readonly List<EventStub> Events=new List<EventStub>();
            internal readonly HashSet<long> EventSequences=new HashSet<long>();
        }
        // Never closes or rewinds the caller's stream; the extra read at the
        // limit can only detect overflow, not be used as verified input.
        sealed class BoundedReadStream:Stream
        {
            readonly Stream source;readonly long limit;long read;
            internal BoundedReadStream(Stream source,long limit){this.source=source;this.limit=limit;}
            public override bool CanRead {get{return true;}} public override bool CanSeek {get{return false;}} public override bool CanWrite {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}} public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] buffer,int offset,int count)
            {
                if(count==0)return 0;
                try
                {
                    if(read>=limit){var one=new byte[1];if(source.Read(one,0,1)!=0)throw new IOException("Input limit exceeded.");return 0;}
                    int allowed=(int)Math.Min((long)count,limit-read);int got=source.Read(buffer,offset,allowed);read=checked(read+got);return got;
                }
                catch(Exception e) when(!Fatal(e)){throw new IOException("Read-only input failed.");}
            }
            public override void Flush(){throw new NotSupportedException();} public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long value){throw new NotSupportedException();} public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
        }
        readonly int maxJournals,maxRecords;readonly long maxBytes;
        readonly HashSet<string> streams=new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<long> taskSequences=new HashSet<long>();readonly BlockState[] blocks={new BlockState(),new BlockState(),new BlockState(),new BlockState()};
        string identityHash,endReason;ViveDevelopmentDataSource? declaredSource;int journals,clean,recovered,noContext,totalRecords;long initialUnknown,recoveryUnknown,gaps,unclassified;
        bool practiceStart,practiceEnd,sessionEnd,busy;readonly int ownerThread;readonly object stateGate=new object();int threadFault;
        public string LastIssue {get;private set;}
        public bool Faulted {get{return Interlocked.CompareExchange(ref threadFault,0,0)!=0;}}
        public ViveSessionEvidenceAnalyzer(int maximumJournals,int maximumRecords,long maximumBytesPerJournal)
        {
            if(maximumJournals<1||maximumJournals>64||maximumRecords<1||maximumRecords>100000||maximumBytesPerJournal<64||maximumBytesPerJournal>67108864)
                throw new ArgumentOutOfRangeException("Explicit bounded analyzer limits required.");
            maxJournals=maximumJournals;maxRecords=maximumRecords;maxBytes=maximumBytesPerJournal;ownerThread=Thread.CurrentThread.ManagedThreadId;
        }
        void Owner(){if(Thread.CurrentThread.ManagedThreadId!=ownerThread){lock(stateGate){Interlocked.Exchange(ref threadFault,1);}throw new InvalidOperationException("Analyzer owner thread required.");}}
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        static string Value(IReadOnlyDictionary<string,string> fields,string key)
        {string value;if(!fields.TryGetValue(key,out value))throw new InvalidDataException("Required field missing.");return value;}
        static string Hash(params string[] values)
        {
            var text=new StringBuilder();foreach(var value in values){if(value==null)text.Append("-1:");else text.Append(value.Length).Append(':').Append(value);}
            using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }
        static int ParseInt(string value)
        {int result;if(!Int32.TryParse(value,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out result))throw new InvalidDataException("Invalid context.");return result;}
        static void Context(Pending pending,ViveDevelopmentJournalRecord record)
        {
            int block=ParseInt(Value(record.Fields,"block_index")),segment=ParseInt(Value(record.Fields,"segment"));
            if(!pending.HasContext){pending.HasContext=true;pending.Block=block;pending.Segment=segment;}
            else if(pending.Block!=block||pending.Segment!=segment)throw new InvalidDataException("Mixed segment context.");
        }
        void Capture(Pending pending,ViveDevelopmentJournalRecord record)
        {
            pending.Records++;if(pending.Records>maxRecords-totalRecords)throw new InvalidDataException("Record limit exceeded.");
            if(record.Kind=="header")
            {
                if(pending.Header||Value(record.Fields,"schema")!=ViveDevelopmentJournal.IdentitySchemaVersion||Value(record.Fields,"role")!="Participant")
                    throw new InvalidDataException("Expected one participant v2 header.");
                pending.Header=true;
                ViveDevelopmentDataSource dataSource;
                if(!Enum.TryParse(Value(record.Fields,"data_source"),false,out dataSource)||!Enum.IsDefined(typeof(ViveDevelopmentDataSource),dataSource))
                    throw new InvalidDataException("Unknown data source.");
                pending.DataSource=dataSource;
                pending.IdentityHash=Hash(Value(record.Fields,"data_source"),Value(record.Fields,"session_id"),Value(record.Fields,"participant_id"),
                    Value(record.Fields,"operator_status"),Value(record.Fields,"operator_id"),Value(record.Fields,"role"),
                    Value(record.Fields,"device_id"),Value(record.Fields,"local_clock_id"),Value(record.Fields,"mapping_version"));
                pending.StreamHash=Hash(Value(record.Fields,"stream_id"));return;
            }
            if(!pending.Header)throw new InvalidDataException("Header required.");
            if(record.Kind=="close")return;
            Context(pending,record);
            if(record.Kind=="initial_coverage_unknown"){pending.InitialUnknown++;return;}
            if(record.Kind=="recovery_coverage_unknown"){pending.RecoveryUnknown++;return;}
            if(record.Kind=="gap"){pending.Gaps++;return;}
            if(record.Kind!="task_event")return;
            if(Value(record.Fields,"event_kind")!="AuthoritativeEvent"){pending.Unclassified++;return;}
            ViveAuthoritativeTaskPayload payload;
            if(!ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(Value(record.Fields,"detail"),out payload))
                throw new InvalidDataException("Invalid authoritative task marker.");
            if((payload.Kind=="practice_started"||payload.Kind=="practice_completed")&&pending.Block!=-1)
                throw new InvalidDataException("Practice marker has inconsistent block context.");
            if((payload.Kind=="block_configuration_locked"||payload.Kind=="block_task_ended"||payload.Kind=="rating"||payload.Kind=="questionnaire_completed")&&
                (pending.Block<0||pending.Block>3))throw new InvalidDataException("Block marker has inconsistent context.");
            if(pending.HasTaskSequence&&(pending.LastTaskSequence==Int64.MaxValue||payload.Sequence!=pending.LastTaskSequence+1))
                throw new InvalidDataException("Missing or reordered task sequence.");
            pending.HasTaskSequence=true;pending.LastTaskSequence=payload.Sequence;
            if(!pending.EventSequences.Add(payload.Sequence))throw new InvalidDataException("Duplicate event sequence.");
            var stub=new EventStub{Sequence=payload.Sequence,Kind=payload.Kind,Block=pending.Block};string value;
            if(payload.Kind=="rating"&&payload.Fields.TryGetValue("item",out value))stub.RatingItem=value;
            if(payload.Kind=="session_ended"&&payload.Fields.TryGetValue("reason",out value))stub.EndReason=value;
            pending.Events.Add(stub);
        }
        // Separate recovery consent is needed for any verified incomplete prefix.
        // A read/I/O failure is never treated as successful prefix recovery.
        public bool AddJournal(Stream input,bool explicitReadOnlyAction,bool explicitIncompleteRecovery)
        {
            Owner();if(Faulted){LastIssue="analyzer_faulted";return false;}
            if(busy)throw new InvalidOperationException("Analysis already in progress.");
            LastIssue=null;
            if(!explicitReadOnlyAction){LastIssue="read_only_confirmation_required";return false;}
            if(journals>=maxJournals||totalRecords>=maxRecords){LastIssue="analysis_limit";return false;}
            busy=true;
            try
            {
                if(input==null||!input.CanRead||input.CanWrite){LastIssue="read_only_stream_required";return false;}
                var pending=new Pending();var result=ViveDevelopmentJournalReader.Read(new BoundedReadStream(input,maxBytes),r=>Capture(pending,r));
                if(!pending.Header||result.Status==ViveDevelopmentJournalReadStatus.ReadFailure)
                {LastIssue="input_unavailable_or_limit";return false;}
                if(result.Status!=ViveDevelopmentJournalReadStatus.CleanClose&&!explicitIncompleteRecovery)
                {LastIssue="incomplete_prefix_requires_separate_confirmation";return false;}
                lock(stateGate)
                {
                    if(Faulted){LastIssue="analyzer_faulted";return false;}
                    if(identityHash!=null&&identityHash!=pending.IdentityHash){LastIssue="different_declared_identity";return false;}
                    if(streams.Contains(pending.StreamHash)){LastIssue="duplicate_declared_stream";return false;}
                    foreach(var sequence in pending.EventSequences)if(taskSequences.Contains(sequence)){LastIssue="duplicate_task_sequence";return false;}
                    // Validate potentially conflicting terminal claims before mutation.
                    bool seenEnd=sessionEnd;foreach(var e in pending.Events)if(e.Kind=="session_ended")
                    {if(seenEnd){LastIssue="multiple_terminal_markers";return false;}seenEnd=true;}
                    identityHash=pending.IdentityHash;declaredSource=pending.DataSource;streams.Add(pending.StreamHash);
                    foreach(var sequence in pending.EventSequences)taskSequences.Add(sequence);
                    totalRecords+=pending.Records;journals++;if(result.Status==ViveDevelopmentJournalReadStatus.CleanClose)clean++;else recovered++;
                    if(!pending.HasContext)noContext++;else if(pending.Block>=0&&pending.Block<4)blocks[pending.Block].Segments.Add(pending.Segment);
                    initialUnknown+=pending.InitialUnknown;recoveryUnknown+=pending.RecoveryUnknown;gaps+=pending.Gaps;unclassified+=pending.Unclassified;
                    foreach(var item in pending.Events)Apply(item);
                    return true;
                }
            }
            catch(Exception e) when(!Fatal(e)){LastIssue="input_validation_or_read_failed";return false;}
            finally{busy=false;}
        }
        void Apply(EventStub item)
        {
            if(item.Kind=="practice_started")practiceStart=true;
            else if(item.Kind=="practice_completed")practiceEnd=true;
            else if(item.Kind=="session_ended")
            {
                sessionEnd=true;endReason=item.EndReason=="Completed"||item.EndReason=="Withdrawn"||item.EndReason=="TechnicalEnd"||item.EndReason=="PracticeFailed"?item.EndReason:"OtherOrUnknown";
            }
            if(item.Block<0||item.Block>3)return;var block=blocks[item.Block];
            if(item.Kind=="block_configuration_locked")block.Configuration=true;
            else if(item.Kind=="block_task_ended")block.End=true;
            else if(item.Kind=="questionnaire_completed")block.QuestionnaireEnd=true;
            else if(item.Kind=="rating")
                for(int i=0;i<Questionnaire.Count;i++)if(item.RatingItem==Questionnaire.Id(i))
                {int bit=1<<i;if((block.RatingMask&bit)!=0)block.DuplicateRating=true;block.RatingMask|=bit;break;}
        }
        public ViveSessionObservedSummary Snapshot()
        {
            Owner();lock(stateGate)
            {
            if(Faulted)throw new InvalidOperationException("Analyzer is faulted.");
            if(busy)throw new InvalidOperationException("Analysis is already in progress.");
            var list=new List<ViveSessionObservedBlock>();
            for(int i=0;i<blocks.Length;i++)
            {
                var b=blocks[i];int count=0;for(int bit=0;bit<Questionnaire.Count;bit++)if((b.RatingMask&(1<<bit))!=0)count++;
                list.Add(new ViveSessionObservedBlock{BlockIndex=i,ConfigurationMarkerSeen=b.Configuration,TaskEndMarkerSeen=b.End,
                    QuestionnaireEndMarkerSeen=b.QuestionnaireEnd,DistinctKnownRatingItemsSeen=count,DistinctSegmentContextsSeen=b.Segments.Count,DuplicateRatingItemSeen=b.DuplicateRating});
            }
            return new ViveSessionObservedSummary{JournalsAccepted=journals,CleanCloseJournals=clean,RecoveredPrefixJournals=recovered,
                JournalsWithoutContext=noContext,InitialUnknownWindowsSeen=initialUnknown,RecoveryUnknownWindowsSeen=recoveryUnknown,ExplicitGapsSeen=gaps,
                UnclassifiedTaskEventsSeen=unclassified,PracticeStartMarkerSeen=practiceStart,PracticeEndMarkerSeen=practiceEnd,
                SessionEndMarkerSeen=sessionEnd,SessionEndReasonCategory=endReason,DeclaredEyeDataSource=declaredSource,LastAddIssue=LastIssue,
                DecodedAuthoritativeEventOrigin=taskSequences.Count==0?(ViveDevelopmentTaskOrigin?)null:ViveDevelopmentTaskOrigin.SimulationDevelopment,
                Blocks=new ReadOnlyCollection<ViveSessionObservedBlock>(list)};
            }
        }
    }
}
