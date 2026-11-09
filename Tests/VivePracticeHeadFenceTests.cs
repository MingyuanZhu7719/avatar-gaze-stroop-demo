using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic task/clock only. No worker, XR, file, network or apparatus.
    public static class VivePracticeHeadFenceTests
    {
        sealed class Clock:IClock
        {
            public double Time=10;
            public Action Hook;
            public bool Fail;
            public string ClockId {get{return "head-fence-synthetic-clock";}}
            public double Now {get{var h=Hook;Hook=null;if(h!=null)h();if(Fail)throw new InvalidOperationException("synthetic clock failure");return Time;}}
        }
        static ViveDevelopmentParticipant New(Clock clock,bool observed=true,int capacity=4096,int traceLimit=4096)
        {
            var config=new SessionConfiguration("head-fence-synthetic-session",42,
                new Allocation("head-fence-fixture",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"head-fence-synthetic-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            if(!observed)return new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
            return new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true,
                new ExperimentIncrementalOptions(capacity,128,16L*1024*1024),traceLimit);
        }
        static VivePreviewTrackpadSample Neutral(){return new VivePreviewTrackpadSample(true,false,0,0);}
        static ViveDevelopmentParticipant Ready(Clock clock)
        {var task=New(clock);task.UpdateTrackpad(true,Neutral());return task;}
        static ViveDevelopmentParticipant.PracticeObservationFence Acquire(ViveDevelopmentParticipant task,IClock clock)
        {
            ViveDevelopmentParticipant.PracticeObservationFence token;
            Check.True(task.TryAcquirePracticeObservationFence(clock,out token)&&token!=null,"expected current owner fence");
            return token;
        }
        static Experiment ExperimentOf(ViveDevelopmentParticipant task)
        {
            var field=typeof(ViveDevelopmentParticipant).GetField("experiment",BindingFlags.NonPublic|BindingFlags.Instance);
            Check.True(field!=null);return (Experiment)field.GetValue(task);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("practice head fence is unavailable without observation or exact clock object",()=>{
                var clock=new Clock();var normal=New(clock,false);ViveDevelopmentParticipant.PracticeObservationFence token;
                Check.True(!normal.TryAcquirePracticeObservationFence(clock,out token)&&token==null);
                var observed=New(clock);var sameLabel=new Clock();Check.Equal(clock.ClockId,sameLabel.ClockId);
                Check.True(!observed.TryAcquirePracticeObservationFence(sameLabel,out token)&&token==null);
                Check.True(!observed.ObservationFaulted);token=Acquire(observed,clock);Check.True(observed.TryConsumePracticeObservationFence(token));
            });
            suite.Run("practice head fence is participant-bound single outstanding and single use",()=>{
                var clock=new Clock();var a=New(clock);var b=New(clock);Check.Equal(a.SessionId,b.SessionId);
                var first=Acquire(a,clock);ViveDevelopmentParticipant.PracticeObservationFence second;
                Check.True(!a.TryAcquirePracticeObservationFence(clock,out second)&&second==null,"an outstanding proof must not be replaced");
                Check.True(!b.TryConsumePracticeObservationFence(first),"same session id must not transfer proof");
                // A rejected foreign proof must never become evidence for B; the
                // originating proof, if still available, remains at most one-use.
                bool consumed=a.TryConsumePracticeObservationFence(first);
                Check.True(!a.TryConsumePracticeObservationFence(first));
                if(consumed)Check.True(!b.TryConsumePracticeObservationFence(first));
            });
            suite.Run("practice head fence successful proof cannot be consumed twice",()=>{
                var clock=new Clock();var task=New(clock);var token=Acquire(task,clock);
                Check.True(task.TryConsumePracticeObservationFence(token));Check.True(!task.TryConsumePracticeObservationFence(token));
            });
            suite.Run("practice head fence abandon is idempotent and does not transfer between participants",()=>{
                var clock=new Clock();var a=New(clock);var b=New(clock);var token=Acquire(a,clock);
                b.AbandonPracticeObservationFence(token);Check.True(a.TryConsumePracticeObservationFence(token));
                var next=Acquire(a,clock);a.AbandonPracticeObservationFence(next);a.AbandonPracticeObservationFence(next);
                Check.True(!a.TryConsumePracticeObservationFence(next));var last=Acquire(a,clock);Check.True(a.TryConsumePracticeObservationFence(last));
            });
            suite.Run("practice head fence reports trial from snapshot rather than practice task display",()=>{
                var clock=new Clock();var task=Ready(clock);clock.Time+=1;task.OwnerBeginPractice(Actor.Owner);
                var token=Acquire(task,clock);Check.Equal(Phase.Practice,token.Snapshot.Task.Phase);
                Check.True(token.Snapshot.Task.TrialId==null,"practice is deliberately omitted from task display snapshot");
                Check.True(token.Snapshot.CurrentTrial.HasValue&&token.Snapshot.CurrentTrial.Value.IsPractice);
                Check.Equal(task.CurrentTrial.TrialId,token.Snapshot.CurrentTrial.Value.TrialId);
                Check.True(task.TryConsumePracticeObservationFence(token));
            });
            suite.Run("practice head fence invalidates on every public task mutation including no-op and safety",()=>{
                var operations=new Action<ViveDevelopmentParticipant>[] {
                    p=>p.UpdateNeutralPending(true),
                    p=>p.UpdateTrackpad(true,Neutral()),
                    p=>p.UpdateAuxiliary(false,false,false,0,true),
                    p=>p.OwnerBeginPractice(Actor.Owner),
                    p=>p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"synthetic-owner"),
                    p=>p.OwnerBeginBlock(Actor.Owner),
                    p=>p.MarkPresented("missing-synthetic-trial"),
                    p=>p.Tick(),
                    p=>p.EndTechnical(Actor.Owner,"synthetic-end"),
                    p=>p.OwnerPause(Actor.Owner),
                    p=>p.ParticipantPause(),
                    p=>p.ParticipantWithdraw()
                };
                for(int i=0;i<operations.Length;i++)
                {
                    var clock=new Clock();var task=Ready(clock);var token=Acquire(task,clock);
                    // Some operations intentionally fail in Preparation; the
                    // failed invocation still must invalidate old proof.
                    try{operations[i](task);}catch(InvalidOperationException){}catch(ArgumentException){}
                    Check.True(!task.TryConsumePracticeObservationFence(token),"stale proof survived public mutation "+i);
                }
            });
            suite.Run("practice head fence invalidates invalid input and caller exceptions",()=>{
                var clock=new Clock();var task=Ready(clock);var token=Acquire(task,clock);
                Check.Throws<ArgumentNullException>(()=>task.UpdateTrackpad(true,null));
                Check.True(!task.TryConsumePracticeObservationFence(token));
                var other=Ready(new Clock());var otherClock=new Clock(); // same label but a different instance
                ViveDevelopmentParticipant.PracticeObservationFence ignored;
                Check.True(!other.TryAcquirePracticeObservationFence(otherClock,out ignored));
            });
            suite.Run("practice head fence is unavailable inside an actual task clock callback",()=>{
                var clock=new Clock();var task=Ready(clock);var token=Acquire(task,clock);bool acquired=true,consumed=true;
                clock.Hook=()=>{
                    ViveDevelopmentParticipant.PracticeObservationFence nested;
                    acquired=task.TryAcquirePracticeObservationFence(clock,out nested);
                    consumed=task.TryConsumePracticeObservationFence(token);
                };
                task.OwnerBeginPractice(Actor.Owner);
                Check.True(!acquired&&!consumed,"action callback must not obtain or consume current proof");
                Check.True(!task.TryConsumePracticeObservationFence(token));
            });
            suite.Run("practice head fence invalidates on nested safety mutation",()=>{
                var clock=new Clock();var task=Ready(clock);var token=Acquire(task,clock);bool called=false;
                clock.Hook=()=>{called=true;try{task.ParticipantWithdraw();}catch(InvalidOperationException){}};
                try{task.OwnerBeginPractice(Actor.Owner);}catch(InvalidOperationException){}
                Check.True(called);Check.True(!task.TryConsumePracticeObservationFence(token));
            });
            suite.Run("practice head fence is unavailable during a safety action clock callback",()=>{
                var clock=new Clock();var task=Ready(clock);clock.Time+=1;task.OwnerBeginPractice(Actor.Owner);
                var token=Acquire(task,clock);bool acquired=true,consumed=true;
                clock.Hook=()=>{
                    ViveDevelopmentParticipant.PracticeObservationFence nested;
                    acquired=task.TryAcquirePracticeObservationFence(clock,out nested);
                    consumed=task.TryConsumePracticeObservationFence(token);
                };
                task.ParticipantPause();Check.Equal(Phase.Paused,task.Phase);
                Check.True(!acquired&&!consumed,"safety action must invalidate and block proof");
                Check.True(!task.TryConsumePracticeObservationFence(token));
            });
            suite.Run("practice head fence rejects foreign-thread access and preserves safety",()=>{
                var clock=new Clock();var task=Ready(clock);var token=Acquire(task,clock);bool result=true;Exception caught=null;
                var thread=new Thread(()=>{try{result=task.TryConsumePracticeObservationFence(token);}catch(Exception error){caught=error;}});
                thread.Start();Check.True(thread.Join(5000));Check.True(caught==null&&!result);Check.True(!task.TryConsumePracticeObservationFence(token));
                // An owner-thread safety action must remain reachable even if
                // the untrusted thread caused observation to latch a fault.
                task.ParticipantWithdraw();Check.Equal(Phase.Ended,task.Phase);Check.Equal(EndReason.Withdrawn,task.EndReason.Value);
            });
            suite.Run("practice head fence refuses unconsumed producer backlog and overflow",()=>{
                var clock=new Clock();var task=New(clock,true,1);var experiment=ExperimentOf(task);
                experiment.RecordHeadsetRemoval(Actor.Owner);experiment.RecordHeadsetRemoval(Actor.Owner);
                ViveDevelopmentParticipant.PracticeObservationFence token;
                Check.True(!task.TryAcquirePracticeObservationFence(clock,out token)&&token==null);
                Check.True(task.ObservationFaulted);task.ParticipantWithdraw();Check.Equal(Phase.Ended,task.Phase);
            });
            suite.Run("practice head fence rejects source mutation without a participant wrapper",()=>{
                var clock=new Clock();var task=New(clock);var token=Acquire(task,clock);
                // Bypass is intentional fault injection on this synthetic task.
                ExperimentOf(task).RecordHeadsetRemoval(Actor.Owner);
                Check.True(!task.TryConsumePracticeObservationFence(token));
                Check.True(task.SynchronizeObservation());var next=Acquire(task,clock);Check.True(task.TryConsumePracticeObservationFence(next));
            });
            suite.Run("practice head fence cannot consume a token after a source error",()=>{
                var clock=new Clock();var task=Ready(clock);var token=Acquire(task,clock);
                clock.Fail=true;Check.Throws<InvalidOperationException>(()=>task.OwnerBeginPractice(Actor.Owner));
                clock.Fail=false;Check.True(!task.TryConsumePracticeObservationFence(token));
                task.ParticipantWithdraw();Check.Equal(Phase.Ended,task.Phase);
            });
        }
    }
}
