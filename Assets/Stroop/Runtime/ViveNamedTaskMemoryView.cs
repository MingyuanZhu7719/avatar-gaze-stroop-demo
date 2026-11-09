using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A bounded, explicitly requested view of the actual ended Simulation
    // participant's task events and task-owned record value snapshots. It is
    // neither a journal nor an export file; missing records are not inferred.
    public sealed class ViveNamedTaskMemoryView
    {
        public const string Schema="named-task-event-view-v1";
        public const string RecordSchema="named-task-record-view-v1";
        const int MaximumEvents=4096,MaximumFieldsPerEvent=256,MaximumRows=50000,MaximumOutputBytes=2*1024*1024;
        const string Header="preview_version,category,event_sequence_u64,event_time_f64_b64,clock_id_b64,session_id_b64,block_i32,segment_i32,trial_id_present,trial_id_b64,event_kind_b64,named_schema_b64,named_option_b64,schedule_b64,geometry_b64,list_index_i32,field_present,field_name_b64,value_present,value_b64\n";
        const string RecordHeader="preview_version,record_type,record_index_u64,field_name_utf8_b64,value_present,value_utf8_b64\n";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        readonly ViveDevelopmentParticipant participant;
        readonly string csv,recordCsv;
        readonly int eventCount,trialEventCount,ratingEventCount,trialCount,ratingCount;
        public string Csv {get{return participant.NamedViewStillAllowed(this)?csv:null;}}
        public int EventCount {get{return participant.NamedViewStillAllowed(this)?eventCount:0;}}
        public int TrialEventCount {get{return participant.NamedViewStillAllowed(this)?trialEventCount:0;}}
        public int RatingEventCount {get{return participant.NamedViewStillAllowed(this)?ratingEventCount:0;}}
        public string RecordsCsv {get{return participant.NamedViewStillAllowed(this)?recordCsv:null;}}
        public int TrialRecordCount {get{return participant.NamedViewStillAllowed(this)?trialCount:0;}}
        public int RatingRecordCount {get{return participant.NamedViewStillAllowed(this)?ratingCount:0;}}
        public bool CompleteSessionProven {get{return false;}}
        public bool EyeDataIncluded {get{return false;}}
        public bool Saved {get{return false;}}
        ViveNamedTaskMemoryView(ViveDevelopmentParticipant participant,string csv,string recordCsv,int count,int trial,int rating,int trials,int ratings)
        {this.participant=participant;this.csv=csv;this.recordCsv=recordCsv;eventCount=count;trialEventCount=trial;ratingEventCount=rating;trialCount=trials;ratingCount=ratings;}
        internal bool BelongsTo(ViveDevelopmentParticipant candidate){return Object.ReferenceEquals(participant,candidate);}
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        static string Enc(string value)
        {if(value==null||Utf8.GetByteCount(value)>65536)throw new InvalidOperationException("Field limit exceeded.");return "b64:"+Convert.ToBase64String(Utf8.GetBytes(value));}
        static string Signed(int value){return "i32:"+value.ToString(CultureInfo.InvariantCulture);}
        static void Row(StringBuilder target,ref int bytes,params string[] cells)
        {
            string row=String.Join(",",cells)+"\n";int n=Utf8.GetByteCount(row);
            if(n>MaximumOutputBytes-bytes)throw new InvalidOperationException("Output limit exceeded.");
            target.Append(row);bytes+=n;
        }
        // No path, stream, clipboard, eye data or device operation is involved.
        // The caller is responsible for clearing its reference when hidden.
        public static bool TryCreate(ViveDevelopmentParticipant participant,bool explicitAction,bool viewRunning,
            out ViveNamedTaskMemoryView view,out string issue)
        {return TryCreateCore(participant,explicitAction,viewRunning,null,out view,out issue);}
        // Only synthetic tests supply a hook; it runs outside participant locks.
        internal static bool TryCreateForTest(ViveDevelopmentParticipant participant,bool explicitAction,bool viewRunning,Action beforePublish,
            out ViveNamedTaskMemoryView view,out string issue)
        {return TryCreateCore(participant,explicitAction,viewRunning,beforePublish,out view,out issue);}
        static bool TryCreateCore(ViveDevelopmentParticipant participant,bool explicitAction,bool viewRunning,Action beforePublish,
            out ViveNamedTaskMemoryView view,out string issue)
        {
            view=null;issue=null;
            if(!explicitAction){issue="explicit_action_required";return false;}
            if(participant==null||!participant.CanStartNamedView()||participant.IsResearchSession||!participant.HasNamedOption||viewRunning||
                participant.Phase!=Phase.Ended||!participant.EndReason.HasValue||participant.EndReason.Value==EndReason.Withdrawn)
            {issue="ended_nonwithdrawn_named_simulation_required";return false;}
            try
            {
                int count=participant.AuthoritativeEventCount;
                if(count<1||count>MaximumEvents){issue="event_limit_or_empty";return false;}
                string session=participant.SessionId,clock=participant.EventClockId;
                ViveNamedTaskRecordSnapshot records;
                if(!participant.TryCopyEndedNamedTaskRecords(out records)||records==null)throw new InvalidOperationException();
                IReadOnlyList<JournalEvent> events=participant.ReadEventsAfter(-1,count);
                if(events==null||events.Count!=count)throw new InvalidOperationException();
                var plan=participant.NamedPlan;
                if(plan!=null)
                {
                    string eventCsv,planRecordCsv;int trialEvents,ratingEvents;
                    ViveNamedPlanTaskMemoryProjection.Render(plan,events,records,session,clock,out eventCsv,out planRecordCsv,out trialEvents,out ratingEvents);
                    if(participant.NamedPlan!=plan||participant.AuthoritativeEventCount!=count||
                        participant.TrialCount!=records.Trials.Length||participant.RatingCount!=records.Ratings.Length)throw new InvalidOperationException();
                    var planCandidate=new ViveNamedTaskMemoryView(participant,eventCsv,planRecordCsv,count,trialEvents,ratingEvents,records.Trials.Length,records.Ratings.Length);
                    if(beforePublish!=null)beforePublish();
                    if(!participant.TryPublishNamedView(planCandidate,out view)){issue="withdrawn_or_source_changed";return false;}
                    return true;
                }
                // This view is intentionally only the old single-block
                // named schema. A future plan must use a distinct reader.
                string scope;
                if(ViveNamedOptionProvenance.SchemaVersion!="vive-named-option-simulation-v1"||events[0]==null||
                    events[0].Kind!="session_created"||!events[0].Fields.TryGetValue("scope",out scope)||scope!="single_block_simulation_only")
                    throw new InvalidOperationException();
                var output=new StringBuilder(Header);int bytes=Utf8.GetByteCount(Header),trial=0,rating=0,rows=0;
                ViveNamedOptionProvenance provenance=null;double previous=-1;
                for(int i=0;i<events.Count;i++)
                {
                    var item=events[i];
                    if(item==null||item.Sequence!=i||item.SessionId!=session||item.ClockId!=clock||item.NamedOption==null||
                        item.Fields==null||item.Kind==null||Double.IsNaN(item.Time)||Double.IsInfinity(item.Time)||item.Time<previous)
                        throw new InvalidOperationException();
                    if(provenance==null)provenance=item.NamedOption;
                    else if(!Object.ReferenceEquals(provenance,item.NamedOption))throw new InvalidOperationException();
                    string typedSchema,typedOption,typedSchedule,typedGeometry;
                    if(!item.Fields.TryGetValue("named_schema",out typedSchema)||typedSchema!=ViveNamedOptionProvenance.SchemaVersion||
                        !item.Fields.TryGetValue("named_option",out typedOption)||typedOption!=provenance.Option.ToString()||
                        !item.Fields.TryGetValue("named_schedule",out typedSchedule)||typedSchedule!=provenance.ScheduleVersion||
                        !item.Fields.TryGetValue("named_geometry",out typedGeometry)||typedGeometry!=provenance.GeometryVersion)
                        throw new InvalidOperationException();
                    previous=item.Time;
                    string category=item.Kind=="rating"?"rating":item.TrialId!=null?"trial":"task";
                    if(category=="rating")rating++;else if(category=="trial")trial++;
                    if(item.Fields.Count>MaximumFieldsPerEvent||Math.Max(1,item.Fields.Count)>MaximumRows-rows)throw new InvalidOperationException();
                    rows+=Math.Max(1,item.Fields.Count);
                    var keys=new List<string>(item.Fields.Keys);keys.Sort(StringComparer.Ordinal);
                    var common=new[]{Schema,category,"u64:"+item.Sequence.ToString(CultureInfo.InvariantCulture),
                        Enc(item.Time.ToString("R",CultureInfo.InvariantCulture)),Enc(clock),Enc(session),Signed(item.BlockIndex),Signed(item.Segment),
                        item.TrialId==null?"bool:false":"bool:true",item.TrialId==null?"":Enc(item.TrialId),Enc(item.Kind),
                        Enc(ViveNamedOptionProvenance.SchemaVersion),Enc(provenance.Option.ToString()),Enc(provenance.ScheduleVersion),Enc(provenance.GeometryVersion),Signed(provenance.ListIndex)};
                    if(keys.Count==0)Row(output,ref bytes,Join(common,"bool:false","","bool:false",""));
                    foreach(var key in keys)
                    {
                        string value=item.Fields[key];Row(output,ref bytes,Join(common,"bool:true",Enc(key),
                            value==null?"bool:false":"bool:true",value==null?"":Enc(value)));
                    }
                }
                if(participant.Phase!=Phase.Ended||participant.EndReason==EndReason.Withdrawn||participant.AuthoritativeEventCount!=count)
                    throw new InvalidOperationException();
                string recordCsv=RenderRecords(records,session,clock,provenance);
                if(participant.AuthoritativeEventCount!=count||participant.TrialCount!=records.Trials.Length||participant.RatingCount!=records.Ratings.Length)
                    throw new InvalidOperationException();
                var candidate=new ViveNamedTaskMemoryView(participant,output.ToString(),recordCsv,count,trial,rating,records.Trials.Length,records.Ratings.Length);
                if(beforePublish!=null)beforePublish();
                if(!participant.TryPublishNamedView(candidate,out view)){issue="withdrawn_or_source_changed";return false;}
                return true;
            }
            catch(Exception e) when(!Fatal(e)){issue="source_invalid_or_limit";return false;}
        }
        static string[] Join(string[] common,params string[] tail)
        {var result=new string[common.Length+tail.Length];Array.Copy(common,result,common.Length);Array.Copy(tail,0,result,common.Length,tail.Length);return result;}
        static string Number(double? value){return value.HasValue?value.Value.ToString("R",CultureInfo.InvariantCulture):null;}
        static string Int(int value){return value.ToString(CultureInfo.InvariantCulture);}
        static void RecordField(StringBuilder target,ref int bytes,string kind,int index,string name,string value)
        {Row(target,ref bytes,RecordSchema,kind,"u64:"+Int(index),Enc(name),value==null?"bool:false":"bool:true",value==null?"":Enc(value));}
        static string RenderRecords(ViveNamedTaskRecordSnapshot records,string session,string clock,ViveNamedOptionProvenance provenance)
        {
            var output=new StringBuilder(RecordHeader);int bytes=Utf8.GetByteCount(RecordHeader);
            RecordField(output,ref bytes,"manifest",0,"session_id",session);
            RecordField(output,ref bytes,"manifest",0,"clock_id",clock);
            RecordField(output,ref bytes,"manifest",0,"scope","ended_simulation_task_snapshot_only");
            RecordField(output,ref bytes,"manifest",0,"eye_data","not_included");
            RecordField(output,ref bytes,"manifest",0,"coverage","unknown");
            for(int i=0;i<records.Trials.Length;i++)
            {
                var t=records.Trials[i];if(t==null||t.Session!=session||t.Id==null||t.List==null||
                    !Object.ReferenceEquals(t.Named,provenance)||(t.Practice?t.Block!=-1:t.Block!=0))throw new InvalidOperationException();
                RecordField(output,ref bytes,"trial",i,"trial_id",t.Id);RecordField(output,ref bytes,"trial",i,"session_id",t.Session);
                RecordField(output,ref bytes,"trial",i,"list_id",t.List);RecordField(output,ref bytes,"trial",i,"is_practice",t.Practice.ToString());
                RecordField(output,ref bytes,"trial",i,"practice_attempt",Int(t.Attempt));RecordField(output,ref bytes,"trial",i,"block_index",Int(t.Block));
                RecordField(output,ref bytes,"trial",i,"planned_index",Int(t.Planned));RecordField(output,ref bytes,"trial",i,"segment",Int(t.Segment));
                RecordField(output,ref bytes,"trial",i,"word",t.Word.ToString());RecordField(output,ref bytes,"trial",i,"ink",t.Ink.ToString());
                RecordField(output,ref bytes,"trial",i,"onset_local_seconds",Number(t.Onset));RecordField(output,ref bytes,"trial",i,"response_local_seconds",Number(t.ResponseAt));
                RecordField(output,ref bytes,"trial",i,"response",t.Response.HasValue?t.Response.Value.ToString():null);
                RecordField(output,ref bytes,"trial",i,"correct",t.Correct.HasValue?t.Correct.Value.ToString():null);
                RecordField(output,ref bytes,"trial",i,"reaction_seconds",Number(t.ReactionSeconds));RecordField(output,ref bytes,"trial",i,"outcome",t.Outcome.ToString());
                RecordField(output,ref bytes,"trial",i,"technical_issue",t.TechnicalIssue.ToString());
                RecordField(output,ref bytes,"trial",i,"named_schema",t.Named==null?null:ViveNamedOptionProvenance.SchemaVersion);
                RecordField(output,ref bytes,"trial",i,"named_option",t.Named==null?null:t.Named.Option.ToString());
                RecordField(output,ref bytes,"trial",i,"named_schedule",t.Named==null?null:t.Named.ScheduleVersion);
                RecordField(output,ref bytes,"trial",i,"named_geometry",t.Named==null?null:t.Named.GeometryVersion);
                RecordField(output,ref bytes,"trial",i,"named_list_index",t.Named==null?null:Int(t.Named.ListIndex));
            }
            for(int i=0;i<records.Ratings.Length;i++)
            {
                var r=records.Ratings[i];if(r==null||r.Block!=0||r.Item==null||r.Language==null||r.Version==null)throw new InvalidOperationException();
                RecordField(output,ref bytes,"rating",i,"block_index",Int(r.Block));RecordField(output,ref bytes,"rating",i,"item_id",r.Item);
                RecordField(output,ref bytes,"rating",i,"value",Int(r.Value));RecordField(output,ref bytes,"rating",i,"time_local_seconds",Number(r.Time));
                RecordField(output,ref bytes,"rating",i,"language",r.Language);RecordField(output,ref bytes,"rating",i,"questionnaire_version",r.Version);
            }
            return output.ToString();
        }
        public static string SliceForDisplay(string text,int offset,int maximumCharacters)
        {
            if(text==null||offset<0||offset>text.Length||maximumCharacters<1||maximumCharacters>8192)throw new ArgumentOutOfRangeException();
            return text.Substring(offset,Math.Min(maximumCharacters,text.Length-offset));
        }
    }
}
