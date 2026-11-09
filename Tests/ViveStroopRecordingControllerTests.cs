#if STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.IO;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using AvatarStroop.Unity;

namespace AvatarStroop.Tests
{
    // Synthetic task, provider and in-memory sink only: no headset, file, move,
    // socket or real capture is used by any test in this fixture.
    public static class ViveStroopRecordingControllerTests
    {
        sealed class Clock:IClock {public double Now {get;set;} public string ClockId {get{return "host-clock";}}}
        sealed class Provider:IViveProEyeProvider
        {
            public readonly Clock Clock;public int Polls;public bool Missing,NoFreshSnapshot,RepeatEye,HeadUnavailable;
            ViveEyeFrame previousEye;
            public Provider(Clock clock){Clock=clock;}
            public ViveProEyePoll Poll()
            {
                Polls++;if(Missing)return new ViveProEyePoll(null,null,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                var ray=new ViveEyeRay(2,true,true,new Vec3(0,0,0),new Vec3(0,0,1));
                if(!RepeatEye||previousEye==null)previousEye=new ViveEyeFrame(Polls,Polls,Clock.Now,false,null,null,ray);
                return new ViveProEyePoll(new ViveHeadFrame(Polls,Clock.Now,true,!HeadUnavailable,Rotation.Identity),
                    previousEye,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
            }
            public bool Peek(out int frame,out int timestamp,out double observed)
            {frame=Polls+1;timestamp=Polls+1;observed=Clock.Now;return !NoFreshSnapshot;}
        }
        sealed class CountingStream:MemoryStream
        {
            public int Writes;
            public override void Write(byte[] buffer,int offset,int count){Writes++;base.Write(buffer,offset,count);}
        }
        sealed class Handle:IViveJournalSinkHandle
        {
            public readonly CountingStream Memory=new CountingStream();public int Flushes,Disposals;public bool FailDispose;
            public Stream Stream {get{return Memory;}}
            public void FlushToStorage(){Flushes++;}
            public void Dispose(){Disposals++;if(FailDispose)throw new IOException("synthetic dispose failure");Memory.Dispose();}
        }
        sealed class Factory:IViveJournalSinkFactory
        {
            public readonly List<Handle> Handles=new List<Handle>();public readonly List<string> Paths=new List<string>();
            public bool FailOpen;
            public IViveJournalSinkHandle Open(string path,FileMode mode)
            {Check.Equal(FileMode.CreateNew,mode);Paths.Add(path);if(FailOpen)throw new IOException("synthetic");var h=new Handle();Handles.Add(h);return h;}
        }
        sealed class Fixture
        {
            public readonly Clock Clock=new Clock {Now=1};public readonly Provider Provider;public readonly ViveDevelopmentParticipant Task;public readonly Factory Factory=new Factory();public bool Ready=true;
            public readonly string Directory=Path.GetPathRoot(Path.GetTempPath());
            public Fixture()
            {
                Provider=new Provider(Clock);string session="vive-development-"+Guid.NewGuid().ToString("N");
                var allocation=new Allocation("fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var config=new SessionConfiguration(session,42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,DevelopmentFixtures.Geometry(),map,.5,true);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Task.OwnerBeginPractice(Actor.Owner);
                for(int i=0;i<8;i++)
                {
                    var trial=Task.CurrentTrial;Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Task.MarkPresented(trial.TrialId);
                    var colour=trial.Stimulus.Ink;double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0,y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
                    Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                }
                Task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"owner");Clock.Now=2;
            }
            public ViveStroopRecordingController Create(bool eye=true,bool task=true,bool advance=true)
            {var result=new ViveStroopRecordingController(Task,Provider,Clock,Directory,eye,task,_=>true,()=>Ready,Provider.Peek,
                (path,source,clock,id,t)=>new ViveRecordingSinkPort(path,source,clock,id,t,ViveDevelopmentDataSource.RealVive,true,true,Factory));if(advance)Clock.Now+=.1;return result;}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("legacy live recording rejects before inspecting callbacks or destination",()=>{
                bool called=false;
                Check.True(!ViveStroopRecordingController.LegacyLiveRecordingAllowed);
                Check.Throws<InvalidOperationException>(()=>ViveStroopRecordingController.Enable(null,null,null,null,true,true,
                    ()=>{called=true;return true;},(out int frame,out int stamp,out double observed)=>{called=true;frame=0;stamp=0;observed=0;return true;}));
                Check.True(!called);
            });
            suite.Run("rig eye pump borrows one snapshot only during its owner frame",()=>{
                var clock=new Clock{Now=2};var provider=new Provider(clock);var pump=new ViveStroopEyePump(provider);
                int sequence,timestamp;double observed;
                Check.True(pump.Poll()==null&&!pump.Peek(out sequence,out timestamp,out observed));
                pump.BeginFrame();var first=pump.Poll();Check.True(first!=null&&pump.HeadReady&&Object.ReferenceEquals(first,pump.Poll()));
                Check.True(pump.Peek(out sequence,out timestamp,out observed));Check.Equal(first.Eye.FrameSequence,sequence);
                Check.Equal(first.Eye.Timestamp,timestamp);Check.Equal(first.Eye.ObservedLocalSeconds,observed);
                Check.Equal(1,provider.Polls);Check.Throws<InvalidOperationException>(()=>pump.BeginFrame());
                pump.EndFrame();Check.True(pump.Poll()==null&&!pump.HeadReady&&!pump.Peek(out sequence,out timestamp,out observed));
                bool rejected=false;var thread=new System.Threading.Thread(()=>{try{pump.BeginFrame();}catch(InvalidOperationException){rejected=true;}});
                thread.Start();thread.Join();Check.True(rejected);Check.Equal(1,provider.Polls);
                clock.Now=3;provider.HeadUnavailable=true;pump.BeginFrame();Check.Equal(2,provider.Polls);Check.True(!pump.HeadReady&&!Object.ReferenceEquals(first,pump.Poll()));
                pump.EndFrame();provider.Missing=true;pump.BeginFrame();Check.True(!pump.HeadReady&&pump.Poll().Head==null);
                pump.Close();Check.True(pump.Poll()==null);Check.Throws<InvalidOperationException>(()=>pump.BeginFrame());
            });
            suite.Run("recording waits outside a rig frame and uses its single snapshot for boundary and write",()=>{
                var f=new Fixture();var pump=new ViveStroopEyePump(f.Provider);
                var c=new ViveStroopRecordingController(f.Task,pump,f.Clock,f.Directory,true,true,_=>true,()=>f.Ready,pump.Peek,
                    (path,source,clock,id,t)=>new ViveRecordingSinkPort(path,source,clock,id,t,ViveDevelopmentDataSource.RealVive,true,true,f.Factory));
                f.Clock.Now+=.1;bool called=false;
                Check.True(!c.RunTaskMutation(()=>{called=true;f.Task.OwnerBeginBlock(Actor.Owner);}));
                Check.True(!called&&c.WaitingForFreshEye);Check.Equal(0,f.Provider.Polls);
                pump.BeginFrame();Check.True(c.Refresh(),c.Status);Check.Equal(1,f.Provider.Polls);
                Check.True(c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)),c.Status);
                Check.True(c.LastEyeUsable);Check.Equal(1,f.Provider.Polls);Check.Equal(Phase.Countdown,f.Task.Phase);
                pump.EndFrame();c.Dispose();pump.Close();
            });
            suite.Run("duplicate eye with lost head pauses participant without another worker read",()=>{
                var f=new Fixture();var pump=new ViveStroopEyePump(f.Provider);
                var c=new ViveStroopRecordingController(f.Task,pump,f.Clock,f.Directory,true,true,_=>true,()=>f.Ready,pump.Peek,
                    (path,source,clock,id,t)=>new ViveRecordingSinkPort(path,source,clock,id,t,ViveDevelopmentDataSource.RealVive,true,true,f.Factory));
                f.Clock.Now+=.1;pump.BeginFrame();Check.True(c.Refresh(),c.Status);
                Check.True(c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)),c.Status);pump.EndFrame();
                f.Clock.Now+=.01;f.Provider.RepeatEye=true;f.Provider.HeadUnavailable=true;pump.BeginFrame();
                Check.True(c.PollEye());Check.True(c.RunTaskMutation(()=>f.Task.UpdateTrackpad(false,new VivePreviewTrackpadSample(false,false,0,0))),c.Status);
                Check.Equal(Phase.Paused,f.Task.Phase);Check.Equal(2,f.Provider.Polls);pump.EndFrame();c.Dispose();pump.Close();
            });
#if STROOP_UI_FIXTURE
            suite.Run("neutral wait allows a new menu safety edge while another control stays held",()=>{
                var ordinary=new ViveStroopNeutralGate();var safety=new ViveStroopSafetyMenuGate();
                Check.True(!ordinary.Observe(true,false,false,false,false,false).NeutralPending);
                var boundary=ordinary.Observe(true,true,false,true,false,false);Check.True(boundary.NeutralPending);
                Check.True(!safety.Observe(true,false,false));
                var held=ordinary.Observe(true,false,false,true,false,true);Check.True(held.NeutralPending&&!held.MenuDown);
                Check.True(safety.Observe(true,false,true));Check.True(!safety.Observe(true,false,true));
                Check.True(!safety.Observe(false,false,true));Check.True(!safety.Observe(true,false,true));
                Check.True(!safety.Observe(true,false,false));Check.True(safety.Observe(true,false,true));
                Check.True(!safety.Observe(true,true,true));Check.True(!safety.Observe(true,false,true));
                Check.True(!safety.Observe(true,false,false));Check.True(safety.Observe(true,false,true));
                // A new menu press on the very first neutral-wait frame must
                // not be mistaken for a button held across the boundary.
                ordinary=new ViveStroopNeutralGate();safety=new ViveStroopSafetyMenuGate();
                Check.True(!ordinary.Observe(true,false,false,false,false,false).NeutralPending);
                Check.True(!safety.Observe(true,false,false));
                Check.True(ordinary.Observe(true,true,false,true,false,true).NeutralPending);
                Check.True(safety.Observe(true,false,true));Check.True(!safety.Observe(true,false,true));
                // A menu already held before the boundary is not a new edge.
                ordinary=new ViveStroopNeutralGate();safety=new ViveStroopSafetyMenuGate();
                ordinary.Observe(true,false,false,false,false,false);safety.Observe(true,false,false);
                Check.True(safety.Observe(true,false,true));
                Check.True(ordinary.Observe(true,true,false,true,false,true).NeutralPending);
                Check.True(!safety.Observe(true,false,true));
            });
            suite.Run("legacy recording controls stay closed and constrain the scroll viewport",()=>{
                for(int mask=0;mask<16;mask++)
                {bool eye=(mask&1)!=0,task=(mask&2)!=0,worker=(mask&4)!=0,terminal=(mask&8)!=0;
                    Check.True(!ViveStroopDevelopmentBootstrap.CanEnableRecording(eye,task,worker,terminal));}
                Check.True(!ViveStroopDevelopmentBootstrap.CanAcknowledgeInputFault(true,true,Phase.Paused));
                Check.True(!ViveStroopDevelopmentBootstrap.CanAcknowledgeInputFault(true,false,Phase.MainTask));
                Check.True(ViveStroopDevelopmentBootstrap.CanAcknowledgeInputFault(true,false,Phase.Paused));
                foreach(var size in new[]{new UnityEngine.Vector2(640,480),new UnityEngine.Vector2(1024,768),new UnityEngine.Vector2(1920,1080)})
                {var rect=ViveStroopPreviewRig.OwnerPanelBounds(size.x,size.y);
                    Check.True(rect.x>=0&&rect.y>=0&&rect.xMax<=size.x&&rect.yMax<=size.y&&rect.width>0&&rect.height>0);}
            });
#endif
            suite.Run("recording host requires both choices before storage and capture",()=>{
                var f=new Fixture();Check.Throws<InvalidOperationException>(()=>f.Create(false,true));Check.Throws<InvalidOperationException>(()=>f.Create(true,false));
                Check.Equal(0,f.Provider.Polls);Check.Equal(0,f.Factory.Paths.Count);
            });
            suite.Run("recording host refuses opt-in after a block countdown has started",()=>{
                var f=new Fixture();f.Task.OwnerBeginBlock(Actor.Owner);Check.Throws<InvalidOperationException>(()=>f.Create());
                Check.Equal(0,f.Provider.Polls);Check.Equal(0,f.Factory.Paths.Count);
            });
            suite.Run("recording host opens and samples before countdown with isolated v2 identity",()=>{
                var f=new Fixture();var c=f.Create();Check.True(c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)),c.Status);
                Check.Equal(Phase.Countdown,f.Task.Phase);Check.Equal(1,f.Provider.Polls);Check.Equal(1,f.Factory.Paths.Count);
                Check.True(c.SegmentOpen&&c.LastEyeUsable&&!c.ResearchAdmission&&!c.FullSessionCoverageProven);
                c.Dispose();var result=ViveDevelopmentJournalReader.Read(new MemoryStream(f.Factory.Handles[0].Memory.ToArray()));
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,result.Status);Check.Equal(Phase.Paused,f.Task.Phase);
            });
            suite.Run("recording host pauses before countdown on a failed create",()=>{
                var f=new Fixture();f.Factory.FailOpen=true;var c=f.Create();Check.True(!c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)));
                Check.True(c.Faulted);Check.Equal(Phase.Paused,f.Task.Phase);Check.Equal(0,f.Provider.Polls);Check.Equal(1,f.Factory.Paths.Count);
            });
            suite.Run("recording host waits for fresh snapshot before it permits a countdown",()=>{
                var f=new Fixture();f.Provider.NoFreshSnapshot=true;var c=f.Create();bool called=false;
                Check.True(!c.RunTaskMutation(()=>{called=true;f.Task.OwnerBeginBlock(Actor.Owner);}));
                Check.True(!called&&c.WaitingForFreshEye&&!c.Faulted);Check.Equal(Phase.BlockReady,f.Task.Phase);Check.Equal(0,f.Provider.Polls);Check.Equal(0,f.Factory.Paths.Count);
                f.Provider.NoFreshSnapshot=false;Check.True(c.Refresh(),c.Status);Check.True(c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)),c.Status);
                Check.Equal(1,f.Provider.Polls);Check.Equal(Phase.Countdown,f.Task.Phase);c.Dispose();
            });
            suite.Run("recording host never commits a frame from before the recording choice",()=>{
                var f=new Fixture();var c=f.Create(advance:false);Check.True(!c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)));
                Check.True(c.WaitingForFreshEye&&!c.Faulted);Check.Equal(0,f.Provider.Polls);f.Clock.Now+=.1;
                Check.True(c.Refresh(),c.Status);Check.Equal(1,f.Provider.Polls);c.Dispose();
            });
            suite.Run("recording host reports if clock failure prevents confirming a task stop",()=>{
                var f=new Fixture();var c=f.Create();f.Clock.Now=0;Check.True(!c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)));
                Check.True(c.Faulted&&c.Status.Contains("could not be confirmed"));Check.Equal(0,f.Provider.Polls);
            });
            suite.Run("recording host marks an unfinished waiting transition on withdrawal or stop",()=>{
                var f=new Fixture();f.Provider.NoFreshSnapshot=true;var c=f.Create();
                Check.True(!c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)));c.Withdraw();
                Check.Equal(Phase.Ended,f.Task.Phase);Check.True(c.Faulted&&c.UnrecordedTransition&&c.Status.Contains("No active segment"));Check.Equal(0,f.Factory.Paths.Count);
                f=new Fixture();f.Provider.NoFreshSnapshot=true;c=f.Create();Check.True(!c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)));c.Dispose();
                Check.True(c.Faulted&&c.UnrecordedTransition);Check.Equal(Phase.Paused,f.Task.Phase);
                f=new Fixture();f.Provider.NoFreshSnapshot=true;c=f.Create();Check.True(!c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)));
                f.Ready=false;Check.True(!c.Refresh());Check.True(c.Faulted&&c.UnrecordedTransition);
            });
            suite.Run("withdraw preserves the prefix without draining events closing marker or explicit flush",()=>{
                var f=new Fixture();var c=f.Create();Check.True(c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)),c.Status);
                var h=f.Factory.Handles[0];var before=h.Memory.ToArray();int writes=h.Memory.Writes,flushes=h.Flushes;
                c.Withdraw();Check.Equal(Phase.Ended,f.Task.Phase);Check.Equal(EndReason.Withdrawn,f.Task.EndReason);
                Check.True(c.Faulted&&c.UnrecordedTransition&&c.Status.Contains("unclean prefix")&&c.Status.Contains("not written"));
                Check.Equal(writes,h.Memory.Writes);Check.Equal(flushes,h.Flushes);Check.Equal(1,h.Disposals);
                var after=h.Memory.ToArray();Check.Equal(before.Length,after.Length);
                for(int i=0;i<before.Length;i++)Check.Equal(before[i],after[i]);
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,ViveDevelopmentJournalReader.Read(new MemoryStream(after)).Status);
                Check.True(!c.PollEye()&&!c.RunTaskMutation(()=>{}));c.Withdraw();c.Dispose();Check.Equal(1,h.Disposals);
                Check.Equal(writes,h.Memory.Writes);Check.Equal(flushes,h.Flushes);
            });
            suite.Run("withdraw during open gap retains prefix and no explicit flush",()=>{
                var f=new Fixture();var c=f.Create();Check.True(c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)),c.Status);
                f.Clock.Now=3;f.Provider.Missing=true;Check.True(!c.PollEye()&&c.RecoveryPending);
                var h=f.Factory.Handles[0];var before=h.Memory.ToArray();int writes=h.Memory.Writes,flushes=h.Flushes;
                c.Withdraw();Check.Equal(Phase.Ended,f.Task.Phase);Check.Equal(writes,h.Memory.Writes);Check.Equal(flushes,h.Flushes);
                Check.Equal(before.Length,h.Memory.ToArray().Length);Check.Equal(1,h.Disposals);
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,ViveDevelopmentJournalReader.Read(new MemoryStream(h.Memory.ToArray())).Status);
            });
            suite.Run("withdraw reports uncertain disposal and does not retry",()=>{
                var f=new Fixture();var c=f.Create();Check.True(c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)),c.Status);
                var h=f.Factory.Handles[0];h.FailDispose=true;int writes=h.Memory.Writes,flushes=h.Flushes;
                c.Withdraw();Check.True(c.Status.Contains("cleanup is not confirmed"));Check.Equal(1,h.Disposals);
                c.Withdraw();c.Dispose();Check.Equal(1,h.Disposals);Check.Equal(writes,h.Memory.Writes);Check.Equal(flushes,h.Flushes);
            });
            suite.Run("recording host does not hide an unconfirmed stop on clock failure during disposal",()=>{
                var f=new Fixture();var c=f.Create();Check.True(c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)),c.Status);
                f.Clock.Now=6;Check.True(c.RunTaskMutation(()=>f.Task.Tick()),c.Status);Check.Equal(Phase.MainTask,f.Task.Phase);
                f.Clock.Now=0;c.Dispose();Check.True(c.Faulted&&c.Status.Contains("could not be confirmed"));Check.Equal(Phase.MainTask,f.Task.Phase);
            });
            suite.Run("recording host retains explicit interruption before explicit review",()=>{
                var f=new Fixture();var c=f.Create();Check.True(c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)),c.Status);
                f.Clock.Now=3;f.Provider.Missing=true;Check.True(!c.PollEye());Check.True(c.RecoveryPending&&!c.Faulted);Check.Equal(Phase.Paused,f.Task.Phase);
                f.Clock.Now=4;f.Provider.Missing=false;Check.True(c.PollEye(),c.Status);Check.True(c.RecoveryPending);Check.True(c.AcknowledgeRecoveredSource());
                Check.Equal(Phase.Paused,f.Task.Phase);c.Dispose();
            });
            suite.Run("recording host closes old segment and pauses resumed countdown until a fresh frame",()=>{
                var f=new Fixture();var c=f.Create();Check.True(c.RunTaskMutation(()=>f.Task.OwnerBeginBlock(Actor.Owner)),c.Status);
                f.Clock.Now=6;Check.True(c.RunTaskMutation(()=>f.Task.Tick()),c.Status);Check.Equal(Phase.MainTask,f.Task.Phase);
                Check.True(c.RunTaskMutation(()=>f.Task.MarkPresented(f.Task.CurrentTrial.TrialId)),c.Status);c.Pause();Check.Equal(Phase.Paused,f.Task.Phase);
                f.Clock.Now=7;Check.True(c.RunTaskMutation(()=>f.Task.UpdateAuxiliary(false,false,false,0,true)),c.Status);
                Check.True(!c.RunTaskMutation(()=>f.Task.UpdateAuxiliary(true,false,false,0,true)));
                Check.Equal(1,f.Task.Segment);Check.Equal(Phase.Paused,f.Task.Phase);Check.True(c.WaitingForFreshEye&&!c.Faulted);
                Check.Equal(1,f.Factory.Paths.Count);var old=ViveDevelopmentJournalReader.Read(new MemoryStream(f.Factory.Handles[0].Memory.ToArray()));
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,old.Status);
                f.Clock.Now=7.1;Check.True(c.Refresh(),c.Status);Check.Equal(2,f.Factory.Paths.Count);
                Check.True(f.Factory.Paths[0]!=f.Factory.Paths[1]);Check.Equal(Phase.Paused,f.Task.Phase);Check.Equal(1,f.Task.Segment);c.Dispose();
            });
        }
        public static int Main(string[] args)
        {var suite=new TestSuite();Register(suite);Console.WriteLine("Recording host: "+suite.Passed+" passed, "+suite.Failed+" failed");return suite.Failed==0?0:1;}
    }
}
#endif
