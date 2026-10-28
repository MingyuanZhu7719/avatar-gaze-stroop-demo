using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AvatarStroop.Core
{
    public sealed class JournalEvidenceResult
    {
        public bool Success {get;internal set;}
        public string DirectoryPath {get;internal set;}
        public string Error {get;internal set;}
        public JournalReadResult ReadResult {get;internal set;}
    }

    // Offline evidence extraction only. No Experiment is constructed or resumed.
    public static class JournalEvidenceExporter
    {
        public const string SchemaVersion="avatar-stroop-journal-extraction-1";
        public static JournalEvidenceResult Export(string journalPath,string parentDirectory)
        {
            if(String.IsNullOrWhiteSpace(journalPath)||String.IsNullOrWhiteSpace(parentDirectory))throw new ArgumentException("Explicit source and destination paths required");
            var result=new JournalEvidenceResult();
            try
            {
                // Offline source: permit readers but do not share with an active writer.
                using(var source=new FileStream(journalPath,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    Directory.CreateDirectory(parentDirectory);
                    string directory=Path.Combine(parentDirectory,"journal-extraction-"+Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);result.DirectoryPath=directory;
                    long fieldRows=0;
                    using(var recordFile=New(directory,"records.csv.partial"))
                    using(var fieldFile=New(directory,"fields.csv.partial"))
                    using(var records=new StreamWriter(recordFile,new UTF8Encoding(false),4096,true))
                    using(var fields=new StreamWriter(fieldFile,new UTF8Encoding(false),4096,true))
                    {
                        Row(records,"record_sequence","record_kind","field_count");Row(fields,"record_sequence","field_name","is_null","field_value");
                        result.ReadResult=JournalPrefixReader.Read(source,record=>{
                            Row(records,record.Sequence,record.Kind,record.Fields.Count);
                            foreach(var field in record.Fields){Row(fields,record.Sequence,field.Key,field.Value==null,field.Value);fieldRows++;}
                        });
                        records.Flush();fields.Flush();recordFile.Flush(true);fieldFile.Flush(true);
                    }
                    File.Move(Path.Combine(directory,"records.csv.partial"),Path.Combine(directory,"records.csv"));
                    File.Move(Path.Combine(directory,"fields.csv.partial"),Path.Combine(directory,"fields.csv"));
                    using(var prefix=New(directory,"verified-prefix.asj.partial"))
                    {
                        source.Position=0;long remaining=result.ReadResult.VerifiedBytes;var buffer=new byte[16384];
                        while(remaining>0){int count=source.Read(buffer,0,(int)Math.Min(buffer.Length,remaining));if(count==0)throw new IOException("Source changed during extraction");prefix.Write(buffer,0,count);remaining-=count;}
                        prefix.Flush(true);
                    }
                    File.Move(Path.Combine(directory,"verified-prefix.asj.partial"),Path.Combine(directory,"verified-prefix.asj"));
                    using(var reportFile=New(directory,"recovery.csv.partial"))
                    using(var report=new StreamWriter(reportFile,new UTF8Encoding(false),4096,true))
                    {
                        Row(report,"schema_version","session_id","role","data_label","journal_status","read_error","verified_bytes","record_count","clean_close_marker_observed",
                            "source_bytes","runtime_flush_acknowledgement","session_completion_interpretation");
                        var read=result.ReadResult;var id=read.Identity;
                        Row(report,SchemaVersion,id==null?null:id.SessionId,id==null?null:id.Role,id==null?null:id.DataLabel,read.Status,read.Error,read.VerifiedBytes,read.RecordCount,
                            read.CleanCloseMarkerObserved,source.Length,"unavailable_from_journal_bytes;consult_external_runtime_ack_evidence",
                            "only_explicit_session_observation_may_report_completion;close_marker_does_not");
                        report.Flush();reportFile.Flush(true);
                    }
                    File.Move(Path.Combine(directory,"recovery.csv.partial"),Path.Combine(directory,"recovery.csv"));
                    using(var manifestFile=New(directory,"manifest.csv.partial"))
                    using(var manifest=new StreamWriter(manifestFile,new UTF8Encoding(false),4096,true))
                    {
                        Row(manifest,"schema_version","file_name","sha256","row_count","extraction_finalized");
                        Manifest(manifest,directory,"records.csv",result.ReadResult.RecordCount);Manifest(manifest,directory,"fields.csv",fieldRows);
                        Manifest(manifest,directory,"recovery.csv",1);Manifest(manifest,directory,"verified-prefix.asj",null);
                        manifest.Flush();manifestFile.Flush(true);
                    }
                    File.Move(Path.Combine(directory,"manifest.csv.partial"),Path.Combine(directory,"manifest.csv"));
                    result.Success=true;
                }
            }
            catch(Exception error) when(ContinuousJournal.IsStorageError(error)||error is ArgumentException)
            {result.Error=error.GetType().Name+": "+error.Message;}
            return result;
        }
        static FileStream New(string directory,string name){return new FileStream(Path.Combine(directory,name),FileMode.CreateNew,FileAccess.Write,FileShare.None);}
        static void Manifest(TextWriter writer,string directory,string name,long? rows)
        {
            string hash;using(var file=File.OpenRead(Path.Combine(directory,name)))using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
            Row(writer,SchemaVersion,name,hash,rows,true);
        }
        static void Row(TextWriter writer,params object[] values)
        {
            for(int i=0;i<values.Length;i++)
            {
                if(i>0)writer.Write(',');object value=values[i];string s=value==null?"":value is bool?((bool)value?"true":"false"):Convert.ToString(value,CultureInfo.InvariantCulture);
                if(s.IndexOfAny(new[]{',','"','\r','\n'})>=0)writer.Write("\""+s.Replace("\"","\"\"")+"\"");else writer.Write(s);
            }
            writer.Write("\r\n");
        }
    }
}
