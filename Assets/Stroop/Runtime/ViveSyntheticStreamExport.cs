#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Test-only stream candidate. It opens no paths, does not authenticate
    // consent or sources, and cannot be compiled into a Unity player.
    internal static class ViveSyntheticStreamExport
    {
        internal enum Outcome { WrittenAndFlushed, Rejected, Cancelled, OutputUncertain }
        const int MaximumInput=8388608, MaximumOutput=33554432, MaximumRecords=250000;
        const string SyntheticSource="0,manifest,source,utf8_b64,U1lOVEhFVElDX09OTFk=\n";
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
        // The source is deliberately MemoryStream-only; ToArray freezes an
        // independent immutable snapshot for all validation and page scans.
        // The caller must exclusively own the empty output throughout this
        // call. The output remains caller-owned; flush is not durability.
        internal static Outcome Write(MemoryStream input,Stream output,CancellationToken cancellation)
        {
            if(input==null||output==null||Object.ReferenceEquals(input,output)||!output.CanWrite||!output.CanSeek)return Outcome.Rejected;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if(output.Length!=0||output.Position!=0)return Outcome.Rejected;
                if(input.Length<8||input.Length>MaximumInput)return Outcome.Rejected;
                byte[] snapshot=input.ToArray();
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
                    if(!Same(projected,first)||first.DisplayText==null||!first.DisplayText.Contains(SyntheticSource))return Outcome.Rejected;
                    // The omitted incomplete tail has not been parsed and may
                    // contain a partial withdrawal or other sensitive record.
                    if(projected.VerifiedBytes<8||projected.VerifiedBytes>snapshot.Length)return Outcome.Rejected;
                    int prefixLength=checked((int)projected.VerifiedBytes);
                    string binaryHash;
                    using(var sha=SHA256.Create())binaryHash=BitConverter.ToString(sha.ComputeHash(snapshot,0,prefixLength)).Replace("-",String.Empty).ToLowerInvariant();
                    Section(staged,"manifest","scope=VERIFIED_PREFIX_ONLY\nsource=SYNTHETIC_ONLY\ntrailing_frame_truncated="+
                        (projected.TrailingFrameTruncated?"true":"false")+"\ncapture_terminal_declared="+
                        (projected.CaptureTerminalObserved?"true":"false")+"\ncomplete_session_proven=false\nwriter_confirmation=UNKNOWN\n"+
                        "tail_coverage=UNKNOWN\nsource_authenticated=false\nconsent_authenticated=false\ncurrent_withdrawal=UNKNOWN\n"+
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
            {return Outcome.Rejected;}
        }
    }
}
#endif
