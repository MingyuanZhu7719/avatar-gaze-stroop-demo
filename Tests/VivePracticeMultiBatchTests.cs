using System;
using System.Collections.Generic;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // In-memory/simulation only; hashes establish structure, not provenance,
    // actual consent, authenticity, device capture time or durable storage.
    public static class VivePracticeMultiBatchTests
    {
        const int Max=256*1024, Batches=64;
        static readonly Dictionary<VivePracticeMultiWriter,VivePracticeContextV3> WriterContexts=new Dictionary<VivePracticeMultiWriter,VivePracticeContextV3>();
        sealed class Clock:IClock {public double Time=10;public string ClockId{get{return "multi-clock";}}public double Now{get{return Time;}}}
        sealed class Fixture
        {
            internal readonly VivePracticeStartBatch Batch;internal readonly VivePracticeContextV3 Context;
            internal readonly VivePracticeStartEnvelope Start;
            internal Fixture(string session="multi-session",string epoch="multi-epoch")
            {
                var clock=new Clock();var config=new SessionConfiguration(session,42,
                    new Allocation("multi-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"multi-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                var task=new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));clock.Time=11;task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBatch batch;Check.True(VivePracticeStartBatchExtractor.TryExtract(task,clock,1,4096,out batch));Batch=batch;
                Context=new VivePracticeContextV3(session,"multi-participant","multi-stream","multi-device",clock.ClockId,"multi-map",
                    1,epoch,0,null,VivePracticeEpochReasonV3.Initial,batch.Events[0].Time);
                Start=VivePracticeStartEnvelopeCodec.Encode(Context,Batch,Max);
            }
            internal JournalEvent Task(int increment=1,double extraTime=0,string session=null,string clock=null,int? segment=null,
                string kind="stimulus_presented",IDictionary<string,string> fields=null)
            {
                var last=Batch.Events[2];
                return new JournalEvent(last.Sequence+increment,last.Time+extraTime,clock??last.ClockId,session??last.SessionId,
                    last.BlockIndex,segment??last.Segment,last.TrialId,kind,fields??new Dictionary<string,string>());
            }
            internal VivePracticeMultiWriter Writer(){var writer=new VivePracticeMultiWriter(Start,Max,Batches);WriterContexts.Add(writer,Context);return writer;}
        }
        static VivePracticeMultiReadResult Read(byte[] bytes,bool recover=true,int maxBytes=Max,int maxBatches=Batches)
        {return VivePracticeMultiReader.Read(bytes,recover,maxBytes,maxBatches);}
        static byte[] Prefix(byte[] data,int n){var p=new byte[n];Buffer.BlockCopy(data,0,p,0,n);return p;}
        static byte[] Append(byte[] a,byte[] b){var p=new byte[a.Length+b.Length];Buffer.BlockCopy(a,0,p,0,a.Length);Buffer.BlockCopy(b,0,p,a.Length,b.Length);return p;}
        static bool Append(VivePracticeMultiWriter writer,params JournalEvent[] events){return writer.AppendTaskTransaction(WriterContexts[writer],events);}
        static Dictionary<string,string> Completion(string attempt="1",string correct="8",string planned="8")
        {return new Dictionary<string,string>{{"attempt",attempt},{"correct_first",correct},{"planned",planned}};}
        static VivePracticeContextV3[] OtherContexts(VivePracticeContextV3 c)
        {
            return new[]{
                new VivePracticeContextV3(c.SessionId,"other-participant",c.StreamId,c.DeviceId,c.ClockId,c.MappingVersion,1,c.RecordingEpochId,0,null,VivePracticeEpochReasonV3.Initial,c.ObservedPracticeStartLocalSeconds),
                new VivePracticeContextV3(c.SessionId,c.ParticipantId,"other-stream",c.DeviceId,c.ClockId,c.MappingVersion,1,c.RecordingEpochId,0,null,VivePracticeEpochReasonV3.Initial,c.ObservedPracticeStartLocalSeconds),
                new VivePracticeContextV3(c.SessionId,c.ParticipantId,c.StreamId,"other-device",c.ClockId,c.MappingVersion,1,c.RecordingEpochId,0,null,VivePracticeEpochReasonV3.Initial,c.ObservedPracticeStartLocalSeconds),
                new VivePracticeContextV3(c.SessionId,c.ParticipantId,c.StreamId,c.DeviceId,c.ClockId,"other-map",1,c.RecordingEpochId,0,null,VivePracticeEpochReasonV3.Initial,c.ObservedPracticeStartLocalSeconds),
                new VivePracticeContextV3(c.SessionId,c.ParticipantId,c.StreamId,c.DeviceId,c.ClockId,c.MappingVersion,1,"other-epoch",0,null,VivePracticeEpochReasonV3.Initial,c.ObservedPracticeStartLocalSeconds),
                new VivePracticeContextV3(c.SessionId,c.ParticipantId,c.StreamId,c.DeviceId,c.ClockId,c.MappingVersion,1,c.RecordingEpochId,0,null,VivePracticeEpochReasonV3.Initial,c.ObservedPracticeStartLocalSeconds.Value+0.125),
                new VivePracticeContextV3(c.SessionId,c.ParticipantId,c.StreamId,c.DeviceId,c.ClockId,c.MappingVersion,1,"resumed-epoch",1,c.RecordingEpochId,VivePracticeEpochReasonV3.ResumedAfterInterruption,c.ObservedPracticeStartLocalSeconds)
            };
        }
        static void SameEvent(JournalEvent a,JournalEvent b)
        {
            Check.Equal(a.Sequence,b.Sequence);Check.Equal(BitConverter.DoubleToInt64Bits(a.Time),BitConverter.DoubleToInt64Bits(b.Time));
            Check.Equal(a.SessionId,b.SessionId);Check.Equal(a.ClockId,b.ClockId);Check.Equal(a.BlockIndex,b.BlockIndex);Check.Equal(a.Segment,b.Segment);
            Check.Equal(a.TrialId,b.TrialId);Check.Equal(a.Kind,b.Kind);Check.Equal(a.Fields.Count,b.Fields.Count);
            foreach(var x in a.Fields)Check.Equal(x.Value,b.Fields[x.Key]);
        }
        static void Rejected(VivePracticeMultiReadResult result){Check.True(result.Status!=VivePracticeMultiStatus.Complete);}
        public static void Register(TestSuite suite)
        {
            suite.Run("multi practice clean close exposes start and caller declared tasks",()=>{
                var f=new Fixture();var w=f.Writer();var task=f.Task(fields:new Dictionary<string,string>{{"utf8","hello \u00e9"},{"nullable",null}});
                Check.True(Append(w,task));Check.True(w.Close());var bytes=w.Snapshot();var r=Read(bytes,false);
                Check.Equal(VivePracticeMultiStatus.Complete,r.Status);Check.True(r.Closed);Check.Equal(4,r.Events.Count);
                for(int i=0;i<3;i++)SameEvent(f.Batch.Events[i],r.Events[i]);SameEvent(task,r.Events[3]);
                Check.Equal((long)bytes.Length,(long)r.CommittedBytes);Check.True(r.VerifiedBytes>=r.CommittedBytes);
            });
            suite.Run("multi practice unclean prefix requires explicit recovery",()=>{
                var f=new Fixture();var w=f.Writer();Check.True(Append(w,f.Task()));var bytes=w.Snapshot();
                var no=Read(bytes,false);Check.Equal(VivePracticeMultiStatus.Unclean,no.Status);Check.Equal(0,no.Events.Count);
                var yes=Read(bytes,true);Check.Equal(VivePracticeMultiStatus.Unclean,yes.Status);Check.Equal(4,yes.Events.Count);Check.True(!yes.Closed);
            });
            suite.Run("multi practice all truncated prefixes expose at most complete commits",()=>{
                var f=new Fixture();var w=f.Writer();var start=w.Snapshot();Check.True(Append(w,f.Task()));var first=w.Snapshot();
                Check.True(Append(w,f.Task(2,0.001)));var second=w.Snapshot();Check.True(w.Close());var all=w.Snapshot();
                for(int n=0;n<all.Length;n++)
                {
                    var r=Read(Prefix(all,n));Rejected(r);int expected=n<start.Length?0:n<first.Length?3:n<second.Length?4:5;
                    Check.Equal(expected,r.Events.Count,"prefix "+n);Check.True(r.CommittedBytes<=r.VerifiedBytes&&r.VerifiedBytes<=n,"prefix "+n);
                }
                Check.Equal(VivePracticeMultiStatus.Complete,Read(all,false).Status);
            });
            suite.Run("multi practice multi-event transaction is never half published",()=>{
                var f=new Fixture();var w=f.Writer();var before=w.Snapshot();
                var first=f.Task();var second=f.Task(2,.001,kind:"first_response");Check.True(Append(w,first,second));
                var after=w.Snapshot();Check.True(w.Close());var closed=w.Snapshot();
                for(int n=before.Length;n<after.Length;n++)
                {
                    var result=Read(Prefix(after,n));Rejected(result);Check.Equal(3,result.Events.Count,"partial transaction "+n);
                    Check.Equal(before.Length,result.CommittedBytes);
                }
                var complete=Read(after);Check.Equal(VivePracticeMultiStatus.Unclean,complete.Status);Check.Equal(5,complete.Events.Count);
                SameEvent(first,complete.Events[3]);SameEvent(second,complete.Events[4]);Check.Equal(VivePracticeMultiStatus.Complete,Read(closed,false).Status);
            });
            suite.Run("multi practice invalid second transaction event leaves previous prefix unchanged",()=>{
                var f=new Fixture();var w=f.Writer();var before=w.Snapshot();Check.True(!Append(w,f.Task(),f.Task(3)));
                Check.True(w.Faulted);var after=w.Snapshot();Check.Equal(before.Length,after.Length);
                for(int i=0;i<before.Length;i++)Check.Equal(before[i],after[i]);
                var recovered=Read(after);Check.Equal(3,recovered.Events.Count);
                var empty=f.Writer();Check.True(!empty.AppendTaskTransaction(f.Context,new JournalEvent[0]));Check.True(empty.Faulted);
                var big=f.Writer();var many=new JournalEvent[257];for(int i=0;i<many.Length;i++)many[i]=f.Task(i+1);
                Check.True(!big.AppendTaskTransaction(f.Context,many));Check.True(big.Faulted);
            });
            suite.Run("multi practice corrupt stage or commit never publishes it",()=>{
                var f=new Fixture();var w=f.Writer();var start=w.Snapshot();Check.True(Append(w,f.Task()));var next=w.Snapshot();
                foreach(int at in new[]{start.Length,(start.Length+next.Length)/2,next.Length-1})
                {
                    var bad=(byte[])next.Clone();bad[at]^=1;var r=Read(bad);Rejected(r);Check.Equal(3,r.Events.Count);Check.Equal((long)start.Length,(long)r.CommittedBytes);
                }
                var damagedStart=(byte[])start.Clone();damagedStart[damagedStart.Length-1]^=1;var first=Read(damagedStart);Rejected(first);Check.Equal(0,first.Events.Count);
            });
            suite.Run("multi practice rejects a valid batch spliced from another context",()=>{
                var a=new Fixture("session-aaaa","epoch-a");var b=new Fixture("session-bbbb","epoch-b");var wa=a.Writer();var wb=b.Writer();
                var aStart=wa.Snapshot();var bStart=wb.Snapshot();Check.True(Append(wb,b.Task()));var bNext=wb.Snapshot();
                var foreign=new byte[bNext.Length-bStart.Length];Buffer.BlockCopy(bNext,bStart.Length,foreign,0,foreign.Length);
                var r=Read(Append(aStart,foreign));Rejected(r);Check.Equal(3,r.Events.Count);Check.Equal((long)aStart.Length,(long)r.CommittedBytes);
            });
            suite.Run("multi practice rejects rehashed invalid semantics and unknown batch types",()=>{
                var f=new Fixture();var w=f.Writer();var start=w.Snapshot();var prior=new byte[32];Buffer.BlockCopy(start,start.Length-32,prior,0,32);
                var invalidEvents=new[]{f.Task(2),f.Task(session:"other-session"),f.Task(clock:"other-clock"),f.Task(segment:f.Batch.Events[2].Segment+1),
                    f.Task(kind:"eye_observation"),f.Task(kind:"practice_completed",fields:Completion("2"))};
                foreach(var invalid in invalidEvents)
                {
                    byte[] digest;var payload=VivePracticeMultiCodec.EncodeTransaction(f.Context,new[]{invalid},262144);
                    var frame=VivePracticeMultiCodec.Frame(1,2,prior,payload,out digest);var read=Read(Append(start,frame));
                    Check.Equal(VivePracticeMultiStatus.Invalid,read.Status);Check.Equal(3,read.Events.Count);Check.Equal(start.Length,read.CommittedBytes);
                }
                byte[] ignored;var eyeBatch=VivePracticeMultiCodec.Frame(1,4,prior,new byte[]{0,1,2},out ignored);var result=Read(Append(start,eyeBatch));
                Check.Equal(VivePracticeMultiStatus.Invalid,result.Status);Check.Equal(3,result.Events.Count);
                var wrongAttempt=f.Task(kind:"practice_completed",fields:Completion("2"));
                var boundary=new JournalEvent(wrongAttempt.Sequence+1,wrongAttempt.Time,wrongAttempt.ClockId,wrongAttempt.SessionId,0,wrongAttempt.Segment,null,
                    "phase_changed",new Dictionary<string,string>{{"from","Practice"},{"to","BlockPreparation"}});
                var wrongPayload=VivePracticeMultiCodec.EncodeTransaction(f.Context,new[]{wrongAttempt,boundary},262144);
                var forged=VivePracticeMultiCodec.Frame(1,2,prior,wrongPayload,out ignored);var wrongResult=Read(Append(start,forged));
                Check.Equal(VivePracticeMultiStatus.Invalid,wrongResult.Status);Check.Equal(3,wrongResult.Events.Count);
                var invalidSecond=VivePracticeMultiCodec.EncodeTransaction(f.Context,new[]{f.Task(),f.Task(3)},262144);
                var mixed=VivePracticeMultiCodec.Frame(1,2,prior,invalidSecond,out ignored);var mixedResult=Read(Append(start,mixed));
                Check.Equal(VivePracticeMultiStatus.Invalid,mixedResult.Status);Check.Equal(3,mixedResult.Events.Count);
                Check.Equal(start.Length,mixedResult.CommittedBytes);
            });
            suite.Run("multi practice rejects fully rehashed cross epoch identity and mapping context",()=>{
                var f=new Fixture();var original=f.Writer().Snapshot();var prior=new byte[32];Buffer.BlockCopy(original,original.Length-32,prior,0,32);
                foreach(var other in OtherContexts(f.Context))
                {
                    var writer=f.Writer();Check.True(!writer.AppendTaskTransaction(other,new[]{f.Task()}));Check.True(writer.Faulted);
                    byte[] ignored;var payload=VivePracticeMultiCodec.EncodeTransaction(other,new[]{f.Task()},262144);
                    var frame=VivePracticeMultiCodec.Frame(1,2,prior,payload,out ignored);var result=Read(Append(original,frame));
                    Check.Equal(VivePracticeMultiStatus.Invalid,result.Status);Check.Equal(3,result.Events.Count);Check.Equal(original.Length,result.CommittedBytes);
                }
            });
            suite.Run("multi practice rejects event sequence gaps replays and time reversal",()=>{
                var f=new Fixture();foreach(var item in new[]{f.Task(0),f.Task(2),f.Task(1,-0.001),f.Task(1,Double.NaN)})
                {var w=f.Writer();var before=w.Snapshot();Check.True(!Append(w,item));Check.True(w.Faulted);Check.Equal(before.Length,w.Snapshot().Length);}
                var good=f.Writer();Check.True(Append(good,f.Task()));var beforeReplay=good.Snapshot();
                Check.True(!Append(good,f.Task()));Check.True(good.Faulted);Check.Equal(beforeReplay.Length,good.Snapshot().Length);
            });
            suite.Run("multi practice rejects mismatched session clock segment and attempt",()=>{
                var f=new Fixture();var wrong=new[]{f.Task(session:"other-session"),f.Task(clock:"other-clock"),f.Task(segment:f.Batch.Events[2].Segment+1),
                    f.Task(kind:"practice_completed",fields:Completion("2"))};
                foreach(var item in wrong){var w=f.Writer();var before=w.Snapshot();Check.True(!Append(w,item));Check.True(w.Faulted);Check.Equal(before.Length,w.Snapshot().Length);}
                var badAttempt=f.Task(kind:"practice_completed",fields:Completion("2"));
                var boundary=new JournalEvent(badAttempt.Sequence+1,badAttempt.Time,badAttempt.ClockId,badAttempt.SessionId,0,badAttempt.Segment,null,
                    "phase_changed",new Dictionary<string,string>{{"from","Practice"},{"to","BlockPreparation"}});
                var candidate=f.Writer();Check.True(!Append(candidate,badAttempt,boundary));Check.True(candidate.Faulted);
            });
            suite.Run("multi practice rejects repeated start and eye kinds",()=>{
                var f=new Fixture();foreach(var kind in new[]{"practice_started","eye_observation","raw_eye",""})
                {var w=f.Writer();Check.True(!Append(w,f.Task(kind:kind)));Check.True(w.Faulted);}
            });
            suite.Run("multi practice completion requires its boundary and forbids later task",()=>{
                var f=new Fixture();var done=f.Task(kind:"practice_completed",fields:Completion());
                var boundary=new JournalEvent(done.Sequence+1,done.Time,done.ClockId,done.SessionId,0,done.Segment,null,"phase_changed",
                    new Dictionary<string,string>{{"from","Practice"},{"to","BlockPreparation"}});
                var incomplete=f.Writer();Check.True(!Append(incomplete,done));Check.True(incomplete.Faulted);
                var w=f.Writer();Check.True(Append(w,done,boundary));Check.True(w.Close());Check.Equal(VivePracticeMultiStatus.Complete,Read(w.Snapshot(),false).Status);
                var x=f.Writer();Check.True(Append(x,done,boundary));Check.True(!Append(x,f.Task(3)));Check.True(x.Faulted);
            });
            suite.Run("multi practice completion commits block preparation transition in same transaction",()=>{
                var f=new Fixture();var w=f.Writer();var done=f.Task(kind:"practice_completed",fields:Completion());
                var boundary=new JournalEvent(done.Sequence+1,done.Time,done.ClockId,done.SessionId,0,done.Segment,null,"phase_changed",
                    new Dictionary<string,string>{{"from","Practice"},{"to","BlockPreparation"}});
                Check.True(Append(w,done,boundary));Check.True(w.Close());var result=Read(w.Snapshot(),false);
                Check.Equal(VivePracticeMultiStatus.Complete,result.Status);Check.Equal(5,result.Events.Count);SameEvent(done,result.Events[3]);SameEvent(boundary,result.Events[4]);
                var wrong=new JournalEvent(done.Sequence+1,done.Time,done.ClockId,done.SessionId,0,done.Segment,null,"stimulus_presented",new Dictionary<string,string>());
                var bad=f.Writer();Check.True(!Append(bad,done,wrong));Check.True(bad.Faulted);
                var failed=f.Task(kind:"practice_completed",fields:Completion("1","2"));var falsePass=f.Writer();
                Check.True(!Append(falsePass,failed,boundary));Check.True(falsePass.Faulted);
                var badScore=f.Writer();Check.True(!Append(badScore,f.Task(kind:"practice_completed",fields:Completion("1","9")),boundary));Check.True(badScore.Faulted);
                var badPlanned=f.Writer();Check.True(!Append(badPlanned,f.Task(kind:"practice_completed",fields:Completion("1","8","7")),boundary));Check.True(badPlanned.Faulted);
            });
            suite.Run("multi practice failed first attempt records retry boundary without entering main task",()=>{
                var f=new Fixture();var w=f.Writer();var failed=f.Task(kind:"practice_completed",fields:Completion("1","2"));
                var retry=f.Task(2,kind:"phase_changed",fields:new Dictionary<string,string>{{"from","Practice"},{"to","PracticeRetry"}});
                Check.True(Append(w,failed,retry));Check.True(w.Close());var result=Read(w.Snapshot(),false);
                Check.Equal(VivePracticeMultiStatus.Complete,result.Status);Check.Equal(5,result.Events.Count);SameEvent(failed,result.Events[3]);SameEvent(retry,result.Events[4]);
            });
            suite.Run("multi practice rejects tail after close and old formats",()=>{
                var f=new Fixture();var w=f.Writer();Check.True(w.Close());var tail=Append(w.Snapshot(),new byte[]{1});
                var result=Read(tail);Rejected(result);
                foreach(var old in new[]{Encoding.ASCII.GetBytes("VPRAC003"),f.Start.ToArray(),Encoding.ASCII.GetBytes("NOTAFILE")})
                {var r=Read(old);Rejected(r);Check.Equal(0,r.Events.Count);}
            });
            suite.Run("multi practice budgets never disclose partial recovery",()=>{
                var f=new Fixture();var w=f.Writer();Check.True(Append(w,f.Task()));Check.True(w.Close());var bytes=w.Snapshot();
                var limited=Read(bytes,true,bytes.Length-1,Batches);Check.Equal(VivePracticeMultiStatus.LimitExceeded,limited.Status);Check.Equal(0,limited.Events.Count);
                var countLimited=Read(bytes,true,Max,1);Check.Equal(VivePracticeMultiStatus.LimitExceeded,countLimited.Status);Check.Equal(0,countLimited.Events.Count);
            });
            suite.Run("multi practice result and writer snapshot are independent copies",()=>{
                var f=new Fixture();var w=f.Writer();Check.True(w.Close());var a=w.Snapshot();var b=w.Snapshot();
                byte first=a[0];a[0]^=0x7f;Check.Equal(first,b[0]);var parsed=Read(b,false);Check.Equal(VivePracticeMultiStatus.Complete,parsed.Status);
                for(int i=0;i<b.Length;i++)b[i]=0;Check.Equal(3,parsed.Events.Count);SameEvent(f.Batch.Events[0],parsed.Events[0]);
            });
        }
    }
}
