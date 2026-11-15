using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum VivePracticeMultiStatus { Complete, Unclean, Truncated, Invalid, LimitExceeded }

    // Development-only structural records; all source/identity assertions are
    // caller-declared. This does not grant recording, validate consent, or prove
    // that a complete physical session is present.
    public sealed class VivePracticeMultiReadResult
    {
        public VivePracticeMultiStatus Status {get;internal set;}
        public IReadOnlyList<JournalEvent> Events {get;internal set;}
        public VivePracticeContextV3 Context {get;internal set;}
        public int VerifiedBytes {get;internal set;}
        public int CommittedBytes {get;internal set;}
        public bool Closed {get;internal set;}
        public bool ConsentAuthenticated {get{return false;}}
        public bool DurableStorageProven {get{return false;}}
        public bool FullPracticeCoverageProven {get{return false;}}
    }

    // New standalone framing; old VPRAC003 and raw VPSTG001 are not this
    // format. Hashes detect accidental/tampered structure but are not keyed and
    // do not authenticate origin or prevent deliberate hash recomputation.
    static class VivePracticeMultiCodec
    {
        internal const int FixedStage=57,HashBytes=32,CommitBytes=80,MaximumBytes=67108864,MaximumPayload=262144,MaximumTransactionEvents=256,MaximumEvents=100000;
        internal static readonly byte[] Magic=Encoding.ASCII.GetBytes("VPMUL004"),StageMagic=Encoding.ASCII.GetBytes("VPMST001"),CommitMagic=Encoding.ASCII.GetBytes("VPMCMT01");
        static readonly byte[] StageDomain=Encoding.ASCII.GetBytes("VIVE-PRACTICE-MULTI-STAGE-1\0"),CommitDomain=Encoding.ASCII.GetBytes("VIVE-PRACTICE-MULTI-COMMIT-1\0");
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        internal static void Limits(int bytes,int batches){if(bytes<256||bytes>MaximumBytes||batches<1||batches>100000)throw new ArgumentOutOfRangeException("Explicit bounded limits required.");}
        internal static bool Equal(byte[] x,byte[] y){if(x==null||y==null||x.Length!=y.Length)return false;int d=0;for(int i=0;i<x.Length;i++)d|=x[i]^y[i];return d==0;}
        static byte[] Hash(byte[] domain,byte[] data)
        {using(var h=SHA256.Create()){h.TransformBlock(domain,0,domain.Length,domain,0);h.TransformFinalBlock(data,0,data.Length);return h.Hash;}}
        internal static byte[] Slice(byte[] source,int offset,int length){var b=new byte[length];Buffer.BlockCopy(source,offset,b,0,length);return b;}
        internal static bool MagicAt(byte[] b,int pos,byte[] magic){if(pos<0||pos>b.Length-magic.Length)return false;for(int i=0;i<magic.Length;i++)if(b[pos+i]!=magic[i])return false;return true;}
        internal static byte[] Frame(long index,byte kind,byte[] previous,byte[] payload,out byte[] commitHash)
        {
            if(index<0||previous==null||previous.Length!=32||payload==null||payload.Length>MaximumPayload)throw new ArgumentException("Invalid bounded frame.");
            byte[] stage;
            using(var m=new MemoryStream())using(var w=new BinaryWriter(m,Utf8,true))
            {w.Write(StageMagic);w.Write(index);w.Write(kind);w.Write(payload.Length);w.Write(~payload.Length);w.Write(previous);w.Write(payload);w.Flush();stage=m.ToArray();}
            byte[] stageHash=Hash(StageDomain,stage),commitInput;
            using(var m=new MemoryStream())using(var w=new BinaryWriter(m,Utf8,true))
            {w.Write(CommitMagic);w.Write(index);w.Write(stageHash);w.Flush();commitInput=m.ToArray();}
            commitHash=Hash(CommitDomain,commitInput);var frame=new byte[stage.Length+32+commitInput.Length+32];
            Buffer.BlockCopy(stage,0,frame,0,stage.Length);Buffer.BlockCopy(stageHash,0,frame,stage.Length,32);
            Buffer.BlockCopy(commitInput,0,frame,stage.Length+32,commitInput.Length);Buffer.BlockCopy(commitHash,0,frame,stage.Length+32+commitInput.Length,32);return frame;
        }
        internal static byte[] StageHash(byte[] stage){return Hash(StageDomain,stage);}
        internal static byte[] CommitHash(byte[] input){return Hash(CommitDomain,input);}
        static void Room(BinaryWriter writer,int n,int maximum)
        {if(n<0||writer.BaseStream.Position>maximum-(long)n)throw new ArgumentException("Encoded task payload exceeds its bound.");}
        static void WriteString(BinaryWriter writer,string value,int maximum)
        {
            if(value==null){Room(writer,4,maximum);writer.Write(-1);return;}if(value.Length>4096)throw new ArgumentException("String bound exceeded.");
            int n=Utf8.GetByteCount(value);if(n>4096)throw new ArgumentException("String bound exceeded.");Room(writer,4+n,maximum);
            writer.Write(n);if(n!=0)writer.Write(Utf8.GetBytes(value));
        }
        static string ReadString(BinaryReader reader)
        {
            int n=reader.ReadInt32();if(n==-1)return null;if(n<0||n>4096||n>reader.BaseStream.Length-reader.BaseStream.Position)throw new InvalidDataException();
            var bytes=reader.ReadBytes(n);if(bytes.Length!=n)throw new InvalidDataException();return Utf8.GetString(bytes);
        }
        internal static byte[] EncodeEvent(JournalEvent e,int maximum)
        {
            if(e!=null&&(e.CarriesNamedProvenance||e.CarriesLegacyRawProvenance))throw new InvalidOperationException("Legacy practice transaction cannot encode versioned raw or named-option provenance.");
            if(e==null||e.Fields==null||e.Fields.Count>64||maximum<0||maximum>MaximumPayload)throw new ArgumentException("Bounded task event required.");
            using(var m=new MemoryStream())using(var w=new BinaryWriter(m,Utf8,true))
            {
                Room(w,16,maximum);w.Write(e.Sequence);w.Write(BitConverter.DoubleToInt64Bits(e.Time));WriteString(w,e.ClockId,maximum);WriteString(w,e.SessionId,maximum);
                Room(w,8,maximum);w.Write(e.BlockIndex);w.Write(e.Segment);WriteString(w,e.TrialId,maximum);WriteString(w,e.Kind,maximum);
                var keys=new List<string>(e.Fields.Keys);keys.Sort(StringComparer.Ordinal);Room(w,4,maximum);w.Write(keys.Count);
                foreach(var key in keys){if(String.IsNullOrWhiteSpace(key))throw new ArgumentException("Invalid task field.");WriteString(w,key,maximum);WriteString(w,e.Fields[key],maximum);}
                w.Flush();return m.ToArray();
            }
        }
        static void WriteContext(BinaryWriter w,VivePracticeContextV3 c,int maximum)
        {
            if(c==null||!c.ObservedPracticeStartLocalSeconds.HasValue)throw new ArgumentException("Declared context required.");
            WriteString(w,c.SessionId,maximum);WriteString(w,c.ParticipantId,maximum);WriteString(w,c.StreamId,maximum);
            WriteString(w,c.DeviceId,maximum);WriteString(w,c.ClockId,maximum);WriteString(w,c.MappingVersion,maximum);
            Room(w,4,maximum);w.Write(c.PracticeAttempt);WriteString(w,c.RecordingEpochId,maximum);
            Room(w,4,maximum);w.Write(c.RecoveryOrdinal);WriteString(w,c.PreviousEpochId,maximum);
            Room(w,12,maximum);w.Write((int)c.EpochReason);w.Write(BitConverter.DoubleToInt64Bits(c.ObservedPracticeStartLocalSeconds.Value));
        }
        static VivePracticeContextV3 ReadContext(BinaryReader r)
        {
            string session=ReadString(r),participant=ReadString(r),stream=ReadString(r),device=ReadString(r),clock=ReadString(r),mapping=ReadString(r);
            int attempt=r.ReadInt32();string epoch=ReadString(r);int ordinal=r.ReadInt32();string previous=ReadString(r);
            var reason=(VivePracticeEpochReasonV3)r.ReadInt32();double start=BitConverter.Int64BitsToDouble(r.ReadInt64());
            return new VivePracticeContextV3(session,participant,stream,device,clock,mapping,attempt,epoch,ordinal,previous,reason,start);
        }
        internal static bool SameContext(VivePracticeContextV3 a,VivePracticeContextV3 b)
        {
            return a!=null&&b!=null&&a.SessionId==b.SessionId&&a.ParticipantId==b.ParticipantId&&a.StreamId==b.StreamId&&
                a.DeviceId==b.DeviceId&&a.ClockId==b.ClockId&&a.MappingVersion==b.MappingVersion&&a.PracticeAttempt==b.PracticeAttempt&&
                a.RecordingEpochId==b.RecordingEpochId&&a.RecoveryOrdinal==b.RecoveryOrdinal&&a.PreviousEpochId==b.PreviousEpochId&&
                a.EpochReason==b.EpochReason&&a.ObservedPracticeStartLocalSeconds.HasValue&&b.ObservedPracticeStartLocalSeconds.HasValue&&
                BitConverter.DoubleToInt64Bits(a.ObservedPracticeStartLocalSeconds.Value)==BitConverter.DoubleToInt64Bits(b.ObservedPracticeStartLocalSeconds.Value);
        }
        internal static byte[] EncodeTransaction(VivePracticeContextV3 context,JournalEvent[] items,int maximum)
        {
            if(items==null||items.Length<1||items.Length>MaximumTransactionEvents||maximum<4||maximum>MaximumPayload)throw new ArgumentException("Bounded nonempty transaction required.");
            using(var m=new MemoryStream())using(var w=new BinaryWriter(m,Utf8,true))
            {
                WriteContext(w,context,maximum);Room(w,4,maximum);w.Write(items.Length);foreach(var item in items)
                {
                    int left=checked(maximum-(int)m.Length-4);if(left<0)throw new ArgumentException("Transaction bound exceeded.");
                    byte[] eventBytes=EncodeEvent(item,left);w.Write(eventBytes.Length);w.Write(eventBytes);
                }
                w.Flush();return m.ToArray();
            }
        }
        internal static JournalEvent[] DecodeTransaction(byte[] payload,out VivePracticeContextV3 context)
        {
            using(var m=new MemoryStream(payload,false))using(var r=new BinaryReader(m,Utf8,true))
            {
                context=ReadContext(r);int n=r.ReadInt32();if(n<1||n>MaximumTransactionEvents)throw new InvalidDataException();var items=new JournalEvent[n];
                for(int i=0;i<n;i++)
                {int len=r.ReadInt32();if(len<0||len>MaximumPayload||len>m.Length-m.Position)throw new InvalidDataException();items[i]=DecodeEvent(r.ReadBytes(len));}
                if(m.Position!=m.Length)throw new InvalidDataException();return items;
            }
        }
        internal static JournalEvent DecodeEvent(byte[] bytes)
        {
            using(var m=new MemoryStream(bytes,false))using(var r=new BinaryReader(m,Utf8,true))
            {
                long seq=r.ReadInt64();double time=BitConverter.Int64BitsToDouble(r.ReadInt64());string clock=ReadString(r),session=ReadString(r);
                int block=r.ReadInt32(),segment=r.ReadInt32();string trial=ReadString(r),kind=ReadString(r);int n=r.ReadInt32();
                if(n<0||n>64)throw new InvalidDataException();var fields=new Dictionary<string,string>(StringComparer.Ordinal);string prior=null;
                for(int i=0;i<n;i++){string key=ReadString(r),value=ReadString(r);if(String.IsNullOrWhiteSpace(key)||(prior!=null&&StringComparer.Ordinal.Compare(prior,key)>=0))throw new InvalidDataException();fields.Add(key,value);prior=key;}
                if(m.Position!=m.Length)throw new InvalidDataException();return new JournalEvent(seq,time,clock,session,block,segment,trial,kind,fields);
            }
        }
        internal static bool ValidTask(JournalEvent e,VivePracticeContextV3 context,int segment,long lastSequence,double lastTime,bool afterCompletion,bool completionPassed,bool afterEnd,bool boundaryReached)
        {
            if(e!=null&&(e.CarriesNamedProvenance||e.CarriesLegacyRawProvenance))return false;
            if(e==null||context==null||lastSequence==Int64.MaxValue||e.Sequence!=lastSequence+1||Double.IsNaN(e.Time)||Double.IsInfinity(e.Time)||e.Time<lastTime||
                e.SessionId!=context.SessionId||e.ClockId!=context.ClockId||e.Segment!=segment||String.IsNullOrWhiteSpace(e.Kind)||e.Kind.Length>256||boundaryReached||
                !KnownPracticeKind(e.Kind)||e.Fields==null||e.Fields.Count>64)return false;
            if(afterEnd)
            {
                string from,to;Phase previous;
                return e.Kind=="phase_changed"&&e.BlockIndex==-1&&e.Fields.TryGetValue("from",out from)&&
                    Enum.TryParse(from,false,out previous)&&Enum.IsDefined(typeof(Phase),previous)&&previous.ToString()==from&&previous!=Phase.Ended&&
                    e.Fields.TryGetValue("to",out to)&&to=="Ended"&&
                    (!(afterCompletion&&context.PracticeAttempt==2&&!completionPassed)||
                        (e.TrialId==null&&e.Fields.Count==2&&(from=="Practice"||from=="PracticeCorrection")));
            }
            if(afterCompletion)
            {
                if(context.PracticeAttempt==2&&!completionPassed)
                {
                    string reason,detail;
                    return e.Kind=="session_ended"&&e.BlockIndex==-1&&e.TrialId==null&&e.Fields.Count==2&&
                        e.Fields.TryGetValue("reason",out reason)&&reason=="PracticeFailed"&&
                        e.Fields.TryGetValue("detail",out detail)&&detail=="two_practice_attempts_below_6_of_8";
                }
                string from,to;
                return e.Kind=="phase_changed"&&e.Fields.TryGetValue("from",out from)&&(from=="Practice"||from=="PracticeCorrection")&&
                    e.Fields.TryGetValue("to",out to)&&(completionPassed?(e.BlockIndex==0&&to=="BlockPreparation"):(e.BlockIndex==-1&&to=="PracticeRetry"))&&
                    (context.PracticeAttempt!=2||(e.TrialId==null&&e.Fields.Count==2));
            }
            if(e.BlockIndex!=-1)return false;
            // Attempt two cannot enter a terminal phase in Active without its
            // required completion/end prefix. Preserve the legacy attempt-one
            // reader's acceptance semantics for previously encoded streams.
            if(context.PracticeAttempt==2&&e.Kind=="phase_changed")
            {
                string from,to;Phase parsedFrom,parsedTo;
                if(!e.Fields.TryGetValue("from",out from)||!Enum.TryParse(from,false,out parsedFrom)||
                    !Enum.IsDefined(typeof(Phase),parsedFrom)||parsedFrom.ToString()!=from||
                    !e.Fields.TryGetValue("to",out to)||!Enum.TryParse(to,false,out parsedTo)||
                    !Enum.IsDefined(typeof(Phase),parsedTo)||parsedTo.ToString()!=to||e.Fields.Count!=2||
                    !((parsedFrom==Phase.Practice&&(parsedTo==Phase.Practice||parsedTo==Phase.PracticeCorrection||parsedTo==Phase.Paused))||
                      (parsedFrom==Phase.PracticeCorrection&&(parsedTo==Phase.Practice||parsedTo==Phase.Paused))))return false;
            }
            if(context.PracticeAttempt==2&&(e.Kind=="explicit_resume"||e.Kind=="practice_resume_next_planned_trial"))return false;
            if(context.PracticeAttempt==2&&e.Kind=="session_ended")
            {
                string reason,detail;
                if(e.Fields.Count!=2||!e.Fields.TryGetValue("reason",out reason)||
                    (reason!="Withdrawn"&&reason!="TechnicalEnd")||!e.Fields.TryGetValue("detail",out detail)||
                    (reason=="Withdrawn"&&detail!="participant_withdrawal")||
                    (reason=="TechnicalEnd"&&String.IsNullOrWhiteSpace(detail)))return false;
            }
            if(e.Kind=="practice_completed")
            {bool ignored;if(!TryCompletion(e,context,out ignored))return false;}
            return true;
        }
        internal static bool TryCompletion(JournalEvent e,VivePracticeContextV3 context,out bool passed)
        {
            passed=false;string attempt,correct,planned;int count;
            if(e==null||e.Fields==null||context==null||(context.PracticeAttempt!=1&&context.PracticeAttempt!=2)||
                (context.PracticeAttempt==2&&(e.Fields.Count!=3||String.IsNullOrWhiteSpace(e.TrialId)))||!e.Fields.TryGetValue("attempt",out attempt)||
                attempt!=context.PracticeAttempt.ToString(CultureInfo.InvariantCulture)||!e.Fields.TryGetValue("planned",out planned)||planned!="8"||!e.Fields.TryGetValue("correct_first",out correct)||
                !Int32.TryParse(correct,NumberStyles.None,CultureInfo.InvariantCulture,out count)||count<0||count>8||count.ToString(CultureInfo.InvariantCulture)!=correct)return false;
            passed=count>=6;return true;
        }
        // Both writer and reader use this transition. Pending terminal states
        // may exist only while privately validating one complete transaction.
        internal static bool AdvanceTask(JournalEvent item,VivePracticeContextV3 context,int segment,
            ref long sequence,ref double time,ref bool completed,ref bool passed,ref bool ended,ref bool boundary)
        {
            if(!ValidTask(item,context,segment,sequence,time,completed,passed,ended,boundary))return false;
            sequence=item.Sequence;time=item.Time;
            if(ended||(completed&&!(context.PracticeAttempt==2&&!passed&&item.Kind=="session_ended")))boundary=true;
            if(item.Kind=="practice_completed")
            {completed=true;if(!TryCompletion(item,context,out passed))return false;}
            if(item.Kind=="session_ended")ended=true;
            if(context.PracticeAttempt==2&&!completed&&!ended&&item.Kind=="phase_changed")
            {string to;if(item.Fields.TryGetValue("to",out to)&&to=="Paused")boundary=true;}
            return true;
        }
        internal static bool CheckAttemptTwoPause(JournalEvent item,VivePracticeContextV3 context,ref bool pending)
        {
            if(context.PracticeAttempt!=2)return true;
            string to;bool pausePhase=item.Kind=="phase_changed"&&item.Fields.TryGetValue("to",out to)&&to=="Paused";
            if(pending){if(!pausePhase)return false;pending=false;return true;}
            if(pausePhase)return false;
            if(item.Kind=="explicit_pause")pending=true;
            return true;
        }
        // These fields are declarations in the stream, not proof of consent,
        // authentic linkage to a previous recording, or permission to record.
        internal static bool SupportedStart(VivePracticeContextV3 c,IReadOnlyList<JournalEvent> events)
        {
            if(c==null||events==null||events.Count!=3||c.RecoveryOrdinal!=0)return false;
            if(c.PracticeAttempt==1)return c.EpochReason==VivePracticeEpochReasonV3.Initial&&c.PreviousEpochId==null;
            string from;
            return c.PracticeAttempt==2&&c.EpochReason==VivePracticeEpochReasonV3.PracticeRetry&&
                c.PreviousEpochId!=null&&events[1]!=null&&events[1].Fields!=null&&
                events[1].Fields.TryGetValue("from",out from)&&from=="PracticeRetry";
        }
        // Explicit task-only allowlist from the current Experiment's practice
        // paths. Field content and event origin remain unauthenticated.
        static bool KnownPracticeKind(string kind)
        {
            switch(kind)
            {
                case "stimulus_presented":case "first_response":case "practice_correction_continue":case "trial_planned":
                case "phase_changed":case "practice_completed":case "trial_interrupted":case "explicit_pause":case "explicit_resume":
                case "practice_resume_next_planned_trial":case "headset_removed":case "technical_issue":case "session_ended":return true;
                default:return false;
            }
        }
    }

    // Bounded private-memory writer. AppendTask accepts caller-declared events;
    // it has no participant fence, storage capability or real eye integration.
    public sealed class VivePracticeMultiWriter
    {
        readonly MemoryStream memory=new MemoryStream();readonly int maximumBytes,maximumBatches,segment;readonly VivePracticeContextV3 context;
        readonly object sync=new object();readonly int owner;
        VivePracticeOwnedMemoryStart boundIssuer;Prepared outstanding;bool preparing,commitInProgress;
        byte[] previous=new byte[32];long index,lastSequence;double lastTime;int eventCount;bool closed,completedPractice,completionPassed,sessionEnded,boundaryReached;
        sealed class Proposal
        {
            internal byte[] Frame,CommitHash;internal long LastSequence;internal double LastTime;internal int EventCount;
            internal bool Completed,Passed,Ended,Boundary;
        }
        sealed class Baseline
        {
            internal readonly byte[] Previous;internal readonly long Index,LastSequence,Length;internal readonly double LastTime;
            internal readonly int EventCount;internal readonly bool Closed,Completed,Passed,Ended,Boundary;
            internal Baseline(VivePracticeMultiWriter w)
            {Previous=w.previous;Index=w.index;LastSequence=w.lastSequence;Length=w.memory.Length;LastTime=w.lastTime;EventCount=w.eventCount;
                Closed=w.closed;Completed=w.completedPractice;Passed=w.completionPassed;Ended=w.sessionEnded;Boundary=w.boundaryReached;}
        }
        // A bounded in-memory delta. It is not an I/O or consent permit; only
        // the exact issuer, lease and private outstanding reference may commit.
        internal sealed class Prepared
        {
            readonly VivePracticeMultiWriter writer;readonly VivePracticeOwnedMemoryStart issuer;
            readonly VivePracticeTaskEventCursor.CursorAppendLease lease;readonly byte[] delta,deltaDigest,commitHash;
            readonly long lastSequence;readonly double lastTime;readonly int eventCount;
            readonly bool completed,passed,ended,boundary;
            internal readonly long ExpectedPreviousLength;
            internal long NewTotalLength {get{return ExpectedPreviousLength+delta.Length;}}
            internal int DeltaLength {get{return delta.Length;}}
            internal Prepared(VivePracticeMultiWriter writer,VivePracticeOwnedMemoryStart issuer,
                VivePracticeTaskEventCursor.CursorAppendLease lease,byte[] delta,byte[] commitHash,long lastSequence,double lastTime,int eventCount,
                bool completed,bool passed,bool ended,bool boundary,long previousLength,byte[] digest)
            {this.writer=writer;this.issuer=issuer;this.lease=lease;this.delta=delta;this.commitHash=commitHash;this.lastSequence=lastSequence;
                this.lastTime=lastTime;this.eventCount=eventCount;this.completed=completed;this.passed=passed;this.ended=ended;this.boundary=boundary;
                ExpectedPreviousLength=previousLength;deltaDigest=digest;}
            internal byte[] CopyDelta()
            {lock(writer.sync){return writer.CanCopyPreparedUnderLock(this,issuer,lease)?(byte[])delta.Clone():null;}}
            internal byte[] CopyDeltaDigest()
            {lock(writer.sync){return writer.CanCopyPreparedUnderLock(this,issuer,lease)?(byte[])deltaDigest.Clone():null;}}
            internal bool MatchesDigest(byte[] candidate){return VivePracticeMultiCodec.Equal(deltaDigest,candidate);}
            internal bool Matches(VivePracticeMultiWriter w,VivePracticeOwnedMemoryStart i,VivePracticeTaskEventCursor.CursorAppendLease l)
            {return Object.ReferenceEquals(writer,w)&&Object.ReferenceEquals(issuer,i)&&Object.ReferenceEquals(lease,l);}
            internal bool ApplyUnderWriterLock(VivePracticeMultiWriter candidate)
            {
                if(!Object.ReferenceEquals(writer,candidate)||!Monitor.IsEntered(writer.sync)||!writer.commitInProgress||
                    !Object.ReferenceEquals(writer.outstanding,this))return false;
                writer.memory.Write(delta,0,delta.Length);writer.previous=commitHash;writer.index++;
                writer.lastSequence=lastSequence;writer.lastTime=lastTime;writer.eventCount=eventCount;
                writer.completedPractice=completed;writer.completionPassed=passed;writer.sessionEnded=ended;writer.boundaryReached=boundary;return true;
            }
        }
        public bool Faulted {get;private set;} public bool CanWriteResearchData {get{return false;}}
        public bool FullPracticeCoverageProven {get{return false;}}
        public VivePracticeMultiWriter(VivePracticeStartEnvelope start,int maximumBytes,int maximumBatches)
        {
            VivePracticeMultiCodec.Limits(maximumBytes,maximumBatches);if(start==null)throw new ArgumentNullException("start");
            byte[] payload=start.ToArray();var decoded=VivePracticeStartEnvelopeCodec.DecodePrefix(payload,0,payload.Length,VivePracticeStartEnvelopeCodec.MaximumEnvelopeBytes);
            if(decoded.Status!=VivePracticeStartEnvelopeStatus.Complete||decoded.ConsumedBytes!=payload.Length||decoded.Events.Count!=3)throw new ArgumentException("Complete start envelope required.");
            this.maximumBytes=maximumBytes;this.maximumBatches=maximumBatches;context=decoded.Context;segment=decoded.Events[0].Segment;owner=Thread.CurrentThread.ManagedThreadId;
            if(!VivePracticeMultiCodec.SupportedStart(context,decoded.Events))
                throw new ArgumentException("Unsupported development practice context.");
            lastSequence=decoded.Events[2].Sequence;lastTime=decoded.Events[2].Time;
            eventCount=3;
            memory.Write(VivePracticeMultiCodec.Magic,0,VivePracticeMultiCodec.Magic.Length);
            if(!AppendFrame(1,payload))throw new ArgumentException("Start exceeds budget.");
        }
        bool AppendFrame(byte kind,byte[] payload)
        {
            if(index>=maximumBatches||index==Int64.MaxValue){Faulted=true;return false;}
            byte[] digest,frame=VivePracticeMultiCodec.Frame(index,kind,previous,payload,out digest);
            if(memory.Length>maximumBytes-(long)frame.Length){Faulted=true;return false;}
            memory.Write(frame,0,frame.Length);previous=digest;index++;return true;
        }
        bool BaselineMatches(Baseline b)
        {return b!=null&&Object.ReferenceEquals(previous,b.Previous)&&index==b.Index&&lastSequence==b.LastSequence&&memory.Length==b.Length&&
            BitConverter.DoubleToInt64Bits(lastTime)==BitConverter.DoubleToInt64Bits(b.LastTime)&&eventCount==b.EventCount&&closed==b.Closed&&
            completedPractice==b.Completed&&completionPassed==b.Passed&&sessionEnded==b.Ended&&boundaryReached==b.Boundary;}
        Proposal BuildProposal(Baseline baseline,VivePracticeContextV3 declaredContext,JournalEvent[] items)
        {
            if(!VivePracticeMultiCodec.SameContext(context,declaredContext)||items==null||items.Length<1||items.Length>VivePracticeMultiCodec.MaximumTransactionEvents||
                baseline==null||baseline.EventCount>VivePracticeMultiCodec.MaximumEvents-items.Length||baseline.Index>=maximumBatches||baseline.Index==Int64.MaxValue)
                throw new InvalidOperationException("Invalid bounded transaction.");
            var copy=(JournalEvent[])items.Clone();long nextSequence=baseline.LastSequence;double nextTime=baseline.LastTime;
            bool nextCompleted=baseline.Completed,nextPassed=baseline.Passed,nextEnded=baseline.Ended,nextBoundary=baseline.Boundary,pendingPause=false;
            foreach(var item in copy)
            {
                if(!VivePracticeMultiCodec.CheckAttemptTwoPause(item,context,ref pendingPause)||
                    !VivePracticeMultiCodec.AdvanceTask(item,context,segment,ref nextSequence,ref nextTime,ref nextCompleted,ref nextPassed,ref nextEnded,ref nextBoundary))
                    throw new InvalidOperationException("Invalid task event order or context.");
            }
            if(pendingPause||(nextCompleted||nextEnded)&&!nextBoundary)throw new InvalidOperationException("Terminal transaction missing its boundary.");
            long available=maximumBytes-baseline.Length-(VivePracticeMultiCodec.FixedStage+VivePracticeMultiCodec.HashBytes+VivePracticeMultiCodec.CommitBytes);
            if(available<4)throw new InvalidOperationException("Transaction budget exhausted.");
            byte[] payload=VivePracticeMultiCodec.EncodeTransaction(declaredContext,copy,(int)Math.Min(available,VivePracticeMultiCodec.MaximumPayload));
            byte[] commitHash;byte[] frame=VivePracticeMultiCodec.Frame(baseline.Index,2,baseline.Previous,payload,out commitHash);
            if(baseline.Length>maximumBytes-(long)frame.Length)throw new InvalidOperationException("Transaction budget exhausted.");
            return new Proposal{Frame=frame,CommitHash=commitHash,LastSequence=nextSequence,LastTime=nextTime,EventCount=baseline.EventCount+copy.Length,
                Completed=nextCompleted,Passed=nextPassed,Ended=nextEnded,Boundary=nextBoundary};
        }
        void ApplyProposal(Proposal proposal)
        {
            memory.Write(proposal.Frame,0,proposal.Frame.Length);previous=proposal.CommitHash;index++;
            lastSequence=proposal.LastSequence;lastTime=proposal.LastTime;eventCount=proposal.EventCount;
            completedPractice=proposal.Completed;completionPassed=proposal.Passed;sessionEnded=proposal.Ended;boundaryReached=proposal.Boundary;
        }
        internal bool BindIssuer(VivePracticeOwnedMemoryStart issuer)
        {
            bool owned=false;try{owned=issuer!=null&&issuer.OwnsWriter(this);}catch(Exception error) when(!VivePracticeJournalV3Codec.Fatal(error)){}
            lock(sync)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||Faulted||closed||boundIssuer!=null||outstanding!=null||preparing||!owned)
                {Faulted=true;return false;}
                boundIssuer=issuer;return true;
            }
        }
        internal bool TryPrepareTaskTransaction(VivePracticeOwnedMemoryStart issuer,VivePracticeTaskEventCursor.CursorAppendLease lease,out Prepared prepared)
        {
            prepared=null;
            lock(sync)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||Faulted||closed||issuer==null||!Object.ReferenceEquals(boundIssuer,issuer)||outstanding!=null||preparing)
                {Faulted=true;return false;}
                preparing=true;
            }
            try
            {
                bool live=false;try{live=issuer.IsPendingLease(lease);}catch(Exception error) when(!VivePracticeJournalV3Codec.Fatal(error)){}
                Baseline baseline;
                lock(sync)
                {
                    if(Thread.CurrentThread.ManagedThreadId!=owner||Faulted||closed||!Object.ReferenceEquals(boundIssuer,issuer)||outstanding!=null||!live||
                        lease==null||lease.Transaction==null||lease.Anchor==null||!Object.ReferenceEquals(lease.Anchor.Issuer,issuer))
                    {Faulted=true;return false;}
                    baseline=new Baseline(this);
                }
                try
                {
                    var events=lease.Transaction.Events;if(events==null||events.Count<1||events.Count>VivePracticeMultiCodec.MaximumTransactionEvents)throw new InvalidOperationException();
                    var copy=new JournalEvent[events.Count];for(int i=0;i<copy.Length;i++)copy[i]=events[i];
                    var proposal=BuildProposal(baseline,lease.Anchor.Context,copy);
                    byte[] digest;using(var hash=SHA256.Create())digest=hash.ComputeHash(proposal.Frame);
                    lock(sync)
                    {
                        if(Faulted||closed||!preparing||!Object.ReferenceEquals(boundIssuer,issuer)||outstanding!=null||!BaselineMatches(baseline))
                        {Faulted=true;return false;}
                        long required=checked(memory.Length+proposal.Frame.Length);if(required>Int32.MaxValue)throw new InvalidOperationException();
                        // Reserve capacity before any target append. Commit writes
                        // only to this private MemoryStream; no external callback.
                        if(memory.Capacity<required)memory.Capacity=(int)required;
                        prepared=new Prepared(this,issuer,lease,proposal.Frame,proposal.CommitHash,proposal.LastSequence,proposal.LastTime,proposal.EventCount,
                            proposal.Completed,proposal.Passed,proposal.Ended,proposal.Boundary,memory.Length,digest);outstanding=prepared;return true;
                    }
                }
                catch(Exception error) when(!VivePracticeJournalV3Codec.Fatal(error)){lock(sync){Faulted=true;outstanding=null;}return false;}
            }
            finally{lock(sync)preparing=false;}
        }
        internal bool IsOutstandingPrepared(VivePracticeOwnedMemoryStart issuer,VivePracticeTaskEventCursor.CursorAppendLease lease,Prepared prepared)
        {
            lock(sync)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||Faulted||closed||preparing||issuer==null||!Object.ReferenceEquals(boundIssuer,issuer)||
                    prepared==null||!Object.ReferenceEquals(outstanding,prepared)||!prepared.Matches(this,issuer,lease)||memory.Length!=prepared.ExpectedPreviousLength)
                {Faulted=true;return false;}
                return true;
            }
        }
        bool CanCopyPreparedUnderLock(Prepared prepared,VivePracticeOwnedMemoryStart issuer,VivePracticeTaskEventCursor.CursorAppendLease lease)
        {
            if(!Monitor.IsEntered(sync)||Thread.CurrentThread.ManagedThreadId!=owner||Faulted||closed||preparing||
                !Object.ReferenceEquals(boundIssuer,issuer)||prepared==null||!Object.ReferenceEquals(outstanding,prepared)||!prepared.Matches(this,issuer,lease))
            {Faulted=true;outstanding=null;return false;}
            return true;
        }
        internal bool CanCommitPrepared(VivePracticeOwnedMemoryStart issuer,VivePracticeTaskEventCursor.CursorAppendLease lease,Prepared prepared,ViveExclusiveMemoryTarget.Receipt receipt)
        {
            bool authorized=false;try{authorized=issuer!=null&&issuer.MayCommitPrepared(this,lease,prepared,receipt);}catch(Exception error) when(!VivePracticeJournalV3Codec.Fatal(error)){}
            lock(sync)
            {
                return authorized&&Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&!closed&&!preparing&&Object.ReferenceEquals(boundIssuer,issuer)&&
                    prepared!=null&&Object.ReferenceEquals(outstanding,prepared)&&prepared.Matches(this,issuer,lease)&&memory.Length==prepared.ExpectedPreviousLength;
            }
        }
        internal bool CommitPrepared(VivePracticeOwnedMemoryStart issuer,VivePracticeTaskEventCursor.CursorAppendLease lease,Prepared prepared,ViveExclusiveMemoryTarget.Receipt receipt)
        {
            // Writer-only commit would separate its head from cursor progress.
            // The issuer must use CommitPreparedAndCursor under the full locks.
            lock(sync){Faulted=true;outstanding=null;return false;}
        }
        // The issuer already holds cursor -> issuer -> choice -> participant ->
        // target locks. The writer lock is acquired last and both private state
        // advances happen while it remains held. No arbitrary callback or I/O.
        internal bool CommitPreparedAndCursor(VivePracticeOwnedMemoryStart issuer,VivePracticeTaskEventCursor.CursorAppendLease lease,
            Prepared prepared,ViveExclusiveMemoryTarget.Receipt receipt,VivePracticeTaskEventCursor cursor)
        {
            bool authorized=CanCommitPrepared(issuer,lease,prepared,receipt);
            lock(sync)
            {
                if(!authorized||Thread.CurrentThread.ManagedThreadId!=owner||Faulted||closed||preparing||
                    !Object.ReferenceEquals(boundIssuer,issuer)||prepared==null||!Object.ReferenceEquals(outstanding,prepared)||
                    !prepared.Matches(this,issuer,lease)||memory.Length!=prepared.ExpectedPreviousLength||cursor==null||
                    !Object.ReferenceEquals(cursor,lease.Cursor)||!cursor.CanCommitUnderIssuerLocks(issuer,lease))
                {Faulted=true;outstanding=null;return false;}
                commitInProgress=true;
                try
                {
                    if(!prepared.ApplyUnderWriterLock(this)||!cursor.CommitUnderIssuerLocks(issuer,lease))
                    {Faulted=true;outstanding=null;return false;}
                    outstanding=null;return true;
                }
                catch(Exception error) when(!VivePracticeJournalV3Codec.Fatal(error)){Faulted=true;outstanding=null;return false;}
                finally{commitInProgress=false;}
            }
        }
        internal void AbandonPrepared(VivePracticeOwnedMemoryStart issuer,VivePracticeTaskEventCursor.CursorAppendLease lease,Prepared prepared)
        {lock(sync){Faulted=true;outstanding=null;}}
        public bool AppendTaskTransaction(VivePracticeContextV3 declaredContext,JournalEvent[] items)
        {
            Baseline baseline;
            lock(sync)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||boundIssuer!=null||outstanding!=null||preparing){Faulted=true;return false;}
                if(Faulted||closed)return false;preparing=true;baseline=new Baseline(this);
            }
            try
            {
                var proposal=BuildProposal(baseline,declaredContext,items);
                lock(sync)
                {if(Faulted||closed||!preparing||boundIssuer!=null||outstanding!=null||!BaselineMatches(baseline)){Faulted=true;return false;}
                    ApplyProposal(proposal);return true;}
            }
            catch(Exception error) when(!VivePracticeJournalV3Codec.Fatal(error)){lock(sync)Faulted=true;return false;}
            finally{lock(sync)preparing=false;}
        }
        public bool Close()
        {
            Baseline baseline;
            lock(sync)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||boundIssuer!=null||outstanding!=null||preparing){Faulted=true;return false;}
                if(Faulted||closed)return false;preparing=true;baseline=new Baseline(this);
            }
            try
            {
                if(baseline.Index>=maximumBatches||baseline.Index==Int64.MaxValue)throw new InvalidOperationException();
                byte[] digest,frame=VivePracticeMultiCodec.Frame(baseline.Index,3,baseline.Previous,new byte[0],out digest);
                lock(sync)
                {
                    if(Faulted||closed||!preparing||boundIssuer!=null||outstanding!=null||!BaselineMatches(baseline)||memory.Length>maximumBytes-(long)frame.Length)
                    {Faulted=true;return false;}
                    memory.Write(frame,0,frame.Length);previous=digest;index++;closed=true;return true;
                }
            }
            catch(Exception error) when(!VivePracticeJournalV3Codec.Fatal(error)){lock(sync)Faulted=true;return false;}
            finally{lock(sync)preparing=false;}
        }
        public byte[] Snapshot(){lock(sync)return memory.ToArray();}
    }

    public static class VivePracticeMultiReader
    {
        static readonly IReadOnlyList<JournalEvent> Empty=new ReadOnlyCollection<JournalEvent>(new List<JournalEvent>());
        static VivePracticeMultiReadResult Result(VivePracticeMultiStatus status,int verified,int committed,bool closed,
            VivePracticeContextV3 context,List<JournalEvent> events,bool expose)
        {return new VivePracticeMultiReadResult{Status=status,VerifiedBytes=verified,CommittedBytes=committed,Closed=expose&&closed,
            Context=expose?context:null,Events=expose?new ReadOnlyCollection<JournalEvent>(new List<JournalEvent>(events)):Empty};}
        static long LongAt(byte[] b,int p){return BitConverter.ToInt64(b,p);}static int IntAt(byte[] b,int p){return BitConverter.ToInt32(b,p);}
        public static VivePracticeMultiReadResult Read(byte[] bytes,bool explicitRecovery,int maximumBytes,int maximumBatches)
        {
            VivePracticeMultiCodec.Limits(maximumBytes,maximumBatches);if(bytes==null)throw new ArgumentNullException("bytes");
            var events=new List<JournalEvent>();VivePracticeContextV3 context=null;int verified=0,committed=0,p=0,count=0,segment=-1;
            long index=0,lastSequence=-1;double lastTime=-1;bool closed=false,completed=false,completionPassed=false,sessionEnded=false,boundaryReached=false;byte[] previous=new byte[32];
            if(bytes.Length>maximumBytes)return Result(VivePracticeMultiStatus.LimitExceeded,0,0,false,null,events,false);
            // Parse one bounded private snapshot: a caller may mutate its input
            // concurrently, and every verified digest must cover the bytes
            // actually decoded. This is an additional allocation up to maxBytes.
            bytes=(byte[])bytes.Clone();
            if(bytes.Length<VivePracticeMultiCodec.Magic.Length)return Result(VivePracticeMultiStatus.Truncated,0,0,false,null,events,false);
            if(!VivePracticeMultiCodec.MagicAt(bytes,0,VivePracticeMultiCodec.Magic))return Result(VivePracticeMultiStatus.Invalid,0,0,false,null,events,false);
            p=verified=VivePracticeMultiCodec.Magic.Length;
            try
            {
                while(p<bytes.Length)
                {
                    if(closed)return Result(VivePracticeMultiStatus.Invalid,verified,committed,true,context,events,explicitRecovery&&context!=null);
                    if(count>=maximumBatches)return Result(VivePracticeMultiStatus.LimitExceeded,verified,committed,false,null,events,false);
                    int start=p;
                    if(bytes.Length-p<VivePracticeMultiCodec.FixedStage)return Result(VivePracticeMultiStatus.Truncated,verified,committed,false,context,events,explicitRecovery&&context!=null);
                    if(!VivePracticeMultiCodec.MagicAt(bytes,p,VivePracticeMultiCodec.StageMagic))return Result(VivePracticeMultiStatus.Invalid,verified,committed,false,context,events,explicitRecovery&&context!=null);
                    p+=8;long number=LongAt(bytes,p);p+=8;byte kind=bytes[p++];int n=IntAt(bytes,p);p+=4;int inverse=IntAt(bytes,p);p+=4;
                    if(number!=index||n<0||n>VivePracticeMultiCodec.MaximumPayload||inverse!=~n||!VivePracticeMultiCodec.Equal(VivePracticeMultiCodec.Slice(bytes,p,32),previous))
                        return Result(VivePracticeMultiStatus.Invalid,verified,committed,false,context,events,explicitRecovery&&context!=null);
                    p+=32;if(bytes.Length-p<n+VivePracticeMultiCodec.HashBytes)return Result(VivePracticeMultiStatus.Truncated,verified,committed,false,context,events,explicitRecovery&&context!=null);
                    byte[] payload=VivePracticeMultiCodec.Slice(bytes,p,n);p+=n;byte[] stage=VivePracticeMultiCodec.Slice(bytes,start,p-start);
                    byte[] stageHash=VivePracticeMultiCodec.Slice(bytes,p,32);p+=32;
                    if(!VivePracticeMultiCodec.Equal(stageHash,VivePracticeMultiCodec.StageHash(stage)))return Result(VivePracticeMultiStatus.Invalid,verified,committed,false,context,events,explicitRecovery&&context!=null);
                    verified=p;
                    if(bytes.Length-p<VivePracticeMultiCodec.CommitBytes)return Result(VivePracticeMultiStatus.Truncated,verified,committed,false,context,events,explicitRecovery&&context!=null);
                    if(!VivePracticeMultiCodec.MagicAt(bytes,p,VivePracticeMultiCodec.CommitMagic)||LongAt(bytes,p+8)!=index||
                        !VivePracticeMultiCodec.Equal(VivePracticeMultiCodec.Slice(bytes,p+16,32),stageHash))return Result(VivePracticeMultiStatus.Invalid,verified,committed,false,context,events,explicitRecovery&&context!=null);
                    byte[] commitInput=VivePracticeMultiCodec.Slice(bytes,p,48),commitHash=VivePracticeMultiCodec.Slice(bytes,p+48,32);
                    if(!VivePracticeMultiCodec.Equal(commitHash,VivePracticeMultiCodec.CommitHash(commitInput)))return Result(VivePracticeMultiStatus.Invalid,verified,committed,false,context,events,explicitRecovery&&context!=null);
                    p+=VivePracticeMultiCodec.CommitBytes;
                    // Validate entire batch privately before advancing public state.
                    if(index==0)
                    {
                        if(kind!=1)throw new InvalidDataException();var startResult=VivePracticeStartEnvelopeCodec.DecodePrefix(payload,0,payload.Length,VivePracticeStartEnvelopeCodec.MaximumEnvelopeBytes);
                        if(startResult.Status!=VivePracticeStartEnvelopeStatus.Complete||startResult.ConsumedBytes!=payload.Length||startResult.Events.Count!=3)throw new InvalidDataException();
                        if(!VivePracticeMultiCodec.SupportedStart(startResult.Context,startResult.Events))throw new InvalidDataException();
                        context=startResult.Context;segment=startResult.Events[0].Segment;foreach(var e in startResult.Events)events.Add(e);
                        lastSequence=startResult.Events[2].Sequence;lastTime=startResult.Events[2].Time;
                    }
                    else if(kind==2)
                    {
                        VivePracticeContextV3 transactionContext;var items=VivePracticeMultiCodec.DecodeTransaction(payload,out transactionContext);
                        if(!VivePracticeMultiCodec.SameContext(context,transactionContext))throw new InvalidDataException();
                        if(events.Count>VivePracticeMultiCodec.MaximumEvents-items.Length)return Result(VivePracticeMultiStatus.LimitExceeded,verified,committed,false,null,events,false);
                        long nextSequence=lastSequence;double nextTime=lastTime;bool nextCompleted=completed,nextPassed=completionPassed,nextEnded=sessionEnded,nextBoundary=boundaryReached,pendingPause=false;
                        foreach(var item in items)
                        {
                            if(!VivePracticeMultiCodec.CheckAttemptTwoPause(item,context,ref pendingPause)||
                                !VivePracticeMultiCodec.AdvanceTask(item,context,segment,ref nextSequence,ref nextTime,ref nextCompleted,ref nextPassed,ref nextEnded,ref nextBoundary))throw new InvalidDataException();
                        }
                        if(pendingPause||(nextCompleted||nextEnded)&&!nextBoundary)throw new InvalidDataException();
                        events.AddRange(items);lastSequence=nextSequence;lastTime=nextTime;completed=nextCompleted;completionPassed=nextPassed;sessionEnded=nextEnded;boundaryReached=nextBoundary;
                    }
                    else if(kind==3){if(n!=0)throw new InvalidDataException();closed=true;}
                    else throw new InvalidDataException();
                    previous=commitHash;index++;count++;committed=verified=p;
                }
                if(context==null)return Result(VivePracticeMultiStatus.Truncated,verified,committed,false,null,events,false);
                return Result(closed?VivePracticeMultiStatus.Complete:VivePracticeMultiStatus.Unclean,verified,committed,closed,context,events,closed||explicitRecovery);
            }
            catch(Exception error) when(!VivePracticeJournalV3Codec.Fatal(error))
            {return Result(VivePracticeMultiStatus.Invalid,verified,committed,closed,context,events,explicitRecovery&&context!=null);}
        }
    }
}
