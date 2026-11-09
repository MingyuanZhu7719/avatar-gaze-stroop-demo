using System;
using System.Collections.Generic;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Only real Simulation task objects and synthetic controller inputs.
    public static class ViveNamedPlanTaskMemoryProjectionTests
    {
        sealed class Clock:IClock {public double Time;public double Now {get{return Time;}}public string ClockId {get{return "plan-view-clock";}}}
        static ViveDevelopmentParticipant Make(Clock clock,int count)
        {
            var options=new ViveNamedOptionProvenance[count];
            for(int i=0;i<count;i++)options[i]=ViveNamedOptionProvenance.Select(Actor.Owner,i%2==0?ViveNamedOption.Automatic:ViveNamedOption.NoAvatar,i,i%2==0?"synthetic-geometry":null);
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-plan",options);
            var config=SessionConfiguration.NamedPlanDevelopment("plan-session",37,plan);
            var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"synthetic-geometry");
            var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(config,clock,geometry,mapping,.5,true);
        }
        static void Input(ViveDevelopmentParticipant p,bool pressed=false,Colour colour=Colour.Red)
        {double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0;double y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
            p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,pressed,x,y));}
        static void Respond(ViveDevelopmentParticipant p,Clock c)
        {Input(p);p.MarkPresented(p.CurrentTrial.TrialId);var ink=p.CurrentTrial.Stimulus.Ink;c.Time+=.2;Input(p,true,ink);Input(p);}
        static void Complete(ViveDevelopmentParticipant p,Clock c,int blocks)
        {
            Input(p);p.OwnerBeginPractice(Actor.Owner);for(int i=0;i<8;i++)Respond(p,c);
            p.OwnerConfirmNamedPlan(Actor.Owner,"synthetic-owner");p.OwnerBeginBlock(Actor.Owner);c.Time+=3;p.Tick();
            for(int block=0;block<blocks;block++)
            {
                Check.Equal(Phase.MainTask,p.Phase);Check.Equal(block,p.BlockIndex);
                while(p.Phase==Phase.MainTask)Respond(p,c);
                Check.Equal(Phase.Questionnaire,p.Phase);p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);
                for(int q=0;q<Questionnaire.Count;q++){p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);}
                if(block+1<blocks)
                {Check.Equal(Phase.Rest,p.Phase);p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);p.Tick();c.Time+=3;p.Tick();}
            }
            Check.Equal(Phase.Ended,p.Phase);Check.Equal((EndReason?)EndReason.Completed,p.EndReason);
        }
        static string Decode(string value)
        {Check.True(value.StartsWith("b64:"));return Encoding.UTF8.GetString(Convert.FromBase64String(value.Substring(4)));}
        static string Time(double? value){return value.HasValue?value.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture):null;}
        static Dictionary<string,Dictionary<string,string>> Read(string csv)
        {
            var result=new Dictionary<string,Dictionary<string,string>>();var rows=csv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);
            for(int i=1;i<rows.Length;i++)
            {var c=rows[i].Split(',');Check.Equal(6,c.Length);string id=c[1]+"/"+c[2];Dictionary<string,string> fields;
                if(!result.TryGetValue(id,out fields)){fields=new Dictionary<string,string>();result.Add(id,fields);}
                fields.Add(Decode(c[3]),c[4]=="bool:true"?Decode(c[5]):null);}
            return result;
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("named plan task view captures actual participant records for one to four blocks",()=>{
                for(int count=1;count<=4;count++)
                {
                    var c=new Clock();var p=Make(c,count);Complete(p,c,count);ViveNamedTaskMemoryView view;string issue;
                    Check.True(ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue),issue);
                    Check.Equal(8+24*count,view.TrialRecordCount);Check.Equal(Questionnaire.Count*count,view.RatingRecordCount);
                    Check.True(!view.EyeDataIncluded&&!view.Saved&&!view.CompleteSessionProven);
                    var actualEvents=p.ReadEventsAfter(-1,4096);var eventRows=Read(view.Csv);var records=Read(view.RecordsCsv);
                    for(int i=0;i<actualEvents.Count;i++)
                    {
                        var item=actualEvents[i];var row=eventRows["event/u64:"+i];Check.Equal(item.Kind,row["kind"]);
                        Check.Equal(item.Sequence.ToString(),row["sequence"]);Check.Equal(Time(item.Time),row["time_local_seconds"]);
                        Check.Equal(item.SessionId,row["session_id"]);Check.Equal(item.ClockId,row["clock_id"]);
                        Check.Equal(item.BlockIndex.ToString(),row["block_index"]);Check.Equal(item.Segment.ToString(),row["segment"]);
                        Check.Equal(item.NamedPlan.PlanId,row["typed_plan_id"]);
                        Check.Equal(item.TrialId,row["trial_id"]);Check.Equal(item.NamedBlock==null?null:item.NamedBlock.Index.ToString(),row["typed_block_index"]);
                        foreach(var field in item.Fields)Check.Equal(field.Value,row["source_field:"+field.Key]);
                    }
                    for(int i=0;i<8;i++){var row=records["trial/u64:"+i];Check.Equal("True",row["is_practice"]);Check.Equal<string>(null,row["typed_block_index"]);Check.Equal<string>(null,row["typed_option"]);}
                    for(int b=0;b<count;b++)
                    {
                        var row=records["trial/u64:"+(8+24*b)];Check.Equal(b.ToString(),row["typed_block_index"]);
                        Check.Equal(b%2==0?"Automatic":"NoAvatar",row["typed_option"]);
                        Check.True(records.ContainsKey("planned_block/u64:"+b));
                    }
                    var taskField=typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                    var task=(Experiment)taskField.GetValue(p);
                    for(int i=0;i<task.Trials.Count;i++)
                    {
                        var t=task.Trials[i];var row=records["trial/u64:"+i];Check.Equal(t.TrialId,row["trial_id"]);Check.Equal(t.SessionId,row["session_id"]);
                        Check.Equal(t.ListId,row["list_id"]);Check.Equal(t.IsPractice.ToString(),row["is_practice"]);Check.Equal(t.PracticeAttempt.ToString(),row["practice_attempt"]);
                        Check.Equal(t.BlockIndex.ToString(),row["block_index"]);Check.Equal(t.PlannedIndex.ToString(),row["planned_index"]);Check.Equal(t.Segment.ToString(),row["segment"]);
                        Check.Equal(t.Stimulus.Word.ToString(),row["word"]);Check.Equal(t.Stimulus.Ink.ToString(),row["ink"]);Check.Equal(Time(t.Onset),row["onset_local_seconds"]);
                        Check.Equal(Time(t.ResponseAt),row["response_local_seconds"]);Check.Equal(t.Response.HasValue?t.Response.Value.ToString():null,row["response"]);
                        Check.Equal(t.Correct.HasValue?t.Correct.Value.ToString():null,row["correct"]);Check.Equal(Time(t.ReactionSeconds),row["reaction_seconds"]);
                        Check.Equal(t.Outcome.ToString(),row["outcome"]);Check.Equal(t.TechnicalIssue.ToString(),row["technical_issue"]);
                        Check.Equal(t.NamedBlock==null?null:t.NamedBlock.Index.ToString(),row["typed_block_index"]);
                        Check.Equal(t.NamedBlock==null?null:t.NamedBlock.Selection.Option.ToString(),row["typed_option"]);
                    }
                    for(int i=0;i<task.Ratings.Count;i++)
                    {
                        var r=task.Ratings[i];var row=records["rating/u64:"+i];Check.Equal(r.BlockIndex.ToString(),row["block_index"]);Check.Equal(r.ItemId,row["item_id"]);
                        Check.Equal(r.Value.ToString(),row["value"]);Check.Equal(Time(r.Time),row["time_local_seconds"]);Check.Equal(r.Language,row["language"]);
                        Check.Equal(r.Version,row["questionnaire_version"]);Check.True(!row.ContainsKey("typed_option"));
                    }
                }
            });
            suite.Run("named plan early technical stop shows planned blocks without inventing trials",()=>{
                var c=new Clock();var p=Make(c,4);Input(p);p.OwnerBeginPractice(Actor.Owner);p.EndTechnical(Actor.Owner,"synthetic stop");
                ViveNamedTaskMemoryView view;string issue;Check.True(ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue),issue);
                Check.Equal(1,view.TrialRecordCount);Check.Equal(0,view.RatingRecordCount);var rows=Read(view.RecordsCsv);
                Check.Equal("planned_only_not_proof_of_occurrence",rows["manifest/u64:0"]["plan_items"]);
                for(int b=0;b<4;b++)Check.True(rows.ContainsKey("planned_block/u64:"+b));
                Check.Equal<string>(null,rows["trial/u64:0"]["typed_block_index"]);
                Check.Equal("Interrupted",rows["trial/u64:0"]["outcome"]);Check.Equal<string>(null,rows["trial/u64:0"]["onset_local_seconds"]);
                p.ParticipantWithdraw();Check.True(view.Csv==null&&view.RecordsCsv==null);
            });
            suite.Run("named plan partial questionnaire does not invent future ratings",()=>{
                var c=new Clock();var p=Make(c,2);Input(p);p.OwnerBeginPractice(Actor.Owner);for(int i=0;i<8;i++)Respond(p,c);
                p.OwnerConfirmNamedPlan(Actor.Owner,"synthetic-owner");p.OwnerBeginBlock(Actor.Owner);c.Time+=3;p.Tick();
                while(p.Phase==Phase.MainTask)Respond(p,c);Check.Equal(Phase.Questionnaire,p.Phase);
                p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);
                p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);
                Check.Equal(1,p.RatingCount);p.EndTechnical(Actor.Owner,"synthetic partial questionnaire");
                ViveNamedTaskMemoryView view;string issue;Check.True(ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue),issue);
                Check.Equal(32,view.TrialRecordCount);Check.Equal(1,view.RatingRecordCount);var rows=Read(view.RecordsCsv);
                Check.True(rows.ContainsKey("rating/u64:0")&&!rows.ContainsKey("rating/u64:1"));Check.True(!view.CompleteSessionProven);
            });
            suite.Run("named plan preserves practice retry attempt without assigning a block",()=>{
                var c=new Clock();var p=Make(c,2);Input(p);p.OwnerBeginPractice(Actor.Owner);
                for(int i=0;i<8;i++)
                {
                    Input(p);p.MarkPresented(p.CurrentTrial.TrialId);var ink=p.CurrentTrial.Stimulus.Ink;
                    c.Time+=.2;Input(p,true,ink==Colour.Red?Colour.Green:Colour.Red);Input(p);Check.Equal(Phase.PracticeCorrection,p.Phase);
                    p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);
                }
                Check.Equal(Phase.PracticeRetry,p.Phase);Input(p);p.UpdateAuxiliary(false,false,false,0,true);p.OwnerBeginPractice(Actor.Owner);
                Check.Equal(Phase.Practice,p.Phase);p.EndTechnical(Actor.Owner,"synthetic retry stop");
                ViveNamedTaskMemoryView view;string issue;Check.True(ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue),issue);
                Check.Equal(9,view.TrialRecordCount);var rows=Read(view.RecordsCsv);
                Check.Equal("2",rows["trial/u64:8"]["practice_attempt"]);Check.Equal<string>(null,rows["trial/u64:8"]["typed_block_index"]);
                Check.Equal("Interrupted",rows["trial/u64:8"]["outcome"]);Check.Equal<string>(null,rows["trial/u64:8"]["onset_local_seconds"]);
            });
            suite.Run("named plan task projection rejects conflicting typed and declared provenance",()=>{
                var c=new Clock();var p=Make(c,2);Complete(p,c,2);ViveNamedTaskRecordSnapshot records;
                Check.True(p.TryCopyEndedNamedTaskRecords(out records));var source=p.ReadEventsAfter(-1,4096);
                foreach(int scenario in new[]{0,1,2})
                {
                    var events=new List<JournalEvent>(source);int index=-1;
                    for(int i=0;i<events.Count;i++)if(scenario==0?events[i].NamedBlock==null:events[i].NamedBlock!=null){index=i;break;}
                    Check.True(index>=0);var original=events[index];var fields=new Dictionary<string,string>();foreach(var kv in original.Fields)fields.Add(kv.Key,kv.Value);
                    if(scenario==0)fields["named_source"]="AUTOMATIC";
                    else if(scenario==1)fields["named_option"]=original.NamedBlock.Selection.Option==ViveNamedOption.Automatic?"NoAvatar":"Automatic";
                    else fields["named_participant_eye"]="RECORDED";
                    events[index]=new JournalEvent(original.Sequence,original.Time,original.ClockId,original.SessionId,original.BlockIndex,original.Segment,original.TrialId,original.Kind,fields,null,original.NamedPlan,original.NamedBlock);
                    Check.Throws<InvalidOperationException>(()=>{string a,b;int trial,rating;ViveNamedPlanTaskMemoryProjection.Render(p.NamedPlan,events,records,p.SessionId,p.EventClockId,out a,out b,out trial,out rating);});
                }
            });
            suite.Run("named plan task view refuses withdrawal during final publication",()=>{
                var c=new Clock();var p=Make(c,2);Input(p);p.OwnerBeginPractice(Actor.Owner);p.EndTechnical(Actor.Owner,"synthetic stop");
                ViveNamedTaskMemoryView view;string issue;
                bool made=ViveNamedTaskMemoryView.TryCreateForTest(p,true,false,()=>{var t=new System.Threading.Thread(p.ParticipantWithdraw);t.Start();Check.True(t.Join(5000));},out view,out issue);
                Check.True(!made&&view==null&&issue=="withdrawn_or_source_changed");
            });
        }
    }
}
