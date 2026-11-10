using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Internal development analysis candidate. Output MUST remain in a
    // caller-owned private stage until scan, current authorization, source
    // identity, withdrawal and publication are independently established.
    // This component opens no paths and cannot grant any of those authorities.
    internal static class ViveRawReadableAnalysisCsv
    {
        internal enum Outcome { WrittenAndFlushed, Rejected, Cancelled, OutputUncertain }
        internal static bool ProductionGateOpen {get{return ViveControlledRecordingBuildPolicy.Available;}}
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        // VRAW3/VRAW4 observation fields. Manifest/task rows preserve the
        // reader's explicit design variant and all configuration fields.
        // Every recognized eye field has both a
        // human-readable column and an exact type-prefixed raw column. New
        // observation fields must be added here rather than silently dropped.
        static readonly string[] EyeFields={
            "native_frame","native_timestamp","eye_observed","head_sequence","head_observed","checked_at","loss_generation",
            "runtime","calibration","eye_connected","native_result_present","native_result","runtime_status_present","runtime_status","no_user",
            "left_present","left_mask","left_origin_valid","left_direction_valid","left_origin_x","left_origin_y","left_origin_z","left_direction_x","left_direction_y","left_direction_z",
            "right_present","right_mask","right_origin_valid","right_direction_valid","right_origin_x","right_origin_y","right_origin_z","right_direction_x","right_direction_y","right_direction_z",
            "combined_present","combined_mask","combined_origin_valid","combined_direction_valid","combined_origin_x","combined_origin_y","combined_origin_z","combined_direction_x","combined_direction_y","combined_direction_z",
            "head_connected","head_valid","head_rotation_x","head_rotation_y","head_rotation_z","head_rotation_w",
            "ingest_phase","ingest_block","ingest_segment","ingest_trial_id","task_cursor"};
        static readonly HashSet<string> EyeFieldSet=new HashSet<string>(EyeFields,StringComparer.Ordinal);
        static readonly HashSet<string> WozBitFields=new HashSet<string>(new[]{
            "woz_task_issued_participant_time","woz_challenge_issued_participant_time","woz_receipt_participant_time",
            "woz_send_operator_time","woz_head_operator_time","woz_eye_operator_time",
            "woz_head_qx","woz_head_qy","woz_head_qz","woz_head_qw","woz_eye_dx","woz_eye_dy","woz_eye_dz"},StringComparer.Ordinal);
        internal static Outcome Write(Stream input,Stream privateOutput,long maximumInputBytes,int maximumRecords,
            long maximumOutputBytes,CancellationToken cancellation)
        {
            if(!ProductionGateOpen)return Outcome.Rejected;
            return WriteCore(input,privateOutput,maximumInputBytes,maximumRecords,maximumOutputBytes,cancellation,false);
        }
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
        internal static Outcome WriteSyntheticForTest(Stream input,Stream privateOutput,long maximumInputBytes,int maximumRecords,
            long maximumOutputBytes,CancellationToken cancellation)
        {return WriteCore(input,privateOutput,maximumInputBytes,maximumRecords,maximumOutputBytes,cancellation,true);}
#endif
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        static string SafeText(string value)
        {
            if(String.IsNullOrEmpty(value))return value??String.Empty;
            // A leading apostrophe prevents common spreadsheet formula entry;
            // raw_value preserves the original encoding without modification.
            int p=0;while(p<value.Length&&value[p]==' ')p++;
            if(p<value.Length&&("=+-@\t\r\n".IndexOf(value[p])>=0))return "'"+value;
            return value;
        }
        static string Csv(string value){return "\""+(value??String.Empty).Replace("\"","\"\"")+"\"";}
        static string Human(string type,string raw)
        {
            if(type=="utf8_b64")return SafeText(Utf8.GetString(Convert.FromBase64String(raw)));
            if(type=="f64_bits_hex")
            {ulong bits=UInt64.Parse(raw,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture);return BitConverter.Int64BitsToDouble(unchecked((long)bits)).ToString("R",CultureInfo.InvariantCulture);}
            if(type=="bool")return raw=="1"?"true":"false";
            if(type=="null")return "NULL";
            if(type=="int"||type=="uint")return raw;
            return SafeText(raw);
        }
        static string Header()
        {
            var result=new StringBuilder("ordinal,record_kind,field,type,readable_value,raw_value,row_warning");
            foreach(string field in EyeFields)result.Append(',').Append(Csv("eye_"+field)).Append(',').Append(Csv("eye_"+field+"_raw"));
            return result.Append('\n').ToString();
        }
        static string Meta(string field,string value)
        {return "-1,export,"+field+",text,"+value+",,"+new string(',',EyeFields.Length*2)+"\n";}
        static string ExcelWarning(string human,string raw)
        {return human.Length>32767||raw.Length>32767?"EXCEL_CELL_TOO_LONG_USE_FULL_CSV_OR_VRAW":String.Empty;}
        static string ConvertFrame(string frame)
        {
            var result=new StringBuilder();int start=0;string ordinal=null,kind=null,payloadKey=null;bool woz=false;
            var eye=new Dictionary<string,string[]>(StringComparer.Ordinal);
            string padding=new string(',',EyeFields.Length*2);
            while(start<frame.Length)
            {
                int end=frame.IndexOf('\n',start);if(end<0)throw new InvalidDataException("Incomplete verified row.");
                string line=frame.Substring(start,end-start);start=end+1;
                string[] columns=line.Split(',');if(columns.Length!=5)throw new InvalidDataException("Invalid verified row.");
                if(ordinal==null){ordinal=columns[0];kind=columns[1];}
                if(columns[0]!=ordinal||columns[1]!=kind)throw new InvalidDataException("Mixed verified frame.");
                if(kind=="observation")
                {
                    if(!EyeFieldSet.Contains(columns[2])||eye.ContainsKey(columns[2]))throw new InvalidDataException("Unknown or repeated eye field.");
                    eye.Add(columns[2],columns);continue;
                }
                string readable=Human(columns[3],columns[4]),raw="raw:"+columns[4];
                if(kind=="manifest"&&columns[2]=="legacy_control_mode"&&readable=="OPERATOR_WOZ_PARTICIPANT_LOCAL_RECORDING")woz=true;
                // The verified V5 schema stores exact IEEE bits in task text.
                // Decode only its whitelisted numeric fields for readability;
                // keep the original UTF-8/base64 raw value byte-for-byte.
                if(kind=="task"&&columns[2].StartsWith("field_",StringComparison.Ordinal))
                {
                    if(columns[2].EndsWith("_key",StringComparison.Ordinal))payloadKey=readable;
                    else if(columns[2].EndsWith("_value",StringComparison.Ordinal)&&payloadKey!=null&&WozBitFields.Contains(payloadKey)&&readable.Length==16)
                        readable=Human("f64_bits_hex",readable);
                }
                result.Append(Csv(columns[0])).Append(',').Append(Csv(columns[1])).Append(',').Append(Csv(columns[2])).Append(',')
                    .Append(Csv(columns[3])).Append(',').Append(Csv(readable)).Append(',').Append(Csv(raw)).Append(',')
                    .Append(Csv(ExcelWarning(readable,raw))).Append(padding).Append('\n');
            }
            if(kind=="observation")
            {
                var fields=new StringBuilder();string warning=String.Empty;
                foreach(string field in EyeFields)
                {
                    string[] columns;
                    if(eye.TryGetValue(field,out columns))
                    {
                        string readable=Human(columns[3],columns[4]),raw="raw:"+columns[3]+":"+columns[4];
                        if(ExcelWarning(readable,raw).Length>0)warning="EXCEL_CELL_TOO_LONG_USE_FULL_CSV_OR_VRAW";
                        fields.Append(',').Append(Csv(readable)).Append(',').Append(Csv(raw));
                    }
                    else fields.Append(",\"MISSING\",\"raw:missing\"");
                }
                result.Append(Csv(ordinal)).Append(",\"observation\",\"eye_frame\",\"wide\",\"\",\"\",").Append(Csv(warning)).Append(fields).Append('\n');
            }
            if(woz){result.Append(Meta("remote_operator_cleanup","NOT_PROVEN_BY_PARTICIPANT_CAPTURE_TERMINAL"));
                result.Append(Meta("network_time_alignment","INDEPENDENT_ROLE_CLOCKS_NO_SYNCHRONIZATION_OR_LATENCY_ESTIMATE"));}
            return result.ToString();
        }
        static Outcome WriteCore(Stream input,Stream privateOutput,long maximumInputBytes,int maximumRecords,
            long maximumOutputBytes,CancellationToken cancellation,bool syntheticFixture)
        {
            if(input==null||privateOutput==null||Object.ReferenceEquals(input,privateOutput)||maximumOutputBytes<1024)return Outcome.Rejected;
            bool started=false,outputFailure=false;long outputBytes=0;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if(!input.CanRead||input.CanWrite||!privateOutput.CanWrite||!privateOutput.CanSeek)return Outcome.Rejected;
                if(privateOutput.Length!=0||privateOutput.Position!=0)return Outcome.Rejected;
                var writer=new StreamWriter(privateOutput,Utf8,16384,true);writer.NewLine="\n";
                Action<string> append=value=>{
                    cancellation.ThrowIfCancellationRequested();
                    long count=Utf8.GetByteCount(value);if(count>maximumOutputBytes-outputBytes)throw new IOException("Analysis staging limit reached.");
                    try{started=true;writer.Write(value);outputBytes+=count;}
                    catch(Exception e) when(!Fatal(e)){outputFailure=true;throw;}
                };
                append(Header());
                append(Meta("scope","VERIFIED_FRAMES_ONLY"));
                append(Meta("original_vraw","RETAIN_ORIGINAL_UNCHANGED"));
                append(Meta("native_timestamp","NATIVE_TIME_OPAQUE"));
                append(Meta("eye_context","INGEST_NOT_SAMPLE_ALIGNMENT"));
                append(Meta("eye_coverage","UNKNOWN"));
                append(Meta("source_and_consent","NOT_AUTHENTICATED"));
                append(Meta("spreadsheet_formula_escape","LEADING_APOSTROPHE_RAW_VALUE_UNMODIFIED"));
                append(Meta("raw_value_encoding","REMOVE_RAW_PREFIX_USE_TYPE_OR_EMBEDDED_TYPE"));
                append(Meta("layout","ONE_ROW_PER_EYE_FRAME_OTHER_RECORDS_LONG_FORM"));
                append(Meta("excel_limit","EXCEL_VIEW_MAY_TRUNCATE_USE_FULL_CSV_PARSER"));
                var scan=ViveRawSessionJournalReader.ScanAnalysisRows(input,maximumInputBytes,maximumRecords,cancellation,
                    rows=>append(ConvertFrame(rows)),syntheticFixture);
                if(outputFailure)return Outcome.OutputUncertain;
                if(scan.Status!=ViveRawSessionReadStatus.VerifiedPrefix||scan.TrailingFrameTruncated||!scan.CaptureTerminalObserved)
                    return started?Outcome.OutputUncertain:(scan.Status==ViveRawSessionReadStatus.Cancelled?Outcome.Cancelled:Outcome.Rejected);
                append(Meta("verified_chain_sha256",scan.VerifiedPrefixChainDigestHex));
                append(Meta("verified_record_count",scan.VerifiedRecordCount.ToString(CultureInfo.InvariantCulture)));
                append(Meta("writer_confirmation","UNKNOWN"));
                append(Meta("complete_session_proven","false"));
                // This row describes only the source scan. It is deliberately
                // not an output-complete marker: Flush can still fail and
                // withdrawal can race with an already issued write.
                append(Meta("source_terminal","DECLARED_AND_CHAIN_VERIFIED_NOT_EXPORT_SUCCESS"));
                cancellation.ThrowIfCancellationRequested();writer.Flush();
                if(cancellation.IsCancellationRequested||privateOutput.Length!=outputBytes||privateOutput.Position!=outputBytes)return Outcome.OutputUncertain;
                return Outcome.WrittenAndFlushed;
            }
            catch(OperationCanceledException){return started?Outcome.OutputUncertain:Outcome.Cancelled;}
            catch(Exception e) when(!Fatal(e)){return started?Outcome.OutputUncertain:Outcome.Rejected;}
        }
    }
}
