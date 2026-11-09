using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Actual in-memory Simulation participant and application choice state;
    // no real consent, provider, headset, writer, file or network.
    public static class VivePracticeStartBindingTests
    {
        sealed class Clock:IClock
        {
            public double Value=10;public Action OnId,OnNow;public bool BrokenId,BrokenNow;
            public string ClockId {get{var a=OnId;OnId=null;if(a!=null)a();if(BrokenId)throw new InvalidOperationException();return "start-binding-clock";}}
            public double Now {get{var a=OnNow;OnNow=null;if(a!=null)a();if(BrokenNow)throw new InvalidOperationException();return Value;}}
        }
        sealed class Fixture
        {
            public readonly Clock Clock;public readonly ViveDevelopmentParticipant Task;
            public readonly VivePracticeConsentAuthority Choices;public readonly VivePracticeStartBinding Binding;
            public Fixture(double start=11)
            {
                Clock=new Clock();Task=NewParticipant(Clock);Choices=new VivePracticeConsentAuthority(Task,Clock,true);
                Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());Binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Clock.Value=start;Task.OwnerBeginPractice(Actor.Owner);
            }
            public VivePracticeStartBinding.Ticket Acquire()
            {VivePracticeStartBinding.Ticket ticket;Check.True(Binding.TryAcquire(1,4096,out ticket));Check.True(ticket!=null);return ticket;}
        }
        static ViveDevelopmentParticipant NewParticipant(Clock clock)
        {
            var config=new SessionConfiguration("start-binding-session",42,
                new Allocation("start-binding-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"start-binding-geometry");
            var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(config,clock,geometry,mapping,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
        }
        static bool Rejected(Action action){try{action();return false;}catch(Exception){return true;}}
        static void ResumePractice(Fixture f)
        {
            f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
            f.Task.UpdateAuxiliary(false,false,false,0,true);
            f.Task.UpdateAuxiliary(true,false,false,0,true);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("practice start binding requires both choices and actual same task and clock",()=>{
                var c=new Clock();var task=NewParticipant(c);var choices=new VivePracticeConsentAuthority(task,c,true);
                Check.True(Rejected(()=>new VivePracticeStartBinding(task,c,choices,true)));
                Check.True(choices.ConfirmEye());Check.True(Rejected(()=>new VivePracticeStartBinding(task,c,choices,true)));
                Check.True(choices.ConfirmTask());var other=NewParticipant(c);var otherClock=new Clock();
                Check.True(Rejected(()=>new VivePracticeStartBinding(other,c,choices,true)));
                Check.True(Rejected(()=>new VivePracticeStartBinding(task,otherClock,choices,true)));
                Check.True(Rejected(()=>new VivePracticeStartBinding(task,c,choices,false)));
            });
            suite.Run("practice start binding requires start strictly after choices",()=>{
                var f=new Fixture(10);VivePracticeStartBinding.Ticket ticket;
                Check.True(!f.Binding.TryAcquire(1,4096,out ticket)&&ticket==null);Check.True(!f.Binding.CanWrite&&!f.Binding.ConsentAuthenticated);
            });
            suite.Run("practice start binding hands actual three same-tick events once",()=>{
                var f=new Fixture();var ticket=f.Acquire();VivePracticeStartBatch batch;
                Check.True(f.Binding.TryTake(ticket,out batch));Check.True(batch!=null&&!batch.CanWrite);
                Check.Equal(3,batch.Events.Count);Check.Equal("practice_started",batch.Events[0].Kind);
                Check.Equal("phase_changed",batch.Events[1].Kind);Check.Equal("trial_planned",batch.Events[2].Kind);
                for(int i=0;i<3;i++){Check.Equal(11.0,batch.Events[i].Time);if(i>0)Check.Equal(batch.Events[i-1].Sequence+1,batch.Events[i].Sequence);}
                Check.Equal(f.Task.CurrentTrial.TrialId,batch.Events[2].TrialId);
                VivePracticeStartBatch again;Check.True(!f.Binding.TryTake(ticket,out again)&&again==null);
                VivePracticeStartBinding.Ticket next;Check.True(!f.Binding.TryAcquire(1,4096,out next)&&next==null);
            });
            suite.Run("practice start revision cannot be handed out by another binding",()=>{
                var f=new Fixture();var second=new VivePracticeStartBinding(f.Task,f.Clock,f.Choices,true);
                var first=f.Acquire();VivePracticeStartBatch batch;Check.True(f.Binding.TryTake(first,out batch));
                VivePracticeStartBinding.Ticket duplicate;
                Check.True(!second.TryAcquire(1,4096,out duplicate)&&duplicate==null);
                var third=new VivePracticeStartBinding(f.Task,f.Clock,f.Choices,true);
                Check.True(!third.TryAcquire(1,4096,out duplicate)&&duplicate==null);
            });
            suite.Run("practice start binding rejects withdrawal before or within clock callback",()=>{
                for(int mode=0;mode<2;mode++)
                {
                    var f=new Fixture();var ticket=f.Acquire();if(mode==0)f.Choices.Withdraw();else f.Clock.OnNow=f.Choices.Withdraw;
                    VivePracticeStartBatch batch;Check.True(!f.Binding.TryTake(ticket,out batch)&&batch==null);Check.True(f.Choices.Revoked);
                    VivePracticeStartBinding.Ticket retry;Check.True(!f.Binding.TryAcquire(1,4096,out retry));
                }
                var g=new Fixture();g.Clock.OnId=g.Choices.Withdraw;VivePracticeStartBinding.Ticket issued;
                Check.True(!g.Binding.TryAcquire(1,4096,out issued)&&issued==null&&g.Choices.Revoked);
            });
            suite.Run("practice start binding rejects task pause and withdraw after ticket",()=>{
                for(int mode=0;mode<2;mode++)
                {
                    var f=new Fixture();var ticket=f.Acquire();if(mode==0)f.Task.ParticipantPause();else f.Task.ParticipantWithdraw();
                    VivePracticeStartBatch batch;Check.True(!f.Binding.TryTake(ticket,out batch)&&batch==null);
                }
            });
            suite.Run("practice start binding rejects pause resume ABA and task mutation callbacks",()=>{
                var f=new Fixture();var ticket=f.Acquire();f.Task.ParticipantPause();ResumePractice(f);Check.Equal(Phase.Practice,f.Task.Phase);
                VivePracticeStartBatch batch;Check.True(!f.Binding.TryTake(ticket,out batch)&&batch==null);
                var g=new Fixture();var t=g.Acquire();g.Clock.OnNow=g.Task.ParticipantWithdraw;
                Check.True(!g.Binding.TryTake(t,out batch)&&batch==null);Check.Equal(Phase.Ended,g.Task.Phase);
            });
            suite.Run("practice start binding refuses foreign ticket and releases on close",()=>{
                var f=new Fixture();var other=new VivePracticeStartBinding(f.Task,f.Clock,f.Choices,true);var ticket=f.Acquire();VivePracticeStartBatch batch;
                Check.True(!other.TryTake(ticket,out batch)&&batch==null);f.Binding.Close();
                Check.True(!f.Binding.TryTake(ticket,out batch));ViveDevelopmentParticipant.PracticeObservationFence fence;
                Check.True(f.Task.TryAcquirePracticeObservationFence(f.Clock,out fence));f.Task.AbandonPracticeObservationFence(fence);
                VivePracticeStartBinding.Ticket retry;Check.True(!other.TryAcquire(1,4096,out retry)&&retry==null);
            });
            suite.Run("practice start binding foreign thread fails closed then safety remains",()=>{
                var f=new Fixture();var ticket=f.Acquire();bool ok=true;VivePracticeStartBatch result=null;
                var thread=new Thread(()=>ok=f.Binding.TryTake(ticket,out result));thread.Start();Check.True(thread.Join(5000));
                Check.True(!ok&&result==null&&f.Binding.Faulted);Check.True(!f.Binding.TryTake(ticket,out result));
                f.Task.ParticipantWithdraw();Check.Equal(Phase.Ended,f.Task.Phase);
            });
            suite.Run("practice start reservation from foreign thread faults participant observation",()=>{
                var f=new Fixture();ViveDevelopmentParticipant.PracticeObservationFence fence;
                Check.True(f.Task.TryAcquirePracticeObservationFence(f.Clock,out fence));bool reserved=true;
                var thread=new Thread(()=>reserved=f.Task.TryReservePracticeStartRevision(fence,fence.Revision));
                thread.Start();Check.True(thread.Join(5000));Check.True(!reserved&&f.Task.ObservationFaulted);
                f.Task.ParticipantWithdraw();Check.Equal(Phase.Ended,f.Task.Phase);
            });
            suite.Run("practice start binding clock errors or rollback consume ticket and fault",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var f=new Fixture();var ticket=f.Acquire();if(mode==0)f.Clock.BrokenId=true;else if(mode==1)f.Clock.BrokenNow=true;else f.Clock.Value=9;
                    VivePracticeStartBatch batch;Check.True(!f.Binding.TryTake(ticket,out batch)&&batch==null);
                    Check.True(f.Binding.Faulted);VivePracticeStartBinding.Ticket next;
                    Check.True(!f.Binding.TryAcquire(1,4096,out next));
                }
            });
            suite.Run("practice start binding reentrant take faults without returning batch",()=>{
                var f=new Fixture();var ticket=f.Acquire();VivePracticeStartBatch inner=null,outer;
                f.Clock.OnNow=()=>f.Binding.TryTake(ticket,out inner);
                Check.True(!f.Binding.TryTake(ticket,out outer)&&outer==null&&inner==null&&f.Binding.Faulted);
            });
        }
    }
}
