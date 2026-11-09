using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // In-memory authority/ordering tests. No file, provider data or OS I/O.
    public static class ViveSharedCompletionFenceTests
    {
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
        sealed class SignaledWrite:MemoryStream
        {
            internal readonly ManualResetEvent Written=new ManualResetEvent(false);
            public override void Write(byte[] bytes,int offset,int count){base.Write(bytes,offset,count);Written.Set();}
        }
#endif
        sealed class Client:IViveProEyeClient
        {
            internal readonly ManualResetEvent Ready=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "synthetic";}}public bool Start(){return true;}
            public ViveProEyePoll Poll(){Ready.Set();Release.WaitOne();return null;}public void Dispose(){}
        }
        sealed class Fixture:IDisposable
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("shared-completion-synthetic");
            internal readonly Client Source=new Client();internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;internal readonly ViveNamedSessionFrameAdmission Admission;
            internal ViveRawIoFence Fence {get{return Admission.IoFence;}}
            internal Fixture()
            {
                Worker=new ViveProEyeWorker(Clock,()=>new ViveHeadFrame(1,Clock.Now,true,true,Rotation.Identity),()=>Source);
                Check.True(Worker.Start()&&Source.Ready.WaitOne(3000));
                var selection=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"synthetic-geometry");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-plan",selection);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-intent",true,true);
                var config=SessionConfiguration.NamedRecordingDevelopment("synthetic-completion",37,plan,intent);
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"synthetic-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true);
                Admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(Task,Worker,Clock,8);
            }
            public void Dispose(){Source.Release.Set();Check.True(Worker.Stop(1000));Worker.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
            suite.Run("bound private stage can finish local I/O after sink calls return without publication authority",()=>{
                using(var f=new Fixture())using(var target=new SignaledWrite())
                {var shared=f.Task.NamedRawRevocation;var stage=new ViveRawPrivateStage(target,1024,CancellationToken.None,shared);
                    bool completed=false;var writer=new Thread(()=>completed=f.Fence.TryPublishCallsReturned());
                    Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(writer.Join(3000)&&completed);
                    Check.True(!shared.MayReserve&&!shared.IsRevoked&&!shared.ParticipantWithdrawalRequested);
                    stage.OfferMagic(new byte[]{1,2,3,4,5,6,7,8});
                    try{Check.True(target.Written.WaitOne(3000));Check.Equal(ViveRawPrivateStage.Outcome.Pending,stage.State);}
                    finally{stage.Abandon();Check.True(SpinWait.SpinUntil(()=>stage.WorkerExited,3000));}
                    Check.True(!shared.IsRevoked&&!shared.ParticipantWithdrawalRequested);}
            });
#endif
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            suite.Run("participant withdrawal signal prevents a racing IO permit before final check",()=>{
                using(var f=new Fixture())
                using(var atReservation=new ManualResetEvent(false))using(var releaseReservation=new ManualResetEvent(false))
                using(var atWithdrawal=new ManualResetEvent(false))using(var releaseWithdrawal=new ManualResetEvent(false))
                {
                    var authority=f.Task.NamedRawRevocation;
                    f.Fence.BeforeReservationFinalCheckForTest=()=>{atReservation.Set();releaseReservation.WaitOne();};
                    authority.AfterWithdrawalSignalForTest=()=>{atWithdrawal.Set();releaseWithdrawal.WaitOne();};
                    bool reserved=true;ViveRawIoFence.Permit permit=null;Exception error=null;
                    var writer=new Thread(()=>{try{reserved=f.Fence.TryReserve(ViveRawIoFence.IoOperation.DataWrite,out permit);}catch(Exception e){error=e;}});
                    var withdrawal=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(Exception e){error=e;}});
                    try{
                        Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(atReservation.WaitOne(3000));
                        withdrawal.Start();Check.True(atWithdrawal.WaitOne(3000));Check.True(f.Task.NamedRecordingWithdrawalRequested);
                        releaseReservation.Set();Check.True(writer.Join(3000));Check.True(!reserved&&permit==null&&!f.Fence.HasReservation);
                        releaseWithdrawal.Set();Check.True(withdrawal.Join(3000));if(error!=null)throw error;
                    }finally{releaseReservation.Set();releaseWithdrawal.Set();}
                }
            });
            suite.Run("participant withdrawal signal prevents a racing page claim before window clear",()=>{
                using(var f=new Fixture())
                using(var atClaim=new ManualResetEvent(false))using(var releaseClaim=new ManualResetEvent(false))
                using(var atWithdrawal=new ManualResetEvent(false))using(var releaseWithdrawal=new ManualResetEvent(false))
                {
                    var authority=f.Task.NamedRawRevocation;var owner=new object();var token=new object();var value=new object();
                    Check.True(authority.TryAttachSimulationWindow(owner)&&authority.TryPublishSimulationPage(owner,token,value));
                    authority.BeforeClaimCasForTest=()=>{atClaim.Set();releaseClaim.WaitOne();};
                    authority.AfterWithdrawalSignalForTest=()=>{atWithdrawal.Set();releaseWithdrawal.WaitOne();};
                    bool claimed=true;object payload=null;Exception error=null;
                    var claim=new Thread(()=>{try{claimed=authority.TryClaimSimulationPage(owner,token,out payload);}catch(Exception e){error=e;}});
                    var withdrawal=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(Exception e){error=e;}});
                    try{
                        claim.Start();Check.True(atClaim.WaitOne(3000));withdrawal.Start();Check.True(atWithdrawal.WaitOne(3000));
                        Check.True(f.Task.NamedRecordingWithdrawalRequested&&authority.IsRevoked&&!authority.MayReserve);
                        releaseClaim.Set();Check.True(claim.Join(3000));Check.True(!claimed&&payload==null);
                        f.Fence.Revoke();Check.True(f.Task.NamedRecordingWithdrawalRequested);
                        releaseWithdrawal.Set();Check.True(withdrawal.Join(3000));if(error!=null)throw error;
                    }finally{releaseClaim.Set();releaseWithdrawal.Set();}
                }
            });
            suite.Run("participant withdrawal signal prevents a racing completion before window clear",()=>{
                using(var f=new Fixture())
                using(var atComplete=new ManualResetEvent(false))using(var releaseComplete=new ManualResetEvent(false))
                using(var atWithdrawal=new ManualResetEvent(false))using(var releaseWithdrawal=new ManualResetEvent(false))
                {
                    var authority=f.Task.NamedRawRevocation;
                    authority.BeforeCompleteCasForTest=()=>{atComplete.Set();releaseComplete.WaitOne();};
                    authority.AfterWithdrawalSignalForTest=()=>{atWithdrawal.Set();releaseWithdrawal.WaitOne();};
                    bool completed=true;Exception error=null;
                    var writer=new Thread(()=>{try{completed=f.Fence.TryPublishCallsReturned();}catch(Exception e){error=e;}});
                    var withdrawal=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(Exception e){error=e;}});
                    try{
                        Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(atComplete.WaitOne(3000));
                        withdrawal.Start();Check.True(atWithdrawal.WaitOne(3000));
                        Check.True(f.Task.NamedRecordingWithdrawalRequested);releaseComplete.Set();Check.True(writer.Join(3000));
                        Check.True(!completed);Check.Equal(ViveRawRevocation.Outcome.Revoked,f.Fence.CompletionOutcome);
                        releaseWithdrawal.Set();Check.True(withdrawal.Join(3000));if(error!=null)throw error;
                    }finally{releaseComplete.Set();releaseWithdrawal.Set();}
                }
            });
