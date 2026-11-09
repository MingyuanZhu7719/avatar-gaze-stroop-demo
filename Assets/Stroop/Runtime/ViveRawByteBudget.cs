using System;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Pure per-capture accounting only. A budget is not authorization, a disk
    // space reservation, proof of free space, or a full-session guarantee.
    // The caller writes frames incrementally; no allocation or I/O occurs here.
    internal sealed class ViveRawByteBudget
    {
        internal const long MaximumSupportedBytes=8589934592L; // Scan limit: 8 GiB
        internal static readonly int TerminalFrameBytes=ViveRawSessionJournalCodec.TerminalPayloadV3(1,-1,0,0,0,0,0).Length+40;
        readonly long maximum;
        internal long MaximumBytes {get{return maximum;}}
        ViveRawByteBudget(long value){maximum=value;}
        // An explicit programmatic selection; not participant permission.
        // No default is provided and creating the object does not touch disk.
        internal static ViveRawByteBudget SelectExplicit(long maximumBytes)
        {
            if(maximumBytes<=TerminalFrameBytes||maximumBytes>MaximumSupportedBytes)
                throw new ArgumentOutOfRangeException("maximumBytes");
            return new ViveRawByteBudget(maximumBytes);
        }
        // The terminal reserve is subtracted before admission of data, so an
        // otherwise clean capture can still fit its marker. Exact byte counts
        // refer to already-framed data (including first manifest+magic).
        internal bool FitsData(long bytesAccepted,int frameBytes)
        {
            if(bytesAccepted<0||frameBytes<=0)return false;
            long dataLimit=maximum-TerminalFrameBytes;
            return bytesAccepted<=dataLimit&&(long)frameBytes<=dataLimit-bytesAccepted;
        }
        // This only checks size. A separately verified normal-seal authority
        // must precede any terminal write; withdrawal/fault never qualifies.
        // A returned Write/close is not a durability proof.
        internal bool FitsTerminal(long bytesAccepted,int frameBytes)
        {
            return bytesAccepted>=0&&bytesAccepted<=maximum&&frameBytes==TerminalFrameBytes&&
                (long)frameBytes<=maximum-bytesAccepted;
        }

        // Single-producer cumulative budget: reservations are never refunded,
        // including on a failed enqueue or an uncertain in-flight write. It
        // counts queued AND already-consumed frames; it does not reserve space
        // on disk or say that a write completed. The sink must use this exact
        // ledger and independently check each encoded frame against its bound.
        internal sealed class QueueLedger
        {
            const int EyeFixedUpper=352,StatusUpper=128,MaxTextBytes=32768;
            static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
            readonly ViveRawByteBudget budget;readonly int owner;
            readonly ViveNamedSessionFrameAdmission.Manifest manifest;
            long reserved,nextOrdinal;int headerBytes;bool initialized;
            internal QueueLedger(ViveRawByteBudget budget,ViveNamedSessionFrameAdmission.Manifest manifest)
            {if(budget==null||manifest==null)throw new ArgumentNullException();this.budget=budget;this.manifest=manifest;owner=Thread.CurrentThread.ManagedThreadId;}
            internal bool Uses(ViveRawByteBudget other){return Object.ReferenceEquals(budget,other);}
            // Owner-only bound check while admission exclusively holds its
            // producer bit. The producer cannot charge another ordinal until
            // this reservation ends; the writer never reduces or mutates the
            // cumulative amount. A later exact TryCharge is still required.
            // No disk space or write success is implied.
            internal bool CanReserveUpper(long ordinal,int upperBytes)
            {
                return Thread.CurrentThread.ManagedThreadId==owner&&initialized&&ordinal>0&&ordinal!=Int64.MaxValue&&
                    ordinal==nextOrdinal&&upperBytes>0&&budget.FitsData(reserved,upperBytes);
            }
            internal bool TryFrameBound(long ordinal,object record,out int bytes)
            {
                bytes=0;if(ordinal<0||record==null)return false;
                try
                {
                    var task=record as JournalEvent;
                    if(task!=null)return ViveRawTaskFrameMeasurement.TryMeasure(checked(ordinal+1),task,manifest,1048616,out bytes);
                    var eye=record as ViveNamedSessionFrameAdmission.Frame;
                    if(eye!=null)
                    {
                        if(eye.TrialAtIngest!=null&&eye.TrialAtIngest.Length>MaxTextBytes)return false;
                        int text=eye.TrialAtIngest==null?0:Utf8.GetByteCount(eye.TrialAtIngest);
                        if(text>MaxTextBytes)return false;bytes=checked(EyeFixedUpper+text);return true;
                    }
                    if(record is ViveProEyeWorker.ObservedEyeStatus){bytes=StatusUpper;return true;}
                    return false;
                }
                catch(Exception e) when(e is EncoderFallbackException||e is OverflowException||e is ArgumentException)
                {bytes=0;return false;}
            }
            internal bool TryCharge(long ordinal,object record)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||ordinal<0||ordinal!=nextOrdinal||ordinal==Int64.MaxValue)return false;
                int bound;if(!TryFrameBound(ordinal,record,out bound))return false;
                try
                {
                    if(!initialized)
                    {
                        if(ordinal!=0)return false;
                        int manifestBytes=checked((manifest.Context!=null&&manifest.Context.IsLegacy?ViveRawSessionJournalCodec.ManifestPayloadV4(manifest):ViveRawSessionJournalCodec.ManifestPayloadV3(manifest)).Length+48);
                        if(!budget.FitsData(0,manifestBytes)||!budget.FitsData(manifestBytes,bound))return false;
                        headerBytes=manifestBytes;reserved=checked((long)manifestBytes+bound);initialized=true;Interlocked.Exchange(ref nextOrdinal,1);return true;
                    }
                    if(ordinal==0||!budget.FitsData(reserved,bound))return false;
                    reserved=checked(reserved+bound);Interlocked.Exchange(ref nextOrdinal,ordinal+1);return true;
                }
                catch(Exception e) when(e is InvalidOperationException||e is OverflowException||e is EncoderFallbackException||e is ArgumentException)
                {return false;}
            }
            // Writer-thread check binds the actual bytes to this FIFO item and
            // to the exact manifest selected by the producer. No refund occurs.
            internal bool VerifyEncoded(long ordinal,object record,int frameBytes,int actualHeaderBytes)
            {
                if(!initialized||ordinal<0||ordinal>=Interlocked.Read(ref nextOrdinal)||frameBytes<=0)return false;
                int bound;if(!TryFrameBound(ordinal,record,out bound))return false;
                if(ordinal==0)return actualHeaderBytes==headerBytes&&frameBytes>=actualHeaderBytes&&frameBytes-actualHeaderBytes<=bound;
                return actualHeaderBytes==0&&frameBytes<=bound;
            }
        }
    }
}
