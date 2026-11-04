using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Development-preview discovery only. A complete CSV snapshot is not a
    // completed experiment. No old export is moved, rewritten or inferred.
    public sealed class PreviewExportReceipt
    {
        const string Tag="ASPREVIEWEXPORT",Version="1";
        const string ManifestHeader="schema_version,protocol_version,software_version,session_id,run_kind,data_label,block_index,segment,trial_id,source,file_name,row_count,sha256,complete_bundle,session_completed,exporter_id";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static readonly string[] Files={"session.csv","allocations.csv","events.csv","event_fields.csv","trials.csv","ratings.csv",
            "operator_samples.csv","participant_samples.csv","gaze_windows.csv","gaze_intervals.csv","gaze_summary.csv",
            "avatar_presentations.csv","clock_audit.csv","transport_audit.csv"};
        static readonly StringComparison PathComparison=Path.DirectorySeparatorChar=='\\'?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        public string SessionId {get;private set;}
        public string RequestId {get;private set;}
        public string DirectoryPath {get;private set;}
        PreviewExportReceipt(string session,string request,string directory)
        {SessionId=session;RequestId=request;DirectoryPath=directory;}

        public static string ReceiptPath(string sessionDirectory,string requestId)
        {
            if(!OwnerCommandCodec.ValidId(requestId))throw new ArgumentException("A canonical export request ID is required","requestId");
            return Path.Combine(AbsoluteDirectory(sessionDirectory),"export-"+requestId+".receipt");
        }

        // The owner calls this only after Export returns success. Verification
        // and durable, create-new publication must also return before its ACK.
        public static void WriteCompleted(string sessionDirectory,string sessionId,string requestId,ExportResult result)
        {
            WireCodec.RequireIdentity(sessionId);
            string destination=ReceiptPath(sessionDirectory,requestId);
            if(result==null||!result.Success)throw new InvalidOperationException("A successful complete CSV export is required for a receipt");
            string directory=ExportDirectory(sessionDirectory,result.DirectoryPath);
            string digest=VerifyBundle(directory,sessionId);
            try
            {
                byte[] bytes=Encoding.ASCII.GetBytes(String.Join("|",new[]{Tag,Version,Encode(sessionId),requestId,Encode(directory),digest})+"\n");
                if(bytes.Length>16384)throw new InvalidDataException("Export receipt exceeds its size limit");
                string staging=destination+".pending-"+Guid.NewGuid().ToString("N");
                using(var stream=new FileStream(staging,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                File.Move(staging,destination);
            }
            catch(Exception error) when(PreviewRecovery.ExpectedFailure(error))
            {throw new IOException("CSV bundle saved at "+directory+"; export receipt publication failed; no automatic retry.",error);}
        }

        public static bool TryReadVerified(string sessionDirectory,string sessionId,string requestId,out PreviewExportReceipt receipt,out string reason)
        {
            receipt=null;reason=null;
            try
            {
                WireCodec.RequireIdentity(sessionId);
                string path=ReceiptPath(sessionDirectory,requestId);RejectLink(path);
                byte[] bytes=ReadBounded(path,16384);
                if(bytes.Length==0||bytes[bytes.Length-1]!=10)throw new InvalidDataException("Export receipt is incomplete");
                for(int i=0;i<bytes.Length-1;i++)if(bytes[i]<33||bytes[i]>126)throw new InvalidDataException("Export receipt contains invalid bytes");
                string[] fields=Encoding.ASCII.GetString(bytes,0,bytes.Length-1).Split('|');
                if(fields.Length!=6||fields[0]!=Tag||fields[1]!=Version||fields[2]!=Encode(sessionId)||fields[3]!=requestId||!OwnerCommandCodec.ValidHash(fields[5]))
                    throw new InvalidDataException("Export receipt does not match this session and request");
                string supplied=Decode(fields[4]),directory=ExportDirectory(sessionDirectory,supplied);
                if(directory!=supplied)throw new InvalidDataException("Export receipt path is not canonical");
                if(VerifyBundle(directory,sessionId)!=fields[5])throw new InvalidDataException("Export manifest changed after its receipt");
                receipt=new PreviewExportReceipt(sessionId,requestId,directory);return true;
            }
            catch(Exception error) when(PreviewRecovery.ExpectedFailure(error)||error is FormatException)
            {reason="CSV export could not be verified: "+error.GetType().Name+": "+error.Message;return false;}
        }

        static string AbsoluteDirectory(string path)
        {
            if(String.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path)||path.IndexOfAny(new[]{'\r','\n','"'})>=0)
                throw new ArgumentException("An absolute local preview directory is required");
            string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            if(!Directory.Exists(full))throw new DirectoryNotFoundException("The preview directory does not exist");
            RejectLink(full);return full;
        }
        static string ExportDirectory(string sessionDirectory,string directory)
        {
            string root=AbsoluteDirectory(sessionDirectory),full=AbsoluteDirectory(directory);
            string parent=Path.Combine(root,"exports");
            if(!String.Equals(Path.GetDirectoryName(full),parent,PathComparison)||!Path.GetFileName(full).StartsWith("export-",StringComparison.Ordinal))
                throw new InvalidDataException("CSV folder is not a direct export of this preview session");
            RejectLink(parent);return full;
        }
        static void RejectLink(string path)
        {if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked export paths are not accepted");}
        static string VerifyBundle(string directory,string sessionId)
        {
            string manifest=Path.Combine(directory,"manifest.csv");RejectLink(manifest);
            byte[] bytes=ReadBounded(manifest,65536);var rows=ParseCsv(Utf8.GetString(bytes));
            if(rows.Count!=Files.Length+1||String.Join(",",rows[0])!=ManifestHeader)
                throw new InvalidDataException("Export manifest does not contain the complete CSV schema");
            var remaining=new HashSet<string>(Files,StringComparer.Ordinal);
            for(int i=1;i<rows.Count;i++)
            {
                var row=rows[i];long count;
                if(row.Count!=16||row[0]!=AuditExporter.SchemaVersion||row[3]!=sessionId||row[4]!="Simulation"||row[5]!="SIMULATION"||
                    row[9]!="export_manifest"||!remaining.Remove(row[10])||!Int64.TryParse(row[11],NumberStyles.None,CultureInfo.InvariantCulture,out count)||
                    !OwnerCommandCodec.ValidHash(row[12])||row[13]!="true"||(row[14]!="true"&&row[14]!="false"))
                    throw new InvalidDataException("Export manifest identity, completeness or file entry is invalid");
                string file=Path.Combine(directory,row[10]);RejectLink(file);
                using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
                using(var sha=SHA256.Create())
                    if(Digest(sha.ComputeHash(stream))!=row[12])throw new InvalidDataException("Export CSV hash does not match: "+row[10]);
            }
            if(Directory.GetFiles(directory,"*.partial").Length!=0)throw new InvalidDataException("Export contains unfinished CSV files");
            using(var sha=SHA256.Create())return Digest(sha.ComputeHash(bytes));
        }
        static byte[] ReadBounded(string path,int maximum)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length<1||stream.Length>maximum)throw new InvalidDataException("Export evidence size is invalid");
                var bytes=new byte[(int)stream.Length];int offset=0;
                while(offset<bytes.Length){int read=stream.Read(bytes,offset,bytes.Length-offset);if(read==0)throw new EndOfStreamException("Export evidence changed while reading");offset+=read;}
                return bytes;
            }
        }
        // Strict CSV reader for the bounded manifest, including quoted identity
        // fields. The exported data files themselves are streamed into hashes.
        static List<List<string>> ParseCsv(string csv)
        {
            var rows=new List<List<string>>();var row=new List<string>();var cell=new StringBuilder();bool quoted=false,closed=false;
            for(int i=0;i<csv.Length;i++)
            {
                char c=csv[i];
                if(quoted)
                {
                    if(c=='"'){if(i+1<csv.Length&&csv[i+1]=='"'){cell.Append('"');i++;}else{quoted=false;closed=true;}}
                    else cell.Append(c);
                }
                else if(c==','||c=='\r'||c=='\n')
                {
                    row.Add(cell.ToString());cell.Length=0;closed=false;
                    if(c!=','){if(c=='\r'&&i+1<csv.Length&&csv[i+1]=='\n')i++;rows.Add(row);row=new List<string>();}
                }
                else if(c=='"'&&cell.Length==0&&!closed)quoted=true;
                else {if(closed||c=='"')throw new InvalidDataException("Malformed manifest CSV quoting");cell.Append(c);}
            }
            if(quoted||closed||cell.Length!=0||row.Count!=0)throw new InvalidDataException("Manifest CSV is incomplete");
            return rows;
        }
        static string Encode(string value){return Convert.ToBase64String(Utf8.GetBytes(value));}
        static string Decode(string value)
        {byte[] bytes=Convert.FromBase64String(value);if(Convert.ToBase64String(bytes)!=value)throw new InvalidDataException("Noncanonical receipt encoding");return Utf8.GetString(bytes);}
        static string Digest(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
    }
}
