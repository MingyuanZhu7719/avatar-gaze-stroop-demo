using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Stable native output storage. It deliberately has no finalizer: a
    // pending/unknown OS call must retain the owner and this allocation until
    // process exit, rather than risk a delayed kernel write into freed memory.
    // The owner alone may release it, and only after completion is confirmed.
    internal sealed class ViveRawNativeOutputStorage
    {
        readonly IntPtr address;readonly Thread owner;int released;
        internal ViveRawNativeOutputStorage(int size)
        {
            if(size<1||size>256)throw new ArgumentOutOfRangeException("size");
            address=Marshal.AllocHGlobal(size);owner=Thread.CurrentThread;
            for(int i=0;i<size;i++)Marshal.WriteByte(address,i,0);
        }
        internal IntPtr Address
        {get{if(Volatile.Read(ref released)!=0)throw new ObjectDisposedException("native output");return address;}}
        internal void ReleaseAfterConfirmedCompletion()
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,owner))throw new InvalidOperationException("Native output owner required.");
            if(Interlocked.Exchange(ref released,1)==0)Marshal.FreeHGlobal(address);
        }
    }
}
