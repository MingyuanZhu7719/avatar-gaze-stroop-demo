using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // All journals are generated from fictional data in MemoryStream. Even a
    // fixture whose header says RealVive contains no hardware observations.
    public static class ViveSessionEvidenceAnalyzerTests
    {
        sealed class Row
        {public long Sequence;public string Kind,Key,Value;public Row(long sequence,string kind,string key=null,string value=null){Sequence=sequence;Kind=kind;Key=key;Value=value;}}
        static string Encode(string value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));}
        static byte[] Journal(string streamId,int block,int segment,bool clean,ViveDevelopmentDataSource dataSource,string session,params Row[] rows)
        {
            var stream=new MemoryStream();var identity=new ViveDevelopmentStreamIdentity(session,"fixture-participant",null,ViveDevelopmentMeasurementRole.Participant,
                streamId,"fixture-device","fixture-clock","fixture-map",ViveDevelopmentOperatorStatus.NotApplicable);
            var journal=ViveDevelopmentJournal.CreateV2WithIdentity(stream,()=>{},identity,dataSource,true,true);
            if(block>=0)Check.True(journal.TryAppendInitialUnknownCoverage(block,segment,0,.1));
            foreach(var row in rows)
            {
                string detail="AE2;SIMULATION_DEVELOPMENT;"+row.Sequence+";"+Encode(row.Kind)+";"+(row.Key==null?"0":"1;"+Encode(row.Key)+";"+Encode(row.Value));
                Check.True(journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,1,block,segment,"UNRESOLVED",null,detail));
            }
            if(clean)Check.True(journal.Close());
            return stream.ToArray();
        }
        static MemoryStream Input(byte[] value){return new MemoryStream(value,false);}
        static byte[] One(string stream,int block,int segment,long seq,string kind="block_task_ended",bool clean=true,string session="fixture-session",ViveDevelopmentDataSource source=ViveDevelopmentDataSource.SyntheticFixture)
        {return Journal(stream,block,segment,clean,source,session,new Row(seq,kind));}
        sealed class Probe:Stream
        {
            public int Inspections;public override bool CanRead {get{Inspections++;throw new Exception("private-path-marker");}}
            public override bool CanWrite {get{Inspections++;throw new Exception("private-path-marker");}}
            public override bool CanSeek {get{return false;}}public override long Length {get{throw new NotSupportedException();}}
            public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n){throw new Exception("private-path-marker");}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long v){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        sealed class FailRead:Stream
        {
            public override bool CanRead {get{return true;}}public override bool CanWrite {get{return false;}}public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n){throw new Exception("private-path-marker");}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long v){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        sealed class FailAfter:Stream
        {
            readonly byte[] bytes;readonly int cutoff;int position;
            public FailAfter(byte[] bytes,int cutoff){this.bytes=bytes;this.cutoff=cutoff;}
            public override bool CanRead {get{return true;}}public override bool CanWrite {get{return false;}}public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n)
            {if(position>=cutoff)throw new IOException("secret-location");int count=Math.Min(n,Math.Min(cutoff-position,bytes.Length-position));Buffer.BlockCopy(bytes,position,b,o,count);position+=count;return count;}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long v){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        sealed class ConcurrentProbe:Stream
        {
            readonly MemoryStream input;readonly ViveSessionEvidenceAnalyzer analyzer;bool probed;
            public ConcurrentProbe(byte[] bytes,ViveSessionEvidenceAnalyzer analyzer){input=Input(bytes);this.analyzer=analyzer;}
            public override bool CanRead {get{return true;}}public override bool CanWrite {get{return false;}}public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n)
            {
                if(!probed){probed=true;var thread=new Thread(()=>{try{analyzer.Snapshot();}catch(InvalidOperationException){}});thread.Start();if(!thread.Join(3000))throw new IOException();}
                return input.Read(b,o,n);
            }
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long v){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE session evidence reports observed four blocks without claiming completeness",()=>{
                var analyzer=new ViveSessionEvidenceAnalyzer(12,1000,1000000);long sequence=0;
                for(int b=0;b<4;b++){
                    var rows=new List<Row>{new Row(sequence++,"block_configuration_locked"),new Row(sequence++,"block_task_ended")};
                    for(int i=0;i<Questionnaire.Count;i++)rows.Add(new Row(sequence++,"rating","item",Questionnaire.Id(i)));
                    rows.Add(new Row(sequence++,"questionnaire_completed"));if(b==3)rows.Add(new Row(sequence++,"session_ended","reason","Completed"));
                    var bytes=Journal("stream-"+b,b,0,true,ViveDevelopmentDataSource.SyntheticFixture,"fixture-session",rows.ToArray());
                    using(var input=Input(bytes))Check.True(analyzer.AddJournal(input,true,false));
                }
                var summary=analyzer.Snapshot();Check.Equal(4,summary.JournalsAccepted);Check.Equal(4,summary.CleanCloseJournals);
                Check.Equal((ViveDevelopmentDataSource?)ViveDevelopmentDataSource.SyntheticFixture,summary.DeclaredEyeDataSource);
                Check.Equal((ViveDevelopmentTaskOrigin?)ViveDevelopmentTaskOrigin.SimulationDevelopment,summary.DecodedAuthoritativeEventOrigin);
                Check.True(!summary.PracticeStartMarkerSeen&&!summary.PracticeEndMarkerSeen&&summary.SessionEndMarkerSeen);
                Check.Equal("Completed",summary.SessionEndReasonCategory);Check.Equal(4L,summary.InitialUnknownWindowsSeen);
                foreach(var block in summary.Blocks){Check.True(block.ConfigurationMarkerSeen&&block.TaskEndMarkerSeen&&block.QuestionnaireEndMarkerSeen);
                    Check.Equal(5,block.DistinctKnownRatingItemsSeen);Check.Equal(1,block.DistinctSegmentContextsSeen);}
                Check.True(!summary.CompleteSessionProven&&!summary.ContinuousCoverageProven&&!summary.DurableStorageProven&&!summary.ResearchValidityProven&&!summary.DiskExportAvailable);
            });
            suite.Run("VIVE session evidence tracks practice and resumed segment markers only if seen",()=>{
                var a=new ViveSessionEvidenceAnalyzer(5,100,100000);
                var practice=Journal("practice",-1,0,true,ViveDevelopmentDataSource.SyntheticFixture,"fixture-session",new Row(0,"practice_started"),new Row(1,"practice_completed"));
                using(var s=Input(practice))Check.True(a.AddJournal(s,true,false));
                using(var s=Input(One("seg0",0,0,2,"trial_interrupted")))Check.True(a.AddJournal(s,true,false));
                using(var s=Input(One("seg1",0,1,3)))Check.True(a.AddJournal(s,true,false));
                var sum=a.Snapshot();Check.True(sum.PracticeStartMarkerSeen&&sum.PracticeEndMarkerSeen);Check.Equal(2,sum.Blocks[0].DistinctSegmentContextsSeen);
                Check.Equal(0,sum.Blocks[1].DistinctSegmentContextsSeen);Check.True(!sum.CompleteSessionProven);
            });
            suite.Run("VIVE session evidence rejects duplicate and mismatched streams atomically",()=>{
                var a=new ViveSessionEvidenceAnalyzer(5,100,100000);var first=One("stream",0,0,0);
                using(var s=Input(first))Check.True(a.AddJournal(s,true,false));
                using(var s=Input(first))Check.True(!a.AddJournal(s,true,false));Check.Equal("duplicate_declared_stream",a.Snapshot().LastAddIssue);
                using(var s=Input(One("another",0,0,0)))Check.True(!a.AddJournal(s,true,false));Check.Equal("duplicate_task_sequence",a.LastIssue);
                using(var s=Input(One("other-session",0,0,1,session:"other")))Check.True(!a.AddJournal(s,true,false));Check.Equal("different_declared_identity",a.LastIssue);
                Check.Equal(1,a.Snapshot().JournalsAccepted);
            });
            suite.Run("VIVE session evidence requires separate incomplete-prefix recovery",()=>{
                var bytes=One("incomplete",0,0,0,clean:false);var a=new ViveSessionEvidenceAnalyzer(3,100,100000);
                using(var s=Input(bytes))Check.True(!a.AddJournal(s,true,false));Check.Equal(0,a.Snapshot().JournalsAccepted);
                using(var s=Input(bytes))Check.True(a.AddJournal(s,true,true));Check.Equal(1,a.Snapshot().RecoveredPrefixJournals);
                Check.True(!a.Snapshot().CompleteSessionProven);
            });
            suite.Run("VIVE session evidence refuses AE2 event gaps and reordering",()=>{
                foreach(var rows in new[]{new[]{new Row(1,"block_task_ended"),new Row(3,"questionnaire_completed")},new[]{new Row(2,"block_task_ended"),new Row(1,"questionnaire_completed")}}){
                    var a=new ViveSessionEvidenceAnalyzer(3,100,100000);var bytes=Journal("bad-sequence",0,0,true,ViveDevelopmentDataSource.SyntheticFixture,"fixture-session",rows);
                    using(var s=Input(bytes))Check.True(!a.AddJournal(s,true,false));Check.Equal(0,a.Snapshot().JournalsAccepted);
                }
            });
            suite.Run("VIVE session evidence rejects practice and block markers in wrong contexts",()=>{
                var a=new ViveSessionEvidenceAnalyzer(3,100,100000);
                using(var s=Input(One("bad-practice",0,0,0,"practice_completed")))Check.True(!a.AddJournal(s,true,false));
                Check.True(!a.Snapshot().PracticeEndMarkerSeen&&a.Snapshot().JournalsAccepted==0);
                using(var s=Input(One("bad-block",-1,0,1,"questionnaire_completed")))Check.True(!a.AddJournal(s,true,false));
                Check.True(!a.Snapshot().Blocks[0].QuestionnaireEndMarkerSeen&&a.Snapshot().JournalsAccepted==0);
            });
            suite.Run("VIVE session evidence labels synthetic fake RealVive header with simulated task origin",()=>{
                var a=new ViveSessionEvidenceAnalyzer(3,100,100000);var bytes=One("fake-real-header",0,0,0,source:ViveDevelopmentDataSource.RealVive);
                using(var s=Input(bytes))Check.True(a.AddJournal(s,true,false));var sum=a.Snapshot();
                Check.Equal((ViveDevelopmentDataSource?)ViveDevelopmentDataSource.RealVive,sum.DeclaredEyeDataSource);
                Check.Equal((ViveDevelopmentTaskOrigin?)ViveDevelopmentTaskOrigin.SimulationDevelopment,sum.DecodedAuthoritativeEventOrigin);
                Check.True(!sum.ResearchValidityProven&&!sum.IdentityAuthenticated);
            });
            suite.Run("VIVE session evidence leaves unclassified task event origin unknown",()=>{
                var bytes=new MemoryStream();var id=new ViveDevelopmentStreamIdentity("fixture-session","fixture-participant",null,
                    ViveDevelopmentMeasurementRole.Participant,"fixture-unclassified","fixture-device","fixture-clock","fixture-map",ViveDevelopmentOperatorStatus.NotApplicable);
                var journal=ViveDevelopmentJournal.CreateV2WithIdentity(bytes,()=>{},id,ViveDevelopmentDataSource.SyntheticFixture,true,true);
                Check.True(journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.Paused,1,0,0,"UNRESOLVED",null,"fixture"));Check.True(journal.Close());
                var a=new ViveSessionEvidenceAnalyzer(1,100,100000);using(var s=Input(bytes.ToArray()))Check.True(a.AddJournal(s,true,false));
                var sum=a.Snapshot();Check.Equal(1L,sum.UnclassifiedTaskEventsSeen);Check.True(sum.UnclassifiedTaskEventOriginUnknown);
                Check.Equal((ViveDevelopmentTaskOrigin?)null,sum.DecodedAuthoritativeEventOrigin);
            });
            suite.Run("VIVE session evidence refuses unconfirmed or writable stream before read",()=>{
                var a=new ViveSessionEvidenceAnalyzer(2,100,100000);var p=new Probe();Check.True(!a.AddJournal(p,false,false));Check.Equal(0,p.Inspections);
                using(var writable=new MemoryStream()){Check.True(!a.AddJournal(writable,true,false));Check.Equal("read_only_stream_required",a.LastIssue);}
            });
            suite.Run("VIVE session evidence sanitizes input errors and observes limits",()=>{
                var a=new ViveSessionEvidenceAnalyzer(2,100,100000);using(var s=new FailRead())Check.True(!a.AddJournal(s,true,true));
                Check.True(a.LastIssue.IndexOf("private-path-marker",StringComparison.Ordinal)<0);Check.Equal(0,a.Snapshot().JournalsAccepted);
                a=new ViveSessionEvidenceAnalyzer(2,100,64);using(var s=Input(One("stream",0,0,0)))Check.True(!a.AddJournal(s,true,true));
                Check.Equal(0,a.Snapshot().JournalsAccepted);
                a=new ViveSessionEvidenceAnalyzer(1,100,100000);using(var s=Input(One("a",0,0,0)))Check.True(a.AddJournal(s,true,false));
                using(var s=Input(One("b",1,0,1)))Check.True(!a.AddJournal(s,true,false));Check.Equal("analysis_limit",a.LastIssue);
            });
            suite.Run("VIVE session evidence faults on cross-thread access",()=>{
                var a=new ViveSessionEvidenceAnalyzer(1,100,100000);Exception error=null;
                var thread=new Thread(()=>{try{a.Snapshot();}catch(Exception e){error=e;}});thread.Start();Check.True(thread.Join(3000));
                Check.True(error is InvalidOperationException&&a.Faulted);Check.Throws<InvalidOperationException>(()=>a.Snapshot());
            });
            suite.Run("VIVE session evidence counts unknown recovery and gap without exposing gaze",()=>{
                var bytes=new MemoryStream();var id=new ViveDevelopmentStreamIdentity("fixture-session","fixture-participant",null,
                    ViveDevelopmentMeasurementRole.Participant,"fixture-gap-stream","fixture-device","fixture-clock","fixture-map",ViveDevelopmentOperatorStatus.NotApplicable);
                var journal=ViveDevelopmentJournal.CreateV2WithIdentity(bytes,()=>{},id,ViveDevelopmentDataSource.SyntheticFixture,true,true);
                Check.True(journal.TryAppendInitialUnknownCoverage(0,0,0,.1));
                var provenance=ViveDevelopmentProvenance.FromObserved(id,1,.1,.2,.2,.2,true);
                var observation=ViveDevelopmentExportRecord.Observation(provenance,ViveDevelopmentDataSource.SyntheticFixture,0,0,0,1,2,new Vec3(0,0,1),true,new Vec3(0,0,1),"fixture-frame",true);
                Check.True(journal.TryAppendObservation(observation));Check.True(journal.TryAppendRecoveryUnknownCoverage(0,0,.2,.4,ViveDevelopmentGapReason.Missing));
                Check.True(journal.TryAppendObservation(ViveDevelopmentExportRecord.Gap(provenance,ViveDevelopmentDataSource.SyntheticFixture,1,0,0,.4,.5,ViveDevelopmentGapReason.Missing)));
                Check.True(journal.Close());var a=new ViveSessionEvidenceAnalyzer(2,100,100000);
                using(var s=Input(bytes.ToArray()))Check.True(a.AddJournal(s,true,false));var summary=a.Snapshot();
                Check.Equal(1L,summary.InitialUnknownWindowsSeen);Check.Equal(1L,summary.RecoveryUnknownWindowsSeen);Check.Equal(1L,summary.ExplicitGapsSeen);
                Check.True(!summary.ContinuousCoverageProven&&!summary.ResearchValidityProven);
            });
            suite.Run("VIVE session evidence never recovers a read exception or publishes a partial merge",()=>{
                var a=new ViveSessionEvidenceAnalyzer(3,100,100000);using(var s=Input(One("first",0,0,0)))Check.True(a.AddJournal(s,true,false));
                var bytes=One("second",1,0,1);using(var s=new FailAfter(bytes,bytes.Length-5))Check.True(!a.AddJournal(s,true,true));
                var summary=a.Snapshot();Check.Equal(1,summary.JournalsAccepted);Check.Equal("input_unavailable_or_limit",summary.LastAddIssue);
                Check.True(summary.LastAddIssue.IndexOf("secret-location",StringComparison.Ordinal)<0);
            });
            suite.Run("VIVE session evidence requires valid header and opt-in for a damaged tail",()=>{
                var badHeader=One("bad-header",0,0,0);badHeader[0]^=1;var a=new ViveSessionEvidenceAnalyzer(3,100,100000);
                using(var s=Input(badHeader))Check.True(!a.AddJournal(s,true,true));Check.Equal(0,a.Snapshot().JournalsAccepted);
                var badTail=One("bad-tail",0,0,0);badTail[badTail.Length-1]^=1;
                using(var s=Input(badTail))Check.True(!a.AddJournal(s,true,false));Check.Equal(0,a.Snapshot().JournalsAccepted);
                using(var s=Input(badTail))Check.True(a.AddJournal(s,true,true));var summary=a.Snapshot();
                Check.Equal(1,summary.RecoveredPrefixJournals);Check.True(summary.Blocks[0].TaskEndMarkerSeen&&!summary.CompleteSessionProven);
            });
            suite.Run("VIVE session evidence rejects concurrent misuse before pending commit",()=>{
                var a=new ViveSessionEvidenceAnalyzer(1,100,100000);
                using(var s=new ConcurrentProbe(One("concurrent",0,0,0),a))Check.True(!a.AddJournal(s,true,false));
                Check.True(a.Faulted);Check.Equal("analyzer_faulted",a.LastIssue);Check.Throws<InvalidOperationException>(()=>a.Snapshot());
            });
        }
    }
}
