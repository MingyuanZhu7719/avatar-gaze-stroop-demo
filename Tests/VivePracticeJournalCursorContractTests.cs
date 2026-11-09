using System;
using System.Collections.Generic;
using System.IO;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic in-memory fixtures only: no provider, real participant, file,
    // network, OS move, recording authority or experiment gate is involved.
    public static class VivePracticeJournalCursorContractTests
    {
        sealed class Clock:IClock
        {public double Value=10;public double Now {get{return Value;}}public string ClockId {get{return "fixture-clock";}}}
        static VivePracticeContextV3 Context()
        {return new VivePracticeContextV3("fixture-session","fixture-participant","fixture-stream","fixture-device","fixture-clock","fixture-map",1,"epoch-1",0,null,VivePracticeEpochReasonV3.Initial,9);}
        static JournalEvent Event(long sequence,double time,string kind)
        {return new JournalEvent(sequence,time,"fixture-clock","fixture-session",-1,0,null,kind,new Dictionary<string,string>{{"attempt","1"}});}
        static VivePracticeJournalV3Writer Open(MemoryStream stream,Clock clock,bool contiguous)
        {
            var context=Context();clock.Value=8;
            var preparation=VivePracticeRecordingPreparationV3.Create(clock,context.SessionId,context.PracticeAttempt,context.RecordingEpochId,true,true);
            clock.Value=10;
            if(!contiguous)return new VivePracticeJournalV3Writer(stream,clock,context,Event(0,9,"practice_started"),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,preparation);
            return new VivePracticeJournalV3Writer(stream,clock,context,Event(0,9,"practice_started"),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,preparation,VivePracticeTaskCursorContractV3.StartContiguousV1);
        }
        static VivePracticeJournalV3ReadResult Read(byte[] bytes,bool recover=false)
        {using(var stream=new MemoryStream(bytes,false))return VivePracticeJournalV3Reader.Read(stream,true,recover,1000,1000000);}
        static byte[] Complete(bool contiguous)
        {
            var clock=new Clock();var stream=new MemoryStream();var writer=Open(stream,clock,contiguous);
            if(contiguous){writer.WriteTaskEvent(Event(1,9,"phase_changed"));writer.WriteTaskEvent(Event(2,9,"trial_planned"));writer.WriteTaskEvent(Event(3,9.5,"stimulus_presented"));}
            else {clock.Value=10.1;writer.WriteTaskEvent(Event(5,10.1,"stimulus_presented"));}
            clock.Value=10.2;Check.True(writer.Close());return stream.ToArray();
        }
        // Rehash a synthetic document after deliberately changing a decoded
        // record. This exercises semantic validation, not merely a bad digest.
        static byte[] Rewrite(VivePracticeJournalV3ReadResult input,Action<string,int,Dictionary<string,string>> change,string omit=null,int limit=int.MaxValue)
        {
            var memory=new MemoryStream();memory.Write(VivePracticeJournalV3Codec.Magic,0,VivePracticeJournalV3Codec.Magic.Length);
            var token=new byte[16];var previous=new byte[32];long sequence=0;int taskIndex=0;
            foreach(var row in input.Records)
            {
                if(row.Kind==omit)continue;if(sequence>=limit)break;
                var fields=new Dictionary<string,string>();foreach(var pair in row.Fields)fields.Add(pair.Key,pair.Value);
                int index=row.Kind=="task_event"?taskIndex++:-1;
                if(change!=null)change(row.Kind,index,fields);
                var payload=ContinuousJournal.Payload(sequence++,token,row.Kind,fields);
                var digest=ContinuousJournal.Digest(previous,payload);var prefix=new byte[8];
                ContinuousJournal.PutInt(prefix,payload.Length);ContinuousJournal.PutInt(prefix,~payload.Length,4);
                memory.Write(prefix,0,prefix.Length);memory.Write(payload,0,payload.Length);memory.Write(digest,0,digest.Length);previous=digest;
            }
            return memory.ToArray();
        }
        static void ReplaceTask(Dictionary<string,string> fields,long sequence,double time,string kind)
        {fields["task_sequence"]=VivePracticeJournalV3Codec.N(sequence);fields["observed_local_seconds"]=VivePracticeJournalV3Codec.N(time);fields["detail"]=VivePracticeJournalV3Codec.TaskDetail(Event(sequence,time,kind));}
        public static void Register(TestSuite suite)
        {
            suite.Run("V3 contiguous cursor round trips start+1 and same-tick tasks before open",()=>{
                var result=Read(Complete(true));Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,result.Status);
                Check.Equal("START_CONTIGUOUS_V1",result.Records[0].Fields["task_cursor_contract"]);
                Check.True(result.HasDeclaredStartBoundary);Check.True(!result.IdentityAuthenticated&&!result.ConsentAuthenticated&&!result.ContinuousCoverageProven);
                var sequences=new List<string>();var times=new List<string>();foreach(var row in result.Records)if(row.Kind=="task_event"){sequences.Add(row.Fields["task_sequence"]);times.Add(row.Fields["observed_local_seconds"]);}
                Check.Equal(3,sequences.Count);Check.Equal("1",sequences[0]);Check.Equal("2",sequences[1]);Check.Equal("3",sequences[2]);
                Check.Equal("9",times[0]);Check.Equal("9",times[1]);Check.Equal("9.5",times[2]);
            });
            suite.Run("V3 contiguous writer rejects a first skipped event before any task append",()=>{
                var clock=new Clock();var stream=new MemoryStream();var writer=Open(stream,clock,true);long before=stream.Length;
                Check.Throws<ArgumentException>(()=>writer.WriteTaskEvent(Event(2,9,"trial_planned")));
                Check.Equal(before,stream.Length);Check.Equal(VivePracticeV3WriterState.Faulted,writer.State);Check.True(!writer.ProductionGateOpen);
            });
            suite.Run("V3 contiguous writer rejects subsequent gaps and replay",()=>{
                foreach(long bad in new long[]{1,3}){var clock=new Clock();var stream=new MemoryStream();var writer=Open(stream,clock,true);
                    writer.WriteTaskEvent(Event(1,9,"phase_changed"));long before=stream.Length;
                    Check.Throws<ArgumentException>(()=>writer.WriteTaskEvent(Event(bad,9,"trial_planned")));
                    Check.Equal(before,stream.Length);Check.Equal(VivePracticeV3WriterState.Faulted,writer.State);}
            });
            suite.Run("V3 contiguous writer rejects pre-start, backward and future times",()=>{
                var clock=new Clock();var stream=new MemoryStream();var writer=Open(stream,clock,true);
                Check.Throws<ArgumentException>(()=>writer.WriteTaskEvent(Event(1,8.99,"phase_changed")));
                clock=new Clock();stream=new MemoryStream();writer=Open(stream,clock,true);writer.WriteTaskEvent(Event(1,9.5,"phase_changed"));
                Check.Throws<ArgumentException>(()=>writer.WriteTaskEvent(Event(2,9.4,"trial_planned")));
                clock=new Clock();stream=new MemoryStream();writer=Open(stream,clock,true);
                Check.Throws<ArgumentException>(()=>writer.WriteTaskEvent(Event(1,10.1,"phase_changed")));
            });
            suite.Run("V3 legacy writer and reader keep original optional contract semantics",()=>{
                var result=Read(Complete(false));Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,result.Status);
                Check.True(!result.Records[0].Fields.ContainsKey("task_cursor_contract"));
                foreach(var row in result.Records)if(row.Kind=="task_event")Check.Equal("5",row.Fields["task_sequence"]);
                var clock=new Clock();var stream=new MemoryStream();var writer=Open(stream,clock,false);
                Check.Throws<ArgumentException>(()=>writer.WriteTaskEvent(Event(1,10,"phase_changed")));
            });
            suite.Run("V3 contiguous reader rejects rehashed first and later skipped tasks",()=>{
                var original=Read(Complete(true));
                var first=Rewrite(original,(kind,index,fields)=>{if(kind=="task_event"&&index==0)ReplaceTask(fields,2,9,"phase_changed");});
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(first).Status);
                var later=Rewrite(original,(kind,index,fields)=>{if(kind=="task_event"&&index==1)ReplaceTask(fields,3,9,"trial_planned");});
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(later).Status);
                var repeated=Rewrite(original,(kind,index,fields)=>{if(kind=="task_event"&&index==1)ReplaceTask(fields,1,9,"trial_planned");});
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(repeated).Status);
            });
            suite.Run("V3 contiguous reader rejects rehashed time rollback and pre-start",()=>{
                var original=Read(Complete(true));
                var before=Rewrite(original,(kind,index,fields)=>{if(kind=="task_event"&&index==0)ReplaceTask(fields,1,8.99,"phase_changed");});
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(before).Status);
                var backward=Rewrite(original,(kind,index,fields)=>{
                    if(kind=="task_event"&&index==0)ReplaceTask(fields,1,9.5,"phase_changed");
                    if(kind=="task_event"&&index==1)ReplaceTask(fields,2,9.25,"trial_planned");});
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(backward).Status);
            });
            suite.Run("V3 reader rejects unknown new contract and unexpected header fields",()=>{
                var original=Read(Complete(true));
                var unknown=Rewrite(original,(kind,index,fields)=>{if(kind=="header")fields["task_cursor_contract"]="START_CONTIGUOUS_V2";});
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(unknown).Status);
                var empty=Rewrite(original,(kind,index,fields)=>{if(kind=="header")fields["task_cursor_contract"]=null;});
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(empty).Status);
                var extra=Rewrite(original,(kind,index,fields)=>{if(kind=="header")fields["unexpected"]="value";});
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(extra).Status);
            });
            suite.Run("V3 contiguous header requires start boundary for complete data",()=>{
                var original=Read(Complete(true));
                var missing=Rewrite(original,null,"practice_start_boundary");Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(missing).Status);
                var headerOnly=Rewrite(original,null,null,1);var recovered=Read(headerOnly,true);
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,recovered.Status);Check.True(!recovered.HasDeclaredStartBoundary&&!recovered.CompleteSessionProven);
                var strict=Read(headerOnly);Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,strict.Status);Check.Equal(0,strict.Records.Count);
            });
            suite.Run("V3 contiguous start sequence reserves a safe next value",()=>{
                var clock=new Clock();var ctx=Context();clock.Value=8;
                var prep=VivePracticeRecordingPreparationV3.Create(clock,ctx.SessionId,ctx.PracticeAttempt,ctx.RecordingEpochId,true,true);clock.Value=10;
                var stream=new MemoryStream();
                Check.Throws<ArgumentException>(()=>new VivePracticeJournalV3Writer(stream,clock,ctx,Event(Int64.MaxValue-1,9,"practice_started"),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,prep,VivePracticeTaskCursorContractV3.StartContiguousV1));
                Check.Equal(0L,stream.Length);
                clock=new Clock();clock.Value=8;prep=VivePracticeRecordingPreparationV3.Create(clock,ctx.SessionId,ctx.PracticeAttempt,ctx.RecordingEpochId,true,true);clock.Value=10;stream=new MemoryStream();
                var legacy=new VivePracticeJournalV3Writer(stream,clock,ctx,Event(Int64.MaxValue-1,9,"practice_started"),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,prep);
                clock.Value=10.1;Check.True(legacy.Close());Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(stream.ToArray()).Status);
            });
            suite.Run("V3 contiguous damaged bytes cannot be exposed as clean data",()=>{
                var complete=Complete(true);var bad=(byte[])complete.Clone();bad[bad.Length-1]^=1;
                var strict=Read(bad);Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,strict.Status);Check.Equal(0,strict.Records.Count);
                var recovered=Read(bad,true);Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,recovered.Status);
                Check.True(recovered.Records.Count>0&&!recovered.CompleteSessionProven);
                var truncated=new byte[complete.Length-5];Buffer.BlockCopy(complete,0,truncated,0,truncated.Length);
                var result=Read(truncated,true);Check.True(result.Status!=ViveDevelopmentJournalReadStatus.CleanClose&&!result.CompleteSessionProven);
            });
        }
    }
}
