using System;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Only synthetic bytes and memory streams; no file export or headset use.
    public static class ViveRawSessionPageTests
    {
        const string Header="ordinal,record_kind,field,type,value\n";
        static ViveRawSessionReadResult Page(byte[] bytes,long start,int size,long budget=4194304,int records=5000)
        {using(var input=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.ReadRawPage(input,true,true,start,size,budget,records,CancellationToken.None);}
        sealed class InterruptedRead:Stream
        {
            readonly Stream inner;readonly CancellationTokenSource cancellation;readonly bool fail;int calls;
            internal InterruptedRead(byte[] bytes,CancellationTokenSource token,int after,bool fail)
            {inner=new MemoryStream(bytes,false);cancellation=token;calls=after;this.fail=fail;}
            public override bool CanRead {get{return true;}}public override bool CanSeek {get{return false;}}public override bool CanWrite {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int offset,int count)
            {if(--calls==0){if(fail)throw new IOException("synthetic");cancellation.Cancel();}return inner.Read(b,offset,count);}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long n,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long n){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
            protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
        }
        // Simulates a source replaced during a scan. This does not simulate
        // Windows file identity, locking, or a change after the final read.
        sealed class ReplacedRead:Stream
        {
            readonly byte[] first,second;readonly int boundary;int position;
            internal ReplacedRead(byte[] first,byte[] second,int boundary){this.first=first;this.second=second;this.boundary=boundary;}
            public override bool CanRead {get{return true;}}public override bool CanSeek {get{return false;}}public override bool CanWrite {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] data,int offset,int count)
            {byte[] source=position<boundary?first:second;int end=position<boundary?boundary:source.Length;int n=Math.Min(count,end-position);if(n<=0)return 0;Array.Copy(source,position,data,offset,n);position+=n;return n;}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long n,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long n){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VRAW page preserves every stored field while independently verifying the selected prefix",()=>{
                long manifest,task;byte[] bytes=ViveRawSessionJournalReaderTests.Fixture(out manifest,out task);
                ViveRawSessionReadResult all;using(var input=new MemoryStream(bytes,false))all=ViveRawSessionJournalReader.Read(input,true,true,4194304,5000);
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,all.Status);Check.Equal(3L,all.VerifiedRecordCount);
                var joined=new StringBuilder(Header);string digest=null;
                for(int i=0;i<3;i++)
                {
                    var page=Page(bytes,i,1);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,page.Status);Check.Equal((long)i,page.PageStartOrdinal);Check.Equal(1,page.PageRecordCount);
                    Check.Equal(i<2,page.PageHasMoreRecords);Check.Equal(3L,page.VerifiedRecordCount);Check.Equal((long)bytes.Length,page.VerifiedBytes);
                    Check.True(!page.SourceAuthenticated&&!page.ConsentAuthenticated&&!page.CurrentAccessAuthorized&&!page.CompleteSessionProven&&!page.DurableStorageProven);
                    Check.Equal("UNKNOWN",page.CurrentWithdrawalStatus);Check.True(page.DisplayText.StartsWith(Header,StringComparison.Ordinal));
                    if(digest==null)digest=page.VerifiedPrefixChainDigestHex;else Check.Equal(digest,page.VerifiedPrefixChainDigestHex);
                    joined.Append(page.DisplayText.Substring(Header.Length));
                }
                Check.Equal(64,digest.Length);Check.Equal(all.DisplayText,joined.ToString());
                var eye=Page(bytes,2,1);Check.True(eye.DisplayText.Contains("2,observation,left_mask,uint,3\n")&&eye.DisplayText.Contains("2,observation,right_present,bool,0\n")&&
                    eye.DisplayText.Contains("2,observation,combined_direction_x,f64_bits_hex,")&&eye.DisplayText.Contains("2,observation,head_rotation_w,f64_bits_hex,"));
            });
            suite.Run("VRAW page validates all later bytes before exposing any earlier selection",()=>{
                byte[] bytes=ViveRawSessionTerminalTests.Fixture(withStatus:true);var good=Page(bytes,0,1);
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,good.Status);Check.True(good.CaptureTerminalObserved&&good.PageHasMoreRecords);
                byte[] corrupted=(byte[])bytes.Clone();corrupted[corrupted.Length-1]^=1;var bad=Page(corrupted,0,1);
                Check.Equal(ViveRawSessionReadStatus.Invalid,bad.Status);Check.Equal((string)null,bad.DisplayText);Check.Equal((string)null,bad.VerifiedPrefixChainDigestHex);Check.Equal(0,bad.PageRecordCount);
                foreach(var value in new[]{ViveRawSessionTerminalTests.Fixture(reason:"Withdrawn"),ViveRawSessionTerminalTests.Fixture(reason:"unrecognized")})
                {var withheld=Page(value,0,1);Check.Equal(ViveRawSessionReadStatus.ProjectionWithheld,withheld.Status);Check.Equal((string)null,withheld.DisplayText);Check.Equal(0L,withheld.VerifiedBytes);}
            });
            suite.Run("VRAW3 pages preserve status task and terminal fields without inferring completeness",()=>{
                byte[] bytes=ViveRawSessionTerminalTests.Fixture(withStatus:true);ViveRawSessionReadResult full;
                using(var input=new MemoryStream(bytes,false))full=ViveRawSessionJournalReader.Read(input,true,true,4194304,5000);
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,full.Status);Check.True(full.CaptureTerminalObserved);
                var joined=new StringBuilder(Header);string digest=null;
                for(long ordinal=0;ordinal<full.VerifiedRecordCount;ordinal++)
                {var page=Page(bytes,ordinal,1);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,page.Status);Check.Equal(1,page.PageRecordCount);
                    if(digest==null)digest=page.VerifiedPrefixChainDigestHex;else Check.Equal(digest,page.VerifiedPrefixChainDigestHex);
                    Check.True(!page.CompleteSessionProven&&!page.SourceAuthenticated&&!page.CurrentAccessAuthorized);
                    joined.Append(page.DisplayText.Substring(Header.Length));}
                Check.Equal(full.DisplayText,joined.ToString());Check.True(joined.ToString().Contains(",eye_status,cause_flags,uint,1\n")&&joined.ToString().Contains(",capture_terminal,declaration,utf8_b64,"));
            });
            suite.Run("VRAW page records truncated prefix and detects changed source without claiming identity",()=>{
                byte[] first=ViveRawSessionTerminalTests.Fixture(withStatus:true);byte[] second=ViveRawSessionTerminalTests.Fixture(withStatus:true);
                var a=Page(first,0,1);var b=Page(second,1,1);Check.Equal(a.VerifiedBytes,b.VerifiedBytes);Check.True(a.VerifiedPrefixChainDigestHex!=b.VerifiedPrefixChainDigestHex);
                var partial=Page(ViveRawSessionTerminalTests.Fixture(withStatus:true,truncateTerminal:true),0,1);
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,partial.Status);Check.True(partial.TrailingFrameTruncated&&!partial.CaptureTerminalObserved&&partial.PageHasMoreRecords);
                Check.True(!partial.SourceAuthenticated&&!partial.CompleteSessionProven&&!partial.CurrentAccessAuthorized);
                var past=Page(first,100,10);Check.Equal(0,past.PageRecordCount);Check.True(!past.PageHasMoreRecords);Check.Equal(Header,past.DisplayText);
            });
            suite.Run("VRAW bound pages discard changed appended or truncated selected prefixes",()=>{
                long manifest,task;byte[] bytes=ViveRawSessionJournalReaderTests.Fixture(out manifest,out task);
                var first=Page(bytes,0,1);var prefix=ViveRawSessionJournalReader.PagePrefix.FromVerified(first);Check.True(prefix!=null);
                using(var input=new MemoryStream(bytes,false))
                {var page=ViveRawSessionJournalReader.ReadRawPageBoundToPrefix(input,true,true,prefix,2,1,4194304,5000,CancellationToken.None);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,page.Status);Check.True(page.DisplayText.Contains(",observation,"));}
                byte[] appended=new byte[bytes.Length+1];Array.Copy(bytes,appended,bytes.Length);appended[appended.Length-1]=7;
                var changed=ViveRawSessionTerminalTests.Fixture(withStatus:true);
                byte[] shortPrefix=new byte[(int)manifest];Array.Copy(bytes,shortPrefix,shortPrefix.Length);
                foreach(var different in new[]{appended,changed,shortPrefix})
                {using(var input=new MemoryStream(different,false))
                    {var page=ViveRawSessionJournalReader.ReadRawPageBoundToPrefix(input,true,true,prefix,0,1,4194304,5000,CancellationToken.None);
                        Check.True(page.Status!=ViveRawSessionReadStatus.VerifiedPrefix);Check.Equal((string)null,page.DisplayText);}}
                var truncated=Page(appended,0,1);Check.True(truncated.TrailingFrameTruncated);
                Check.Equal((ViveRawSessionJournalReader.PagePrefix)null,ViveRawSessionJournalReader.PagePrefix.FromVerified(truncated));
                Check.True(!first.SourceAuthenticated&&!first.CurrentAccessAuthorized&&!first.CompleteSessionProven);
            });
            suite.Run("VRAW bound pages reject a valid complete frame appended or a source replaced while reading",()=>{
                long manifest,task,otherManifest,otherTask;byte[] bytes=ViveRawSessionJournalReaderTests.Fixture(out manifest,out task);
                byte[] firstPrefix=new byte[(int)task];Array.Copy(bytes,firstPrefix,firstPrefix.Length);
                var initial=Page(firstPrefix,0,1);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,initial.Status);
                var prefix=ViveRawSessionJournalReader.PagePrefix.FromVerified(initial);Check.True(prefix!=null);
                using(var input=new MemoryStream(bytes,false))
                {var page=ViveRawSessionJournalReader.ReadRawPageBoundToPrefix(input,true,true,prefix,0,1,4194304,5000,CancellationToken.None);
                    Check.Equal(ViveRawSessionReadStatus.SourceChanged,page.Status);Check.Equal((string)null,page.DisplayText);}
                byte[] other=ViveRawSessionJournalReaderTests.Fixture(out otherManifest,out otherTask);
                Check.Equal(task,otherTask);Check.Equal(bytes.Length,other.Length);
                var original=ViveRawSessionJournalReader.PagePrefix.FromVerified(Page(bytes,0,1));
                using(var input=new ReplacedRead(bytes,other,(int)task))
                {var page=ViveRawSessionJournalReader.ReadRawPageBoundToPrefix(input,true,true,original,0,1,4194304,5000,CancellationToken.None);
                    Check.True(page.Status!=ViveRawSessionReadStatus.VerifiedPrefix);Check.Equal((string)null,page.DisplayText);}
            });
            suite.Run("VRAW page can select late records beyond the full display budget",()=>{
                byte[] bytes=ViveRawSessionStreamingTests.ManyObservations(2400);
                using(var input=new MemoryStream(bytes,false))Check.Equal(ViveRawSessionReadStatus.LimitExceeded,ViveRawSessionJournalReader.Read(input,true,true,4194304,5000).Status);
                var page=Page(bytes,2400,2);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,page.Status);Check.Equal(2,page.PageRecordCount);
                Check.Equal(2403L,page.VerifiedRecordCount);Check.True(page.PageHasMoreRecords&&page.DisplayText.Contains("2400,observation,native_frame,int,"));
            });
            suite.Run("VRAW page requires explicit action and discards content on read failure limits or cancellation",()=>{
                long manifest,task;byte[] bytes=ViveRawSessionJournalReaderTests.Fixture(out manifest,out task);
                using(var input=new MemoryStream(bytes,false))
                {var denied=ViveRawSessionJournalReader.ReadRawPage(input,true,false,0,1,4194304,5000,CancellationToken.None);Check.Equal(ViveRawSessionReadStatus.RecoveryRequired,denied.Status);Check.Equal(0L,input.Position);}
                using(var input=new MemoryStream(bytes,false))Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalReader.ReadRawPage(input,false,true,0,1,4194304,5000,CancellationToken.None));
                using(var input=new MemoryStream(bytes,false))Check.Throws<ArgumentOutOfRangeException>(()=>ViveRawSessionJournalReader.ReadRawPage(input,true,true,0,257,4194304,5000,CancellationToken.None));
                foreach(var limited in new[]{Page(bytes,0,1,manifest+1,5000),Page(bytes,0,1,4194304,1)})
                {Check.Equal(ViveRawSessionReadStatus.LimitExceeded,limited.Status);Check.Equal((string)null,limited.DisplayText);Check.Equal(0L,limited.VerifiedBytes);}
                using(var token=new CancellationTokenSource())using(var input=new InterruptedRead(bytes,token,5,false))
                {var stopped=ViveRawSessionJournalReader.ReadRawPage(input,true,true,0,1,4194304,5000,token.Token);Check.Equal(ViveRawSessionReadStatus.Cancelled,stopped.Status);Check.Equal((string)null,stopped.DisplayText);}
                using(var token=new CancellationTokenSource())using(var input=new InterruptedRead(bytes,token,5,true))
                {var failure=ViveRawSessionJournalReader.ReadRawPage(input,true,true,0,1,4194304,5000,token.Token);Check.Equal(ViveRawSessionReadStatus.ReadFailure,failure.Status);Check.Equal((string)null,failure.DisplayText);}
            });
        }
    }
}
