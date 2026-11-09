using System;
using System.IO;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentReadOnlyAnalysisControllerTests
    {
        static byte[] Fixture(bool clean)
        {
            var id = new ViveDevelopmentStreamIdentity("session", "anonymous", null, ViveDevelopmentMeasurementRole.Participant,
                "stream", "device", "clock", "mapping", ViveDevelopmentOperatorStatus.NotApplicable);
            using (var memory = new MemoryStream())
            {
                var journal = ViveDevelopmentJournal.CreateV2WithIdentity(memory, () => {}, id, ViveDevelopmentDataSource.SyntheticFixture, true, true);
                Check.True(journal.TryAppendInitialUnknownCoverage(0, 0, 1, 2));
                var provenance = ViveDevelopmentProvenance.FromObserved(id, 1, 2, 3, 2, 3, true);
                Check.True(journal.TryAppendObservation(ViveDevelopmentExportRecord.Observation(provenance,
                    ViveDevelopmentDataSource.SyntheticFixture, 0, 0, 0, 1, 2, new Vec3(0,0,1), true,
                    new Vec3(0,0,1), "tracking-local", true)));
                if (clean) Check.True(journal.Close());
                return memory.ToArray();
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE read-only controller requires explicit actions and does not open on selection", () => {
                var c = new ViveDevelopmentReadOnlyAnalysisController(100,100000); int opens=0; byte[] bytes=Fixture(true);
                c.SelectExistingJournal(() => { opens++; return new MemoryStream(bytes,false); });
                Check.Equal(0, opens); Check.True(!c.AnalyzeClean(false)); Check.True(!c.RecoverVerifiedPrefix(true,false));
                Check.Equal(0, opens); Check.True(c.AnalyzeClean(true)); Check.Equal(1, opens);
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,c.Summary.SourceStatus);
                Check.Equal(2L,c.Summary.EvidenceRows); Check.True(c.Summary.Coverage.Contains("unknown"));
                Check.Equal(1L,c.Summary.InitialUnknownCoverageRecords); Check.Equal(0L,c.Summary.RecoveryUnknownCoverageRecords);
                Check.Equal(0L,c.Summary.GapRecords);
                Check.True(c.Summary.SaveStatus.Contains("no CSV file has been saved"));
                Check.True(!c.Summary.IncompletePrefix && !c.Summary.RecoveryRequested);
                c.SelectExistingJournal(null); Check.True(c.Summary==null && !c.HasSelection);
            });
            suite.Run("VIVE read-only controller requires a separate recovery action after an unclean journal", () => {
                var c=new ViveDevelopmentReadOnlyAnalysisController(100,100000); byte[] bytes=Fixture(false); int opens=0;
                c.SelectExistingJournal(() => {opens++; return new MemoryStream(bytes,false);});
                Check.True(!c.AnalyzeClean(true)); Check.True(c.Summary==null); Check.Equal(1,opens);
                Check.True(!c.RecoverVerifiedPrefix(true,false)); Check.Equal(1,opens);
                Check.True(c.RecoverVerifiedPrefix(true,true)); Check.Equal(2,opens);
                Check.True(c.Summary.IncompletePrefix && c.Summary.RecoveryRequested);
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,c.Summary.SourceStatus);
            });
            suite.Run("VIVE read-only controller sanitizes permission and malformed input failures", () => {
                var c=new ViveDevelopmentReadOnlyAnalysisController(100,100000);
                c.SelectExistingJournal(() => {throw new UnauthorizedAccessException("private path and identity");});
                Check.True(!c.AnalyzeClean(true)); Check.True(c.Summary==null && !c.Message.Contains("private"));
                c.SelectExistingJournal(() => new MemoryStream(new byte[]{1,2,3},false));
                Check.True(!c.RecoverVerifiedPrefix(true,true)); Check.True(c.Summary==null && !c.Message.Contains("private"));
            });
        }
    }
}
