using System;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class Check
    {
        public static void True(bool value, string message="Expected true") { if(!value) throw new Exception(message); }
        public static void Equal<T>(T expected, T actual, string message="")
        { if(!Object.Equals(expected,actual)) throw new Exception(message+" Expected "+expected+", actual "+actual); }
        public static void Near(double expected,double actual,double tolerance=0.000001)
        { if(Double.IsNaN(actual) || Math.Abs(expected-actual)>tolerance) throw new Exception("Expected "+expected+", actual "+actual); }
        public static void Throws<T>(Action action) where T:Exception
        { try { action(); } catch(T) { return; } throw new Exception("Expected "+typeof(T).Name); }
    }
    public sealed class TestSuite
    {
        public int Passed, Failed;
        public void Run(string name, Action test)
        {
            try { test(); Passed++; Console.WriteLine("PASS "+name); }
            catch(Exception error) { Failed++; Console.WriteLine("FAIL "+name+"\n"+error); }
        }
    }
    public sealed class ManualClock : IClock
    {
        public double Now {get;set;}
        public string ClockId {get{return "test-monotonic";}}
        public void Advance(double seconds) {Now+=seconds;}
    }
    public static class Fixtures
    {
        public static Experiment New(ManualClock clock,Condition first=Condition.A, RunKind runKind=RunKind.Simulation)
        {
            string order=first==Condition.A?"ABDC":first==Condition.B?"BCAD":first==Condition.C?"CDBA":"DACB";
            var conditions=Array.ConvertAll(order.ToCharArray(),x=>(Condition)Enum.Parse(typeof(Condition),x.ToString()));
            return new Experiment(new SessionConfiguration("test-session",42,new Allocation("test-allocation",conditions,GazeTarget.Participant,new[]{0,1,2,3}),runKind,"en",1),clock);
        }
        public static void Practice(Experiment e,ManualClock c,int correct)
        {
            if(e.Phase==Phase.Preparation) e.BeginFamiliarization(Actor.Owner);
            e.BeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {
                e.MarkPresented(e.CurrentTrial.TrialId); c.Advance(.4);
                Colour ink=e.CurrentTrial.Stimulus.Ink;
                Colour answer=i<correct?ink:(Colour)(((int)ink+1)%4);
                Check.True(e.ButtonChanged(answer,true)); e.ButtonChanged(answer,false);
                if(e.Phase==Phase.PracticeCorrection) e.ContinuePractice();
            }
        }
        public static void Start(Experiment e,ManualClock c,ControlSelection source=ControlSelection.Automatic)
        {
            e.LockBlock(Actor.Owner,source,"test-owner"); e.BeginCountdown(Actor.Owner,true);
            c.Advance(1); e.Tick(); Check.Equal(Phase.MainTask,e.Phase);
        }
        public static void Answer(Experiment e,ManualClock c,bool correct=true)
        {
            e.MarkPresented(e.CurrentTrial.TrialId); c.Advance(.2);
            var answer=correct?e.CurrentTrial.Stimulus.Ink:(Colour)(((int)e.CurrentTrial.Stimulus.Ink+1)%4);
            Check.True(e.ButtonChanged(answer,true)); e.ButtonChanged(answer,false);
        }
        public static void Ratings(Experiment e,ManualClock c)
        {for(int i=0;i<5;i++){c.Advance(2);e.Rate(i+1);}}
    }
    public static class Program
    {
        public static int Main(string[] args)
        {
            var suite=new TestSuite(); TrialPlanTests.Register(suite); ExperimentTests.Register(suite);
            Console.WriteLine("RESULT "+suite.Passed+" passed; "+suite.Failed+" failed");
            return suite.Failed==0?0:1;
        }
    }
}
