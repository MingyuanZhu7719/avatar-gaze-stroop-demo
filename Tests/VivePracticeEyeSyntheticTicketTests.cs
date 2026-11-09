using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Sealed synthetic feed and Simulation participant only. No file, runtime,
    // network or headset; issued markers are not eye observations or permits.
    public static class VivePracticeEyeSyntheticTicketTests
    {
        sealed class Clock:IClock
        {
            public double Value=10;public Action Hook;
            public string ClockId {get{return "synthetic-ticket-clock";}}
            public double Now {get{var h=Hook;Hook=null;if(h!=null)h();return Value;}}
        }
        sealed class Fixture:IDisposable
        {
            internal readonly Clock Clock=new Clock();
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;
            internal readonly VivePracticeEyeFencedSource Source;
            internal readonly VivePracticeSyntheticFrameFeed Feed;
            internal readonly VivePracticeContextV3 Context;
            internal Fixture()
            {
                var allocation=new Allocation("ticket-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var config=new SessionConfiguration("ticket-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"ticket-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                VivePracticeSyntheticFrameFeed feed;
                Source=VivePracticeEyeFencedSource.CreateSealedSyntheticTicketSource(Task,Clock,Choices,new ViveProEyeRehearsalCoordinates(),.2,out feed);
                Feed=feed;Clock.Value=11;Task.OwnerBeginPractice(Actor.Owner);
                Context=new VivePracticeContextV3(Task.SessionId,"synthetic","synthetic-stream","synthetic-device",Clock.ClockId,
                    new ViveProEyeRehearsalCoordinates().Version,1,"synthetic-epoch",0,null,VivePracticeEpochReasonV3.Initial,11);
                Check.True(Source.BindPractice(Context));
            }
            internal VivePracticeSyntheticTicket Issue(byte marker=42)
            {
                Clock.Value=11.11;Feed.SetFrame(1,1,11.1,1,11.1,marker);VivePracticeSyntheticTicket t;
                Check.True(Source.TryIssueSyntheticTicket(out t)&&t!=null);return t;
            }
            public void Dispose(){Source.Dispose();}
        }
        static bool Consume(VivePracticeEyeFencedSource source,VivePracticeSyntheticTicket ticket,out byte marker)
        {return source.TryConsumeSyntheticTicket(ticket,ticket.Snapshot,ticket.Revision,ticket.TraceCount,out marker);}
        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic eye ticket binds checkpoint and consumes only once",()=>{
                using(var f=new Fixture())
                {
                    var ticket=f.Issue();Check.True(Object.ReferenceEquals(f.Context,ticket.Context));Check.Equal(.2,ticket.MaximumAgeSeconds);byte marker;Check.True(Consume(f.Source,ticket,out marker));Check.Equal((byte)42,marker);
                    Check.True(!Consume(f.Source,ticket,out marker));
                }
            });
            suite.Run("synthetic eye ticket rejects other checkpoint and replay",()=>{
                using(var f=new Fixture())
                {
                    var ticket=f.Issue();byte marker;
                    Check.True(!f.Source.TryConsumeSyntheticTicket(ticket,ticket.Snapshot,ticket.Revision+1,ticket.TraceCount,out marker));
                    Check.True(!Consume(f.Source,ticket,out marker));
                }
            });
            suite.Run("synthetic eye ticket rejects task mutation after issue",()=>{
                using(var f=new Fixture())
                {
                    var ticket=f.Issue();f.Clock.Value=11.12;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);byte marker;
                    Check.True(!Consume(f.Source,ticket,out marker));Check.True(!Consume(f.Source,ticket,out marker));
                }
            });
            suite.Run("synthetic eye ticket rejects revoked choices and stale candidate",()=>{
                using(var f=new Fixture()){var t=f.Issue();f.Choices.Withdraw();byte m;Check.True(!Consume(f.Source,t,out m));}
                using(var f=new Fixture()){var t=f.Issue();f.Clock.Value=12;byte m;Check.True(!Consume(f.Source,t,out m));}
            });
            suite.Run("synthetic eye ticket rejects other source and foreign thread",()=>{
                using(var a=new Fixture())using(var b=new Fixture())
                {var t=a.Issue();byte m;Check.True(!Consume(b.Source,t,out m));Check.True(!Consume(a.Source,t,out m));}
                using(var f=new Fixture())
                {var t=f.Issue();bool result=true;var thread=new Thread(()=>{byte m;result=Consume(f.Source,t,out m);});
                    thread.Start();Check.True(thread.Join(5000));Check.True(!result);byte marker;Check.True(!Consume(f.Source,t,out marker));}
            });
            suite.Run("synthetic eye ticket refuses candidate after intervening read",()=>{
                using(var f=new Fixture())
                {
                    var ticket=f.Issue();f.Clock.Value=11.12;f.Feed.SetFrame(2,2,11.11,2,11.11,7);
                    VivePracticeEyeCandidate other;Check.True(f.Source.TryRead(out other));byte marker;Check.True(!Consume(f.Source,ticket,out marker));
                }
            });
            suite.Run("synthetic eye ticket refuses task mutation inside clock callback",()=>{
                using(var f=new Fixture())
                {
                    var ticket=f.Issue();f.Clock.Hook=()=>f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
                    byte marker;Check.True(!Consume(f.Source,ticket,out marker));
                }
            });
            suite.Run("synthetic ticket context is exact instance not matching declared IDs",()=>{
                using(var f=new Fixture())
                {
                    var t=f.Issue();var copy=new VivePracticeContextV3(f.Context.SessionId,f.Context.ParticipantId,f.Context.StreamId,
                        f.Context.DeviceId,f.Context.ClockId,f.Context.MappingVersion,f.Context.PracticeAttempt,f.Context.RecordingEpochId,
                        f.Context.RecoveryOrdinal,f.Context.PreviousEpochId,f.Context.EpochReason,f.Context.ObservedPracticeStartLocalSeconds);
                    Check.True(VivePracticeMultiCodec.SameContext(f.Context,copy));Check.True(!Object.ReferenceEquals(t.Context,copy));
                    byte marker;Check.True(Consume(f.Source,t,out marker));
                }
            });
            suite.Run("synthetic ticket issuance fails closed after reentry",()=>{
                using(var f=new Fixture())
                {
                    f.Clock.Value=11.11;f.Feed.SetFrame(1,1,11.1,1,11.1,42);
                    f.Clock.Hook=()=>{VivePracticeSyntheticTicket nested;Check.True(!f.Source.TryIssueSyntheticTicket(out nested));};
                    VivePracticeSyntheticTicket ticket;Check.True(!f.Source.TryIssueSyntheticTicket(out ticket)&&ticket==null);
                }
            });
            suite.Run("synthetic ticket consumption fails closed after same-ticket reentry",()=>{
                using(var f=new Fixture())
                {
                    var t=f.Issue();f.Clock.Hook=()=>{byte nested;Check.True(!Consume(f.Source,t,out nested));};
                    byte marker;Check.True(!Consume(f.Source,t,out marker));
                }
            });
            suite.Run("consumed synthetic ticket stays current only under source gate and until any next read",()=>{
                using(var f=new Fixture())
                {
                    var t=f.Issue();byte marker;Check.True(Consume(f.Source,t,out marker));
                    Check.True(!f.Source.ConsumedTicketStillCurrent(t));
                    lock(f.Source.SyntheticTicketGate)Check.True(f.Source.ConsumedTicketStillCurrent(t));
                    VivePracticeEyeCandidate next;Check.True(!f.Source.TryRead(out next)); // same frame is duplicate
                    lock(f.Source.SyntheticTicketGate)Check.True(!f.Source.ConsumedTicketStillCurrent(t));
                }
            });
            suite.Run("consumed synthetic ticket becomes invalid on dispose",()=>{
                var f=new Fixture();var t=f.Issue();byte marker;Check.True(Consume(f.Source,t,out marker));
                f.Source.Dispose();lock(f.Source.SyntheticTicketGate)Check.True(!f.Source.ConsumedTicketStillCurrent(t));
            });
            suite.Run("source gate orders foreign invalidation after a fake publication check",()=>{
                using(var f=new Fixture())
                {
                    var t=f.Issue();byte marker;Check.True(Consume(f.Source,t,out marker));
                    var started=new ManualResetEvent(false);var finished=new ManualResetEvent(false);
                    Thread other;
                    lock(f.Source.SyntheticTicketGate)
                    {
                        other=new Thread(()=>{started.Set();VivePracticeSyntheticTicket ignored;f.Source.TryIssueSyntheticTicket(out ignored);finished.Set();});
                        other.Start();Check.True(started.WaitOne(5000));Check.True(!finished.WaitOne(20));
                        Check.True(f.Source.ConsumedTicketStillCurrent(t));
                    }
                    Check.True(finished.WaitOne(5000)&&other.Join(5000));
                    lock(f.Source.SyntheticTicketGate)Check.True(!f.Source.ConsumedTicketStillCurrent(t));
                }
            });
        }
    }
}