#endif
            suite.Run("shared completion follows writer authority and closes future permits",()=>{
                using(var f=new Fixture())
                {Exception error=null;var writer=new Thread(()=>{try{
                    Check.True(f.Fence.TryPublishCallsReturned());Check.Equal(ViveRawRevocation.Outcome.CallsReturned,f.Fence.CompletionOutcome);
                    Check.True(f.Fence.IsRevoked&&!f.Task.NamedRawRevocation.IsRevoked);ViveRawIoFence.Permit permit;
                    Check.True(!f.Fence.TryReserve(ViveRawIoFence.IoOperation.DataWrite,out permit)&&!f.Fence.TryPublishCallsReturned());
                }catch(Exception e){error=e;}});Check.True(f.Fence.BindWriter(writer));
                    Check.True(!f.Fence.TryPublishCallsReturned());writer.Start();Check.True(writer.Join(3000));if(error!=null)throw error;
                    f.Task.NamedRawRevocation.Revoke();Check.Equal(ViveRawRevocation.Outcome.Revoked,f.Fence.CompletionOutcome);}
            });
            suite.Run("shared revocation wins before writer completion",()=>{
                using(var f=new Fixture())
                {var ready=new ManualResetEvent(false);var release=new ManualResetEvent(false);bool result=true;
                    var writer=new Thread(()=>{ready.Set();release.WaitOne();result=f.Fence.TryPublishCallsReturned();});
                    Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(ready.WaitOne(3000));f.Task.NamedRawRevocation.Revoke();
                    release.Set();Check.True(writer.Join(3000));Check.True(!result);Check.Equal(ViveRawRevocation.Outcome.Revoked,f.Fence.CompletionOutcome);}
            });
            suite.Run("shared completion does not discard a simulation page",()=>{
                using(var f=new Fixture())
                {var owner=new object();var token=new object();var payload=new object();
                    Check.True(f.Task.NamedRawRevocation.TryAttachSimulationWindow(owner));
                    Check.True(f.Task.NamedRawRevocation.TryPublishSimulationPage(owner,token,payload));bool first=true,second=false;
                    var writer=new Thread(()=>{first=f.Fence.TryPublishCallsReturned();f.Task.NamedRawRevocation.CloseSimulationWindow(owner);second=f.Fence.TryPublishCallsReturned();});
                    Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(writer.Join(3000));Check.True(!first&&second);
                    object ignored;Check.True(!f.Task.NamedRawRevocation.TryClaimSimulationPage(owner,token,out ignored));}
            });
            suite.Run("shared completion rejects active synthetic permit",()=>{
                using(var f=new Fixture())
                {Exception error=null;var writer=new Thread(()=>{try{ViveRawIoFence.Permit p;
                    Check.True(f.Fence.TryReserve(ViveRawIoFence.IoOperation.DataWrite,out p));Check.True(!f.Fence.TryPublishCallsReturned());
                    Check.Equal(ViveRawIoFence.IoOutcome.ApiReturned,f.Fence.Finish(p,true,true));Check.True(f.Fence.TryPublishCallsReturned());
                }catch(Exception e){error=e;}});Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(writer.Join(3000));if(error!=null)throw error;}
            });
            suite.Run("completed authority cannot enable new worker status evidence",()=>{
                using(var f=new Fixture())
                {bool completed=false;var writer=new Thread(()=>completed=f.Fence.TryPublishCallsReturned());
                    Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(writer.Join(3000)&&completed);
                    object run;Check.True(!f.Worker.TryEnableStatusEvidence(8,f.Task.NamedRawRevocation,out run)&&run==null);
                    Check.True(!f.Task.NamedRawRevocation.IsRevoked&&f.Fence.IsRevoked);}
            });
            suite.Run("completion invalidates already-enabled worker status evidence",()=>{
                using(var f=new Fixture())
                {object run;Check.True(f.Worker.TryEnableStatusEvidence(8,f.Task.NamedRawRevocation,out run));
                    bool completed=false;var writer=new Thread(()=>completed=f.Fence.TryPublishCallsReturned());
                    Check.True(f.Fence.BindWriter(writer));writer.Start();Check.True(writer.Join(3000)&&completed);
                    ViveProEyeWorker.StatusTicket ticket;
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Unavailable,f.Worker.TryReserveStatusEvidence(run,out ticket));Check.True(ticket==null);}
            });
        }
    }
}
