using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AvatarStroop.Runtime
{
    // WaitOne(5) can round up to a Windows scheduler tick (~15.6 ms). A local
    // high-resolution waitable timer avoids changing the system timer period
    // and remains interruptible by Stop. This is a polling delay, not a sensor
    // sampling-rate guarantee. Unsupported OS versions retain the safe wait.
    internal sealed class ViveNativePollDelay : IDisposable
    {
        sealed class TimerHandle : WaitHandle
        {internal TimerHandle(IntPtr handle){SafeWaitHandle=new SafeWaitHandle(handle,true);}}
        readonly WaitHandle stop;
        TimerHandle timer;
        WaitHandle[] waits;
        internal bool HighResolution {get{return timer!=null;}}
        internal ViveNativePollDelay(WaitHandle stop)
        {
            this.stop=stop??throw new ArgumentNullException("stop");
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)return;
            try
            {
                IntPtr handle=CreateWaitableTimerEx(IntPtr.Zero,null,2,0x1F0003);
                if(handle==IntPtr.Zero)return;
                timer=new TimerHandle(handle);waits=new WaitHandle[]{stop,timer};
            }
            catch(EntryPointNotFoundException){}
            catch(DllNotFoundException){}
        }
        internal bool Wait(int milliseconds)
        {
            if(timer==null)return stop.WaitOne(milliseconds);
            long due=-10000L*milliseconds;
            if(!SetWaitableTimer(timer.SafeWaitHandle,ref due,0,IntPtr.Zero,IntPtr.Zero,false))
            {timer.Dispose();timer=null;waits=null;return stop.WaitOne(milliseconds);}
            return WaitHandle.WaitAny(waits)==0;
        }
        public void Dispose(){if(timer!=null){timer.Dispose();timer=null;waits=null;}}
        [DllImport("kernel32.dll",EntryPoint="CreateWaitableTimerExW",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern IntPtr CreateWaitableTimerEx(IntPtr attributes,string name,uint flags,uint access);
        [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWaitableTimer(SafeWaitHandle timer,ref long dueTime,int period,IntPtr completion,IntPtr context,[MarshalAs(UnmanagedType.Bool)] bool resume);
    }
}
