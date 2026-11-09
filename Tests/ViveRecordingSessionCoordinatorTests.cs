using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // No file, device, network, Unity or hardware operations: both the source
    // and the not-yet-implemented persistence port are in-memory fakes.
    public static class ViveRecordingSessionCoordinatorTests
    {
        sealed class Clock:IClock {double now;public bool Throw;public double Now {get{if(Throw)throw new InvalidOperationException();return now;}set{now=value;}} public string ClockId {get{return "coord-clock";}}}
        sealed class Provider:IViveProEyeProvider
        {
            public ViveProEyePoll Next;
            public ViveProEyePoll Poll(){return Next;}
        }
        sealed class Port:IViveRecordingSegmentPort
        {
            public readonly IClock Clock;public readonly ViveParticipantEyeSource Source;public readonly List<string> Calls=new List<string>();
            public bool Dormant=true,FailOpen,FailObservation,ThrowClose;
            public bool FailGap {get;set;} public bool FailEvent {get;set;} public bool FailClose {get;set;} public bool FailAbort {get;set;}
            public bool FailUnknown {get;set;} public bool FailAck {get;set;} public int Acknowledgements;
            public Port(IClock clock,ViveParticipantEyeSource source){Clock=clock;Source=source;}
            public string SessionId {get{return "coord-session";}}
            public string StreamId {get{return "participant/vive/epoch";}}
            public string DeviceId {get{return "participant-device";}}
            public string MappingVersion {get{return ViveProEyeRehearsalCoordinates.MappingVersion;}}
            public string ClockId {get{return "coord-clock";}}
            public int BlockIndex {get{return 0;}} public int Segment {get{return 0;}}
            public bool IsDormant {get{return Dormant;}}
            public bool UsesClock(IClock clock){return Object.ReferenceEquals(clock,Clock);}
            public bool UsesSource(ViveParticipantEyeSource source){return Object.ReferenceEquals(source,Source);}
            public bool OpenSegmentAndBeginTaskEvents(long first,ViveDevelopmentTaskOrigin origin)
            {Calls.Add("open:"+first+":"+origin);Dormant=false;return !FailOpen;}
            public bool AcknowledgeSourceRead(long generation,ViveParticipantReadStatus status){Acknowledgements++;return !FailAck;}
            public bool RecordUnknownInitialCoverage(double start,double end){Calls.Add("unknown:"+start+":"+end);return !FailUnknown;}
            public bool RecordGap(double start,double end,ViveDevelopmentGapReason reason)
            {Calls.Add("gap:"+start+":"+end+":"+reason);return !FailGap;}
            public bool RecordObservation(ViveParticipantEyeObservation value){Calls.Add("observation");return !FailObservation;}
            public bool RecordAuthoritativeTaskEvent(JournalEvent value){Calls.Add("event:"+value.Sequence);return !FailEvent;}
            public bool CloseSegment(){Calls.Add("close");if(ThrowClose)throw new InvalidOperationException();return !FailClose;}
            public bool AbortUnclean(string reason){Calls.Add("abort:"+reason);return !FailAbort;}
        }
        sealed class Fixture
        {
            public readonly Clock Clock=new Clock{Now=1};public readonly Provider Provider=new Provider();
            public readonly ViveDevelopmentParticipant Task;public readonly Port Port;public readonly ViveParticipantEyeSource Source;
            public readonly long First;int sequence;
            public Fixture()
            {
                var allocation=new Allocation("fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var config=new SessionConfiguration("coord-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
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
                Source=new ViveParticipantEyeSource("coord-session",0,0,"participant/vive/epoch","participant-device",
                    "operator/vive/epoch","operator-device",Clock,Provider,new ViveProEyeRehearsalCoordinates());
                Port=new Port(Clock,Source);
                First=-1;foreach(var item in Task.ReadEventsAfter(-1,4096))if(item.BlockIndex==0&&item.Segment==0){First=item.Sequence;break;}
                Check.True(First>=0);
            }
            public ViveRecordingSessionCoordinator Create(bool eye=true,bool task=true,bool segment=true)
            {return new ViveRecordingSessionCoordinator(Task,Source,Port,Clock,eye,task,segment);}
            public ViveRecordingSessionCoordinator Started()
            {var coordinator=Create();Check.True(coordinator.BeginTaskEvents(First,true));return coordinator;}
            public ViveParticipantEyeObservation Observation(bool valid=true)
            {
                sequence++;var ray=new ViveEyeRay(2,true,valid,new Vec3(0,0,0),new Vec3(0,0,1));
                Provider.Next=new ViveProEyePoll(new ViveHeadFrame(sequence,Clock.Now,true,true,Rotation.Identity),
                    new ViveEyeFrame(sequence,sequence,Clock.Now,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                ViveParticipantEyeObservation value;Source.TryRead(out value);Check.True(value!=null);return value;
            }
            public void Missing()
            {Provider.Next=new ViveProEyePoll(null,null,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                ViveParticipantEyeObservation ignored;Check.True(!Source.TryRead(out ignored));Check.Equal(ViveParticipantReadStatus.Missing,Source.LastReadStatus);}
            public void Drain(ViveRecordingSessionCoordinator coordinator)
            {foreach(var item in Task.ReadEventsAfter(First-1,4096)){if(item.BlockIndex==0&&item.Segment==0)Check.True(coordinator.RecordTaskEvent(item),coordinator.LastIssue);}}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("coordinator requires all explicit choices and identical clock before opening",()=>{
                foreach(var flags in new[]{0,1,2,3,4,5,6}){var f=new Fixture();Check.Throws<InvalidOperationException>(()=>f.Create((flags&1)!=0,(flags&2)!=0,(flags&4)!=0));Check.Equal(0,f.Port.Calls.Count);}
                var mismatch=new Fixture();Check.Throws<ArgumentException>(()=>new ViveRecordingSessionCoordinator(mismatch.Task,mismatch.Source,new Port(new Clock{Now=4},mismatch.Source),mismatch.Clock,true,true,true));
                Check.Equal(0,mismatch.Port.Calls.Count);
                var otherClock=new Clock{Now=4};var otherSource=new ViveParticipantEyeSource("coord-session",0,0,"participant/vive/epoch","participant-device",
                    "operator/vive/epoch","operator-device",otherClock,new Provider(),new ViveProEyeRehearsalCoordinates());
                Check.Throws<ArgumentException>(()=>new ViveRecordingSessionCoordinator(mismatch.Task,otherSource,new Port(mismatch.Clock,otherSource),mismatch.Clock,true,true,true));
            });
            suite.Run("coordinator starts exactly at the first authoritative event",()=>{
                var f=new Fixture();var c=f.Create();Check.True(!c.BeginTaskEvents(f.First+1,true));Check.Equal(0,f.Port.Calls.Count);
                f=new Fixture();c=f.Started();Check.True(!c.BeginTaskEvents(f.First,true));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);
                Check.True(f.Port.Calls[0].StartsWith("open:"));Check.True(f.Port.Calls[1].StartsWith("abort:"));
            });
            suite.Run("coordinator rejects pending replacement and replay without writes",()=>{
                var f=new Fixture();var c=f.Started();var first=f.Observation();var token=c.Observe(first);
                Check.Throws<InvalidOperationException>(()=>c.Observe(first));Check.Equal(1,f.Port.Calls.Count);
                Check.True(c.Commit(token));Check.Throws<InvalidOperationException>(()=>c.Commit(token));Check.Equal(3,f.Port.Calls.Count);
                c.Dispose();Check.True(f.Port.Calls[3].StartsWith("abort:"));
            });
            suite.Run("coordinator cannot substitute a later source read for a pending observation",()=>{
                var f=new Fixture();var c=f.Started();var token=c.Observe(f.Observation());f.Clock.Now=5;f.Observation();
                Check.True(!c.Commit(token));Check.True(f.Port.Calls[1].StartsWith("abort:"));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);
            });
            suite.Run("coordinator rejects skipped accepted source observations",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Clock.Now=5;f.Observation();
                f.Clock.Now=6;var later=f.Observation();Check.Throws<InvalidOperationException>(()=>c.Observe(later));
                Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);Check.True(f.Port.Calls[3].StartsWith("abort:"));
            });
            suite.Run("coordinator requires unknown initial coverage to be explicitly recorded",()=>{
                var f=new Fixture();var c=f.Started();f.Port.FailUnknown=true;Check.True(!c.Commit(c.Observe(f.Observation())));
                Check.True(f.Port.Calls[1].StartsWith("unknown:"));Check.True(f.Port.Calls[2].StartsWith("abort:"));Check.Equal(3,f.Port.Calls.Count);
            });
            suite.Run("coordinator aborts unknown initial coverage rather than inventing a gap",()=>{
                var f=new Fixture();var c=f.Started();f.Missing();Check.True(!c.MarkUnavailable(ViveDevelopmentGapReason.Missing));
                Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);Check.True(f.Port.Calls[1].StartsWith("abort:"));
                Check.True(!c.CloseAfterSegment());
            });
            suite.Run("coordinator writes detected gap before recovery as separate operations",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Clock.Now=5;
                f.Missing();Check.True(c.MarkUnavailable(ViveDevelopmentGapReason.Missing));f.Clock.Now=6;Check.True(c.MarkUnavailable(ViveDevelopmentGapReason.Missing));
                f.Clock.Now=7;var token=c.Observe(f.Observation());Check.Equal(3,f.Port.Calls.Count);Check.True(c.Commit(token));
                Check.Equal("unknown:1:4",f.Port.Calls[1]);Check.Equal("gap:4:7:Missing",f.Port.Calls[3]);Check.Equal("observation",f.Port.Calls[4]);
                Check.True(!c.ContinuousCoverageProven&&!c.HardwareCaptureTimeVerified&&!c.ResearchExportAllowed);
            });
            suite.Run("coordinator preserves unclean prefix if recovery write fails after gap",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Clock.Now=5;
                f.Missing();Check.True(c.MarkUnavailable(ViveDevelopmentGapReason.Missing));f.Clock.Now=6;var token=c.Observe(f.Observation());f.Port.FailObservation=true;
                Check.True(!c.Commit(token));Check.Equal("gap:4:6:Missing",f.Port.Calls[3]);Check.Equal("observation",f.Port.Calls[4]);
                Check.True(f.Port.Calls[5].StartsWith("abort:"));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);Check.True(!c.CloseAfterSegment());
            });
            suite.Run("coordinator does not write recovery after failed gap",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Clock.Now=5;
                f.Missing();Check.True(c.MarkUnavailable(ViveDevelopmentGapReason.Missing));f.Clock.Now=6;var token=c.Observe(f.Observation());f.Port.FailGap=true;
                Check.True(!c.Commit(token));Check.True(f.Port.Calls[3].StartsWith("gap:"));Check.True(f.Port.Calls[4].StartsWith("abort:"));Check.Equal(5,f.Port.Calls.Count);
            });
            suite.Run("coordinator rejects changed pending observations and unusable gap recovery",()=>{
                var f=new Fixture();var c=f.Started();var observation=f.Observation();var token=c.Observe(observation);observation.RawPoll=null;
                Check.True(!c.Commit(token));Check.True(f.Port.Calls[1].StartsWith("abort:"));
                f=new Fixture();c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Clock.Now=5;
                f.Missing();Check.True(c.MarkUnavailable(ViveDevelopmentGapReason.Missing));f.Clock.Now=6;
                Check.Throws<InvalidOperationException>(()=>c.Observe(f.Observation(false)));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);
            });
            suite.Run("coordinator retains invalid raw observation but cannot close it cleanly",()=>{
                var f=new Fixture();var c=f.Started();Check.True(!c.Commit(c.Observe(f.Observation(false))));
                Check.Equal("observation",f.Port.Calls[2]);Check.True(f.Port.Calls[3].StartsWith("abort:"));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);
                f=new Fixture();c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Clock.Now=5;
                Check.True(!c.Commit(c.Observe(f.Observation(false))));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);Check.True(!c.CloseAfterSegment());
            });
            suite.Run("coordinator aborts open gap at segment end",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Clock.Now=5;
                f.Missing();Check.True(c.MarkUnavailable(ViveDevelopmentGapReason.Missing));f.Task.EndTechnical(Actor.Owner,"synthetic");
                Check.True(!c.CloseAfterSegment());Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);Check.True(f.Port.Calls[3].StartsWith("abort:"));
            });
            suite.Run("coordinator rejects visible final source failure before close",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));
                f.Provider.Next=null;ViveParticipantEyeObservation ignored;Check.True(!f.Source.TryRead(out ignored));
                f.Task.EndTechnical(Actor.Owner,"synthetic");Check.True(!c.CloseAfterSegment());
                Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);Check.True(f.Port.Calls[3].StartsWith("abort:"));
            });
            suite.Run("coordinator rejects concealed failed reads and accounts for duplicates",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));
                f.Provider.Next=null;ViveParticipantEyeObservation ignored;Check.True(!f.Source.TryRead(out ignored));
                f.Clock.Now=5;var next=f.Observation();Check.Throws<InvalidOperationException>(()=>c.Observe(next));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);
                f=new Fixture();c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));Check.True(!f.Source.TryRead(out ignored));
                Check.Equal(ViveParticipantReadStatus.Duplicate,f.Source.LastReadStatus);Check.True(c.AcknowledgeDuplicateRead());
                f.Clock.Now=5;Check.True(c.Commit(c.Observe(f.Observation())));Check.Equal(ViveRecordingCoordinatorState.Open,c.State);
            });
            suite.Run("coordinator consumes one unavailable read into a detected gap",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Clock.Now=5;
                f.Provider.Next=new ViveProEyePoll(null,null,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                ViveParticipantEyeObservation ignored;Check.True(!f.Source.TryRead(out ignored));Check.Equal(ViveParticipantReadStatus.Missing,f.Source.LastReadStatus);
                Check.True(c.MarkUnavailable(ViveDevelopmentGapReason.Missing));f.Clock.Now=6;Check.True(c.Commit(c.Observe(f.Observation())));
                Check.Equal("gap:4:6:Missing",f.Port.Calls[3]);Check.Equal(ViveRecordingCoordinatorState.Open,c.State);
            });
            suite.Run("coordinator cannot treat provider failure as a recoverable gap",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Clock.Now=5;f.Provider.Next=null;
                ViveParticipantEyeObservation ignored;Check.True(!f.Source.TryRead(out ignored));Check.Equal(ViveParticipantReadStatus.ProviderFailure,f.Source.LastReadStatus);
                Check.True(!c.MarkUnavailable(ViveDevelopmentGapReason.Missing));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);
                f.Clock.Now=6;f.Observation();Check.True(!c.CloseAfterSegment());Check.True(f.Port.Calls[3].StartsWith("abort:"));
            });
            suite.Run("coordinator rejects invented or contradictory gap reasons",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Clock.Now=5;
                Check.True(!c.MarkUnavailable(ViveDevelopmentGapReason.Missing));Check.Equal(ViveRecordingCoordinatorState.Open,c.State);
                f.Missing();Check.True(!c.MarkUnavailable(ViveDevelopmentGapReason.CalibrationUnavailable));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);
                f=new Fixture();c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Clock.Now=5;f.Missing();Check.True(c.MarkUnavailable(ViveDevelopmentGapReason.Missing));
                f.Provider.Next=new ViveProEyePoll(null,null,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,false,0);
                ViveParticipantEyeObservation ignored;Check.True(!f.Source.TryRead(out ignored));Check.Equal(ViveParticipantReadStatus.DeviceUnavailable,f.Source.LastReadStatus);
                Check.True(!c.MarkUnavailable(ViveDevelopmentGapReason.DeviceUnavailable));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);
            });
            suite.Run("coordinator aborts if the port cannot acknowledge an exact source attempt",()=>{
                var f=new Fixture();var c=f.Started();f.Port.FailAck=true;var observation=f.Observation();
                Check.Throws<InvalidOperationException>(()=>c.Observe(observation));Check.Equal(1,f.Port.Acknowledgements);
                Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);Check.True(f.Port.Calls[1].StartsWith("abort:"));
            });
            suite.Run("coordinator requires full ordered authoritative event drain before close",()=>{
                var f=new Fixture();var c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));Check.True(!c.CloseAfterSegment());
                f.Task.EndTechnical(Actor.Owner,"synthetic");Check.True(!c.CloseAfterSegment());f.Drain(c);Check.True(c.CloseAfterSegment());
                Check.Equal(ViveRecordingCoordinatorState.Closed,c.State);Check.True(!c.CompleteSessionProven&&!c.PowerLossDurabilityGuaranteed);c.Dispose();
                Check.Equal("close",f.Port.Calls[f.Port.Calls.Count-1]);
            });
            suite.Run("coordinator rejects forged or skipped task events",()=>{
                var f=new Fixture();var c=f.Started();var event0=f.Task.ReadEventsAfter(f.First-1,1)[0];
                var fields=new Dictionary<string,string>();foreach(var pair in event0.Fields)fields.Add(pair.Key,pair.Value);
                var forged=new JournalEvent(event0.Sequence,event0.Time,event0.ClockId,event0.SessionId,event0.BlockIndex,event0.Segment,event0.TrialId,"forged",fields);
                Check.True(!c.RecordTaskEvent(forged));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);Check.True(f.Port.Calls[1].StartsWith("abort:"));
                f=new Fixture();c=f.Started();var later=f.Task.ReadEventsAfter(f.First,1)[0];Check.True(!c.RecordTaskEvent(later));
                Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);
            });
            suite.Run("coordinator latches open and close uncertainty without retry",()=>{
                var f=new Fixture();f.Port.FailOpen=true;var c=f.Create();Check.True(!c.BeginTaskEvents(f.First,true));Check.Equal(2,f.Port.Calls.Count);c.Dispose();Check.Equal(2,f.Port.Calls.Count);
                f=new Fixture();c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Task.EndTechnical(Actor.Owner,"synthetic");f.Drain(c);
                f.Port.ThrowClose=true;Check.True(!c.CloseAfterSegment());Check.Equal(ViveRecordingCoordinatorState.CloseOutcomeUncertain,c.State);
                var calls=f.Port.Calls.Count;c.Dispose();Check.True(!c.CloseAfterSegment());Check.Equal(calls,f.Port.Calls.Count);
            });
            suite.Run("coordinator latches task append, abort and failed close without retries",()=>{
                var f=new Fixture();var c=f.Started();f.Port.FailEvent=true;f.Port.FailAbort=true;
                Check.True(!c.RecordTaskEvent(f.Task.ReadEventsAfter(f.First-1,1)[0]));Check.Equal(ViveRecordingCoordinatorState.Faulted,c.State);
                Check.True(c.AbortAttempted&&!c.AbortAcknowledged);var calls=f.Port.Calls.Count;c.Dispose();Check.Equal(calls,f.Port.Calls.Count);
                f=new Fixture();c=f.Started();Check.True(c.Commit(c.Observe(f.Observation())));f.Task.EndTechnical(Actor.Owner,"synthetic");f.Drain(c);
                f.Port.FailClose=true;Check.True(!c.CloseAfterSegment());Check.Equal(ViveRecordingCoordinatorState.Faulted,c.State);
                calls=f.Port.Calls.Count;c.Dispose();Check.Equal(calls,f.Port.Calls.Count);
            });
            suite.Run("coordinator enforces owner thread and shared clock rollback",()=>{
                var f=new Fixture();var c=f.Started();Exception caught=null;var thread=new Thread(()=>{try{c.MarkUnavailable(ViveDevelopmentGapReason.Stale);}catch(Exception e){caught=e;}});
                thread.Start();Check.True(thread.Join(3000));Check.True(caught is InvalidOperationException);Check.Equal(1,f.Port.Calls.Count);
                f.Clock.Now=0;Check.Throws<InvalidOperationException>(()=>c.MarkUnavailable(ViveDevelopmentGapReason.Stale));Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);
                f=new Fixture();c=f.Started();f.Clock.Throw=true;Check.Throws<InvalidOperationException>(()=>c.MarkUnavailable(ViveDevelopmentGapReason.Stale));
                f.Clock.Throw=false;Check.Equal(ViveRecordingCoordinatorState.Aborted,c.State);Check.True(!c.CloseAfterSegment());
            });
        }
        public static int Main(string[] args)
        {var suite=new TestSuite();Register(suite);Console.WriteLine("Coordinator: "+suite.Passed+" passed, "+suite.Failed+" failed");return suite.Failed==0?0:1;}
    }
}
