using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic, in-memory codec tests only. Complete means structurally
    // committed, not real user consent, authentic hardware, or durability.
    public static class VivePracticeStartEnvelopeTests
    {
        const int Max=256*1024;
        sealed class Clock:IClock {public double Time=10;public string ClockId{get{return "envelope-clock";}}public double Now{get{return Time;}}}
        sealed class Fixture
        {
            public readonly VivePracticeStartBatch Batch;public readonly VivePracticeContextV3 Context;
            public Fixture()
            {
                var c=new Clock();var config=new SessionConfiguration("envelope-session",42,
                    new Allocation("envelope-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"envelope-geometry");
                var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                var task=new ViveDevelopmentParticipant(config,c,geometry,mapping,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));c.Time=11;task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBatch batch;Check.True(VivePracticeStartBatchExtractor.TryExtract(task,c,1,4096,out batch));Batch=batch;
                Context=new VivePracticeContextV3(task.SessionId,"envelope-participant","envelope-stream","envelope-device",c.ClockId,
                    "envelope-map",1,"envelope-epoch",0,null,VivePracticeEpochReasonV3.Initial,batch.Events[0].Time);
            }
        }
        sealed class ShortReadStream:Stream
        {
            readonly byte[] data;readonly int chunk,throwAt,throwType;readonly bool failCanRead;int offset;public bool Disposed;
            public ShortReadStream(byte[] bytes,int maxChunk,int failAt=-1,int failureType=0,bool failReadable=false)
            {data=bytes;chunk=maxChunk;throwAt=failAt;throwType=failureType;failCanRead=failReadable;}
            void Fail(){if(throwType==1)throw new InvalidOperationException("synthetic failure");if(throwType==2)throw new ObjectDisposedException("synthetic");throw new IOException("synthetic failure");}
            public override int Read(byte[] buffer,int off,int count)
            {
                if(throwAt>=0&&offset>=throwAt)Fail();
                int n=Math.Min(Math.Min(count,chunk),data.Length-offset);if(throwAt>=0)n=Math.Min(n,throwAt-offset);
                if(n<=0)return 0;Buffer.BlockCopy(data,offset,buffer,off,n);offset+=n;return n;
            }
            protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
            public override bool CanRead{get{if(failCanRead)Fail();return true;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return false;}}
            public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long off,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] b,int off,int n){throw new NotSupportedException();}
        }
        static byte[] Join(byte[] a,byte[] b)
        {var result=new byte[a.Length+b.Length];Buffer.BlockCopy(a,0,result,0,a.Length);Buffer.BlockCopy(b,0,result,a.Length,b.Length);return result;}
        static VivePracticeStartEnvelopeReadResult Decode(byte[] data,int limit=Max)
        {using(var input=new MemoryStream(data,false))return VivePracticeStartEnvelopeCodec.Decode(input,limit);}
        static void NoEvents(VivePracticeStartEnvelopeReadResult result)
        {Check.True(result!=null&&result.Status.ToString()!="Complete");Check.True(result.Events==null||result.Events.Count==0);}
        static void SameEvent(JournalEvent expected,JournalEvent actual)
        {
            Check.Equal(expected.Sequence,actual.Sequence);Check.Equal(BitConverter.DoubleToInt64Bits(expected.Time),BitConverter.DoubleToInt64Bits(actual.Time));
            Check.Equal(expected.ClockId,actual.ClockId);Check.Equal(expected.SessionId,actual.SessionId);Check.Equal(expected.BlockIndex,actual.BlockIndex);
            Check.Equal(expected.Segment,actual.Segment);Check.Equal(expected.TrialId,actual.TrialId);Check.Equal(expected.Kind,actual.Kind);
            Check.Equal(expected.Fields.Count,actual.Fields.Count);foreach(var field in expected.Fields)Check.Equal(field.Value,actual.Fields[field.Key]);
        }
        static void SameContext(VivePracticeContextV3 expected,VivePracticeContextV3 actual)
        {
            Check.True(actual!=null&&!actual.IdentityAuthenticated);Check.Equal(expected.SessionId,actual.SessionId);Check.Equal(expected.ParticipantId,actual.ParticipantId);
            Check.Equal(expected.StreamId,actual.StreamId);Check.Equal(expected.DeviceId,actual.DeviceId);Check.Equal(expected.ClockId,actual.ClockId);
            Check.Equal(expected.MappingVersion,actual.MappingVersion);Check.Equal(expected.PracticeAttempt,actual.PracticeAttempt);
            Check.Equal(expected.RecordingEpochId,actual.RecordingEpochId);Check.Equal(expected.RecoveryOrdinal,actual.RecoveryOrdinal);
            Check.Equal(expected.PreviousEpochId,actual.PreviousEpochId);Check.Equal(expected.EpochReason,actual.EpochReason);
            Check.Equal(BitConverter.DoubleToInt64Bits(expected.ObservedPracticeStartLocalSeconds.Value),BitConverter.DoubleToInt64Bits(actual.ObservedPracticeStartLocalSeconds.Value));
        }
        static bool Throws(Action action){try{action();return false;}catch(ArgumentException){return true;}catch(InvalidOperationException){return true;}}
        static byte[] Hash(string domain,byte[] value,int offset,int count)
        {
            var d=Encoding.ASCII.GetBytes(domain);using(var sha=SHA256.Create())
            {sha.TransformBlock(d,0,d.Length,d,0);sha.TransformFinalBlock(value,offset,count);return sha.Hash;}
        }
        static JournalEvent Replace(JournalEvent item,long sequence,double time,string session,string kind,IDictionary<string,string> fields)
        {return new JournalEvent(sequence,time,item.ClockId,session,item.BlockIndex,item.Segment,item.TrialId,kind,fields);}
        public static void Register(TestSuite suite)
        {
            suite.Run("start envelope round-trips the actual three observed events only on commit",()=>{
                var f=new Fixture();var envelope=VivePracticeStartEnvelopeCodec.Encode(f.Context,f.Batch,Max);
                var all=Join(envelope.StageBytes,envelope.CommitBytes);var result=Decode(all);
                Check.Equal("Complete",result.Status.ToString());Check.Equal(3,result.Events.Count);Check.Equal(f.Batch.Revision,result.Revision);
                SameContext(f.Context,result.Context);for(int i=0;i<3;i++)SameEvent(f.Batch.Events[i],result.Events[i]);
                Check.Equal((long)all.Length,result.VerifiedBytes);
            });
            suite.Run("start envelope all truncated prefixes expose no staged event",()=>{
                var f=new Fixture();var e=VivePracticeStartEnvelopeCodec.Encode(f.Context,f.Batch,Max);var all=Join(e.StageBytes,e.CommitBytes);
                for(int n=0;n<all.Length;n++)
                {
                    var prefix=new byte[n];Buffer.BlockCopy(all,0,prefix,0,n);var result=Decode(prefix);
                    NoEvents(result);Check.True(result.Context==null);Check.True(result.VerifiedBytes<=n);
                }
            });
            suite.Run("start envelope rejects corrupted stage and commit and any trailing byte",()=>{
                var f=new Fixture();var e=VivePracticeStartEnvelopeCodec.Encode(f.Context,f.Batch,Max);var stage=e.StageBytes;var commit=e.CommitBytes;
                var complete=Join(stage,commit);foreach(int index in new[]{0,stage.Length/2,stage.Length-1,stage.Length,complete.Length-1})
                {var bad=(byte[])complete.Clone();bad[index]^=0x01;NoEvents(Decode(bad));}
                var trailing=new byte[complete.Length+1];Buffer.BlockCopy(complete,0,trailing,0,complete.Length);trailing[trailing.Length-1]=1;
                NoEvents(Decode(trailing));NoEvents(Decode(commit));
            });
            suite.Run("start envelope commit is bound to the exact stage and declared epoch",()=>{
                var f=new Fixture();var a=VivePracticeStartEnvelopeCodec.Encode(f.Context,f.Batch,Max);
                var other=new VivePracticeContextV3(f.Context.SessionId,f.Context.ParticipantId,f.Context.StreamId,f.Context.DeviceId,f.Context.ClockId,
                    f.Context.MappingVersion,1,"other-epoch",0,null,VivePracticeEpochReasonV3.Initial,f.Context.ObservedPracticeStartLocalSeconds);
                var b=VivePracticeStartEnvelopeCodec.Encode(other,f.Batch,Max);
                NoEvents(Decode(Join(a.StageBytes,b.CommitBytes)));NoEvents(Decode(Join(b.StageBytes,a.CommitBytes)));
            });
            suite.Run("start envelope refuses rehashed semantic corruption",()=>{
                var f=new Fixture();var e=VivePracticeStartEnvelopeCodec.Encode(f.Context,f.Batch,Max);var stage=e.StageBytes;var commit=e.CommitBytes;
                var needle=Encoding.UTF8.GetBytes("Familiarization");int found=-1;
                for(int i=16;i<=stage.Length-32-needle.Length;i++)
                {bool same=true;for(int j=0;j<needle.Length;j++)if(stage[i+j]!=needle[j]){same=false;break;}if(same){found=i;break;}}
                Check.True(found>=0);stage[found]=(byte)'X';int payloadLength=stage.Length-48;
                var stageHash=Hash("VIVE-PRACTICE-START-STAGE-1\0",stage,16,payloadLength);Buffer.BlockCopy(stageHash,0,stage,stage.Length-32,32);
                var commitHash=Hash("VIVE-PRACTICE-START-COMMIT-1\0",stage,0,stage.Length);Buffer.BlockCopy(commitHash,0,commit,8,32);
                var result=Decode(Join(stage,commit));NoEvents(result);Check.Equal("Invalid",result.Status.ToString());
            });
            suite.Run("start envelope preserves UTF8 null and exact event time bits",()=>{
                var f=new Fixture();var events=new List<JournalEvent>();
                for(int i=0;i<3;i++)
                {
                    var item=f.Batch.Events[i];var fields=new Dictionary<string,string>();foreach(var pair in item.Fields)fields.Add(pair.Key,pair.Value);
                    fields.Add("extra-α","测试\u00e9");fields.Add("nullable",null);
                    double time=BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(item.Time)+i);
                    events.Add(new JournalEvent(item.Sequence,time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,item.TrialId,item.Kind,fields));
                }
                var copy=new VivePracticeStartBatch(f.Batch.SessionId,f.Batch.ClockId,f.Batch.Attempt,f.Batch.Revision,events);
                var e=VivePracticeStartEnvelopeCodec.Encode(f.Context,copy,Max);var result=Decode(Join(e.StageBytes,e.CommitBytes));
                Check.Equal("Complete",result.Status.ToString());Check.Equal(3,result.Events.Count);
                for(int i=0;i<3;i++)SameEvent(events[i],result.Events[i]);
            });
            suite.Run("start envelope prefix decoder respects bounds and reports exact consumption",()=>{
                var f=new Fixture();var e=VivePracticeStartEnvelopeCodec.Encode(f.Context,f.Batch,Max);var all=Join(e.StageBytes,e.CommitBytes);
                var container=new byte[7+all.Length+5];for(int i=0;i<container.Length;i++)container[i]=0x77;Buffer.BlockCopy(all,0,container,7,all.Length);
                var result=VivePracticeStartEnvelopeCodec.DecodePrefix(container,7,all.Length+5,Max);
                Check.Equal("Complete",result.Status.ToString());Check.Equal(all.Length,result.ConsumedBytes);
                Check.Equal(3,result.Events.Count);for(int i=0;i<3;i++)SameEvent(f.Batch.Events[i],result.Events[i]);
                var shortened=VivePracticeStartEnvelopeCodec.DecodePrefix(container,7,all.Length-1,Max);NoEvents(shortened);
                var includesTail=new byte[all.Length+5];Buffer.BlockCopy(container,7,includesTail,0,includesTail.Length);NoEvents(Decode(includesTail));
            });
            suite.Run("start envelope immutable byte access cannot change encoded payload",()=>{
                var f=new Fixture();var e=VivePracticeStartEnvelopeCodec.Encode(f.Context,f.Batch,Max);
                var stage=e.StageBytes;var commit=e.CommitBytes;byte stageFirst=stage[0],commitFirst=commit[0];stage[0]^=0x55;commit[0]^=0x55;
                Check.Equal(stageFirst,e.StageBytes[0]);Check.Equal(commitFirst,e.CommitBytes[0]);Check.Equal("Complete",Decode(Join(e.StageBytes,e.CommitBytes)).Status.ToString());
            });
            suite.Run("start envelope enforces bounded encode and read",()=>{
                var f=new Fixture();var e=VivePracticeStartEnvelopeCodec.Encode(f.Context,f.Batch,Max);var bytes=Join(e.StageBytes,e.CommitBytes);
                Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,f.Batch,1)));
                var result=Decode(bytes,bytes.Length-1);NoEvents(result);Check.Equal("LimitExceeded",result.Status.ToString());
            });
            suite.Run("start envelope tolerates short reads and preserves caller stream ownership",()=>{
                var f=new Fixture();var e=VivePracticeStartEnvelopeCodec.Encode(f.Context,f.Batch,Max);var input=new ShortReadStream(Join(e.StageBytes,e.CommitBytes),3);
                var result=VivePracticeStartEnvelopeCodec.Decode(input,Max);Check.Equal("Complete",result.Status.ToString());
                Check.True(!input.Disposed);Check.Equal(3,result.Events.Count);input.Dispose();
            });
            suite.Run("start envelope reports stream failures without exposing staged events",()=>{
                var f=new Fixture();var e=VivePracticeStartEnvelopeCodec.Encode(f.Context,f.Batch,Max);var data=Join(e.StageBytes,e.CommitBytes);
                foreach(int fail in new[]{0,1,e.StageBytes.Length/2,e.StageBytes.Length,e.StageBytes.Length+1})
                {var input=new ShortReadStream(data,7,fail);var result=VivePracticeStartEnvelopeCodec.Decode(input,Max);
                    Check.Equal("ReadFailure",result.Status.ToString());NoEvents(result);Check.True(!input.Disposed);input.Dispose();}
                for(int kind=1;kind<=2;kind++)
                {
                    var duringRead=new ShortReadStream(data,7,e.StageBytes.Length,kind);var a=VivePracticeStartEnvelopeCodec.Decode(duringRead,Max);
                    Check.Equal("ReadFailure",a.Status.ToString());NoEvents(a);Check.True(!duringRead.Disposed);
                    var canRead=new ShortReadStream(data,7,-1,kind,true);var b=VivePracticeStartEnvelopeCodec.Decode(canRead,Max);
                    Check.Equal("ReadFailure",b.Status.ToString());NoEvents(b);Check.True(!canRead.Disposed);
                }
            });
            suite.Run("start envelope rejects contexts that do not match actual batch",()=>{
                var f=new Fixture();var x=f.Context;
                var wrongSession=new VivePracticeContextV3("other-session",x.ParticipantId,x.StreamId,x.DeviceId,x.ClockId,x.MappingVersion,1,"other-epoch",0,null,VivePracticeEpochReasonV3.Initial,x.ObservedPracticeStartLocalSeconds);
                var wrongClock=new VivePracticeContextV3(x.SessionId,x.ParticipantId,x.StreamId,x.DeviceId,"other-clock",x.MappingVersion,1,"other-epoch",0,null,VivePracticeEpochReasonV3.Initial,x.ObservedPracticeStartLocalSeconds);
                var wrongTime=new VivePracticeContextV3(x.SessionId,x.ParticipantId,x.StreamId,x.DeviceId,x.ClockId,x.MappingVersion,1,"other-epoch",0,null,VivePracticeEpochReasonV3.Initial,x.ObservedPracticeStartLocalSeconds.Value+1);
                Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(wrongSession,f.Batch,Max)));
                Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(wrongClock,f.Batch,Max)));
                Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(wrongTime,f.Batch,Max)));
            });
            suite.Run("start envelope refuses malformed caller-declared batch shape",()=>{
                var f=new Fixture();var items=new List<JournalEvent>(f.Batch.Events);var first=items[0];var second=items[1];var third=items[2];
                Func<IReadOnlyList<JournalEvent>,VivePracticeStartBatch> batch=x=>new VivePracticeStartBatch(f.Batch.SessionId,f.Batch.ClockId,f.Batch.Attempt,f.Batch.Revision,x);
                Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,batch(new[]{first,second}),Max)));
                var fields=new Dictionary<string,string>();foreach(var pair in second.Fields)fields.Add(pair.Key,pair.Value);
                var duplicate=Replace(second,first.Sequence,second.Time,second.SessionId,second.Kind,fields);
                Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,batch(new[]{first,duplicate,third}),Max)));
                var rollback=Replace(second,second.Sequence,first.Time-1,second.SessionId,second.Kind,fields);
                Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,batch(new[]{first,rollback,third}),Max)));
                var invalidTime=Replace(second,second.Sequence,Double.NaN,second.SessionId,second.Kind,fields);
                Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,batch(new[]{first,invalidTime,third}),Max)));
                var wrongSession=Replace(second,second.Sequence,second.Time,"other-session",second.Kind,fields);
                Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,batch(new[]{first,wrongSession,third}),Max)));
                var wrongKind=Replace(second,second.Sequence,second.Time,second.SessionId,"wrong_kind",fields);
                Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,batch(new[]{first,wrongKind,third}),Max)));
                fields["to"]="Ended";var wrongPhase=Replace(second,second.Sequence,second.Time,second.SessionId,second.Kind,fields);
                Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,batch(new[]{first,wrongPhase,third}),Max)));
            });
            suite.Run("start envelope bounds UTF16 input UTF8 byte lengths and field count",()=>{
                var f=new Fixture();var original=f.Batch.Events[1];
                Func<IDictionary<string,string>,VivePracticeStartBatch> batch=values=>new VivePracticeStartBatch(f.Batch.SessionId,f.Batch.ClockId,f.Batch.Attempt,f.Batch.Revision,
                    new[]{f.Batch.Events[0],Replace(original,original.Sequence,original.Time,original.SessionId,original.Kind,values),f.Batch.Events[2]});
                Func<Dictionary<string,string>> fields=()=>{var result=new Dictionary<string,string>();foreach(var pair in original.Fields)result.Add(pair.Key,pair.Value);return result;};
                var tooLong=fields();tooLong.Add("extra",new string('a',4097));Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,batch(tooLong),Max)));
                var utf8TooLong=fields();utf8TooLong.Add("extra",new string('中',2000));Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,batch(utf8TooLong),Max)));
                var invalidUtf16=fields();invalidUtf16.Add("extra","\uD800");Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,batch(invalidUtf16),Max)));
                var many=fields();for(int i=0;i<65;i++)many.Add("extra-"+i,"v");Check.True(Throws(()=>VivePracticeStartEnvelopeCodec.Encode(f.Context,batch(many),Max)));
            });
        }
    }
}
