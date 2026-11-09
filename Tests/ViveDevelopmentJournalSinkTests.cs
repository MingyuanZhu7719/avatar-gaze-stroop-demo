using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Only synthetic in-memory handles are used. These tests never invoke the
    // physical factory, create a file, perform File.Move or access a device.
    public static class ViveDevelopmentJournalSinkTests
    {
        sealed class Clock:IClock {public double Now {get;set;} public string ClockId {get{return "sink-clock";}}}
        sealed class Provider:IViveProEyeProvider
        {
            public int Calls;
            public double Observed=1;
            public ViveProEyePoll Poll()
            {
                Calls++;var ray=new ViveEyeRay(2,true,true,new Vec3(0,0,0),new Vec3(0,0,1));
                return new ViveProEyePoll(new ViveHeadFrame(Calls,Observed,true,true,Rotation.Identity),
                    new ViveEyeFrame(Calls,Calls,Observed,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
            }
        }
        sealed class FaultStream:MemoryStream
        {
            public bool FailWrite;
            public override void Write(byte[] buffer,int offset,int count)
            {if(FailWrite)throw new IOException("synthetic write fault");base.Write(buffer,offset,count);}
        }
        sealed class Handle:IViveJournalSinkHandle
        {
            public readonly FaultStream Memory=new FaultStream();
            public int Flushes,Disposals,FailFlushAt;
            public bool FailDispose;
            public Stream Stream {get{return Memory;}}
            public void FlushToStorage(){Flushes++;if(Flushes==FailFlushAt)throw new IOException("synthetic flush fault");}
            public void Dispose(){Disposals++;if(FailDispose)throw new IOException("synthetic disposal fault");Memory.Dispose();}
        }
        sealed class Factory:IViveJournalSinkFactory
        {
            public int Opens;public FileMode Mode;public string Path;public bool FailOpen;public readonly Handle Handle=new Handle();
            public IViveJournalSinkHandle Open(string path,FileMode mode)
            {Opens++;Path=path;Mode=mode;if(FailOpen)throw new IOException("synthetic collision");return Handle;}
        }
        static string PathForTest()
        {return System.IO.Path.Combine(System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath()),"vive-synthetic","journal.vdev");}
        static ViveDevelopmentProvenance Identity()
        {return new ViveDevelopmentProvenance("sink-session","participant","operator",ViveDevelopmentMeasurementRole.Participant,
            "participant/vive/epoch","participant-device","sink-clock",ViveProEyeRehearsalCoordinates.MappingVersion,null,0,0,0,null,true);}
        static ViveParticipantEyeSource Source(Clock clock,Provider provider,int block=1,int segment=2)
        {return new ViveParticipantEyeSource("sink-session",block,segment,"participant/vive/epoch","participant-device",
            "operator/vive/epoch","operator-device",clock,provider,new ViveProEyeRehearsalCoordinates());}
        static ViveDevelopmentParticipant Task(Clock clock,string session="sink-session")
        {
            var allocation=new Allocation("fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var config=new SessionConfiguration(session,42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},
                {ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(config,clock,DevelopmentFixtures.Geometry(),map,.5,true);
        }
        static void StartTask(ViveDevelopmentParticipant task,Clock clock)
        {
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));task.OwnerBeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));task.MarkPresented(task.CurrentTrial.TrialId);
                var colour=task.CurrentTrial.Stimulus.Ink;
                double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0;
                double y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
            }
            task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"fixture-owner");task.OwnerBeginBlock(Actor.Owner);
            clock.Now+=3;task.Tick();Check.Equal(Phase.MainTask,task.Phase);Check.Equal(0,task.BlockIndex);Check.Equal(0,task.Segment);
        }
        static ViveDevelopmentJournalSink Open(Clock clock,Provider provider,Factory factory,bool eye=true,bool task=true,string path=null)
        {return ViveDevelopmentJournalSink.OpenWithFactory(path??PathForTest(),Source(clock,provider),clock,Identity(),
            ViveDevelopmentDataSource.SyntheticFixture,1,2,eye,task,factory);}
        static ViveDevelopmentJournalReadResult Read(Handle handle)
        {return ViveDevelopmentJournalReader.Read(new MemoryStream(handle.Memory.ToArray(),false));}
        static long FirstSegmentEvent(ViveDevelopmentParticipant task)
        {
            foreach(var item in task.ReadEventsAfter(-1,4096))if(item.BlockIndex==0&&item.Segment==0)return item.Sequence;
            throw new InvalidOperationException("No authoritative segment event");
        }
        static long DrainSegment(ViveDevelopmentJournalSink sink,ViveDevelopmentParticipant task,long after)
        {
            foreach(var item in task.ReadEventsAfter(after,4096))
            {
                if(item.BlockIndex!=0||item.Segment!=0)throw new InvalidOperationException("Unexpected event context in fixture");
                Check.True(sink.RecordAuthoritativeTaskEvent(item),sink.Error);after=item.Sequence;
            }
            return after;
        }

        public static void Register(TestSuite suite)
        {
            suite.Run("legacy physical VIVE sink rejects before validating path or creating a file",()=>{
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentJournalSink.OpenNewV2WithIdentity(
                    null,null,null,null,ViveDevelopmentDataSource.SyntheticFixture,0,0,true,true));
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentJournalSink.OpenNew(
                    null,null,null,null,ViveDevelopmentDataSource.RealVive,0,0,true,true));
            });
            suite.Run("VIVE sink requires both recording choices before opening or polling",()=>{
                foreach(var opt in new[]{Tuple.Create(false,false),Tuple.Create(false,true),Tuple.Create(true,false)})
                {
                    var clock=new Clock {Now=1};var provider=new Provider();var factory=new Factory();
                    Check.Throws<InvalidOperationException>(()=>Open(clock,provider,factory,opt.Item1,opt.Item2));
                    Check.Equal(0,factory.Opens);Check.Equal(0,provider.Calls);
                }
            });
            suite.Run("VIVE sink rejects noncanonical paths and mismatched source before opening",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var factory=new Factory();
                foreach(string path in new[]{"", "relative.vdev", ".\\relative.vdev", "C:relative.vdev", "\\root-relative.vdev"})
                    Check.Throws<ArgumentException>(()=>Open(clock,provider,factory,path:path));
                var otherClock=new Clock {Now=1};var source=Source(clock,provider);
                Check.Throws<ArgumentException>(()=>ViveDevelopmentJournalSink.OpenWithFactory(PathForTest(),source,otherClock,Identity(),
                    ViveDevelopmentDataSource.SyntheticFixture,1,2,true,true,factory));
                Check.Equal(0,factory.Opens);Check.Equal(0,provider.Calls);
            });
            suite.Run("VIVE sink creates once with CreateNew and closes after flush acknowledgement",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var factory=new Factory();var sink=Open(clock,provider,factory);
                Check.Equal(1,factory.Opens);Check.Equal(FileMode.CreateNew,factory.Mode);Check.Equal(PathForTest(),factory.Path);
                Check.Equal(1,factory.Handle.Flushes);Check.Equal(0,provider.Calls);Check.Equal(0L,sink.LastFlushAcknowledgedSequence);
                Check.Equal(ViveParticipantRecordingPoll.Recorded,sink.Poll());Check.True(sink.Flush());Check.Equal(1L,sink.LastFlushAcknowledgedSequence);
                Check.True(sink.Close());Check.True(sink.IsClosed&&sink.CloseAttempted&&sink.JournalCloseAcknowledged&&!sink.IsFaulted);
                Check.True(!sink.PowerLossDurabilityGuaranteed&&!sink.HardwareCaptureTimeVerified&&!sink.ApparatusMappingValidated);
                Check.Equal(1,factory.Handle.Disposals);Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(factory.Handle).Status);
                Check.True(sink.Close());sink.Dispose();Check.Equal(1,factory.Handle.Disposals);
            });
            suite.Run("VIVE sink collision is reported without retry or polling",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var factory=new Factory {FailOpen=true};
                Check.Throws<IOException>(()=>Open(clock,provider,factory));Check.Equal(1,factory.Opens);Check.Equal(FileMode.CreateNew,factory.Mode);
                Check.Equal(0,provider.Calls);Check.Equal(0,factory.Handle.Disposals);
            });
            suite.Run("VIVE sink preserves partial header and does not write a close marker on initial flush fault",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var factory=new Factory();factory.Handle.FailFlushAt=1;
                var sink=Open(clock,provider,factory);Check.True(sink.IsFaulted);Check.Equal(ViveParticipantRecordingPoll.Faulted,sink.Poll());
                Check.Equal(0,provider.Calls);long before=factory.Handle.Memory.Length;
                Check.True(!sink.Close());Check.True(sink.IsClosed&&!sink.JournalCloseAcknowledged);Check.Equal(before,factory.Handle.Memory.ToArray().Length);
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(factory.Handle).Status);Check.Equal(1,factory.Handle.Disposals);
            });
            suite.Run("VIVE sink latches append failure and retains the unclosed prefix",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var factory=new Factory();var sink=Open(clock,provider,factory);
                long header=factory.Handle.Memory.Length;factory.Handle.Memory.FailWrite=true;
                Check.Equal(ViveParticipantRecordingPoll.Faulted,sink.Poll());Check.True(sink.IsFaulted&&!sink.Flush());
                Check.True(!sink.Close());Check.Equal(header,factory.Handle.Memory.ToArray().Length);
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(factory.Handle).Status);Check.Equal(1,factory.Handle.Disposals);
            });
            suite.Run("VIVE sink does not call a failed final flush durable even if close marker is readable",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var factory=new Factory();factory.Handle.FailFlushAt=2;
                var sink=Open(clock,provider,factory);Check.Equal(ViveParticipantRecordingPoll.Recorded,sink.Poll());
                Check.True(!sink.Close());Check.True(sink.IsFaulted&&!sink.JournalCloseAcknowledged&&sink.IsClosed);
                Check.Equal(0L,sink.LastFlushAcknowledgedSequence);
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(factory.Handle).Status);
                Check.True(!sink.Close());Check.Equal(1,factory.Handle.Disposals);
            });
            suite.Run("VIVE sink reports uncertain disposal without claiming closed or retrying",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var factory=new Factory();factory.Handle.FailDispose=true;
                var sink=Open(clock,provider,factory);Check.True(!sink.Close());Check.True(sink.CloseAttempted&&!sink.IsClosed&&sink.IsFaulted);
                Check.True(sink.JournalCloseAcknowledged);Check.True(!sink.Close());Check.Equal(1,factory.Handle.Disposals);
                Check.True(sink.CoverageSnapshot().Faulted&&sink.CoverageSnapshot().CleanCloseAcknowledged);
            });
            suite.Run("VIVE sink requires its owner thread without consuming or closing the stream",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var factory=new Factory();var sink=Open(clock,provider,factory);
                Exception failure=null;var thread=new Thread(()=>{try{sink.Close();}catch(Exception error){failure=error;}});
                thread.Start();Check.True(thread.Join(3000));Check.True(failure is InvalidOperationException);Check.Equal(0,factory.Handle.Disposals);
                Check.True(sink.Close());Check.Equal(1,factory.Handle.Disposals);
            });
            suite.Run("VIVE sink forwards the authoritative task cursor and event without relabeling",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var factory=new Factory();var sink=Open(clock,provider,factory);
                Check.True(sink.BeginAuthoritativeEvents(8,true,ViveDevelopmentTaskOrigin.SimulationDevelopment));
                var fields=new Dictionary<string,string>{{"response","Green"},{"detail","synthetic"}};
                var taskEvent=new JournalEvent(8,1,"sink-clock","sink-session",1,2,"trial-1","first_response",fields);
                Check.True(sink.RecordAuthoritativeTaskEvent(taskEvent));Check.True(sink.Close());
                var rows=new System.Collections.Generic.List<ViveDevelopmentJournalRecord>();
                var result=ViveDevelopmentJournalReader.Read(new MemoryStream(factory.Handle.Memory.ToArray(),false),rows.Add);
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,result.Status);
                Check.Equal("AuthoritativeEvent",rows[1].Fields["event_kind"]);ViveAuthoritativeTaskPayload payload;
                Check.True(ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(rows[1].Fields["detail"],out payload));
                Check.Equal(8L,payload.Sequence);Check.Equal("first_response",payload.Kind);Check.Equal("Green",payload.Fields["response"]);
                Check.Equal(ViveDevelopmentTaskOrigin.SimulationDevelopment,payload.TaskOrigin);
                Check.True(!sink.RecordAuthoritativeTaskEvent(taskEvent));
            });
            suite.Run("VIVE real-eye sink refuses absent wrong or not-yet-open authoritative task before opening",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var factory=new Factory();var source=Source(clock,provider,0,0);
                Check.Throws<ArgumentException>(()=>ViveDevelopmentJournalSink.OpenWithFactory(PathForTest(),source,clock,Identity(),
                    ViveDevelopmentDataSource.RealVive,0,0,true,true,factory));
                var otherClock=new Clock {Now=1};var wrongClockTask=Task(otherClock);
                Check.Throws<ArgumentException>(()=>ViveDevelopmentJournalSink.OpenWithFactory(PathForTest(),source,clock,Identity(),
                    ViveDevelopmentDataSource.RealVive,0,0,true,true,factory,wrongClockTask));
                var wrongSession=Task(clock,"different-session");
                Check.Throws<ArgumentException>(()=>ViveDevelopmentJournalSink.OpenWithFactory(PathForTest(),source,clock,Identity(),
                    ViveDevelopmentDataSource.RealVive,0,0,true,true,factory,wrongSession));
                var task=Task(clock);
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentJournalSink.OpenWithFactory(PathForTest(),source,clock,Identity(),
                    ViveDevelopmentDataSource.RealVive,0,0,true,true,factory,task));
                Check.Equal(0,factory.Opens);Check.Equal(0,provider.Calls);
            });
            suite.Run("VIVE real-eye sink rejects clock rollback before creating the output",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var task=Task(clock);StartTask(task,clock);
                var factory=new Factory();clock.Now=.5; // segment boundary was observed at time 1
                Check.Throws<ArgumentException>(()=>ViveDevelopmentJournalSink.OpenWithFactory(PathForTest(),Source(clock,provider,0,0),clock,Identity(),
                    ViveDevelopmentDataSource.RealVive,0,0,true,true,factory,task));
                Check.Equal(0,factory.Opens);Check.Equal(0,provider.Calls);
            });
            suite.Run("VIVE real-eye sink passes bound task and preserves partial evidence on premature close",()=>{
                // Provider and stream are synthetic; this tests the RealVive label contract only.
                var clock=new Clock {Now=1};var provider=new Provider();var task=Task(clock);StartTask(task,clock);
                var factory=new Factory();var sink=ViveDevelopmentJournalSink.OpenWithFactory(PathForTest(),Source(clock,provider,0,0),clock,Identity(),
                    ViveDevelopmentDataSource.RealVive,0,0,true,true,factory,task);
                Check.Equal(1,factory.Opens);Check.Equal(0,provider.Calls);
                var coverage=sink.CoverageSnapshot();Check.Equal("ONE_BLOCK_SEGMENT_ONLY",coverage.Scope);
                Check.True(!coverage.CompleteSessionProven&&!coverage.CleanCloseAcknowledged&&!coverage.ResearchExportAllowed);
                Check.True(!sink.Close());Check.True(sink.IsFaulted&&!sink.JournalCloseAcknowledged&&sink.IsClosed);
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(factory.Handle).Status);Check.Equal(1,factory.Handle.Disposals);
            });
            suite.Run("VIVE real-eye sink reconciles actual task events and closes only after the segment ends",()=>{
                // Synthetic provider and memory handle exercise the real-source declaration contract only.
                var clock=new Clock {Now=1};var provider=new Provider();var task=Task(clock);StartTask(task,clock);provider.Observed=clock.Now;
                var factory=new Factory();var sink=ViveDevelopmentJournalSink.OpenWithFactory(PathForTest(),Source(clock,provider,0,0),clock,Identity(),
                    ViveDevelopmentDataSource.RealVive,0,0,true,true,factory,task);
                long first=FirstSegmentEvent(task);Check.True(sink.BeginAuthoritativeEvents(first,true,ViveDevelopmentTaskOrigin.SimulationDevelopment));
                long cursor=DrainSegment(sink,task,first-1);Check.Equal(ViveParticipantRecordingPoll.Recorded,sink.Poll());
                task.ParticipantWithdraw();Check.Equal(ViveParticipantRecordingPoll.ContextClosed,sink.Poll());Check.Equal(1,provider.Calls);
                cursor=DrainSegment(sink,task,cursor);Check.True(cursor>=first);
                Check.True(sink.Flush());Check.True(sink.Close());Check.True(sink.JournalCloseAcknowledged&&!sink.IsFaulted);
                var coverage=sink.CoverageSnapshot();Check.True(coverage.KnownSegmentEventsReconciledAtClose&&coverage.CleanCloseAcknowledged);
                Check.True(!coverage.CompleteSessionProven&&!coverage.OtherSegmentsCovered&&!coverage.PracticeCovered&&!coverage.ResearchExportAllowed);
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(factory.Handle).Status);
            });
            suite.Run("VIVE real-eye sink rejects forged authoritative task events without writing a close marker",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();var task=Task(clock);StartTask(task,clock);
                var factory=new Factory();var sink=ViveDevelopmentJournalSink.OpenWithFactory(PathForTest(),Source(clock,provider,0,0),clock,Identity(),
                    ViveDevelopmentDataSource.RealVive,0,0,true,true,factory,task);
                long first=FirstSegmentEvent(task);Check.True(sink.BeginAuthoritativeEvents(first,true,ViveDevelopmentTaskOrigin.SimulationDevelopment));
                var actual=task.ReadEventsAfter(first-1,1)[0];
                var forged=new JournalEvent(actual.Sequence,actual.Time,actual.ClockId,actual.SessionId,actual.BlockIndex,actual.Segment,
                    actual.TrialId,"forged-kind",new Dictionary<string,string>());
                Check.True(!sink.RecordAuthoritativeTaskEvent(forged));Check.True(sink.IsFaulted&&!sink.Close());
                Check.True(!sink.JournalCloseAcknowledged);Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(factory.Handle).Status);
            });
        }
    }
}
