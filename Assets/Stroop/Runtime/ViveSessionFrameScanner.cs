using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Framing-only status. EndOfInput says only that byte framing reached EOF;
    // it is not a schema-valid close, durable storage or session completion.
    internal enum ViveSessionFrameStatus { EndOfInput,Truncated,Invalid,ReadFailure,BudgetExceeded,ConsumerRejected,ConsumerFailed }
    internal sealed class ViveSessionFramedRecord
    {
        internal long Ordinal {get;private set;}
        internal string Kind {get;private set;}
        internal IReadOnlyDictionary<string,string> Fields {get;private set;}
        internal ViveSessionFramedRecord(long ordinal,string kind,Dictionary<string,string> fields)
        {Ordinal=ordinal;Kind=kind;Fields=new ReadOnlyDictionary<string,string>(fields);}
    }
    internal sealed class ViveSessionFrameScanResult
    {
        internal ViveSessionFrameStatus Status {get;set;}
        internal long VerifiedBytes {get;set;}
        internal long RecordCount {get;set;}
        internal string Token {get;set;}
    }
    // Internal primitive only. A future schema validator must independently
    // check header, field domains, stop semantics, withdrawal and coverage.
    // A callback sees framing-valid records before final EOF/status and can
    // retain references. Consumers must stage privately and not publish unless
    // final framing AND independent schema/terminal checks succeed. Invalid,
    // truncated, budget, I/O, consumer failure or rejected schema all require
    // discarding the stage. A verified prefix requires a separate explicit
    // recovery decision and independent schema checks. This primitive does not
    // release callbacks or securely wipe memory. Neither it nor a hash chain
    // authenticates data or grants publication.
    internal static class ViveSessionFrameScanner
    {
        static readonly byte[] Magic=Encoding.ASCII.GetBytes("VSESS004");
        enum ReadResult { Full,End,Budget }
        sealed class InputReadFailure:Exception {}
        sealed class BoundedInput
        {
            readonly Stream source;readonly long limit;long consumed;
            internal BoundedInput(Stream source,long limit){this.source=source;this.limit=limit;}
            internal long Consumed {get{return consumed;}}
            int Read(byte[] value,int offset,int count)
            {
                try{int n=source.Read(value,offset,count);if(n<0||n>count)throw new InputReadFailure();return n;}
                catch(Exception e) when(!Fatal(e)){throw new InputReadFailure();}
            }
            internal ReadResult ReadExact(byte[] value)
            {
                int offset=0;
                while(offset<value.Length)
                {
                    if(consumed>=limit)
                    {
                        // One read-only probe distinguishes an exact EOF from
                        // extra data beyond the configured input budget.
                        byte[] one=new byte[1];return Read(one,0,1)==0?ReadResult.End:ReadResult.Budget;
                    }
                    int count=(int)Math.Min(value.Length-offset,limit-consumed);
                    int n=Read(value,offset,count);if(n==0)return ReadResult.End;
                    consumed=checked(consumed+n);offset+=n;
                }
                return ReadResult.Full;
            }
        }
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        static ViveSessionFrameScanResult Stop(ViveSessionFrameScanResult result,ViveSessionFrameStatus status)
        {result.Status=status;return result;}
        // Does not inspect the stream until the explicit read-only action.
        // Limits are on input bytes, number of frames, each payload and fields
        // per frame. The source remains owned by the caller and is not rewound.
        // At the byte limit, one extra read-only byte may be probed to tell EOF
        // from over-budget input; maximum actual consumption is budget + 1.
        // VerifiedBytes and RecordCount omit the current frame if the callback
        // rejects or throws even though that frame passed framing/hash checks.
        // EndOfInput includes magic-only input: schema must reject no header.
        internal static ViveSessionFrameScanResult Scan(Stream input,bool explicitReadOnlyAction,long maximumBytes,
            int maximumFrames,int maximumPayloadBytes,int maximumFields,Func<ViveSessionFramedRecord,bool> onRecord)
        {
            if(!explicitReadOnlyAction)throw new InvalidOperationException("Explicit read-only action required.");
            if(maximumBytes<8||maximumBytes>1073741824L||maximumFrames<1||maximumFrames>1000000||
                maximumPayloadBytes<32||maximumPayloadBytes>ContinuousJournal.MaximumFrameBytes||
                maximumFields<1||maximumFields>4096)throw new ArgumentOutOfRangeException("Explicit bounded limits required.");
            var result=new ViveSessionFrameScanResult();byte[] prior=new byte[32],token=null;
            try
            {
                try{if(input==null||!input.CanRead||input.CanWrite)return Stop(result,ViveSessionFrameStatus.ReadFailure);}
                catch(Exception e) when(!Fatal(e)){return Stop(result,ViveSessionFrameStatus.ReadFailure);}
                var source=new BoundedInput(input,maximumBytes);var magic=new byte[Magic.Length];
                var state=source.ReadExact(magic);
                if(state==ReadResult.Budget)return Stop(result,ViveSessionFrameStatus.BudgetExceeded);
                if(state==ReadResult.End)return Stop(result,ViveSessionFrameStatus.Truncated);
                if(!ContinuousJournal.Equal(magic,Magic))return Stop(result,ViveSessionFrameStatus.Invalid);
                result.VerifiedBytes=magic.Length;
                for(long sequence=0;;sequence++)
                {
                    var prefix=new byte[8];state=source.ReadExact(prefix);
                    if(state==ReadResult.Budget)return Stop(result,ViveSessionFrameStatus.BudgetExceeded);
                    if(state==ReadResult.End)
                        return Stop(result,source.Consumed==result.VerifiedBytes?ViveSessionFrameStatus.EndOfInput:ViveSessionFrameStatus.Truncated);
                    if(sequence>=maximumFrames)return Stop(result,ViveSessionFrameStatus.BudgetExceeded);
                    int length=ContinuousJournal.Int(prefix);
                    if(ContinuousJournal.Int(prefix,4)!=~length||length<32||length>ContinuousJournal.MaximumFrameBytes)return Stop(result,ViveSessionFrameStatus.Invalid);
                    if(length>maximumPayloadBytes)return Stop(result,ViveSessionFrameStatus.BudgetExceeded);
                    var payload=new byte[length];var digest=new byte[32];
                    state=source.ReadExact(payload);if(state!=ReadResult.Full)return Stop(result,state==ReadResult.Budget?ViveSessionFrameStatus.BudgetExceeded:ViveSessionFrameStatus.Truncated);
                    state=source.ReadExact(digest);if(state!=ReadResult.Full)return Stop(result,state==ReadResult.Budget?ViveSessionFrameStatus.BudgetExceeded:ViveSessionFrameStatus.Truncated);
                    if(!ContinuousJournal.Equal(digest,ContinuousJournal.Digest(prior,payload)))return Stop(result,ViveSessionFrameStatus.Invalid);
                    long ordinal;byte[] frameToken;string kind;var fields=new Dictionary<string,string>(StringComparer.Ordinal);
                    using(var memory=new MemoryStream(payload,false))using(var reader=new BinaryReader(memory,ContinuousJournal.Utf8,true))
                    {
                        ordinal=reader.ReadInt64();frameToken=reader.ReadBytes(16);kind=ContinuousJournal.ReadString(reader,false);int count=reader.ReadInt32();
                        if(ordinal!=sequence||frameToken.Length!=16||String.IsNullOrWhiteSpace(kind)||count<0||count>4096)
                            return Stop(result,ViveSessionFrameStatus.Invalid);
                        if(count>maximumFields)return Stop(result,ViveSessionFrameStatus.BudgetExceeded);
                        for(int i=0;i<count;i++)
                        {
                            string key=ContinuousJournal.ReadString(reader,false);if(String.IsNullOrEmpty(key)||fields.ContainsKey(key))
                                return Stop(result,ViveSessionFrameStatus.Invalid);
                            fields.Add(key,ContinuousJournal.ReadString(reader,true));
                        }
                        if(memory.Position!=memory.Length)return Stop(result,ViveSessionFrameStatus.Invalid);
                    }
                    if(token!=null&&!ContinuousJournal.Equal(token,frameToken))return Stop(result,ViveSessionFrameStatus.Invalid);
                    var record=new ViveSessionFramedRecord(ordinal,kind,fields);
                    if(onRecord!=null)
                    {
                        bool accepted;try{accepted=onRecord(record);}catch(Exception e) when(!Fatal(e)){return Stop(result,ViveSessionFrameStatus.ConsumerFailed);}
                        if(!accepted)return Stop(result,ViveSessionFrameStatus.ConsumerRejected);
                    }
                    if(token==null){token=frameToken;result.Token=new Guid(token).ToString("N");}
                    prior=digest;result.VerifiedBytes=source.Consumed;result.RecordCount++;
                }
            }
            catch(InputReadFailure){return Stop(result,ViveSessionFrameStatus.ReadFailure);}
            catch(Exception e) when(e is InvalidDataException||e is EndOfStreamException||e is DecoderFallbackException||e is ArgumentException||e is OverflowException)
            {return Stop(result,ViveSessionFrameStatus.Invalid);}
            catch(Exception e) when(!Fatal(e))
            {return Stop(result,ViveSessionFrameStatus.ReadFailure);}
        }
    }
}
