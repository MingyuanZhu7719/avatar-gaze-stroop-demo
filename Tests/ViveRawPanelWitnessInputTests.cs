using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // This fixture attaches a synthetic admission and deliberately drives the
    // experiment directly to the target phase. It never validates plan receipts,
    // actual rendering, physical input, file output or participant consent.
    public static class ViveRawPanelWitnessInputTests
    {
        sealed class Client:IViveProEyeClient
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "synthetic";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Entered.Set();Release.WaitOne();return null;}
            public void Dispose(){}
        }
        // Only the test injects this clock into the private Engine field. It
        // simulates a first successful read followed by a backward Log read.
        sealed class SecondReadRollbackClock:IClock
        {
            int reads;
            public string ClockId {get{return "synthetic-panel-clock";}}
            public double Now {get{return Interlocked.Increment(ref reads)==1?1000000:999999;}}
        }
        sealed class Fixture:IDisposable
        {
            internal readonly Client Source=new Client();internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;internal readonly ViveNamedSessionFrameAdmission Admission;internal readonly Experiment Engine;
            internal ViveRawByteBudget Budget;
            internal Fixture(int capacity=32,long budgetBytes=0)
            {
                var clock=new MonotonicClock("synthetic-panel-clock");
                Worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>Source);
                Check.True(Worker.Start()&&Source.Entered.WaitOne(3000));
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"panel-test-geometry");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"panel-test-plan",option);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"panel-test-intent",true,true);
                var config=SessionConfiguration.NamedRecordingDevelopment("panel-test-session",37,plan,intent);
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"panel-test-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
                Admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(Task,Worker,clock,capacity);
                if(budgetBytes>0)
                {
                    Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());
                    Budget=ViveRawByteBudget.SelectExplicit(budgetBytes);Check.True(Admission.TryBindQueueBudget(Budget));
                }
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Engine=(Experiment)typeof(ViveDevelopmentParticipant).GetField("experiment",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Task);
                Engine.PrepareNamedRecordingBlockPlan(Actor.Owner,true);Engine.BeginFamiliarization(Actor.Owner);Engine.BeginPractice(Actor.Owner);
            }
            internal void Aux(bool confirm,bool pause,bool withdraw,bool visible)
            {Task.UpdateAuxiliary(confirm,pause,withdraw,0,visible);}
            internal void Correction()
            {
                var t=Engine.CurrentTrial;Engine.MarkPresented(t.TrialId);
                var wrong=(Colour)(((int)t.Stimulus.Ink+1)%4);Check.True(Engine.ButtonChanged(wrong,true));Engine.ButtonChanged(wrong,false);
                Check.Equal(Phase.PracticeCorrection,Engine.Phase);
            }
            internal void CountdownWithoutReceiptForFixture()
            {
                while(Engine.Phase==Phase.Practice)
                {var t=Engine.CurrentTrial;Engine.MarkPresented(t.TrialId);Check.True(Engine.ButtonChanged(t.Stimulus.Ink,true));Engine.ButtonChanged(t.Stimulus.Ink,false);}
                Engine.ConfirmNamedPlan(Actor.Owner,"synthetic-owner");Engine.PrepareNamedRecordingBlockPlan(Actor.Owner,false);Engine.BeginCountdown(Actor.Owner,true);
            }
            // Drain real admission FIFO through its synthetic acknowledgement.
            // It proves neither a writer receipt nor actual file durability.
            internal void DrainTaskEvents(bool leaveFinal=false)
            {
                while(true)
                {
                    bool staged;Check.True(Admission.TryStageNextTaskEvent(out staged));
                    if(!staged)return;
                    if(leaveFinal&&Engine.Events[Engine.Events.Count-1].Sequence==
                        ((JournalEvent)PeekOnly()).Sequence)return;
                    ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                    Check.True(Admission.TryReserveConsumer(out receipt)&&receipt!=null);
                    var item=(JournalEvent)receipt.Record;
                    var manifest=(ViveNamedSessionFrameAdmission.Manifest)typeof(ViveNamedSessionFrameAdmission).GetField("manifest",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Admission);
                    int header=receipt.Ordinal==0?ViveRawSessionJournalCodec.ManifestPayloadV3(manifest).Length+48:0;
                    int bytes=ViveRawSessionJournalCodec.RecordPayload(receipt.Ordinal+1,item,manifest).Length+40+header;
                    Check.True(Admission.VerifyQueuedFrameBudget(receipt,item,Budget,bytes,header));
                    Check.True(Admission.TryAcknowledgeSyntheticForTest(receipt));
                }
            }
            object PeekOnly()
            {
                // Tests only: inspect the queue head without claiming it.
                var ring=(object[])typeof(ViveNamedSessionFrameAdmission).GetField("ring",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Admission);
                var ordinal=(long)typeof(ViveNamedSessionFrameAdmission).GetField("firstOrdinal",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Admission);
                return ring[(int)(ordinal%ring.Length)];
            }
            public void Dispose(){Admission.Withdraw();Worker.TryRequestStop();Source.Release.Set();Check.True(Worker.Stop(1000));Worker.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("named raw correction requires witnessed panel and a fresh confirm edge",()=>{
                using(var f=new Fixture())
                {
                    f.Correction();f.Aux(false,false,false,false);f.Aux(true,false,false,false);Check.Equal(Phase.PracticeCorrection,f.Task.Phase);
                    f.Aux(true,false,false,true);f.Aux(true,false,false,true);Check.Equal(Phase.PracticeCorrection,f.Task.Phase);
                    f.Aux(false,false,false,true);f.Aux(true,false,false,true);Check.Equal(Phase.Practice,f.Task.Phase);
                }
            });
            suite.Run("named raw paused resume requires witnessed panel and a fresh confirm edge",()=>{
                using(var f=new Fixture())
                {
                    f.Engine.Pause(Actor.Participant);Check.Equal(Phase.Paused,f.Task.Phase);
                    f.Aux(false,false,false,false);f.Aux(true,false,false,false);Check.Equal(Phase.Paused,f.Task.Phase);
                    f.Aux(true,false,false,true);f.Aux(true,false,false,true);Check.Equal(Phase.Paused,f.Task.Phase);
                    f.Aux(false,false,false,true);f.Aux(true,false,false,true);Check.Equal(Phase.Practice,f.Task.Phase);
                }
            });
            suite.Run("named raw panel witness never blocks participant safety pause",()=>{
                using(var f=new Fixture())
                {f.Aux(false,false,false,false);f.Aux(false,true,false,false);Check.Equal(Phase.Paused,f.Task.Phase);}
            });
            suite.Run("named raw panel witness never blocks participant withdrawal",()=>{
                using(var f=new Fixture())
                {f.Aux(false,false,false,false);f.Aux(false,false,true,false);Check.Equal(Phase.Ended,f.Task.Phase);Check.Equal((EndReason?)EndReason.Withdrawn,f.Task.EndReason);}
            });
            suite.Run("named raw pause is not lost on the first panel boundary",()=>{
                using(var f=new Fixture())
                {f.Aux(false,true,false,false);Check.Equal(Phase.Paused,f.Task.Phase);}
            });
            suite.Run("named raw withdrawal is not lost on the first panel boundary",()=>{
                using(var f=new Fixture())
                {f.Aux(false,false,true,false);Check.Equal(Phase.Ended,f.Task.Phase);Check.Equal((EndReason?)EndReason.Withdrawn,f.Task.EndReason);}
            });
            suite.Run("attached named raw countdown accepts only one valid software witness",()=>{
                using(var f=new Fixture())
                {
                    f.CountdownWithoutReceiptForFixture();Check.Equal(Phase.Countdown,f.Task.Phase);Check.True(f.Task.NamedRawCountdownAwaitingSoftwareWitness);
                    Check.True(!f.Task.TryStartNamedRawCountdownFromSoftwareWitness(-1,1,1));Check.True(f.Task.NamedRawCountdownAwaitingSoftwareWitness);
                    Check.True(f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10,1,1));Check.True(!f.Task.NamedRawCountdownAwaitingSoftwareWitness);
                    Check.True(!f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10,1,1));
                }
            });
            suite.Run("budgeted raw countdown reserves and enqueues its real witness once",()=>{
                using(var f=new Fixture(8,8*1024*1024))
                {
                    f.CountdownWithoutReceiptForFixture();f.DrainTaskEvents();
                    int before=f.Engine.Events.Count;Check.True(f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10,1,1));
                    Check.Equal(before+1,f.Engine.Events.Count);Check.Equal(1,f.Admission.Count);
                    ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                    Check.True(f.Admission.TryReserveConsumer(out receipt)&&receipt!=null);
                    var item=receipt.Record as JournalEvent;Check.True(item!=null);Check.Equal("countdown_software_witness",item.Kind);
                    var manifest=(ViveNamedSessionFrameAdmission.Manifest)typeof(ViveNamedSessionFrameAdmission).GetField("manifest",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f.Admission);
                    int frameBytes=ViveRawSessionJournalCodec.RecordPayload(receipt.Ordinal+1,item,manifest).Length+40;
                    Check.True(f.Admission.VerifyQueuedFrameBudget(receipt,item,f.Budget,frameBytes,0));
                    Check.True(!f.Admission.VerifyQueuedFrameBudget(receipt,item,f.Budget,frameBytes+1,0));
                    Check.True(f.Admission.TryAcknowledgeSyntheticForTest(receipt));
                    Check.True(!f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10,1,1));
                }
            });
            suite.Run("budgeted raw countdown charges each pause and resumed witness",()=>{
                using(var f=new Fixture(8,16*1024*1024))
                {
                    f.CountdownWithoutReceiptForFixture();f.DrainTaskEvents();
                    for(int i=0;i<4;i++)
                    {
                        Check.True(f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10+i,1+i,1+i));
                        ViveNamedSessionFrameAdmission.ConsumerReceipt witnessReceipt;
                        Check.True(f.Admission.TryReserveConsumer(out witnessReceipt)&&witnessReceipt!=null&&
                            ((JournalEvent)witnessReceipt.Record).Kind=="countdown_software_witness");
                        Check.True(f.Admission.TryAcknowledgeSyntheticForTest(witnessReceipt));f.DrainTaskEvents();
                        if(i<3){f.Engine.Pause(Actor.Participant);f.Engine.Resume(true);f.DrainTaskEvents();}
                    }
                    int waiting=0,witness=0;foreach(var item in f.Engine.Events)
                    {if(item.Kind=="countdown_waiting_for_software_witness")waiting++;if(item.Kind=="countdown_software_witness")witness++;}
                    Check.Equal(4,waiting);Check.Equal(4,witness);Check.Equal(0,f.Admission.Count);
                }
            });
            suite.Run("full FIFO rejects raw countdown before mutation while withdrawal remains available",()=>{
                using(var f=new Fixture(1,8*1024*1024))
                {
                    f.CountdownWithoutReceiptForFixture();f.DrainTaskEvents(true);
                    int before=f.Engine.Events.Count;Check.True(!f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10,1,1));
                    Check.Equal(before,f.Engine.Events.Count);Check.True(f.Engine.RawCountdownAwaitingSoftwareWitness);
                    Check.True(f.Admission.IoFence.IsRevoked);Check.True(!f.Task.NamedRecordingWithdrawalRequested);
                    f.Aux(false,false,true,false);Check.Equal(Phase.Ended,f.Task.Phase);Check.Equal((EndReason?)EndReason.Withdrawn,f.Task.EndReason);
                }
            });
            suite.Run("insufficient byte reserve rejects raw countdown before mutation",()=>{
                using(var f=new Fixture(8,1048576))
                {
                    f.CountdownWithoutReceiptForFixture();f.DrainTaskEvents();
                    int before=f.Engine.Events.Count;Check.True(!f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10,1,1));
                    Check.Equal(before,f.Engine.Events.Count);Check.True(f.Engine.RawCountdownAwaitingSoftwareWitness);
                    Check.True(f.Admission.IoFence.IsRevoked);Check.True(!f.Task.NamedRecordingWithdrawalRequested);
                }
            });
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            suite.Run("withdrawal during countdown slot reservation prevents witness mutation",()=>{
                using(var f=new Fixture(8,8*1024*1024))
                {
                    f.CountdownWithoutReceiptForFixture();f.DrainTaskEvents();
                    f.Admission.AfterCountdownReservationForTest=()=>f.Task.ParticipantWithdraw();
                    Check.True(!f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10,1,1));
                    Check.Equal(Phase.Ended,f.Task.Phase);Check.Equal((EndReason?)EndReason.Withdrawn,f.Task.EndReason);
                    foreach(var item in f.Engine.Events)Check.True(item.Kind!="countdown_software_witness");
                    Check.True(f.Admission.IoFence.IsRevoked);Check.True(f.Task.NamedRecordingWithdrawalRequested);
                }
            });
            suite.Run("pause during countdown slot reservation is not blocked or mistaken for withdrawal",()=>{
                using(var f=new Fixture(8,8*1024*1024))
                {
                    f.CountdownWithoutReceiptForFixture();f.DrainTaskEvents();
                    f.Admission.AfterCountdownReservationForTest=()=>f.Task.ParticipantPause();
                    Check.True(!f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10,1,1));
                    Check.Equal(Phase.Paused,f.Task.Phase);Check.True(f.Admission.IoFence.IsRevoked);
                    Check.True(!f.Task.NamedRecordingWithdrawalRequested);
                    foreach(var item in f.Engine.Events)Check.True(item.Kind!="countdown_software_witness");
                }
            });
            suite.Run("a consumed countdown reservation cannot publish a second time",()=>{
                using(var f=new Fixture(8,8*1024*1024))
                {
                    f.CountdownWithoutReceiptForFixture();f.DrainTaskEvents();
                    ViveNamedSessionFrameAdmission.CountdownWitnessReservation captured=null;
                    f.Admission.AfterCountdownReservationForTest=()=>captured=(ViveNamedSessionFrameAdmission.CountdownWitnessReservation)
                        typeof(ViveNamedSessionFrameAdmission).GetField("countdownSlot",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f.Admission);
                    Check.True(f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10,1,1));Check.True(captured!=null);
                    int count=f.Engine.Events.Count;Check.True(!f.Admission.TryCommitCountdownWitness(captured));
                    Check.Equal(count,f.Engine.Events.Count);Check.True(f.Admission.IoFence.IsRevoked);
                    Check.True(!f.Task.NamedRecordingWithdrawalRequested);
                }
            });
