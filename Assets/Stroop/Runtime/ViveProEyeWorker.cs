using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public interface IViveProEyeClient : IViveProEyeProvider, IDisposable
    {
        bool Start();
        string Diagnostic {get;}
    }

    // Optional for clients which contain cleanup failures instead of throwing.
    // The property must remain readable after Dispose. false means the native
    // lifecycle is uncertain and this worker must not permit another start.
    public interface IViveProEyeCleanupStatus
    {
        bool RestartSafe {get;}
    }

    // In-memory counts of committed worker publications for one run, not device
    // frequency, hardware freshness, valid gaze, calibration quality or accuracy.
    // NativePolls counts provider Poll returns, not individual vendor API calls.
    public sealed class ViveWorkerCaptureDiagnostics
    {
        public long NativePolls {get;}
        public long SuccessfulEyeReads {get;}
        public long SequenceChanges {get;}
        public long TimestampChanges {get;}
        internal ViveWorkerCaptureDiagnostics(long nativePolls,long successfulEyeReads,long sequenceChanges,long timestampChanges)
        {NativePolls=nativePolls;SuccessfulEyeReads=successfulEyeReads;SequenceChanges=sequenceChanges;TimestampChanges=timestampChanges;}
    }

    // The client factory, initialization, polling and disposal all run serially on
    // one background thread. The supplied clock must be thread-safe (e.g. Stopwatch)
    // and shared with that client and the source; do not use a Unity-only clock API.
    // Poll/headReader remain on the thread which constructed this worker. This class
    // never refreshes a cached frame's observation time or treats a repeated frame as new.
    public sealed class ViveProEyeWorker : IViveProEyeProvider, IDisposable
    {
        sealed class Run
        {
            internal Thread Thread;
            internal readonly ManualResetEvent StopSignal=new ManualResetEvent(false);
            internal bool Starting=true,Ready,StopRequested,Completed;
            internal ViveProEyePoll Latest;
            internal string Diagnostic="Starting eye client on its background thread.";
            internal long NativePolls,SuccessfulEyeReads,SequenceChanges,TimestampChanges;
            internal bool HasSuccessfulEyeRead;
            internal int LastEyeSequence,LastEyeTimestamp;
        }

        const int MinimumPollDelayMilliseconds=5;
        static readonly ViveWorkerCaptureDiagnostics NoCaptureDiagnostics=new ViveWorkerCaptureDiagnostics(0,0,0,0);
        readonly object gate=new object();
        readonly Func<ViveHeadFrame> headReader;
        readonly Func<IViveProEyeClient> factory;
        readonly int headThreadId;
        Run current;
        ViveWorkerCaptureDiagnostics completedDiagnostics=NoCaptureDiagnostics;
        bool disposed,cleanupFailed;
        string idleDiagnostic="Stopped; explicit Start is required.",headDiagnostic;
        public string ClockId {get;private set;}
        public bool Starting {get{lock(gate){Reap();return current!=null&&current.Starting&&!current.StopRequested;}}}
        // Started means native client initialization succeeded, not valid eye tracking.
        public bool Started {get{lock(gate){Reap();return current!=null&&current.Ready&&!current.StopRequested;}}}
        public bool Stopping {get{lock(gate){Reap();return current!=null&&(current.StopRequested||(!current.Starting&&!current.Ready));}}}
        public bool CanStart {get{lock(gate){Reap();return !disposed&&!cleanupFailed&&current==null;}}}
        // Completion is independent of permission to restart after cleanup failure/disposal.
        public bool HasExited {get{lock(gate){Reap();return current==null;}}}
        public string Diagnostic
        {get{lock(gate){Reap();string state=current==null?idleDiagnostic:current.Diagnostic;return headDiagnostic==null?state:headDiagnostic+" "+state;}}}
        // All four values belong to one atomic snapshot. A new run starts at zero;
        // no completed run or gaze payload is retained by this diagnostic surface.
        public ViveWorkerCaptureDiagnostics CaptureDiagnostics
        {
            get
            {
                lock(gate)
                {
                    Reap();
                    return current==null?NoCaptureDiagnostics:new ViveWorkerCaptureDiagnostics(current.NativePolls,current.SuccessfulEyeReads,
                        current.SequenceChanges,current.TimestampChanges);
                }
            }
        }

        public ViveProEyeWorker(IClock clock,Func<ViveHeadFrame> mainThreadHead,Func<IViveProEyeClient> factory)
        {
            if(clock==null)throw new ArgumentNullException("clock");
            if(String.IsNullOrWhiteSpace(clock.ClockId))throw new ArgumentException("Explicit shared clock identity required","clock");
            if(mainThreadHead==null)throw new ArgumentNullException("mainThreadHead");
            if(factory==null)throw new ArgumentNullException("factory");
            ClockId=clock.ClockId;headReader=mainThreadHead;this.factory=factory;
            headThreadId=Thread.CurrentThread.ManagedThreadId;
        }

        // true acknowledges the asynchronous request, not completion or tracking readiness.
        public bool Start()
        {
            lock(gate)
            {
                Reap();if(disposed||cleanupFailed||current!=null)return false;
                var run=new Run();completedDiagnostics=NoCaptureDiagnostics;
                run.Thread=new Thread(()=>RunClient(run)){IsBackground=true,Name="VIVE Pro Eye native polling"};
                current=run;headDiagnostic=null;
                try{run.Thread.Start();return true;}
                catch(Exception error)
                {
                    current=null;run.StopSignal.Dispose();
                    idleDiagnostic="Cannot start eye worker: "+error.GetType().Name;return false;
                }
            }
        }

        public ViveProEyePoll Poll()
        {
            // A wrongly placed Poll cannot invoke Unity APIs from a background thread.
            if(Thread.CurrentThread.ManagedThreadId!=headThreadId)
                throw new InvalidOperationException("VIVE head observations must be polled on the worker's creating thread");
            ViveHeadFrame head=null;string failure=null;
            try{head=headReader();}
            catch(Exception error){failure="Head observation failed: "+error.GetType().Name+".";}
            lock(gate)
            {
                Reap();headDiagnostic=failure;
                var latest=current==null||!current.Ready||current.StopRequested?null:current.Latest;
                if(latest==null)return new ViveProEyePoll(head,null,ViveEyeRuntimeState.Unavailable,ViveEyeCalibrationState.Unknown,false);
                return new ViveProEyePoll(head,latest.Eye,latest.Runtime,latest.Calibration,latest.EyeDeviceConnected,latest.NativeResultCode,latest.NativeRuntimeStatus);
            }
        }

        // Default is nonblocking. A bounded join is available for controlled shutdown
        // and tests; an in-flight native call is never aborted or unloaded underneath.
        // false means cleanup/thread exit is still pending, so Start remains refused.
        public bool Stop(int maximumWaitMilliseconds=0)
        {
            if(maximumWaitMilliseconds<0||maximumWaitMilliseconds>1000)throw new ArgumentOutOfRangeException("maximumWaitMilliseconds");
            Thread thread;
            lock(gate)
            {
                Reap();if(current==null)return true;
                RequestStop(current);thread=current.Thread;
            }
            if(thread==Thread.CurrentThread)return false;
            bool ended=thread.Join(maximumWaitMilliseconds);
            if(ended)lock(gate){Reap();}
            return ended;
        }

        // Atomically prevent further publications and return the last committed
        // aggregate. No native call or thread join occurs while holding the lock.
        public ViveWorkerCaptureDiagnostics StopAndCaptureDiagnostics()
        {
            lock(gate)
            {
                Reap();
                if(current==null)return completedDiagnostics;
                RequestStop(current);
                return Snapshot(current);
            }
        }

        public void Dispose()
        {
            // Do not join: Unity teardown must not wait on an unresponsive native DLL.
            // The background thread retains the client until its own finally completes.
            lock(gate)
            {
                if(disposed)return;disposed=true;Reap();
                if(current!=null)RequestStop(current);
            }
        }

        void RequestStop(Run run)
        {
            run.StopRequested=true;run.Ready=false;run.Starting=false;run.Latest=null;
            if(!run.Completed)
            {
                run.Diagnostic="Stopping; waiting for any native call and client cleanup to finish.";
                run.StopSignal.Set();
            }
        }

        void RunClient(Run run)
        {
            IViveProEyeClient client=null;string failure=null,finalDiagnostic=null;
            try
            {
                if(run.StopSignal.WaitOne(0))return;
                client=factory();if(client==null)throw new InvalidOperationException("Client factory returned null");
                if(run.StopSignal.WaitOne(0))return;
                bool initialized=client.Start();string diagnostic=ReadDiagnostic(client);
                lock(gate)
                {
                    run.Starting=false;
                    if(!run.StopRequested){run.Ready=initialized;run.Diagnostic=diagnostic;}
                }
                if(!initialized){failure="Eye client initialization failed. "+diagnostic;return;}
                while(!run.StopSignal.WaitOne(0))
                {
                    ViveProEyePoll poll=client.Poll();diagnostic=ReadDiagnostic(client);
                    lock(gate)
                    {
                        // Stop clears publication immediately. A late native return
                        // cannot restore a frame after stop/dispose was requested.
                        if(run.StopRequested)break;
                        run.Latest=poll;run.Diagnostic=diagnostic;
                        run.NativePolls++;
                        if(poll!=null&&poll.Eye!=null&&poll.NativeResultCode.HasValue&&poll.NativeResultCode.Value==0)
                        {
                            run.SuccessfulEyeReads++;
                            if(run.HasSuccessfulEyeRead)
                            {
                                if(poll.Eye.FrameSequence!=run.LastEyeSequence)run.SequenceChanges++;
                                if(poll.Eye.Timestamp!=run.LastEyeTimestamp)run.TimestampChanges++;
                            }
                            run.HasSuccessfulEyeRead=true;run.LastEyeSequence=poll.Eye.FrameSequence;run.LastEyeTimestamp=poll.Eye.Timestamp;
                        }
                    }
                    if(run.StopSignal.WaitOne(MinimumPollDelayMilliseconds))break;
                }
            }
            catch(Exception error)
            {
                failure="Eye worker failed: "+error.GetType().Name+".";
                lock(gate){run.Latest=null;run.Ready=false;run.Starting=false;run.Diagnostic=failure;}
            }
            finally
            {
                // No client call, including Diagnostic and Dispose, occurs under gate.
                bool clientDisposed=false;
                try
                {
                    if(client!=null){client.Dispose();clientDisposed=true;}
                    else finalDiagnostic="Stopped; no native client was created.";
                }
                catch(Exception error)
                {
                    finalDiagnostic="Client cleanup failed: "+error.GetType().Name+". Restart is blocked for this worker.";
                    lock(gate){cleanupFailed=true;}
                }
                if(clientDisposed)
                {
                    bool restartUnsafe=false;
                    var cleanup=client as IViveProEyeCleanupStatus;
                    if(cleanup!=null)
                    {
                        try
                        {
                            if(!cleanup.RestartSafe){restartUnsafe=true;lock(gate){cleanupFailed=true;}}
                        }
                        catch(Exception error)
                        {
                            restartUnsafe=true;lock(gate){cleanupFailed=true;}
                            failure="Client cleanup status unavailable: "+error.GetType().Name+". Restart is blocked for this worker.";
                        }
                    }
                    try{finalDiagnostic=ReadDiagnostic(client);}
                    catch(Exception error)
                    {finalDiagnostic=(restartUnsafe?"Client cleanup is unsafe; restart is blocked. ":"Stopped; client cleanup completed. ")+"Diagnostic unavailable: "+error.GetType().Name+".";}
                }
                lock(gate)
                {
                    run.Latest=null;run.Ready=false;run.Starting=false;run.Completed=true;
                    run.Diagnostic=failure==null?finalDiagnostic:failure+" "+finalDiagnostic;
                    // Stop only signals an incomplete run under this same lock.
                    run.StopSignal.Dispose();
                }
            }
        }

        // Keep the run reserved until the actual thread has exited, including its
        // disposal finally. This closes the restart window between cleanup and return.
        void Reap()
        {
            if(current!=null&&current.Completed&&!current.Thread.IsAlive)
            {completedDiagnostics=Snapshot(current);idleDiagnostic=current.Diagnostic;current=null;}
        }
        static ViveWorkerCaptureDiagnostics Snapshot(Run run)
        {return new ViveWorkerCaptureDiagnostics(run.NativePolls,run.SuccessfulEyeReads,run.SequenceChanges,run.TimestampChanges);}
        static string ReadDiagnostic(IViveProEyeClient client)
        {
            string diagnostic=client.Diagnostic;
            return String.IsNullOrWhiteSpace(diagnostic)?"Eye client supplied no diagnostic.":diagnostic;
        }
    }
}
