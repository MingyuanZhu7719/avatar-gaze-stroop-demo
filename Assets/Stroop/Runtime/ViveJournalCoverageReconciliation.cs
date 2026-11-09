using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AvatarStroop.Runtime
{
    // All input records have already passed their version's reader. These
    // observations describe only the selected declared sources, not missing
    // files, continuous eye coverage, authenticated identity, or study success.
    internal sealed class ViveCoverageRecord
    {
        internal long Sequence;
        internal string Kind;
        internal IReadOnlyDictionary<string,string> Fields;
    }
    internal sealed class ViveCoverageSource
    {
        internal string Schema,OperatorDeclaration;
        internal ViveDevelopmentJournalReadStatus Status;
        internal long VerifiedBytes;
        internal int Records,Eye,InitialUnknown,RecoveryUnknown,Unavailable,TailUnknown,ExplicitGap,Unclassified;
        internal bool DeclaredPracticeStart,ObservedPracticeBoundary;
        internal readonly int[] NamedPresent=new int[ViveSelectedSessionIntegrity.NamedKeys.Length];
        internal readonly int[] NamedEmpty=new int[ViveSelectedSessionIntegrity.NamedKeys.Length];
        internal readonly int[] NamedNull=new int[ViveSelectedSessionIntegrity.NamedKeys.Length];
        internal readonly int[] NamedMissing=new int[ViveSelectedSessionIntegrity.NamedKeys.Length];
        internal readonly List<ViveCoverageTaskMarker> Markers=new List<ViveCoverageTaskMarker>();
    }
    internal sealed class ViveCoverageTaskMarker
    {
        internal long Sequence;
        internal int Block;
        internal string Kind;
        internal string RetainedDeclarationFingerprint;
        internal string TerminalCategory;
        internal bool ComparedFieldsPresent;
    }
    internal static class ViveJournalCoverageReconciliation
    {
        static string Get(IReadOnlyDictionary<string,string> fields,string name)
        {string value;return fields.TryGetValue(name,out value)?value:null;}
        static void FingerprintField(StringBuilder target,string value,bool present)
        {
            if(!present){target.Append("missing;");return;}
            if(value==null){target.Append("null;");return;}
            target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        }
        static string Fingerprint(ViveCoverageRecord record,bool practice,string detail)
        {
            // Compares retained declarations only. It is not authentication,
            // hardware provenance, or comparison of every source-record field.
            var text=new StringBuilder();
            FingerprintField(text,detail,true);
            string value;bool has;
            // V3 declares practice scope in its header but the event record
            // carries no block field. Never synthesize a v2 block declaration.
            if(practice)FingerprintField(text,null,false);
            else {has=record.Fields.TryGetValue("block_index",out value);FingerprintField(text,value,has);}
            has=record.Fields.TryGetValue(practice?"task_segment":"segment",out value);FingerprintField(text,value,has);
            has=record.Fields.TryGetValue("trial_id",out value);FingerprintField(text,value,has);
            has=record.Fields.TryGetValue("observed_local_seconds",out value);FingerprintField(text,value,has);
            using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }
        static string TerminalCategory(ViveAuthoritativeTaskPayload payload)
        {
            if(payload.Kind!="session_ended")return null;
            string reason;if(!payload.Fields.TryGetValue("reason",out reason))return "Unknown";
            return reason=="Completed"||reason=="Withdrawn"||reason=="TechnicalEnd"||reason=="PracticeFailed"?reason:"Unknown";
        }
        internal static ViveCoverageSource Observe(string schema,ViveDevelopmentJournalReadStatus status,long verified,IReadOnlyList<ViveCoverageRecord> rows)
        {
            var source=new ViveCoverageSource{Schema=schema,Status=status,VerifiedBytes=verified,Records=rows.Count};
            bool practice=schema=="vive-practice-journal-v3";
            source.OperatorDeclaration=practice?"not_recorded_in_v3":Get(rows[0].Fields,"operator_status");
            foreach(var row in rows)
            {
                if(row.Kind=="header"&&practice)
                    source.DeclaredPracticeStart=Get(row.Fields,"practice_start_binding")=="CALLER_DECLARED";
                if(row.Kind=="observation"||row.Kind=="eye_observation")source.Eye++;
                if(row.Kind=="initial_coverage_unknown"||row.Kind=="initial_unknown")source.InitialUnknown++;
                if(row.Kind=="recovery_coverage_unknown"||row.Kind=="recovery_unknown")source.RecoveryUnknown++;
                if(row.Kind=="unavailable_detected")source.Unavailable++;
                if(row.Kind=="tail_unknown")source.TailUnknown++;
                if(row.Kind=="gap")source.ExplicitGap++;
                bool boundary=practice&&row.Kind=="practice_start_boundary";
                if(row.Kind!="task_event"&&!boundary)continue;
                if(!practice&&Get(row.Fields,"event_kind")!="AuthoritativeEvent"){source.Unclassified++;continue;}
                ViveAuthoritativeTaskPayload payload;
                if(!ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(Get(row.Fields,"detail"),out payload))
                {source.Unclassified++;continue;}
                int block=-1;
                if(!practice&&!Int32.TryParse(Get(row.Fields,"block_index"),NumberStyles.Integer,CultureInfo.InvariantCulture,out block))
                {source.Unclassified++;continue;}
                if(boundary)source.ObservedPracticeBoundary=true;
                for(int i=0;i<ViveSelectedSessionIntegrity.NamedKeys.Length;i++)
                {
                    string value;
                    if(!payload.Fields.TryGetValue(ViveSelectedSessionIntegrity.NamedKeys[i],out value))source.NamedMissing[i]++;
                    else if(value==null)source.NamedNull[i]++;
                    else if(value.Length==0)source.NamedEmpty[i]++;
                    else source.NamedPresent[i]++;
                }
                source.Markers.Add(new ViveCoverageTaskMarker{Sequence=payload.Sequence,Block=block,Kind=payload.Kind,
                    RetainedDeclarationFingerprint=Fingerprint(row,practice,Get(row.Fields,"detail")),TerminalCategory=TerminalCategory(payload),
                    ComparedFieldsPresent=!practice&&row.Fields.ContainsKey("block_index")&&row.Fields.ContainsKey("segment")&&
                        row.Fields.ContainsKey("trial_id")&&row.Fields.ContainsKey("observed_local_seconds")});
            }
            return source;
        }
        internal static string Render(IReadOnlyList<ViveCoverageSource> sources)
        {
            var result=new StringBuilder();
            result.AppendLine("SELECTED-SOURCE RECONCILIATION (not a full session export)");
            result.AppendLine("Unselected files and before/after coverage are unknown. IDs, clock and task origins are declared, not authenticated.");
            result.AppendLine("Compared declarations: data source, session, participant, device, local clock and mapping. Operator ID and status are not compared across sources; v3 does not record them.");
            result.AppendLine("Markers are counts, not deduplicated outcomes; no absent trial, successful response, or scientific validity is inferred.");
            var seen=new SortedSet<long>();int repeats=0,practiceStart=0,practiceEnd=0,sessionEnd=0,unclassified=0;
            var blockConfig=new int[4];var blockEnd=new int[4];var questionEnd=new int[4];var ratings=new int[4];var planned=new int[4];var responses=new int[4];var interrupted=new int[4];
            for(int i=0;i<sources.Count;i++)
            {
                var s=sources[i];
                result.Append("Source ").Append(i+1).Append(": ").Append(s.Schema).Append("; status ").Append(s.Status)
                    .Append("; verified bytes ").Append(s.VerifiedBytes).Append("; records ").Append(s.Records)
                    .Append("; eye observations ").Append(s.Eye).Append("; initial/recovery unknown ").Append(s.InitialUnknown).Append('/').Append(s.RecoveryUnknown)
                    .Append("; unavailable detections ").Append(s.Unavailable).Append("; tail unknown ").Append(s.TailUnknown)
                    .Append("; explicit gaps ").Append(s.ExplicitGap).Append("; unclassified task markers ").Append(s.Unclassified)
                    .Append("; operator status declaration ").Append(s.OperatorDeclaration??"unknown").AppendLine();
                if(s.Schema=="vive-practice-journal-v3")
                    result.Append("  Practice start declared in header: ").Append(s.DeclaredPracticeStart)
                        .Append("; start-boundary record observed: ").Append(s.ObservedPracticeBoundary).AppendLine(". These are caller-declared.");
                unclassified+=s.Unclassified;
                foreach(var m in s.Markers)
                {
                    if(!seen.Add(m.Sequence))repeats++;
                    if(m.Kind=="practice_started")practiceStart++;
                    else if(m.Kind=="practice_completed")practiceEnd++;
                    else if(m.Kind=="session_ended")sessionEnd++;
                    if(m.Block<0||m.Block>=4)continue;
                    if(m.Kind=="block_configuration_locked")blockConfig[m.Block]++;
                    else if(m.Kind=="block_task_ended")blockEnd[m.Block]++;
                    else if(m.Kind=="questionnaire_completed")questionEnd[m.Block]++;
                    else if(m.Kind=="rating")ratings[m.Block]++;
                    else if(m.Kind=="trial_planned")planned[m.Block]++;
                    else if(m.Kind=="first_response")responses[m.Block]++;
                    else if(m.Kind=="trial_interrupted")interrupted[m.Block]++;
                }
            }
            int ranges=0;long previous=0;bool hasPrevious=false;
            foreach(long seq in seen){if(hasPrevious&&seq>previous&&seq-previous>1)ranges++;previous=seq;hasPrevious=true;}
            result.Append("Declared simulation task markers: ").Append(seen.Count+repeats).Append("; repeated sequence occurrences: ").Append(repeats)
                .Append("; unobserved sequence ranges between selected markers: ").Append(ranges)
                .Append("; unclassified: ").Append(unclassified).AppendLine(". No ordering or completeness is proven.");
            result.Append("Practice start/end markers: ").Append(practiceStart).Append('/').Append(practiceEnd)
                .Append("; session-end markers: ").Append(sessionEnd).AppendLine(".");
            for(int i=0;i<4;i++)
                result.Append("Block ").Append(i+1).Append(": configuration/task-end/questionnaire-end markers ")
                    .Append(blockConfig[i]).Append('/').Append(blockEnd[i]).Append('/').Append(questionEnd[i])
                    .Append("; trial-planned/first-response/interrupted/rating markers ").Append(planned[i]).Append('/').Append(responses[i]).Append('/').Append(interrupted[i]).Append('/').Append(ratings[i]).AppendLine(".");
            result.AppendLine("Complete session, continuous tracking, durable storage, trial alignment across devices and research validity: UNKNOWN.");
            return result.ToString();
        }
    }
}