#endif
            suite.Run("second countdown witness clock read faults and revokes without claiming withdrawal or rollback",()=>{
                using(var f=new Fixture())
                {
                    f.CountdownWithoutReceiptForFixture();Check.True(f.Task.NamedRawCountdownAwaitingSoftwareWitness);
                    var field=typeof(Experiment).GetField("clock",BindingFlags.Instance|BindingFlags.NonPublic);
                    Check.True(field!=null);field.SetValue(f.Engine,new SecondReadRollbackClock());
                    int priorEvents=f.Engine.Events.Count,priorTrials=f.Engine.Trials.Count;
                    Check.True(!f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10,1,1));
                    Check.True(f.Admission.IoFence.IsRevoked);Check.Equal(3,f.Admission.State);
                    Check.True(f.Task.NamedRecordingAuthorityFaulted);Check.True(!f.Task.NamedRecordingWithdrawalRequested);
                    Check.Equal(Phase.Countdown,f.Task.Phase);Check.Equal(priorTrials,f.Engine.Trials.Count);Check.Equal(priorEvents,f.Engine.Events.Count);
                    // The first clock read already changed countdown state;
                    // this is an UNKNOWN partial mutation, not rollback.
                    Check.True(!f.Engine.RawCountdownAwaitingSoftwareWitness);
                    Check.Throws<InvalidOperationException>(()=>f.Task.Tick());
                    Check.Equal(Phase.Countdown,f.Task.Phase);Check.Equal(priorTrials,f.Engine.Trials.Count);
                }
            });
            suite.Run("budgeted countdown second clock failure revokes reserved slot with unknown mutation",()=>{
                using(var f=new Fixture(8,8*1024*1024))
                {
                    f.CountdownWithoutReceiptForFixture();f.DrainTaskEvents();
                    var field=typeof(Experiment).GetField("clock",BindingFlags.Instance|BindingFlags.NonPublic);
                    Check.True(field!=null);field.SetValue(f.Engine,new SecondReadRollbackClock());
                    int prior=f.Engine.Events.Count;
                    Check.True(!f.Task.TryStartNamedRawCountdownFromSoftwareWitness(10,1,1));
                    Check.True(f.Admission.IoFence.IsRevoked);Check.Equal(3,f.Admission.State);
                    Check.True(f.Task.NamedRecordingAuthorityFaulted);Check.True(!f.Task.NamedRecordingWithdrawalRequested);
                    Check.Equal(prior,f.Engine.Events.Count);Check.True(!f.Engine.RawCountdownAwaitingSoftwareWitness);
                    Check.Throws<InvalidOperationException>(()=>f.Task.Tick());
                }
            });
        }
    }
}
