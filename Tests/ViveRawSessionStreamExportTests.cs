#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveRawSessionStreamExportTests
    {
        sealed class BrokenOutput:MemoryStream
        {
            public override void Write(byte[] buffer,int offset,int count)
            {base.Write(buffer,offset,Math.Min(count,7));throw new IOException("synthetic write failure");}
        }
        sealed class BrokenFlush:MemoryStream
        {public override void Flush(){throw new IOException("synthetic flush failure");}}
        sealed class CancelOnWrite:MemoryStream
        {
            readonly CancellationTokenSource cancel;
            internal CancelOnWrite(CancellationTokenSource value){cancel=value;}
            public override void Write(byte[] bytes,int offset,int count){base.Write(bytes,offset,count);cancel.Cancel();}
        }
        sealed class CancelDuringInputCheck:MemoryStream
        {
            readonly CancellationTokenSource cancel;
            internal CancelDuringInputCheck(byte[] bytes,CancellationTokenSource value):base(bytes,false){cancel=value;}
            public override long Length {get{cancel.Cancel();return base.Length;}}
        }
        sealed class ForwardOnly:Stream
        {
            readonly MemoryStream source;readonly CancellationTokenSource cancel;readonly bool abort;
            internal ForwardOnly(byte[] bytes,CancellationTokenSource cancel=null,bool abort=false){source=new MemoryStream(bytes,false);this.cancel=cancel;this.abort=abort;}
            public override bool CanRead {get{return true;}} public override bool CanSeek {get{return false;}} public override bool CanWrite {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}} public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int off,int len){if(abort){cancel.Cancel();throw new IOException("synthetic source revoked");}return source.Read(b,off,Math.Min(len,8192));}
            public override void Flush(){throw new NotSupportedException();} public override long Seek(long off,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long n){throw new NotSupportedException();} public override void Write(byte[] b,int off,int len){throw new NotSupportedException();}
            protected override void Dispose(bool disposing){if(disposing)source.Dispose();base.Dispose(disposing);}
        }
        static string Export(byte[] source,out ViveRawSessionStreamExport.Outcome outcome)
        {
            using(var input=new MemoryStream(source,false))using(var output=new MemoryStream())
            {outcome=ViveRawSessionStreamExport.Write(input,output,CancellationToken.None);return Encoding.UTF8.GetString(output.ToArray());}
        }
        static int Count(string value,string target)
        {int count=0,pos=0;while((pos=value.IndexOf(target,pos,StringComparison.Ordinal))>=0){count++;pos+=target.Length;}return count;}
        static string Body(string data,string name)
        {
            string prefix="SECTION "+name+" ";int at=data.IndexOf(prefix,StringComparison.Ordinal);Check.True(at>=0);
            int end=data.IndexOf('\n',at);Check.True(end>at);int size=Int32.Parse(data.Substring(at+prefix.Length,end-at-prefix.Length));
            Check.True(size>=0&&end+1+size<=data.Length);return data.Substring(end+1,size);
        }
        static string RawPages(string data)
        {
            var pages=new StringBuilder();int at=0;
            while(at<data.Length)
            {
                int end=data.IndexOf('\n',at);Check.True(end>at);var header=data.Substring(at,end-at).Split(' ');
                Check.True(header.Length==3&&header[0]=="SECTION");int length=Int32.Parse(header[2]);at=end+1;
                Check.True(length>=0&&length<=data.Length-at);string body=data.Substring(at,length);at+=length;
                Check.True(at<data.Length&&data[at++]=='\n');
                if(header[1]=="raw_typed_records") {if(pages.Length>0)body=body.Substring(body.IndexOf('\n')+1);pages.Append(body);}
            }
            return pages.ToString();
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic stream export preserves four blocks, typed pages and unknowns",()=>{
                byte[] fixture=ViveRawTrialProjectionTests.FourBlocks();ViveRawSessionStreamExport.Outcome result;string data=Export(fixture,out result);
                Check.Equal(ViveRawSessionStreamExport.Outcome.WrittenAndFlushed,result);
                Check.True(data.Contains("trailing_frame_truncated=false\n")&&data.Contains("capture_terminal_declared=true\n")&&
                    data.Contains("complete_session_proven=false\n")&&data.Contains("writer_confirmation=UNKNOWN\n")&&data.Contains("tail_coverage=UNKNOWN\n"));
                Check.True(data.Contains("SECTION trials ")&&data.Contains("SECTION ratings ")&&data.Contains(",task,event_kind,utf8_b64,")&&
                    Count(data,"SECTION raw_typed_records ")>1&&data.Contains("SECTION export_end "));
                using(var source=new MemoryStream(fixture,false))
                    Check.Equal(ViveRawSessionJournalReader.Read(source,true,true,4194304,5000).DisplayText,RawPages(data));
            });
            suite.Run("synthetic export marks a truncated tail and keeps verified prefix",()=>{
                ViveRawSessionStreamExport.Outcome result;string data=Export(ViveRawTrialProjectionTests.FourBlocks(truncateTerminal:true),out result);
                Check.Equal(ViveRawSessionStreamExport.Outcome.WrittenAndFlushed,result);
                Check.True(data.Contains("trailing_frame_truncated=true\n")&&data.Contains("capture_terminal_declared=false\n")&&
                    data.Contains("tail_coverage=UNKNOWN\n")&&data.Contains("raw_typed_records=VERIFIED_PREFIX_ONLY\n"));
            });
            suite.Run("synthetic export preserves exact verified VRAW bytes and omits truncated tail",()=>{
                foreach(bool truncated in new[]{false,true})
                {
                    byte[] fixture=ViveRawTrialProjectionTests.FourBlocks(truncateTerminal:truncated);
                    ViveRawSessionStreamExport.Outcome result;string data=Export(fixture,out result);
                    Check.Equal(ViveRawSessionStreamExport.Outcome.WrittenAndFlushed,result);
                    byte[] saved=Convert.FromBase64String(Body(data,"verified_vraw_prefix_base64"));
                    long verified;using(var source=new MemoryStream(fixture,false))verified=ViveRawSessionJournalReader.ProjectTrials(source,true,true,4194304,5000,CancellationToken.None).VerifiedBytes;
                    Check.Equal(verified,(long)saved.Length);Check.True(!truncated||saved.Length<fixture.Length);
                    for(int i=0;i<saved.Length;i++)Check.Equal(fixture[i],saved[i]);
                    Check.True(data.Contains("unverified_tail_included=false\n"));
                }
            });
            suite.Run("synthetic export withholds observed withdrawal before any output",()=>{
                var fixture=ViveRawSessionTerminalTests.Fixture(reason:"Withdrawn");
                using(var input=new MemoryStream(fixture,false))using(var output=new MemoryStream())
                {Check.Equal(ViveRawSessionStreamExport.Outcome.Rejected,ViveRawSessionStreamExport.Write(input,output,CancellationToken.None));Check.Equal(0L,output.Length);}
            });
            suite.Run("synthetic export retains raw eye fields and does not claim gaze alignment",()=>{
                long manifest,task;var fixture=ViveRawSessionJournalReaderTests.Fixture(out manifest,out task);
                ViveRawSessionStreamExport.Outcome result;string data=Export(fixture,out result);
                Check.Equal(ViveRawSessionStreamExport.Outcome.WrittenAndFlushed,result);
                Check.True(data.Contains(",observation,left_mask,uint,3\n")&&data.Contains(",observation,combined_direction_x,f64_bits_hex,")&&
                    data.Contains("samples_joined_to_trials,bool:false\n"));
            });
            suite.Run("synthetic export rejects invalid input and cancellation before output",()=>{
                byte[] bytes=ViveRawTrialProjectionTests.FourBlocks();bytes[bytes.Length-1]^=1;
                using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {Check.Equal(ViveRawSessionStreamExport.Outcome.Rejected,ViveRawSessionStreamExport.Write(input,output,CancellationToken.None));Check.Equal(0L,output.Length);}
                using(var input=new MemoryStream(ViveRawTrialProjectionTests.FourBlocks(),false))using(var output=new MemoryStream())using(var cancel=new CancellationTokenSource())
                {cancel.Cancel();Check.Equal(ViveRawSessionStreamExport.Outcome.Cancelled,ViveRawSessionStreamExport.Write(input,output,cancel.Token));Check.Equal(0L,output.Length);}
            });
            suite.Run("reader cancellation is retained before output",()=>{
                using(var cancel=new CancellationTokenSource())using(var input=new CancelDuringInputCheck(ViveRawTrialProjectionTests.FourBlocks(),cancel))using(var output=new MemoryStream())
                {Check.Equal(ViveRawSessionStreamExport.Outcome.Cancelled,ViveRawSessionStreamExport.Write(input,output,cancel.Token));Check.Equal(0L,output.Length);}
            });
            suite.Run("synthetic output failure is uncertain, never success",()=>{
                using(var input=new MemoryStream(ViveRawTrialProjectionTests.FourBlocks(),false))using(var output=new BrokenOutput())
                {Check.Equal(ViveRawSessionStreamExport.Outcome.OutputUncertain,ViveRawSessionStreamExport.Write(input,output,CancellationToken.None));Check.Equal(7L,output.Length);}
            });
            suite.Run("synthetic export rejects nonempty and shifted output without writing",()=>{
                var fixture=ViveRawTrialProjectionTests.FourBlocks();
                using(var input=new MemoryStream(fixture,false))using(var output=new MemoryStream())
                {output.WriteByte(42);output.Position=0;Check.Equal(ViveRawSessionStreamExport.Outcome.Rejected,ViveRawSessionStreamExport.Write(input,output,CancellationToken.None));Check.Equal(1L,output.Length);Check.Equal(42,(int)output.ToArray()[0]);}
                using(var input=new MemoryStream(fixture,false))using(var output=new MemoryStream())
                {output.Position=1;Check.Equal(ViveRawSessionStreamExport.Outcome.Rejected,ViveRawSessionStreamExport.Write(input,output,CancellationToken.None));Check.Equal(0L,output.Length);}
            });
            suite.Run("synthetic flush failure and cancellation during write are uncertain",()=>{
                var fixture=ViveRawTrialProjectionTests.FourBlocks();
                using(var input=new MemoryStream(fixture,false))using(var output=new BrokenFlush())
                {Check.Equal(ViveRawSessionStreamExport.Outcome.OutputUncertain,ViveRawSessionStreamExport.Write(input,output,CancellationToken.None));Check.True(output.Length>0);}
                using(var input=new MemoryStream(fixture,false))using(var cancel=new CancellationTokenSource())using(var output=new CancelOnWrite(cancel))
                {Check.Equal(ViveRawSessionStreamExport.Outcome.OutputUncertain,ViveRawSessionStreamExport.Write(input,output,cancel.Token));Check.True(output.Length>0);}
            });
            suite.Run("declared SDK source remains unauthenticated",()=>{
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"fixture");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"fixture-plan",option);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"fixture-intent",true,true);
                var manifest=new ViveNamedSessionFrameAdmission.Manifest("fixture","clock",plan,intent,false);manifest.SealChoiceTime(0,-1);
                byte[] digest;byte[] frame=ViveRawSessionJournalCodec.FrameV3(ViveRawSessionJournalCodec.ManifestPayloadV3(manifest),new byte[32],true,out digest);
                using(var input=new MemoryStream(frame,false))using(var output=new MemoryStream())
                {Check.Equal(ViveRawSessionStreamExport.Outcome.WrittenAndFlushed,ViveRawSessionStreamExport.Write(input,output,CancellationToken.None));
                    string text=Encoding.UTF8.GetString(output.ToArray());Check.True(text.Contains("declared_source=INSTALLED_SDK_CODE_PATH_ONLY\n")&&text.Contains("source_authenticated=false\n")&&text.Contains("consent_authenticated=false\n"));}
            });
            suite.Run("export rejects writable source and shifted source without output",()=>{
                byte[] fixture=ViveRawTrialProjectionTests.FourBlocks();
                using(var input=new MemoryStream())using(var output=new MemoryStream())
                {input.Write(fixture,0,fixture.Length);input.Position=0;Check.Equal(ViveRawSessionStreamExport.Outcome.Rejected,ViveRawSessionStreamExport.Write(input,output,CancellationToken.None));Check.Equal(0L,output.Length);}
                using(var input=new MemoryStream(fixture,false))using(var output=new MemoryStream())
                {input.Position=1;Check.Equal(ViveRawSessionStreamExport.Outcome.Rejected,ViveRawSessionStreamExport.Write(input,output,CancellationToken.None));Check.Equal(0L,output.Length);}
            });
            suite.Run("forward-only exact-length source retains verified bytes",()=>{
                byte[] fixture=ViveRawTrialProjectionTests.FourBlocks();
                using(var input=new ForwardOnly(fixture))using(var output=new MemoryStream())
                {Check.Equal(ViveRawSessionStreamExport.Outcome.WrittenAndFlushed,ViveRawSessionStreamExport.WriteExpected(input,fixture.Length,output,CancellationToken.None));
                    byte[] raw=Convert.FromBase64String(Body(Encoding.UTF8.GetString(output.ToArray()),"verified_vraw_prefix_base64"));Check.Equal(fixture.Length,raw.Length);for(int i=0;i<raw.Length;i++)Check.Equal(fixture[i],raw[i]);}
            });
            suite.Run("forward-only length mismatch and source withdrawal do not emit",()=>{
                byte[] fixture=ViveRawTrialProjectionTests.FourBlocks();
                foreach(int delta in new[]{-1,1})using(var input=new ForwardOnly(fixture))using(var output=new MemoryStream())
                {Check.Equal(ViveRawSessionStreamExport.Outcome.Rejected,ViveRawSessionStreamExport.WriteExpected(input,fixture.Length+delta,output,CancellationToken.None));Check.Equal(0L,output.Length);}
                using(var cancel=new CancellationTokenSource())using(var input=new ForwardOnly(fixture,cancel,true))using(var output=new MemoryStream())
                {Check.Equal(ViveRawSessionStreamExport.Outcome.Cancelled,ViveRawSessionStreamExport.WriteExpected(input,fixture.Length,output,cancel.Token));Check.Equal(0L,output.Length);}
            });
        }
    }
}
#endif
