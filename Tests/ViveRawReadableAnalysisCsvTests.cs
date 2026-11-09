#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveRawReadableAnalysisCsvTests
    {
        sealed class FailingOutput:MemoryStream
        {public override void Write(byte[] b,int o,int n){base.Write(b,o,Math.Min(3,n));throw new IOException("synthetic target failed");}}
        sealed class FailingProperty:MemoryStream
        {public override bool CanRead {get{throw new IOException("synthetic property failure");}}}
        sealed class FailingFlush:MemoryStream
        {public override void Flush(){throw new IOException("synthetic flush failure");}}
        sealed class CancelWrite:MemoryStream
        {
            readonly CancellationTokenSource cancel;internal CancelWrite(CancellationTokenSource value){cancel=value;}
            public override void Write(byte[] b,int o,int n){base.Write(b,o,n);cancel.Cancel();}
        }
        sealed class CancelRead:MemoryStream
        {
            readonly CancellationTokenSource cancel;int calls;
            internal CancelRead(byte[] value,CancellationTokenSource source):base(value,false){cancel=source;}
            public override int Read(byte[] b,int o,int n){int result=base.Read(b,o,n);if(++calls==8)cancel.Cancel();return result;}
        }
        sealed class ForwardOnly:Stream
        {
            readonly MemoryStream inner;internal long ReadBytes;
            internal ForwardOnly(byte[] bytes){inner=new MemoryStream(bytes,false);}
            public override bool CanRead {get{return true;}}public override bool CanSeek {get{return false;}}public override bool CanWrite {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n){int value=inner.Read(b,o,n);ReadBytes+=value;return value;}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long n){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
            protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
        }
        static string Convert(string rows)
        {
            var method=typeof(ViveRawReadableAnalysisCsv).GetMethod("ConvertFrame",BindingFlags.NonPublic|BindingFlags.Static);
            return (string)method.Invoke(null,new object[]{rows});
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("readable analysis streams all verified four-block fields without changing original",()=>{
                byte[] bytes=ViveRawTrialProjectionTests.FourBlocks();byte[] original=(byte[])bytes.Clone();
                using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {
                    var status=ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,CancellationToken.None);
                    Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,status);
                    string csv=Encoding.UTF8.GetString(output.ToArray());
                    Check.True(csv.StartsWith("ordinal,record_kind,field,type,readable_value,raw_value,row_warning,",StringComparison.Ordinal));
                    Check.True(csv.Contains("\"task\",\"event_kind\",\"utf8_b64\",\"trial_planned\"")&&
                        csv.Contains("NATIVE_TIME_OPAQUE")&&csv.Contains("INGEST_NOT_SAMPLE_ALIGNMENT")&&
                        csv.Contains("writer_confirmation,text,UNKNOWN")&&csv.Contains("DECLARED_AND_CHAIN_VERIFIED_NOT_EXPORT_SUCCESS")&&
                        csv.Contains("LEADING_APOSTROPHE_RAW_VALUE_UNMODIFIED")&&csv.Contains("ONE_ROW_PER_EYE_FRAME_OTHER_RECORDS_LONG_FORM")&&
                        csv.Contains("EXCEL_VIEW_MAY_TRUNCATE_USE_FULL_CSV_PARSER"));
                }
                Check.Equal(original.Length,bytes.Length);for(int i=0;i<bytes.Length;i++)Check.Equal(original[i],bytes[i]);
                Check.True(!ViveRawReadableAnalysisCsv.ProductionGateOpen);
            });
            suite.Run("readable rows decode strings floats and protect spreadsheet formulas",()=>{
                string text=Convert("1,task,field_0_value,utf8_b64,PSsy\n");
                string eye=Convert("2,observation,combined_direction_x,f64_bits_hex,3FF0000000000000\n2,observation,native_frame,int,7\n");
                Check.True(text.Contains("\"'=+2\",\"raw:PSsy\"")&&eye.Contains("\"1\",\"raw:f64_bits_hex:3FF0000000000000\"")&&eye.Contains("\"7\",\"raw:int:7\""));
                Check.True(Convert("3,task,block,int,-1\n").Contains("\"-1\",\"raw:-1\""));
                string extra=Convert("4,task,value,uint,18446744073709551615\n")+
                    Convert("5,observation,left_origin_x,f64_bits_hex,8000000000000000\n")+
                    Convert("6,observation,left_origin_y,f64_bits_hex,7FF8000000000000\n")+
                    Convert("7,observation,left_origin_z,f64_bits_hex,7FF0000000000000\n")+
                    Convert("8,manifest,bytes,bytes_b64,+w==\n")+Convert("9,task,text,utf8_b64,YSxcImJcIlxu\n");
                Check.True(extra.Contains("raw:18446744073709551615")&&extra.Contains("raw:f64_bits_hex:8000000000000000")&&
                    extra.Contains("raw:f64_bits_hex:7FF8000000000000")&&extra.Contains("raw:f64_bits_hex:7FF0000000000000")&&extra.Contains("raw:+w=="));
            });
            suite.Run("readable analysis never completes for truncated corrupted or withdrawn source",()=>{
                byte[] truncated=ViveRawTrialProjectionTests.FourBlocks(truncateTerminal:true);
                byte[] corrupt=ViveRawTrialProjectionTests.FourBlocks();corrupt[corrupt.Length-1]^=1;
                foreach(var source in new[]{truncated,corrupt,ViveRawSessionTerminalTests.Fixture(reason:"Withdrawn")})
                using(var input=new MemoryStream(source,false))using(var output=new MemoryStream())
                {
                    Check.Equal(ViveRawReadableAnalysisCsv.Outcome.OutputUncertain,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,CancellationToken.None));
                    Check.True(!Encoding.UTF8.GetString(output.ToArray()).Contains("DECLARED_AND_CHAIN_VERIFIED_NOT_EXPORT_SUCCESS"));
                }
            });
            suite.Run("readable analysis withholds output on prior cancellation and rejects nonempty target",()=>{
                byte[] bytes=ViveRawTrialProjectionTests.FourBlocks();
                using(var cancel=new CancellationTokenSource())using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {cancel.Cancel();Check.Equal(ViveRawReadableAnalysisCsv.Outcome.Cancelled,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,cancel.Token));Check.Equal(0L,output.Length);}
                using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {output.WriteByte(42);output.Position=0;Check.Equal(ViveRawReadableAnalysisCsv.Outcome.Rejected,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,CancellationToken.None));Check.Equal(1L,output.Length);}
            });
            suite.Run("readable analysis marks target failure and cancellation during output uncertain",()=>{
                byte[] bytes=ViveRawTrialProjectionTests.FourBlocks();
                using(var input=new MemoryStream(bytes,false))using(var output=new FailingOutput())
                {Check.Equal(ViveRawReadableAnalysisCsv.Outcome.OutputUncertain,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,CancellationToken.None));}
                using(var input=new MemoryStream(bytes,false))using(var cancel=new CancellationTokenSource())using(var output=new CancelWrite(cancel))
                {Check.Equal(ViveRawReadableAnalysisCsv.Outcome.OutputUncertain,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,cancel.Token));}
            });
            suite.Run("readable analysis respects explicit output budget",()=>{
                using(var input=new MemoryStream(ViveRawTrialProjectionTests.FourBlocks(),false))using(var output=new MemoryStream())
                {Check.Equal(ViveRawReadableAnalysisCsv.Outcome.OutputUncertain,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,8192,CancellationToken.None));}
            });
            suite.Run("readable analysis rejects property failure and respects closed scanner gate",()=>{
                using(var input=new FailingProperty())using(var output=new MemoryStream())
                {Check.Equal(ViveRawReadableAnalysisCsv.Outcome.Rejected,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,CancellationToken.None));Check.Equal(0L,output.Length);}
                using(var input=new MemoryStream(ViveRawTrialProjectionTests.FourBlocks(),false))
                {bool called=false;Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalReader.ScanAnalysisRows(input,4194304,5000,CancellationToken.None,_=>called=true,false));Check.True(!called&&input.Position==0);}
            });
            suite.Run("readable analysis does not claim output completion after flush failure",()=>{
                using(var input=new MemoryStream(ViveRawTrialProjectionTests.FourBlocks(),false))using(var output=new FailingFlush())
                {Check.Equal(ViveRawReadableAnalysisCsv.Outcome.OutputUncertain,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,CancellationToken.None));
                    string value=Encoding.UTF8.GetString(output.ToArray());Check.True(!value.Contains("SCAN_RETURNED_COMPLETE_TERMINAL")&&!value.Contains("analysis_end"));}
            });
            suite.Run("readable analysis treats cancellation during input as uncertain",()=>{
                using(var cancel=new CancellationTokenSource())using(var input=new CancelRead(ViveRawTrialProjectionTests.FourBlocks(),cancel))using(var output=new MemoryStream())
                {Check.Equal(ViveRawReadableAnalysisCsv.Outcome.OutputUncertain,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,cancel.Token));
                    Check.True(!Encoding.UTF8.GetString(output.ToArray()).Contains("DECLARED_AND_CHAIN_VERIFIED_NOT_EXPORT_SUCCESS"));}
            });
            suite.Run("readable analysis supports one-pass forward-only source",()=>{
                var bytes=ViveRawTrialProjectionTests.FourBlocks();
                using(var input=new ForwardOnly(bytes))using(var output=new MemoryStream())
                {Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,CancellationToken.None));
                    Check.Equal((long)bytes.Length,input.ReadBytes);Check.True(output.Length>0);}
            });
            suite.Run("readable analysis warns on long Excel cells and rejects unknown eye fields",()=>{
                string longValue=new string('x',32768);string encoded=System.Convert.ToBase64String(Encoding.UTF8.GetBytes(longValue));
                string output=Convert("1,task,value,utf8_b64,"+encoded+"\n");
                Check.True(output.Contains("EXCEL_CELL_TOO_LONG_USE_FULL_CSV_OR_VRAW")&&output.Contains("raw:"+encoded));
                var method=typeof(ViveRawReadableAnalysisCsv).GetMethod("ConvertFrame",BindingFlags.NonPublic|BindingFlags.Static);
                bool rejected=false;try{method.Invoke(null,new object[]{"2,observation,future_field,int,1\n"});}
                catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}
                Check.True(rejected,"unknown eye fields must not be silently omitted");
            });
        }
    }
}
#endif
