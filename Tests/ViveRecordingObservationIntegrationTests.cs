#if STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using AvatarStroop.Unity;

namespace AvatarStroop.Tests
{
    // Simulation and in-memory sink only; never opens hardware, files or sockets.
    public static class ViveRecordingObservationIntegrationTests
    {
        sealed class Clock:IClock {public double Now {get;set;} public string ClockId {get{return "observe-recording-fixture";}}}
        sealed class Provider:IViveProEyeProvider
        {
            readonly Clock clock;public bool Missing,NoFresh;public int Polls;
            internal Provider(Clock clock){this.clock=clock;}
            public ViveProEyePoll Poll()
            {
                Polls++;if(Missing)return new ViveProEyePoll(null,null,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                var ray=new ViveEyeRay(2,true,true,new Vec3(0,0,0),new Vec3(0,0,1));
                return new ViveProEyePoll(new ViveHeadFrame(Polls,clock.Now,true,true,Rotation.Identity),
                    new ViveEyeFrame(Polls,Polls,clock.Now,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
            }
            internal bool Peek(out int frame,out int timestamp,out double observed)
            {frame=Polls+1;timestamp=Polls+1;observed=clock.Now;return !NoFresh;}
        }
        sealed class Handle:IViveJournalSinkHandle
        {
            internal readonly MemoryStream Bytes=new MemoryStream();
            public Stream Stream {get{return Bytes;}} public void FlushToStorage(){} public void Dispose(){Bytes.Dispose();}
        }
        sealed class Factory:IViveJournalSinkFactory
        {
            internal readonly List<Handle> Handles=new List<Handle>();
            public IViveJournalSinkHandle Open(string path,FileMode mode)
            {Check.Equal(FileMode.CreateNew,mode);var handle=new Handle();Handles.Add(handle);return handle;}
        }
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock{Now=1};internal readonly Provider Provider;internal readonly Factory Factory=new Factory();
            internal readonly ViveDevelopmentParticipant Task;
            internal Fixture(int traceLimit)
            {
                Provider=new Provider(Clock);string session="vive-development-"+Guid.NewGuid().ToString("N");
                var allocation=new Allocation("fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var config=new SessionConfiguration(session,42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,DevelopmentFixtures.Geometry(),map,.5,true,
                    new ExperimentIncrementalOptions(4096,128,16L*1024*1024),traceLimit);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Task.OwnerBeginPractice(Actor.Owner);
                for(int i=0;i<8;i++)
                {
                    var trial=Task.CurrentTrial;Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Task.MarkPresented(trial.TrialId);
                    Colour colour=trial.Stimulus.Ink;double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0,y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
                    Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                }
                Task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"owner");Clock.Now=2;
                Check.True(Task.ObservationEnabled&&!Task.ObservationFaulted&&!Task.ObservationPending);
            }
            internal int TraceCount()
            {
                var field=typeof(ViveDevelopmentParticipant).GetField("observation",BindingFlags.NonPublic|BindingFlags.Instance);
                Check.True(field!=null);var pump=(ViveSyntheticAggregatePump)field.GetValue(Task);ViveSyntheticAggregateHead head;
                Check.True(pump.TryGet(out head));return head.StepTrace.Count;
            }
            internal ViveStroopRecordingController Create()
            {
                string directory=Path.GetPathRoot(Path.GetTempPath());
                var controller=new ViveStroopRecordingController(Task,Provider,Clock,directory,true,true,_=>true,()=>true,Provider.Peek,
                    (path,source,clock,id,t)=>new ViveRecordingSinkPort(path,source,clock,id,t,ViveDevelopmentDataSource.RealVive,true,true,Factory));
                Clock.Now+=.1;return controller;
            }
        }
        static List<ViveAuthoritativeTaskPayload> Records(Handle handle,out ViveDevelopmentJournalReadResult result)
        {
            var payloads=new List<ViveAuthoritativeTaskPayload>();
            result=ViveDevelopmentJournalReader.Read(new MemoryStream(handle.Bytes.ToArray()),record=>{
                if(record.Kind!="task_event"||record.Fields["event_kind"]!="AuthoritativeEvent")return;
                ViveAuthoritativeTaskPayload payload;Check.True(ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(record.Fields["detail"],out payload));
                payloads.Add(payload);
            });
            return payloads;
        }
        static void CheckPrefix(ViveDevelopmentParticipant task,List<ViveAuthoritativeTaskPayload> payloads)
        {
            Check.True(payloads.Count>0);var source=task.ReadEventsAfter(-1,4096);
            for(int i=0;i<payloads.Count;i++)
            {
                if(i>0)Check.Equal(payloads[i-1].Sequence+1,payloads[i].Sequence,"Recorded authoritative sequence must be contiguous");
                Check.True(payloads[i].Sequence>=0&&payloads[i].Sequence<source.Count);
                var e=source[(int)payloads[i].Sequence];Check.Equal(e.Sequence,payloads[i].Sequence);Check.Equal(e.Kind,payloads[i].Kind);
                Check.Equal(e.Fields.Count,payloads[i].Fields.Count);
                foreach(var item in e.Fields){string actual;Check.True(payloads[i].Fields.TryGetValue(item.Key,out actual));Check.Equal(item.Value,actual);}
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("observed recording eye loss pauses task and preserves the ordered authoritative prefix",()=>{
                var f=new Fixture(4096);var c=f.Create();Check.True(c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)),c.Status);
                f.Clock.Now=3;f.Provider.Missing=true;Check.True(!c.PollEye());
                Check.True(c.RecoveryPending&&!c.Faulted);Check.Equal(Phase.Paused,f.Task.Phase);
                Check.True(!f.Task.ObservationFaulted&&!f.Task.ObservationPending);
                c.Dispose();Check.Equal(1,f.Factory.Handles.Count);ViveDevelopmentJournalReadResult result;
                var payloads=Records(f.Factory.Handles[0],out result);Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,result.Status);
                CheckPrefix(f.Task,payloads);Check.True(payloads.Exists(p=>p.Kind=="phase_changed"&&p.Fields.ContainsKey("to")&&p.Fields["to"]=="Paused"));
                Check.True(!c.FullSessionCoverageProven&&!c.ResearchAdmission);
            });
            suite.Run("observed recording refresh pauses an unready countdown without claiming a recorded segment",()=>{
                var f=new Fixture(4096);f.Provider.NoFresh=true;var c=f.Create();
                // Simulate an independently advanced task; Refresh must not conceal
                // that no recording segment was open for its initial events.
                f.Task.OwnerBeginBlock(Actor.Owner);Check.Equal(Phase.Countdown,f.Task.Phase);
                Check.True(!c.Refresh());Check.Equal(Phase.Paused,f.Task.Phase);Check.True(c.WaitingForFreshEye);
                Check.True(!f.Task.ObservationFaulted&&!f.Task.ObservationPending);Check.Equal(0,f.Factory.Handles.Count);
                c.Dispose();Check.True(c.Faulted&&c.UnrecordedTransition);
            });
            suite.Run("observed recording completes event drain after applied action faults observation",()=>{
                var probe=new Fixture(4096);int existing=probe.TraceCount();Check.True(existing>0&&existing<4095);
                var f=new Fixture(existing+1);var c=f.Create();int calls=0;
                Check.True(c.RunTaskMutation(()=>{calls++;f.Task.OwnerBeginBlock(Actor.Owner);}),c.Status);
                Check.Equal(1,calls);Check.Equal(Phase.Countdown,f.Task.Phase);Check.True(f.Task.ObservationFaulted);
                c.Pause();Check.Equal(Phase.Paused,f.Task.Phase);Check.Equal(1,calls);Check.True(!c.Faulted);
                c.Dispose();Check.Equal(1,f.Factory.Handles.Count);ViveDevelopmentJournalReadResult result;
                var payloads=Records(f.Factory.Handles[0],out result);Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,result.Status);
                CheckPrefix(f.Task,payloads);
                Check.True(payloads.Exists(p=>p.Kind=="phase_changed"&&p.Fields.ContainsKey("to")&&p.Fields["to"]=="Countdown"));
                Check.True(payloads.Exists(p=>p.Kind=="phase_changed"&&p.Fields.ContainsKey("to")&&p.Fields["to"]=="Paused"));
            });
        }
    }
}
#endif
