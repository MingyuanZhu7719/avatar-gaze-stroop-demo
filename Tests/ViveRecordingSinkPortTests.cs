using System;
using System.Collections.Generic;
using System.IO;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveRecordingSinkPortTests
    {
        sealed class Clock:IClock {public double Now {get;set;} public string ClockId {get{return "port-clock";}}}
        sealed class Provider:IViveProEyeProvider {public ViveProEyePoll Next;public int Calls;public ViveProEyePoll Poll(){Calls++;return Next;}}
        sealed class FailingMemory:MemoryStream
        {public int Writes,FailAt=-1;public override void Write(byte[] buffer,int offset,int count){Writes++;if(Writes==FailAt)throw new IOException("synthetic failure");base.Write(buffer,offset,count);}}
        sealed class Handle:IViveJournalSinkHandle
        {public FailingMemory Memory=new FailingMemory();public Stream Stream {get{return Memory;}} public int Disposals;public void FlushToStorage(){} public void Dispose(){Disposals++;Memory.Dispose();}}
        sealed class Factory:IViveJournalSinkFactory
        {public int Opens;public Handle Handle=new Handle();public IViveJournalSinkHandle Open(string path,FileMode mode){Check.Equal(FileMode.CreateNew,mode);Opens++;return Handle;}}
        static byte[] AddUnknown(byte[] prefix,long sequence,string start,string end)
        {
            var token=new byte[16];Buffer.BlockCopy(prefix,24,token,0,16);var prior=new byte[32];Buffer.BlockCopy(prefix,prefix.Length-32,prior,0,32);
            var fields=new Dictionary<string,string>{{"block_index","0"},{"segment","0"},{"coverage_start_local_seconds",start},{"coverage_end_local_seconds",end}};
            var payload=ContinuousJournal.Payload(sequence,token,"initial_coverage_unknown",fields);var bytes=new byte[prefix.Length+8+payload.Length+32];
            Buffer.BlockCopy(prefix,0,bytes,0,prefix.Length);ContinuousJournal.PutInt(bytes,payload.Length,prefix.Length);
            ContinuousJournal.PutInt(bytes,~payload.Length,prefix.Length+4);Buffer.BlockCopy(payload,0,bytes,prefix.Length+8,payload.Length);
            Buffer.BlockCopy(ContinuousJournal.Digest(prior,payload),0,bytes,prefix.Length+8+payload.Length,32);return bytes;
        }
        sealed class Fixture
        {
            public Clock Clock=new Clock{Now=1};public Provider Provider=new Provider();public Factory Factory=new Factory();
            public ViveDevelopmentParticipant Task;public ViveParticipantEyeSource Source;public ViveRecordingSinkPort Port;public long First;int sequence;
            public Fixture()
            {
                var allocation=new Allocation("fixture",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var config=new SessionConfiguration("session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,DevelopmentFixtures.Geometry(),map,.5,true);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Task.OwnerBeginPractice(Actor.Owner);
                for(int i=0;i<8;i++)
                {
                    var trial=Task.CurrentTrial;Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Task.MarkPresented(trial.TrialId);
                    var c=trial.Stimulus.Ink;double x=c==Colour.Green?1:c==Colour.Yellow?-1:0,y=c==Colour.Red?1:c==Colour.Blue?-1:0;
                    Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                }
                Task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"owner");Task.OwnerBeginBlock(Actor.Owner);Clock.Now+=3;Task.Tick();
                Source=ViveParticipantEyeSource.CreateForAutomatic("session",0,0,"participant/stream","device",Clock,Provider,new ViveProEyeRehearsalCoordinates());
                var id=new ViveDevelopmentStreamIdentity("session","participant",null,ViveDevelopmentMeasurementRole.Participant,"participant/stream","device","port-clock",
                    ViveProEyeRehearsalCoordinates.MappingVersion,ViveDevelopmentOperatorStatus.NotApplicable);
                var path=Path.Combine(Path.GetPathRoot(Path.GetTempPath()),"fixture","only-injected.vdev");
                Port=new ViveRecordingSinkPort(path,Source,Clock,id,Task,ViveDevelopmentDataSource.RealVive,true,true,Factory);
                First=-1;foreach(var item in Task.ReadEventsAfter(-1,4096))if(item.BlockIndex==0&&item.Segment==0){First=item.Sequence;break;}
            }
            public ViveRecordingSessionCoordinator Started()
            {var c=new ViveRecordingSessionCoordinator(Task,Source,Port,Clock,true,true,true);Check.True(c.BeginTaskEvents(First,true),c.LastIssue);return c;}
            public ViveParticipantEyeObservation Observe(bool valid=true)
            {
                sequence++;var ray=new ViveEyeRay(2,true,valid,new Vec3(0,0,0),new Vec3(0,0,1));
                Provider.Next=new ViveProEyePoll(new ViveHeadFrame(sequence,Clock.Now,true,true,Rotation.Identity),
                    new ViveEyeFrame(sequence,sequence,Clock.Now,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                ViveParticipantEyeObservation result;Check.True(Source.TryRead(out result));return result;
            }
            public void Missing()
            {Provider.Next=new ViveProEyePoll(null,null,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);ViveParticipantEyeObservation ignored;Check.True(!Source.TryRead(out ignored));}
            public void EndAndDrain(ViveRecordingSessionCoordinator c)
            {Task.EndTechnical(Actor.Owner,"synthetic");foreach(var item in Task.ReadEventsAfter(First-1,4096))if(item.BlockIndex==0&&item.Segment==0)Check.True(c.RecordTaskEvent(item),c.LastIssue);}
            public ViveDevelopmentJournalReadResult Read(List<ViveDevelopmentJournalRecord> rows)
            {return ViveDevelopmentJournalReader.Read(new MemoryStream(Factory.Handle.Memory.ToArray()),rows.Add);}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("sink port is dormant and two-phase commit records initial unknown before eye sample",()=>{
                var f=new Fixture();Check.True(f.Port.IsDormant);Check.Equal(0,f.Factory.Opens);Check.Equal(0,f.Provider.Calls);
                var c=f.Started();var value=f.Observe();var pending=c.Observe(value);var before=new List<ViveDevelopmentJournalRecord>();f.Read(before);
                Check.Equal(1,before.Count);Check.True(c.Commit(pending),c.LastIssue);f.EndAndDrain(c);Check.True(c.CloseAfterSegment(),c.LastIssue);
                var rows=new List<ViveDevelopmentJournalRecord>();Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,f.Read(rows).Status);
                Check.Equal("initial_coverage_unknown",rows[1].Kind);Check.Equal("1",rows[1].Fields["coverage_start_local_seconds"]);
                Check.Equal("4",rows[1].Fields["coverage_end_local_seconds"]);Check.Equal("observation",rows[2].Kind);
                var csv=ViveDevelopmentJournalExtraction.Extract(new MemoryStream(f.Factory.Handle.Memory.ToArray()),false,100,100000).Csv;
                Check.True(csv.Contains("initial_coverage_unknown")&&csv.Contains("coverage_start_local_seconds"));
                Check.True(!c.ContinuousCoverageProven&&!c.ResearchExportAllowed);
            });
            suite.Run("sink port records duplicate without gap and recovery gap before sample",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observe())),c.LastIssue);
                ViveParticipantEyeObservation duplicate;Check.True(!f.Source.TryRead(out duplicate));Check.True(c.AcknowledgeDuplicateRead(),c.LastIssue);
                f.Clock.Now=5;f.Missing();Check.True(c.MarkUnavailable(ViveDevelopmentGapReason.Missing),c.LastIssue);
                f.Clock.Now=7;Check.True(c.Commit(c.Observe(f.Observe())),c.LastIssue);
                var sink=(ViveDevelopmentJournalSink)typeof(ViveRecordingSinkPort).GetField("sink",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(f.Port);
                Check.Equal(0L,sink.CoverageSnapshot().ExplicitGapCount);Check.Equal(1L,sink.CoverageSnapshot().RecoveryUnknownCoverageCount);
                f.EndAndDrain(c);Check.True(c.CloseAfterSegment(),c.LastIssue);
                var rows=new List<ViveDevelopmentJournalRecord>();Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,f.Read(rows).Status);
                Check.Equal("initial_coverage_unknown",rows[1].Kind);Check.Equal("observation",rows[2].Kind);Check.Equal("recovery_coverage_unknown",rows[3].Kind);
                Check.Equal("4",rows[3].Fields["coverage_start_local_seconds"]);Check.Equal("7",rows[3].Fields["coverage_end_local_seconds"]);Check.Equal("Missing",rows[3].Fields["coverage_reason_detected"]);Check.Equal("observation",rows[4].Kind);
                var csv=ViveDevelopmentJournalExtraction.Extract(new MemoryStream(f.Factory.Handle.Memory.ToArray()),false,100,100000).Csv;
                Check.True(csv.Contains("recovery_coverage_unknown")&&csv.Contains("coverage_reason_detected"));
            });
            suite.Run("sink port abort never writes a close marker for pending or skipped observations",()=>{
                var f=new Fixture();var c=f.Started();c.Observe(f.Observe());c.Dispose();var rows=new List<ViveDevelopmentJournalRecord>();
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,f.Read(rows).Status);Check.Equal(1,f.Factory.Handle.Disposals);Check.Equal(1,rows.Count);
                f=new Fixture();c=f.Started();f.Observe();f.Clock.Now=5;f.Observe();Check.Throws<InvalidOperationException>(()=>c.Observe(f.Source.LastObservation));
                rows.Clear();Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,f.Read(rows).Status);Check.Equal(1,f.Factory.Handle.Disposals);
            });
            suite.Run("sink port preserves an unclean gap prefix when recovery observation write fails",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observe())),c.LastIssue);
                f.Clock.Now=5;f.Missing();Check.True(c.MarkUnavailable(ViveDevelopmentGapReason.Missing),c.LastIssue);
                f.Clock.Now=7;var pending=c.Observe(f.Observe());f.Factory.Handle.Memory.FailAt=f.Factory.Handle.Memory.Writes+2;
                Check.True(!c.Commit(pending));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);Check.True(!c.CloseAfterSegment());
                var rows=new List<ViveDevelopmentJournalRecord>();Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,f.Read(rows).Status);
                Check.Equal("initial_coverage_unknown",rows[1].Kind);Check.Equal("observation",rows[2].Kind);Check.Equal("recovery_coverage_unknown",rows[3].Kind);
                Check.Equal(4,rows.Count);Check.Equal(1,f.Factory.Handle.Disposals);
            });
            suite.Run("sink port independently rejects fabricated recovery reason and unusable recovery",()=>{
                foreach(bool unusable in new[]{false,true})
                {
                    var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observe())),c.LastIssue);
                    f.Clock.Now=5;f.Missing();Check.True(c.MarkUnavailable(ViveDevelopmentGapReason.Missing),c.LastIssue);
                    f.Clock.Now=7;f.Observe(!unusable);Check.True(f.Port.AcknowledgeSourceRead(f.Source.ReadAttemptGeneration,ViveParticipantReadStatus.Accepted));
                    Check.True(!f.Port.RecordGap(4,7,unusable?ViveDevelopmentGapReason.Missing:ViveDevelopmentGapReason.DeviceUnavailable));
                    c.Abort("synthetic_invalid_recovery");var rows=new List<ViveDevelopmentJournalRecord>();
                    Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,f.Read(rows).Status);Check.Equal(3,rows.Count);
                }
            });
            suite.Run("unknown initial coverage is v2-only and rejects invalid bounds",()=>{
                var p=new ViveDevelopmentProvenance("session","participant","operator",ViveDevelopmentMeasurementRole.Participant,"stream","device","clock","map",null,0,0,0,null,true);
                var legacy=new ViveDevelopmentJournal(new MemoryStream(),()=>{},p,ViveDevelopmentDataSource.SyntheticFixture,true,true);
                Check.Throws<InvalidOperationException>(()=>legacy.TryAppendInitialUnknownCoverage(0,0,1,2));
                var id=new ViveDevelopmentStreamIdentity("session","participant",null,ViveDevelopmentMeasurementRole.Participant,"stream","device","clock","map",ViveDevelopmentOperatorStatus.NotApplicable);
                var current=ViveDevelopmentJournal.CreateV2WithIdentity(new MemoryStream(),()=>{},id,ViveDevelopmentDataSource.SyntheticFixture,true,true);
                Check.Throws<ArgumentException>(()=>current.TryAppendInitialUnknownCoverage(0,0,2,1));
            });
            suite.Run("unknown coverage reader rejects v1, inverted and duplicated frames",()=>{
                var p=new ViveDevelopmentProvenance("session","participant","operator",ViveDevelopmentMeasurementRole.Participant,"stream","device","clock","map",null,0,0,0,null,true);
                var legacyBytes=new MemoryStream();var legacy=new ViveDevelopmentJournal(legacyBytes,()=>{},p,ViveDevelopmentDataSource.SyntheticFixture,true,true);
                var injected=AddUnknown(legacyBytes.ToArray(),1,"1","2");Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,ViveDevelopmentJournalReader.Read(new MemoryStream(injected)).Status);
                var id=new ViveDevelopmentStreamIdentity("session","participant",null,ViveDevelopmentMeasurementRole.Participant,"stream","device","clock","map",ViveDevelopmentOperatorStatus.NotApplicable);
                var mem=new MemoryStream();var current=ViveDevelopmentJournal.CreateV2WithIdentity(mem,()=>{},id,ViveDevelopmentDataSource.SyntheticFixture,true,true);
                var inverted=AddUnknown(mem.ToArray(),1,"2","1");Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,ViveDevelopmentJournalReader.Read(new MemoryStream(inverted)).Status);
                Check.True(current.TryAppendInitialUnknownCoverage(0,0,1,2));Check.Throws<InvalidOperationException>(()=>current.TryAppendInitialUnknownCoverage(0,0,1,2));
                var duplicate=AddUnknown(mem.ToArray(),2,"1","2");Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,ViveDevelopmentJournalReader.Read(new MemoryStream(duplicate)).Status);
                GC.KeepAlive(legacy);
            });
        }
    }
}
