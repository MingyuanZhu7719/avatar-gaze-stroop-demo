using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Globalization;

namespace AvatarStroop.Runtime
{
    // Explicitly selected v2/v3 streams only; no path access, writing, clipboard,
    // whole-session coverage inference, source authentication or disk publication.
    public sealed class ViveJournalMemoryPreview
    {
        const int MaxSources=8, MaxRecords=5000, MaxOutputBytes=2*1024*1024;
        const long MaxInputBytes=4*1024*1024;
        const string ManifestHeader="preview_version,source_index_u64,journal_token,source_schema_utf8_b64,source_status,verified_bytes_u64,record_count_u64,operator_status_declaration,operator_id_and_status_compared,identity_authenticated,whole_session_proven,continuous_coverage_proven\n";
        const string FieldHeader="preview_version,source_index_u64,journal_token,record_sequence_u64,record_kind_utf8_b64,field_present,field_name_utf8_b64,value_present,value_utf8_b64\n";
        const string PreviewVersion="selected-source-v1";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        readonly HashSet<string> tokens=new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> streams=new HashSet<string>(StringComparer.Ordinal);
        readonly List<ViveCoverageSource> sources=new List<ViveCoverageSource>();
        string identity,preview="",report="",manifest="",fieldTable="",eventTable="";
        public int Count {get;private set;}
        public string Text {get{return preview;}}
        public string Report {get{return report;}}
        public string ManifestCsv {get{return manifest;}}
        public string FieldCsv {get{return fieldTable;}}
        public string EventCsv {get{return eventTable;}}
        public string Issue {get;private set;}
        public bool DiskExportAvailable {get{return false;}}
        public bool CompleteSessionProven {get{return false;}}

