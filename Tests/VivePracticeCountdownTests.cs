using AvatarStroop.Runtime;
using AvatarStroop.Core;
using System;
using System.Globalization;
using System.Linq;

namespace AvatarStroop.Tests
{
    public static class VivePracticeCountdownTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("practice warning needs five seconds of consecutive rendered frames",()=>{
                var countdown=new VivePracticeCountdown();
                Check.Near(5,countdown.RemainingSeconds);
                Check.True(!countdown.CompleteForFrame(100,1));
                for(int i=0;i<=100;i++)
                {
                    countdown.ObserveRendered(i*.05,i);
                    if(i<100)Check.True(!countdown.CompleteForFrame(i*.05+.01,i+1));
                }
                Check.True(countdown.CompleteForFrame(5.01,101));
                countdown.Reset();Check.Near(5,countdown.RemainingSeconds);
            });
            suite.Run("practice warning restarts after hidden or stalled frames and ignores stereo duplicates",()=>{
                var countdown=new VivePracticeCountdown();
                countdown.ObserveRendered(0,0);countdown.ObserveRendered(.2,1);
                countdown.ObserveRendered(.4,1);Check.Near(4.8,countdown.RemainingSeconds);
                countdown.ObserveRendered(.45,3);Check.Near(5,countdown.RemainingSeconds);
                countdown.ObserveRendered(.5,4);countdown.ObserveRendered(2,5);
                Check.Near(5,countdown.RemainingSeconds);
                countdown.ObserveRendered(double.NaN,6);Check.Near(5,countdown.RemainingSeconds);
                Check.True(!countdown.CompleteForFrame(double.PositiveInfinity,7));
            });
            suite.Run("all four block celebrations hold ratings and exclude their actual duration",()=>{
                var clock=new ManualClock();var task=Fixtures.New(clock);Fixtures.Practice(task,clock,8);
                for(int block=0;block<4;block++)
                {
                    Fixtures.Start(task,clock);
                    while(task.Phase==Phase.MainTask)Fixtures.Answer(task,clock);
                    Check.Equal(Phase.Questionnaire,task.Phase);
                    task.BeginBlockCelebration();int count=task.Events.Count;
                    task.BeginBlockCelebration();Check.Equal(count,task.Events.Count);
                    clock.Advance(8.2);task.Tick();Check.True(!task.QuestionnaireInputReady);
                    Check.Throws<InvalidOperationException>(()=>task.Rate(1));
                    task.EndBlockCelebration();count=task.Events.Count;
                    task.EndBlockCelebration();Check.Equal(count,task.Events.Count);
                    Check.Near(8.2,Double.Parse(task.Events.Last(x=>x.Kind=="block_celebration_completed").Fields["actual_elapsed_seconds"],CultureInfo.InvariantCulture));
                    for(int q=0;q<Questionnaire.Count;q++){clock.Advance(2);task.Rate(q+1);}
                    Check.Near(2*Questionnaire.Count,Double.Parse(task.Events.Last(x=>x.Kind=="questionnaire_completed").Fields["duration_seconds"],CultureInfo.InvariantCulture));
                    if(block<3){clock.Advance(task.Configuration.MinimumRestSeconds);Check.True(task.Ready());}
                }
                Check.Equal(4,task.Events.Count(x=>x.Kind=="block_celebration_started"));
                Check.Equal(4,task.Events.Count(x=>x.Kind=="block_celebration_completed"));
            });
        }
    }
}
