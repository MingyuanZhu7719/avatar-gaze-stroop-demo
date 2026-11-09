using System;
using System.Runtime.Serialization;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveRawIoFenceTests
    {
        // Identity placeholder only. No admission constructor, worker, file,
        // participant, network or headset is used in these fake-I/O tests.
        static ViveNamedSessionFrameAdmission Identity()
        {return (ViveNamedSessionFrameAdmission)FormatterServices.GetUninitializedObject(typeof(ViveNamedSessionFrameAdmission));}
        public static void Register(TestSuite suite)
        {
            suite.Run("raw IO shared revocation before fence attachment cannot be revived",()=>{
                var shared=new ViveRawRevocation();shared.Revoke();var fence=new ViveRawIoFence(Identity(),shared);
                var thread=new Thread(()=>{});
                Check.True(!fence.BindWriter(thread)&&fence.IsRevoked&&!fence.HasReservation);
            });
            suite.Run("raw IO fence revocation precedes new reservation",()=>{
                var identity=Identity();var shared=new ViveRawRevocation();var fence=new ViveRawIoFence(identity,shared);Exception error=null;bool reserved=true;
                var thread=new Thread(()=>{try{ViveRawIoFence.Permit p;reserved=fence.TryReserve(ViveRawIoFence.IoOperation.Create,out p);}catch(Exception e){error=e;}});
                Check.True(fence.Matches(identity)&&!fence.Matches(Identity())&&fence.MatchesAuthority(identity,shared)&&
                    !fence.MatchesAuthority(identity,new ViveRawRevocation())&&fence.BindWriter(thread));
                shared.Revoke();thread.Start();Check.True(thread.Join(3000));if(error!=null)throw error;
                Check.True(!reserved&&fence.IsRevoked&&!fence.HasReservation&&!fence.OutcomeUncertain);
            });
            suite.Run("raw IO fence allows one Create and prevents reuse",()=>{
                var fence=new ViveRawIoFence(Identity(),new ViveRawRevocation());Exception error=null;
                var thread=new Thread(()=>{try{
                    ViveRawIoFence.Permit p;Check.True(fence.TryReserve(ViveRawIoFence.IoOperation.Create,out p));
                    Check.Equal(ViveRawIoFence.IoOutcome.ApiReturned,fence.Finish(p,true,true));
                    Check.True(p.ApiReturned&&p.ApiSucceeded&&!fence.TryReserve(ViveRawIoFence.IoOperation.Create,out p));
                    Check.True(!fence.TryReserve(ViveRawIoFence.IoOperation.DataWrite,out p));
                }catch(Exception e){error=e;}});
                Check.True(fence.BindWriter(thread));thread.Start();Check.True(thread.Join(3000));if(error!=null)throw error;
                Check.True(fence.IsFaulted&&fence.IsRevoked&&!fence.HasReservation);
            });
            suite.Run("raw IO fence uses distinct single-use permits for directory handles",()=>{
                var fence=new ViveRawIoFence(Identity(),new ViveRawRevocation());Exception error=null;
                var thread=new Thread(()=>{try{
                    ViveRawIoFence.Permit first,second;
                    Check.True(fence.TryReserve(ViveRawIoFence.IoOperation.CloseDirectory,out first));
                    Check.Equal(ViveRawIoFence.IoOutcome.ApiReturned,fence.Finish(first,true,true));
                    Check.True(fence.TryReserve(ViveRawIoFence.IoOperation.CloseDirectory,out second));
                    Check.True(!Object.ReferenceEquals(first,second)&&second.Sequence>first.Sequence);
                    Check.Equal(ViveRawIoFence.IoOutcome.ApiReturned,fence.Finish(second,true,true));
                }catch(Exception e){error=e;}});
                Check.True(fence.BindWriter(thread));thread.Start();Check.True(thread.Join(3000));if(error!=null)throw error;
                Check.True(!fence.IsFaulted&&!fence.IsRevoked&&!fence.HasReservation);
            });
            suite.Run("raw IO fence keeps racing API result uncertain after revoke",()=>{
                var shared=new ViveRawRevocation();var fence=new ViveRawIoFence(Identity(),shared);var entered=new ManualResetEvent(false);var resume=new ManualResetEvent(false);
                Exception error=null;ViveRawIoFence.IoOutcome result=ViveRawIoFence.IoOutcome.Rejected;
                var thread=new Thread(()=>{try{
                    ViveRawIoFence.Permit p;Check.True(fence.TryReserve(ViveRawIoFence.IoOperation.DataWrite,out p));
                    entered.Set();Check.True(resume.WaitOne(3000));result=fence.Finish(p,true,true);
                    ViveRawIoFence.Permit next;Check.True(!fence.TryReserve(ViveRawIoFence.IoOperation.Flush,out next));
                }catch(Exception e){error=e;}});
                try
                {
                    Check.True(fence.BindWriter(thread));thread.Start();Check.True(entered.WaitOne(3000));
                    var before=DateTime.UtcNow;shared.Revoke();Check.True((DateTime.UtcNow-before).TotalMilliseconds<500);
                    Check.True(fence.IsRevoked&&fence.HasReservation&&fence.OutcomeUncertain);
                    resume.Set();Check.True(thread.Join(3000));if(error!=null)throw error;
                    Check.Equal(ViveRawIoFence.IoOutcome.Unknown,result);Check.True(!fence.HasReservation&&fence.OutcomeUncertain);
                }
                finally{resume.Set();}
            });
            suite.Run("raw IO fence failed or absent API return is terminal and not retried",()=>{
                foreach(bool returned in new[]{false,true})
                {
                    var fence=new ViveRawIoFence(Identity(),new ViveRawRevocation());Exception error=null;
                    var thread=new Thread(()=>{try{
                        ViveRawIoFence.Permit p;Check.True(fence.TryReserve(ViveRawIoFence.IoOperation.DataWrite,out p));
                        Check.Equal(ViveRawIoFence.IoOutcome.Unknown,fence.Finish(p,returned,false));
                        Check.True(p.ApiReturned==returned&&!p.ApiSucceeded);
                        Check.True(!fence.TryReserve(ViveRawIoFence.IoOperation.DataWrite,out p));
                    }catch(Exception e){error=e;}});
                    Check.True(fence.BindWriter(thread));thread.Start();Check.True(thread.Join(3000));if(error!=null)throw error;
                    Check.True(fence.IsFaulted&&fence.OutcomeUncertain);
                }
            });
            suite.Run("raw IO fence refuses forged and replayed permits",()=>{
                var fence=new ViveRawIoFence(Identity(),new ViveRawRevocation());Exception error=null;
                var thread=new Thread(()=>{try{
                    ViveRawIoFence.Permit actual;Check.True(fence.TryReserve(ViveRawIoFence.IoOperation.VerifyFileId,out actual));
                    var fake=new ViveRawIoFence.Permit(fence,ViveRawIoFence.IoOperation.VerifyFileId,actual.Sequence);
                    Check.Equal(ViveRawIoFence.IoOutcome.Rejected,fence.Finish(fake,true,true));
                    Check.Equal(ViveRawIoFence.IoOutcome.Unknown,fence.Finish(actual,true,true));
                    Check.Equal(ViveRawIoFence.IoOutcome.Rejected,fence.Finish(actual,true,true));
                }catch(Exception e){error=e;}});
                Check.True(fence.BindWriter(thread));thread.Start();Check.True(thread.Join(3000));if(error!=null)throw error;
                Check.True(fence.IsFaulted&&fence.IsRevoked&&fence.OutcomeUncertain);
            });
            suite.Run("raw IO fence rejects completion by a different thread",()=>{
                var fence=new ViveRawIoFence(Identity(),new ViveRawRevocation());var entered=new ManualResetEvent(false);var resume=new ManualResetEvent(false);
                ViveRawIoFence.Permit permit=null;Exception error=null;ViveRawIoFence.IoOutcome ownerResult=ViveRawIoFence.IoOutcome.Rejected;
                var thread=new Thread(()=>{try{
                    Check.True(fence.TryReserve(ViveRawIoFence.IoOperation.DataWrite,out permit));entered.Set();
                    Check.True(resume.WaitOne(3000));ownerResult=fence.Finish(permit,true,true);
                }catch(Exception e){error=e;}});
                try
                {
                    Check.True(fence.BindWriter(thread));thread.Start();Check.True(entered.WaitOne(3000));
                    Check.Equal(ViveRawIoFence.IoOutcome.Rejected,fence.Finish(permit,true,true));
                    Check.True(fence.IsRevoked&&fence.IsFaulted&&fence.OutcomeUncertain);
                    resume.Set();Check.True(thread.Join(3000));if(error!=null)throw error;
                    Check.Equal(ViveRawIoFence.IoOutcome.Unknown,ownerResult);
                }
                finally{resume.Set();}
            });
        }
    }
}
