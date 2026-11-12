using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Sealed fake MemoryStream target only. No real session, file, network,
    // device, CreateNew or File.Move is used by these tests.
    public static class ViveSessionJournalAsyncWriterTests
    {
        sealed class Frame
        {internal long Ordinal;internal byte[] Token;internal string Kind;internal Dictionary<string,string> Fields;}
        static List<Frame> Decode(byte[] all)
        {
            Check.True(all.Length>=8&&Encoding.ASCII.GetString(all,0,8)=="VSESS004");
            var frames=new List<Frame>();var prior=new byte[32];int pos=8;
            while(pos<all.Length)
            {
                Check.True(pos<=all.Length-8);int n=ContinuousJournal.Int(all,pos),complement=ContinuousJournal.Int(all,pos+4);pos+=8;
                Check.True(n>=0&&complement==~n&&n<=all.Length-pos-32);
                var payload=new byte[n];Buffer.BlockCopy(all,pos,payload,0,n);pos+=n;
                var hash=new byte[32];Buffer.BlockCopy(all,pos,hash,0,32);pos+=32;
                Check.True(ContinuousJournal.Equal(hash,ContinuousJournal.Digest(prior,payload)));prior=hash;
                using(var memory=new MemoryStream(payload,false))using(var reader=new BinaryReader(memory,new UTF8Encoding(false,true)))
                {
                    var frame=new Frame{Ordinal=reader.ReadInt64(),Token=reader.ReadBytes(16),Kind=ContinuousJournal.ReadString(reader,false),Fields=new Dictionary<string,string>()};
                    int fields=reader.ReadInt32();Check.True(fields>=0&&fields<=16);
                    for(int i=0;i<fields;i++)frame.Fields.Add(ContinuousJournal.ReadString(reader,false),ContinuousJournal.ReadString(reader,true));
                    Check.Equal(memory.Length,memory.Position);frames.Add(frame);
                }
            }
            return frames;
        }
        static ViveSessionSyntheticRecord Record(string value)
        {return new ViveSessionSyntheticRecord("task_event",new[]{"synthetic_value"},new[]{value});}
        static void Exit(ViveSessionJournalAsyncWriter writer)
        {Check.True(SpinWait.SpinUntil(()=>writer.HasExited,5000),"Synthetic worker did not exit.");}
        // Positive enqueue/finish cases arrange an available queue gate rather
        // than retrying a false result: contention terminates this writer.
        // The existing fake target hook runs outside the writer queue gate.
        sealed class HeaderPausedWriter:IDisposable
        {
            readonly ManualResetEvent entered=new ManualResetEvent(false),release=new ManualResetEvent(false);
            internal readonly ViveSessionJournalAsyncWriter Writer;
            internal HeaderPausedWriter(ViveSessionSyntheticTarget target,int count,int bytes,long maximumOutputBytes=67108864)
            {
                var previous=target.BeforeOperation;int writes=0;
                target.BeforeOperation=operation=>{
                    if(operation=="write"&&Interlocked.Increment(ref writes)==1)
                    {entered.Set();if(!release.WaitOne(5000))throw new IOException("fixture header barrier timeout");}
                    if(previous!=null)previous(operation);
                };
                Writer=new ViveSessionJournalAsyncWriter(target,count,bytes,maximumOutputBytes);
                if(!entered.WaitOne(5000)){Writer.Withdraw();release.Set();Exit(Writer);throw new Exception("Synthetic header write did not reach its barrier.");}
                Check.True(Writer.HasReservedIo&&target.WriteCount==0,"Header must be reserved outside the queue gate before owner actions.");
            }
            internal void Release(){release.Set();}
            public void Dispose(){Writer.Withdraw();release.Set();Exit(Writer);entered.Dispose();release.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic async writer emits versioned hash chained records and clean finish",()=>{
                var target=new ViveSessionSyntheticTarget();int owner=Thread.CurrentThread.ManagedThreadId;
                using(var header=new HeaderPausedWriter(target,8,32768))
                {
                var writer=header.Writer;
                string[] keys={"synthetic_value"},values={"first"};var record=new ViveSessionSyntheticRecord("task_event",keys,values);
                keys[0]="changed";values[0]="mutated";
                Check.True(writer.TryEnqueue(record));Check.True(writer.TryEnqueue(new ViveSessionSyntheticRecord("eye_sample",new[]{"marker"},new[]{"fake"})));
                Check.True(writer.Finish());header.Release();Exit(writer);Check.Equal(ViveSessionSyntheticWriterState.Closed,writer.State);
                Check.Equal(2L,writer.Accepted);Check.Equal(2L,writer.Committed);
                Check.True(target.OpenThread!=owner&&target.OpenThread==target.WriteThread&&target.OpenThread==target.FlushThread&&target.OpenThread==target.DisposeThread);
                var frames=Decode(target.CopyBytes());Check.Equal(4,frames.Count);
                for(int i=0;i<frames.Count;i++){Check.Equal((long)i,frames[i].Ordinal);Check.Equal(BitConverter.ToString(frames[0].Token),BitConverter.ToString(frames[i].Token));}
                Check.Equal("header",frames[0].Kind);Check.Equal("vive-session-journal-v4",frames[0].Fields["schema"]);
                Check.Equal("SYNTHETIC_ONLY",frames[0].Fields["data_scope"]);Check.Equal("DECLARED_ONLY",frames[0].Fields["identity_authority"]);
                Check.Equal("first",frames[1].Fields["synthetic_value"]);Check.Equal("eye_sample",frames[2].Kind);
                Check.Equal("close",frames[3].Kind);Check.Equal("2",frames[3].Fields["record_count"]);
                Check.True(!writer.ProductionGateOpen&&!writer.ConsentAuthenticated&&!writer.HardwareAuthenticated&&!writer.DurableStorageProven);
                }
            });
            suite.Run("synthetic record fields are strictly bounded and copied",()=>{
                Check.Throws<ArgumentException>(()=>new ViveSessionSyntheticRecord("close",new[]{"x"},new[]{"a"}));
                Check.Throws<ArgumentException>(()=>new ViveSessionSyntheticRecord("task_event",new[]{"bad-key"},new[]{"a"}));
                Check.Throws<ArgumentException>(()=>new ViveSessionSyntheticRecord("task_event",new[]{"x","x"},new[]{"a","b"}));
                Check.Throws<ArgumentException>(()=>new ViveSessionSyntheticRecord("task_event",new[]{"x"},new[]{new string('a',1025)}));
                Check.Throws<ArgumentException>(()=>new ViveSessionSyntheticRecord("task_event",new string[17],new string[17]));
            });
            suite.Run("withdraw never waits on in-flight synthetic write and discards queued work",()=>{
                var inWrite=new ManualResetEvent(false);var release=new ManualResetEvent(false);var target=new ViveSessionSyntheticTarget();
                target.BeforeOperation=op=>{if(op=="write"){inWrite.Set();if(!release.WaitOne(5000))throw new IOException("fixture timeout");}};
                var writer=new ViveSessionJournalAsyncWriter(target,4,32768);Check.True(inWrite.WaitOne(5000));
                Check.True(writer.TryEnqueue(Record("queued")));var watch=System.Diagnostics.Stopwatch.StartNew();writer.Withdraw();watch.Stop();
                Check.True(watch.ElapsedMilliseconds<500);Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);
                Check.True(!writer.TryEnqueue(Record("late")));release.Set();Exit(writer);
                Check.Equal(1,target.WriteCount);Check.Equal(0,target.FlushCount);Check.Equal(0,target.DisposeCount);Check.Equal(0L,writer.Committed);
                var frames=Decode(target.CopyBytes());Check.Equal(1,frames.Count);Check.Equal("header",frames[0].Kind);
            });
            suite.Run("a close write reserved before withdrawal remains unclean even if bytes contain close",()=>{
                var atClose=new ManualResetEvent(false);var release=new ManualResetEvent(false);var target=new ViveSessionSyntheticTarget();int writes=0;
                target.BeforeOperation=op=>{if(op=="write"&&Interlocked.Increment(ref writes)==3){atClose.Set();if(!release.WaitOne(5000))throw new IOException("fixture timeout");}};
                using(var header=new HeaderPausedWriter(target,4,32768))
                {
                var writer=header.Writer;Check.True(writer.TryEnqueue(Record("one")));Check.True(writer.Finish());header.Release();
                Check.True(atClose.WaitOne(5000));writer.Withdraw();Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);
                release.Set();Exit(writer);Check.Equal(0,target.FlushCount);Check.Equal(0,target.DisposeCount);
                var frames=Decode(target.CopyBytes());Check.Equal(3,frames.Count);Check.Equal("close",frames[2].Kind);
                Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);
                }
            });
            suite.Run("queue overflow is fail closed and leaves in-flight result uncertain",()=>{
                var inWrite=new ManualResetEvent(false);var release=new ManualResetEvent(false);var target=new ViveSessionSyntheticTarget();
                target.BeforeOperation=op=>{if(op=="write"){inWrite.Set();if(!release.WaitOne(5000))throw new IOException("fixture timeout");}};
                var writer=new ViveSessionJournalAsyncWriter(target,1,32768);Check.True(inWrite.WaitOne(5000));
                Check.True(writer.TryEnqueue(Record("one")));Check.True(!writer.TryEnqueue(Record("overflow")));
                Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);release.Set();Exit(writer);
                Check.Equal(1,target.WriteCount);Check.Equal(0,target.FlushCount);
            });
            suite.Run("queue lock contention fails immediately and never waits for I/O",()=>{
                var target=new ViveSessionSyntheticTarget();var writer=new ViveSessionJournalAsyncWriter(target,4,32768);
                var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                var holder=new Thread(()=>{lock(writer.QueueGateForTest){entered.Set();release.WaitOne(5000);}});holder.Start();Check.True(entered.WaitOne(5000));
                var watch=System.Diagnostics.Stopwatch.StartNew();Check.True(!writer.TryEnqueue(Record("contended")));watch.Stop();
                Check.True(watch.ElapsedMilliseconds<500);release.Set();Check.True(holder.Join(5000));Exit(writer);
                Check.True(writer.State==ViveSessionSyntheticWriterState.Faulted||writer.State==ViveSessionSyntheticWriterState.Uncertain);
            });
            suite.Run("finish under a held queue gate fails once and cannot produce a clean synthetic close",()=>{
                var target=new ViveSessionSyntheticTarget();
                using(var header=new HeaderPausedWriter(target,4,32768))
                using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {
                    var writer=header.Writer;
                    var holder=new Thread(()=>{lock(writer.QueueGateForTest){entered.Set();release.WaitOne(5000);}}){IsBackground=true};holder.Start();
                    try
                    {
                        Check.True(entered.WaitOne(5000));var watch=System.Diagnostics.Stopwatch.StartNew();
                        bool finished=writer.Finish();watch.Stop(); // Exactly one request; false is terminal.
                        Check.True(!finished&&watch.ElapsedMilliseconds<500,"Contended Finish must return false promptly, never wait or be retried.");
                        Check.True(writer.QueueDetachedForTest);Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);
                        Check.Equal(0,target.WriteCount);Check.Equal(0,target.FlushCount);Check.Equal(0,target.DisposeCount);
                    }
                    finally{release.Set();Check.True(holder.Join(5000));header.Release();}
                    Exit(writer);Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);
                    Check.Equal(0L,writer.Accepted);Check.Equal(0L,writer.Committed);Check.Equal(0,target.FlushCount);Check.Equal(0,target.DisposeCount);
                    var frames=Decode(target.CopyBytes());Check.Equal(1,frames.Count);Check.Equal("header",frames[0].Kind);
                }
            });
            suite.Run("withdrawal detaches queued synthetic records without waiting for held queue gate",()=>{
                var inWrite=new ManualResetEvent(false);var releaseWrite=new ManualResetEvent(false);var target=new ViveSessionSyntheticTarget();
                target.BeforeOperation=op=>{if(op=="write"){inWrite.Set();if(!releaseWrite.WaitOne(5000))throw new IOException("fixture timeout");}};
                var writer=new ViveSessionJournalAsyncWriter(target,4,32768);Check.True(inWrite.WaitOne(5000));
                Check.True(writer.TryEnqueue(Record("synthetic-queued")));var entered=new ManualResetEvent(false);var releaseGate=new ManualResetEvent(false);
                var holder=new Thread(()=>{lock(writer.QueueGateForTest){entered.Set();releaseGate.WaitOne(5000);}});holder.Start();Check.True(entered.WaitOne(5000));
                var watch=System.Diagnostics.Stopwatch.StartNew();writer.Withdraw();watch.Stop();
                Check.True(watch.ElapsedMilliseconds<500&&writer.QueueDetachedForTest);Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);
                releaseGate.Set();releaseWrite.Set();Check.True(holder.Join(5000));Exit(writer);Check.Equal(0L,writer.Committed);
            });
            suite.Run("fake I/O exception produces uncertain prefix without flush or retry",()=>{
                var target=new ViveSessionSyntheticTarget();int calls=0;
                target.BeforeOperation=op=>{if(op=="write"&&Interlocked.Increment(ref calls)==2)throw new IOException("synthetic");};
                using(var header=new HeaderPausedWriter(target,4,32768))
                {
                var writer=header.Writer;Check.True(writer.TryEnqueue(Record("one")));header.Release();
                Exit(writer);Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);Check.Equal(1,target.WriteCount);
                Check.Equal(0,target.FlushCount);Check.Equal(0,target.DisposeCount);Check.Equal(0L,writer.Committed);
                }
            });
            suite.Run("withdrawal while synthetic factory open is reserved starts no later write",()=>{
                var opened=new ManualResetEvent(false);var release=new ManualResetEvent(false);var target=new ViveSessionSyntheticTarget();
                target.BeforeOperation=op=>{if(op=="open"){opened.Set();if(!release.WaitOne(5000))throw new IOException("fixture timeout");}};
                var writer=new ViveSessionJournalAsyncWriter(target,4,32768);Check.True(opened.WaitOne(5000));writer.Withdraw();
                Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);release.Set();Exit(writer);
                Check.Equal(1,target.OpenCount);Check.Equal(0,target.WriteCount);Check.Equal(0,target.FlushCount);Check.Equal(0,target.DisposeCount);
            });
            suite.Run("idle withdrawal is revoked without clean marker or flush",()=>{
                var target=new ViveSessionSyntheticTarget();var writer=new ViveSessionJournalAsyncWriter(target,4,32768);
                Check.True(SpinWait.SpinUntil(()=>target.WriteCount==1&&!writer.HasReservedIo,5000));
                writer.Withdraw();Exit(writer);Check.Equal(ViveSessionSyntheticWriterState.Revoked,writer.State);
                Check.Equal(0,target.FlushCount);Check.Equal(0,target.DisposeCount);var frames=Decode(target.CopyBytes());Check.Equal(1,frames.Count);
            });
            suite.Run("synthetic flush failure is uncertain even when close bytes exist",()=>{
                var target=new ViveSessionSyntheticTarget();target.BeforeOperation=op=>{if(op=="flush")throw new IOException("synthetic flush failure");};
                using(var header=new HeaderPausedWriter(target,4,32768))
                {
                var writer=header.Writer;Check.True(writer.TryEnqueue(Record("one")));Check.True(writer.Finish());header.Release();
                Exit(writer);Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);Check.Equal(0,target.FlushCount);Check.Equal(0,target.DisposeCount);
                var frames=Decode(target.CopyBytes());Check.Equal("close",frames[frames.Count-1].Kind);
                }
            });
            suite.Run("separate synthetic sessions receive distinct tokens",()=>{
                var a=new ViveSessionSyntheticTarget();var b=new ViveSessionSyntheticTarget();
                using(var first=new HeaderPausedWriter(a,4,32768))using(var second=new HeaderPausedWriter(b,4,32768))
                {
                var x=first.Writer;var y=second.Writer;
                bool xFinish=x.Finish(),yFinish=y.Finish(); // One request per independent writer, never retry false.
                Check.True(xFinish&&yFinish);first.Release();second.Release();Exit(x);Exit(y);Check.Equal(ViveSessionSyntheticWriterState.Closed,x.State);Check.Equal(ViveSessionSyntheticWriterState.Closed,y.State);
                Check.True(BitConverter.ToString(Decode(a.CopyBytes())[0].Token)!=BitConverter.ToString(Decode(b.CopyBytes())[0].Token));
                }
            });
            suite.Run("withdrawal during reserved flush forbids subsequent dispose",()=>{
                var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);var target=new ViveSessionSyntheticTarget();
                target.BeforeOperation=op=>{if(op=="flush"){entered.Set();if(!release.WaitOne(5000))throw new IOException("fixture timeout");}};
                using(var header=new HeaderPausedWriter(target,4,32768))
                {
                var writer=header.Writer;Check.True(writer.Finish());header.Release();Check.True(entered.WaitOne(5000));
                writer.Withdraw();Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);release.Set();Exit(writer);
                Check.Equal(1,target.FlushCount);Check.Equal(0,target.DisposeCount);Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);
                }
            });
            suite.Run("withdrawal during reserved dispose preserves uncertain outcome",()=>{
                var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);var target=new ViveSessionSyntheticTarget();
                target.BeforeOperation=op=>{if(op=="dispose"){entered.Set();if(!release.WaitOne(5000))throw new IOException("fixture timeout");}};
                using(var header=new HeaderPausedWriter(target,4,32768))
                {
                var writer=header.Writer;Check.True(writer.Finish());header.Release();Check.True(entered.WaitOne(5000));
                writer.Withdraw();Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);release.Set();Exit(writer);
                Check.Equal(1,target.FlushCount);Check.Equal(1,target.DisposeCount);Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);
                }
            });
            suite.Run("concurrent withdrawal and fault preserve in-flight uncertainty",()=>{
                var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);var target=new ViveSessionSyntheticTarget();
                target.BeforeOperation=op=>{if(op=="write"){entered.Set();if(!release.WaitOne(5000))throw new IOException("fixture timeout");}};
                var writer=new ViveSessionJournalAsyncWriter(target,4,32768);Check.True(entered.WaitOne(5000));
                var go=new ManualResetEvent(false);var a=new Thread(()=>{go.WaitOne();writer.Withdraw();});
                var b=new Thread(()=>{go.WaitOne();writer.TryEnqueue(Record("wrong-thread"));});a.Start();b.Start();go.Set();
                Check.True(a.Join(5000)&&b.Join(5000));Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);
                release.Set();Exit(writer);Check.Equal(ViveSessionSyntheticWriterState.Uncertain,writer.State);Check.Equal(0,target.FlushCount);
            });
            suite.Run("withdrawal after acknowledged close does not reopen or alter output",()=>{
                var target=new ViveSessionSyntheticTarget();using(var header=new HeaderPausedWriter(target,4,32768))
                {
                var writer=header.Writer;Check.True(writer.Finish());header.Release();Exit(writer);Check.Equal(ViveSessionSyntheticWriterState.Closed,writer.State);
                byte[] before=target.CopyBytes();writer.Withdraw();byte[] after=target.CopyBytes();
                Check.Equal(ViveSessionSyntheticWriterState.Closed,writer.State);Check.Equal(BitConverter.ToString(before),BitConverter.ToString(after));
                }
            });
            suite.Run("withdrawal wins after dispose acknowledgement but before final completion",()=>{
                var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);var target=new ViveSessionSyntheticTarget();
                using(var header=new HeaderPausedWriter(target,4,32768))
                {
                var writer=header.Writer;
                writer.BeforeCompletionForTest=()=>{entered.Set();if(!release.WaitOne(5000))throw new IOException("fixture timeout");};
                Check.True(writer.Finish());header.Release();Check.True(entered.WaitOne(5000));Check.True(!writer.HasReservedIo);
                writer.Withdraw();Check.Equal(ViveSessionSyntheticWriterState.Revoked,writer.State);release.Set();Exit(writer);
                Check.Equal(ViveSessionSyntheticWriterState.Revoked,writer.State);Check.Equal(1,target.DisposeCount);
                var frames=Decode(target.CopyBytes());Check.Equal("close",frames[frames.Count-1].Kind);
                }
            });
            suite.Run("synthetic output byte limit preserves an unclean bounded prefix",()=>{
                var target=new ViveSessionSyntheticTarget();using(var header=new HeaderPausedWriter(target,4,32768,256))
                {
                var writer=header.Writer;Check.True(writer.TryEnqueue(Record("one")));Check.True(writer.Finish());header.Release();Exit(writer);
                Check.Equal(ViveSessionSyntheticWriterState.Faulted,writer.State);Check.True(target.CopyBytes().Length<=256);
                var frames=Decode(target.CopyBytes());Check.True(frames.Count>=1&&frames[frames.Count-1].Kind!="close");
                Check.Equal(0,target.FlushCount);Check.Equal(0,target.DisposeCount);
                }
            });
        }
    }
}
