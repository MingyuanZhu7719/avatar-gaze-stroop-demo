using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AvatarStroop.Runtime
{
    // A bounded, in-memory view of records already accepted by a strict v2/v3
    // reader. One row is one observed event or explicit coverage marker. It
    // never joins eye frames to trials, fills missing trials, reads paths,
    // writes files, or establishes a complete/valid session.
    internal static class ViveSelectedJournalEventCsv
    {
        internal const string Header="preview_version,source_index_u64,journal_token,source_schema_utf8_b64,source_status,verified_bytes_u64,record_sequence_u64,record_kind_utf8_b64,decode_status,source_event_kind,declared_task_sequence,declared_event_kind,block_index,segment,trial_id,observed_local_seconds,phase,word,ink,list,planned_index,response,correct,onset,rt_seconds,condition,selection,gap_start_local_seconds,gap_end_local_seconds,gap_reason,observed_time_source_field,list_index,selection_origin,confirmation_block_index,confirmation_event_sequence,named_schema,named_option,named_schedule,named_geometry,named_source,named_avatar_aoi,named_participant_eye,named_phase,named_active_seconds,named_scope\n";
        const string Version="selected-journal-observed-event-v1";
        const int MaximumBytes=2*1024*1024;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static string B64(string value){return "b64:"+Convert.ToBase64String(Utf8.GetBytes(value));}
        static string U64(long value){if(value<0)throw new InvalidDataException();return "u64:"+value.ToString(CultureInfo.InvariantCulture);}
        static string Optional(IReadOnlyDictionary<string,string> fields,string key)
        {
            string value;if(fields==null||!fields.TryGetValue(key,out value))return "state:missing";
            return value==null?"state:null":B64(value);
        }
        static bool IsCoverage(string kind)
        {
            return kind=="initial_coverage_unknown"||kind=="recovery_coverage_unknown"||kind=="gap"||
                kind=="initial_unknown"||kind=="recovery_unknown"||kind=="tail_unknown"||kind=="unavailable_detected";
        }
        static void Add(StringBuilder output,ref int bytes,string[] values)
        {
            string line=String.Join(",",values)+"\n";int size=Utf8.GetByteCount(line);
            if(size>MaximumBytes-bytes)throw new InvalidDataException("Event preview limit exceeded.");
            output.Append(line);bytes+=size;
        }
        static string CoverageField(ViveCoverageRecord row,string legacy,string current)
        {return Optional(row.Fields,row.Kind=="gap"?legacy:current);}
        internal static string AppendSource(string existing,int sourceIndex,string token,string schema,
            ViveDevelopmentJournalReadStatus status,long verified,IReadOnlyList<ViveCoverageRecord> rows)
        {
            if(existing==null||token==null||schema==null||rows==null)throw new ArgumentNullException();
            var output=new StringBuilder(existing.Length==0?Header:existing);int bytes=Utf8.GetByteCount(output.ToString());
            bool practice=schema=="vive-practice-journal-v3";
            foreach(var row in rows)
            {
                bool task=row.Kind=="task_event"||row.Kind=="practice_start_boundary",coverage=IsCoverage(row.Kind);
                if(!task&&!coverage)continue;
                ViveAuthoritativeTaskPayload payload=null;string decode="state:not_task";
                if(task)
                {
                    string detail=null;bool candidate=(practice||Optional(row.Fields,"event_kind")==B64("AuthoritativeEvent"))&&
                        row.Fields.TryGetValue("detail",out detail);
                    if(candidate&&ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(detail,out payload))
                        decode="state:simulation_development_declared";
                    else decode=candidate?"state:undecodable":"state:unclassified";
                }
                IReadOnlyDictionary<string,string> eventFields=payload==null?null:payload.Fields;
                string sourceKind=Optional(row.Fields,"event_kind");
                string taskSequence=payload==null?"state:missing":B64(payload.Sequence.ToString(CultureInfo.InvariantCulture));
                string eventKind=payload==null?"state:missing":B64(payload.Kind);
                string start="state:missing",end="state:missing",reason="state:missing";
                if(coverage)
                {
                    if(row.Kind=="initial_coverage_unknown"||row.Kind=="recovery_coverage_unknown")
                    {start=Optional(row.Fields,"coverage_start_local_seconds");end=Optional(row.Fields,"coverage_end_local_seconds");reason=Optional(row.Fields,"coverage_reason_detected");}
                    else if(row.Kind=="unavailable_detected")reason=Optional(row.Fields,"reason");
                    else {start=CoverageField(row,"gap_start_local_seconds","start_local_seconds");end=CoverageField(row,"gap_end_local_seconds","end_local_seconds");reason=CoverageField(row,"gap_reason","reason");}
                }
                string observedKey=row.Kind=="unavailable_detected"?"detected_local_seconds":"observed_local_seconds";
                string ignored;
                string observed=row.Fields.TryGetValue(observedKey,out ignored)?Optional(row.Fields,observedKey):"state:missing";
                string observedSource=row.Fields.ContainsKey(observedKey)?B64(observedKey):"state:missing";
                Add(output,ref bytes,new[]{Version,U64(sourceIndex),"id:"+token,B64(schema),"status:"+status,U64(verified),U64(row.Sequence),B64(row.Kind),decode,
                    sourceKind,taskSequence,eventKind,Optional(row.Fields,"block_index"),Optional(row.Fields,practice?"task_segment":"segment"),
                    Optional(row.Fields,"trial_id"),observed,Optional(row.Fields,"phase"),Optional(eventFields,"word"),Optional(eventFields,"ink"),
                    Optional(eventFields,"list"),Optional(eventFields,"planned_index"),Optional(eventFields,"response"),Optional(eventFields,"correct"),
                    Optional(eventFields,"onset"),Optional(eventFields,"rt_seconds"),Optional(eventFields,"condition"),Optional(eventFields,"selection"),start,end,reason,
                    observedSource,Optional(eventFields,"list_index"),Optional(eventFields,"selection_origin"),Optional(eventFields,"confirmation_block_index"),
                    Optional(eventFields,"confirmation_event_sequence"),Optional(eventFields,"named_schema"),Optional(eventFields,"named_option"),
                    Optional(eventFields,"named_schedule"),Optional(eventFields,"named_geometry"),Optional(eventFields,"named_source"),
                    Optional(eventFields,"named_avatar_aoi"),Optional(eventFields,"named_participant_eye"),Optional(eventFields,"named_phase"),
                    Optional(eventFields,"named_active_seconds"),Optional(eventFields,"named_scope")});
            }
            return output.ToString();
        }
    }
}
