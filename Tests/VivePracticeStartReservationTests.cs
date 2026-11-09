using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Actual in-memory simulation task and application choice state with a
    // fake callback. These checks do not authorize or exercise real I/O.
    public static class VivePracticeStartReservationTests
    {
        sealed class Clock:IClock
        {
            internal double Value=10;internal Action OnNow,OnId;internal bool ThrowNow;
            public string ClockId {get{var action=OnId;OnId=null;if(action!=null)action();return "start-reservation-clock";}}
            public double Now {get{var action=OnNow;OnNow=null;if(action!=null)action();if(ThrowNow)throw new InvalidOperationException();return Value;}}
        }
        sealed class Fake:IVivePracticeStartFakeSink
        {
            internal int Calls,SimulatedPrefix;internal Action OnAccept;internal bool Result=true,Throw;
            internal VivePracticeStartBatch Last;
            public bool AcceptSynthetic(VivePracticeStartBatch batch)
            {
                Calls++;Last=batch;SimulatedPrefix++;
                if(OnAccept!=null)OnAccept();
                if(Throw)throw new InvalidOperationException("synthetic failure");
                return Result;
            }
        }
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;
            internal readonly VivePracticeStartBinding Binding;
            internal readonly VivePracticeStartReservation Reservation;
            internal readonly VivePracticeStartBinding.Ticket Ticket;
            internal Fixture()
            {
                var config=new SessionConfiguration("reservation-session",42,
                    new Allocation("reservation-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"reservation-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                Binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Clock.Value=11;Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;Check.True(Binding.TryAcquire(1,4096,out ticket));Ticket=ticket;
                Reservation=new VivePracticeStartReservation(Task,Clock,Choices,Binding);
            }
        }
        static void Resume(Fixture f)
        {
            f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
            f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);
        }
        static void RejectedBeforeFake(Fixture f,Fake fake)
        {
            Check.True(!f.Reservation.TryAccept(f.Ticket,fake));Check.Equal(0,fake.Calls);
            Check.Equal(VivePracticeStartReservationOutcome.Rejected,f.Reservation.Outcome);
        }
        static void Uncertain(Fixture f,Fake fake)
        {
            Check.True(!f.Reservation.TryAccept(f.Ticket,fake));Check.Equal(1,fake.Calls);
            Check.Equal(1,fake.SimulatedPrefix);Check.Equal(VivePracticeStartReservationOutcome.Uncertain,f.Reservation.Outcome);
            Check.True(!f.Reservation.CanWrite);Check.True(!f.Reservation.TryAccept(f.Ticket,fake));Check.Equal(1,fake.Calls);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("practice reservation accepts only the genuine start batch once in fake memory",()=>{
                var f=new Fixture();var fake=new Fake();Check.True(f.Reservation.TryAccept(f.Ticket,fake));
                Check.Equal(VivePracticeStartReservationOutcome.AcceptedByFake,f.Reservation.Outcome);Check.True(!f.Reservation.CanWrite);
                Check.Equal(1,fake.Calls);Check.Equal(3,fake.Last.Events.Count);Check.Equal("practice_started",fake.Last.Events[0].Kind);
                Check.Equal("phase_changed",fake.Last.Events[1].Kind);Check.Equal("trial_planned",fake.Last.Events[2].Kind);
                Check.Equal(f.Task.CurrentTrial.TrialId,fake.Last.Events[2].TrialId);
                Check.True(!f.Reservation.TryAccept(f.Ticket,fake));Check.Equal(1,fake.Calls);
            });
            suite.Run("practice reservation rejects withdrawal and pause before fake callback",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var f=new Fixture();if(mode==0)f.Choices.Withdraw();else if(mode==1)f.Task.ParticipantPause();else f.Task.ParticipantWithdraw();
                    RejectedBeforeFake(f,new Fake());
                }
            });
            suite.Run("practice reservation rejects clock callback withdrawal and task mutation before fake",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var f=new Fixture();if(mode==0)f.Clock.OnNow=f.Choices.Withdraw;
                    else if(mode==1)f.Clock.OnNow=f.Task.ParticipantPause;else f.Clock.OnId=f.Task.ParticipantWithdraw;
                    RejectedBeforeFake(f,new Fake());
                }
            });
            suite.Run("practice reservation detects same-state ABA before fake",()=>{
                var f=new Fixture();f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);
                RejectedBeforeFake(f,new Fake());
            });
            suite.Run("practice reservation treats callback withdrawal and task mutations as uncertain",()=>{
                for(int mode=0;mode<4;mode++)
                {
                    var f=new Fixture();var fake=new Fake();
                    if(mode==0)fake.OnAccept=f.Choices.Withdraw;
                    else if(mode==1)fake.OnAccept=f.Task.ParticipantPause;
                    else if(mode==2)fake.OnAccept=f.Task.ParticipantWithdraw;
                    else fake.OnAccept=f.Task.Tick; // even a no-op invalidates the old generation
                    Uncertain(f,fake);
                }
            });
            suite.Run("practice reservation rejects in-flight pause resume ABA",()=>{
                var f=new Fixture();var fake=new Fake();fake.OnAccept=()=>{f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);};
                Uncertain(f,fake);
            });
            suite.Run("practice reservation preserves uncertain fake prefix after failure or exception",()=>{
                for(int mode=0;mode<2;mode++)
                {var f=new Fixture();var fake=new Fake{Result=mode!=0,Throw=mode==1};Uncertain(f,fake);}
            });
            suite.Run("practice reservation callback reentry cannot commit",()=>{
                var f=new Fixture();var fake=new Fake();bool nested=true;
                fake.OnAccept=()=>nested=f.Reservation.TryAccept(f.Ticket,fake);Uncertain(f,fake);Check.True(!nested);
            });
            suite.Run("practice reservation allows concurrent revoke without holding a callback lock",()=>{
                var f=new Fixture();var fake=new Fake();bool joined=false;
                fake.OnAccept=()=>{var thread=new Thread(f.Choices.Withdraw){IsBackground=true};thread.Start();joined=thread.Join(5000);};
                Uncertain(f,fake);Check.True(joined&&f.Choices.Revoked);
            });
            suite.Run("practice reservation concurrent participant withdrawal invalidates choice without deadlock",()=>{
                var f=new Fixture();var fake=new Fake();bool joined=false,wrongThreadRejected=false;
                fake.OnAccept=()=>{
                    var thread=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(InvalidOperationException){wrongThreadRejected=true;}}){IsBackground=true};
                    thread.Start();joined=thread.Join(5000);
                };
                Uncertain(f,fake);Check.True(joined&&wrongThreadRejected&&f.Choices.Revoked);
                f.Task.ParticipantWithdraw();Check.Equal(Phase.Ended,f.Task.Phase);
            });
            suite.Run("practice reservation foreign thread cannot invoke fake and safety still works",()=>{
                var f=new Fixture();var fake=new Fake();bool result=true;
                var thread=new Thread(()=>result=f.Reservation.TryAccept(f.Ticket,fake)){IsBackground=true};thread.Start();Check.True(thread.Join(5000));
                Check.True(!result);Check.Equal(0,fake.Calls);Check.True(!f.Reservation.TryAccept(f.Ticket,fake));
                f.Task.ParticipantWithdraw();Check.Equal(Phase.Ended,f.Task.Phase);
            });
            suite.Run("practice reservation rejects participant or choice foreign thread fault before fake",()=>{
                for(int mode=0;mode<2;mode++)
                {
                    var f=new Fixture();var fake=new Fake();bool result=true;
                    var thread=new Thread(()=>{
                        try{if(mode==0)f.Task.Tick();else result=f.Choices.ConfirmEye();}
                        catch(InvalidOperationException){result=false;}
                    }){IsBackground=true};
                    thread.Start();Check.True(thread.Join(5000));
                    Check.True(!result);RejectedBeforeFake(f,fake);
                    f.Task.ParticipantWithdraw();Check.Equal(Phase.Ended,f.Task.Phase);
                }
            });
            suite.Run("practice reservation rejects a different object despite equal session and clock labels",()=>{
                var f=new Fixture();var other=new Fixture();bool rejected=false;
                try{new VivePracticeStartReservation(other.Task,f.Clock,f.Choices,f.Binding);}catch(ArgumentException){rejected=true;}
                Check.True(rejected);var fake=new Fake();Check.True(!f.Reservation.TryAccept(other.Ticket,fake));Check.Equal(0,fake.Calls);
                Check.True(f.Reservation.TryAccept(f.Ticket,fake));Check.Equal(1,fake.Calls);
            });
            suite.Run("practice reservation rejects invalid local clock before fake",()=>{
                for(int mode=0;mode<2;mode++)
                {var f=new Fixture();if(mode==0)f.Clock.Value=9;else f.Clock.ThrowNow=true;RejectedBeforeFake(f,new Fake());}
            });
        }
    }
}
