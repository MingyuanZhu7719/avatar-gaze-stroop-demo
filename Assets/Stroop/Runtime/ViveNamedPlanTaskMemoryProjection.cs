using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // In-memory projection of the same ended task's immutable values. It does
    // not store, infer or join eye samples, and is not a durable journal.
    internal static class ViveNamedPlanTaskMemoryProjection
    {
        internal const string EventSchema="named-plan-task-event-view-v2";
        internal const string RecordSchema="named-plan-task-record-view-v2";
        const int MaxOutput=2*1024*1024,MaxRows=50000,MaxFields=256;
        const string Header="preview_version,record_type,record_index_u64,field_name_utf8_b64,value_present,value_utf8_b64\n";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static string E(string value)
        {if(value==null||Utf8.GetByteCount(value)>65536)throw new InvalidOperationException();return "b64:"+Convert.ToBase64String(Utf8.GetBytes(value));}
        static string N(int value){return value.ToString(CultureInfo.InvariantCulture);}
        static string T(double? value){return value.HasValue?value.Value.ToString("R",CultureInfo.InvariantCulture):null;}
        static void Field(StringBuilder b,ref int bytes,ref int rows,string schema,string kind,int index,string name,string value)
        {
            if(++rows>MaxRows)throw new InvalidOperationException();
            string line=String.Join(",",new[]{schema,kind,"u64:"+N(index),E(name),value==null?"bool:false":"bool:true",value==null?"":E(value)})+"\n";
            int n=Utf8.GetByteCount(line);if(n>MaxOutput-bytes)throw new InvalidOperationException();b.Append(line);bytes+=n;
        }
        static bool SameBlock(ViveNamedSimulationPlan plan,ViveNamedSimulationBlock block,int index)
        {return block!=null&&index>=0&&index<plan.Blocks.Count&&Object.ReferenceEquals(block.Plan,plan)&&
            block.Index==index&&Object.ReferenceEquals(block,plan.Blocks[index])&&block.Selection!=null;}
        internal static void Render(ViveNamedSimulationPlan plan,IReadOnlyList<JournalEvent> events,ViveNamedTaskRecordSnapshot records,
            string session,string clock,out string eventCsv,out string recordCsv,out int trialEvents,out int ratingEvents)
        {
            eventCsv=null;recordCsv=null;trialEvents=0;ratingEvents=0;
            if(ViveNamedSimulationPlan.SchemaVersion!="vive-named-simulation-plan-v2"||plan==null||plan.Blocks.Count<1||plan.Blocks.Count>4||events==null||events.Count<1||events.Count>4096||records==null||
                session==null||clock==null||events[0]==null||events[0].Kind!="session_created")throw new InvalidOperationException();
            string scope;
            if(!events[0].Fields.TryGetValue("scope",out scope)||scope!="explicit_plan_simulation_only")throw new InvalidOperationException();
            var eb=new StringBuilder(Header);int ebytes=Utf8.GetByteCount(Header),erows=0;double previous=-1;
            for(int i=0;i<events.Count;i++)
            {
                var item=events[i];string schema,planId,eventScope,participantEye;
                if(item==null||item.Sequence!=i||item.SessionId!=session||item.ClockId!=clock||item.Kind==null||item.Fields==null||
                    item.Fields.Count>MaxFields||item.NamedOption!=null||!Object.ReferenceEquals(item.NamedPlan,plan)||
                    Double.IsNaN(item.Time)||Double.IsInfinity(item.Time)||item.Time<previous||
                    !item.Fields.TryGetValue("named_schema",out schema)||schema!=ViveNamedSimulationPlan.SchemaVersion||
                    !item.Fields.TryGetValue("named_plan_id",out planId)||planId!=plan.PlanId||
                    !item.Fields.TryGetValue("named_scope",out eventScope)||eventScope!="explicit_plan_simulation_only"||
                    !item.Fields.TryGetValue("named_participant_eye",out participantEye)||participantEye!="NOT_RECORDED")throw new InvalidOperationException();
                if(item.NamedBlock==null)
                {
                    if(item.BlockIndex!=-1||item.Fields.ContainsKey("named_block_index")||item.Fields.ContainsKey("named_option")||
                        item.Fields.ContainsKey("named_list_index")||item.Fields.ContainsKey("named_block_started")||
                        item.Fields.ContainsKey("named_schedule")||item.Fields.ContainsKey("named_geometry")||
                        item.Fields.ContainsKey("named_source")||item.Fields.ContainsKey("named_avatar_aoi"))throw new InvalidOperationException();
                }
                else
                {
                    string index,option,list,schedule,geometry,started,source,aoi;
                    if(!SameBlock(plan,item.NamedBlock,item.BlockIndex)||
                        !item.Fields.TryGetValue("named_block_index",out index)||index!=N(item.NamedBlock.Index)||
                        !item.Fields.TryGetValue("named_option",out option)||option!=item.NamedBlock.Selection.Option.ToString()||
                        !item.Fields.TryGetValue("named_list_index",out list)||list!=N(item.NamedBlock.Selection.ListIndex)||
                        !item.Fields.TryGetValue("named_schedule",out schedule)||schedule!=item.NamedBlock.Selection.ScheduleVersion||
                        !item.Fields.TryGetValue("named_geometry",out geometry)||geometry!=item.NamedBlock.Selection.GeometryVersion||
                        !item.Fields.TryGetValue("named_block_started",out started)||(started!="True"&&started!="False")||
                        !item.Fields.TryGetValue("named_source",out source)||!item.Fields.TryGetValue("named_avatar_aoi",out aoi)||
                        source!=(item.NamedBlock.Selection.Option==ViveNamedOption.NoAvatar?ViveNamedOptionProvenance.NoAvatarNotApplicable:"AUTOMATIC")||
                        aoi!=(item.NamedBlock.Selection.Option==ViveNamedOption.NoAvatar?ViveNamedOptionProvenance.NoAvatarNotApplicable:"NOT_ASSESSED"))throw new InvalidOperationException();
                }
                previous=item.Time;if(item.Kind=="rating")ratingEvents++;else if(item.TrialId!=null)trialEvents++;
                Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"sequence",N(i));
                Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"time_local_seconds",T(item.Time));
                Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"session_id",session);
                Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"clock_id",clock);
                Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"block_index",N(item.BlockIndex));
                Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"segment",N(item.Segment));
                Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"trial_id",item.TrialId);
                Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"kind",item.Kind);
                Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"typed_plan_id",plan.PlanId);
                Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"typed_block_index",item.NamedBlock==null?null:N(item.NamedBlock.Index));
                Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"typed_block_semantics",item.NamedBlock==null?null:"assigned_not_proof_started");
                var keys=new List<string>(item.Fields.Keys);keys.Sort(StringComparer.Ordinal);
                foreach(string key in keys)Field(eb,ref ebytes,ref erows,EventSchema,"event",i,"source_field:"+key,item.Fields[key]);
            }
            var rb=new StringBuilder(Header);int rbytes=Utf8.GetByteCount(Header),rrows=0;
            Field(rb,ref rbytes,ref rrows,RecordSchema,"manifest",0,"session_id",session);
            Field(rb,ref rbytes,ref rrows,RecordSchema,"manifest",0,"clock_id",clock);
            Field(rb,ref rbytes,ref rrows,RecordSchema,"manifest",0,"plan_schema",ViveNamedSimulationPlan.SchemaVersion);
            Field(rb,ref rbytes,ref rrows,RecordSchema,"manifest",0,"plan_id",plan.PlanId);
            Field(rb,ref rbytes,ref rrows,RecordSchema,"manifest",0,"scope","ended_simulation_task_snapshot_only");
            Field(rb,ref rbytes,ref rrows,RecordSchema,"manifest",0,"coverage","unknown");
            Field(rb,ref rbytes,ref rrows,RecordSchema,"manifest",0,"eye_data","not_included");
            Field(rb,ref rbytes,ref rrows,RecordSchema,"manifest",0,"plan_items","planned_only_not_proof_of_occurrence");
            for(int i=0;i<plan.Blocks.Count;i++)
            {
                var block=plan.Blocks[i];if(!SameBlock(plan,block,i))throw new InvalidOperationException();var selection=block.Selection;
                Field(rb,ref rbytes,ref rrows,RecordSchema,"planned_block",i,"option",selection.Option.ToString());
                Field(rb,ref rbytes,ref rrows,RecordSchema,"planned_block",i,"list_index",N(selection.ListIndex));
                Field(rb,ref rbytes,ref rrows,RecordSchema,"planned_block",i,"schedule",selection.ScheduleVersion);
                Field(rb,ref rbytes,ref rrows,RecordSchema,"planned_block",i,"geometry",selection.GeometryVersion);
            }
            for(int i=0;i<records.Trials.Length;i++)
            {
                var t=records.Trials[i];if(t==null||t.Session!=session||t.Id==null||t.List==null||t.Named!=null||!Object.ReferenceEquals(t.Plan,plan)||
                    (t.Practice?(t.Block!=-1||t.NamedBlock!=null):!SameBlock(plan,t.NamedBlock,t.Block)))throw new InvalidOperationException();
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"trial_id",t.Id);Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"session_id",t.Session);
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"list_id",t.List);Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"is_practice",t.Practice.ToString());
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"practice_attempt",N(t.Attempt));Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"block_index",N(t.Block));
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"planned_index",N(t.Planned));Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"segment",N(t.Segment));
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"word",t.Word.ToString());Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"ink",t.Ink.ToString());
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"onset_local_seconds",T(t.Onset));Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"response_local_seconds",T(t.ResponseAt));
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"response",t.Response.HasValue?t.Response.Value.ToString():null);
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"correct",t.Correct.HasValue?t.Correct.Value.ToString():null);
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"reaction_seconds",T(t.ReactionSeconds));Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"outcome",t.Outcome.ToString());
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"technical_issue",t.TechnicalIssue.ToString());
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"typed_plan_id",plan.PlanId);
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"typed_block_index",t.NamedBlock==null?null:N(t.NamedBlock.Index));
                Field(rb,ref rbytes,ref rrows,RecordSchema,"trial",i,"typed_option",t.NamedBlock==null?null:t.NamedBlock.Selection.Option.ToString());
            }
            for(int i=0;i<records.Ratings.Length;i++)
            {
                var r=records.Ratings[i];if(r==null||r.Block<0||r.Block>=plan.Blocks.Count||r.Item==null||r.Language==null||r.Version==null)throw new InvalidOperationException();
                Field(rb,ref rbytes,ref rrows,RecordSchema,"rating",i,"block_index",N(r.Block));Field(rb,ref rbytes,ref rrows,RecordSchema,"rating",i,"item_id",r.Item);
                Field(rb,ref rbytes,ref rrows,RecordSchema,"rating",i,"value",N(r.Value));Field(rb,ref rbytes,ref rrows,RecordSchema,"rating",i,"time_local_seconds",T(r.Time));
                Field(rb,ref rbytes,ref rrows,RecordSchema,"rating",i,"language",r.Language);Field(rb,ref rbytes,ref rrows,RecordSchema,"rating",i,"questionnaire_version",r.Version);
            }
            eventCsv=eb.ToString();recordCsv=rb.ToString();
        }
    }
}
