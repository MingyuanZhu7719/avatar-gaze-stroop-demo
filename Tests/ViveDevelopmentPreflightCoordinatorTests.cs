using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentPreflightCoordinatorTests
    {
        sealed class App : IVivePreflightAppPort
        {
            public bool IsOpen {get;set;}=true;
            public bool HasExplicitConfirmation {get;set;}=true;
            public bool RecoveryDecisionRequired {get;set;}
            public bool Faulted {get;set;}
            public bool LastPresentationReady;public int Ticks;
            public void TickParticipant(bool ready){Ticks++;LastPresentationReady=ready;}
        }
        sealed class Port : IVivePreflightParticipantPort
        {
            public bool Active {get;set;}=true;
            public bool RecoveryDecisionRequired {get;set;}
            public bool Candidate,BeginResult=true,ThrowPoll;public int Begins,Polls,Losses;
            public bool Begin(TaskSnapshot t,bool a,bool l){Begins++;return BeginResult;}
            public bool Poll(TaskSnapshot t,bool a,bool l){Polls++;if(ThrowPoll)throw new InvalidOperationException("synthetic");return false;}
            public bool CandidateReady(TaskSnapshot t,bool a,bool l){return Candidate;}
            public void SignalLoss(string reason){Losses++;RecoveryDecisionRequired=true;Active=false;}
        }
        sealed class Host : IVivePreflightBoundParticipantHost
        {
            public string BoundSessionId {get;set;}="session";
            public TaskSnapshot Task;public bool LiveSourceSelected {get;set;}=true;
            public bool ClearFails,PauseFails,NoTransition;public int Clears,Pauses;
            public TaskSnapshot ReadAuthoritativeTask(){return Task;}
            public void ClearLivePose(){Clears++;if(ClearFails)throw new InvalidOperationException("synthetic renderer error");}
            public void ApplyLivePose(ViveDevelopmentRemotePose pose){throw new Exception("This coordinator must never render a live pose");}
            public void PauseByOperator(){throw new NotImplementedException();}
            public void PauseForTransportLoss()
            {
                Pauses++;if(PauseFails)throw new InvalidOperationException("synthetic pause failure");
                if(!NoTransition&&Task!=null)Task=new TaskSnapshot(Task.SessionId,Task.BlockIndex,Task.Segment,null,null,null,Phase.Paused,Task.Condition);
            }
        }
        sealed class FaultHost : IVivePreflightBoundParticipantHost
        {
            public string BoundSessionId {get{return "session";}}
            public bool ThrowTask,ThrowSource,Live;public TaskSnapshot Task;public int Pauses,Clears;
            public TaskSnapshot ReadAuthoritativeTask(){if(ThrowTask)throw new InvalidOperationException("synthetic task read");return Task;}
            public bool LiveSourceSelected {get{if(ThrowSource)throw new InvalidOperationException("synthetic source read");return Live;}}
            public void ClearLivePose(){Clears++;}
            public void ApplyLivePose(ViveDevelopmentRemotePose pose){throw new NotImplementedException();}
            public void PauseByOperator(){throw new NotImplementedException();}
            public void PauseForTransportLoss(){Pauses++;if(Task!=null)Task=new TaskSnapshot("session",Task.BlockIndex,Task.Segment,null,null,null,Phase.Paused,Task.Condition);}
        }
        static TaskSnapshot Task(string session,Phase phase,int block=0,int segment=0,Condition condition=Condition.A)
        {return new TaskSnapshot(session,block,segment,null,null,null,phase,condition);}
        static void Practice(Experiment experiment,IClock clock,Action advance)
        {
            experiment.BeginFamiliarization(Actor.Owner);experiment.BeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {
                experiment.MarkPresented(experiment.CurrentTrial.TrialId);advance();
                var ink=experiment.CurrentTrial.Stimulus.Ink;Check.True(experiment.ButtonChanged(ink,true));experiment.ButtonChanged(ink,false);
                if(experiment.Phase==Phase.PracticeCorrection)experiment.ContinuePractice();
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VDPF coordinator candidate cannot open countdown or render",()=>{
                var host=new Host {Task=Task("session",Phase.BlockReady)};var app=new App();var port=new Port {Candidate=true};
                var coordinator=new ViveDevelopmentPreflightCoordinator("session",host,app,port);coordinator.Tick();
                Check.True(coordinator.CandidateObserved&&!coordinator.CanEnterCountdown&&!coordinator.CryptographicallyAuthenticated);
                Check.True(app.Ticks==1&&!app.LastPresentationReady);Check.Equal(1,port.Begins);Check.Equal(0,host.Pauses);
                coordinator.Tick();Check.Equal(1,port.Begins);Check.Equal(2,port.Polls);Check.Equal(0,host.Pauses);
            });
            suite.Run("VDPF coordinator really pauses authoritative experiment before countdown tick",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);Practice(experiment,clock,()=>clock.Advance(.4));
                experiment.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"owner");int clears=0;
                var host=new VivePreflightExperimentHost(experiment,()=>clears++,true);var port=new Port {Candidate=true};var app=new App();
                var coordinator=new ViveDevelopmentPreflightCoordinator(experiment.Configuration.SessionId,host,app,port);
                coordinator.Tick();Check.True(coordinator.CandidateObserved&&experiment.Phase==Phase.BlockReady);
                // Deliberately force the unsupported transition to check fail-closed behavior.
                experiment.BeginCountdown(Actor.Owner,true);coordinator.Tick();
                Check.Equal(Phase.Paused,experiment.Phase);Check.True(coordinator.RecoveryDecisionRequired&&coordinator.PauseVerified);
                Check.Equal(1,port.Losses);Check.True(clears>0&&!coordinator.CanEnterCountdown);
                clock.Advance(30);experiment.Tick();Check.Equal(Phase.Paused,experiment.Phase);
            });
            suite.Run("VDPF coordinator pauses an active trial and retains interruption",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);Practice(experiment,clock,()=>clock.Advance(.4));
                experiment.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"owner");experiment.BeginCountdown(Actor.Owner,true);clock.Advance(2);experiment.Tick();
                var trial=experiment.CurrentTrial;var host=new VivePreflightExperimentHost(experiment,()=>{},true);
                var coordinator=new ViveDevelopmentPreflightCoordinator(experiment.Configuration.SessionId,host,new App(),new Port());coordinator.Tick();
                Check.Equal(Phase.Paused,experiment.Phase);Check.Equal(TrialOutcome.Interrupted,trial.Outcome);Check.True(coordinator.PauseVerified);
            });
            suite.Run("VDPF coordinator still pauses when renderer cleanup throws",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);Practice(experiment,clock,()=>clock.Advance(.4));
                experiment.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"owner");experiment.BeginCountdown(Actor.Owner,true);
                var host=new VivePreflightExperimentHost(experiment,()=>{throw new InvalidOperationException("synthetic renderer");},true);
                var coordinator=new ViveDevelopmentPreflightCoordinator(experiment.Configuration.SessionId,host,new App(),new Port());coordinator.Tick();
                Check.Equal(Phase.Paused,experiment.Phase);Check.True(coordinator.PauseVerified&&coordinator.LastIssue.Contains("pause_callback_failed"));
            });
            suite.Run("VDPF coordinator gap source change and false pause do not resume",()=>{
                foreach(int mode in new[]{0,1,2,3})
                {
                    var host=new Host {Task=Task("session",Phase.BlockReady)};var app=new App();var port=new Port {Candidate=true};
                    var coordinator=new ViveDevelopmentPreflightCoordinator("session",host,app,port);coordinator.Tick();
                    host.Task=Task("session",Phase.Countdown);
                    if(mode==0)port.RecoveryDecisionRequired=true;
                    if(mode==1)host.LiveSourceSelected=false;
                    if(mode==2)host.NoTransition=true;
                    if(mode==3)host.PauseFails=true;
                    coordinator.Tick();Check.True(coordinator.RecoveryDecisionRequired&&!coordinator.CandidateObserved&&!coordinator.CanEnterCountdown);
                    Check.Equal(1,host.Pauses);Check.Equal(mode<2,coordinator.PauseVerified);
                    coordinator.Tick();Check.Equal(mode<2?1:2,host.Pauses);Check.Equal(1,port.Losses);
                }
            });
            suite.Run("VDPF coordinator does not pause automatic or foreign session",()=>{
                var automatic=new Host {Task=Task("session",Phase.MainTask),LiveSourceSelected=false};var port=new Port();
                var coordinator=new ViveDevelopmentPreflightCoordinator("session",automatic,new App(),port);coordinator.Tick();
                coordinator.SignalRemoteLoss();Check.Equal(0,automatic.Pauses);
                var foreign=new Host {Task=Task("other",Phase.MainTask)};var another=new ViveDevelopmentPreflightCoordinator("session",foreign,new App(),new Port());
                another.Tick();another.Tick();Check.Equal(0,foreign.Pauses);Check.Equal(0,foreign.Clears);Check.True(another.RecoveryDecisionRequired&&!another.PauseVerified);
            });
            suite.Run("VDPF coordinator keeps known automatic isolated if task read is missing",()=>{
                foreach(bool throws in new[]{false,true})
                {
                    var host=new FaultHost {Task=null,Live=false,ThrowTask=throws};var coordinator=new ViveDevelopmentPreflightCoordinator("session",host,new App(),new Port());
                    coordinator.Tick();Check.Equal(0,host.Pauses);Check.True(coordinator.RecoveryDecisionRequired&&!coordinator.PauseVerified);
                }
            });
            suite.Run("VDPF coordinator pauses unknown source in a bound active task",()=>{
                var host=new FaultHost {Task=Task("session",Phase.Countdown),ThrowSource=true};
                var coordinator=new ViveDevelopmentPreflightCoordinator("session",host,new App(),new Port());coordinator.Tick();
                Check.True(coordinator.RecoveryDecisionRequired&&coordinator.PauseVerified);Check.Equal(1,host.Pauses);
            });
            suite.Run("VDPF coordinator rechecks unknown source after an automatic-side loss",()=>{
                var host=new FaultHost {Task=Task("session",Phase.BlockReady),Live=false};
                var coordinator=new ViveDevelopmentPreflightCoordinator("session",host,new App(),new Port());
                coordinator.SignalRemoteLoss();Check.Equal(0,host.Pauses);
                host.Task=Task("session",Phase.Countdown);host.ThrowSource=true;coordinator.Tick();
                Check.Equal(1,host.Pauses);Check.True(coordinator.PauseVerified);
            });
            suite.Run("VDPF foreign snapshot cannot poison automatic task state after recovery latch",()=>{
                var host=new Host {Task=Task("session",Phase.MainTask),LiveSourceSelected=false};
                var coordinator=new ViveDevelopmentPreflightCoordinator("session",host,new App(),new Port());
                coordinator.SignalRemoteLoss();int clears=host.Clears;
                host.Task=Task("foreign",Phase.MainTask);host.LiveSourceSelected=true;coordinator.Tick();
                Check.Equal(clears,host.Clears);Check.Equal(0,host.Pauses);
                host.Task=Task("session",Phase.MainTask);host.LiveSourceSelected=false;coordinator.Tick();
                Check.Equal(0,host.Pauses);Check.True(coordinator.RecoveryDecisionRequired&&!coordinator.PauseVerified);
            });
            suite.Run("VDPF repeated faults keep diagnostic text bounded",()=>{
                var host=new Host {Task=Task("session",Phase.Countdown),PauseFails=true,ClearFails=true};
                var coordinator=new ViveDevelopmentPreflightCoordinator("session",host,new App(),new Port());coordinator.Tick();
                for(int i=0;i<2000;i++)coordinator.Tick();
                Check.True(coordinator.LastIssue.Length<=1024&&coordinator.LastIssue.Contains("coordinator_fault"));
                Check.True(coordinator.RecoveryDecisionRequired&&!coordinator.PauseVerified);
            });
            suite.Run("VDPF coordinator blocks preflight loss at BlockReady without pretending to pause",()=>{
                var host=new Host {Task=Task("session",Phase.BlockReady)};var port=new Port {BeginResult=false};
                var coordinator=new ViveDevelopmentPreflightCoordinator("session",host,new App(),port);coordinator.Tick();
                Check.True(coordinator.RecoveryDecisionRequired&&!coordinator.PauseVerified&&!coordinator.CanEnterCountdown);
                Check.Equal(0,host.Pauses);Check.Equal(1,port.Losses);
            });
            suite.Run("VDPF coordinator enforces an earlier BlockReady failure after a forced countdown",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);Practice(experiment,clock,()=>clock.Advance(.4));
                experiment.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"owner");
                var host=new VivePreflightExperimentHost(experiment,()=>{},true);var port=new Port {BeginResult=false};
                var coordinator=new ViveDevelopmentPreflightCoordinator(experiment.Configuration.SessionId,host,new App(),port);
                coordinator.Tick();Check.True(coordinator.RecoveryDecisionRequired&&!coordinator.PauseVerified);Check.Equal(Phase.BlockReady,experiment.Phase);
                experiment.BeginCountdown(Actor.Owner,true);coordinator.Tick();
                Check.Equal(Phase.Paused,experiment.Phase);Check.True(coordinator.PauseVerified);
                clock.Advance(30);experiment.Tick();Check.Equal(Phase.Paused,experiment.Phase);Check.Equal(1,port.Losses);
            });
            suite.Run("VDPF coordinator remote loss enforces previously latched block failure immediately",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);Practice(experiment,clock,()=>clock.Advance(.4));
                experiment.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"owner");
                var host=new VivePreflightExperimentHost(experiment,()=>{},true);var port=new Port {BeginResult=false};
                var coordinator=new ViveDevelopmentPreflightCoordinator(experiment.Configuration.SessionId,host,new App(),port);
                coordinator.Tick();experiment.BeginCountdown(Actor.Owner,true);coordinator.SignalRemoteLoss();
                Check.Equal(Phase.Paused,experiment.Phase);Check.True(coordinator.PauseVerified);Check.Equal(1,port.Losses);
            });
            suite.Run("VDPF coordinator retries an unsuccessful pause without unfreezing state",()=>{
                var host=new Host {Task=Task("session",Phase.Countdown),PauseFails=true};var port=new Port();
                var coordinator=new ViveDevelopmentPreflightCoordinator("session",host,new App(),port);coordinator.Tick();
                Check.True(coordinator.RecoveryDecisionRequired&&!coordinator.PauseVerified);Check.Equal(1,host.Pauses);
                host.PauseFails=false;coordinator.Tick();Check.True(coordinator.PauseVerified);Check.Equal(2,host.Pauses);
                coordinator.Tick();Check.Equal(2,host.Pauses);Check.Equal(1,port.Losses);
                int clears=host.Clears;host.BoundSessionId="foreign";host.Task=Task("foreign",Phase.MainTask);coordinator.Tick();
                Check.True(!coordinator.PauseVerified);Check.Equal(2,host.Pauses);Check.Equal(clears,host.Clears);
            });
            suite.Run("VDPF coordinator requires a stable explicit host session binding",()=>{
                var host=new Host {BoundSessionId="different",Task=Task("different",Phase.MainTask)};
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentPreflightCoordinator("session",host,new App(),new Port()));
                Check.Equal(0,host.Clears);Check.Equal(0,host.Pauses);
                host.BoundSessionId="session";host.Task=Task("session",Phase.BlockReady);
                var coordinator=new ViveDevelopmentPreflightCoordinator("session",host,new App(),new Port());coordinator.Tick();
                int clears=host.Clears;host.BoundSessionId="different";host.Task=Task("different",Phase.MainTask);coordinator.Tick();coordinator.Tick();
                Check.Equal(clears,host.Clears);Check.Equal(0,host.Pauses);Check.True(coordinator.RecoveryDecisionRequired&&!coordinator.PauseVerified);
            });
            suite.Run("VDPF coordinator latches preflight exceptions and cleanup failures",()=>{
                var host=new Host {Task=Task("session",Phase.BlockReady)};var port=new Port {ThrowPoll=true};
                var coordinator=new ViveDevelopmentPreflightCoordinator("session",host,new App(),port);coordinator.Tick();
                Check.True(coordinator.RecoveryDecisionRequired&&!coordinator.PauseVerified);Check.Equal(1,port.Losses);
                var countdown=new Host {Task=Task("session",Phase.Countdown),ClearFails=true};
                var another=new ViveDevelopmentPreflightCoordinator("session",countdown,new App(),new Port());another.Tick();
                Check.True(another.RecoveryDecisionRequired&&another.PauseVerified);Check.Equal(1,countdown.Pauses);
                Check.True(another.LastIssue.Contains("clear_failed"));
            });
        }
    }
}
