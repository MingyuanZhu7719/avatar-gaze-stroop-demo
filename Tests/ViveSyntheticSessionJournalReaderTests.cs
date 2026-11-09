using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Memory-only, artificial VSESS004 frames and sealed fake writer; no
    // participant data, filesystem, export publication or device is used.
    public static class ViveSyntheticSessionJournalReaderTests
    {
        sealed class Frame
        {internal string Kind;internal Dictionary<string,string> Fields;internal Frame(string kind,Dictionary<string,string> fields){Kind=kind;Fields=fields;}}
        static Dictionary<string,string> Header(string scope="SYNTHETIC_ONLY")
        {return new Dictionary<string,string>{{"schema","vive-session-journal-v4"},{"data_scope",scope},{"identity_authority","DECLARED_ONLY"}};}
        static Frame[] Valid()
        {return new[]{new Frame("header",Header()),new Frame("task_event",new Dictionary<string,string>{{"kind","fixture"}}),new Frame("close",new Dictionary<string,string>{{"record_count","1"}})};}
        static byte[] Frames(Frame[] frames,out long[] ends)
        {
            var stream=new MemoryStream();byte[] magic=Encoding.ASCII.GetBytes("VSESS004");stream.Write(magic,0,magic.Length);
            var token=new byte[16];token[0]=4;byte[] prior=new byte[32];ends=new long[frames.Length];
            for(int i=0;i<frames.Length;i++)
            {
                byte[] payload=ContinuousJournal.Payload(i,token,frames[i].Kind,frames[i].Fields),digest=ContinuousJournal.Digest(prior,payload),prefix=new byte[8];
                ContinuousJournal.PutInt(prefix,payload.Length);ContinuousJournal.PutInt(prefix,~payload.Length,4);
                stream.Write(prefix,0,prefix.Length);stream.Write(payload,0,payload.Length);stream.Write(digest,0,digest.Length);
                prior=digest;ends[i]=stream.Length;
            }
            return stream.ToArray();
        }
        static ViveSyntheticSessionReadResult Read(byte[] bytes,bool recovery=false,int maxRecords=100,long extraBytes=1)
        {using(var stream=new MemoryStream(bytes,false))return ViveSyntheticSessionJournalReader.Read(stream,true,recovery,bytes.Length+extraBytes,maxRecords);}
        sealed class Probe:Stream
        {
            internal int Inspections;
            public override bool CanRead {get{Inspections++;throw new Exception("private");}}public override bool CanWrite {get{Inspections++;throw new Exception("private");}}
            public override bool CanSeek {get{return false;}}public override long Length {get{throw new NotSupportedException();}}
            public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n){Inspections++;throw new Exception("private");}
            public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}public override void Flush(){throw new NotSupportedException();}
            public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}public override void SetLength(long v){throw new NotSupportedException();}
        }
        sealed class FailRead:Stream
        {
            readonly byte[] data;readonly int cutoff;int position;
            internal FailRead(byte[] data,int cutoff){this.data=data;this.cutoff=cutoff;}
            public override bool CanRead {get{return true;}}public override bool CanWrite {get{return false;}}public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n){if(position>=cutoff)throw new IOException("private fixture");int c=Math.Min(n,Math.Min(cutoff-position,data.Length-position));Buffer.BlockCopy(data,position,b,o,c);position+=c;return c;}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long v){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic VSESS004 schema reader requires explicit action before inspecting input",()=>{
                var probe=new Probe();bool refused=false;try{ViveSyntheticSessionJournalReader.Read(probe,false,false,1000,20);}catch(InvalidOperationException){refused=true;}
                Check.True(refused);Check.Equal(0,probe.Inspections);
            });
            suite.Run("synthetic VSESS004 schema validates frame count but never claims live consent or durability",()=>{
                long[] ends;byte[] bytes=Frames(Valid(),out ends);var result=Read(bytes);
                Check.Equal(ViveSyntheticSessionReadStatus.StructuralCloseObserved,result.Status);Check.Equal(3,result.Records.Count);
                Check.True(result.CloseMarkerObserved);Check.Equal((long)bytes.Length,result.VerifiedBytes);
                Check.True(!result.WriterAcknowledgementProven&&!result.ConsentAuthenticated&&!result.SourceAuthenticated&&!result.DataScopeAuthenticated&&
                    result.ContainsPotentiallySensitiveData&&!result.DurableStorageProven&&!result.CompleteSessionProven);
            });
            suite.Run("synthetic VSESS004 every truncation requires a verified header and separate recovery",()=>{
                long[] ends;byte[] data=Frames(Valid(),out ends);
                for(int cut=0;cut<data.Length;cut++)
                {
                    var bytes=new byte[cut];Buffer.BlockCopy(data,0,bytes,0,cut);
                    var normal=Read(bytes,false,100,Math.Max(1,9-cut));var recovery=Read(bytes,true,100,Math.Max(1,9-cut));
                    if(cut<ends[0]){Check.Equal(ViveSyntheticSessionReadStatus.Invalid,normal.Status);Check.Equal(ViveSyntheticSessionReadStatus.Invalid,recovery.Status);}
                    else
                    {
                        Check.Equal(ViveSyntheticSessionReadStatus.RecoveryRequired,normal.Status);Check.Equal(0,normal.Records.Count);
                        Check.Equal((string)null,normal.Token);Check.Equal(0L,normal.VerifiedBytes);
                        Check.Equal(ViveSyntheticSessionReadStatus.VerifiedPrefix,recovery.Status);Check.True(recovery.Records.Count>=1);
                        Check.True(!recovery.CompleteSessionProven&&!recovery.DurableStorageProven);
                    }
                }
            });
            suite.Run("synthetic VSESS004 rejects real-scope or malformed header even during prefix recovery",()=>{
                foreach(var frame in new[]{new Frame("header",Header("REAL")),new Frame("bad",Header()),new Frame("header",new Dictionary<string,string>{{"schema","vive-session-journal-v4"}})})
                {
                    long[] ends;byte[] bytes=Frames(new[]{frame},out ends);var result=Read(bytes,true);
                    Check.Equal(ViveSyntheticSessionReadStatus.Invalid,result.Status);Check.Equal(0,result.Records.Count);
                    Check.Equal((string)null,result.Token);Check.Equal(0L,result.VerifiedBytes);
                }
            });
            suite.Run("synthetic VSESS004 schema rejects bad kind, key, close count and trailing frame",()=>{
                var cases=new[]{
                    new[]{new Frame("header",Header()),new Frame("other",new Dictionary<string,string>{{"a","b"}})},
                    new[]{new Frame("header",Header()),new Frame("task_event",new Dictionary<string,string>{{"bad-key","b"}})},
                    new[]{new Frame("header",Header()),new Frame("close",new Dictionary<string,string>{{"record_count","01"}})},
                    new[]{new Frame("header",Header()),new Frame("close",new Dictionary<string,string>{{"record_count","0"}}),new Frame("task_event",new Dictionary<string,string>{{"a","b"}})}
                };
                foreach(var frames in cases)
                {
                    long[] ends;byte[] bytes=Frames(frames,out ends);var normal=Read(bytes);var recovery=Read(bytes,true);
                    Check.Equal(ViveSyntheticSessionReadStatus.RecoveryRequired,normal.Status);Check.Equal(0,normal.Records.Count);
                    Check.Equal(ViveSyntheticSessionReadStatus.VerifiedPrefix,recovery.Status);
                    Check.Equal(ViveSessionFrameStatus.ConsumerRejected,recovery.FramingStatus);Check.True(!recovery.CompleteSessionProven);
                }
            });
            suite.Run("synthetic VSESS004 I/O and budget failures never expose staged records",()=>{
                long[] ends;byte[] bytes=Frames(Valid(),out ends);
                using(var input=new FailRead(bytes,(int)ends[0]))
                {var result=ViveSyntheticSessionJournalReader.Read(input,true,true,bytes.Length+1,100);Check.Equal(ViveSyntheticSessionReadStatus.ReadFailure,result.Status);Check.Equal(0,result.Records.Count);Check.Equal((string)null,result.Token);Check.Equal(0L,result.VerifiedBytes);}
                var budget=Read(bytes,true,2);Check.Equal(ViveSyntheticSessionReadStatus.LimitExceeded,budget.Status);Check.Equal(0,budget.Records.Count);Check.Equal((string)null,budget.Token);Check.Equal(0L,budget.VerifiedBytes);
            });
            suite.Run("synthetic VSESS004 close bytes after withdrawal do not prove writer acknowledgement",()=>{
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
                    byte[] bytes=target.CopyBytes();var result=Read(bytes);Check.Equal(ViveSyntheticSessionReadStatus.StructuralCloseObserved,result.Status);
                    Check.True(result.CloseMarkerObserved&&!result.WriterAcknowledgementProven&&!result.DurableStorageProven&&!result.ConsentAuthenticated&&!result.CompleteSessionProven);
                    Check.Equal(0,target.FlushCount);Check.Equal(0,target.DisposeCount);
                }
                finally{openRelease.Set();closeRelease.Set();writer.Withdraw();SpinWait.SpinUntil(()=>writer.HasExited,5000);open.Dispose();openRelease.Dispose();close.Dispose();closeRelease.Dispose();}
            });
        }
    }
}
