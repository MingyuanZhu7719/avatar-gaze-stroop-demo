using System;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic byte streams only; no recording, publication or disk access.
    public static class ViveRawSessionStreamingTests
    {
        internal static byte[] ManyObservations(int count)
        {
            long manifest,task;byte[] seed=ViveRawSessionJournalReaderTests.Fixture(out manifest,out task);
            int eyeLength=ContinuousJournal.Int(seed,(int)task);byte[] eye=new byte[eyeLength];
            Buffer.BlockCopy(seed,(int)task+8,eye,0,eyeLength);
            using(var output=new MemoryStream())
            {
                output.Write(seed,0,seed.Length);byte[] prior=new byte[32];Buffer.BlockCopy(seed,seed.Length-32,prior,0,32);
                for(int i=0;i<count;i++)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes((long)i+3),0,eye,0,8);
                    Buffer.BlockCopy(BitConverter.GetBytes(i+10),0,eye,9,4);
                    Buffer.BlockCopy(BitConverter.GetBytes(i+124),0,eye,13,4);
                    Buffer.BlockCopy(BitConverter.GetBytes((double)i+3),0,eye,17,8);
                    Buffer.BlockCopy(BitConverter.GetBytes((long)i+8),0,eye,25,8);
                    Buffer.BlockCopy(BitConverter.GetBytes((double)i+3.1),0,eye,33,8);
                    Buffer.BlockCopy(BitConverter.GetBytes((double)i+3.2),0,eye,41,8);
                    byte[] digest,frame=ViveRawSessionJournalCodec.Frame(eye,prior,false,out digest);
                    output.Write(frame,0,frame.Length);prior=digest;
                }
                return output.ToArray();
            }
        }
        sealed class CancellingStream:Stream
        {
            readonly Stream input;readonly CancellationTokenSource cancellation;int remaining;
            internal CancellingStream(byte[] data,CancellationTokenSource token,int reads){input=new MemoryStream(data,false);cancellation=token;remaining=reads;}
            public override bool CanRead {get{return true;}}public override bool CanSeek {get{return false;}}public override bool CanWrite {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n){int read=input.Read(b,o,n);if(--remaining==0)cancellation.Cancel();return read;}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
            protected override void Dispose(bool disposing){if(disposing)input.Dispose();base.Dispose(disposing);}
        }
        sealed class CancellationThrowingStream:Stream
        {
            readonly Stream input;readonly CancellationTokenSource token;readonly bool cancel;int remaining;
            internal CancellationThrowingStream(byte[] data,CancellationTokenSource token,bool cancel,int reads)
            {input=new MemoryStream(data,false);this.token=token;this.cancel=cancel;remaining=reads;}
            public override bool CanRead {get{return true;}}public override bool CanSeek {get{return false;}}public override bool CanWrite {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n){if(--remaining==0){if(cancel)token.Cancel();throw new OperationCanceledException();}return input.Read(b,o,n);}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
            protected override void Dispose(bool disposing){if(disposing)input.Dispose();base.Dispose(disposing);}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VRAW streaming scan handles larger-than-display synthetic sessions without retaining rows",()=>{
                byte[] data=ManyObservations(2400);
                using(var stream=new MemoryStream(data,false))
                {
                    var display=ViveRawSessionJournalReader.Read(stream,true,true,4194304,5000);
                    Check.Equal(ViveRawSessionReadStatus.LimitExceeded,display.Status);Check.Equal((string)null,display.DisplayText);
                }
                using(var stream=new MemoryStream(data,false))
                {
                    var scan=ViveRawSessionJournalReader.Scan(stream,true,true,4194304,5000,CancellationToken.None);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,scan.Status);Check.Equal((string)null,scan.DisplayText);
                    Check.Equal((long)data.Length,scan.VerifiedBytes);Check.Equal(1L,scan.VerifiedTaskRecords);Check.Equal(2401L,scan.VerifiedObservationRecords);
                    Check.True(!scan.TrailingFrameTruncated&&!scan.SourceAuthenticated&&!scan.ConsentAuthenticated&&!scan.CompleteSessionProven&&!scan.DurableStorageProven);
                }
            });
            suite.Run("VRAW scan verifies complete prefix only and discards state on budget or corruption",()=>{
                long manifest,task;byte[] data=ViveRawSessionJournalReaderTests.Fixture(out manifest,out task);
                byte[] truncated=new byte[data.Length-1];Buffer.BlockCopy(data,0,truncated,0,truncated.Length);
                using(var stream=new MemoryStream(truncated,false))
                {
                    var scan=ViveRawSessionJournalReader.Scan(stream,true,true,4194304,5000,CancellationToken.None);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,scan.Status);Check.True(scan.TrailingFrameTruncated);
                    Check.Equal(task,scan.VerifiedBytes);Check.Equal(1L,scan.VerifiedTaskRecords);Check.Equal(0L,scan.VerifiedObservationRecords);
                }
                using(var stream=new MemoryStream(data,false))
                {var scan=ViveRawSessionJournalReader.Scan(stream,true,true,manifest+1,5000,CancellationToken.None);Check.Equal(ViveRawSessionReadStatus.LimitExceeded,scan.Status);Check.Equal(0L,scan.VerifiedBytes);}
                data[data.Length-1]^=1;
                using(var stream=new MemoryStream(data,false))
                {var scan=ViveRawSessionJournalReader.Scan(stream,true,true,4194304,5000,CancellationToken.None);Check.Equal(ViveRawSessionReadStatus.Invalid,scan.Status);Check.Equal(0L,scan.VerifiedTaskRecords);Check.Equal(0L,scan.VerifiedBytes);}
            });
            suite.Run("VRAW scan requires explicit selection and cancels without returning partial counts",()=>{
                long manifest,task;byte[] data=ViveRawSessionJournalReaderTests.Fixture(out manifest,out task);
                using(var stream=new MemoryStream(data,false))
                {var r=ViveRawSessionJournalReader.Scan(stream,true,false,4194304,5000,CancellationToken.None);Check.Equal(ViveRawSessionReadStatus.RecoveryRequired,r.Status);Check.Equal(0L,stream.Position);}
                using(var token=new CancellationTokenSource())using(var stream=new CancellingStream(data,token,5))
                {var r=ViveRawSessionJournalReader.Scan(stream,true,true,4194304,5000,token.Token);Check.Equal(ViveRawSessionReadStatus.Cancelled,r.Status);Check.Equal(0L,r.VerifiedBytes);Check.Equal((string)null,r.DisplayText);}
                using(var token=new CancellationTokenSource())using(var stream=new MemoryStream(data,false))
                {token.Cancel();var r=ViveRawSessionJournalReader.Scan(stream,true,true,4194304,5000,token.Token);Check.Equal(ViveRawSessionReadStatus.Cancelled,r.Status);Check.Equal(0L,stream.Position);}
                using(var token=new CancellationTokenSource())using(var stream=new CancellationThrowingStream(data,token,true,5))
                {var r=ViveRawSessionJournalReader.Scan(stream,true,true,4194304,5000,token.Token);Check.Equal(ViveRawSessionReadStatus.Cancelled,r.Status);Check.Equal(0L,r.VerifiedBytes);}
                // Exact-budget EOF uses a final one-byte probe; cancellation
                // thrown from that probe must also discard staged counts.
                using(var token=new CancellationTokenSource())using(var stream=new CancellationThrowingStream(data,token,true,11))
                {var r=ViveRawSessionJournalReader.Scan(stream,true,true,data.Length,5000,token.Token);Check.Equal(ViveRawSessionReadStatus.Cancelled,r.Status);Check.Equal(0L,r.VerifiedBytes);}
                using(var token=new CancellationTokenSource())using(var stream=new CancellationThrowingStream(data,token,false,5))
                {var r=ViveRawSessionJournalReader.Scan(stream,true,true,4194304,5000,token.Token);Check.Equal(ViveRawSessionReadStatus.ReadFailure,r.Status);Check.Equal(0L,r.VerifiedBytes);}
            });
        }
    }
}
