using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Application state only; these tests do not assert a person's consent.
    public static class VivePracticeConsentAuthorityTests
    {
        sealed class Clock:IClock
        {
            internal double Value=10;internal Action OnRead;internal bool ThrowTime,ThrowId;
            public string ClockId {get{if(ThrowId)throw new InvalidOperationException();return "consent-test-clock";}}
            public double Now {get{var hook=OnRead;OnRead=null;if(hook!=null)hook();if(ThrowTime)throw new InvalidOperationException();return Value;}}
        }
        static ViveDevelopmentParticipant Participant(Clock clock,bool observed=true)
        {
            var config=new SessionConfiguration("consent-test-session",42,
                new Allocation("consent-test-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"consent-test-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return observed?new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096):
                new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
        }
        static VivePreviewTrackpadSample Neutral(){return new VivePreviewTrackpadSample(true,false,0,0);}
        public static void Register(TestSuite suite)
        {
            suite.Run("practice consent requires two explicit distinct choices on observed participant",()=>{
                foreach(bool eyeFirst in new[]{false,true})
                {
                    var clock=new Clock();var p=Participant(clock);var a=new VivePracticeConsentAuthority(p,clock,true);
                    Check.True(!a.Active&&!a.CanWrite&&!a.ConsentAuthenticated);
                    Check.True(eyeFirst?a.ConfirmEye():a.ConfirmTask());Check.True(!a.Active);var first=a.Generation;
                    clock.Value=11;Check.True(eyeFirst?a.ConfirmTask():a.ConfirmEye());Check.True(a.Active&&a.Generation>first);
                    Check.Equal(11.0,a.EnabledAt);Check.True(a.IsCurrent(a.Generation,p,clock));
                    Check.True(!a.IsCurrent(a.Generation,Participant(clock),clock));Check.True(!a.IsCurrent(a.Generation,p,new Clock()));
                    Check.True(!(eyeFirst?a.ConfirmEye():a.ConfirmTask()));Check.True(a.Active);
                }
            });
            suite.Run("practice consent has no active proof without observation and refuses late choices",()=>{
                var c=new Clock();Check.Throws<InvalidOperationException>(()=>new VivePracticeConsentAuthority(Participant(c,false),c,true));
                var p=Participant(c);p.UpdateTrackpad(true,Neutral());c.Value=11;p.OwnerBeginPractice(Actor.Owner);
                var late=new VivePracticeConsentAuthority(p,c,true);Check.True(!late.ConfirmEye()&&late.Faulted&&!late.Active);
            });
            suite.Run("practice consent withdrawal invalidates generation permanently",()=>{
                var c=new Clock();var p=Participant(c);var a=new VivePracticeConsentAuthority(p,c,true);
                Check.True(a.ConfirmEye()&&a.ConfirmTask());long previous=a.Generation;
                var thread=new Thread(a.Withdraw);thread.Start();Check.True(thread.Join(5000));
                Check.True(a.Revoked&&!a.Active&&!a.IsCurrent(previous,p,c));Check.True(a.Generation>previous);
                Check.True(!a.ConfirmEye()&&!a.ConfirmTask()&&!a.CanWrite);
            });
            suite.Run("practice consent fails closed on clock rollback and exceptions",()=>{
                var c=new Clock();var p=Participant(c);var a=new VivePracticeConsentAuthority(p,c,true);
                Check.True(a.ConfirmEye());c.Value=9;Check.True(!a.ConfirmTask()&&a.Faulted&&!a.Active);
                var c2=new Clock();var p2=Participant(c2);var b=new VivePracticeConsentAuthority(p2,c2,true);
                Check.True(b.ConfirmEye());c2.ThrowId=true;Check.True(!b.ConfirmTask()&&b.Faulted);
                var c3=new Clock();var p3=Participant(c3);var d=new VivePracticeConsentAuthority(p3,c3,true);
                Check.True(d.ConfirmEye());c3.ThrowTime=true;Check.True(!d.ConfirmTask()&&d.Faulted);
            });
            suite.Run("practice consent callback withdrawal and reentry cannot grant",()=>{
                var c=new Clock();var p=Participant(c);var a=new VivePracticeConsentAuthority(p,c,true);
                Check.True(a.ConfirmEye());c.OnRead=a.Withdraw;Check.True(!a.ConfirmTask()&&a.Revoked&&!a.Active);
                var c2=new Clock();var p2=Participant(c2);var b=new VivePracticeConsentAuthority(p2,c2,true);
                c2.OnRead=()=>b.ConfirmTask();Check.True(!b.ConfirmEye()&&b.Faulted&&!b.Active);
            });
            suite.Run("practice consent fails on task mutation in choice callback",()=>{
                var c=new Clock();var p=Participant(c);var a=new VivePracticeConsentAuthority(p,c,true);
                c.OnRead=()=>p.UpdateTrackpad(true,Neutral());Check.True(!a.ConfirmEye()&&a.Faulted&&!a.Active);
            });
            suite.Run("practice consent foreign thread cannot grant",()=>{
                var c=new Clock();var p=Participant(c);var a=new VivePracticeConsentAuthority(p,c,true);bool result=true;
                var thread=new Thread(()=>result=a.ConfirmEye());thread.Start();Check.True(thread.Join(5000));
                Check.True(!result&&a.Faulted&&!a.Active&&!a.ConfirmTask());
            });
            suite.Run("practice consent withdrawal and generation share one serialized state",()=>{
                var c=new Clock();var p=Participant(c);var a=new VivePracticeConsentAuthority(p,c,true);
                Check.True(a.ConfirmEye()&&a.ConfirmTask());long before=a.Generation;
                var field=typeof(VivePracticeConsentAuthority).GetField("stateGate",BindingFlags.Instance|BindingFlags.NonPublic);
                Check.True(field!=null);object gate=field.GetValue(a);var started=new ManualResetEvent(false);var done=new ManualResetEvent(false);
                Thread worker=null;
                lock(gate)
                {
                    worker=new Thread(()=>{started.Set();a.Withdraw();done.Set();});worker.Start();
                    Check.True(started.WaitOne(5000));Check.True(!done.WaitOne(20));
                    Check.True(a.IsCurrent(before,p,c)&&a.Active,"withdrawal has not acquired its linearization lock");
                }
                Check.True(done.WaitOne(5000)&&worker.Join(5000));Check.True(a.Revoked&&!a.Active&&!a.IsCurrent(before,p,c));
                Check.True(a.Generation>before);
            });
            suite.Run("participant withdrawal revokes its attached application choices",()=>{
                var c=new Clock();var p=Participant(c);var a=new VivePracticeConsentAuthority(p,c,true);
                Check.True(a.ConfirmEye()&&a.ConfirmTask());p.ParticipantWithdraw();
                Check.True(!a.Active&&a.Revoked);
            });
        }
    }
}