        sealed class Capture:Stream
        {
            readonly Stream input;readonly byte[] prefix=new byte[40];int captured;long read;
            public bool Failed {get;private set;}
            public Capture(Stream source){input=source;}
            public string Token {get{if(captured!=40)throw new InvalidDataException();var value=new byte[16];Buffer.BlockCopy(prefix,24,value,0,16);return new Guid(value).ToString("N");}}
            public override bool CanRead {get{return true;}} public override bool CanWrite {get{return false;}} public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}} public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] buffer,int offset,int count)
            {
                if(count==0)return 0;
                try {
                    if(read>=MaxInputBytes){var one=new byte[1];if(input.Read(one,0,1)!=0)throw new IOException();return 0;}
                    int n=input.Read(buffer,offset,(int)Math.Min(count,MaxInputBytes-read));
                    int copy=Math.Min(n,prefix.Length-captured);if(copy>0){Buffer.BlockCopy(buffer,offset,prefix,captured,copy);captured+=copy;}
                    read=checked(read+n);return n;
                } catch(Exception e) when(!Fatal(e)){Failed=true;throw new IOException("Input failed or exceeded limit.");}
            }
            public override void Flush(){throw new NotSupportedException();} public override long Seek(long a,SeekOrigin b){throw new NotSupportedException();}
            public override void SetLength(long a){throw new NotSupportedException();} public override void Write(byte[] a,int b,int c){throw new NotSupportedException();}
        }
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        static string Field(IReadOnlyDictionary<string,string> f,string k){string v;if(!f.TryGetValue(k,out v)||String.IsNullOrEmpty(v))throw new InvalidDataException();return v;}
        static string Identity(IReadOnlyDictionary<string,string> f)
        {
            var s=new StringBuilder();
            foreach(string key in new[]{"data_source","session_id","participant_id","device_id","local_clock_id","mapping_version"})
            {string v=Field(f,key);s.Append(v.Length).Append(':').Append(v);}
            return s.ToString();
        }
        static string Safe(string value)
        {
            if(value==null)return "<null>";
            var output=new StringBuilder("\"");
            foreach(char c in value)
            {
                var category=Char.GetUnicodeCategory(c);
                if(c=='\\'||c=='\"'){output.Append('\\').Append(c);continue;}
                if(Char.IsControl(c)||category==System.Globalization.UnicodeCategory.Format||
                    category==System.Globalization.UnicodeCategory.LineSeparator||category==System.Globalization.UnicodeCategory.ParagraphSeparator)
                {output.Append("\\u").Append(((int)c).ToString("x4",System.Globalization.CultureInfo.InvariantCulture));continue;}
                output.Append(c);
            }
            return output.Append('\"').ToString();
        }
        static void Append(StringBuilder target,string text,ref int bytes)
        {
            int count=Utf8.GetByteCount(text);
            if(count>MaxOutputBytes-bytes)throw new InvalidDataException();
            target.Append(text);bytes+=count;
        }
        static string Unsigned(long value)
        {if(value<0)throw new InvalidDataException();return "u64:"+value.ToString(CultureInfo.InvariantCulture);}
        static string Encoded(string value)
        {return "b64:"+Convert.ToBase64String(Utf8.GetBytes(value));}
        static void CsvRow(StringBuilder target,ref int bytes,params string[] cells)
        {
            // All cells are fixed safe tokens, decimal values prefixed with u64:,
            // or UTF-8 encoded with b64:. No raw value can become a spreadsheet
            // formula, date or a lossy large integer on import.
            Append(target,String.Join(",",cells)+"\n",ref bytes);
        }
        // The caller opens a read-only stream only after its explicit action. The
        // source's record order is preserved; no shared clock or completeness is inferred.
        public bool Add(Stream input,bool v3,bool explicitAction,bool recoverIncomplete)
        {
            Issue=null;
            if(!explicitAction){Issue="explicit_read_required";return false;}
            if(Count>=MaxSources){Issue="source_limit";return false;}
            try
            {
                if(input==null||!input.CanRead||input.CanWrite)throw new InvalidDataException();
                var capture=new Capture(input);var rows=new List<ViveCoverageRecord>();ViveDevelopmentJournalReadStatus status;long verified;
                if(v3)
                {
                    var result=VivePracticeJournalV3Reader.Read(capture,true,recoverIncomplete,MaxRecords,MaxInputBytes);
                    status=result.Status;verified=result.VerifiedBytes;
                    foreach(var r in result.Records)rows.Add(new ViveCoverageRecord{Sequence=r.Sequence,Kind=r.Kind,Fields=r.Fields});
                }
                else
                {
                    var result=ViveDevelopmentJournalReader.Read(capture,r=>{
                        if(rows.Count>=MaxRecords)throw new InvalidDataException();
                        rows.Add(new ViveCoverageRecord{Sequence=r.Sequence,Kind=r.Kind,Fields=r.Fields});
                    });
                    status=result.Status;verified=result.VerifiedBytes;
                }
                if(capture.Failed||status==ViveDevelopmentJournalReadStatus.ReadFailure||rows.Count==0||rows[0].Kind!="header")throw new InvalidDataException();
                if(status!=ViveDevelopmentJournalReadStatus.CleanClose&&!recoverIncomplete){Issue="incomplete_requires_recovery";return false;}
                var header=rows[0].Fields;
                string schema=Field(header,"schema");
                if((v3&&schema!="vive-practice-journal-v3")||(!v3&&schema!=ViveDevelopmentJournal.IdentitySchemaVersion))throw new InvalidDataException();
                if(!v3&&Field(header,"role")!="Participant")throw new InvalidDataException();
                string id=Identity(header),stream=Field(header,"stream_id"),token=capture.Token;
                if(identity!=null&&identity!=id){Issue="different_declared_identity";return false;}
                if(tokens.Contains(token)||streams.Contains(stream)){Issue="duplicate_source";return false;}
                var candidate=ViveJournalCoverageReconciliation.Observe(schema,status,verified,rows);
                var stagedSources=new List<ViveCoverageSource>(sources){candidate};
                string stagedReport=ViveJournalCoverageReconciliation.Render(stagedSources)+"\n"+
                    ViveSelectedSessionIntegrity.Render(stagedSources);
                string stagedEvents=ViveSelectedJournalEventCsv.AppendSource(eventTable,Count+1,token,schema,status,verified,rows);
                var next=new StringBuilder(preview);int outputBytes=Utf8.GetByteCount(preview);
                var nextManifest=new StringBuilder(manifest.Length==0?ManifestHeader:manifest);
                var nextTable=new StringBuilder(fieldTable.Length==0?FieldHeader:fieldTable);
                int manifestBytes=Utf8.GetByteCount(nextManifest.ToString()),tableBytes=Utf8.GetByteCount(nextTable.ToString());
                string sourceIndex=Unsigned(Count+1),journalToken="id:"+token;
                CsvRow(nextManifest,ref manifestBytes,PreviewVersion,sourceIndex,journalToken,Encoded(schema),"status:"+status,
                    Unsigned(verified),Unsigned(rows.Count),"status:"+candidate.OperatorDeclaration,"bool:false","bool:false","bool:false","bool:false");
                Append(next,"\n=== SELECTED SOURCE "+(Count+1)+" ===\nschema: "+Safe(schema)+"\njournal_token: "+token+
                    "\nsource_status: "+status+"\nverified_bytes: "+verified+
                    "\nIdentity, start/finish and coverage are declared or unknown; not authenticated or proven.\n",ref outputBytes);
                foreach(var row in rows)
                {
                    Append(next,"\nrecord "+row.Sequence+" | "+Safe(row.Kind)+"\n",ref outputBytes);
                    var keys=new List<string>(row.Fields.Keys);keys.Sort(StringComparer.Ordinal);
                    string sequence=Unsigned(row.Sequence),kind=Encoded(row.Kind);
                    if(keys.Count==0)
                        CsvRow(nextTable,ref tableBytes,PreviewVersion,sourceIndex,journalToken,sequence,kind,"bool:false","","bool:false","");
                    foreach(string key in keys)
                    {
                        string value=row.Fields[key];
                        Append(next,Safe(key)+" = "+Safe(value)+"\n",ref outputBytes);
                        CsvRow(nextTable,ref tableBytes,PreviewVersion,sourceIndex,journalToken,sequence,kind,
                            "bool:true",Encoded(key),value==null?"bool:false":"bool:true",value==null?"":Encoded(value));
                    }
                }
                preview=next.ToString();report=stagedReport;manifest=nextManifest.ToString();fieldTable=nextTable.ToString();eventTable=stagedEvents;
                identity=id;tokens.Add(token);streams.Add(stream);sources.Add(candidate);Count++;return true;
            }
            catch(Exception e) when(!Fatal(e)){Issue="input_invalid_or_limit";return false;}
        }
        // Clears references, not a secure memory wipe. No source is modified.
        public void Clear(){preview="";report="";manifest="";fieldTable="";eventTable="";identity=null;tokens.Clear();streams.Clear();sources.Clear();Count=0;Issue=null;}
    }
}
