using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentAnalysisEntryTests
    {
        sealed class ProbeStream : MemoryStream
        {
            public int Reads,CapabilityChecks;
            public ProbeStream(byte[] bytes):base(bytes,false){}
            public override bool CanRead {get{CapabilityChecks++;return base.CanRead;}}
            public override int Read(byte[] bytes,int offset,int count){Reads++;return base.Read(bytes,offset,count);}
        }
        sealed class FaultStream : MemoryStream
        {
            public FaultStream(byte[] bytes):base(bytes,false){}
            public override int Read(byte[] bytes,int offset,int count){throw new IOException("private source path");}
        }
        sealed class CapabilityFaultStream : MemoryStream
        {
            readonly Func<Exception> failure;
            public CapabilityFaultStream(byte[] bytes,Func<Exception> failure):base(bytes,false){this.failure=failure;}
            public override bool CanRead {get{throw failure();}}
        }
        sealed class DelayedFaultStream : MemoryStream
        {
            readonly long failAfter;readonly Func<Exception> failure;
            public DelayedFaultStream(byte[] bytes,long failAfter,Func<Exception> failure):base(bytes,false)
            {this.failAfter=failAfter;this.failure=failure;}
            public override int Read(byte[] bytes,int offset,int count)
            {
                if(Position>=failAfter)throw failure();
                return base.Read(bytes,offset,(int)Math.Min(count,failAfter-Position));
            }
        }
        static ViveDevelopmentStreamIdentity Identity()
        {return new ViveDevelopmentStreamIdentity("session","anonymous-participant",null,ViveDevelopmentMeasurementRole.Participant,"participant/epoch","device","clock","mapping-v3",ViveDevelopmentOperatorStatus.NotApplicable);}
        static string Enc(string value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));}
        static byte[] Fixture(bool versionTwo,bool clean,bool realLabel=false)
        {
            var identity=Identity();var memory=new MemoryStream();ViveDevelopmentJournal journal;
            var source=realLabel?ViveDevelopmentDataSource.RealVive:ViveDevelopmentDataSource.SyntheticFixture;
            ViveDevelopmentProvenance provenance;
            if(versionTwo)
            {
                journal=ViveDevelopmentJournal.CreateV2WithIdentity(memory,()=>{},identity,source,true,true);
                provenance=ViveDevelopmentProvenance.FromObserved(identity,-21,10,11,9.9,10.2,true);
                Check.True(journal.TryAppendInitialUnknownCoverage(0,0,9.8,10.2));
            }
            else
            {
                provenance=new ViveDevelopmentProvenance("session","anonymous-participant","operator",ViveDevelopmentMeasurementRole.Participant,
                    "participant/epoch","device","clock","mapping-v3",-21,10,11,9.9,10.2,true);
                journal=new ViveDevelopmentJournal(memory,()=>{},provenance,source,true,true);
            }
            var first=ViveDevelopmentExportRecord.Observation(provenance,source,0,0,0,-42,2,
                new Vec3(0,0,2),true,new Vec3(0,0,1),"tracking-local",true);
            Check.True(journal.TryAppendObservation(first));
            if(versionTwo)Check.True(journal.TryAppendRecoveryUnknownCoverage(0,0,10.2,10.4,ViveDevelopmentGapReason.Stale));
            else Check.True(journal.TryAppendObservation(ViveDevelopmentExportRecord.Gap(provenance,source,1,0,0,10.2,10.4,ViveDevelopmentGapReason.Stale)));
            if(versionTwo)
                Check.True(journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,10.4,0,0,"UNRESOLVED","trial-1",
                    "AE2;SIMULATION_DEVELOPMENT;7;"+Enc("trial_planned")+";1;"+Enc("planned_index")+";"+Enc("17")));
            else Check.True(journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.StimulusPresented,10.4,0,0,"MainTask","trial-1","synthetic detail"));
            if(clean)Check.True(journal.Close());return memory.ToArray();
        }
        static List<string> CsvLine(string text)
        {
            var values=new List<string>();var field=new StringBuilder();bool quoted=false;
            for(int i=0;i<text.Length;i++)
            {char c=text[i];if(c=='"'){if(quoted&&i+1<text.Length&&text[i+1]=='"'){field.Append('"');i++;}else quoted=!quoted;}else if(c==','&&!quoted){values.Add(field.ToString());field.Clear();}else field.Append(c);}
            if(quoted)throw new InvalidDataException("Invalid synthetic CSV");values.Add(field.ToString());return values;
        }
        static Dictionary<string,string> Row(string header,string line)
        {
            var keys=CsvLine(header);var values=CsvLine(line);Check.Equal(keys.Count,values.Count);
            var row=new Dictionary<string,string>();for(int i=0;i<keys.Count;i++)row.Add(keys[i],values[i]);return row;
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE analysis entry checks both opt-ins before accessing the supplied stream",()=>{
                var probe=new ProbeStream(Fixture(true,true));
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentAnalysisEntry.AnalyzeClean(probe,false,100,100000));
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentAnalysisEntry.RecoverVerifiedPrefix(probe,false,true,100,100000));
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentAnalysisEntry.RecoverVerifiedPrefix(probe,true,false,100,100000));
                Check.Equal(0,probe.Reads);Check.Equal(0,probe.CapabilityChecks);Check.Equal(0L,probe.Position);
            });
            suite.Run("VIVE analysis entry clean v1 preserves source and gaze data without authorizing disk publication",()=>{
                var bytes=Fixture(false,true);var input=new ProbeStream(bytes);
                var result=ViveDevelopmentAnalysisEntry.AnalyzeClean(input,true,100,100000);
                Check.True(input.Reads>0&&input.CanRead);Check.True(!result.RecoveredIncompletePrefix&&!result.DiskPublicationAvailable);
                Check.True(!result.SourceIdentityAuthenticated&&!result.HardwareCaptureTimeVerified&&!result.ResearchAcceptanceEstablished&&!result.CompleteSessionProven&&result.ContainsPotentiallySensitiveData);
                Check.Equal((long)bytes.Length,result.SourceVerifiedBytes);Check.Equal(3L,result.EvidenceRows);
                var lines=result.Csv.TrimEnd('\n').Split('\n');var row=Row(lines[0],lines[1]);
                Check.Equal("CleanClose",row["source_status"]);Check.Equal(result.SourceJournalId,row["source_journal_id"]);
                Check.Equal("-21",row["native_eye_timestamp_opaque"]);Check.Equal("2",row["raw_combined_sdk_direction_z"]);Check.Equal("1",row["derived_direction_z"]);
                Check.Equal("Stale",Row(lines[0],lines[2])["gap_reason"]);
            });
            suite.Run("VIVE analysis entry clean v2 retains absent operator and unknown coverage",()=>{
                var result=ViveDevelopmentAnalysisEntry.AnalyzeClean(new MemoryStream(Fixture(true,true),false),true,100,100000);
                var lines=result.Csv.TrimEnd('\n').Split('\n');Check.Equal(5,lines.Length);
                var initial=Row(lines[0],lines[1]);Check.Equal("NotApplicable",initial["operator_status"]);Check.Equal("",initial["operator_id"]);
                Check.Equal("false",initial["identity_authenticated"]);Check.Equal("initial_coverage_unknown",initial["source_record_kind"]);
                Check.Equal("9.8",initial["coverage_start_local_seconds"]);Check.Equal("10.2",initial["coverage_end_local_seconds"]);
                var sample=Row(lines[0],lines[2]);Check.Equal("2",sample["raw_combined_sdk_direction_z"]);Check.Equal("1",sample["derived_direction_z"]);
                var recovery=Row(lines[0],lines[3]);Check.Equal("recovery_coverage_unknown",recovery["source_record_kind"]);Check.Equal("Stale",recovery["coverage_reason_detected"]);
                Check.Equal("SIMULATION_DEVELOPMENT",Row(lines[0],lines[4])["task_origin_declared"]);
            });
            suite.Run("VIVE analysis entry preserves real-source declaration without claiming authentic identity or study validity",()=>{
                // This is a wholly synthetic test stream carrying a RealVive label;
                // it does not obtain or persist hardware data.
                var result=ViveDevelopmentAnalysisEntry.AnalyzeClean(new MemoryStream(Fixture(true,true,true),false),true,100,100000);
                var lines=result.Csv.TrimEnd('\n').Split('\n');var sample=Row(lines[0],lines[2]);
                Check.Equal("RealVive",sample["data_source"]);Check.Equal("false",sample["identity_authenticated"]);
                Check.Equal("false",sample["hardware_capture_time_verified"]);Check.Equal("false",sample["apparatus_mapping_validated"]);
                Check.True(!result.SourceIdentityAuthenticated&&!result.ResearchAcceptanceEstablished&&!result.CompleteSessionProven);
                var task=Row(lines[0],lines[4]);Check.Equal("SIMULATION_DEVELOPMENT",task["task_origin_declared"]);
                ViveAuthoritativeTaskPayload payload;Check.True(ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(task["detail"],out payload));
                Check.Equal(7L,payload.Sequence);Check.Equal("trial_planned",payload.Kind);Check.Equal("17",payload.Fields["planned_index"]);
            });
            suite.Run("VIVE analysis entry requires separate recovery for an unclean verified prefix",()=>{
                var bytes=Fixture(true,false);Check.Throws<InvalidDataException>(()=>ViveDevelopmentAnalysisEntry.AnalyzeClean(new MemoryStream(bytes,false),true,100,100000));
                var result=ViveDevelopmentAnalysisEntry.RecoverVerifiedPrefix(new MemoryStream(bytes,false),true,true,100,100000);
                Check.True(result.RecoveredIncompletePrefix);Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,result.SourceStatus);
                var lines=result.Csv.TrimEnd('\n').Split('\n');for(int i=1;i<lines.Length;i++)Check.Equal("UncleanEnd",Row(lines[0],lines[i])["source_status"]);
            });
            suite.Run("VIVE analysis entry labels truncated and corrupted verified prefixes and fails closed by default",()=>{
                foreach(bool corrupt in new[]{false,true})
                {
                    var source=Fixture(false,false);byte[] bytes;
                    if(corrupt){bytes=(byte[])source.Clone();bytes[bytes.Length-1]^=1;}
                    else{bytes=new byte[source.Length-1];Array.Copy(source,bytes,bytes.Length);}
                    Check.Throws<InvalidDataException>(()=>ViveDevelopmentAnalysisEntry.AnalyzeClean(new MemoryStream(bytes,false),true,100,100000));
                    var result=ViveDevelopmentAnalysisEntry.RecoverVerifiedPrefix(new MemoryStream(bytes,false),true,true,100,100000);
                    var expected=corrupt?ViveDevelopmentJournalReadStatus.Invalid:ViveDevelopmentJournalReadStatus.Truncated;
                    Check.Equal(expected,result.SourceStatus);Check.True(result.RecoveredIncompletePrefix);
                    var lines=result.Csv.TrimEnd('\n').Split('\n');for(int i=1;i<lines.Length;i++)Check.Equal(expected.ToString(),Row(lines[0],lines[i])["source_status"]);
                }
            });
            suite.Run("VIVE analysis entry sanitizes read errors and refuses invalid headers and bounds",()=>{
                try{ViveDevelopmentAnalysisEntry.RecoverVerifiedPrefix(new FaultStream(Fixture(false,true)),true,true,100,100000);throw new Exception("Expected read failure");}
                catch(IOException error){Check.True(!error.Message.Contains("private source path"));}
                Check.Throws<InvalidDataException>(()=>ViveDevelopmentAnalysisEntry.RecoverVerifiedPrefix(new MemoryStream(new byte[0]),true,true,100,100000));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveDevelopmentAnalysisEntry.AnalyzeClean(new MemoryStream(Fixture(false,true)),true,0,100000));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveDevelopmentAnalysisEntry.AnalyzeClean(new MemoryStream(Fixture(false,true)),true,100,0));
            });
            suite.Run("VIVE analysis entry distinguishes source stream exceptions from malformed journal bytes",()=>{
                var bytes=Fixture(true,true);
                var failures=new Func<Exception>[] {
                    ()=>new ArgumentException("private source argument"),
                    ()=>new InvalidOperationException("private source state"),
                    ()=>new InvalidDataException("private source decoder"),
                    ()=>new UnauthorizedAccessException("private source access"),
                    ()=>new NotSupportedException("private source operation"),
                    ()=>new FormatException("private source format"),
                    ()=>new OverflowException("private source overflow"),
                    ()=>new Exception("private source custom failure") };
                foreach(var failure in failures)
                foreach(long limit in new[]{0L,(long)bytes.Length-1})
                using(var input=new DelayedFaultStream(bytes,limit,failure))
                {
                    try
                    {ViveDevelopmentAnalysisEntry.RecoverVerifiedPrefix(input,true,true,100,100000);throw new Exception("Expected source reading failure: "+failure().GetType().Name+" at "+limit);}
                    catch(IOException error){Check.True(!error.Message.Contains("private source"));}
                }
                foreach(var failure in failures)
                using(var input=new CapabilityFaultStream(bytes,failure))
                {
                    try
                    {ViveDevelopmentAnalysisEntry.RecoverVerifiedPrefix(input,true,true,100,100000);throw new Exception("Expected capability reading failure");}
                    catch(IOException error){Check.True(!error.Message.Contains("private source"));}
                }
                var malformed=(byte[])bytes.Clone();malformed[0]^=1;
                Check.Throws<InvalidDataException>(()=>ViveDevelopmentAnalysisEntry.RecoverVerifiedPrefix(new MemoryStream(malformed,false),true,true,100,100000));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveDevelopmentAnalysisEntry.AnalyzeClean(new MemoryStream(bytes,false),true,0,100000));
                using(var fatal=new DelayedFaultStream(bytes,0,()=>new OutOfMemoryException("synthetic fatal")))
                    Check.Throws<OutOfMemoryException>(()=>ViveDevelopmentAnalysisEntry.AnalyzeClean(fatal,true,100,100000));
            });
        }
    }
}
