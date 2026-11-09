using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AvatarStroop.Runtime
{
    // In-memory development evidence only. Extraction does not read files,
    // publish CSVs, fill gaps, validate hardware timing or assert study completion.
    public sealed class ViveDevelopmentJournalExtractionResult
    {
        public string Csv {get;internal set;}
        public ViveDevelopmentJournalReadStatus SourceStatus {get;internal set;}
        public long VerifiedBytes {get;internal set;}
        public long EvidenceRows {get;internal set;}
        // Random writer-generated journal token, not a hardware/person ID or signature.
        public string SourceJournalId {get;internal set;}
        public bool SourceCleanClose {get{return SourceStatus==ViveDevelopmentJournalReadStatus.CleanClose;}}
    }

    public static class ViveDevelopmentJournalExtraction
    {
        public const string SchemaVersion="vive-development-journal-extract-v1";
        public const string IdentitySchemaVersion="vive-development-journal-extract-v2";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static readonly string[] Identity={"data_source","session_id","participant_id","operator_id","role","stream_id","device_id","local_clock_id","mapping_version","hardware_capture_time_verified","apparatus_mapping_validated"};
        static readonly string[] IdentityV2={"data_source","session_id","participant_id","operator_status","operator_id","role","stream_id","device_id","local_clock_id","mapping_version","hardware_capture_time_verified","apparatus_mapping_validated"};
        static readonly string[] Evidence={"ordinal","block_index","segment","poll_started_local_seconds","poll_completed_local_seconds","head_observed_local_seconds","eye_observed_local_seconds","native_eye_timestamp_opaque","native_frame_sequence","native_combined_mask","native_combined_direction_reported_valid","raw_combined_sdk_direction_x","raw_combined_sdk_direction_y","raw_combined_sdk_direction_z","derived_direction_frame","derived_direction_x","derived_direction_y","derived_direction_z","eye_usable","gap_start_local_seconds","gap_end_local_seconds","gap_reason","event_kind","observed_local_seconds","phase","trial_id","detail"};
        static readonly HashSet<string> Numeric=new HashSet<string>(new[]{"ordinal","block_index","segment","poll_started_local_seconds","poll_completed_local_seconds","head_observed_local_seconds","eye_observed_local_seconds","native_eye_timestamp_opaque","native_frame_sequence","native_combined_mask","raw_combined_sdk_direction_x","raw_combined_sdk_direction_y","raw_combined_sdk_direction_z","derived_direction_x","derived_direction_y","derived_direction_z","gap_start_local_seconds","gap_end_local_seconds","observed_local_seconds"},StringComparer.Ordinal);

        // Reader validates the first frame, including its 16-byte stream token.
        // Retain only its fixed-size prefix as it reads, including for non-seekable
        // streams. This wrapper never closes or rewinds the caller's input.
        sealed class HeaderCaptureStream : Stream
        {
            readonly Stream input;readonly byte[] prefix=new byte[40];int captured;
            public HeaderCaptureStream(Stream input){this.input=input;}
            public override bool CanRead {get{return input.CanRead;}}
            public override bool CanSeek {get{return false;}}
            public override bool CanWrite {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}
            public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] buffer,int offset,int count)
            {
                int read=input.Read(buffer,offset,count),copy=Math.Min(read,prefix.Length-captured);
                if(copy>0){Buffer.BlockCopy(buffer,offset,prefix,captured,copy);captured+=copy;}return read;
            }
            public string VerifiedJournalId()
            {
                if(captured!=prefix.Length)throw new InvalidDataException("Verified journal token is unavailable");
                var token=new byte[16];Buffer.BlockCopy(prefix,24,token,0,16);return new Guid(token).ToString("N");
            }
            public override void Flush(){throw new NotSupportedException();}
            public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long value){throw new NotSupportedException();}
            public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
        }

        // allowIncomplete is a deliberate recovery action: every resulting row
        // retains the source's actual read status. Even CleanClose does not mean
        // that all trials completed or the experiment was valid.
        public static ViveDevelopmentJournalExtractionResult Extract(Stream input,bool allowIncomplete,int maximumRows,int maximumUtf8Bytes)
        {
            if(input==null||!input.CanRead)throw new ArgumentException("A readable development journal stream is required", "input");
            if(maximumRows<1||maximumRows>1000000)throw new ArgumentOutOfRangeException("maximumRows");
            if(maximumUtf8Bytes<1||maximumUtf8Bytes>268435456)throw new ArgumentOutOfRangeException("maximumUtf8Bytes");
            var records=new List<ViveDevelopmentJournalRecord>();long heldBytes=0;
            var capturedInput=new HeaderCaptureStream(input);
            var result=ViveDevelopmentJournalReader.Read(capturedInput,record=>{
                if(records.Count>=maximumRows+2)throw new InvalidDataException("Journal extraction row limit exceeded");
                foreach(var pair in record.Fields){heldBytes=checked(heldBytes+Utf8.GetByteCount(pair.Key)+(pair.Value==null?0:Utf8.GetByteCount(pair.Value)));}
                if(heldBytes>maximumUtf8Bytes)throw new InvalidDataException("Journal extraction input limit exceeded");
                records.Add(record);
            });
            if(records.Count==0||records[0].Kind!="header")throw new InvalidDataException("No verified development journal header");
            if(result.Status==ViveDevelopmentJournalReadStatus.ReadFailure)throw new IOException("Journal input could not be read completely");
            if(result.Status!=ViveDevelopmentJournalReadStatus.CleanClose&&!allowIncomplete)
                throw new InvalidDataException("Journal did not close cleanly; explicit verified-prefix recovery is required");
            // Only consult this token after the reader has accepted the header.
            string journalId=capturedInput.VerifiedJournalId();
            var identity=records[0].Fields;
            bool versionTwo=Value(identity,"schema")==ViveDevelopmentJournal.IdentitySchemaVersion;
            var identityColumns=versionTwo?IdentityV2:Identity;
            var output=new StringBuilder();long outputBytes=0;var header=new List<string>{"extraction_schema","source_status","source_verified_bytes","source_record_sequence","source_record_kind","source_journal_id"};
            header.AddRange(identityColumns);header.AddRange(Evidence);
            if(versionTwo){header.Add("task_origin_declared");header.Add("identity_authenticated");header.Add("coverage_start_local_seconds");header.Add("coverage_end_local_seconds");header.Add("coverage_reason_detected");}
            Append(output,header,maximumUtf8Bytes,ref outputBytes);
            long rows=0;
            foreach(var record in records)
            {
                if(record.Kind=="header"||record.Kind=="close")continue;
                rows++;if(rows>maximumRows)throw new InvalidDataException("Journal extraction row limit exceeded");
                var values=new List<string>{versionTwo?IdentitySchemaVersion:SchemaVersion,result.Status.ToString(),result.VerifiedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),record.Kind,journalId};
                foreach(var key in identityColumns)values.Add(Value(identity,key));
                foreach(var key in Evidence)values.Add(Value(record.Fields,key));
                if(versionTwo)
                {
                    string origin=null;
                    if(record.Kind=="task_event")
                    {
                        if(Value(record.Fields,"event_kind")=="AuthoritativeEvent")
                        {
                            ViveAuthoritativeTaskPayload payload;
                            if(!ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(Value(record.Fields,"detail"),out payload))
                                throw new InvalidDataException("V2 authoritative task event has missing or invalid task provenance");
                            origin="SIMULATION_DEVELOPMENT";
                        }
                        else
                        {
                            if(Value(identity,"data_source")=="RealVive")throw new InvalidDataException("Real-eye journal task event lacks explicit provenance");
                            origin="SYNTHETIC_UNSPECIFIED";
                        }
                    }
                    values.Add(origin);
                    // Header identities and AE2 task origin are declarations.
                    // The unkeyed chain does not authenticate either source.
                    values.Add("false");
                    values.Add(Value(record.Fields,"coverage_start_local_seconds"));
                    values.Add(Value(record.Fields,"coverage_end_local_seconds"));
                    values.Add(Value(record.Fields,"coverage_reason_detected"));
                }
                for(int i=6;i<values.Count;i++)
                {
                    string key=i<6+identityColumns.Length?identityColumns[i-6]:i<6+identityColumns.Length+Evidence.Length?
                        Evidence[i-6-identityColumns.Length]:i==6+identityColumns.Length+Evidence.Length?"task_origin_declared":
                        i==7+identityColumns.Length+Evidence.Length?"identity_authenticated":
                        i==10+identityColumns.Length+Evidence.Length?"coverage_reason_detected":"coverage_time";
                    if(!Numeric.Contains(key)&&key!="coverage_time")SafeSpreadsheetText(values[i]);
                }
                Append(output,values,maximumUtf8Bytes,ref outputBytes);
            }
            // A header-only CSV would discard source status and identity if the
            // string were saved separately. Do not emit apparently usable evidence.
            if(rows==0)throw new InvalidDataException("No verified evidence rows to extract");
            return new ViveDevelopmentJournalExtractionResult{Csv=output.ToString(),SourceStatus=result.Status,VerifiedBytes=result.VerifiedBytes,EvidenceRows=rows,SourceJournalId=journalId};
        }
        static string Value(IReadOnlyDictionary<string,string> fields,string key)
        {string value;return fields.TryGetValue(key,out value)?value:null;}
        static void SafeSpreadsheetText(string value)
        {
            if(String.IsNullOrEmpty(value))return;
            int i=0;while(i<value.Length&&Char.IsWhiteSpace(value[i]))i++;
            if(i<value.Length&&(value[i]=='='||value[i]=='+'||value[i]=='-'||value[i]=='@'))
                throw new InvalidDataException("Unsafe spreadsheet text prefix; extraction was not produced");
        }
        static void Append(StringBuilder output,List<string> values,int limit,ref long outputBytes)
        {
            var line=new StringBuilder();
            for(int i=0;i<values.Count;i++)
            {if(i>0)line.Append(',');line.Append('"').Append((values[i]??"").Replace("\"","\"\"")).Append('"');}
            line.Append('\n');
            long next=checked(outputBytes+Utf8.GetByteCount(line.ToString()));
            if(next>limit)
                throw new InvalidDataException("Journal extraction output limit exceeded");
            output.Append(line);outputBytes=next;
        }
    }
}
