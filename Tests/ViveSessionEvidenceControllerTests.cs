using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Text;
using System.Globalization;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveSessionEvidenceControllerTests
    {
        sealed class DisposeFailure : MemoryStream
        {
            public DisposeFailure(byte[] bytes):base(bytes,false){}
            protected override void Dispose(bool disposing){base.Dispose(disposing);throw new IOException("private-dispose-path");}
        }
        sealed class Clock:IClock {public double Value;public string ClockId {get{return "fixture-clock";}}public double Now {get{return Value;}}}
        static byte[] Practice(string session,bool close,bool markUnavailable=false)
        {
            var clock=new Clock{Value=8};var output=new MemoryStream();
            var ctx=new VivePracticeContextV3(session,"fixture-participant","fixture-practice","fixture-device","fixture-clock","fixture-map",1,"fixture-epoch",0,null,VivePracticeEpochReasonV3.Initial,9);
            var prep=VivePracticeRecordingPreparationV3.Create(clock,session,1,"fixture-epoch",true,true);clock.Value=10;
            var start=new JournalEvent(0,9,"fixture-clock",session,-1,0,null,"practice_started",new Dictionary<string,string>{{"attempt","1"}});
            var writer=new VivePracticeJournalV3Writer(output,clock,ctx,start,ViveDevelopmentDataSource.SyntheticFixture,true,true,50,100000,prep);
            if(markUnavailable){clock.Value=10.2;writer.MarkUnavailable(ViveDevelopmentGapReason.DeviceUnavailable);}
            if(close){clock.Value=11;Check.True(writer.Close());}
            return output.ToArray();
        }
        static byte[] Journal(string streamId, bool close)
        {
            var output = new MemoryStream();
            var id = new ViveDevelopmentStreamIdentity("fixture-session", "fixture-participant", null,
                ViveDevelopmentMeasurementRole.Participant, streamId, "fixture-device", "fixture-clock", "fixture-map", ViveDevelopmentOperatorStatus.NotApplicable);
            var journal = ViveDevelopmentJournal.CreateV2WithIdentity(output, () => {}, id, ViveDevelopmentDataSource.SyntheticFixture, true, true);
            Check.True(journal.TryAppendInitialUnknownCoverage(0, 0, 0, .1));
            if (close) Check.True(journal.Close());
            return output.ToArray();
        }
        static byte[] LargeJournal()
        {
            var output=new MemoryStream();
            var id=new ViveDevelopmentStreamIdentity("fixture-session","fixture-participant",null,ViveDevelopmentMeasurementRole.Participant,
                "large-stream","fixture-device","fixture-clock","fixture-map",ViveDevelopmentOperatorStatus.NotApplicable);
            var j=ViveDevelopmentJournal.CreateV2WithIdentity(output,()=>{},id,ViveDevelopmentDataSource.SyntheticFixture,true,true);
            string detail=new string('x',1024);
            for(int i=0;i<2300;i++)Check.True(j.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.PhaseChanged,1,0,0,"MainTask",null,detail));
            Check.True(j.Close());return output.ToArray();
        }
        static byte[] ManyFieldsJournal()
        {
            var output=new MemoryStream();
            var id=new ViveDevelopmentStreamIdentity("fixture-session","fixture-participant",null,ViveDevelopmentMeasurementRole.Participant,
                "many-fields","fixture-device","fixture-clock","fixture-map",ViveDevelopmentOperatorStatus.NotApplicable);
            var j=ViveDevelopmentJournal.CreateV2WithIdentity(output,()=>{},id,ViveDevelopmentDataSource.SyntheticFixture,true,true);
            for(int i=0;i<4000;i++)Check.True(j.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.PhaseChanged,1,0,0,"MainTask",null,"x"));
            Check.True(j.Close());return output.ToArray();
        }
        static byte[] Events(string streamId,int block,params string[] pairs)
        {
            var output=new MemoryStream();
            var id=new ViveDevelopmentStreamIdentity("fixture-session","fixture-participant",null,ViveDevelopmentMeasurementRole.Participant,
                streamId,"fixture-device","fixture-clock","fixture-map",ViveDevelopmentOperatorStatus.NotApplicable);
            var j=ViveDevelopmentJournal.CreateV2WithIdentity(output,()=>{},id,ViveDevelopmentDataSource.SyntheticFixture,true,true);
            Check.True(j.TryAppendInitialUnknownCoverage(block,0,0,.1));
            for(int i=0;i<pairs.Length;i+=2)
            {
                string kind=Convert.ToBase64String(Encoding.UTF8.GetBytes(pairs[i+1]));
                string detail="AE2;SIMULATION_DEVELOPMENT;"+pairs[i]+";"+kind+";0";
                Check.True(j.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,1,block,0,"MainTask",null,detail));
            }
            Check.True(j.Close());return output.ToArray();
        }
        static byte[] SpecialJournal()
        {
            var initial=new MemoryStream();
            var id=new ViveDevelopmentStreamIdentity("fixture-session","fixture-participant",null,ViveDevelopmentMeasurementRole.Participant,
                "fixture-special","fixture-device","fixture-clock","fixture-map",ViveDevelopmentOperatorStatus.NotApplicable);
            var journal=ViveDevelopmentJournal.CreateV2WithIdentity(initial,()=>{},id,ViveDevelopmentDataSource.SyntheticFixture,true,true);
            Check.True(journal.TryAppendInitialUnknownCoverage(0,0,0,.1));
            foreach(string value in new[]{"=2+3","2026-11-06","-1.234","replace-with-empty"})
                Check.True(journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.PhaseChanged,1,0,0,"MainTask",null,value));
            Check.True(journal.Close());var records=new List<ViveDevelopmentJournalRecord>();
            using(var source=new MemoryStream(initial.ToArray(),false))
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,ViveDevelopmentJournalReader.Read(source,r=>records.Add(r)).Status);
            var output=new MemoryStream();output.Write(ViveDevelopmentJournal.Magic,0,ViveDevelopmentJournal.Magic.Length);
            byte[] token=new byte[16],previous=new byte[32];
            foreach(var record in records)
            {
                var fields=new Dictionary<string,string>();foreach(var pair in record.Fields)fields.Add(pair.Key,pair.Value);
                string detail;if(fields.TryGetValue("detail",out detail)&&detail=="replace-with-empty")fields["detail"]="";
                byte[] payload=ContinuousJournal.Payload(record.Sequence,token,record.Kind,fields);
                byte[] digest=ContinuousJournal.Digest(previous,payload),prefix=new byte[8];
                ContinuousJournal.PutInt(prefix,payload.Length);ContinuousJournal.PutInt(prefix,~payload.Length,4);
                output.Write(prefix,0,prefix.Length);output.Write(payload,0,payload.Length);output.Write(digest,0,digest.Length);previous=digest;
            }
            return output.ToArray();
        }
        static string B64(string value){return "b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(value));}
        static string DecodeB64(string value)
        {Check.True(value.StartsWith("b64:",StringComparison.Ordinal));return Encoding.UTF8.GetString(Convert.FromBase64String(value.Substring(4)));}
        static void ExpectedRecord(Dictionary<string,string> target,int source,long sequence,string kind,IReadOnlyDictionary<string,string> fields)
        {
            string prefix=source.ToString(CultureInfo.InvariantCulture)+"|"+sequence.ToString(CultureInfo.InvariantCulture)+"|"+kind+"|";
            if(fields.Count==0){target.Add(prefix+"<no-field>","<no-value>");return;}
            foreach(var field in fields)target.Add(prefix+field.Key,field.Value==null?"N":"V"+field.Value);
        }
        static byte[] OperatorJournal(string streamId,string operatorId)
        {
            var output=new MemoryStream();
            var presence=operatorId==null?ViveDevelopmentOperatorStatus.NotApplicable:ViveDevelopmentOperatorStatus.Present;
            var id=new ViveDevelopmentStreamIdentity("fixture-session","fixture-participant",operatorId,ViveDevelopmentMeasurementRole.Participant,
                streamId,"fixture-device","fixture-clock","fixture-map",presence);
            var journal=ViveDevelopmentJournal.CreateV2WithIdentity(output,()=>{},id,ViveDevelopmentDataSource.SyntheticFixture,true,true);
            Check.True(journal.TryAppendInitialUnknownCoverage(0,0,0,.1));Check.True(journal.Close());return output.ToArray();
        }
        static string EventDetail(long sequence,string kind,params string[] fields)
        {
            var pairs=new SortedDictionary<string,string>(StringComparer.Ordinal);
            for(int i=0;i<fields.Length;i+=2)pairs.Add(fields[i],fields[i+1]);
            var text=new StringBuilder("AE2;SIMULATION_DEVELOPMENT;").Append(sequence.ToString(CultureInfo.InvariantCulture))
                .Append(';').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(kind)))
                .Append(';').Append(pairs.Count.ToString(CultureInfo.InvariantCulture));
            foreach(var pair in pairs)
            {
                text.Append(';').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key)));
                text.Append(';').Append(pair.Value==null?"~":Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value)));
            }
            return text.ToString();
        }
        static byte[] TrialEvents()
        {
            var output=new MemoryStream();
            var id=new ViveDevelopmentStreamIdentity("fixture-session","fixture-participant",null,ViveDevelopmentMeasurementRole.Participant,
                "fixture-trial","fixture-device","fixture-clock","fixture-map",ViveDevelopmentOperatorStatus.NotApplicable);
            var j=ViveDevelopmentJournal.CreateV2WithIdentity(output,()=>{},id,ViveDevelopmentDataSource.SyntheticFixture,true,true);
            Check.True(j.TryAppendInitialUnknownCoverage(0,0,0,.1));
            Check.True(j.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,1,0,0,"MainTask","fixture/trial-0",
                EventDetail(10,"trial_planned","list","fixture-list","planned_index","0")));
            Check.True(j.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,1.25,0,0,"MainTask","fixture/trial-0",
                EventDetail(11,"stimulus_presented","word","Red","ink","Blue","list","fixture-list","planned_index","0",
                    "named_schema","fixture-named-v1","named_option","NoAvatar","named_schedule","fixture-schedule",
                    "named_geometry","fixture-geometry","named_source","NOT_APPLICABLE","named_avatar_aoi","NOT_APPLICABLE",
                    "named_participant_eye","NOT_RECORDED","named_phase","MainTask","named_active_seconds","0.25","named_scope","single_block_simulation_only")));
            Check.True(j.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,1.75,0,0,"MainTask","fixture/trial-0",
                EventDetail(12,"first_response","response","Blue","correct","True","onset","1.25","rt_seconds","0.5")));
            Check.True(j.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,2,0,1,"MainTask","fixture/trial-1",
                EventDetail(13,"trial_interrupted","reason","fixture")));
            Check.True(j.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,2.1,0,1,"MainTask","fixture/trial-2",
                "AE2;SIMULATION_DEVELOPMENT;not-decodable"));
            Check.True(j.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,2.2,0,1,"BlockReady",null,
                EventDetail(14,"block_configuration_locked","selection","NOT_APPLICABLE","list_index","0",
                    "selection_origin","explicit_owner_confirmation","confirmation_block_index","0","confirmation_event_sequence","9")));
            Check.True(j.Close());return output.ToArray();
        }
        static byte[] IntegrityJournal(string stream,string trialId,int block,int segment,bool close,params string[][] events)
        {
            var output=new MemoryStream();
            var identity=new ViveDevelopmentStreamIdentity("fixture-session","fixture-participant",null,ViveDevelopmentMeasurementRole.Participant,
                stream,"fixture-device","fixture-clock","fixture-map",ViveDevelopmentOperatorStatus.NotApplicable);
            var journal=ViveDevelopmentJournal.CreateV2WithIdentity(output,()=>{},identity,ViveDevelopmentDataSource.SyntheticFixture,true,true);
            foreach(var item in events)
            {
                long sequence=Int64.Parse(item[0],CultureInfo.InvariantCulture);var fields=new string[item.Length-2];Array.Copy(item,2,fields,0,fields.Length);
                Check.True(journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,1,block,segment,"MainTask",trialId,
                    EventDetail(sequence,item[1],fields)));
            }
            if(close)Check.True(journal.Close());return output.ToArray();
        }
        static byte[] V2PracticeStart()
        {
            var output=new MemoryStream();
            var identity=new ViveDevelopmentStreamIdentity("fixture-session","fixture-participant",null,ViveDevelopmentMeasurementRole.Participant,
                "v2-practice","fixture-device","fixture-clock","fixture-map",ViveDevelopmentOperatorStatus.NotApplicable);
            var journal=ViveDevelopmentJournal.CreateV2WithIdentity(output,()=>{},identity,ViveDevelopmentDataSource.SyntheticFixture,true,true);
            Check.True(journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,9,-1,0,"Practice",null,
                EventDetail(0,"practice_started","attempt","1")));
            Check.True(journal.Close());return output.ToArray();
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE evidence review requires idle view and confirmed recorder stop", () => {
                Check.True(ViveSessionEvidenceController.CanReview(false,false,false,false,false,false));
                Check.True(ViveSessionEvidenceController.CanReview(false,true,true,true,true,false));
                Check.True(!ViveSessionEvidenceController.CanReview(true,true,true,true,true,false));
                Check.True(!ViveSessionEvidenceController.CanReview(false,true,false,false,false,false));
                Check.True(!ViveSessionEvidenceController.CanReview(false,true,true,true,false,false));
                Check.True(!ViveSessionEvidenceController.CanReview(false,true,true,true,true,true));
            });
            suite.Run("VIVE evidence controller requires explicit action before opening", () => {
                int opens = 0; var c = new ViveSessionEvidenceController(p => { opens++; return new MemoryStream(Journal("a", true), false); });
                Check.True(!c.AddClean("C:\\fixture", false));
                Check.True(!c.AddVerifiedPrefix("C:\\fixture", true, false));
                Check.True(!c.AddClean("relative", true)); Check.Equal(0, opens);
                Check.True(c.AddClean("C:\\fixture", true)); Check.Equal(1, opens);
                Check.Equal(1, c.Summary.CleanCloseJournals); Check.Equal(1L, c.Summary.InitialUnknownWindowsSeen);
                Check.True(!c.Summary.CompleteSessionProven && !c.Summary.DiskExportAvailable);
            });
            suite.Run("VIVE evidence controller requires separate recovery and retains prior summary", () => {
                var c = new ViveSessionEvidenceController(p => new MemoryStream(Journal(p.EndsWith("one") ? "one" : "two", p.EndsWith("one")), false));
                Check.True(c.AddClean("C:\\one", true)); Check.True(!c.AddClean("C:\\two", true));
                Check.Equal(1, c.Summary.JournalsAccepted); Check.True(c.Message.Contains("separate"));
                Check.True(c.AddVerifiedPrefix("C:\\two", true, true));
                Check.Equal(2, c.Summary.JournalsAccepted); Check.Equal(1, c.Summary.RecoveredPrefixJournals);
            });
            suite.Run("VIVE evidence controller sanitizes open errors and refuses writable input", () => {
                var c = new ViveSessionEvidenceController(p => { throw new IOException("secret-fixture-path"); });
                Check.True(!c.AddClean("C:\\secret-fixture-path", true)); Check.True(!c.Message.Contains("secret-fixture-path"));
                var writable = new ViveSessionEvidenceController(p => new MemoryStream());
                Check.True(!writable.AddClean("C:\\fixture", true)); Check.Equal(0, writable.Summary.JournalsAccepted);
                Check.True(!writable.Message.Contains("fixture"));
            });
            suite.Run("VIVE evidence controller rejects nonlocal and ambiguous path forms before opening", () => {
                int opens = 0; var c = new ViveSessionEvidenceController(p => { opens++; return new MemoryStream(Journal("unicode", true), false); });
                foreach (string path in new[]{@"\\server\share\data", @"\\?\C:\data", @"\\.\device", @"C:relative", @"\rooted", @"C:\file:stream", "relative", ""})
                    Check.True(!c.AddClean(path, true));
                Check.Equal(0, opens);
                Check.True(c.AddClean(@"C:\数据\journal.vdev", true)); Check.Equal(1, opens);
                Check.True(!c.AddClean(@"\\server\data", true));
                Check.Equal(1, c.Summary.JournalsAccepted); Check.True(c.Message.Contains("Previously accepted"));
            });
            suite.Run("VIVE sensitive memory preview needs explicit local input and preserves v2/v3 distinctions", () => {
                int opens=0;var v2=Journal("fixture-block",true);var v3=Practice("fixture-session",true);
                var c=new ViveSessionEvidenceController(p=>{opens++;return new MemoryStream(p.EndsWith("practice")?v3:v2,false);});
                Check.True(!c.AddMemory(@"C:\block",false,false,false,false));Check.Equal(0,opens);
                Check.True(!c.AddMemory(@"C:\practice",true,true,true,false));Check.Equal(0,opens);
                Check.True(!c.AddMemory("relative",false,true,false,false));Check.Equal(0,opens);
                Check.True(c.AddMemory(@"C:\block",false,true,false,false));
                Check.True(c.AddMemory(@"C:\practice",true,true,false,false));Check.Equal(2,c.MemorySourceCount);
                string text=c.MemoryText;
                Check.True(text.Contains("vive-development-journal-v2")&&text.Contains("vive-practice-journal-v3"));
                Check.True(text.Contains("initial_coverage_unknown")&&text.Contains("tail_unknown"));
                Check.True(text.Contains("CleanClose")&&text.Contains("verified_bytes")&&text.Contains("not authenticated or proven"));
                Check.True(!c.AddMemory(@"C:\block",false,true,false,false));Check.Equal(2,c.MemorySourceCount);Check.Equal(text,c.MemoryText);
                c.ClearMemory();Check.Equal(0,c.MemorySourceCount);Check.Equal("",c.MemoryText);
            });
            suite.Run("VIVE memory preview separately confirms incomplete recovery and refuses mixed identities atomically", () => {
                var v2=Journal("fixture-block",true);var prefix=Practice("fixture-session",false);var other=Practice("other-session",true);
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("block")?v2:p.EndsWith("other")?other:prefix,false));
                Check.True(c.AddMemory(@"C:\block",false,true,false,false));string before=c.MemoryText;
                Check.True(!c.AddMemory(@"C:\prefix",true,true,false,false));Check.Equal(before,c.MemoryText);
                Check.True(!c.AddMemory(@"C:\other",true,true,false,false));Check.Equal(before,c.MemoryText);
                Check.True(c.AddMemory(@"C:\prefix",true,true,true,true));Check.Equal(2,c.MemorySourceCount);
                Check.True(c.MemoryText.Contains("UncleanEnd"));
                Check.True(c.MemoryReport.Contains("status UncleanEnd")&&c.MemoryReport.Contains("Unselected files"));c.ClearMemory();
            });
            suite.Run("VIVE memory preview rejects wrong schemas and does not expose path errors", () => {
                var data=Journal("fixture-block",true);var c=new ViveSessionEvidenceController(p=>new MemoryStream(data,false));
                Check.True(!c.AddMemory(@"C:\private-path",true,true,false,false));Check.Equal(0,c.MemorySourceCount);
                Check.True(!c.MemoryMessage.Contains("private-path"));Check.Equal("",c.MemoryText);
                var envelope=new ViveSessionEvidenceController(p=>new MemoryStream(Encoding.ASCII.GetBytes("VPSTG001"),false));
                Check.True(!envelope.AddMemory(@"C:\envelope",true,true,true,true));Check.Equal(0,envelope.MemorySourceCount);
                envelope.ClearMemory();
                var failing=new ViveSessionEvidenceController(p=>{throw new IOException("private-path");});
                Check.True(!failing.AddMemory(@"C:\private-path",false,true,false,false));
                Check.True(!failing.MemoryMessage.Contains("private-path"));failing.ClearMemory();
            });
            suite.Run("VIVE memory preview rejects corrupted header and requires explicit truncated-prefix recovery", () => {
                var clean=Practice("fixture-session",true);var truncated=new byte[clean.Length-5];Buffer.BlockCopy(clean,0,truncated,0,truncated.Length);
                var corrupt=(byte[])clean.Clone();corrupt[45]^=1;
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("bad")?corrupt:truncated,false));
                Check.True(!c.AddMemory(@"C:\bad",true,true,true,true));Check.Equal(0,c.MemorySourceCount);
                Check.True(!c.AddMemory(@"C:\truncated",true,true,false,false));Check.Equal(0,c.MemorySourceCount);
                Check.True(c.AddMemory(@"C:\truncated",true,true,true,true));
                Check.True(c.MemoryText.Contains("Truncated")&&c.MemoryText.Contains("verified_bytes"));c.ClearMemory();
            });
            suite.Run("VIVE memory preview rejects output over budget without replacing an accepted source", () => {
                var first=Journal("fixture-block",true);var large=LargeJournal();
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("large")?large:first,false));
                Check.True(c.AddMemory(@"C:\block",false,true,false,false));string before=c.MemoryText;
                Check.True(!c.AddMemory(@"C:\large",false,true,false,false));
                Check.Equal(1,c.MemorySourceCount);Check.Equal(before,c.MemoryText);c.ClearMemory();
            });
            suite.Run("VIVE sensitive preview timer drops references without GUI calls", () => {
                var data=Journal("fixture-block",true);var c=new ViveSessionEvidenceController(p=>new MemoryStream(data,false),25);
                Check.True(c.AddMemory(@"C:\block",false,true,false,false));
                Check.True(SpinWait.SpinUntil(()=>c.MemorySourceCount==0,2000),"Preview did not expire");
                Check.Equal("",c.MemoryText);Check.Equal("",c.MemoryReport);
                Check.Equal("",c.MemoryManifestCsv);Check.Equal("",c.MemoryFieldCsv);Check.Equal("",c.MemoryEventCsv);c.ClearMemory();
            });
            suite.Run("VIVE memory preview clears all sensitive references if source disposal fails", () => {
                var data=Journal("fixture-block",true);var second=Journal("fixture-second",true);
                var c=new ViveSessionEvidenceController(p=>p.EndsWith("bad")?(Stream)new DisposeFailure(second):new MemoryStream(data,false));
                Check.True(c.AddMemory(@"C:\first",false,true,false,false));Check.Equal(1,c.MemorySourceCount);
                Check.True(!c.AddMemory(@"C:\bad",false,true,false,false));
                Check.Equal(0,c.MemorySourceCount);Check.Equal("",c.MemoryText);Check.True(c.MemoryMessage.Contains("cleared"));
                Check.Equal("",c.MemoryManifestCsv);Check.Equal("",c.MemoryFieldCsv);Check.Equal("",c.MemoryEventCsv);
                Check.True(!c.MemoryMessage.Contains("private-dispose-path"));c.ClearMemory();
            });
            suite.Run("VIVE reconciliation reports selected marker gaps and repeats without claiming whole-session coverage", () => {
                var a=Events("stream-a",0,"10","trial_planned","12","first_response");
                var b=Events("stream-b",0,"12","first_response","13","rating");
                var practice=Practice("fixture-session",true);
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("a")?a:p.EndsWith("b")?b:practice,false));
                Check.True(c.AddMemory(@"C:\a",false,true,false,false),"add a");Check.True(c.AddMemory(@"C:\b",false,true,false,false),"add b");
                Check.True(c.AddMemory(@"C:\practice",true,true,false,false),"add practice");string report=c.MemoryReport;
                Check.True(report.Contains("repeated sequence occurrences: 1"),"repeated sequence");
                Check.True(report.Contains("unobserved sequence ranges between selected markers: 2"),"missing sequence ranges");
                Check.True(report.Contains("trial-planned/first-response/interrupted/rating markers 1/2/0/1"),"block marker counts");
                Check.True(report.Contains("Practice start declared in header: True; start-boundary record observed: True"),"declared practice start");
                Check.True(report.Contains("Complete session, continuous tracking, durable storage, trial alignment across devices and research validity: UNKNOWN"),"unknown coverage");
                Check.True(!report.Contains("fixture-session")&&!report.Contains("fixture-participant")&&!report.Contains("fixture-device"),"no identifiers");
                c.ClearMemory();Check.Equal("",c.MemoryReport);
            });
            suite.Run("VIVE reconciliation keeps earlier summary intact when a later source is refused", () => {
                var a=Events("stream-a",0,"10","trial_planned");var other=Practice("other-session",true);
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("a")?a:other,false));
                Check.True(c.AddMemory(@"C:\a",false,true,false,false));string before=c.MemoryReport;
                Check.True(!c.AddMemory(@"C:\other",true,true,false,false));Check.Equal(before,c.MemoryReport);
                Check.True(before.Contains("unselected files")||before.Contains("Unselected files"));c.ClearMemory();
            });
            suite.Run("VIVE selected-source CSV preserves raw strings, nulls, empty fields and safe spreadsheet prefixes", () => {
                var data=SpecialJournal();var practice=Practice("fixture-session",true);
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("practice")?practice:data,false));
                var original=Thread.CurrentThread.CurrentCulture;
                try
                {
                    Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
                    Check.True(c.AddMemory(@"C:\special",false,true,false,false));Check.True(c.AddMemory(@"C:\practice",true,true,false,false));
                    string manifest=c.MemoryManifestCsv,table=c.MemoryFieldCsv;
                    Check.True(manifest.StartsWith("preview_version,source_index_u64,"));
                    Check.True(manifest.Contains("status:CleanClose")&&manifest.Contains("u64:1")&&manifest.Contains("u64:2"));
                    Check.True(manifest.Contains("bool:false,bool:false,bool:false"));
                    Check.True(table.Contains(B64("=2+3"))&&table.Contains(B64("2026-11-06"))&&table.Contains(B64("-1.234")));
                    Check.True(!table.Contains("=2+3")&&!table.Contains("2026-11-06")&&!table.Contains("-1.234"));
                    bool empty=false,missing=false,decimalValue=false,close=false;
                    foreach(string line in table.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] cells=line.Split(',');Check.Equal(9,cells.Length);
                        foreach(string cell in cells)if(cell.Length>0)Check.True(cell[0]!='='&&cell[0]!='+'&&cell[0]!='-'&&cell[0]!='@');
                        if(cells[6]==B64("detail")&&cells[7]=="bool:true"&&cells[8]=="b64:")empty=true;
                        if(cells[6]==B64("trial_id")&&cells[7]=="bool:false"&&cells[8]=="")missing=true;
                        if(cells[6]==B64("coverage_end_local_seconds")&&cells[8]==B64("0.1"))decimalValue=true;
                        if(cells[4]==B64("close")&&cells[5]=="bool:false")close=true;
                    }
                    Check.True(empty&&missing&&decimalValue&&close,"missing reversible null, empty, decimal or fieldless record");
                }
                finally{Thread.CurrentThread.CurrentCulture=original;c.ClearMemory();}
                Check.Equal("",c.MemoryManifestCsv);Check.Equal("",c.MemoryFieldCsv);
            });
            suite.Run("VIVE selected-source CSV failed addition does not change either table", () => {
                var a=Events("stream-a",0,"10","trial_planned");var other=Practice("other-session",true);
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("a")?a:other,false));
                Check.True(c.AddMemory(@"C:\a",false,true,false,false));string manifest=c.MemoryManifestCsv,table=c.MemoryFieldCsv;
                Check.True(!c.AddMemory(@"C:\other",true,true,false,false));
                Check.Equal(manifest,c.MemoryManifestCsv);Check.Equal(table,c.MemoryFieldCsv);c.ClearMemory();
            });
            suite.Run("VIVE selected-source CSV rejects table budget without altering earlier sources", () => {
                var first=Journal("fixture-block",true);var many=ManyFieldsJournal();
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("many")?many:first,false));
                Check.True(c.AddMemory(@"C:\first",false,true,false,false));
                string original=c.MemoryText,manifest=c.MemoryManifestCsv,table=c.MemoryFieldCsv,events=c.MemoryEventCsv,report=c.MemoryReport;
                // Verify this same strict-reader fixture exceeds the event view's
                // own cap, not merely a different view's cap. Token widths match.
                var rows=new List<ViveCoverageRecord>();ViveDevelopmentJournalReadResult read;
                using(var stream=new MemoryStream(many,false))
                    read=ViveDevelopmentJournalReader.Read(stream,r=>rows.Add(new ViveCoverageRecord{Sequence=r.Sequence,Kind=r.Kind,Fields=r.Fields}));
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,read.Status);bool eventLimit=false;
                try{ViveSelectedJournalEventCsv.AppendSource(events,2,new string('0',32),"vive-development-journal-v2",read.Status,read.VerifiedBytes,rows);}
                catch(InvalidDataException){eventLimit=true;}
                Check.True(eventLimit,"synthetic source must exceed the event-view cap");
                Check.True(!c.AddMemory(@"C:\many",false,true,false,false));
                Check.Equal(1,c.MemorySourceCount);Check.Equal(original,c.MemoryText);
                Check.Equal(manifest,c.MemoryManifestCsv);Check.Equal(table,c.MemoryFieldCsv);Check.Equal(events,c.MemoryEventCsv);Check.Equal(report,c.MemoryReport);c.ClearMemory();
            });
            suite.Run("VIVE selected-source CSV matches every strict v2 and v3 reader record and field", () => {
                var v2=SpecialJournal();var v3=Practice("fixture-session",true);var expected=new Dictionary<string,string>();
                using(var input=new MemoryStream(v2,false))
                    Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,ViveDevelopmentJournalReader.Read(input,
                        r=>ExpectedRecord(expected,1,r.Sequence,r.Kind,r.Fields)).Status);
                using(var input=new MemoryStream(v3,false))
                {
                    var read=VivePracticeJournalV3Reader.Read(input,true,false,100,100000);
                    Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,read.Status);
                    foreach(var row in read.Records)ExpectedRecord(expected,2,row.Sequence,row.Kind,row.Fields);
                }
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("v2")?v2:v3,false));
                Check.True(c.AddMemory(@"C:\v2",false,true,false,false));Check.True(c.AddMemory(@"C:\v3",true,true,false,false));
                var actual=new Dictionary<string,string>();bool header=true;
                foreach(string line in c.MemoryFieldCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries))
                {
                    if(header){header=false;continue;}string[] cells=line.Split(',');Check.Equal(9,cells.Length);
                    Check.True(cells[1].StartsWith("u64:")&&cells[3].StartsWith("u64:"));
                    int source=Int32.Parse(cells[1].Substring(4),CultureInfo.InvariantCulture);
                    long sequence=Int64.Parse(cells[3].Substring(4),CultureInfo.InvariantCulture);
                    string prefix=source.ToString(CultureInfo.InvariantCulture)+"|"+sequence.ToString(CultureInfo.InvariantCulture)+"|"+DecodeB64(cells[4])+"|";
                    if(cells[5]=="bool:false")
                    {Check.Equal("",cells[6]);Check.Equal("bool:false",cells[7]);Check.Equal("",cells[8]);actual.Add(prefix+"<no-field>","<no-value>");}
                    else
                    {
                        Check.Equal("bool:true",cells[5]);string key=DecodeB64(cells[6]);
                        Check.True(cells[7]=="bool:true"||cells[7]=="bool:false");
                        if(cells[7]=="bool:false"){Check.Equal("",cells[8]);actual.Add(prefix+key,"N");}
                        else actual.Add(prefix+key,"V"+DecodeB64(cells[8]));
                    }
                }
                Check.Equal(expected.Count,actual.Count);
                foreach(var entry in expected){string value;Check.True(actual.TryGetValue(entry.Key,out value),"missing synthetic field");Check.Equal(entry.Value,value);}
                c.ClearMemory();
            });
            suite.Run("VIVE selected-source preview permits different operators and no-operator declarations without claiming a comparison", () => {
                var a=OperatorJournal("stream-a","fixture-operator-a");var b=OperatorJournal("stream-b","fixture-operator-b");
                var noOperator=OperatorJournal("stream-none",null);var practice=Practice("fixture-session",true);
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("a")?a:p.EndsWith("b")?b:p.EndsWith("none")?noOperator:practice,false));
                Check.True(c.AddMemory(@"C:\a",false,true,false,false));Check.True(c.AddMemory(@"C:\b",false,true,false,false));
                Check.True(c.AddMemory(@"C:\none",false,true,false,false));Check.True(c.AddMemory(@"C:\practice",true,true,false,false));
                Check.Equal(4,c.MemorySourceCount);string report=c.MemoryReport,manifest=c.MemoryManifestCsv;
                Check.True(report.Contains("Operator ID and status are not compared across sources"));
                Check.True(report.Contains("operator status declaration Present")&&report.Contains("operator status declaration NotApplicable"));
                Check.True(report.Contains("operator status declaration not_recorded_in_v3"));
                Check.True(manifest.Contains("status:Present,bool:false")&&manifest.Contains("status:NotApplicable,bool:false")&&
                    manifest.Contains("status:not_recorded_in_v3,bool:false"));c.ClearMemory();
            });
            suite.Run("VIVE selected-journal observed-event view preserves trial evidence and explicit gaps without a gaze join", () => {
                var task=TrialEvents();var practice=Practice("fixture-session",true,true);
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("practice")?practice:task,false));
                Check.True(c.AddMemory(@"C:\task",false,true,false,false),"add synthetic task");
                Check.True(c.AddMemory(@"C:\practice",true,true,false,false),"add synthetic practice");
                var sourceManifest=new Dictionary<string,string[]>();bool manifestHeader=true;
                foreach(string line in c.MemoryManifestCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries))
                {
                    if(manifestHeader){manifestHeader=false;continue;}var cells=line.Split(',');Check.Equal(12,cells.Length);sourceManifest.Add(cells[1],cells);
                }
                string csv=c.MemoryEventCsv;bool header=true,planned=false,presented=false,response=false,interrupted=false,unreadable=false,unknown=false,practiceStart=false,unavailable=false,configuration=false,tail=false;
                foreach(string line in csv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries))
                {
                    if(header){header=false;Check.True(line.StartsWith("preview_version,source_index_u64,"));continue;}
                    string[] row=line.Split(',');Check.Equal(45,row.Length);
                    foreach(string cell in row)if(cell.Length>0)Check.True(cell[0]!='='&&cell[0]!='+'&&cell[0]!='-'&&cell[0]!='@');
                    Check.Equal("selected-journal-observed-event-v1",row[0]);
                    string[] manifest;Check.True(sourceManifest.TryGetValue(row[1],out manifest),"source missing from manifest");
                    Check.Equal(manifest[2],row[2]);Check.Equal(manifest[3],row[3]);Check.Equal(manifest[4],row[4]);Check.Equal(manifest[5],row[5]);
                    string kind=DecodeB64(row[7]);
                    if(row[11]==B64("trial_planned"))
                    {planned=true;Check.Equal(B64("fixture/trial-0"),row[14]);Check.Equal(B64("fixture-list"),row[19]);Check.Equal(B64("0"),row[20]);Check.Equal("state:missing",row[21]);}
                    if(row[11]==B64("stimulus_presented"))
                    {
                        presented=true;Check.Equal("state:simulation_development_declared",row[8]);Check.Equal(B64("11"),row[10]);
                        Check.Equal(B64("Red"),row[17]);Check.Equal(B64("Blue"),row[18]);Check.Equal(B64("observed_local_seconds"),row[30]);
                        Check.Equal(B64("fixture-named-v1"),row[35]);Check.Equal(B64("NoAvatar"),row[36]);Check.Equal(B64("NOT_APPLICABLE"),row[39]);
                        Check.Equal(B64("NOT_RECORDED"),row[41]);Check.Equal(B64("single_block_simulation_only"),row[44]);
                    }
                    if(row[11]==B64("first_response"))
                    {response=true;Check.Equal(B64("Blue"),row[21]);Check.Equal(B64("True"),row[22]);Check.Equal(B64("1.25"),row[23]);Check.Equal(B64("0.5"),row[24]);Check.Equal("state:missing",row[36]);}
                    if(row[11]==B64("trial_interrupted"))
                    {interrupted=true;Check.Equal(B64("fixture/trial-1"),row[14]);Check.Equal("state:missing",row[21]);}
                    if(row[8]=="state:undecodable")
                    {unreadable=true;Check.Equal("state:missing",row[10]);Check.Equal("state:missing",row[11]);Check.Equal("state:missing",row[21]);}
                    if(kind=="initial_coverage_unknown")
                    {unknown=true;Check.Equal(B64("0"),row[27]);Check.Equal(B64("0.1"),row[28]);Check.Equal("state:missing",row[14]);}
                    if(kind=="unavailable_detected")
                    {unavailable=true;Check.Equal(B64("10.2"),row[15]);Check.Equal(B64("detected_local_seconds"),row[30]);Check.Equal(B64("DeviceUnavailable"),row[29]);}
                    if(kind=="tail_unknown")
                    {tail=true;Check.Equal(B64("10"),row[27]);Check.Equal(B64("11"),row[28]);Check.Equal(B64("no_observation_yet"),row[29]);}
                    if(row[11]==B64("block_configuration_locked"))
                    {configuration=true;Check.Equal(B64("NOT_APPLICABLE"),row[26]);Check.Equal(B64("0"),row[31]);Check.Equal(B64("explicit_owner_confirmation"),row[32]);Check.Equal(B64("0"),row[33]);Check.Equal(B64("9"),row[34]);}
                    if(row[11]==B64("practice_started"))
                    {practiceStart=true;Check.Equal("u64:2",row[1]);Check.Equal("state:missing",row[12]);Check.Equal("state:null",row[14]);}
                }
                Check.True(planned&&presented&&response&&interrupted&&unreadable&&unknown&&practiceStart&&unavailable&&configuration&&tail,
                    "missing synthetic event classification");
                Check.True(!csv.Contains("fixture/trial-0")&&!csv.Contains("fixture-list"));c.ClearMemory();Check.Equal("",c.MemoryEventCsv);
            });
            suite.Run("VIVE selected-journal event view refuses a source beyond its independent memory budget", () => {
                var rows=new List<ViveCoverageRecord>();var fields=new Dictionary<string,string>{{"event_kind","PhaseChanged"},{"observed_local_seconds","1"},
                    {"block_index","0"},{"segment","0"},{"phase","MainTask"},{"trial_id",null},{"detail","fixture"}};
                for(int i=0;i<5000;i++)rows.Add(new ViveCoverageRecord{Sequence=i,Kind="task_event",Fields=fields});
                bool refused=false;
                try{ViveSelectedJournalEventCsv.AppendSource("",1,new string('0',32),"vive-development-journal-v2",
                    ViveDevelopmentJournalReadStatus.CleanClose,1,rows);}
                catch(InvalidDataException){refused=true;}
                Check.True(refused,"event preview must reject over-budget additions without partial publication");
            });
            suite.Run("VIVE selected-source declaration audit distinguishes repeated and conflicting declarations including withdrawal", () => {
                var a=IntegrityJournal("audit-a","fixture/trial",0,0,true,
                    new[]{"0","session_created","named_schema","fixture-v1","named_option","","named_schedule",null},
                    new[]{"1","trial_planned"},new[]{"3","session_ended","reason","Completed"});
                var b=IntegrityJournal("audit-b","fixture/trial",0,0,true,
                    new[]{"1","trial_planned"},new[]{"2","session_ended","reason","Withdrawn"});
                var conflicting=IntegrityJournal("audit-c","fixture/other",0,0,true,new[]{"1","trial_planned"});
                var incomplete=IntegrityJournal("audit-d","fixture/trial",0,0,false,new[]{"4","trial_interrupted"});
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("a")?a:p.EndsWith("b")?b:p.EndsWith("c")?conflicting:incomplete,false));
                Check.True(c.AddMemory(@"C:\a",false,true,false,false));Check.True(c.AddMemory(@"C:\b",false,true,false,false));
                Check.True(c.AddMemory(@"C:\c",false,true,false,false));Check.True(c.AddMemory(@"C:\d",false,true,true,true));
                string report=c.MemoryReport;
                Check.True(report.Contains("matching compared fingerprints: 1; with differing compared fingerprints: 1"));
                Check.True(report.Contains("clean closes 3; incomplete/invalid prefixes 1"));
                Check.True(report.Contains("session_created markers observed: 1; at sequence zero: 1"));
                Check.True(report.Contains("Completed 1; Withdrawn 1; TechnicalEnd 0; PracticeFailed 0"));
                Check.True(report.Contains("named_schema: 1/0/0/2")&&report.Contains("named_option: 0/1/0/2")&&report.Contains("named_schedule: 0/0/1/2"));
                Check.True(report.Contains("Withdrawal may be absent from an unclean prefix")&&report.Contains("remain UNKNOWN"));
                Check.True(!report.Contains("fixture/trial")&&!report.Contains("fixture/other")&&!report.Contains("fixture-v1"));
                c.ClearMemory();Check.Equal("",c.MemoryReport);
            });
            suite.Run("VIVE selected-source declaration audit never treats observed multi-block completion markers as complete coverage", () => {
                var sources=new Dictionary<string,byte[]>();int sequence=0;
                for(int block=0;block<4;block++)
                {
                    var records=new List<string[]>();
                    if(block==0)records.Add(new[]{(sequence++).ToString(CultureInfo.InvariantCulture),"session_created"});
                    records.Add(new[]{(sequence++).ToString(CultureInfo.InvariantCulture),"block_configuration_locked"});
                    records.Add(new[]{(sequence++).ToString(CultureInfo.InvariantCulture),"block_task_ended"});
                    records.Add(new[]{(sequence++).ToString(CultureInfo.InvariantCulture),"questionnaire_completed"});
                    if(block==3)records.Add(new[]{(sequence++).ToString(CultureInfo.InvariantCulture),"session_ended","reason","Completed"});
                    sources.Add("s"+block,IntegrityJournal("stream-"+block,"fixture/trial",block,0,true,records.ToArray()));
                }
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(sources[p.Substring(p.Length-2)],false));
                for(int block=0;block<4;block++)Check.True(c.AddMemory(@"C:\s"+block,false,true,false,false));
                string report=c.MemoryReport;
                Check.True(report.Contains("session_ended reason markers observed: Completed 1"));
                Check.True(report.Contains("Practice start/end markers: 0/0"));
                Check.True(report.Contains("Complete session, all trials/responses, practice coverage, continuous gaze, cross-device alignment and durable/authenticated publication remain UNKNOWN"));
                Check.True(!report.Contains("all trials covered")&&!report.Contains("session completeness proven"));
                c.ClearMemory();
            });
            suite.Run("VIVE declaration audit leaves v2 explicit block and v3 missing event block comparison indeterminate", () => {
                var v2=V2PracticeStart();var v3=Practice("fixture-session",true);
                var c=new ViveSessionEvidenceController(p=>new MemoryStream(p.EndsWith("v2")?v2:v3,false));
                Check.True(c.AddMemory(@"C:\v2",false,true,false,false));Check.True(c.AddMemory(@"C:\v3",true,true,false,false));
                string report=c.MemoryReport;
                Check.True(report.Contains("matching compared fingerprints: 0; with differing compared fingerprints: 0; indeterminate because compared fields are missing: 1"));
                Check.True(report.Contains("not an authoritative manifest")&&report.Contains("remain UNKNOWN"));
                c.ClearMemory();
            });
        }
    }
}
