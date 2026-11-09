using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Unintegrated, read-only stream export primitive. It opens no paths and
    // grants no authority: callers must own an exclusive stable source handle,
    // validate its identity, current authorization and withdrawal status, and
    // fence publication against withdrawal and stale completion. A stream
    // snapshot and digest alone prove none of these. No product call site exists.
    internal static class ViveRawSessionStreamExport
    {
        internal enum Outcome { WrittenAndFlushed, Rejected, Cancelled, OutputUncertain }
        const int MaximumInput=8388608, MaximumOutput=33554432, MaximumRecords=250000;
        const string SyntheticSource="0,manifest,source,utf8_b64,U1lOVEhFVElDX09OTFk=\n";
        const string InstalledSource="0,manifest,source,utf8_b64,SU5TVEFMTEVEX1NES19DT0RFX1BBVEhfT05MWQ==\n";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static bool Same(ViveRawSessionReadResult a,ViveRawSessionReadResult b)
        {
            return a.Status==ViveRawSessionReadStatus.VerifiedPrefix&&b.Status==a.Status&&
                a.TrailingFrameTruncated==b.TrailingFrameTruncated&&a.VerifiedBytes==b.VerifiedBytes&&
                a.VerifiedRecordCount==b.VerifiedRecordCount&&a.VerifiedTaskRecords==b.VerifiedTaskRecords&&
                a.VerifiedObservationRecords==b.VerifiedObservationRecords&&a.VerifiedStatusRecords==b.VerifiedStatusRecords&&
                a.CaptureTerminalObserved==b.CaptureTerminalObserved&&
                a.VerifiedPrefixChainDigestHex==b.VerifiedPrefixChainDigestHex;
        }
        static void Section(MemoryStream target,string name,string content)
        {
            byte[] body=Utf8.GetBytes(content);byte[] head=Utf8.GetBytes("SECTION "+name+" "+body.Length+"\n");
            if(target.Length+head.Length+body.Length+1>MaximumOutput)throw new InvalidOperationException();
            target.Write(head,0,head.Length);target.Write(body,0,body.Length);target.WriteByte(10);
        }
        // All reads use the same open stream. Snapshot stability against
        // concurrent modification is the caller's responsibility; checking
        // length alone is not identity. Caller must exclusively own both
        // streams, and source must already be at offset zero. Flush is not
        // durability and output failure can leave an incomplete destination.
        internal static Outcome Write(Stream input,Stream output,CancellationToken cancellation)
        {
            if(input==null||!input.CanSeek)return Outcome.Rejected;
            try{return WriteExpected(input,input.Length,output,cancellation);}
            catch(Exception e) when(!(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException)){return Outcome.Rejected;}
        }
        // Supports a forward-only already-open view with an independently
        // obtained exact length. The caller must ensure it starts at byte zero
        // and remains the same authorized object; this method cannot verify
        // either on a forward-only stream. In particular it is not a license
        // to consume the writer's one-shot readback or publish its contents.
        internal static Outcome WriteExpected(Stream input,long expected,Stream output,CancellationToken cancellation)
        {
            if(input==null||output==null||Object.ReferenceEquals(input,output)||!input.CanRead||input.CanWrite||!output.CanWrite||!output.CanSeek||expected<8||expected>MaximumInput)return Outcome.Rejected;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if(output.Length!=0||output.Position!=0)return Outcome.Rejected;
                if(input.CanSeek&&(input.Position!=0||input.Length!=expected))return Outcome.Rejected;
                byte[] snapshot=new byte[checked((int)expected)];int copied=0;
                while(copied<snapshot.Length){cancellation.ThrowIfCancellationRequested();int n=input.Read(snapshot,copied,snapshot.Length-copied);if(n<=0)return Outcome.Rejected;copied+=n;}
                cancellation.ThrowIfCancellationRequested();
                if(input.CanSeek&&(input.Length!=expected||input.Position!=expected))return Outcome.Rejected;
                if(input.ReadByte()!=-1)return Outcome.Rejected;
                using(var source=new MemoryStream(snapshot,false))
                using(var staged=new MemoryStream())
                {
                    var projected=ViveRawSessionJournalReader.ProjectTrials(source,true,true,MaximumInput,MaximumRecords,cancellation);
                    if(projected.Status==ViveRawSessionReadStatus.Cancelled)return Outcome.Cancelled;
                    ViveRawTrialReport report;
                    if(projected.Status!=ViveRawSessionReadStatus.VerifiedPrefix||
                        !ViveRawTrialReport.TryBuild(projected,true,out report))return Outcome.Rejected;
                    source.Position=0;
                    var first=ViveRawSessionJournalReader.ReadRawPage(source,true,true,0,256,MaximumInput,MaximumRecords,cancellation);
                    if(first.Status==ViveRawSessionReadStatus.Cancelled)return Outcome.Cancelled;
                    if(!Same(projected,first)||first.DisplayText==null)return Outcome.Rejected;
                    string declaredSource=first.DisplayText.Contains(SyntheticSource)?"SYNTHETIC_ONLY":first.DisplayText.Contains(InstalledSource)?"INSTALLED_SDK_CODE_PATH_ONLY":null;
                    if(declaredSource==null)return Outcome.Rejected;
                    // The omitted incomplete tail has not been parsed and may
                    // contain a partial withdrawal or other sensitive record.
                    if(projected.VerifiedBytes<8||projected.VerifiedBytes>snapshot.Length)return Outcome.Rejected;
                    int prefixLength=checked((int)projected.VerifiedBytes);
                    string binaryHash;
                    using(var sha=SHA256.Create())binaryHash=BitConverter.ToString(sha.ComputeHash(snapshot,0,prefixLength)).Replace("-",String.Empty).ToLowerInvariant();
                    Section(staged,"manifest","scope=VERIFIED_PREFIX_ONLY\ndeclared_source="+declaredSource+"\ntrailing_frame_truncated="+
                        (projected.TrailingFrameTruncated?"true":"false")+"\ncapture_terminal_declared="+
                        (projected.CaptureTerminalObserved?"true":"false")+"\ncomplete_session_proven=false\nwriter_confirmation=UNKNOWN\n"+
                        "tail_coverage=UNKNOWN\nsource_authenticated=false\nconsent_authenticated=false\ncurrent_withdrawal=UNKNOWN\n"+
                        "native_timestamp=NATIVE_TIME_OPAQUE\neye_context=INGEST_NOT_SAMPLE_ALIGNMENT\neye_coverage=UNKNOWN\n"+
                        "raw_input_bytes="+snapshot.Length+"\nverified_bytes="+projected.VerifiedBytes+"\nverified_records="+
                        projected.VerifiedRecordCount+"\nverified_chain_sha256="+projected.VerifiedPrefixChainDigestHex+
                        "\nraw_bytes_included=VERIFIED_PREFIX_ONLY\nunverified_tail_included=false\nverified_vraw_sha256="+binaryHash+
                        "\nraw_typed_records=VERIFIED_PREFIX_ONLY\n");
                    Section(staged,"report_manifest",report.ManifestCsv);Section(staged,"blocks",report.BlocksCsv);
                    Section(staged,"prepared_stimuli",report.PreparedStimuliCsv);Section(staged,"trials",report.TrialsCsv);
                    Section(staged,"ratings",report.RatingsCsv);Section(staged,"eye_statuses",report.StatusesCsv);
                    Section(staged,"safety_events",report.SafetyEventsCsv);
                    Section(staged,"verified_vraw_prefix_base64",Convert.ToBase64String(snapshot,0,prefixLength));
                    long ordinal=0;var page=first;
                    while(true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if(!Same(projected,page)||page.PageStartOrdinal!=ordinal||page.PageRecordCount<1||page.DisplayText==null)return Outcome.Rejected;
                        Section(staged,"raw_typed_records",page.DisplayText);
                        ordinal=checked(ordinal+page.PageRecordCount);
                        if(!page.PageHasMoreRecords)break;
                        source.Position=0;
                        page=ViveRawSessionJournalReader.ReadRawPage(source,true,true,ordinal,256,MaximumInput,MaximumRecords,cancellation);
                        if(page.Status==ViveRawSessionReadStatus.Cancelled)return Outcome.Cancelled;
                    }
                    if(ordinal!=projected.VerifiedRecordCount)return Outcome.Rejected;
                    Section(staged,"export_end","export_assembled=true\nsession_complete_proven=false\n");
                    cancellation.ThrowIfCancellationRequested();
                    // Once the caller's Write begins, cancellation cannot
                    // retract bytes already handed to that stream.
                    byte[] bytes=staged.ToArray();
                    try {
                        if(output.Length!=0||output.Position!=0)return Outcome.Rejected;
                        output.Write(bytes,0,bytes.Length);
                        if(cancellation.IsCancellationRequested)return Outcome.OutputUncertain;
                        if(output.Position!=bytes.Length||output.Length!=bytes.Length)return Outcome.OutputUncertain;
                        output.Flush();
                        if(cancellation.IsCancellationRequested)return Outcome.OutputUncertain;
                    }
                    catch(Exception e) when(!(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException))
                    {return Outcome.OutputUncertain;}
                    return Outcome.WrittenAndFlushed;
                }
            }
            catch(OperationCanceledException){return Outcome.Cancelled;}
            catch(Exception e) when(!(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException))
            {return cancellation.IsCancellationRequested?Outcome.Cancelled:Outcome.Rejected;}
        }
    }
}
