using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // All fixtures are memory-only. No filesystem, networking or device access.
    public static class ViveDevelopmentStreamIdentityTests
    {
        sealed class Clock:IClock {public double Now {get;set;} public string ClockId {get{return "clock";}}}
        sealed class Provider:IViveProEyeProvider
        {
            public int Calls;public double Observed=1;public ViveProEyePoll Poll()
            {Calls++;var ray=new ViveEyeRay(2,true,true,new Vec3(0,0,0),new Vec3(0,0,2));
                return new ViveProEyePoll(new ViveHeadFrame(Calls,Observed,true,true,Rotation.Identity),
                    new ViveEyeFrame(Calls,-12,Observed,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);}
        }
        sealed class Handle:IViveJournalSinkHandle
        {
            public readonly MemoryStream Memory=new MemoryStream();public int Flushes,Disposals;
            public Stream Stream {get{return Memory;}} public void FlushToStorage(){Flushes++;} public void Dispose(){Disposals++;Memory.Dispose();}
        }
        sealed class Factory:IViveJournalSinkFactory
        {
            public int Opens;public readonly Handle Handle=new Handle();
            public IViveJournalSinkHandle Open(string path,FileMode mode){Check.Equal(FileMode.CreateNew,mode);Opens++;return Handle;}
        }
        static string SyntheticPath(){return Path.Combine(Path.GetPathRoot(Path.GetTempPath()),"vive-test","journal.vdev");}
        static ViveDevelopmentStreamIdentity Identity(ViveDevelopmentOperatorStatus status=ViveDevelopmentOperatorStatus.NotApplicable,
            string operatorId=null,ViveDevelopmentMeasurementRole role=ViveDevelopmentMeasurementRole.Participant)
        {return new ViveDevelopmentStreamIdentity("session","participant",operatorId,role,"participant/vive/epoch","device","clock",
            ViveProEyeRehearsalCoordinates.MappingVersion,status);}
        static ViveDevelopmentProvenance Legacy()
        {return new ViveDevelopmentProvenance("session","participant","operator",ViveDevelopmentMeasurementRole.Participant,
            "participant/vive/epoch","device","clock",ViveProEyeRehearsalCoordinates.MappingVersion,-12,1,1,1,1,true);}
        static ViveDevelopmentProvenance Observed(ViveDevelopmentStreamIdentity identity)
        {return ViveDevelopmentProvenance.FromObserved(identity,-12,1,1,1,1,true);}
        static ViveParticipantEyeSource Automatic(Clock clock,Provider provider)
        {return ViveParticipantEyeSource.CreateForAutomatic("session",1,2,"participant/vive/epoch","device",clock,provider,new ViveProEyeRehearsalCoordinates());}
        static ViveDevelopmentParticipant TaskInFirstBlock(Clock clock)
        {
            var allocation=new Allocation("fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var configuration=new SessionConfiguration("session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var task=new ViveDevelopmentParticipant(configuration,clock,DevelopmentFixtures.Geometry(),map,.5,true);
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));task.OwnerBeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {
                var trial=task.CurrentTrial;task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));task.MarkPresented(trial.TrialId);
                var ink=trial.Stimulus.Ink;double x=ink==Colour.Green?1:ink==Colour.Yellow?-1:0,y=ink==Colour.Red?1:ink==Colour.Blue?-1:0;
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
            }
            task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"owner");task.OwnerBeginBlock(Actor.Owner);clock.Now+=3;task.Tick();return task;
        }
        static List<JournalEvent> BlockEvents(ViveDevelopmentParticipant task)
        {var list=new List<JournalEvent>();foreach(var item in task.ReadEventsAfter(-1,4096))if(item.BlockIndex==0&&item.Segment==0)list.Add(item);return list;}
        static ViveDevelopmentJournalReadResult Read(byte[] bytes,List<ViveDevelopmentJournalRecord> rows=null)
        {using(var memory=new MemoryStream(bytes,false))return ViveDevelopmentJournalReader.Read(memory,rows==null?(Action<ViveDevelopmentJournalRecord>)null:rows.Add);}
        static Dictionary<string,string> Header(string schema,string role,string status,string operatorId)
        {
            var f=new Dictionary<string,string>{{"schema",schema},{"data_source","SyntheticFixture"},{"session_id","session"},
                {"participant_id","participant"},{"operator_id",operatorId},{"role",role},{"stream_id","stream"},{"device_id","device"},
                {"local_clock_id","clock"},{"mapping_version","map"},{"hardware_capture_time_verified","false"},{"apparatus_mapping_validated","false"}};
            if(status!=null)f.Add("operator_status",status);return f;
        }
        static byte[] HeaderBytes(IDictionary<string,string> fields)
        {
            byte[] payload=ContinuousJournal.Payload(0,new byte[16],"header",new Dictionary<string,string>(fields));
            byte[] frame=new byte[8+payload.Length+32];ContinuousJournal.PutInt(frame,payload.Length);ContinuousJournal.PutInt(frame,~payload.Length,4);
            Buffer.BlockCopy(payload,0,frame,8,payload.Length);Buffer.BlockCopy(ContinuousJournal.Digest(new byte[32],payload),0,frame,8+payload.Length,32);
            var result=new byte[ViveDevelopmentJournal.Magic.Length+frame.Length];Buffer.BlockCopy(ViveDevelopmentJournal.Magic,0,result,0,ViveDevelopmentJournal.Magic.Length);
            Buffer.BlockCopy(frame,0,result,ViveDevelopmentJournal.Magic.Length,frame.Length);return result;
        }
        static List<string> ParseCsv(string line)
        {
            var result=new List<string>();var value=new StringBuilder();bool quoted=false;
            for(int i=0;i<line.Length;i++){char c=line[i];if(c=='"'){if(quoted&&i+1<line.Length&&line[i+1]=='"'){value.Append('"');i++;}else quoted=!quoted;}
                else if(c==','&&!quoted){result.Add(value.ToString());value.Length=0;}else value.Append(c);}
            if(quoted)throw new InvalidDataException("Fixture CSV quote mismatch");result.Add(value.ToString());return result;
        }
        static Dictionary<string,string> CsvRow(string header,string line)
        {var names=ParseCsv(header);var values=ParseCsv(line);Check.Equal(names.Count,values.Count);var r=new Dictionary<string,string>();for(int i=0;i<names.Count;i++)r.Add(names[i],values[i]);return r;}

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE v2 identity represents absent operator explicitly without fake device or observation",()=>{
                var id=Identity();Check.Equal(ViveDevelopmentOperatorStatus.NotApplicable,id.OperatorStatus);Check.Equal(null,id.OperatorId);
                Check.True(!id.HardwareCaptureTimeVerified&&!id.MappingValidatedForApparatus);
                Check.True(typeof(ViveDevelopmentStreamIdentity).GetProperty("NativeEyeTimestampOpaque")==null);
                Check.True(typeof(ViveDevelopmentStreamIdentity).GetProperty("PollCompletedLocalSeconds")==null);
                Check.True(typeof(ViveDevelopmentStreamIdentity).GetProperty("OperatorStatus").GetSetMethod()==null);
                var c=new Clock{Now=1};var p=new Provider();var source=Automatic(c,p);Check.Equal(ViveDevelopmentOperatorStatus.NotApplicable,source.OperatorStatus);Check.Equal(0,p.Calls);
                Check.Throws<ArgumentException>(()=>ViveParticipantEyeSource.CreateForAutomatic("session",1,2,"simulator/fake","device",c,p,new ViveProEyeRehearsalCoordinates()));
                Check.Throws<ArgumentException>(()=>ViveParticipantEyeSource.CreateForAutomatic("session",1,2,"vive-pro-eye/operator/fake","device",c,p,new ViveProEyeRehearsalCoordinates()));
            });
            suite.Run("VIVE v2 identity rejects inconsistent operator status role or noncanonical IDs",()=>{
                Check.Throws<ArgumentException>(()=>Identity(ViveDevelopmentOperatorStatus.Present));
                Check.Throws<ArgumentException>(()=>Identity(ViveDevelopmentOperatorStatus.NotApplicable,"operator"));
                Check.Throws<ArgumentException>(()=>Identity(ViveDevelopmentOperatorStatus.NotApplicable,null,ViveDevelopmentMeasurementRole.Operator));
                Check.Throws<ArgumentOutOfRangeException>(()=>Identity((ViveDevelopmentOperatorStatus)99));
                Check.Throws<ArgumentException>(()=>Identity(ViveDevelopmentOperatorStatus.Present,"PARTICIPANT"));
                Check.Equal("operator",Identity(ViveDevelopmentOperatorStatus.Present,"operator").OperatorId);
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentStreamIdentity("s","p",null,ViveDevelopmentMeasurementRole.Participant," stream","d","c","m",ViveDevelopmentOperatorStatus.NotApplicable));
            });
            suite.Run("VIVE v2 header can open before first observation with both opt-ins",()=>{
                var c=new Clock{Now=1};var p=new Provider();var source=Automatic(c,p);var memory=new MemoryStream();var id=Identity();
                Check.Throws<InvalidOperationException>(()=>ViveParticipantRecordingBridge.CreateV2WithIdentity(source,c,id,ViveDevelopmentDataSource.SyntheticFixture,1,2,memory,()=>{},false,true));
                Check.Equal(0L,memory.Length);Check.Equal(0,p.Calls);
                var bridge=ViveParticipantRecordingBridge.CreateV2WithIdentity(source,c,id,ViveDevelopmentDataSource.SyntheticFixture,1,2,memory,()=>{},true,true);
                Check.Equal(0,p.Calls);var coverage=bridge.CoverageSnapshot();Check.True(!coverage.FirstObservationReceived&&!coverage.BeforeFirstObservationCovered&&!coverage.CompleteSessionProven);
                var rows=new List<ViveDevelopmentJournalRecord>();Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(memory.ToArray(),rows).Status);
                Check.Equal("vive-development-journal-v2",rows[0].Fields["schema"]);Check.Equal("NotApplicable",rows[0].Fields["operator_status"]);
                Check.Equal(null,rows[0].Fields["operator_id"]);Check.True(!rows[0].Fields.ContainsKey("native_eye_timestamp_opaque"));
                Check.True(!rows[0].Fields.ContainsKey("poll_started_local_seconds"));
                Check.Equal(ViveParticipantRecordingPoll.Recorded,bridge.Poll());Check.Equal(1,p.Calls);
                Check.True(bridge.CoverageSnapshot().FirstObservationReceived&&!bridge.CoverageSnapshot().BeforeFirstObservationCovered);
                Check.True(bridge.Close());rows.Clear();Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(memory.ToArray(),rows).Status);
                Check.Equal("-12",rows[1].Fields["native_eye_timestamp_opaque"]);Check.Equal("2",rows[1].Fields["raw_combined_sdk_direction_z"]);
                Check.Equal("1",rows[1].Fields["derived_direction_z"]);Check.Equal("false",rows[0].Fields["hardware_capture_time_verified"]);
            });
            suite.Run("VIVE v2 identity cannot invent a gap before any observed frame",()=>{
                var c=new Clock{Now=2};var p=new Provider();var memory=new MemoryStream();
                var bridge=ViveParticipantRecordingBridge.CreateV2WithIdentity(Automatic(c,p),c,Identity(),ViveDevelopmentDataSource.SyntheticFixture,1,2,memory,()=>{},true,true);
                Check.True(!bridge.RecordGap(1,2,ViveDevelopmentGapReason.Missing));Check.True(bridge.IsFaulted);Check.Equal(0,p.Calls);
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(memory.ToArray()).Status);
            });
            suite.Run("VIVE v2 sink uses one identity-only in-memory stream and rejects opt-in identity mismatch before opening",()=>{
                var c=new Clock{Now=1};var p=new Provider();var f=new Factory();var source=Automatic(c,p);var id=Identity();
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentJournalSink.OpenV2WithIdentityFactory(SyntheticPath(),source,c,id,ViveDevelopmentDataSource.SyntheticFixture,1,2,false,true,f));
                Check.Equal(0,f.Opens);Check.Equal(0,p.Calls);
                Check.Throws<ArgumentException>(()=>ViveDevelopmentJournalSink.OpenV2WithIdentityFactory(SyntheticPath(),source,c,
                    Identity(ViveDevelopmentOperatorStatus.Present,"operator"),ViveDevelopmentDataSource.SyntheticFixture,1,2,true,true,f));
                Check.Equal(0,f.Opens);Check.Equal(0,p.Calls);
                var exhausted=Automatic(c,p);typeof(ViveParticipantEyeSource).GetField("readAttemptGeneration",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(exhausted,Int64.MaxValue);
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentJournalSink.OpenV2WithIdentityFactory(SyntheticPath(),exhausted,c,id,ViveDevelopmentDataSource.SyntheticFixture,1,2,true,true,f));
                Check.Equal(0,f.Opens);Check.Equal(0,p.Calls);
                var sink=ViveDevelopmentJournalSink.OpenV2WithIdentityFactory(SyntheticPath(),source,c,id,ViveDevelopmentDataSource.SyntheticFixture,1,2,true,true,f);
                Check.Equal(1,f.Opens);Check.Equal(0,p.Calls);Check.True(!sink.CoverageSnapshot().BeforeFirstObservationCovered);
                Check.True(sink.Close());Check.Equal(1,f.Handle.Disposals);var rows=new List<ViveDevelopmentJournalRecord>();
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(f.Handle.Memory.ToArray(),rows).Status);
                Check.Equal("NotApplicable",rows[0].Fields["operator_status"]);Check.Equal(null,rows[0].Fields["operator_id"]);
            });
            suite.Run("VIVE v2 real-eye automatic sink starts before first sample without inventing an operator",()=>{
                // Entire test is synthetic; RealVive exercises source labelling only.
                var c=new Clock{Now=1};var task=TaskInFirstBlock(c);var p=new Provider{Observed=c.Now};var f=new Factory();var id=Identity();
                var source=ViveParticipantEyeSource.CreateForAutomatic("session",0,0,"participant/vive/epoch","device",c,p,new ViveProEyeRehearsalCoordinates());
                Check.Throws<ArgumentException>(()=>ViveDevelopmentJournalSink.OpenV2WithIdentityFactory(SyntheticPath(),source,c,id,ViveDevelopmentDataSource.RealVive,0,0,true,true,f));
                Check.Equal(0,f.Opens);Check.Equal(0,p.Calls);
                var sink=ViveDevelopmentJournalSink.OpenV2WithIdentityFactory(SyntheticPath(),source,c,id,ViveDevelopmentDataSource.RealVive,0,0,true,true,f,task);
                Check.Equal(1,f.Opens);Check.Equal(0,p.Calls);var initial=sink.CoverageSnapshot();
                Check.True(!initial.FirstObservationReceived&&!initial.BeforeFirstObservationCovered&&!initial.CompleteSessionProven);
                var events=BlockEvents(task);Check.True(events.Count>0);
                Check.True(sink.BeginAuthoritativeEvents(events[0].Sequence,true,ViveDevelopmentTaskOrigin.SimulationDevelopment));
                foreach(var item in events)Check.True(sink.RecordAuthoritativeTaskEvent(item));
                Check.Equal(ViveParticipantRecordingPoll.Recorded,sink.Poll());Check.Equal(1,p.Calls);
                task.EndTechnical(Actor.Owner,"synthetic end");var after=BlockEvents(task);
                for(int i=events.Count;i<after.Count;i++)Check.True(sink.RecordAuthoritativeTaskEvent(after[i]));
                Check.True(sink.Close());var rows=new List<ViveDevelopmentJournalRecord>();
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(f.Handle.Memory.ToArray(),rows).Status);
                Check.Equal("RealVive",rows[0].Fields["data_source"]);Check.Equal("NotApplicable",rows[0].Fields["operator_status"]);Check.Equal(null,rows[0].Fields["operator_id"]);
                var extracted=ViveDevelopmentJournalExtraction.Extract(new MemoryStream(f.Handle.Memory.ToArray()),false,100,100000);
                Check.True(extracted.Csv.Contains("SIMULATION_DEVELOPMENT"));Check.True(!sink.CoverageSnapshot().CompleteSessionProven&&!sink.CoverageSnapshot().ResearchExportAllowed);
            });
            suite.Run("VIVE v2 reader validates schema operator null status role and never treats v1 strings as absent",()=>{
                string v1=ViveDevelopmentJournal.SchemaVersion,v2=ViveDevelopmentJournal.IdentitySchemaVersion;
                Check.Equal("vive-development-journal-v1",v1);Check.Equal("vive-development-journal-v2",v2);
                var legacy=new List<ViveDevelopmentJournalRecord>();Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(HeaderBytes(Header(v1,"Participant",null,"NOT_APPLICABLE")),legacy).Status);
                Check.Equal("NOT_APPLICABLE",legacy[0].Fields["operator_id"]);Check.True(!legacy[0].Fields.ContainsKey("operator_status"));
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(HeaderBytes(Header(v2,"Participant","NotApplicable",null))).Status);
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(HeaderBytes(Header(v2,"Operator","Present","operator"))).Status);
                foreach(var f in new[]{Header(v2,"Operator","NotApplicable",null),Header(v2,"Participant","NotApplicable",""),
                    Header(v2,"Participant","NotApplicable","operator"),Header(v2,"Participant","Present",null),
                    Header(v2,"Participant","FutureStatus","operator"),Header(v2,"Participant",null,"operator"),Header("future-schema","Participant","Present","operator")})
                    Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(HeaderBytes(f)).Status);
                var extra=Header(v2,"Participant","Present","operator");extra.Add("unexpected","x");Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(HeaderBytes(extra)).Status);
            });
            suite.Run("VIVE v1 and v2 writers reject schema downgrade or mixed records",()=>{
                var old=Legacy();var id=Identity();var newer=Observed(id);
                Check.True(!old.IsVersionTwoIdentity&&newer.IsVersionTwoIdentity);
                Check.Throws<ArgumentException>(()=>ViveDevelopmentProvenance.FromObserved(id,null,1,1,null,null,true));
                var oldRecord=ViveDevelopmentExportRecord.Observation(old,ViveDevelopmentDataSource.SyntheticFixture,0,1,2,1,2,new Vec3(0,0,2),true,new Vec3(0,0,1),"frame",true);
                var newRecord=ViveDevelopmentExportRecord.Observation(newer,ViveDevelopmentDataSource.SyntheticFixture,0,1,2,1,2,new Vec3(0,0,2),true,new Vec3(0,0,1),"frame",true);
                ViveDevelopmentExport.RequireLegacyRecord(oldRecord);Check.Throws<InvalidDataException>(()=>ViveDevelopmentExport.RequireLegacyRecord(newRecord));
                var oldJournal=new ViveDevelopmentJournal(new MemoryStream(),()=>{},old,ViveDevelopmentDataSource.SyntheticFixture,true,true);
                Check.Throws<ArgumentException>(()=>oldJournal.TryAppendObservation(newRecord));
                var newJournal=ViveDevelopmentJournal.CreateV2WithIdentity(new MemoryStream(),()=>{},id,ViveDevelopmentDataSource.SyntheticFixture,true,true);
                Check.Throws<ArgumentException>(()=>newJournal.TryAppendObservation(oldRecord));
                Check.Throws<InvalidOperationException>(()=>new ViveDevelopmentJournal(new MemoryStream(),()=>{},newer,ViveDevelopmentDataSource.SyntheticFixture,true,true));
                // Isolate schema-version checking even when every identity field matches.
                var sameId=Identity(ViveDevelopmentOperatorStatus.Present,"operator");var sameNew=Observed(sameId);
                var sameRecord=ViveDevelopmentExportRecord.Observation(sameNew,ViveDevelopmentDataSource.SyntheticFixture,0,1,2,1,2,new Vec3(0,0,2),true,new Vec3(0,0,1),"frame",true);
                var oldSame=new ViveDevelopmentJournal(new MemoryStream(),()=>{},old,ViveDevelopmentDataSource.SyntheticFixture,true,true);
                Check.Throws<ArgumentException>(()=>oldSame.TryAppendObservation(sameRecord));
                var newSame=ViveDevelopmentJournal.CreateV2WithIdentity(new MemoryStream(),()=>{},sameId,ViveDevelopmentDataSource.SyntheticFixture,true,true);
                Check.Throws<ArgumentException>(()=>newSame.TryAppendObservation(oldRecord));
            });
            suite.Run("VIVE v2 extractor labels absent operator and simulated task while keeping v1 separate",()=>{
                var id=Identity();var memory=new MemoryStream();var journal=ViveDevelopmentJournal.CreateV2WithIdentity(memory,()=>{},id,ViveDevelopmentDataSource.RealVive,true,true);
                var record=ViveDevelopmentExportRecord.Observation(Observed(id),ViveDevelopmentDataSource.RealVive,0,1,2,1,2,new Vec3(0,0,2),true,new Vec3(0,0,1),"frame",true);
                Check.True(journal.TryAppendObservation(record));Check.True(journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,1,1,2,"UNRESOLVED",null,"AE2;SIMULATION_DEVELOPMENT;0;cmF0aW5n;0"));Check.True(journal.Close());
                var result=ViveDevelopmentJournalExtraction.Extract(new MemoryStream(memory.ToArray()),false,100,100000);var lines=result.Csv.TrimEnd('\n').Split('\n');
                var eye=CsvRow(lines[0],lines[1]);var task=CsvRow(lines[0],lines[2]);
                Check.Equal("vive-development-journal-extract-v2",eye["extraction_schema"]);Check.Equal("NotApplicable",eye["operator_status"]);Check.Equal("",eye["operator_id"]);
                Check.Equal("SIMULATION_DEVELOPMENT",task["task_origin_declared"]);Check.Equal("RealVive",task["data_source"]);Check.Equal("false",task["identity_authenticated"]);
                var invalid=new MemoryStream();var bad=ViveDevelopmentJournal.CreateV2WithIdentity(invalid,()=>{},id,ViveDevelopmentDataSource.RealVive,true,true);
                Check.True(bad.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.AuthoritativeEvent,1,1,2,"UNRESOLVED",null,"AE2;APPARATUS;0;cmF0aW5n;0"));Check.True(bad.Close());
                Check.Throws<InvalidDataException>(()=>ViveDevelopmentJournalExtraction.Extract(new MemoryStream(invalid.ToArray()),false,100,100000));
                var unlabeled=new MemoryStream();var unlabelledJournal=ViveDevelopmentJournal.CreateV2WithIdentity(unlabeled,()=>{},id,ViveDevelopmentDataSource.RealVive,true,true);
                Check.True(unlabelledJournal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.Paused,1,1,2,"Paused",null,"fixture"));Check.True(unlabelledJournal.Close());
                Check.Throws<InvalidDataException>(()=>ViveDevelopmentJournalExtraction.Extract(new MemoryStream(unlabeled.ToArray()),false,100,100000));
            });
        }
    }
}
