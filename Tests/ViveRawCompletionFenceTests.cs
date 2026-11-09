using System;
using System.Runtime.Serialization;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Exercises the actual per-operation fence only. These are in-memory
    // scheduling tests, not native calls, sink completion or durability.
    public static class ViveRawCompletionFenceTests
    {
        static readonly ViveRawIoFence.IoOperation[] Steps={ViveRawIoFence.IoOperation.TerminalWrite,
            ViveRawIoFence.IoOperation.Flush,ViveRawIoFence.IoOperation.Close,
            ViveRawIoFence.IoOperation.CloseDirectory,ViveRawIoFence.IoOperation.CloseAncestor,
            ViveRawIoFence.IoOperation.CloseAncestor};
        static ViveNamedSessionFrameAdmission Identity()
        {return (ViveNamedSessionFrameAdmission)FormatterServices.GetUninitializedObject(typeof(ViveNamedSessionFrameAdmission));}
        // mode: 0 withdraw immediately before reservation, 1 withdraw with
        // permit in flight, 2 API failure. Earlier operations only declare a
        // returned API result; no file or device is touched.
        static void Exercise(int selected,int mode)
        {
            var shared=new ViveRawRevocation();var fence=new ViveRawIoFence(Identity(),shared);
            var atBoundary=new ManualResetEvent(false);var resume=new ManualResetEvent(false);Exception error=null;
            var writer=new Thread(()=>{try{
                long last=0;
                for(int i=0;i<Steps.Length;i++)
                {
                    if(i==selected&&mode==0){atBoundary.Set();Check.True(resume.WaitOne(3000));}
                    ViveRawIoFence.Permit permit;
                    if(i==selected&&mode==0){Check.True(!fence.TryReserve(Steps[i],out permit));break;}
                    Check.True(fence.TryReserve(Steps[i],out permit));Check.True(permit.Sequence>last);last=permit.Sequence;
                    if(i==selected&&mode==1){atBoundary.Set();Check.True(resume.WaitOne(3000));}
                    var outcome=fence.Finish(permit,true,!(i==selected&&mode==2));
                    if(i==selected&&(mode==1||mode==2))
                    {
                        Check.Equal(ViveRawIoFence.IoOutcome.Unknown,outcome);
                        ViveRawIoFence.Permit retry;Check.True(!fence.TryReserve(Steps[i],out retry));break;
                    }
                    Check.Equal(ViveRawIoFence.IoOutcome.ApiReturned,outcome);
                }
            }catch(Exception e){error=e;}});
            try
            {
                Check.True(fence.BindWriter(writer));writer.Start();
                if(mode!=2){Check.True(atBoundary.WaitOne(3000));shared.Revoke();resume.Set();}
                Check.True(writer.Join(3000));if(error!=null)throw error;
                Check.True(fence.IsRevoked&&!fence.HasReservation);
                Check.Equal(mode!=0,fence.OutcomeUncertain);
            }
            finally{resume.Set();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("completion fence rejects each step after withdrawal before reservation",()=>{for(int i=0;i<Steps.Length;i++)Exercise(i,0);});
            suite.Run("completion fence marks each reserved step unknown on withdrawal",()=>{for(int i=0;i<Steps.Length;i++)Exercise(i,1);});
            suite.Run("completion fence stops after each declared API failure",()=>{for(int i=0;i<Steps.Length;i++)Exercise(i,2);});
        }
    }
}
