using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Independent engineering capture: it has no study task, admission ticket or
    // network authority. Only an explicit Start creates the native client. All
    // client calls, including unregister and cleanup, stay on its single thread.
    // The owner drains immutable native acquisitions and pairs them with separately
    // observed scene/head snapshots. This class never invents a head pose.
    public sealed class ViveGazeValidationCapture : IDisposable
    {
        const int DefaultCapacity=4096,MaximumIdentities=262144;
        const double MaximumRunSeconds=600;
        readonly object gate=new object();
        readonly IClock clock;
        readonly Func<IViveProEyeClient> factory;
        readonly ViveRawEyeAcquisition[] queue;
        readonly HashSet<ulong> identities=new HashSet<ulong>();
        readonly ManualResetEvent stopSignal=new ManualResetEvent(false);
        Thread thread;
        int head,count,stopRequested;
        bool disposed,ready,completed,drained,cleanupConfirmed,hasFrame,highResolutionPollDelay;
        int lastFrame,maximumPending;
        long polls,total,dequeued,duplicates,gaps,discontinuities,rejected;
        double startedAt,lastPublished=-1;
        string fault,nativeDiagnostic,diagnostic="Stopped. Explicit validation recording start is required.";
        ViveProEyePoll latest;

        public ViveGazeValidationCapture(MonotonicClock clock)
            :this(clock,()=>new SrAnipalViveProEyeProvider(clock,()=>null),DefaultCapacity){}
        internal ViveGazeValidationCapture(IClock clock,Func<IViveProEyeClient> factory,int capacity=DefaultCapacity)
        {
            if(clock==null||factory==null)throw new ArgumentNullException();
            if(capacity<1||capacity>65536)throw new ArgumentOutOfRangeException("capacity");
            this.clock=clock;this.factory=factory;queue=new ViveRawEyeAcquisition[capacity];
        }
        public bool Started {get{lock(gate)return ready&&!completed;}}
        public bool HasExited {get{lock(gate)return completed;}}
        public bool ProviderDrained {get{lock(gate)return drained;}}
        public bool CleanupConfirmed {get{lock(gate)return cleanupConfirmed;}}
        public string Fault {get{lock(gate)return fault;}}
        public string Diagnostic {get{lock(gate)return diagnostic;}}
        public string LastNativeDiagnostic {get{lock(gate)return nativeDiagnostic;}}
        public bool UsesHighResolutionPollDelay {get{lock(gate)return highResolutionPollDelay;}}
        public ViveProEyePoll Latest {get{lock(gate)return latest;}}
        public int PendingCount {get{lock(gate)return count;}}
        public int MaximumPendingCount {get{lock(gate)return maximumPending;}}
        public long NativePolls {get{lock(gate)return polls;}}
        public long AcquisitionCount {get{lock(gate)return total;}}
        public long DequeuedCount {get{lock(gate)return dequeued;}}
        public long DuplicatePolls {get{lock(gate)return duplicates;}}
        public long NativeSequenceGapCount {get{lock(gate)return gaps;}}
        public long NativeSequenceDiscontinuities {get{lock(gate)return discontinuities;}}
        // First rejected publication is counted; after a fault the unobserved tail
        // is unknown, so this is not an estimate of all device sample loss.
        public long RejectedAcquisitions {get{lock(gate)return rejected;}}

        public bool Start()
        {
            lock(gate)
            {
                if(disposed||thread!=null||completed||Volatile.Read(ref stopRequested)!=0)return false;
                startedAt=clock.Now;
                if(!ValidTime(startedAt)){fault="invalid_start_clock";diagnostic=fault;completed=true;stopSignal.Dispose();return false;}
                thread=new Thread(Run){IsBackground=true,Name="VIVE fixed-target validation capture"};
                diagnostic="Starting the installed HTC eye client.";
                try{thread.Start();return true;}
                catch(Exception error)
                {
                    fault="capture_thread_start_failed_"+error.GetType().Name;diagnostic=fault;
                    completed=true;cleanupConfirmed=drained=true;stopSignal.Dispose();return false;
                }
            }
        }
        // Nonblocking. Normal and user-aborted runs both preserve acquired data;
        // the caller records its own completion/abort reason. Output is complete
        // only after HasExited, ProviderDrained, CleanupConfirmed, no Fault and
        // an empty owner queue. No restart is permitted on this one-shot object.
        public void RequestStop()
        {
            Interlocked.Exchange(ref stopRequested,1);
            lock(gate){if(!completed)stopSignal.Set();}
        }
        public bool TryDequeue(out ViveRawEyeAcquisition value)
        {
            lock(gate)
            {
                value=null;if(count==0)return false;
                value=queue[head];queue[head]=null;head=(head+1)%queue.Length;count--;dequeued++;return true;
            }
        }
        public void Dispose()
        {
            lock(gate)
            {
                if(disposed)return;disposed=true;
                Interlocked.Exchange(ref stopRequested,1);
                if(completed)return;
                if(thread==null){completed=true;stopSignal.Dispose();return;}
                stopSignal.Set();
            }
        }

        void Run()
        {
            IViveProEyeClient client=null;ViveNativePollDelay delay=null;
            bool initialized=false,sealing=false;
            try
            {
                if(Volatile.Read(ref stopRequested)!=0){lock(gate){drained=true;cleanupConfirmed=true;}return;}
                client=factory();if(client==null)throw new InvalidOperationException("missing_client");
                if(Volatile.Read(ref stopRequested)!=0){lock(gate)drained=true;return;}
                initialized=client.Start();
                string nativeMessage=client.Diagnostic;
                lock(gate)nativeDiagnostic=nativeMessage;
                if(!initialized){Fail("native_start_failed");return;}
                var seal=client as IViveProEyeAcquisitionSeal;
                var live=client as IViveProEyeLiveSnapshot;
                lock(gate){ready=true;diagnostic="Recording native eye callbacks for fixed-target validation.";}
                delay=new ViveNativePollDelay(stopSignal);
                lock(gate)highResolutionPollDelay=delay.HighResolution;
                int burst=0;
                while(true)
                {
                    double now=clock.Now;
                    if(!ValidTime(now)||now<startedAt)throw new InvalidOperationException("invalid_capture_clock");
                    if(now-startedAt>MaximumRunSeconds){Fail("capture_time_budget_exceeded");break;}
                    if(Volatile.Read(ref stopRequested)!=0&&!sealing)
                    {
                        sealing=true;
                        lock(gate)diagnostic="Stopping callbacks and draining their queued tail.";
                        if(seal==null){lock(gate)drained=true;break;}
                        if(!seal.RequestAcquisitionSeal()){Fail("native_callback_seal_failed");break;}
                    }
                    if(sealing&&seal.AcquisitionSealDrained){lock(gate)drained=true;break;}
                    var poll=client.Poll();
                    double published=clock.Now;
                    nativeMessage=client.Diagnostic;
                    ViveProEyePoll current= poll,livePoll;
                    if(live!=null&&live.TryGetLiveSnapshot(out livePoll))current=livePoll;
                    lock(gate){latest=current;polls++;nativeDiagnostic=nativeMessage;}
                    if(!Publish(poll,published))break;
                    if(live!=null&&live.AcquisitionFaulted){Fail("native_callback_acquisition_fault");break;}
                    // A bounded burst drains callback packets instead of sampling
                    // just one Latest value per Unity frame or scheduler tick.
                    if(seal!=null&&seal.HasBufferedAcquisition&&burst<32){burst++;continue;}
                    burst=0;
                    if(!sealing)delay.Wait(5);
                    lock(gate)highResolutionPollDelay=delay.HighResolution;
                }
            }
            catch(Exception error){Fail("capture_exception_"+error.GetType().Name);}
            finally
            {
                if(delay!=null)delay.Dispose();
                bool cleanup=client==null;
                if(client!=null)
                {
                    try
                    {
                        client.Dispose();
                        var status=client as IViveProEyeCleanupStatus;
                        cleanup=status!=null&&status.RestartSafe;
                    }
                    catch(Exception error){Fail("cleanup_exception_"+error.GetType().Name);}
                }
                lock(gate)
                {
                    cleanupConfirmed=cleanup;
                    if(!cleanup&&fault==null)fault="native_cleanup_unconfirmed";
                    ready=false;completed=true;
                    diagnostic=fault??(drained?"Native callbacks drained and client cleanup returned.":"Capture stopped without a complete callback drain.");
                    stopSignal.Dispose();
                }
            }
        }
        bool Publish(ViveProEyePoll poll,double published)
        {
            lock(gate)
            {
                if(!ValidTime(published)||published<lastPublished){SetFault("invalid_publication_clock");return false;}
                lastPublished=published;
                if(poll==null||poll.Eye==null)return true;
                var eye=poll.Eye;
                if(!ValidTime(eye.ObservedLocalSeconds)||eye.ObservedLocalSeconds<startedAt||eye.ObservedLocalSeconds>published)
                {SetFault("invalid_eye_arrival_clock");rejected++;return false;}
                ulong identity=((ulong)(uint)eye.FrameSequence<<32)|(uint)eye.Timestamp;
                if(identities.Contains(identity)){duplicates++;return true;}
                if(count==queue.Length||identities.Count>=MaximumIdentities)
                {rejected++;SetFault(count==queue.Length?"validation_acquisition_queue_overflow":"validation_identity_budget_exceeded");return false;}
                if(hasFrame)
                {
                    long delta=(long)eye.FrameSequence-lastFrame;
                    if(delta>1)gaps+=delta-1;else if(delta<=0)discontinuities++;
                }
                hasFrame=true;lastFrame=eye.FrameSequence;identities.Add(identity);
                var eyeOnly=new ViveProEyePoll(null,eye,poll.Runtime,poll.Calibration,poll.EyeDeviceConnected,
                    poll.NativeResultCode,poll.NativeRuntimeStatus,poll.NativeDiagnostics);
                queue[(head+count)%queue.Length]=new ViveRawEyeAcquisition(total++,published,eyeOnly,polls,duplicates,gaps,discontinuities);
                count++;maximumPending=Math.Max(maximumPending,count);return true;
            }
        }
        void Fail(string reason){lock(gate)SetFault(reason);}
        void SetFault(string reason){if(fault==null)fault=reason;diagnostic=fault;}
        static bool ValidTime(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value)&&value>=0;}
    }
}
