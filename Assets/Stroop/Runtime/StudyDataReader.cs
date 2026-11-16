#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AvatarStroop.Runtime
{
    public sealed class StudyDataRecord
    {
        public long Ordinal {get; internal set;}
        public string Kind {get; internal set;}
        // Decoded original values, not spreadsheet-escaped display text.
        // Null is distinct from an empty string; byte arrays remain base64.
        public IDictionary<string,string> Fields {get; internal set;}
    }

    public sealed class StudyDataReadSummary
    {
        public string Format {get; internal set;}
        public string Status {get; internal set;}
        public string Message {get; internal set;}
        public long RecordCount {get; internal set;}
        public bool SourceTerminalObserved {get; internal set;}
        public bool TrailingFrameTruncated {get; internal set;}
        public string VerifiedChainSha256 {get; internal set;}
        public bool ChainVerified {get; internal set;}
        public bool Accepted {get{return Status=="Complete"||Status=="Incomplete";}}
    }

    // File selection and output publication belong to the offline app. The
    // callback receives a provisional private prefix: retain nothing publicly
    // if the final result is rejected, cancelled or exceeds a limit. This class
    // never opens paths, writes input, interpolates samples or uploads data.
    public static class StudyDataReader
    {
        public const long MaximumInputBytes=8589934592L;
        public const int MaximumRecords=25000000;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static readonly string[] CoreHeader={"ordinal","record_kind","field","type","readable_value","raw_value","row_warning"};
        sealed class InputLimit:IOException {}
        sealed class TruncatedCsv:IOException {}

        public static StudyDataReadSummary Read(Stream input,string format,Action<StudyDataRecord> accept,
            CancellationToken cancellation,long maximumInputBytes=MaximumInputBytes,int maximumRecords=MaximumRecords)
        {
            if(accept==null)throw new ArgumentNullException("accept");
            if(maximumInputBytes<8||maximumInputBytes>MaximumInputBytes||maximumRecords<1||maximumRecords>MaximumRecords)
                throw new ArgumentOutOfRangeException("Explicit bounded limits required.");
            string selected=(format??String.Empty).Trim().TrimStart('.').ToLowerInvariant();
            var result=new StudyDataReadSummary{Format=selected,Status="Rejected",Message="Unsupported input format; select a participant .vraw or readable analysis .csv file."};
            if(selected!="vraw"&&selected!="csv")return result;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if(input==null||!input.CanRead||input.CanWrite)
                {result.Message="Input must be an existing read-only stream.";return result;}
                if(input.CanSeek&&input.Position!=0)
                {result.Message="Input must be positioned at its first byte.";return result;}
                if(selected=="vraw")
                {
                    var scan=ViveRawSessionJournalReader.ScanOfflineStudyRows(input,maximumInputBytes,maximumRecords,cancellation,
                        frame=>accept(DecodeFrame(frame)));
                    result.RecordCount=scan.VerifiedRecordCount;
                    result.SourceTerminalObserved=scan.CaptureTerminalObserved;
                    result.TrailingFrameTruncated=scan.TrailingFrameTruncated;
                    result.VerifiedChainSha256=scan.VerifiedPrefixChainDigestHex;
                    result.ChainVerified=scan.Status==ViveRawSessionReadStatus.VerifiedPrefix;
                    result.Status=scan.Status==ViveRawSessionReadStatus.VerifiedPrefix?
                        (scan.CaptureTerminalObserved&&!scan.TrailingFrameTruncated?"Complete":"Incomplete"):
                        scan.Status==ViveRawSessionReadStatus.Cancelled?"Cancelled":
                        scan.Status==ViveRawSessionReadStatus.LimitExceeded?"LimitExceeded":
                        scan.Status==ViveRawSessionReadStatus.ReadFailure?"ReadFailure":"Rejected";
                    result.Message=result.Status=="Complete"?"VRAW chain and source terminal verified; recording coverage and eye accuracy are separate checks.":
                        result.Status=="Incomplete"?"Verified VRAW prefix only: the source has no verified terminal or a truncated trailing frame.":
                        "VRAW verification did not succeed: "+scan.Status+". Discard provisional records.";
                    return result;
                }
                using(var bounded=new LimitedInput(input,maximumInputBytes,cancellation))
                using(var text=new StreamReader(bounded,Utf8,true,65536,true))
                    ReadCsv(new CsvRows(text,cancellation),accept,result,maximumRecords,cancellation);
            }
            catch(OperationCanceledException){result.Status="Cancelled";result.Message="Analysis cancelled; discard provisional records.";}
            catch(InputLimit){result.Status="LimitExceeded";result.Message="Input, record or CSV field limit exceeded; discard provisional records.";}
            catch(TruncatedCsv){result.Status="Rejected";result.TrailingFrameTruncated=true;result.Message="CSV ends inside a quoted field; discard provisional records.";}
            catch(Exception error) when(error is InvalidDataException||error is FormatException||error is OverflowException||error is DecoderFallbackException||error is ArgumentException)
            {result.Status="Rejected";result.Message="Invalid readable analysis CSV: "+error.Message+" Discard provisional records.";}
            catch(Exception error) when(error is IOException||error is NotSupportedException||error is ObjectDisposedException)
            {result.Status="ReadFailure";result.Message="Input could not be read; discard provisional records. "+error.Message;}
            return result;
        }

        static StudyDataRecord DecodeFrame(string frame)
        {
            var fields=new Dictionary<string,string>(StringComparer.Ordinal);StudyDataRecord record=null;
            using(var reader=new StringReader(frame))
            {
                string line;while((line=reader.ReadLine())!=null)
                {
                    var cells=line.Split(',');if(cells.Length!=5)throw new InvalidDataException("Invalid verified frame row.");
                    long ordinal=Int64.Parse(cells[0],CultureInfo.InvariantCulture);
                    if(record==null)record=new StudyDataRecord{Ordinal=ordinal,Kind=cells[1],Fields=fields};
                    if(record.Ordinal!=ordinal||record.Kind!=cells[1])throw new InvalidDataException("Mixed verified frame.");
                    Add(fields,cells[2],Decode(cells[3],cells[4]));
                }
            }
            if(record==null)throw new InvalidDataException("Empty verified frame.");return record;
        }

        static string Decode(string type,string raw)
        {
            if(type=="utf8_b64")return Utf8.GetString(Convert.FromBase64String(raw));
            if(type=="null"){if(raw.Length!=0)throw new InvalidDataException("Nonempty null encoding.");return null;}
            if(type=="f64_bits_hex")
            {
                if(raw.Length!=16)throw new InvalidDataException("Invalid floating-point encoding.");
                return BitConverter.Int64BitsToDouble(unchecked((long)UInt64.Parse(raw,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture))).ToString("R",CultureInfo.InvariantCulture);
            }
            if(type=="bool")
            {if(raw!="0"&&raw!="1")throw new InvalidDataException("Invalid boolean encoding.");return raw=="1"?"true":"false";}
            if(type=="int")return Int64.Parse(raw,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            if(type=="uint")return UInt64.Parse(raw,NumberStyles.None,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            if(type=="bytes_b64"){Convert.FromBase64String(raw);return raw;}
            throw new InvalidDataException("Unsupported raw type: "+type+".");
        }
        static void Add(IDictionary<string,string> fields,string field,string value)
        {
            if(String.IsNullOrEmpty(field)||fields.ContainsKey(field))throw new InvalidDataException("Empty or repeated record field.");
            if(fields.Count>=1024||field.Length>256||(value!=null&&value.Length>32768))throw new InputLimit();
            fields.Add(field,value);
        }
        static string TypedRaw(string encoded)
        {
            if(!encoded.StartsWith("raw:",StringComparison.Ordinal))throw new InvalidDataException("Missing original raw encoding.");
            int separator=encoded.IndexOf(':',4);if(separator<0)throw new InvalidDataException("Missing original raw type.");
            return Decode(encoded.Substring(4,separator-4),encoded.Substring(separator+1));
        }

        sealed class CsvRecords
        {
            internal StudyDataRecord Current;
            internal readonly Dictionary<string,string> Export=new Dictionary<string,string>(StringComparer.Ordinal);
            internal long Count;
            internal bool Terminal;
            readonly Action<StudyDataRecord> accept;readonly int maximum;
            internal CsvRecords(Action<StudyDataRecord> value,int limit){accept=value;maximum=limit;}
            internal void Flush()
            {
                if(Current==null)return;
                if(Count==0&&(Current.Ordinal!=0||Current.Kind!="manifest"))throw new InvalidDataException("First record must be manifest 0.");
                if(Current.Kind=="manifest"&&Count!=0)throw new InvalidDataException("Repeated manifest.");
                if(Current.Kind=="capture_terminal")
                {
                    string declaration;
                    if(!Current.Fields.TryGetValue("declaration",out declaration)||declaration!="NORMAL_TASK_COMPLETION_DRAIN_DECLARED")
                        throw new InvalidDataException("Invalid source terminal declaration.");
                    Terminal=true;
                }
                accept(Current);Count++;Current=null;
            }
            internal StudyDataRecord Start(long ordinal,string kind,bool single)
            {
                if(Current!=null&&Current.Ordinal==ordinal)
                {
                    if(Current.Kind!=kind||single)throw new InvalidDataException("Mixed or repeated ordinal group.");return Current;
                }
                Flush();if(Terminal)throw new InvalidDataException("Record after source terminal.");
                if(ordinal!=Count)throw new InvalidDataException("Missing, repeated or out-of-order record ordinal.");
                if(Count>=maximum)throw new InputLimit();
                Current=new StudyDataRecord{Ordinal=ordinal,Kind=kind,Fields=new Dictionary<string,string>(StringComparer.Ordinal)};return Current;
            }
        }

        static void ReadCsv(CsvRows rows,Action<StudyDataRecord> accept,StudyDataReadSummary result,int maximumRecords,CancellationToken token)
        {
            string[] header=rows.Next();if(header==null||header.Length<CoreHeader.Length)throw new InvalidDataException("Not a participant readable analysis export.");
            var columns=new Dictionary<string,int>(StringComparer.Ordinal);
            for(int i=0;i<header.Length;i++)
            {
                if(columns.ContainsKey(header[i]))throw new InvalidDataException("Repeated CSV header.");columns.Add(header[i],i);
                if(i<CoreHeader.Length&&header[i]!=CoreHeader[i])throw new InvalidDataException("Unsupported CSV header; fixed-target validation CSV is not a study recording.");
                if(i>=CoreHeader.Length&&!header[i].StartsWith("eye_",StringComparison.Ordinal))throw new InvalidDataException("Unsupported CSV extension header.");
            }
            if(!columns.ContainsKey("eye_native_frame_raw"))throw new InvalidDataException("Missing readable analysis eye schema.");
            var state=new CsvRecords(accept,maximumRecords);string[] row;
            while((row=rows.Next())!=null)
            {
                token.ThrowIfCancellationRequested();
                if(row.Length!=header.Length)throw new InvalidDataException("CSV row has a different number of columns than the header.");
                long ordinal=Int64.Parse(row[0],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture);string kind=row[1];
                if(kind=="export")
                {
                    if(ordinal!=-1||row[3]!="text")throw new InvalidDataException("Invalid export metadata row.");
                    Add(state.Export,row[2],row[4]);continue;
                }
                bool wide=kind=="observation"||kind=="eye_acquisition",quality=kind=="eye_observation";
                if(!wide&&!quality&&kind!="manifest"&&kind!="task"&&kind!="eye_status"&&kind!="capture_terminal")
                    throw new InvalidDataException("Unsupported study record kind: "+kind+".");
                var record=state.Start(ordinal,kind,wide||quality);
                if(wide)
                {
                    if(row[2]!="eye_frame"||row[3]!="wide")throw new InvalidDataException("Invalid wide eye row.");
                    for(int i=CoreHeader.Length;i<header.Length;i++)
                    {
                        if(!header[i].EndsWith("_raw",StringComparison.Ordinal)||row[i].Length==0||row[i]=="raw:missing")continue;
                        Add(record.Fields,header[i].Substring(4,header[i].Length-8),TypedRaw(row[i]));
                    }
                    if(!record.Fields.ContainsKey("native_frame"))throw new InvalidDataException("Wide eye row lacks native identity.");
                }
                else if(quality)
                {
                    if(row[2]!="eye_quality"||row[3]!="typed")throw new InvalidDataException("Invalid owner eye observation.");
                    var parts=row[5].Split(';');if(parts.Length!=4)throw new InvalidDataException("Incomplete owner eye observation.");
                    foreach(string part in parts)
                    {
                        int separator=part.IndexOf(':');if(separator<0)throw new InvalidDataException("Invalid owner eye observation encoding.");
                        string key=part.Substring(0,separator);
                        string field=key=="state"?"eye_observation":key=="reason"?"reason":key=="owner_time"?"owner_observed_local_seconds":key=="task_cursor"?"task_cursor":null;
                        if(field==null)throw new InvalidDataException("Unknown owner eye observation field.");
                        Add(record.Fields,field,TypedRaw("raw:"+part.Substring(separator+1)));
                    }
                }
                else
                {
                    if(!row[5].StartsWith("raw:",StringComparison.Ordinal))throw new InvalidDataException("Missing original long-form raw value.");
                    Add(record.Fields,row[2],Decode(row[3],row[5].Substring(4)));
                }
            }
            state.Flush();result.RecordCount=state.Count;
            if(state.Count==0)throw new InvalidDataException("No manifest or study records.");
            string declaredCount,digest,terminal,scope;
            if(!state.Export.TryGetValue("scope",out scope)||scope!="VERIFIED_FRAMES_ONLY")throw new InvalidDataException("Missing readable analysis export identity.");
            if(state.Export.TryGetValue("verified_record_count",out declaredCount)&&Int64.Parse(declaredCount,CultureInfo.InvariantCulture)!=state.Count)
                throw new InvalidDataException("Export record count differs from parsed records.");
            if(state.Export.TryGetValue("verified_chain_sha256",out digest))
            {
                if(digest.Length!=64)throw new InvalidDataException("Invalid declared chain digest.");
                foreach(char ch in digest)if(!Uri.IsHexDigit(ch))throw new InvalidDataException("Invalid declared chain digest.");
                result.VerifiedChainSha256=digest;
            }
            bool complete=state.Terminal&&state.Export.TryGetValue("source_terminal",out terminal)&&terminal=="DECLARED_AND_CHAIN_VERIFIED_NOT_EXPORT_SUCCESS"&&declaredCount!=null&&digest!=null;
            result.SourceTerminalObserved=state.Terminal;result.ChainVerified=false;
            result.Status=complete?"Complete":"Incomplete";
            result.Message=complete?"Readable CSV parsed; source terminal and chain digest are export declarations. Analyze VRAW to verify the original chain.":
                "Readable CSV parsed as incomplete: source terminal or export footer is missing.";
            accept(new StudyDataRecord{Ordinal=-1,Kind="export",Fields=state.Export});
        }

        sealed class LimitedInput:Stream
        {
            readonly Stream source;readonly long maximum;readonly CancellationToken token;long count;
            internal LimitedInput(Stream input,long bytes,CancellationToken cancellation){source=input;maximum=bytes;token=cancellation;}
            public override int Read(byte[] buffer,int offset,int length)
            {
                token.ThrowIfCancellationRequested();
                if(count==maximum){if(source.ReadByte()!=-1)throw new InputLimit();return 0;}
                int read=source.Read(buffer,offset,(int)Math.Min(length,maximum-count));count+=read;token.ThrowIfCancellationRequested();return read;
            }
            public override bool CanRead {get{return true;}}public override bool CanWrite {get{return false;}}public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}
            public override long Position {get{return count;}set{throw new NotSupportedException();}}
            public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long value){throw new NotSupportedException();}
            public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
            public override void Flush(){throw new NotSupportedException();}
        }
        sealed class CsvRows
        {
            const int MaximumCellChars=1048576,MaximumRowChars=4194304,MaximumColumns=1024;
            readonly TextReader text;readonly CancellationToken token;
            internal CsvRows(TextReader input,CancellationToken cancellation){text=input;token=cancellation;}
            internal string[] Next()
            {
                var fields=new List<string>();var cell=new StringBuilder();bool quoted=false,closed=false,any=false;int chars=0;
                while(true)
                {
                    if((chars&4095)==0)token.ThrowIfCancellationRequested();
                    int read=text.Read();if(read<0)
                    {
                        if(quoted)throw new TruncatedCsv();if(!any)return null;
                        fields.Add(cell.ToString());return fields.ToArray();
                    }
                    any=true;char ch=(char)read;if(++chars>MaximumRowChars)throw new InputLimit();
                    if(quoted)
                    {
                        if(ch=='"')
                        {
                            if(text.Peek()=='"'){text.Read();chars++;cell.Append('"');}
                            else{quoted=false;closed=true;}
                        }
                        else cell.Append(ch);
                    }
                    else if(ch==','||ch=='\n'||ch=='\r')
                    {
                        if(fields.Count>=MaximumColumns)throw new InputLimit();fields.Add(cell.ToString());cell.Length=0;closed=false;
                        if(ch!=','){if(ch=='\r'&&text.Peek()=='\n')text.Read();return fields.ToArray();}
                    }
                    else
                    {
                        if(closed)throw new InvalidDataException("Unexpected character after closing CSV quote.");
                        if(ch=='"'){if(cell.Length!=0)throw new InvalidDataException("Quote inside unquoted CSV field.");quoted=true;}
                        else cell.Append(ch);
                    }
                    if(cell.Length>MaximumCellChars)throw new InputLimit();
                }
            }
        }
    }
}
#endif
