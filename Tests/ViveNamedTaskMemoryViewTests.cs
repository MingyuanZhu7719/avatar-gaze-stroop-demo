using System;
using System.Collections.Generic;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveNamedTaskMemoryViewTests
    {
        sealed class Clock:IClock {public double Time;public double Now {get{return Time;}}public string ClockId {get{return "named-view-clock";}}}
        static ViveDevelopmentParticipant Make(Clock clock,bool noAvatar=false,string geometryVersion="synthetic-geometry")
        {
            var named=ViveNamedOptionProvenance.Select(Actor.Owner,noAvatar?ViveNamedOption.NoAvatar:ViveNamedOption.Automatic,2,noAvatar?null:geometryVersion);
            var config=noAvatar?SessionConfiguration.NamedNoAvatarDevelopment("named-view-session",37,named):SessionConfiguration.NamedAutomaticDevelopment("named-view-session",37,named);
            var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),geometryVersion);
            var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(config,clock,geometry,mapping,.5,true);
        }
        static void Input(ViveDevelopmentParticipant p,bool pressed=false,Colour colour=Colour.Red)
        {double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0;double y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
            p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,pressed,x,y));}
        static void Response(ViveDevelopmentParticipant p,Clock c)
        {Input(p);p.MarkPresented(p.CurrentTrial.TrialId);var ink=p.CurrentTrial.Stimulus.Ink;c.Time+=.2;Input(p,true,ink);Input(p);}
        static void GoToQuestionnaire(ViveDevelopmentParticipant p,Clock c)
        {
            Input(p);p.OwnerBeginPractice(Actor.Owner);for(int i=0;i<8;i++)Response(p,c);
            p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"synthetic-owner");p.OwnerBeginBlock(Actor.Owner);c.Time+=3;p.Tick();
            while(p.Phase==Phase.MainTask)Response(p,c);
            Check.Equal(Phase.Questionnaire,p.Phase);
            p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);
        }
        static void Complete(ViveDevelopmentParticipant p,Clock c)
        {
            GoToQuestionnaire(p,c);
            for(int i=0;i<Questionnaire.Count;i++){p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);}
            Check.Equal(Phase.Ended,p.Phase);Check.Equal((EndReason?)EndReason.Completed,p.EndReason);
        }
        static string Decode(string value)
        {Check.True(value.StartsWith("b64:"));return Encoding.UTF8.GetString(Convert.FromBase64String(value.Substring(4)));}
        public static void Register(TestSuite suite)
        {
            suite.Run("named in-memory event view requires explicit stopped nonwithdrawn simulation",()=>{
                var c=new Clock();var p=Make(c);ViveNamedTaskMemoryView view;string issue;
                Check.True(!ViveNamedTaskMemoryView.TryCreate(p,false,false,out view,out issue)&&issue=="explicit_action_required");
                Check.True(!ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue)&&issue=="ended_nonwithdrawn_named_simulation_required");
                p.ParticipantWithdraw();Check.True(!ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue)&&view==null);
                var q=Make(new Clock());q.EndTechnical(Actor.Owner,"synthetic stop");
                Check.True(!ViveNamedTaskMemoryView.TryCreate(q,true,true,out view,out issue)&&view==null);
                Check.True(ViveNamedTaskMemoryView.TryCreate(q,true,false,out view,out issue));
                Check.True(!view.CompleteSessionProven&&!view.EyeDataIncluded&&!view.Saved);
                q.ParticipantWithdraw();Check.True(view.Csv==null&&view.RecordsCsv==null);Check.Equal(0,view.EventCount);Check.Equal(0,view.TrialRecordCount);
                Check.True(!ViveNamedTaskMemoryView.TryCreate(q,true,false,out view,out issue)&&view==null);
            });
            suite.Run("named in-memory view refuses concurrent withdrawal before publishing",()=>{
                var p=Make(new Clock());p.EndTechnical(Actor.Owner,"synthetic stop");ViveNamedTaskMemoryView view;string issue;
                bool made=ViveNamedTaskMemoryView.TryCreateForTest(p,true,false,()=>{
                    var t=new System.Threading.Thread(p.ParticipantWithdraw);t.Start();Check.True(t.Join(5000));
                },out view,out issue);
                Check.True(!made&&view==null&&issue=="withdrawn_or_source_changed");
            });
            suite.Run("named in-memory view is invalidated by withdrawal signal after end",()=>{
                var p=Make(new Clock());p.EndTechnical(Actor.Owner,"synthetic stop");ViveNamedTaskMemoryView view;string issue;
                Check.True(ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue));
                p.UpdateAuxiliary(false,false,true,0,true);Check.True(p.NamedViewWithdrawalRequested&&view.Csv==null);
                Check.True(!ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue)&&view==null);
            });
            suite.Run("named in-memory view refuses creation outside its owner thread",()=>{
                var p=Make(new Clock());p.EndTechnical(Actor.Owner,"synthetic stop");bool made=true;
                var t=new System.Threading.Thread(()=>{ViveNamedTaskMemoryView view;string issue;
                    made=ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue);});
                t.Start();Check.True(t.Join(5000));Check.True(!made);
            });
            suite.Run("no-avatar task view retains not-applicable source and no eye recording",()=>{
                var p=Make(new Clock(),true);p.EndTechnical(Actor.Owner,"synthetic stop");ViveNamedTaskMemoryView view;string issue;
                Check.True(ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue),issue);
                var rows=view.Csv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);bool source=false,eye=false;
                for(int i=1;i<rows.Length;i++)
                {
                    var cells=rows[i].Split(',');Check.Equal("NoAvatar",Decode(cells[12]));
                    Check.Equal(ViveNamedOptionProvenance.NoAvatarNotApplicable,Decode(cells[13]));
                    Check.Equal(ViveNamedOptionProvenance.NoAvatarNotApplicable,Decode(cells[14]));
                    if(cells[16]=="bool:true"&&Decode(cells[17])=="named_source")
                    {Check.Equal(ViveNamedOptionProvenance.NoAvatarNotApplicable,Decode(cells[19]));source=true;}
                    if(cells[16]=="bool:true"&&Decode(cells[17])=="named_participant_eye")
                    {Check.Equal("NOT_RECORDED",Decode(cells[19]));eye=true;}
                }
                Check.True(source&&eye&&!view.EyeDataIncluded);
            });
            suite.Run("named task view refuses oversized field without partial output",()=>{
                var p=Make(new Clock(),false,new string('x',65537));p.EndTechnical(Actor.Owner,"synthetic stop");
                ViveNamedTaskMemoryView view;string issue;
                Check.True(!ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue)&&view==null&&issue=="source_invalid_or_limit");
            });
            suite.Run("named task pages preserve all characters and bound each display chunk",()=>{
                string input=new string('a',8191)+"\n"+new string('b',8193)+"z";var result=new StringBuilder();
                for(int offset=0;offset<input.Length;)
                {var page=ViveNamedTaskMemoryView.SliceForDisplay(input,offset,8192);Check.True(page.Length>0&&page.Length<=8192);result.Append(page);offset+=page.Length;}
                Check.Equal(input,result.ToString());Check.Equal("",ViveNamedTaskMemoryView.SliceForDisplay(input,input.Length,8192));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveNamedTaskMemoryView.SliceForDisplay(input,-1,8192));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveNamedTaskMemoryView.SliceForDisplay(input,0,8193));
            });
            suite.Run("named task snapshot preserves interrupted unpresented trial and absent ratings",()=>{
                var c=new Clock();var p=Make(c);Input(p);p.OwnerBeginPractice(Actor.Owner);p.EndTechnical(Actor.Owner,"synthetic stop");
                ViveNamedTaskMemoryView view;string issue;Check.True(ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue),issue);
                Check.Equal(1,view.TrialRecordCount);Check.Equal(0,view.RatingRecordCount);
                var fields=new Dictionary<string,string>();foreach(string row in view.RecordsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries))
                {var cells=row.Split(',');if(cells[0]==ViveNamedTaskMemoryView.RecordSchema&&cells[1]=="trial")fields.Add(Decode(cells[3]),cells[4]=="bool:true"?Decode(cells[5]):null);}
                Check.Equal("Interrupted",fields["outcome"]);Check.Equal<string>(null,fields["onset_local_seconds"]);
                Check.Equal<string>(null,fields["response_local_seconds"]);Check.Equal<string>(null,fields["response"]);Check.Equal<string>(null,fields["correct"]);
            });
            suite.Run("v1 named task view rejects foreign trial provenance or block",()=>{
                foreach(bool wrongOption in new[]{false,true})
                {
                    var p=Make(new Clock());Input(p);p.OwnerBeginPractice(Actor.Owner);p.EndTechnical(Actor.Owner,"synthetic stop");
                    var field=typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                    var experiment=(Experiment)field.GetValue(p);var trial=experiment.Trials[0];
                    if(wrongOption)typeof(TrialRecord).GetProperty("NamedOption").SetValue(trial,ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,2,"synthetic-geometry"),null);
                    else typeof(TrialRecord).GetProperty("BlockIndex").SetValue(trial,1,null);
                    ViveNamedTaskMemoryView view;string issue;Check.True(!ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue)&&view==null);
                }
            });
            suite.Run("named task snapshot preserves only completed ratings after technical end",()=>{
                var c=new Clock();var p=Make(c);GoToQuestionnaire(p,c);
                p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);
                Check.Equal(1,p.RatingCount);p.EndTechnical(Actor.Owner,"synthetic stop during questionnaire");
                ViveNamedTaskMemoryView view;string issue;Check.True(ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue),issue);
                Check.Equal(1,view.RatingRecordCount);Check.True(!view.CompleteSessionProven&&!view.EyeDataIncluded);
            });
            suite.Run("named in-memory view preserves every actual event and field across trial and rating rows",()=>{
                var c=new Clock();var p=Make(c);Complete(p,c);ViveNamedTaskMemoryView view;string issue;
                Check.True(ViveNamedTaskMemoryView.TryCreate(p,true,false,out view,out issue),issue);
                var events=p.ReadEventsAfter(-1,4096);Check.Equal(events.Count,view.EventCount);
                Check.True(view.TrialEventCount>0);Check.Equal(Questionnaire.Count,view.RatingEventCount);
                var rows=view.Csv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);int row=1;
                foreach(var e in events)
                {
                    var keys=new List<string>(e.Fields.Keys);keys.Sort(StringComparer.Ordinal);
                    if(keys.Count==0)keys.Add(null);
                    foreach(var key in keys)
                    {
                        var cells=rows[row++].Split(',');Check.Equal(20,cells.Length);Check.Equal(ViveNamedTaskMemoryView.Schema,cells[0]);
                        Check.Equal("u64:"+e.Sequence,cells[2]);Check.Equal(e.Time.ToString("R",System.Globalization.CultureInfo.InvariantCulture),Decode(cells[3]));
                        Check.Equal(e.ClockId,Decode(cells[4]));Check.Equal(e.SessionId,Decode(cells[5]));
                        Check.Equal("i32:"+e.BlockIndex,cells[6]);Check.Equal("i32:"+e.Segment,cells[7]);
                        Check.Equal(e.TrialId==null?"bool:false":"bool:true",cells[8]);if(e.TrialId!=null)Check.Equal(e.TrialId,Decode(cells[9]));
                        Check.Equal(e.Kind,Decode(cells[10]));Check.Equal(ViveNamedOptionProvenance.SchemaVersion,Decode(cells[11]));
                        Check.Equal(e.NamedOption.Option.ToString(),Decode(cells[12]));Check.Equal(e.NamedOption.ScheduleVersion,Decode(cells[13]));
                        Check.Equal(e.NamedOption.GeometryVersion,Decode(cells[14]));Check.Equal("i32:"+e.NamedOption.ListIndex,cells[15]);
                        if(key==null)Check.Equal("bool:false",cells[16]);else {Check.Equal("bool:true",cells[16]);Check.Equal(key,Decode(cells[17]));
                            var value=e.Fields[key];Check.Equal(value==null?"bool:false":"bool:true",cells[18]);if(value!=null)Check.Equal(value,Decode(cells[19]));}
                    }
                }
                Check.Equal(rows.Length,row);Check.True(!view.Csv.Contains("=synthetic"));
                var field=typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var experiment=(Experiment)field.GetValue(p);Check.Equal(experiment.Trials.Count,view.TrialRecordCount);Check.Equal(experiment.Ratings.Count,view.RatingRecordCount);
                var entries=new Dictionary<string,Dictionary<string,string>>();
                var recordRows=view.RecordsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);
                for(int i=1;i<recordRows.Length;i++)
                {
                    var cells=recordRows[i].Split(',');Check.Equal(6,cells.Length);Check.Equal(ViveNamedTaskMemoryView.RecordSchema,cells[0]);
                    string id=cells[1]+"/"+cells[2];Dictionary<string,string> values;
                    if(!entries.TryGetValue(id,out values)){values=new Dictionary<string,string>();entries.Add(id,values);}
                    values.Add(Decode(cells[3]),cells[4]=="bool:true"?Decode(cells[5]):null);
                }
                for(int i=0;i<experiment.Trials.Count;i++)
                {
                    var actual=experiment.Trials[i];var values=entries["trial/u64:"+i];
                    Check.Equal(actual.TrialId,values["trial_id"]);Check.Equal(actual.SessionId,values["session_id"]);Check.Equal(actual.ListId,values["list_id"]);
                    Check.Equal(actual.IsPractice.ToString(),values["is_practice"]);Check.Equal(actual.PracticeAttempt.ToString(),values["practice_attempt"]);
                    Check.Equal(actual.BlockIndex.ToString(),values["block_index"]);Check.Equal(actual.PlannedIndex.ToString(),values["planned_index"]);
                    Check.Equal(actual.Segment.ToString(),values["segment"]);Check.Equal(actual.Stimulus.Word.ToString(),values["word"]);Check.Equal(actual.Stimulus.Ink.ToString(),values["ink"]);
                    Check.Equal(actual.Onset.HasValue?actual.Onset.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture):null,values["onset_local_seconds"]);
                    Check.Equal(actual.ResponseAt.HasValue?actual.ResponseAt.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture):null,values["response_local_seconds"]);
                    Check.Equal(actual.Response.HasValue?actual.Response.Value.ToString():null,values["response"]);Check.Equal(actual.Correct.HasValue?actual.Correct.Value.ToString():null,values["correct"]);
                    Check.Equal(actual.ReactionSeconds.HasValue?actual.ReactionSeconds.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture):null,values["reaction_seconds"]);
                    Check.Equal(actual.Outcome.ToString(),values["outcome"]);Check.Equal(actual.TechnicalIssue.ToString(),values["technical_issue"]);
                    Check.Equal(actual.NamedOption.Option.ToString(),values["named_option"]);Check.Equal(actual.NamedOption.ScheduleVersion,values["named_schedule"]);
                    Check.Equal(actual.NamedOption.GeometryVersion,values["named_geometry"]);Check.Equal(actual.NamedOption.ListIndex.ToString(),values["named_list_index"]);
                }
                for(int i=0;i<experiment.Ratings.Count;i++)
                {
                    var actual=experiment.Ratings[i];var values=entries["rating/u64:"+i];
                    Check.Equal(actual.BlockIndex.ToString(),values["block_index"]);Check.Equal(actual.ItemId,values["item_id"]);Check.Equal(actual.Value.ToString(),values["value"]);
                    Check.Equal(actual.Time.ToString("R",System.Globalization.CultureInfo.InvariantCulture),values["time_local_seconds"]);
                    Check.Equal(actual.Language,values["language"]);Check.Equal(actual.Version,values["questionnaire_version"]);Check.True(!values.ContainsKey("named_option"));
                }
            });
        }
    }
}
