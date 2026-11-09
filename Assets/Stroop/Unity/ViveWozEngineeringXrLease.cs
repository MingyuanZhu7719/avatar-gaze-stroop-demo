#if STROOP_XR_PREVIEW
using System;
using System.Threading;

namespace AvatarStroop.Unity
{
    // Cooperative same-Windows-session exclusion for the two engineering
    // viewers only. This cannot detect or stop unrelated applications or
    // establish whether an XR runtime can serve another process.
    public sealed class ViveWozEngineeringXrLease : IDisposable
    {
        const string Name="AvatarStroop.ViveWozEngineering.Xr";
        Mutex mutex;
        readonly int owner;
        ViveWozEngineeringXrLease(Mutex held){mutex=held;owner=Thread.CurrentThread.ManagedThreadId;}
        public static ViveWozEngineeringXrLease TryAcquire(out string error)
        {
            error=null;Mutex candidate=null;
            try
            {
                candidate=new Mutex(false,Name);
                bool acquired;
                try{acquired=candidate.WaitOne(0);}
                catch(AbandonedMutexException){candidate.ReleaseMutex();candidate.Dispose();error="Prior engineering viewer exited unexpectedly. Check XR processes before starting again.";return null;}
                if(!acquired){candidate.Dispose();error="Another engineering viewer owns the XR lease in this Windows session.";return null;}
                return new ViveWozEngineeringXrLease(candidate);
            }
            catch(Exception e){if(candidate!=null)candidate.Dispose();error="XR lease unavailable: "+e.GetType().Name;return null;}
        }
        public void Dispose()
        {
            if(mutex==null)return;
            if(Thread.CurrentThread.ManagedThreadId!=owner)throw new InvalidOperationException("XR lease belongs to its owning thread.");
            mutex.ReleaseMutex();mutex.Dispose();mutex=null;
        }
    }
}
#endif
