using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic queue mechanics only: no real task mutation, device, file,
    // network, consent, actual-data claim or product authorization.
    public static class ViveSyntheticAdmissionBatchTests
    {
        sealed class Client:IViveProEyeClient
        {
            internal readonly ManualResetEvent Waiting=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "synthetic";}}public bool Start(){return true;}
            public ViveProEyePoll Poll(){Waiting.Set();Release.WaitOne(3000);return null;}
            public void Dispose(){}
        }
        sealed class Fixture:IDisposable
        {
            internal readonly Client Client=new Client();internal readonly ViveProEyeWorker Worker;
            internal readonly ViveNamedSessionFrameAdmission Admission;
            internal Fixture(int capacity)
            {
                var clock=new MonotonicClock("synthetic-batch-clock");
                Worker=new ViveProEyeWorker(clock,()=>null,()=>Client);
                Check.True(Worker.Start()&&Client.Waiting.WaitOne(3000));
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"batch-geometry");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"batch-plan",option);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"batch-intent",true,true);
                var config=SessionConfiguration.NamedRecordingDevelopment("batch-session",37,plan,intent);
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"batch-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                var task=new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
                Admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,Worker,clock,capacity);
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());
            }
            public void Dispose(){Admission.Withdraw();Client.Release.Set();Check.True(Worker.Stop(1000));Worker.Dispose();}
        }
        static void Take(ViveNamedSessionFrameAdmission a,object value,long ordinal)
        {
            ViveNamedSessionFrameAdmission.ConsumerReceipt r;Check.True(a.TryReserveConsumer(out r)&&r!=null);
            Check.Equal(ordinal,r.Ordinal);Check.Equal(value,r.Record);Check.True(a.TryAcknowledgeSyntheticForTest(r));
        }
        static void AbortAtPublication(bool after,bool withdraw)
        {
            using(var f=new Fixture(4))
            {
                var a=f.Admission;ViveNamedSessionFrameAdmission.SyntheticBatchToken token;
                var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                var oldRing=(object[])typeof(ViveNamedSessionFrameAdmission).GetField("ring",flags).GetValue(a);
                Check.True(a.TryReserveSyntheticBatchForTest(2,out token));Check.True(a.TryAddSyntheticBatchForTest(token,"a"));Check.True(a.TryAddSyntheticBatchForTest(token,"b"));
                Action interruption=()=>{if(withdraw)a.Withdraw();else throw new InvalidOperationException("synthetic failure");};
                if(after)a.AfterTailPublishedForTest=interruption;else a.BeforeSyntheticBatchTailForTest=interruption;
                Check.True(!a.TryCommitSyntheticBatchForTest(token));Check.True(a.State!=0&&a.Count==0);
                foreach(var item in oldRing)Check.True(item==null);
                ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;Check.True(!a.TryReserveConsumer(out receipt)&&receipt==null);
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic batch wraps behind reserved consumer and publishes one FIFO tail",()=>{
                using(var f=new Fixture(4))
                {
                    var a=f.Admission;Check.True(a.TryPublishSyntheticForTest("1")&&a.TryPublishSyntheticForTest("2")&&a.TryPublishSyntheticForTest("3"));
                    Take(a,"1",0);ViveNamedSessionFrameAdmission.ConsumerReceipt old;Check.True(a.TryReserveConsumer(out old)&&old!=null);
                    var watch=Stopwatch.StartNew();ViveNamedSessionFrameAdmission.SyntheticBatchToken t;
                    Check.True(a.TryReserveSyntheticBatchForTest(2,out t));Check.True(watch.ElapsedMilliseconds<500);
                    Check.True(a.TryAddSyntheticBatchForTest(t,"4")&&a.TryAddSyntheticBatchForTest(t,"5"));
                    Check.Equal("2",old.Record);Check.True(a.TryAcknowledgeSyntheticForTest(old));
                    ViveNamedSessionFrameAdmission.ConsumerReceipt busy;Check.True(a.TryReserveConsumer(out busy)&&busy==null);
                    Check.True(a.TryCommitSyntheticBatchForTest(t));Take(a,"3",2);Take(a,"4",3);Take(a,"5",4);
                    Check.Equal(0,a.Count);
                }
            });
            suite.Run("synthetic batch full queue fails closed before staging",()=>{
                using(var f=new Fixture(2))
                {var a=f.Admission;Check.True(a.TryPublishSyntheticForTest(1)&&a.TryPublishSyntheticForTest(2));ViveNamedSessionFrameAdmission.SyntheticBatchToken t;
                    Check.True(!a.TryReserveSyntheticBatchForTest(1,out t)&&t==null);Check.True(a.State!=0&&a.Count==0);}
            });
            suite.Run("synthetic batch consumer cannot observe a partial batch during publication",()=>{
                using(var f=new Fixture(4))
                {
                    var a=f.Admission;var before=new ManualResetEvent(false);var after=new ManualResetEvent(false);
                    var beforeDone=new ManualResetEvent(false);var afterDone=new ManualResetEvent(false);Exception error=null;bool started=false;
                    var consumer=new Thread(()=>{try{
                        Check.True(before.WaitOne(3000));ViveNamedSessionFrameAdmission.ConsumerReceipt r;
                        Check.True(a.TryReserveConsumer(out r)&&r==null);beforeDone.Set();
                        Check.True(after.WaitOne(3000));Check.True(a.TryReserveConsumer(out r)&&r==null);afterDone.Set();
                    }catch(Exception e){error=e;beforeDone.Set();afterDone.Set();}}){IsBackground=true};
                    try
                    {
                        a.BeforeSyntheticBatchTailForTest=()=>{before.Set();Check.True(beforeDone.WaitOne(3000));};
                        a.AfterTailPublishedForTest=()=>{after.Set();Check.True(afterDone.WaitOne(3000));};
                        ViveNamedSessionFrameAdmission.SyntheticBatchToken t;Check.True(a.TryReserveSyntheticBatchForTest(2,out t));
                        Check.True(a.TryAddSyntheticBatchForTest(t,"first")&&a.TryAddSyntheticBatchForTest(t,"second"));consumer.Start();started=true;
                        Check.True(a.TryCommitSyntheticBatchForTest(t));Check.True(consumer.Join(3000));if(error!=null)throw error;
                        Take(a,"first",0);Take(a,"second",1);Check.Equal(0,a.Count);
                    }
                    finally{before.Set();after.Set();if(started)consumer.Join(1000);before.Dispose();after.Dispose();beforeDone.Dispose();afterDone.Dispose();}
                }
            });
            suite.Run("synthetic empty commit and cancellation never publish partial items",()=>{
                using(var f=new Fixture(4))
                {var a=f.Admission;ViveNamedSessionFrameAdmission.SyntheticBatchToken t;
                    Check.True(a.TryReserveSyntheticBatchForTest(2,out t)&&a.TryCommitSyntheticBatchForTest(t));Check.Equal(0,a.Count);
                    Check.True(a.TryReserveSyntheticBatchForTest(2,out t)&&a.TryAddSyntheticBatchForTest(t,"discard")&&a.TryCancelSyntheticBatchForTest(t));
                    Check.Equal(0,a.Count);Check.True(a.TryPublishSyntheticForTest("next"));Take(a,"next",0);}
            });
            suite.Run("synthetic batch rejects cross-admission token and replay",()=>{
                using(var a=new Fixture(4))using(var b=new Fixture(4))
                {ViveNamedSessionFrameAdmission.SyntheticBatchToken t;Check.True(a.Admission.TryReserveSyntheticBatchForTest(1,out t));
                    Check.True(!b.Admission.TryAddSyntheticBatchForTest(t,"wrong"));Check.True(b.Admission.State!=0);
                    Check.True(a.Admission.TryAddSyntheticBatchForTest(t,"right")&&a.Admission.TryCommitSyntheticBatchForTest(t));
                    Check.True(!a.Admission.TryCommitSyntheticBatchForTest(t)&&a.Admission.State!=0);}
            });
            suite.Run("synthetic batch rejects forged token and overfilled reservation",()=>{
                using(var f=new Fixture(4))
                {var a=f.Admission;ViveNamedSessionFrameAdmission.SyntheticBatchToken t;Check.True(a.TryReserveSyntheticBatchForTest(1,out t));
                    var forged=new ViveNamedSessionFrameAdmission.SyntheticBatchToken(a,t.Start,t.Limit);
                    Check.True(!a.TryAddSyntheticBatchForTest(forged,"forged")&&a.State!=0&&a.Count==0);}
                using(var f=new Fixture(4))
                {var a=f.Admission;ViveNamedSessionFrameAdmission.SyntheticBatchToken t;Check.True(a.TryReserveSyntheticBatchForTest(1,out t));
                    Check.True(a.TryAddSyntheticBatchForTest(t,"first"));Check.True(!a.TryAddSyntheticBatchForTest(t,"excess"));Check.True(a.State!=0&&a.Count==0);}
            });
            suite.Run("synthetic batch withdrawal does not wait for a reserved producer",()=>{
                using(var f=new Fixture(4))
                {var a=f.Admission;ViveNamedSessionFrameAdmission.SyntheticBatchToken t;Check.True(a.TryReserveSyntheticBatchForTest(2,out t));
                    var revoked=new ManualResetEvent(false);var other=new Thread(()=>{a.Withdraw();revoked.Set();}){IsBackground=true};
                    other.Start();Check.True(revoked.WaitOne(500));Check.True(other.Join(1000));
                    Check.True(!a.TryAddSyntheticBatchForTest(t,"late")&&a.State!=0&&a.Count==0);revoked.Dispose();}
            });
            suite.Run("synthetic withdrawal detaches pending and token holds no ring or value arrays",()=>{
                using(var f=new Fixture(4))
                {
                    var a=f.Admission;ViveNamedSessionFrameAdmission.SyntheticBatchToken token;
                    Check.True(a.TryReserveSyntheticBatchForTest(2,out token));var value=new object();Check.True(a.TryAddSyntheticBatchForTest(token,value));
                    var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                    var stateField=typeof(ViveNamedSessionFrameAdmission).GetField("syntheticBatchState",flags);
                    var state=stateField.GetValue(a);var pending=(object[])state.GetType().GetField("Pending",flags).GetValue(state);
                    Check.True(Object.ReferenceEquals(value,pending[0]));
                    a.Withdraw();Check.True(!Object.ReferenceEquals(state,stateField.GetValue(a))&&pending[0]==null);
                    foreach(var field in typeof(ViveNamedSessionFrameAdmission.SyntheticBatchToken).GetFields(flags|System.Reflection.BindingFlags.Public))
                        Check.True(field.FieldType!=typeof(object[]));
                    Check.True(!a.TryAddSyntheticBatchForTest(token,new object()));
                }
            });
            suite.Run("completed synthetic token does not retain later queue after withdrawal",()=>{
                using(var f=new Fixture(4))
                {
                    var a=f.Admission;ViveNamedSessionFrameAdmission.SyntheticBatchToken token;
                    Check.True(a.TryReserveSyntheticBatchForTest(1,out token)&&a.TryAddSyntheticBatchForTest(token,"first")&&a.TryCommitSyntheticBatchForTest(token));
                    Take(a,"first",0);Check.True(a.TryPublishSyntheticForTest(new object()));a.Withdraw();
                    var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                    Check.True(typeof(ViveNamedSessionFrameAdmission).GetField("ring",flags).GetValue(a)==null);
                    var batchState=typeof(ViveNamedSessionFrameAdmission).GetField("syntheticBatchState",flags).GetValue(a);
                    Check.True(batchState!=null&&batchState.GetType()==typeof(object));
                    foreach(var field in typeof(ViveNamedSessionFrameAdmission.SyntheticBatchToken).GetFields(flags|System.Reflection.BindingFlags.Public))
                        Check.True(field.FieldType!=typeof(object[]));
                    Check.True(!a.TryCommitSyntheticBatchForTest(token));
                }
            });
            suite.Run("synthetic withdrawal racing a producer add detaches the private slot",()=>{
                using(var f=new Fixture(4))
                {
                    var a=f.Admission;ViveNamedSessionFrameAdmission.SyntheticBatchToken token;Check.True(a.TryReserveSyntheticBatchForTest(1,out token));
                    var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                    var stateField=typeof(ViveNamedSessionFrameAdmission).GetField("syntheticBatchState",flags);
                    var state=stateField.GetValue(a);var pending=(object[])state.GetType().GetField("Pending",flags).GetValue(state);var revoked=new ManualResetEvent(false);
                    a.AfterSyntheticBatchAddForTest=()=>{var other=new Thread(()=>{a.Withdraw();revoked.Set();}){IsBackground=true};other.Start();Check.True(revoked.WaitOne(500)&&other.Join(1000));};
                    Check.True(!a.TryAddSyntheticBatchForTest(token,new object()));Check.True(!Object.ReferenceEquals(state,stateField.GetValue(a))&&pending[0]==null);
                    revoked.Dispose();
                }
            });
            suite.Run("withdrawal before synthetic reservation publish forbids later ring reattachment",()=>{
                using(var f=new Fixture(4))
                {
                    var a=f.Admission;Check.True(a.TryPublishSyntheticForTest(new object()));
                    var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                    var ringField=typeof(ViveNamedSessionFrameAdmission).GetField("ring",flags);
                    var stateField=typeof(ViveNamedSessionFrameAdmission).GetField("syntheticBatchState",flags);
                    var revoked=new ManualResetEvent(false);bool detached=false;
                    a.BeforeSyntheticBatchReservePublishForTest=()=>{
                        var other=new Thread(()=>{a.Withdraw();revoked.Set();}){IsBackground=true};other.Start();Check.True(revoked.WaitOne(500)&&other.Join(1000));
                        detached=ringField.GetValue(a)==null&&stateField.GetValue(a)!=null&&stateField.GetValue(a).GetType()==typeof(object);
                    };
                    ViveNamedSessionFrameAdmission.SyntheticBatchToken token;
                    Check.True(!a.TryReserveSyntheticBatchForTest(1,out token)&&token==null&&detached);
                    Check.True(ringField.GetValue(a)==null&&stateField.GetValue(a)!=null&&stateField.GetValue(a).GetType()==typeof(object));revoked.Dispose();
                }
            });
            suite.Run("synthetic batch revocation cannot expose private slots before commit",()=>{AbortAtPublication(false,true);AbortAtPublication(true,true);});
            suite.Run("synthetic batch exception cannot expose private slots before commit",()=>{AbortAtPublication(false,false);AbortAtPublication(true,false);});
            suite.Run("synthetic withdrawal invalidates an already reserved batch consumer",()=>{
                using(var f=new Fixture(4))
                {var a=f.Admission;ViveNamedSessionFrameAdmission.SyntheticBatchToken t;Check.True(a.TryReserveSyntheticBatchForTest(2,out t));
                    Check.True(a.TryAddSyntheticBatchForTest(t,1)&&a.TryAddSyntheticBatchForTest(t,2)&&a.TryCommitSyntheticBatchForTest(t));
                    ViveNamedSessionFrameAdmission.ConsumerReceipt r;Check.True(a.TryReserveConsumer(out r)&&r!=null);a.Withdraw();
                    Check.True(r.Record==null&&!a.TryAcknowledgeSyntheticForTest(r));Check.Equal(2,a.State);}
            });
        }
    }
}
