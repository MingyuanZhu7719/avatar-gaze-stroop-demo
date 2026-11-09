using System;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Only the in-memory shared-state protocol is tested here. No native
    // file, OS handle, participant consent or output publication is created.
    public static class ViveRawNativeBindingTests
    {
        static byte[] FileId(){var value=new byte[16];value[0]=1;return value;}
        sealed class Client:IViveProEyeClient
        {
            internal readonly ManualResetEvent Ready=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "synthetic";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Ready.Set();Release.WaitOne();return null;}
            public void Dispose(){}
        }
        sealed class Fixture:IDisposable
        {
            internal readonly Client Source=new Client();
            internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveNamedSessionFrameAdmission Admission;
            internal ViveRawIoFence Fence {get{return Admission.IoFence;}}
            internal ViveRawRevocation Revocation {get{return Task.NamedRawRevocation;}}
            internal Fixture()
            {
                var clock=new MonotonicClock("synthetic-native-binding");
                Worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>Source);
                Check.True(Worker.Start()&&Source.Ready.WaitOne(3000));
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"synthetic-geometry");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-plan",option);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-intent",true,true);
                var config=SessionConfiguration.NamedRecordingDevelopment("synthetic-binding",37,plan,intent);
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"synthetic-geometry");
                var map=new System.Collections.Generic.Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
                Admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(Task,Worker,clock,8);
            }
            public void Dispose(){Source.Release.Set();Check.True(Worker.Stop(1000));Worker.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("native identity is bound to exact live handle and source objects",()=>{
                var state=new ViveRawRevocation();var capability=new object();var sink=new object();var admission=new object();var handle=new object();object key;
                Check.True(state.TryBeginNativeFile(capability,sink,admission,"vraw-synthetic.vraw",out key));
                Check.True(!state.NativeFileCurrent(key,capability,sink,admission,handle));
                Check.True(!state.TryVerifyNativeFile(key,capability,sink,admission,handle,42,new byte[15]));
                Check.True(!state.TryVerifyNativeFile(key,capability,sink,admission,handle,42,new byte[16]));
                Check.True(state.TryVerifyNativeFile(key,capability,sink,admission,handle,42,FileId()));
                Check.True(state.NativeFileCurrent(key,capability,sink,admission,handle));
                Check.True(!state.NativeFileCurrent(key,capability,sink,new object(),handle));
                Check.True(!state.NativeFileCurrent(key,capability,new object(),admission,handle));
                Check.True(!state.NativeFileCurrent(key,capability,sink,admission,new object()));
                Check.True(!state.TryVerifyNativeFile(key,capability,sink,admission,handle,42,FileId()));
                state.Revoke();Check.True(!state.NativeFileCurrent(key,capability,sink,admission,handle));
                object other;Check.True(!state.TryBeginNativeFile(capability,sink,admission,"vraw-second.vraw",out other));
            });
            suite.Run("native identity and synthetic page window are mutually exclusive",()=>{
                var first=new ViveRawRevocation();var owner=new object();var payload=new object();object key;
                Check.True(first.TryBeginNativeFile(owner,new object(),new object(),"vraw-synthetic.vraw",out key));
                Check.True(!first.TryAttachSimulationWindow(owner));
                var second=new ViveRawRevocation();Check.True(second.TryAttachSimulationWindow(owner));
                Check.True(second.TryPublishSimulationPage(owner,new object(),payload));
                Check.True(!second.TryBeginNativeFile(owner,new object(),new object(),"vraw-synthetic.vraw",out key));
            });
            suite.Run("native identity fence rejects a capability or sink from a different binding",()=>{
                using(var f=new Fixture())
                {
                    var sink=new ViveRawSessionJournalSink(f.Admission,@"C:\synthetic\first.vraw");
                    var otherSink=new ViveRawSessionJournalSink(f.Admission,@"C:\synthetic\second.vraw");
                    var otherFence=new ViveRawIoFence(f.Admission,f.Revocation);
                    var ctor=typeof(ViveRawRelativeFileCapability).GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,
                        new[]{typeof(ViveRawIoFence),typeof(ViveRawDirectoryLease),typeof(ViveRawSessionJournalSink)},null);
                    Check.True(ctor!=null);
                    var capability=(ViveRawRelativeFileCapability)ctor.Invoke(new object[]{f.Fence,null,sink});
                    var wrongCapability=(ViveRawRelativeFileCapability)ctor.Invoke(new object[]{otherFence,null,sink});
                    var handle=new object();bool wrongSink=true,wrongFence=true,correct=false,verified=false,current=false;Exception error=null;
                    var writer=new Thread(()=>{try{
                        object key;
                        wrongSink=f.Fence.TryBeginNativeFile(capability,otherSink,"vraw-synthetic.vraw",out key);
                        wrongFence=f.Fence.TryBeginNativeFile(wrongCapability,sink,"vraw-synthetic.vraw",out key);
                        correct=f.Fence.TryBeginNativeFile(capability,sink,"vraw-synthetic.vraw",out key);
                        verified=f.Fence.TryVerifyNativeFile(key,capability,sink,handle,42,FileId());
                        current=f.Fence.NativeFileCurrent(key,capability,sink,handle);
                    }catch(Exception e){error=e;}});
                    Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(writer.Join(3000));if(error!=null)throw error;
                    Check.True(!wrongSink&&!wrongFence&&correct&&verified&&current);
                    f.Revocation.Revoke();
                }
            });
            suite.Run("verified native binding permits ordinary completion but not future operations",()=>{
                using(var f=new Fixture())
                {
                    var capability=new object();var sink=new object();var handle=new object();object key;
                    Check.True(f.Revocation.TryBeginNativeFile(capability,sink,f.Admission,"vraw-synthetic.vraw",out key));
                    Check.True(f.Revocation.TryVerifyNativeFile(key,capability,sink,f.Admission,handle,42,FileId()));
                    bool completed=false;var writer=new Thread(()=>{completed=f.Fence.TryPublishCallsReturned();});
                    Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(writer.Join(3000));
                    Check.True(completed);Check.Equal(ViveRawRevocation.Outcome.CallsReturned,f.Fence.CompletionOutcome);
                    Check.True(!f.Revocation.NativeFileCurrent(key,capability,sink,f.Admission,handle));
                    f.Revocation.RevokeForParticipant();Check.Equal(ViveRawRevocation.Outcome.Revoked,f.Fence.CompletionOutcome);
                }
            });
            suite.Run("unverified native binding cannot be marked completed",()=>{
                using(var f=new Fixture())
                {
                    object key;Check.True(f.Revocation.TryBeginNativeFile(new object(),new object(),f.Admission,"vraw-synthetic.vraw",out key));
                    bool completed=true;var writer=new Thread(()=>{completed=f.Fence.TryPublishCallsReturned();});
                    Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(writer.Join(3000));
                    Check.True(!completed);Check.Equal(ViveRawRevocation.Outcome.Live,f.Fence.CompletionOutcome);
                    f.Revocation.Revoke();
                }
            });
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            suite.Run("withdrawal defeats native name publication even before window cleanup",()=>{
                using(var f=new Fixture())using(var atBegin=new ManualResetEvent(false))using(var releaseBegin=new ManualResetEvent(false))
                using(var atWithdraw=new ManualResetEvent(false))using(var releaseWithdraw=new ManualResetEvent(false))
                {
                    var state=f.Revocation;state.BeforeNativeBeginCasForTest=()=>{atBegin.Set();releaseBegin.WaitOne();};
                    state.AfterWithdrawalSignalForTest=()=>{atWithdraw.Set();releaseWithdraw.WaitOne();};
                    bool began=true;object key=null;Exception error=null;
                    var begin=new Thread(()=>{try{began=state.TryBeginNativeFile(new object(),new object(),f.Admission,"vraw-synthetic.vraw",out key);}catch(Exception e){error=e;}});
                    var withdraw=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(Exception e){error=e;}});
                    try{begin.Start();Check.True(atBegin.WaitOne(3000));withdraw.Start();Check.True(atWithdraw.WaitOne(3000));
                        releaseBegin.Set();Check.True(begin.Join(3000));Check.True(!began&&key==null&&!state.MayReserve&&!state.NativeFileSlotPresentForTest);
                        releaseWithdraw.Set();Check.True(withdraw.Join(3000));if(error!=null)throw error;
                    }finally{releaseBegin.Set();releaseWithdraw.Set();}
                }
            });
            suite.Run("withdrawal defeats verified native identity publication",()=>{
                using(var f=new Fixture())using(var atVerify=new ManualResetEvent(false))using(var releaseVerify=new ManualResetEvent(false))
                using(var atWithdraw=new ManualResetEvent(false))using(var releaseWithdraw=new ManualResetEvent(false))
                {
                    var state=f.Revocation;var capability=new object();var sink=new object();var handle=new object();object key;
                    Check.True(state.TryBeginNativeFile(capability,sink,f.Admission,"vraw-synthetic.vraw",out key));
                    state.BeforeNativeVerifyCasForTest=()=>{atVerify.Set();releaseVerify.WaitOne();};
                    state.AfterWithdrawalSignalForTest=()=>{atWithdraw.Set();releaseWithdraw.WaitOne();};
                    bool verified=true;Exception error=null;
                    var verify=new Thread(()=>{try{verified=state.TryVerifyNativeFile(key,capability,sink,f.Admission,handle,42,FileId());}catch(Exception e){error=e;}});
                    var withdraw=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(Exception e){error=e;}});
                    try{verify.Start();Check.True(atVerify.WaitOne(3000));withdraw.Start();Check.True(atWithdraw.WaitOne(3000));
                        releaseVerify.Set();Check.True(verify.Join(3000));Check.True(!verified&&!state.NativeFileCurrent(key,capability,sink,f.Admission,handle)&&!state.NativeFileSlotPresentForTest);
                        releaseWithdraw.Set();Check.True(withdraw.Join(3000));if(error!=null)throw error;
                    }finally{releaseVerify.Set();releaseWithdraw.Set();}
                }
            });
            suite.Run("withdrawal signal defeats completion and clears shared native identity",()=>{
                using(var f=new Fixture())using(var atComplete=new ManualResetEvent(false))using(var releaseComplete=new ManualResetEvent(false))
                using(var atWithdraw=new ManualResetEvent(false))using(var releaseWithdraw=new ManualResetEvent(false))
                {
                    var state=f.Revocation;var capability=new object();var sink=new object();var handle=new object();object key;
                    Check.True(state.TryBeginNativeFile(capability,sink,f.Admission,"vraw-synthetic.vraw",out key));
                    Check.True(state.TryVerifyNativeFile(key,capability,sink,f.Admission,handle,42,FileId()));
                    state.BeforeCompleteCasForTest=()=>{atComplete.Set();releaseComplete.WaitOne();};
                    state.AfterWithdrawalSignalForTest=()=>{atWithdraw.Set();releaseWithdraw.WaitOne();};
                    bool completed=true;Exception error=null;
                    var writer=new Thread(()=>{try{completed=f.Fence.TryPublishCallsReturned();}catch(Exception e){error=e;}});
                    var withdraw=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(Exception e){error=e;}});
                    try{Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(atComplete.WaitOne(3000));
                        withdraw.Start();Check.True(atWithdraw.WaitOne(3000));releaseComplete.Set();Check.True(writer.Join(3000));
                        Check.True(!completed&&!state.NativeFileSlotPresentForTest);Check.Equal(ViveRawRevocation.Outcome.Revoked,f.Fence.CompletionOutcome);
                        releaseWithdraw.Set();Check.True(withdraw.Join(3000));if(error!=null)throw error;
                    }finally{releaseComplete.Set();releaseWithdraw.Set();}
                }
            });
#endif
        }
    }
}
