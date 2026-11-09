#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.IO;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveSyntheticReadbackTeeTests
    {
        sealed class BrokenSource : MemoryStream
        {
            internal BrokenSource(byte[] data) : base(data, false) { }
            public override int Read(byte[] buffer, int offset, int count)
            { if (Position > 32) throw new IOException("synthetic source error"); return base.Read(buffer, offset, count); }
        }
        static byte[] Digest(string hex)
        {
            var value = new byte[hex.Length / 2];
            for (int i = 0; i < value.Length; i++) value[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return value;
        }
        static ViveRawSessionReadResult Scan(Stream input)
        { return ViveRawSessionJournalReader.Scan(input, true, true, 4194304, 5000, CancellationToken.None); }
        static byte[] Snapshot(ViveSyntheticReadbackTee tee, ViveRawSessionReadResult result, bool withdrawn = false)
        {
            return tee.SnapshotForTest(result, result.VerifiedRecordCount, result.VerifiedTaskRecords,
                result.VerifiedObservationRecords, result.VerifiedStatusRecords, Digest(result.VerifiedPrefixChainDigestHex), withdrawn);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("bounded synthetic tee matches strict scan and exact four-block bytes", () => {
                byte[] bytes = ViveRawTrialProjectionTests.FourBlocks();
                using (var source = new MemoryStream(bytes, false))
                using (var tee = new ViveSyntheticReadbackTee(source, bytes.Length, bytes.Length))
                {
                    var result = Scan(tee); var captured = Snapshot(tee, result);
                    Check.True(captured != null && captured.Length == bytes.Length);
                    for (int i = 0; i < bytes.Length; i++) Check.Equal(bytes[i], captured[i]);
                    Check.True(Snapshot(tee, result, true) == null);
                    Check.True(tee.SnapshotForTest(result, result.VerifiedRecordCount, result.VerifiedTaskRecords,
                        result.VerifiedObservationRecords, result.VerifiedStatusRecords, new byte[32], false) == null);
                    result.VerifiedTaskRecords++;
                    Check.True(tee.SnapshotForTest(result, result.VerifiedRecordCount, result.VerifiedTaskRecords - 1,
                        result.VerifiedObservationRecords, result.VerifiedStatusRecords, Digest(result.VerifiedPrefixChainDigestHex), false) == null);
                }
            });
            suite.Run("synthetic tee over budget leaves the original strict scan unaffected", () => {
                byte[] bytes = ViveRawTrialProjectionTests.FourBlocks();
                using (var source = new MemoryStream(bytes, false))
                using (var tee = new ViveSyntheticReadbackTee(source, bytes.Length, bytes.Length - 1))
                {
                    var result = Scan(tee);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix, result.Status);
                    Check.True(result.CaptureTerminalObserved && Snapshot(tee, result) == null);
                }
            });
            suite.Run("synthetic tee capture failure does not fail the original strict scan", () => {
                byte[] bytes = ViveRawTrialProjectionTests.FourBlocks();
                using (var source = new MemoryStream(bytes, false))
                using (var tee = new ViveSyntheticReadbackTee(source, bytes.Length, bytes.Length, () => { throw new IOException("synthetic capture failure"); }))
                {
                    var result = Scan(tee);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix, result.Status);
                    Check.True(result.CaptureTerminalObserved && Snapshot(tee, result) == null);
                }
            });
            suite.Run("synthetic tee allocation failure and hard limit do not prevent scanning", () => {
                byte[] bytes = ViveRawTrialProjectionTests.FourBlocks();
                using (var source = new MemoryStream(bytes, false))
                using (var tee = new ViveSyntheticReadbackTee(source, bytes.Length, Int32.MaxValue, null, n => { throw new OutOfMemoryException("synthetic allocation failure"); }))
                {
                    var result = Scan(tee);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix, result.Status);
                    Check.True(result.CaptureTerminalObserved && Snapshot(tee, result) == null);
                }
                bool allocationAttempted = false;
                using (var source = new MemoryStream(bytes, false))
                using (var tee = new ViveSyntheticReadbackTee(source, ViveSyntheticReadbackTee.MaximumCaptureBytes + 1L, Int32.MaxValue, null,
                    n => { allocationAttempted = true; return new byte[n]; }))
                {
                    var result = Scan(tee);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix, result.Status);
                    Check.True(!allocationAttempted && Snapshot(tee, result) == null);
                }
            });
            suite.Run("synthetic tee does not expose corrupt, incomplete or unread data", () => {
                byte[] bytes = ViveRawTrialProjectionTests.FourBlocks(); bytes[bytes.Length - 1] ^= 1;
                using (var source = new MemoryStream(bytes, false))
                using (var tee = new ViveSyntheticReadbackTee(source, bytes.Length, bytes.Length))
                {
                    var result = Scan(tee);
                    Check.True(result.Status != ViveRawSessionReadStatus.VerifiedPrefix);
                    Check.True(tee.SnapshotForTest(result, 0, 0, 0, 0, new byte[32], false) == null);
                }
                byte[] truncated = ViveRawTrialProjectionTests.FourBlocks(truncateTerminal: true);
                using (var source = new MemoryStream(truncated, false))
                using (var tee = new ViveSyntheticReadbackTee(source, truncated.Length, truncated.Length))
                {
                    var result = Scan(tee);
                    Check.True(result.TrailingFrameTruncated && Snapshot(tee, result) == null);
                }
            });
            suite.Run("synthetic tee propagates source failure and exposes no capture", () => {
                byte[] bytes = ViveRawTrialProjectionTests.FourBlocks();
                using (var source = new BrokenSource(bytes))
                using (var tee = new ViveSyntheticReadbackTee(source, bytes.Length, bytes.Length))
                {
                    var result = Scan(tee);
                    Check.Equal(ViveRawSessionReadStatus.ReadFailure, result.Status);
                    Check.True(tee.SnapshotForTest(result, 0, 0, 0, 0, new byte[32], false) == null);
                }
            });
        }
    }
}
#endif
