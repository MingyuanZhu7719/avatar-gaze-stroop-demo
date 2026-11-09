using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Only constructed records can enter the queue. Arrays and fields are
    // copied before enqueue; no caller-owned dictionary/iterator reaches the
    // worker. Values are caller-declared synthetic fixtures; this type cannot
    // authenticate their origin and is never consent or source proof.
    internal sealed class ViveSessionSyntheticRecord
    {
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        internal readonly string Kind;
        internal readonly IReadOnlyDictionary<string,string> Fields;
        internal readonly int EstimatedBytes;
        internal ViveSessionSyntheticRecord(string kind,string[] keys,string[] values)
        {
            if(kind!="task_event"&&kind!="eye_sample"&&kind!="coverage_unknown")throw new ArgumentException("Unsupported synthetic record kind.");
            if(keys==null||values==null||keys.Length<1||keys.Length>16||keys.Length!=values.Length)throw new ArgumentException("Bounded fields required.");
            var copied=new Dictionary<string,string>(StringComparer.Ordinal);int size=0;
            for(int i=0;i<keys.Length;i++)
            {
                string key=keys[i],value=values[i];
                if(String.IsNullOrEmpty(key)||key.Length>64||(value!=null&&value.Length>1024))throw new ArgumentException("Invalid field bound.");
                foreach(char c in key)if(!((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_'))throw new ArgumentException("Invalid field key.");
                if(copied.ContainsKey(key))throw new ArgumentException("Duplicate field key.");
                size=checked(size+Utf8.GetByteCount(key)+(value==null?0:Utf8.GetByteCount(value))+8);
                if(size>8192)throw new ArgumentException("Synthetic field bytes exceed bound.");
                copied.Add(key,value);
            }
            Kind=kind;Fields=new ReadOnlyDictionary<string,string>(copied);EstimatedBytes=checked(size+128);
        }
    }
    internal enum ViveSessionSyntheticWriterState { Running,FinishRequested,Closed,Revoked,Faulted,Uncertain }

    // The only factory/handle accepted by the candidate is this sealed fake.
    // Hooks are test-only and run outside writer locks. They cannot attach a
    // path, real Stream, hardware provider or production storage. Its internal
    // test delegate is caller code and is not sandboxed by this type.
    internal sealed class ViveSessionSyntheticTarget
    {
        readonly object gate=new object();readonly MemoryStream bytes=new MemoryStream();bool opened;
        internal Action<string> BeforeOperation {get;set;}
        internal int OpenThread,WriteThread,FlushThread,DisposeThread;
        internal int OpenCount,WriteCount,FlushCount,DisposeCount;
        void Hook(string op){var hook=BeforeOperation;if(hook!=null)hook(op);}
        internal Stream Open()
        {
            Hook("open");lock(gate){if(opened)throw new InvalidOperationException("Synthetic target already opened.");opened=true;OpenCount++;OpenThread=Thread.CurrentThread.ManagedThreadId;return new Handle(this);}
        }
        internal byte[] CopyBytes(){lock(gate)return bytes.ToArray();}
        sealed class Handle:Stream
        {
            readonly ViveSessionSyntheticTarget target;bool closed;
            internal Handle(ViveSessionSyntheticTarget target){this.target=target;}
            public override bool CanRead {get{return false;}} public override bool CanSeek {get{return false;}} public override bool CanWrite {get{return !closed;}}
            public override long Length {get{throw new NotSupportedException();}} public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int c){throw new NotSupportedException();} public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long v){throw new NotSupportedException();}
            public override void Write(byte[] b,int o,int c)
            {target.Hook("write");lock(target.gate){if(closed)throw new ObjectDisposedException("synthetic");target.WriteCount++;target.WriteThread=Thread.CurrentThread.ManagedThreadId;target.bytes.Write(b,o,c);}}
            public override void Flush(){target.Hook("flush");lock(target.gate){if(closed)throw new ObjectDisposedException("synthetic");target.FlushCount++;target.FlushThread=Thread.CurrentThread.ManagedThreadId;}}
            protected override void Dispose(bool disposing)
            {if(disposing){target.Hook("dispose");lock(target.gate){if(!closed){closed=true;target.DisposeCount++;target.DisposeThread=Thread.CurrentThread.ManagedThreadId;}}}base.Dispose(disposing);}
        }
    }

    // Synthetic in-memory candidate only: this class cannot open a real file or
    // Stream, establish consent, accept physical frames or admit an experiment.
    // A reserved operation may physically execute after Withdraw; its outcome is
    // uncertain, even if the resulting bytes happen to contain a close record.
    internal sealed class ViveSessionJournalAsyncWriter
    {
        static readonly byte[] Magic=Encoding.ASCII.GetBytes("VSESS004");
        readonly object gate=new object();Queue<ViveSessionSyntheticRecord> queue=new Queue<ViveSessionSyntheticRecord>();
        readonly AutoResetEvent wake=new AutoResetEvent(false);readonly ViveSessionSyntheticTarget target;
        readonly int owner,maxCount,maxBytes;readonly long maximumOutputBytes;readonly byte[] token=Guid.NewGuid().ToByteArray();readonly Thread thread;
        // ioState is the single linearization point: 0 idle, 1 reserved,
        // 2 revoked, 3 completed, 4 faulted, 5 uncertain.
        int queuedBytes,status,ioState;
        long accepted,committed;
        // Bounded test hook only, never under gate and never on the owner thread.
        internal Action BeforeCompletionForTest {get;set;}
        internal ViveSessionJournalAsyncWriter(ViveSessionSyntheticTarget target,int maximumCount,int maximumBytes,long maximumOutputBytes=67108864)
        {
            if(target==null)throw new ArgumentNullException("target");
            if(maximumCount<1||maximumCount>4096||maximumBytes<256||maximumBytes>1048576||maximumOutputBytes<256||maximumOutputBytes>67108864)
                throw new ArgumentOutOfRangeException("bounded queue and synthetic output required");
            this.target=target;maxCount=maximumCount;maxBytes=maximumBytes;this.maximumOutputBytes=maximumOutputBytes;owner=Thread.CurrentThread.ManagedThreadId;
            thread=new Thread(Run){IsBackground=true,Name="VIVE synthetic session writer"};thread.Start();
        }
        internal ViveSessionSyntheticWriterState State
        {
            get
            {
                int io=Volatile.Read(ref ioState);
                if(io<2)
                {
                    int phase=Volatile.Read(ref status);
                    io=Volatile.Read(ref ioState);
                    if(io<2)return (ViveSessionSyntheticWriterState)phase;
                }
                if(io==2)return ViveSessionSyntheticWriterState.Revoked;
                if(io==3)return ViveSessionSyntheticWriterState.Closed;
                if(io==4)return ViveSessionSyntheticWriterState.Faulted;
                if(io==5)return ViveSessionSyntheticWriterState.Uncertain;
                return ViveSessionSyntheticWriterState.Faulted;
            }
        }
        internal long Accepted {get{return Interlocked.Read(ref accepted);}}
        internal long Committed {get{return Interlocked.Read(ref committed);}}
        internal bool HasExited {get{return !thread.IsAlive;}}
        internal bool HasReservedIo {get{return Volatile.Read(ref ioState)==1;}}
        internal object QueueGateForTest {get{return gate;}}
        internal bool QueueDetachedForTest {get{return Volatile.Read(ref queue)==null;}}
        internal bool ProductionGateOpen {get{return false;}} internal bool ConsentAuthenticated {get{return false;}}
        internal bool HardwareAuthenticated {get{return false;}} internal bool DurableStorageProven {get{return false;}}
        void Signal(){wake.Set();}
        void Terminate(bool withdraw)
        {
            while(true)
            {
                int previous=Volatile.Read(ref ioState);
                if(previous>=2)break;
                int terminal=previous==1?5:withdraw?2:4;
                if(Interlocked.CompareExchange(ref ioState,terminal,previous)==previous)break;
            }
            // Detach first without waiting for a caller that owns gate or for a
            // blocked reserved I/O call. An in-flight caller may temporarily
            // retain its local queue/record reference; it cannot start new I/O.
            var detached=Interlocked.Exchange(ref queue,null);
            if(Monitor.TryEnter(gate))try{if(detached!=null)detached.Clear();queuedBytes=0;}finally{Monitor.Exit(gate);}
            Signal();
        }
        internal bool TryEnqueue(ViveSessionSyntheticRecord record)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||record==null){Terminate(false);return false;}
            if(!Monitor.TryEnter(gate)){Terminate(false);return false;}
            try
            {
                var current=Volatile.Read(ref queue);
                if(current==null)return false;
                if(Volatile.Read(ref ioState)>=2){current.Clear();queuedBytes=0;return false;}
                if(State!=ViveSessionSyntheticWriterState.Running)
                {if(Volatile.Read(ref ioState)>=2){current.Clear();queuedBytes=0;}return false;}
                if(current.Count>=maxCount||record.EstimatedBytes>maxBytes-queuedBytes||Interlocked.Read(ref accepted)==Int64.MaxValue){Terminate(false);return false;}
                current.Enqueue(record);queuedBytes+=record.EstimatedBytes;Interlocked.Increment(ref accepted);
                if(Volatile.Read(ref ioState)>=2){current.Clear();queuedBytes=0;return false;}
            }
            finally{Monitor.Exit(gate);}
            Signal();return true;
        }
        internal bool Finish()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner){Terminate(false);return false;}
            if(!Monitor.TryEnter(gate)){Terminate(false);return false;}
            try
            {
                if(Volatile.Read(ref ioState)>=2||State!=ViveSessionSyntheticWriterState.Running)return false;
                if(Interlocked.CompareExchange(ref status,(int)ViveSessionSyntheticWriterState.FinishRequested,(int)ViveSessionSyntheticWriterState.Running)!=(int)ViveSessionSyntheticWriterState.Running)return false;
                if(Volatile.Read(ref ioState)>=2)return false;
            }
            finally{Monitor.Exit(gate);}
            Signal();return true;
        }
        // Safe from any thread, never waits for the queue lock or a Stream call.
        internal void Withdraw(){Terminate(true);}
        bool Reserve(){return Interlocked.CompareExchange(ref ioState,1,0)==0;}
        bool Acknowledge(){return Interlocked.CompareExchange(ref ioState,0,1)==1;}
        bool Operation(Action action)
        {
            if(!Reserve())return false;
            try{action();}
            catch(Exception){Terminate(false);return false;}
            return Acknowledge();
        }
        void Run()
        {
            Stream stream=null;byte[] previous=new byte[32];long ordinal=0,outputBytes=0;
            try
            {
                if(!Operation(()=>stream=target.Open()))return;
                var header=new Dictionary<string,string>{{"schema","vive-session-journal-v4"},{"data_scope","SYNTHETIC_ONLY"},{"identity_authority","DECLARED_ONLY"}};
                byte[] hash;byte[] frame=Frame(ordinal,"header",header,previous,out hash,true);
                if(frame.Length>maximumOutputBytes){Terminate(false);return;}
                if(!Operation(()=>stream.Write(frame,0,frame.Length)))return;outputBytes+=frame.Length;previous=hash;ordinal++;
                while(true)
                {
                    ViveSessionSyntheticRecord next=null;bool finish=false;
                    lock(gate)
                    {
                        var current=Volatile.Read(ref queue);
                        if(current==null||Volatile.Read(ref ioState)>=2){if(current!=null)current.Clear();queuedBytes=0;return;}
                        if(current.Count>0){next=current.Dequeue();queuedBytes-=next.EstimatedBytes;}
                        else finish=State==ViveSessionSyntheticWriterState.FinishRequested;
                    }
                    if(next!=null)
                    {
                        if(ordinal==Int64.MaxValue){Terminate(false);return;}
                        frame=Frame(ordinal,next.Kind,next.Fields,previous,out hash,false);
                        if(frame.Length>maximumOutputBytes-outputBytes){Terminate(false);return;}
                        if(!Operation(()=>stream.Write(frame,0,frame.Length)))return;
                        outputBytes+=frame.Length;previous=hash;ordinal++;Interlocked.Increment(ref committed);continue;
                    }
                    if(finish)
                    {
                        if(ordinal==Int64.MaxValue||Committed!=Accepted){Terminate(false);return;}
                        var close=new Dictionary<string,string>{{"record_count",Committed.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
                        frame=Frame(ordinal,"close",close,previous,out hash,false);
                        if(frame.Length>maximumOutputBytes-outputBytes){Terminate(false);return;}
                        if(!Operation(()=>stream.Write(frame,0,frame.Length))||!Operation(()=>stream.Flush())||!Operation(()=>stream.Dispose()))return;
                        if(BeforeCompletionForTest!=null)BeforeCompletionForTest();
                        // Completion and withdrawal compete on the same word.
                        // A completed CAS cannot override an earlier stop.
                        Interlocked.CompareExchange(ref ioState,3,0);
                        return;
                    }
                    wake.WaitOne();
                }
            }
            catch(Exception){Terminate(false);}
            // Do not dispose after withdrawal/failure: real Stream disposal may
            // flush implicitly. This fake-only object retains any unclean prefix.
        }
        byte[] Frame(long ordinal,string kind,IReadOnlyDictionary<string,string> fields,byte[] previous,out byte[] digest,bool magic)
        {
            byte[] payload=ContinuousJournal.Payload(ordinal,token,kind,fields);digest=ContinuousJournal.Digest(previous,payload);
            int start=magic?Magic.Length:0;var result=new byte[start+8+payload.Length+digest.Length];
            if(magic)Buffer.BlockCopy(Magic,0,result,0,Magic.Length);
            ContinuousJournal.PutInt(result,payload.Length,start);ContinuousJournal.PutInt(result,~payload.Length,start+4);
            Buffer.BlockCopy(payload,0,result,start+8,payload.Length);Buffer.BlockCopy(digest,0,result,start+8+payload.Length,digest.Length);return result;
        }
    }
}
