using System;
using System.IO;
using System.Text;

namespace AvatarStroop.Runtime
{
    // UI-neutral owner-side controller. The application must obtain a single
    // existing journal from a file picker and supply a fresh READ-ONLY stream
    // factory. Selecting the factory never opens or reads the journal. This
    // controller has no filesystem, logging, networking or publishing API.
    public sealed class ViveDevelopmentReadOnlyAnalysisController
    {
        public const string UnsavedNotice = "Read-only analysis in memory; no CSV file has been saved.";
        public const string CoverageNotice = "Unknown coverage and gaps remain unknown. A clean close does not prove complete session coverage.";
        public const string IdentityNotice = "Source identities are declared, not authenticated; capture timing and research validity are not established.";
        readonly int maxRows, maxBytes;
        Func<Stream> selected;
        bool busy;
        public ViveDevelopmentReadOnlyAnalysisSummary Summary { get; private set; }
        public string Message { get; private set; }
        public bool HasSelection { get { return selected != null; } }

        public ViveDevelopmentReadOnlyAnalysisController(int maximumRows, int maximumUtf8Bytes)
        {
            if (maximumRows < 1 || maximumRows > 1000000) throw new ArgumentOutOfRangeException("maximumRows");
            if (maximumUtf8Bytes < 1 || maximumUtf8Bytes > 268435456) throw new ArgumentOutOfRangeException("maximumUtf8Bytes");
            maxRows = maximumRows; maxBytes = maximumUtf8Bytes;
            Message = "Select one existing development journal.";
        }
        public void SelectExistingJournal(Func<Stream> openReadOnly)
        {
            if (busy) throw new InvalidOperationException("Analysis is already in progress");
            selected = openReadOnly; Summary = null;
            Message = selected == null ? "No journal selected." : "Journal selected; explicit analysis is required.";
        }
        public bool AnalyzeClean(bool explicitReadOnlyAction)
        { return Analyze(explicitReadOnlyAction, false, false); }
        public bool RecoverVerifiedPrefix(bool explicitReadOnlyAction, bool explicitRecoveryAction)
        { return Analyze(explicitReadOnlyAction, true, explicitRecoveryAction); }

        bool Analyze(bool readOptIn, bool recovery, bool recoveryOptIn)
        {
            if (busy) throw new InvalidOperationException("Analysis is already in progress");
            // Refusal does not invoke a source factory, touch a stream or retain
            // a prior result which might be mistaken for the requested action.
            Summary = null;
            if (!readOptIn || (recovery && !recoveryOptIn))
            { Message = "Explicit read-only and, for recovery, separate recovery consent are required."; return false; }
            if (selected == null) { Message = "Select one existing development journal."; return false; }
            busy = true;
            try
            {
                ViveDevelopmentAnalysisResult result;
                using (Stream source = selected())
                {
                    if (source == null) throw new IOException();
                    result = recovery
                        ? ViveDevelopmentAnalysisEntry.RecoverVerifiedPrefix(source, true, true, maxRows, maxBytes)
                        : ViveDevelopmentAnalysisEntry.AnalyzeClean(source, true, maxRows, maxBytes);
                }
                // Do not retain or expose the potentially sensitive CSV or the
                // source path/declared identity through the presentation model.
                long initialUnknown, recoveryUnknown, recordedGaps;
                CountCoverageRecords(result.Csv, result.EvidenceRows, out initialUnknown, out recoveryUnknown, out recordedGaps);
                Summary = new ViveDevelopmentReadOnlyAnalysisSummary(result.SourceStatus, result.SourceVerifiedBytes,
                    result.EvidenceRows, recovery, initialUnknown, recoveryUnknown, recordedGaps);
                Message = UnsavedNotice; return true;
            }
            catch (InvalidDataException)
            { Message = "No analysis produced: the journal did not pass validation or a configured limit. Recovery is a separate explicit action."; return false; }
            catch (Exception error) when (!(error is OutOfMemoryException || error is StackOverflowException || error is AccessViolationException))
            { Message = "No analysis produced: the selected journal could not be read."; return false; }
            finally { busy = false; }
        }

        // Extractor emits quoted CSV. Examine only the record-kind column;
        // identifiers, timestamps and eye values never enter the UI model.
        static void CountCoverageRecords(string csv, long expectedRows, out long initial, out long recovery, out long gaps)
        {
            initial = recovery = gaps = 0;
            int column=0; long row=0; bool quoted=false, fieldStarted=false, afterQuote=false;
            var kind=new StringBuilder();
            for(int i=0;i<csv.Length;i++)
            {
                char c=csv[i];
                if(quoted)
                {
                    if(c=='"')
                    {
                        if(i+1<csv.Length && csv[i+1]=='"') { if(column==4)kind.Append('"'); i++; }
                        else { quoted=false; afterQuote=true; }
                    }
                    else if(column==4)kind.Append(c);
                    continue;
                }
                if(!fieldStarted && c=='"') { quoted=true; fieldStarted=true; continue; }
                if(afterQuote && c!=',' && c!='\n')throw new InvalidDataException();
                if(c==',' || c=='\n')
                {
                    if(!afterQuote)throw new InvalidDataException();
                    if(c=='\n')
                    {
                        if(column<4)throw new InvalidDataException();
                        string value=kind.ToString();
                        if(row==0 && value!="source_record_kind")throw new InvalidDataException();
                        if(row>0) { if(value=="initial_coverage_unknown")initial++; else if(value=="recovery_coverage_unknown")recovery++; else if(value=="gap")gaps++; }
                        row++;column=0;kind.Length=0;
                    }
                    else column++;
                    fieldStarted=false;afterQuote=false;continue;
                }
                throw new InvalidDataException();
            }
            if(quoted || fieldStarted || column!=0 || row!=expectedRows+1)throw new InvalidDataException();
        }
    }
    public sealed class ViveDevelopmentReadOnlyAnalysisSummary
    {
        public ViveDevelopmentJournalReadStatus SourceStatus { get; private set; }
        public long VerifiedBytes { get; private set; }
        public long EvidenceRows { get; private set; }
        public bool RecoveryRequested { get; private set; }
        public long InitialUnknownCoverageRecords { get; private set; }
        public long RecoveryUnknownCoverageRecords { get; private set; }
        public long GapRecords { get; private set; }
        public bool IncompletePrefix { get { return SourceStatus != ViveDevelopmentJournalReadStatus.CleanClose; } }
        public string Coverage { get { return ViveDevelopmentReadOnlyAnalysisController.CoverageNotice; } }
        public string SaveStatus { get { return ViveDevelopmentReadOnlyAnalysisController.UnsavedNotice; } }
        public string IdentityStatus { get { return ViveDevelopmentReadOnlyAnalysisController.IdentityNotice; } }
        internal ViveDevelopmentReadOnlyAnalysisSummary(ViveDevelopmentJournalReadStatus status, long bytes, long rows, bool recovery,
            long initialUnknown, long recoveryUnknown, long gaps)
        { SourceStatus = status; VerifiedBytes = bytes; EvidenceRows = rows; RecoveryRequested = recovery;
          InitialUnknownCoverageRecords = initialUnknown; RecoveryUnknownCoverageRecords = recoveryUnknown; GapRecords = gaps; }
    }
}
