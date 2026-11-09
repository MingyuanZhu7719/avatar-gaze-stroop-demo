using System;
using System.IO;

namespace AvatarStroop.Runtime
{
    // A caller-owned stream and an in-memory analysis result. This is neither a
    // file picker nor a publication API; it never opens a path or writes a file.
    // The CSV may contain sensitive eye data and identifiers; callers must not
    // log, transmit, or persist it without the applicable explicit authorization.
    public sealed class ViveDevelopmentAnalysisResult
    {
        public string Csv {get;private set;}
        public ViveDevelopmentJournalReadStatus SourceStatus {get;private set;}
        public string SourceJournalId {get;private set;}
        public long SourceVerifiedBytes {get;private set;}
        public long EvidenceRows {get;private set;}
        public bool RecoveredIncompletePrefix {get{return SourceStatus!=ViveDevelopmentJournalReadStatus.CleanClose;}}
        public bool DiskPublicationAvailable {get{return false;}}
        public bool SourceIdentityAuthenticated {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool ResearchAcceptanceEstablished {get{return false;}}
        public bool CompleteSessionProven {get{return false;}}
        public bool ContainsPotentiallySensitiveData {get{return true;}}
        internal ViveDevelopmentAnalysisResult(ViveDevelopmentJournalExtractionResult extraction)
        {
            if(extraction==null)throw new ArgumentNullException("extraction");
            Csv=extraction.Csv;SourceStatus=extraction.SourceStatus;SourceJournalId=extraction.SourceJournalId;
            SourceVerifiedBytes=extraction.VerifiedBytes;EvidenceRows=extraction.EvidenceRows;
        }
    }

    public static class ViveDevelopmentAnalysisEntry
    {
        // Track actual input I/O failures separately. The current extractor may
        // classify a read failure before the first verified header as invalid
        // data; the UI must not misreport inaccessible input as corrupt evidence.
        // The wrapper never disposes the caller's stream or changes its contents.
        sealed class TrackedInput : Stream
        {
            readonly Stream input;
            public bool ReadFailed {get;private set;}
            public TrackedInput(Stream input){this.input=input;}
            public override bool CanRead {get{try{return input.CanRead;}catch(Exception e) when(IsReadError(e)){ReadFailed=true;throw;}}}
            public override bool CanSeek {get{return false;}}
            public override bool CanWrite {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}
            public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] buffer,int offset,int count)
            {try{return input.Read(buffer,offset,count);}catch(Exception e) when(IsReadError(e)){ReadFailed=true;throw;}}
            public override void Flush(){throw new NotSupportedException();}
            public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long value){throw new NotSupportedException();}
            public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
            static bool IsReadError(Exception e)
            {
                // Arbitrary caller-provided streams can throw custom exceptions
                // containing a path. Fatal process failures are not converted.
                return !(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException);
            }
        }
        // The ordinary path is deliberately clean-close only. A close marker is
        // structural evidence, not a claim that a study or all trials completed.
        public static ViveDevelopmentAnalysisResult AnalyzeClean(Stream input,bool explicitReadOnlyOptIn,int maximumRows,int maximumUtf8Bytes)
        {return Analyze(input,explicitReadOnlyOptIn,false,false,maximumRows,maximumUtf8Bytes);}

        // This separate entry requires an additional explicit recovery decision.
        // Any returned evidence row retains the actual source status and verified
        // prefix boundary; no missing frames or coverage are filled in.
        public static ViveDevelopmentAnalysisResult RecoverVerifiedPrefix(Stream input,bool explicitReadOnlyOptIn,
            bool explicitIncompleteRecoveryOptIn,int maximumRows,int maximumUtf8Bytes)
        {return Analyze(input,explicitReadOnlyOptIn,true,explicitIncompleteRecoveryOptIn,maximumRows,maximumUtf8Bytes);}

        static ViveDevelopmentAnalysisResult Analyze(Stream input,bool readOptIn,bool recoveryRequested,bool recoveryOptIn,int maximumRows,int maximumUtf8Bytes)
        {
            // Check both choices before accessing even Stream.CanRead: a refused
            // action cannot consume the stream or inspect its metadata.
            if(!readOptIn)throw new InvalidOperationException("Explicit read-only analysis opt-in is required");
            if(recoveryRequested&&!recoveryOptIn)throw new InvalidOperationException("Explicit incomplete-prefix recovery opt-in is required");
            if(input==null)throw new ArgumentNullException("input");
            var tracked=new TrackedInput(input);
            try
            {
                var extracted=ViveDevelopmentJournalExtraction.Extract(tracked,recoveryRequested,maximumRows,maximumUtf8Bytes);
                // A wrapped reader exception can otherwise look like an invalid
                // prefix, which recovery would permit. Never return partial CSV
                // when reading the source itself failed.
                if(tracked.ReadFailed)throw new IOException("Journal reading failed; no analysis was produced");
                return new ViveDevelopmentAnalysisResult(extracted);
            }
            catch(InvalidDataException)
            {
                if(tracked.ReadFailed)throw new IOException("Journal reading failed; no analysis was produced");
                throw new InvalidDataException("Journal validation or extraction failed; no analysis was produced");
            }
            catch(IOException)
            {throw new IOException("Journal reading failed; no analysis was produced");}
            catch(UnauthorizedAccessException)
            {throw new IOException("Journal reading failed; no analysis was produced");}
            catch(System.Security.SecurityException)
            {throw new IOException("Journal reading failed; no analysis was produced");}
            catch(NotSupportedException)
            {if(tracked.ReadFailed)throw new IOException("Journal reading failed; no analysis was produced");throw;}
            catch(ObjectDisposedException)
            {if(tracked.ReadFailed)throw new IOException("Journal reading failed; no analysis was produced");throw;}
            catch(ArgumentException)
            {if(tracked.ReadFailed)throw new IOException("Journal reading failed; no analysis was produced");throw;}
            catch(InvalidOperationException)
            {if(tracked.ReadFailed)throw new IOException("Journal reading failed; no analysis was produced");throw;}
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {if(tracked.ReadFailed)throw new IOException("Journal reading failed; no analysis was produced");throw;}
        }
    }
}
