using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Simulation state only; no real consent, device, writer, network or file.
    public static class VivePracticeWithdrawConsentTests
    {
        sealed class Clock:IClock
        {
            public double Time=10;public Action Hook,IdHook;
            public string ClockId {get{var h=IdHook;IdHook=null;if(h!=null)h();return "withdraw-consent-clock";}}
            public double Now {get{var h=Hook;Hook=null;if(h!=null)h();return Time;}}
        }
        sealed class Fixture
        {
            public readonly Clock Clock=new Clock();public readonly ViveDevelopmentParticipant Task;
            public readonly VivePracticeConsentAuthority Choices;
            public Fixture()
            {
                Task=New(Clock,true);Choices=new VivePracticeConsentAuthority(Task,Clock,true);
                Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask()&&Choices.Active);
                Task.UpdateTrackpad(true,Neutral());Clock.Time=11;Task.OwnerBeginPractice(Actor.Owner);
            }
        }
        static VivePreviewTrackpadSample Neutral(){return new VivePreviewTrackpadSample(true,false,0,0);}
        static ViveDevelopmentParticipant New(Clock clock,bool observed)
        {
            var config=new SessionConfiguration("withdraw-consent-session",42,
                new Allocation("withdraw-consent-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"withdraw-consent-geometry");
            var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return observed?new ViveDevelopmentParticipant(config,clock,geometry,mapping,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096):
                new ViveDevelopmentParticipant(config,clock,geometry,mapping,.5,true);
        }
        static void Prime(ViveDevelopmentParticipant p){p.UpdateAuxiliary(false,false,false,0,true);}
        static void FailObservation(ViveDevelopmentParticipant p)
        {
            var engine=typeof(ViveDevelopmentParticipant).GetField("experiment",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(p);
            var journal=typeof(Experiment).GetField("incrementalMutationJournal",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(engine);
            var budget=(ExperimentPersistentMutationBudget)journal.GetType().GetField("budget",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(journal);
            Check.True(!budget.TryReserve(budget.EstimatedBytesLimit));Check.True(p.ObservationFaulted);
        }
        // Fake entry counts only. This does not represent the real writer or
        // prove that an already-started Stream.Write can be undone.
        sealed class FakeSink
        {
            readonly Fixture f;readonly long generation;public int WriteStarts;public bool InFlightUncertain;
            public FakeSink(Fixture fixture){f=fixture;generation=f.Choices.Generation;}
            public bool Write(Action during)
            {
                if(!f.Choices.IsCurrent(generation,f.Task,f.Clock))return false;
                WriteStarts++;if(during!=null)during();
                if(!f.Choices.IsCurrent(generation,f.Task,f.Clock)){InFlightUncertain=true;return false;}
                return true;
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("withdrawal during choice authority construction prevents later attachment",()=>{
                var c=new Clock();var p=New(c,true);bool hookRan=false;
                c.IdHook=()=>{hookRan=true;p.ParticipantWithdraw();};bool rejected=false;
                try{new VivePracticeConsentAuthority(p,c,true);}catch(InvalidOperationException){rejected=true;}
                Check.True(hookRan&&rejected);Check.Equal(Phase.Ended,p.Phase);
                rejected=false;try{new VivePracticeConsentAuthority(p,c,true);}catch(InvalidOperationException){rejected=true;}
                Check.True(rejected);
            });
            suite.Run("participant direct withdraw revokes choices before task callback",()=>{
                var f=new Fixture();long before=f.Choices.Generation;bool ran=false,revoked=false,current=true;
                f.Clock.Hook=()=>{ran=true;revoked=f.Choices.Revoked;current=f.Choices.IsCurrent(before,f.Task,f.Clock);};
                f.Task.ParticipantWithdraw();Check.True(ran&&revoked&&!current&&!f.Choices.Active);
                Check.True(f.Choices.Generation>before);Check.Equal(Phase.Ended,f.Task.Phase);Check.Equal(EndReason.Withdrawn,f.Task.EndReason.Value);
            });
            suite.Run("auxiliary withdraw edge revokes before task callback",()=>{
                var f=new Fixture();Prime(f.Task);long before=f.Choices.Generation;bool ran=false,revoked=false;
                f.Clock.Hook=()=>{ran=true;revoked=f.Choices.Revoked&&!f.Choices.IsCurrent(before,f.Task,f.Clock);};
                f.Task.UpdateAuxiliary(false,false,true,0,true);
                Check.True(ran&&revoked&&!f.Choices.Active);Check.Equal(Phase.Ended,f.Task.Phase);
            });
            suite.Run("blocked observation still routes auxiliary safety withdrawal first",()=>{
                var f=new Fixture();Prime(f.Task);FailObservation(f.Task);Prime(f.Task);
                bool ran=false,revoked=false;long before=f.Choices.Generation;
                f.Clock.Hook=()=>{ran=true;revoked=f.Choices.Revoked&&!f.Choices.IsCurrent(before,f.Task,f.Clock);};
                f.Task.UpdateAuxiliary(false,false,true,0,true);
                Check.True(ran&&revoked&&f.Task.ObservationFaulted);Check.Equal(Phase.Ended,f.Task.Phase);
            });
            suite.Run("foreign-thread withdrawal invalidates choice even if task mutation is denied",()=>{
                var f=new Fixture();long before=f.Choices.Generation;Exception error=null;
                var thread=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(Exception e){error=e;}});
                thread.Start();Check.True(thread.Join(5000));
                Check.True(f.Choices.Revoked&&!f.Choices.Active&&!f.Choices.IsCurrent(before,f.Task,f.Clock));
                Check.True(f.Choices.Generation>before);Check.True(error!=null||f.Task.Phase==Phase.Ended);
                if(f.Task.Phase!=Phase.Ended)f.Task.ParticipantWithdraw();
                Check.Equal(Phase.Ended,f.Task.Phase);
            });
            suite.Run("foreign-thread auxiliary withdrawal revokes before owner rejection",()=>{
                var f=new Fixture();Prime(f.Task);long before=f.Choices.Generation;Exception error=null;
                var thread=new Thread(()=>{try{f.Task.UpdateAuxiliary(false,false,true,0,true);}catch(Exception e){error=e;}});
                thread.Start();Check.True(thread.Join(5000));
                Check.True(f.Choices.Revoked&&!f.Choices.Active&&!f.Choices.IsCurrent(before,f.Task,f.Clock));
                Check.True(f.Choices.Generation>before);Check.True(error!=null);Check.True(f.Task.ObservationFaulted);
                Check.Equal(Phase.Practice,f.Task.Phase);f.Task.ParticipantWithdraw();Check.Equal(Phase.Ended,f.Task.Phase);
            });
            suite.Run("participant withdrawal invalidates choices before authority lock is available",()=>{
                var f=new Fixture();long generation=f.Choices.Generation;
                var field=typeof(VivePracticeConsentAuthority).GetField("stateGate",BindingFlags.Instance|BindingFlags.NonPublic);
                var revokedField=typeof(VivePracticeConsentAuthority).GetField("revoked",BindingFlags.Instance|BindingFlags.NonPublic);
                Check.True(field!=null&&revokedField!=null);object gate=field.GetValue(f.Choices);Exception error=null;
                var thread=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(Exception e){error=e;}});
                lock(gate)
                {
                    thread.Start();Check.True(SpinWait.SpinUntil(()=>f.Task.PracticeConsentWithdrawalRequested,5000));
                    Check.True(!(bool)revokedField.GetValue(f.Choices),"authority mutation remains blocked on this lock");
                    Check.True(f.Choices.Revoked&&!f.Choices.Active&&!f.Choices.IsCurrent(generation,f.Task,f.Clock));
                }
                Check.True(thread.Join(5000));Check.True(f.Choices.Revoked&&!f.Choices.Active);
                Check.True(error!=null||f.Task.Phase==Phase.Ended);
                if(f.Task.Phase!=Phase.Ended)f.Task.ParticipantWithdraw();
                Check.Equal(Phase.Ended,f.Task.Phase);
            });
            suite.Run("repeated withdrawal never restores choices or starts new fake writes",()=>{
                var f=new Fixture();var fake=new FakeSink(f);Check.True(fake.Write(null));f.Task.ParticipantWithdraw();
                long after=f.Choices.Generation;Check.True(f.Choices.Revoked);
                try{f.Task.ParticipantWithdraw();}catch(InvalidOperationException){}
                Check.True(f.Choices.Revoked&&!f.Choices.Active&&f.Choices.Generation>=after);
                Check.True(!fake.Write(null));Check.Equal(1,fake.WriteStarts);
            });
            suite.Run("withdrawal during fake write invalidates choice without undoing started write",()=>{
                var f=new Fixture();var fake=new FakeSink(f);
                Check.True(!fake.Write(f.Task.ParticipantWithdraw));Check.True(f.Choices.Revoked&&fake.InFlightUncertain);
                Check.Equal(1,fake.WriteStarts);Check.True(!fake.Write(null));Check.Equal(1,fake.WriteStarts);
            });
            suite.Run("unobserved participant preserves original direct withdrawal",()=>{
                var c=new Clock();var p=New(c,false);p.UpdateTrackpad(true,Neutral());c.Time=11;p.OwnerBeginPractice(Actor.Owner);
                p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Withdrawn,p.EndReason.Value);
            });
            suite.Run("unobserved participant preserves auxiliary edge semantics",()=>{
                var c=new Clock();var p=New(c,false);p.UpdateTrackpad(true,Neutral());c.Time=11;p.OwnerBeginPractice(Actor.Owner);
                p.UpdateAuxiliary(false,false,true,0,true);Check.Equal(Phase.Practice,p.Phase);
                p.UpdateAuxiliary(false,false,true,0,true);Check.Equal(Phase.Practice,p.Phase);
                p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(false,false,true,0,true);
                Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Withdrawn,p.EndReason.Value);
            });
        }
    }
}
