using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // No filesystem, runtime service, participant, or device is touched.
    public static class ViveRawRelativeFileCapabilityTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("relative raw capability is hard-gated before path or authority processing",()=>{
                ViveRawRelativeFileCapability result;
                int before=ViveRawRelativeFileCapability.RetainedCount;
                Check.True(!ViveRawRelativeFileCapability.ProductionGateOpen);
                Check.True(!ViveRawRelativeFileCapability.TryPrepare(null,null,null,"not-a-path",null,out result));
                Check.Equal((ViveRawRelativeFileCapability)null,result);
                Check.Equal(before,ViveRawRelativeFileCapability.RetainedCount);
            });
            suite.Run("relative raw capability accepts bounded first manifest and data frames",()=>{
                Check.True(!ViveRawRelativeFileCapability.IsLegalWriteLength(0));
                Check.True(ViveRawRelativeFileCapability.IsLegalWriteLength(1));
                Check.True(ViveRawRelativeFileCapability.IsLegalWriteLength(1048576+40));
                Check.True(ViveRawRelativeFileCapability.IsLegalWriteLength(2*(1048576+40)+8));
                Check.True(!ViveRawRelativeFileCapability.IsLegalWriteLength(2*(1048576+40)+9));
            });
            suite.Run("relative raw native outputs stay stable through synthetic delayed completion and GC",()=>{
                var handleSlot=new ViveRawNativeOutputStorage(IntPtr.Size);
                var countSlot=new ViveRawNativeOutputStorage(sizeof(uint));
                IntPtr handleAddress=handleSlot.Address,countAddress=countSlot.Address;
                var ready=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                Exception workerError=null;
                var worker=new Thread(()=>{
                    try{ready.Set();if(!release.WaitOne(3000))throw new Exception("fixture timeout");
                        Marshal.WriteIntPtr(handleAddress,new IntPtr(12345));Marshal.WriteInt32(countAddress,617);}
                    catch(Exception e){workerError=e;}
                });
                try
                {
                    worker.Start();Check.True(ready.WaitOne(3000));GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                    release.Set();Check.True(worker.Join(3000));Check.Equal((Exception)null,workerError);
                    Check.Equal(new IntPtr(12345),Marshal.ReadIntPtr(handleSlot.Address));Check.Equal(617,Marshal.ReadInt32(countSlot.Address));
                }
                finally
                {
                    release.Set();if(worker.IsAlive)worker.Join();ready.Dispose();release.Dispose();
                    // The fake worker has completed; no native I/O exists.
                    handleSlot.ReleaseAfterConfirmedCompletion();countSlot.ReleaseAfterConfirmedCompletion();
                }
                Check.Throws<ObjectDisposedException>(()=>{GC.KeepAlive(handleSlot.Address);});
                handleSlot.ReleaseAfterConfirmedCompletion();countSlot.ReleaseAfterConfirmedCompletion();
            });
            suite.Run("relative raw native output PInvokes use stable pointer parameters",()=>{
                var native=typeof(ViveRawRelativeFileCapability).GetNestedType("Native",BindingFlags.NonPublic);
                Check.True(native!=null);
                var nt=native.GetMethod("NtCreateFile",BindingFlags.Static|BindingFlags.NonPublic);
                var write=native.GetMethod("WriteFile",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
                Check.True(nt!=null&&write!=null);
                Check.Equal(typeof(IntPtr),nt.GetParameters()[0].ParameterType);
                Check.Equal(typeof(IntPtr),nt.GetParameters()[3].ParameterType);
                Check.Equal(typeof(IntPtr),write.GetParameters()[3].ParameterType);
            });
        }
    }
}
