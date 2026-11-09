#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.IO;

namespace AvatarStroop.Runtime
{
    // File-free synthetic feasibility check only. This type is excluded from
    // Unity; it is wired only to the synthetic native sink fixture, not to a
    // product export target, consent or publication fence. A copied byte array
    // cannot be revoked once handed to another caller. Never use with participant data.
    internal sealed class ViveSyntheticReadbackTee : Stream
    {
        internal const int MaximumCaptureBytes = 8388608;
        readonly Stream input;
        readonly long expected;
        readonly Action captureHook;
        byte[] capture;
        long read;
        bool eof, failed, disposed;

        internal ViveSyntheticReadbackTee(Stream input, long expected, int limit, Action captureHook = null, Func<int, byte[]> allocate = null)
        {
            if (input == null || !input.CanRead || expected < 0 || limit < 0) throw new ArgumentException("Invalid synthetic tee input.");
            this.input = input; this.expected = expected; this.captureHook = captureHook;
            if (expected <= Math.Min(limit, MaximumCaptureBytes))
            {
                try
                {
                    capture = allocate == null ? new byte[(int)expected] : allocate((int)expected);
                    if (capture == null || capture.Length != expected) Abandon();
                }
                catch (OutOfMemoryException) { Abandon(); }
                catch (Exception error) when (!(error is StackOverflowException || error is AccessViolationException)) { Abandon(); }
            }
        }

        void Abandon()
        {
            failed = true;
            if (capture != null) { Array.Clear(capture, 0, capture.Length); capture = null; }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (disposed) throw new ObjectDisposedException("ViveSyntheticReadbackTee");
            int n;
            try { n = input.Read(buffer, offset, count); }
            catch { Abandon(); throw; }
            if (n == 0) { if (count > 0) eof = true; return n; }
            try
            {
                if (read > expected - n) Abandon();
                else if (capture != null)
                {
                    if (captureHook != null) captureHook();
                    Buffer.BlockCopy(buffer, offset, capture, checked((int)read), n);
                }
                read = checked(read + n);
            }
            catch (Exception error) when (!(error is StackOverflowException || error is AccessViolationException))
            { Abandon(); }
            return n;
        }

        // Test access only after the sink's own strict readback predicates.
        // This is not a real consent, source identity or publication receipt.
        internal byte[] SnapshotForTest(ViveRawSessionReadResult result, long records, long tasks, long eyes, long statuses, byte[] digest, bool withdrawn)
        {
            if (disposed || withdrawn || failed || !eof || capture == null || read != expected ||
                !ViveRawSessionJournalSink.ReadbackMatches(result, expected, records, tasks, eyes, statuses, digest)) return null;
            return (byte[])capture.Clone();
        }
        protected override void Dispose(bool disposing) { if (!disposed) Abandon(); disposed = true; base.Dispose(disposing); }
        public override bool CanRead { get { return !disposed && input.CanRead; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
#endif
