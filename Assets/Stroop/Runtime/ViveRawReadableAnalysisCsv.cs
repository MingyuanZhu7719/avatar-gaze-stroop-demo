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
        static readonly string[] BaseEyeFields={
            "native_frame","native_timestamp","eye_observed","head_sequence","head_observed","checked_at","loss_generation",
            "runtime","calibration","eye_connected","native_result_present","native_result","runtime_status_present","runtime_status","no_user",
            "left_present","left_mask","left_origin_valid","left_direction_valid","left_origin_x","left_origin_y","left_origin_z","left_direction_x","left_direction_y","left_direction_z",
            "right_present","right_mask","right_origin_valid","right_direction_valid","right_origin_x","right_origin_y","right_origin_z","right_direction_x","right_direction_y","right_direction_z",
            "combined_present","combined_mask","combined_origin_valid","combined_direction_valid","combined_origin_x","combined_origin_y","combined_origin_z","combined_direction_x","combined_direction_y","combined_direction_z",
            "head_connected","head_valid","head_rotation_x","head_rotation_y","head_rotation_z","head_rotation_w",
            "ingest_phase","ingest_block","ingest_segment","ingest_trial_id","task_cursor"};
        static readonly string[] EyeFields=BuildEyeFields();
        static readonly HashSet<string> EyeFieldSet=new HashSet<string>(EyeFields,StringComparer.Ordinal);
        static readonly HashSet<string> BaseEyeFieldSet=new HashSet<string>(BaseEyeFields,StringComparer.Ordinal);
        static readonly HashSet<string> IngestOnlyFields=new HashSet<string>(new[]{"head_sequence","head_observed","checked_at","loss_generation",
            "head_connected","head_valid","head_rotation_x","head_rotation_y","head_rotation_z","head_rotation_w",
            "ingest_phase","ingest_block","ingest_segment","ingest_trial_id","task_cursor"},StringComparer.Ordinal);
        static string[] BuildEyeFields()
        {
            var fields=new List<string>(BaseEyeFields);
            fields.AddRange(new[]{"acquisition_version","acquisition_sequence","worker_published","eligible_polls","duplicate_polls","native_sequence_gaps","native_sequence_discontinuities",
                "poll_diagnostics_present","provider_poll_sequence","poll_started","poll_returned",
                "status_returned_present","status_returned","device_returned_present","device_returned","calibration_returned_present","calibration_returned",
                "read_started_present","read_started","read_returned_present","read_returned","status_result_present","status_result",
                "calibration_result_present","calibration_result","calibration_needed_present","calibration_needed","read_result_present","read_result",
                "aoi_extension_version","aoi_policy","aoi_coordinate_basis","aoi_pose_time_meaning","aoi_classification_meaning",
                "aoi_capture_local","aoi_capture_completed_local_present","aoi_capture_completed_local","aoi_unity_seconds","aoi_unity_frame",
                "aoi_scene_capture_local_present","aoi_scene_capture_local","aoi_scene_capture_completed_local_present","aoi_scene_capture_completed_local",
                "aoi_scene_unity_seconds_present","aoi_scene_unity_seconds","aoi_scene_unity_frame",
                "aoi_task_revision","aoi_avatar_revision","aoi_last_render_frame","aoi_last_render_unity_seconds_present","aoi_last_render_unity_seconds",
                "aoi_last_render_task_revision","aoi_last_render_avatar_revision","aoi_phase","aoi_block","aoi_trial","aoi_category","aoi_reason",
                "aoi_active_task","aoi_avatar_applicable","aoi_origin_valid","aoi_direction_valid","aoi_geometry_count","aoi_eye_to_pose_seconds","aoi_eye_to_scene_seconds","aoi_unity_clock_offset_seconds","aoi_scene_unity_clock_offset_seconds"});
            foreach(string name in new[]{"aoi_camera_position","aoi_world_origin","aoi_world_direction"})
                foreach(string axis in new[]{"x","y","z"})fields.Add(name+"_"+axis);
            foreach(string axis in new[]{"x","y","z","w"})fields.Add("aoi_camera_rotation_"+axis);
            for(int i=0;i<8;i++)
            {
                string prefix="aoi_geometry_"+i;fields.Add(prefix+"_name");fields.Add(prefix+"_enabled");
                foreach(string name in new[]{"position","scale"})foreach(string axis in new[]{"x","y","z"})fields.Add(prefix+"_"+name+"_"+axis);
                foreach(string axis in new[]{"x","y","z","w"})fields.Add(prefix+"_rotation_"+axis);
            }
            return fields.ToArray();
        }
        const int QualityColumns=5;
        internal static string EyeObservationLabel(int value)
        {if(value==0)return "NO_NEW_SAMPLE";if(value==1)return "MISSING";throw new InvalidDataException("Unknown eye observation.");}
        internal static string EyeReasonLabel(int value)
        {
            switch(value){case 0:return "DUPLICATE";case 1:return "FROZEN_OR_STALE";case 2:return "INVALID_QUALITY";
                case 3:return "NO_EYE";case 4:return "OUT_OF_ORDER";
                // The optional snapshot was busy or changed before coherence
                // revalidation. Neither case asserts physical sensor loss.
                case 5:return "SNAPSHOT_BUSY";default:throw new InvalidDataException("Unknown eye observation reason.");}
        }
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
            result.Append(",eye_observation,eye_observation_reason,eye_observation_owner_local_seconds,eye_observation_task_cursor,eye_observation_scope");
            return result.Append('\n').ToString();
        }
        static string Meta(string field,string value)
        {return "-1,export,"+field+",text,"+value+",,"+new string(',',EyeFields.Length*2+QualityColumns)+"\n";}
        static string ExcelWarning(string human,string raw)
        {return human.Length>32767||raw.Length>32767?"EXCEL_CELL_TOO_LONG_USE_FULL_CSV_OR_VRAW":String.Empty;}
        static string ConvertFrame(string frame)
        {
            var result=new StringBuilder();int start=0;string ordinal=null,kind=null,payloadKey=null;bool woz=false;
            var eye=new Dictionary<string,string[]>(StringComparer.Ordinal);
            var quality=new Dictionary<string,string[]>(StringComparer.Ordinal);
            string padding=new string(',',EyeFields.Length*2+QualityColumns);
            while(start<frame.Length)
            {
                int end=frame.IndexOf('\n',start);if(end<0)throw new InvalidDataException("Incomplete verified row.");
                string line=frame.Substring(start,end-start);start=end+1;
                string[] columns=line.Split(',');if(columns.Length!=5)throw new InvalidDataException("Invalid verified row.");
                if(ordinal==null){ordinal=columns[0];kind=columns[1];}
                if(columns[0]!=ordinal||columns[1]!=kind)throw new InvalidDataException("Mixed verified frame.");
                if(kind=="observation"||kind=="eye_acquisition")
                {
                    if(!EyeFieldSet.Contains(columns[2])||eye.ContainsKey(columns[2]))throw new InvalidDataException("Unknown or repeated eye field.");
                    eye.Add(columns[2],columns);continue;
                }
                if(kind=="eye_observation")
                {
                    string field=columns[2];
                    if((field!="owner_observed_local_seconds"&&field!="eye_observation"&&field!="reason"&&field!="task_cursor")||quality.ContainsKey(field))
                        throw new InvalidDataException("Unknown or repeated owner eye observation field.");
                    quality.Add(field,columns);continue;
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
            if(kind=="observation"||kind=="eye_acquisition")
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
                    // Unrelated domains have no value for this record kind.
                    // Empty cells are structural absence, never filled from a
                    // neighbouring native sample or rendered observation.
                    else if(BaseEyeFieldSet.Contains(field)&&(kind=="observation"||!IngestOnlyFields.Contains(field)))
                        fields.Append(",\"MISSING\",\"raw:missing\"");
                    else fields.Append(",,");
                }
                // Historical kind 2 may contain invalid or non-finite rays.
                // A recorded sample is not a declaration of valid coverage.
                result.Append(Csv(ordinal)).Append(',').Append(Csv(kind)).Append(",\"eye_frame\",\"wide\",\"\",\"\",").Append(Csv(warning)).Append(fields)
                    .Append(kind=="eye_acquisition"?",\"NATIVE_SAMPLE_RECORDED\",\"\",\"\",\"\",\"NATIVE_ACQUISITION_NO_HEAD_OR_TASK_ALIGNMENT\"\n":
                        ",\"SAMPLE_RECORDED\",\"\",\"\",\"\",\"RECORDED_SAMPLE_NOT_COVERAGE\"\n");
            }
            if(kind=="eye_observation")
            {
                if(quality.Count!=4||quality["owner_observed_local_seconds"][3]!="f64_bits_hex"||
                    quality["eye_observation"][3]!="int"||quality["reason"][3]!="int"||quality["task_cursor"][3]!="int")
                    throw new InvalidDataException("Incomplete or mistyped owner eye observation.");
                int state=Int32.Parse(quality["eye_observation"][4],CultureInfo.InvariantCulture),reason=Int32.Parse(quality["reason"][4],CultureInfo.InvariantCulture);
                long cursor=Int64.Parse(quality["task_cursor"][4],CultureInfo.InvariantCulture);
                string time=Human("f64_bits_hex",quality["owner_observed_local_seconds"][4]);
                double seconds=Double.Parse(time,CultureInfo.InvariantCulture);
                if(!ViveRawEyeObservation.Valid((ViveRawEyeObservation.Disposition)state,(ViveRawEyeObservation.Reason)reason,seconds,cursor))
                    throw new InvalidDataException("Invalid owner eye observation.");
                string raw="state:int:"+quality["eye_observation"][4]+";reason:int:"+quality["reason"][4]+
                    ";owner_time:f64_bits_hex:"+quality["owner_observed_local_seconds"][4]+";task_cursor:int:"+quality["task_cursor"][4];
                // One filterable owner-marker row; every gaze/native-ID cell
                // stays empty. Never forward-fill, fabricate samples, or infer
                // exact missing-sample counts or physical gap endpoints.
                result.Append(Csv(ordinal)).Append(",\"eye_observation\",\"eye_quality\",\"typed\",")
                    .Append(Csv(EyeObservationLabel(state))).Append(',').Append(Csv(raw)).Append(",\"NO_GAZE_VALUES_NO_FILL\"")
                    .Append(new string(',',EyeFields.Length*2)).Append(',').Append(Csv(EyeObservationLabel(state))).Append(',').Append(Csv(EyeReasonLabel(reason)))
                    .Append(',').Append(Csv(time)).Append(',').Append(Csv(cursor.ToString(CultureInfo.InvariantCulture)))
                    .Append(",\"OWNER_OBSERVATION_NOT_NATIVE_SAMPLE_COUNT_OR_INTERVAL\"\n");
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
                append(Meta("eye_quality_markers","OWNER_OBSERVATIONS_NOT_NATIVE_MISSING_SAMPLE_COUNTS_OR_EXACT_INTERVALS"));
                append(Meta("eye_quality_legacy_absence","UNKNOWN_NOT_ZERO_MISSING_SAMPLES"));
                append(Meta("eye_quality_fill","NONE_NO_FORWARD_FILL_OR_INTERPOLATION"));
                append(Meta("eye_quality_reader_compatibility","NEW_READER_ACCEPTS_OLD_FILES_OLD_READERS_REJECT_UNKNOWN_KINDS_OR_REASONS"));
                append(Meta("eye_snapshot_busy","OWNER_SNAPSHOT_CONTENTION_OR_COHERENCE_CHANGE_NOT_PHYSICAL_SENSOR_LOSS"));
                append(Meta("native_acquisition","ONE_UNIQUE_NATIVE_ID_PER_WORKER_ACQUISITION_INCLUDING_INVALID_QUALITY"));
                append(Meta("native_sequence_gaps","COUNTER_DELTAS_NOT_PROVEN_PHYSICAL_SENSOR_LOSS"));
                append(Meta("native_clock_alignment","OPAQUE_NATIVE_TIMESTAMP_WITH_SAME_PROCESS_API_CALL_BRACKETS_NO_PHOTON_ONSET"));
                append(Meta("aoi_association","CURRENT_UNITY_POSE_AND_DECLARED_SCENE_SOFTWARE_TIMES_NOT_NATIVE_CAPTURE_POSE"));
                append(Meta("aoi_absence","MISSING_NOT_ZERO_DWELL_NO_FORWARD_FILL"));
                append(Meta("blank_extension_cells","NOT_RECORDED_OR_NOT_APPLICABLE_FOR_THIS_RECORD_KIND_CHECK_PRESENT_FLAGS_AND_EXTENSION_VERSION"));
                append(Meta("aoi_validity","SURFACE_PROXY_CLASSIFICATION_REQUIRES_APPARATUS_ACCURACY_AND_OCCLUSION_VALIDATION"));
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
                append(Meta("verified_owner_eye_quality_records",scan.VerifiedEyeQualityRecords.ToString(CultureInfo.InvariantCulture)));
                append(Meta("verified_native_acquisition_records",scan.VerifiedAcquisitionRecords.ToString(CultureInfo.InvariantCulture)));
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
