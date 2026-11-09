using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveWozParticipantHostTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE WoZ host requires explicit development opt-in and publishes authoritative response-free task",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);int clears=0;
                Check.Throws<InvalidOperationException>(()=>new ViveWozParticipantHost(experiment,()=>{},false));
                Check.Throws<ArgumentNullException>(()=>new ViveWozParticipantHost(null,()=>{},true));
                var host=new ViveWozParticipantHost(experiment,()=>clears++,true);
                Check.True(!host.LiveSourceSelected&&!host.CanEnterLiveCountdown&&!host.CanRecordOrExport&&!host.HardwareCaptureTimeVerified);
                var start=host.ReadAuthoritativeTask();Check.Equal(Phase.Preparation,start.Phase);Check.Equal<string>(null,start.TrialId);
                Fixtures.Practice(experiment,clock,8);Fixtures.Start(experiment,clock,ControlSelection.LiveWoz);
                var task=host.ReadAuthoritativeTask();Check.True(host.LiveSourceSelected);
                Check.Equal(experiment.Configuration.SessionId,task.SessionId);Check.Equal(experiment.BlockIndex,task.BlockIndex);
                Check.Equal(experiment.Segment,task.Segment);Check.Equal(experiment.CurrentTrial.TrialId,task.TrialId);
                Check.Equal((Colour?)experiment.CurrentTrial.Stimulus.Word,task.Word);Check.Equal((Colour?)experiment.CurrentTrial.Stimulus.Ink,task.Ink);
                Check.True(host.EnforceUnavailableLivePreflight());Check.Equal(Phase.Paused,experiment.Phase);
                Check.Equal(TrialOutcome.Interrupted,experiment.CurrentTrial.Outcome);Check.Equal(1,clears);
                var paused=host.ReadAuthoritativeTask();Check.Equal<string>(null,paused.TrialId);Check.Equal<Colour?>(null,paused.Word);
                clock.Advance(10);experiment.Tick();Check.Equal(Phase.Paused,experiment.Phase);
            });
            suite.Run("VIVE WoZ host pauses live countdown instead of allowing its clock to run",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);int clears=0;
                Fixtures.Practice(experiment,clock,8);experiment.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"owner");
                experiment.BeginCountdown(Actor.Owner,true);var host=new ViveWozParticipantHost(experiment,()=>clears++,true);
                Check.True(host.EnforceUnavailableLivePreflight());Check.Equal(Phase.Paused,experiment.Phase);
                clock.Advance(20);experiment.Tick();Check.Equal(Phase.Paused,experiment.Phase);Check.Equal(1,clears);
                Check.Throws<InvalidOperationException>(()=>experiment.Resume(false));
            });
            suite.Run("VIVE WoZ host leaves automatic task alone and pauses before renderer cleanup failure",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);Fixtures.Practice(experiment,clock,8);
                Fixtures.Start(experiment,clock,ControlSelection.Automatic);var trial=experiment.CurrentTrial;
                var host=new ViveWozParticipantHost(experiment,()=>{throw new InvalidOperationException("synthetic cleanup");},true);
                Check.True(!host.LiveSourceSelected&&!host.EnforceUnavailableLivePreflight());Check.Equal(Phase.MainTask,experiment.Phase);
                Check.Throws<InvalidOperationException>(()=>host.PauseByOperator());
                Check.Equal(Phase.Paused,experiment.Phase);Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
            });
            suite.Run("VIVE WoZ host refuses live pose without integrated preflight and validates owner thread",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);Fixtures.Practice(experiment,clock,8);
                Fixtures.Start(experiment,clock,ControlSelection.LiveWoz);int clears=0;
                var host=new ViveWozParticipantHost(experiment,()=>clears++,true);
                var sample=new HeadEyeSample("test-session","synthetic-stream","synthetic-device","synthetic-clock",0,0,0,0,0,ControlSource.LiveOperator,StreamRole.OperatorControl,Rotation.Identity,new Vec3(0,0,1),true,true);
                var pose=new ViveDevelopmentRemotePose(sample,0,0,"synthetic-mapping");
                Check.Throws<InvalidOperationException>(()=>host.ApplyLivePose(pose));Check.Equal(1,clears);
                Check.Equal(Phase.MainTask,experiment.Phase); // caller must invoke the readiness enforcement loop
                Exception foreign=null;var thread=new Thread(()=>{try{host.ReadAuthoritativeTask();}catch(Exception error){foreign=error;}});
                thread.Start();Check.True(thread.Join(3000));Check.True(foreign is InvalidOperationException);
                Check.True(host.EnforceUnavailableLivePreflight());Check.Equal(Phase.Paused,experiment.Phase);
            });
        }
    }
}
