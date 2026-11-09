using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Pure framing fixtures in MemoryStream; no real session, filesystem,
    // device, publication, or File.Move is involved.
    public static class ViveSessionFrameScannerTests
    {
        static byte[] AddFrame(Stream stream,long ordinal,byte[] token,string kind,Dictionary<string,string> fields,byte[] previous)
        {
            byte[] payload=ContinuousJournal.Payload(ordinal,token,kind,fields),digest=ContinuousJournal.Digest(previous,payload),prefix=new byte[8];
            ContinuousJournal.PutInt(prefix,payload.Length);ContinuousJournal.PutInt(prefix,~payload.Length,4);
            stream.Write(prefix,0,prefix.Length);stream.Write(payload,0,payload.Length);stream.Write(digest,0,digest.Length);return digest;
        }
        static byte[] Fixture(out long[] ends)
        {
            var s=new MemoryStream();byte[] magic=Encoding.ASCII.GetBytes("VSESS004");s.Write(magic,0,magic.Length);
            byte[] token=new byte[16],prior=new byte[32];token[0]=7;ends=new long[3];
            prior=AddFrame(s,0,token,"header",new Dictionary<string,string>{{"schema","fixture"},{"empty",""},{"null",null}},prior);ends[0]=s.Length;
            prior=AddFrame(s,1,token,"task_event",new Dictionary<string,string>{{"word","fixture-value"}},prior);ends[1]=s.Length;
            AddFrame(s,2,token,"close",new Dictionary<string,string>(),prior);ends[2]=s.Length;return s.ToArray();
        }
        static ViveSessionFrameScanResult Scan(Stream stream,long maxBytes,int maxFrames=100,int maxPayload=100000,int maxFields=64,
            Func<ViveSessionFramedRecord,bool> callback=null)
        {return ViveSessionFrameScanner.Scan(stream,true,maxBytes,maxFrames,maxPayload,maxFields,callback);}
        sealed class ShortRead:MemoryStream
        {
            internal ShortRead(byte[] data):base(data,false){}
            public override int Read(byte[] buffer,int offset,int count){return base.Read(buffer,offset,Math.Min(1,count));}
        }
        sealed class CountingRead:MemoryStream
        {
            internal int Count;
            internal CountingRead(byte[] data):base(data,false){}
            public override int Read(byte[] buffer,int offset,int count){int n=base.Read(buffer,offset,count);Count+=n;return n;}
        }
        sealed class InspectProbe:Stream
        {
            internal int Inspections;
            public override bool CanRead {get{Inspections++;throw new InvalidOperationException("private");}}
            public override bool CanWrite {get{Inspections++;throw new InvalidOperationException("private");}}
            public override bool CanSeek {get{return false;}} public override long Length {get{throw new NotSupportedException();}}
            public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n){Inspections++;throw new InvalidOperationException("private");}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long x){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        sealed class FailRead:Stream
        {
            readonly byte[] data;readonly int failAt;int position;
            internal FailRead(byte[] data,int failAt){this.data=data;this.failAt=failAt;}
            public override bool CanRead {get{return true;}}public override bool CanWrite {get{return false;}}public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n)
            {if(position>=failAt)throw new InvalidDataException("private fixture path");int count=Math.Min(n,Math.Min(failAt-position,data.Length-position));Buffer.BlockCopy(data,position,b,o,count);position+=count;return count;}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long x){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VSESS004 framing requires explicit read before any stream inspection",()=>{
                var probe=new InspectProbe();bool refused=false;
                try{ViveSessionFrameScanner.Scan(probe,false,1024,10,1000,10,null);}catch(InvalidOperationException){refused=true;}
                Check.True(refused);Check.Equal(0,probe.Inspections);
            });
            suite.Run("VSESS004 framing validates chains over short reads without claiming schema completion",()=>{
                long[] ends;byte[] data=Fixture(out ends);var seen=new List<ViveSessionFramedRecord>();
                using(var source=new ShortRead(data))
                {
                    var result=Scan(source,data.Length,callback:r=>{seen.Add(r);return true;});
                    Check.Equal(ViveSessionFrameStatus.EndOfInput,result.Status);Check.Equal((long)data.Length,result.VerifiedBytes);
                    Check.Equal(3L,result.RecordCount);Check.Equal(3,seen.Count);Check.True(result.Token!=null);
                    Check.Equal("",seen[0].Fields["empty"]);Check.Equal((string)null,seen[0].Fields["null"]);Check.True(source.CanRead);
                }
            });
            suite.Run("VSESS004 framing labels every byte cut and releases only complete framing prefixes",()=>{
                long[] ends;byte[] data=Fixture(out ends);
                for(int cut=0;cut<=data.Length;cut++)
                {
                    var truncated=new byte[cut];Buffer.BlockCopy(data,0,truncated,0,cut);int callbacks=0;
                    using(var source=new MemoryStream(truncated,false))
                    {
                        var result=Scan(source,data.Length+1,callback:r=>{callbacks++;return true;});
                        bool boundary=cut==8||cut==ends[0]||cut==ends[1]||cut==ends[2];
                        Check.Equal(boundary?ViveSessionFrameStatus.EndOfInput:ViveSessionFrameStatus.Truncated,result.Status);
                        int count=cut>=ends[2]?3:cut>=ends[1]?2:cut>=ends[0]?1:0;
                        long verified=cut>=ends[2]?ends[2]:cut>=ends[1]?ends[1]:cut>=ends[0]?ends[0]:cut>=8?8:0;
                        Check.Equal(count,callbacks);Check.Equal((long)count,result.RecordCount);Check.Equal(verified,result.VerifiedBytes);
                    }
                }
            });
            suite.Run("VSESS004 framing rejects changed hash, mixed token and nonconsecutive ordinal",()=>{
                long[] ends;byte[] original=Fixture(out ends);var changed=(byte[])original.Clone();changed[20]^=1;
                using(var s=new MemoryStream(changed,false)){var r=Scan(s,changed.Length);Check.Equal(ViveSessionFrameStatus.Invalid,r.Status);Check.Equal(0L,r.RecordCount);}
                foreach(bool mixed in new[]{true,false})
                {
                    var s=new MemoryStream();byte[] magic=Encoding.ASCII.GetBytes("VSESS004");s.Write(magic,0,magic.Length);
                    byte[] a=new byte[16],b=new byte[16];b[0]=1;
                    byte[] digest=AddFrame(s,0,a,"header",new Dictionary<string,string>(),new byte[32]);long first=s.Length;
                    AddFrame(s,mixed?1:2,mixed?b:a,"task_event",new Dictionary<string,string>(),digest);
                    using(var input=new MemoryStream(s.ToArray(),false))
                    {var result=Scan(input,s.Length);Check.Equal(ViveSessionFrameStatus.Invalid,result.Status);Check.Equal(1L,result.RecordCount);Check.Equal(first,result.VerifiedBytes);}
                }
            });
            suite.Run("VSESS004 framing distinguishes caller budgets from malformed input",()=>{
                long[] ends;byte[] data=Fixture(out ends);
                using(var s=new MemoryStream(data,false))Check.Equal(ViveSessionFrameStatus.BudgetExceeded,Scan(s,data.Length-1).Status);
                using(var s=new MemoryStream(data,false))Check.Equal(ViveSessionFrameStatus.BudgetExceeded,Scan(s,data.Length,maxFrames:2).Status);
                using(var s=new MemoryStream(data,false))Check.Equal(ViveSessionFrameStatus.BudgetExceeded,Scan(s,data.Length,maxPayload:32).Status);
                using(var s=new MemoryStream(data,false))Check.Equal(ViveSessionFrameStatus.BudgetExceeded,Scan(s,data.Length,maxFields:1).Status);
                var invalid=(byte[])data.Clone();invalid[12]^=1;
                using(var s=new MemoryStream(invalid,false))Check.Equal(ViveSessionFrameStatus.Invalid,Scan(s,invalid.Length).Status);
                using(var s=new CountingRead(data))
                {var result=Scan(s,data.Length-1);Check.Equal(ViveSessionFrameStatus.BudgetExceeded,result.Status);Check.Equal(data.Length,s.Count);}
            });
            suite.Run("VSESS004 input or consumer errors are sanitized and are never clean status",()=>{
                long[] ends;byte[] data=Fixture(out ends);
                using(var source=new FailRead(data,(int)ends[0]))
                {var result=Scan(source,data.Length);Check.Equal(ViveSessionFrameStatus.ReadFailure,result.Status);Check.Equal(1L,result.RecordCount);Check.Equal(ends[0],result.VerifiedBytes);}
                using(var source=new MemoryStream(data,false))
                {var result=Scan(source,data.Length,callback:r=>r.Ordinal==0);Check.Equal(ViveSessionFrameStatus.ConsumerRejected,result.Status);Check.Equal(1L,result.RecordCount);Check.Equal(ends[0],result.VerifiedBytes);}
                using(var source=new MemoryStream(data,false))
                {var result=Scan(source,data.Length,callback:r=>{if(r.Ordinal>0)throw new InvalidDataException("private fixture");return true;});Check.Equal(ViveSessionFrameStatus.ConsumerFailed,result.Status);Check.Equal(1L,result.RecordCount);}
            });
            suite.Run("VSESS004 valid callbacks followed by invalid or truncated framing never imply complete schema",()=>{
                long[] ends;byte[] data=Fixture(out ends);
                foreach(bool corrupt in new[]{true,false})
                {
                    byte[] input;if(corrupt){input=(byte[])data.Clone();input[(int)ends[1]-1]^=1;}
                    else {input=new byte[(int)ends[1]-1];Buffer.BlockCopy(data,0,input,0,input.Length);}
                    var staged=new List<ViveSessionFramedRecord>();
                    using(var stream=new MemoryStream(input,false))
                    {
                        var result=Scan(stream,data.Length+1,callback:r=>{staged.Add(r);return true;});
                        Check.Equal(corrupt?ViveSessionFrameStatus.Invalid:ViveSessionFrameStatus.Truncated,result.Status);
                        Check.Equal(1,staged.Count);Check.Equal(1L,result.RecordCount);Check.Equal(ends[0],result.VerifiedBytes);
                        // The scanner does not revoke consumer references; caller
                        // must discard the stage unless an explicit recovery and
                        // independent schema validator authorize a prefix.
                    }
                }
                using(var magicOnly=new MemoryStream(Encoding.ASCII.GetBytes("VSESS004"),false))
                {var result=Scan(magicOnly,8);Check.Equal(ViveSessionFrameStatus.EndOfInput,result.Status);Check.Equal(0L,result.RecordCount);}
            });
            suite.Run("VSESS004 framing interoperates with the sealed synthetic writer without claiming durable completion",()=>{
                var ready=new ManualResetEvent(false);var release=new ManualResetEvent(false);var target=new ViveSessionSyntheticTarget();
                target.BeforeOperation=op=>{if(op=="open"){ready.Set();if(!release.WaitOne(5000))throw new IOException("synthetic timeout");}};
                var writer=new ViveSessionJournalAsyncWriter(target,8,32768);
                try
                {
                    Check.True(ready.WaitOne(5000));Check.True(writer.TryEnqueue(new ViveSessionSyntheticRecord("task_event",new[]{"kind"},new[]{"synthetic"})));
                    Check.True(writer.Finish());release.Set();Check.True(SpinWait.SpinUntil(()=>writer.HasExited,5000));
                    Check.Equal(ViveSessionSyntheticWriterState.Closed,writer.State);
                    var frames=new List<ViveSessionFramedRecord>();byte[] bytes=target.CopyBytes();
                    using(var input=new MemoryStream(bytes,false))
                    {var result=Scan(input,bytes.Length,callback:r=>{frames.Add(r);return true;});Check.Equal(ViveSessionFrameStatus.EndOfInput,result.Status);Check.Equal(3L,result.RecordCount);}
                    Check.Equal("vive-session-journal-v4",frames[0].Fields["schema"]);Check.Equal("SYNTHETIC_ONLY",frames[0].Fields["data_scope"]);
                    Check.Equal("task_event",frames[1].Kind);Check.Equal("close",frames[2].Kind);Check.Equal("1",frames[2].Fields["record_count"]);
                    Check.True(!writer.DurableStorageProven&&!writer.ProductionGateOpen&&!writer.ConsentAuthenticated);
                }
                finally{release.Set();writer.Withdraw();SpinWait.SpinUntil(()=>writer.HasExited,5000);ready.Dispose();release.Dispose();}
            });
            suite.Run("VSESS004 close bytes after in-flight withdrawal prove framing EOF only",()=>{
                var open=new ManualResetEvent(false);var openRelease=new ManualResetEvent(false);
                var close=new ManualResetEvent(false);var closeRelease=new ManualResetEvent(false);
                var target=new ViveSessionSyntheticTarget();int writes=0;
                target.BeforeOperation=op=>{
                    if(op=="open"){open.Set();if(!openRelease.WaitOne(5000))throw new IOException("synthetic timeout");}
                    if(op=="write"&&Interlocked.Increment(ref writes)==3){close.Set();if(!closeRelease.WaitOne(5000))throw new IOException("synthetic timeout");}
                };
                var writer=new ViveSessionJournalAsyncWriter(target,8,32768);
                try
                {
                    Check.True(open.WaitOne(5000));Check.True(writer.TryEnqueue(new ViveSessionSyntheticRecord("task_event",new[]{"kind"},new[]{"synthetic"})));
                    Check.True(writer.Finish());openRelease.Set();Check.True(close.WaitOne(5000));writer.Withdraw();closeRelease.Set();
                    Check.True(SpinWait.SpinUntil(()=>writer.HasExited,5000));Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);
                    byte[] bytes=target.CopyBytes();var frames=new List<ViveSessionFramedRecord>();
                    using(var input=new MemoryStream(bytes,false))
                    {var result=Scan(input,bytes.Length,callback:r=>{frames.Add(r);return true;});Check.Equal(ViveSessionFrameStatus.EndOfInput,result.Status);}
                    Check.Equal("close",frames[frames.Count-1].Kind);Check.Equal(0,target.FlushCount);Check.Equal(0,target.DisposeCount);
                    Check.True(!writer.DurableStorageProven&&!writer.ConsentAuthenticated);
                }
                finally{openRelease.Set();closeRelease.Set();writer.Withdraw();SpinWait.SpinUntil(()=>writer.HasExited,5000);open.Dispose();openRelease.Dispose();close.Dispose();closeRelease.Dispose();}
            });
        }
    }
}
