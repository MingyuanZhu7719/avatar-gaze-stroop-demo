using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Fake clients only: no installed SDK, headset, filesystem or native DLL.
    public static class ViveRawEyeShutdownTests
    {
        sealed class Client : IViveProEyeClient, IViveProEyeCleanupStatus
        {
            internal readonly ManualResetEvent PollEntered=new ManualResetEvent(false), PollRelease=new ManualResetEvent(false);
            internal readonly ManualResetEvent DisposeEntered=new ManualResetEvent(false), DisposeRelease=new ManualResetEvent(false);
            internal bool Safe=true,ThrowDispose,ThrowStatus;
            public string Diagnostic {get{return "synthetic";}}
            public bool RestartSafe {get{if(ThrowStatus)throw new InvalidOperationException();return Safe;}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){PollEntered.Set();PollRelease.WaitOne();return null;}
            public void Dispose(){DisposeEntered.Set();DisposeRelease.WaitOne();if(ThrowDispose)throw new InvalidOperationException();}
        }
        static bool AwaitFinished(ViveProEyeWorker worker,out bool safe)
        {
            var deadline=DateTime.UtcNow.AddSeconds(3);
            do {if(worker.TryFinishStopped(out safe))return true;Thread.Sleep(1);}while(DateTime.UtcNow<deadline);
            safe=false;return false;
        }
        static void CheckCleanupException(bool throwDispose)
        {
            var client=new Client{ThrowDispose=throwDispose,ThrowStatus=!throwDispose};
            var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>client);
            try
            {
                bool safe;Check.True(worker.Start());Check.True(client.PollEntered.WaitOne(3000));
                Check.True(worker.TryRequestStop());client.PollRelease.Set();Check.True(client.DisposeEntered.WaitOne(3000));
                client.DisposeRelease.Set();Check.True(AwaitFinished(worker,out safe));Check.True(!safe);Check.True(!worker.Start());
            }
            finally{client.PollRelease.Set();client.DisposeRelease.Set();worker.Stop(1000);worker.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE raw eye shutdown does not wait for a busy worker lock",()=>{
                var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>new Client());
                var held=new ManualResetEvent(false);var release=new ManualResetEvent(false);var queried=new ManualResetEvent(false);
                var gate=typeof(ViveProEyeWorker).GetField("gate",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(worker);
                var holder=new Thread(()=>{lock(gate){held.Set();release.WaitOne();}}){IsBackground=true};
                bool result=true;var query=new Thread(()=>{bool safe;result=worker.TryFinishStopped(out safe);queried.Set();}){IsBackground=true};
                try
                {
                    holder.Start();Check.True(held.WaitOne(3000));query.Start();
                    Check.True(queried.WaitOne(1000));Check.True(!result);
                }
                finally{release.Set();if(holder.IsAlive)holder.Join(1000);if(query.IsAlive)query.Join(1000);worker.Dispose();held.Dispose();release.Dispose();queried.Dispose();}
            });
            suite.Run("VIVE raw eye shutdown waits for fake poll and disposal without joining",()=>{
                var client=new Client();var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>client);
                try
                {
                    bool safe;Check.True(!worker.TryFinishStopped(out safe));Check.True(worker.Start());
                    Check.True(client.PollEntered.WaitOne(3000));Check.True(worker.TryRequestStop());
                    Check.True(!worker.TryFinishStopped(out safe));
                    client.PollRelease.Set();Check.True(client.DisposeEntered.WaitOne(3000));
                    Check.True(!worker.TryFinishStopped(out safe));
                    client.DisposeRelease.Set();Check.True(AwaitFinished(worker,out safe));Check.True(safe);
                    Check.True(!worker.Start());
                }
                finally{client.PollRelease.Set();client.DisposeRelease.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("VIVE raw eye shutdown reports unsafe client cleanup after exit",()=>{
                var client=new Client{Safe=false};var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>client);
                try
                {
                    bool safe;Check.True(worker.Start());Check.True(client.PollEntered.WaitOne(3000));
                    Check.True(worker.TryRequestStop());client.PollRelease.Set();Check.True(client.DisposeEntered.WaitOne(3000));
                    client.DisposeRelease.Set();Check.True(AwaitFinished(worker,out safe));Check.True(!safe);Check.True(!worker.Start());
                }
                finally{client.PollRelease.Set();client.DisposeRelease.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("VIVE raw eye shutdown reports Dispose exception as unsafe",()=>CheckCleanupException(true));
            suite.Run("VIVE raw eye shutdown reports cleanup status exception as unsafe",()=>CheckCleanupException(false));
        }
    }
}
