using System;
using System.Globalization;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    // Engine-only software checks. A caller-supplied witness is not a Unity
    // camera, headset visibility, native I/O, or a durability assertion.
    public static class ViveRawCountdownWitnessTests
    {
        static Experiment New(ManualClock clock,bool requireWitness)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"countdown-test-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"countdown-test-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"countdown-test-intent",true,true);
            var e=new Experiment(SessionConfiguration.NamedRecordingDevelopment("countdown-test-session",37,plan,intent),clock);
            if(requireWitness)e.RequireRawCountdownSoftwareWitness();
            e.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
            Fixtures.Practice(e,clock,8);
            e.ConfirmNamedPlan(Actor.Owner,"test-owner");
            e.PrepareNamedRecordingBlockPlan(Actor.Owner,false);
            e.BeginCountdown(Actor.Owner,true);
            return e;
        }
        static bool Witness(Experiment e,int frame=11,long revision=7,double unitySeconds=12.5)
        {return e.TryStartRawCountdownFromSoftwareWitness(frame,revision,unitySeconds);}
        public static void Register(TestSuite suite)
        {
            suite.Run("named raw countdown waits for software witness and records distinct clocks",()=>{
                var c=new ManualClock();var e=New(c,true);
                Check.True(e.RawCountdownAwaitingSoftwareWitness);Check.Near(3,e.CountdownRemainingSeconds);
                c.Advance(30);e.Tick();Check.Equal(Phase.Countdown,e.Phase);Check.Near(3,e.CountdownRemainingSeconds);
                int before=e.Events.Count;Check.True(!Witness(e,-1));Check.True(!Witness(e,11,7,Double.NaN));Check.Equal(before,e.Events.Count);
                double start=c.Now;Check.True(Witness(e));Check.True(!e.RawCountdownAwaitingSoftwareWitness);
                var evt=e.Events[e.Events.Count-1];Check.Equal("countdown_software_witness",evt.Kind);
                Check.Equal(start.ToString("R",CultureInfo.InvariantCulture),evt.Fields["task_start_seconds"]);
                Check.Equal(c.ClockId,evt.Fields["task_clock_id"]);Check.Equal("12.5",evt.Fields["unity_proxy_seconds"]);
                Check.Equal("caller_declared_software_render_not_optical",evt.Fields["basis"]);
                Check.True(!Witness(e));Check.Equal(before+1,e.Events.Count);
                c.Advance(2.99);e.Tick();Check.Equal(Phase.Countdown,e.Phase);
                c.Advance(.02);e.Tick();Check.Equal(Phase.MainTask,e.Phase);
            });
            suite.Run("named raw countdown waits again after pause and preserves remaining time",()=>{
                var c=new ManualClock();var e=New(c,true);Check.True(Witness(e));
                c.Advance(1);e.Pause(Actor.Participant);Check.Equal(Phase.Paused,e.Phase);Check.Near(2,e.CountdownRemainingSeconds);
                Check.True(!Witness(e));c.Advance(20);e.Resume(true);Check.True(e.RawCountdownAwaitingSoftwareWitness);Check.Near(2,e.CountdownRemainingSeconds);
                c.Advance(10);e.Tick();Check.Equal(Phase.Countdown,e.Phase);Check.Near(2,e.CountdownRemainingSeconds);
                Check.True(Witness(e,12,8,13));c.Advance(1.99);e.Tick();Check.Equal(Phase.Countdown,e.Phase);
                c.Advance(.02);e.Tick();Check.Equal(Phase.MainTask,e.Phase);
            });
            suite.Run("named raw countdown paused before first witness never consumes time",()=>{
                var c=new ManualClock();var e=New(c,true);c.Advance(8);e.Pause(Actor.Owner);c.Advance(8);e.Resume(true);
                Check.True(e.RawCountdownAwaitingSoftwareWitness);Check.Near(3,e.CountdownRemainingSeconds);
                Check.True(Witness(e));c.Advance(3);e.Tick();Check.Equal(Phase.MainTask,e.Phase);
            });
            suite.Run("named raw countdown after interrupted main trial waits for a fresh witness",()=>{
                var c=new ManualClock();var e=New(c,true);Check.True(Witness(e));c.Advance(3);e.Tick();
                var old=e.CurrentTrial;e.MarkPresented(old.TrialId);c.Advance(.2);e.Pause(Actor.Participant);c.Advance(5);e.Resume(true);
                Check.Equal(TrialOutcome.Interrupted,old.Outcome);Check.Equal(Phase.Countdown,e.Phase);Check.True(e.RawCountdownAwaitingSoftwareWitness);
                c.Advance(5);e.Tick();Check.Equal(Phase.Countdown,e.Phase);Check.True(Witness(e,20,9,20));
                c.Advance(3);e.Tick();Check.Equal(Phase.MainTask,e.Phase);Check.Equal(1,e.CurrentTrial.PlannedIndex);
            });
            suite.Run("named development countdown without attached raw mode retains existing timing",()=>{
                var c=new ManualClock();var e=New(c,false);Check.True(!e.RawCountdownAwaitingSoftwareWitness);
                c.Advance(3);e.Tick();Check.Equal(Phase.MainTask,e.Phase);Check.True(!Witness(e));
            });
            suite.Run("named raw witness rejects backward clock before changing countdown",()=>{
                var c=new ManualClock();var e=New(c,true);double initial=c.Now;c.Now=initial-1;
                Check.Throws<InvalidOperationException>(()=>Witness(e));Check.True(e.RawCountdownAwaitingSoftwareWitness);Check.Near(3,e.CountdownRemainingSeconds);
            });
            suite.Run("four named raw blocks each wait for their own countdown software witness",()=>{
                var a=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"countdown-test-geometry");
                var n=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"four-countdown-plan",a,n,a,n);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"four-countdown-intent",true,true);
                var c=new ManualClock();var e=new Experiment(SessionConfiguration.NamedRecordingDevelopment("four-countdown-session",37,plan,intent),c);
                e.RequireRawCountdownSoftwareWitness();e.PrepareNamedRecordingBlockPlan(Actor.Owner,true);Fixtures.Practice(e,c,8);
                e.ConfirmNamedPlan(Actor.Owner,"owner");e.PrepareNamedRecordingBlockPlan(Actor.Owner,false);e.BeginCountdown(Actor.Owner,true);
                for(int block=0;block<4;block++)
                {
                    Check.Equal(Phase.Countdown,e.Phase);Check.True(e.RawCountdownAwaitingSoftwareWitness);c.Advance(4);e.Tick();Check.Equal(Phase.Countdown,e.Phase);
                    Check.True(Witness(e,100+block,20+block,100+block));c.Advance(3);e.Tick();Check.Equal(Phase.MainTask,e.Phase);
                    while(e.Phase==Phase.MainTask)Fixtures.Answer(e,c);
                    Check.Equal(Phase.Questionnaire,e.Phase);Check.True(e.AcknowledgeQuestionnaireInstruction());
                    for(int q=0;q<Questionnaire.Count;q++)e.Rate(3);
                    if(block<3){Check.Equal(Phase.Rest,e.Phase);Check.True(e.Ready());e.PrepareNamedRecordingBlockPlan(Actor.Owner,false);Check.True(e.TryBeginRetainedBlockCountdown(true));}
                }
                Check.Equal(Phase.Ended,e.Phase);Check.Equal((EndReason?)EndReason.Completed,e.EndReason);Check.Equal(104,e.Trials.Count);Check.Equal(20,e.Ratings.Count);
                int count=0;foreach(var ev in e.Events)if(ev.Kind=="countdown_software_witness")count++;Check.Equal(4,count);
            });
        }
    }
}
