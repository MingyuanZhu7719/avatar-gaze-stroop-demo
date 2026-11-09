using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveProEyeWorkerTests
    {
        sealed class Client : IViveProEyeClient
        {
            internal readonly ManualResetEvent StartEntered=new ManualResetEvent(false),PollEntered=new ManualResetEvent(false),DisposeEntered=new ManualResetEvent(false),Disposed=new ManualResetEvent(false);
            internal Func<bool> OnStart;
            internal Func<int,ViveProEyePoll> OnPoll;
            internal Action OnDispose;
            internal Func<string> OnDiagnostic;
            internal int StartThread,PollThread,DisposeThread,PollCalls,DisposeCalls;
            public string Diagnostic {get{return OnDiagnostic==null?"fake client":OnDiagnostic();}}
            public bool Start(){StartThread=Thread.CurrentThread.ManagedThreadId;StartEntered.Set();return OnStart==null||OnStart();}
            public ViveProEyePoll Poll()
            {PollThread=Thread.CurrentThread.ManagedThreadId;int call=Interlocked.Increment(ref PollCalls);PollEntered.Set();return OnPoll==null?null:OnPoll(call);}
            public void Dispose()
            {
                DisposeThread=Thread.CurrentThread.ManagedThreadId;Interlocked.Increment(ref DisposeCalls);DisposeEntered.Set();
                try{if(OnDispose!=null)OnDispose();}finally{Disposed.Set();}
            }
        }
        sealed class CleanupStatusClient : IViveProEyeClient, IViveProEyeCleanupStatus
        {
            internal readonly Client Inner=new Client {OnStart=()=>false};
            internal bool StatusThrows;
            public bool RestartSafe {get{if(StatusThrows)throw new InvalidOperationException("private-cleanup-status");return false;}}
            public string Diagnostic {get{return "Native release failed; resources are quarantined.";}}
            public bool Start(){return Inner.Start();}
            public ViveProEyePoll Poll(){return Inner.Poll();}
            public void Dispose(){Inner.Dispose();}
        }
        static void Wait(WaitHandle signal,string message="Expected worker handshake")
        {Check.True(signal.WaitOne(3000),message);}
        static ViveProEyePoll Eye(double observed=2.5,int frame=20,int? timestamp=null,int? code=0)
        {
            var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));
            return new ViveProEyePoll(new ViveHeadFrame(999,observed,true,true,Rotation.Identity),
                new ViveEyeFrame(frame,timestamp??frame*8,observed,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,code,2);
        }
        static void CaptureCounts(ViveWorkerCaptureDiagnostics value,long polls,long successful,long sequences,long timestamps)
        {
            Check.Equal(polls,value.NativePolls);Check.Equal(successful,value.SuccessfulEyeReads);
            Check.Equal(sequences,value.SequenceChanges);Check.Equal(timestamps,value.TimestampChanges);
        }
        static void ReleasedStop(ViveProEyeWorker worker,params ManualResetEvent[] releases)
        {foreach(var release in releases)release.Set();Check.True(worker.Stop(1000),"Worker must exit after fake native calls are released");worker.Dispose();}
        static void ReadableFromAnotherThread(ViveProEyeWorker worker)
        {
            var completed=new ManualResetEvent(false);Exception failure=null;
            var observer=new Thread(()=>{try{string ignored=worker.Diagnostic;bool ready=worker.CanStart;}catch(Exception error){failure=error;}finally{completed.Set();}}){IsBackground=true};
            observer.Start();Wait(completed,"A native callback must not hold the worker state lock");Check.Equal(null,failure);Check.True(observer.Join(1000));completed.Dispose();
        }

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE installed factory identifies only software construction path",()=>{
                var clock=new ManualClock{Now=2.5};var worker=ViveProEyeWorker.CreateInstalled(clock,()=>null);
                try{Check.True(worker.UsesInstalledSdkPath&&!worker.Started&&worker.UsesClock(clock));}
                finally{worker.Dispose();}
            });
            suite.Run("VIVE worker recording tickets bind exact poll and reject duplicates and replay",()=>{
                var waiting=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                var client=new Client{OnPoll=call=>{if(call==1)return Eye();waiting.Set();Wait(release);return Eye();}};
                var clock=new ManualClock{Now=2.5};var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(!worker.UsesInstalledSdkPath);Check.True(worker.Start());Wait(waiting);
                    var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket,ignored;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(Eye(),out ignored));
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(poll,out ticket));
                    Check.True(!ticket.InstalledPath);Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(poll,out ignored));
                    ViveProEyePoll captured;Check.True(worker.TryConsumeRecordingTicket(ticket,out captured)&&Object.ReferenceEquals(poll,captured));
                    Check.True(!worker.TryConsumeRecordingTicket(ticket,out captured));
                    var duplicate=worker.Poll();Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Duplicate,worker.TryIssueRecordingTicket(duplicate,out ignored));
                }
                finally{ReleasedStop(worker,release);}
            });
            suite.Run("VIVE worker repeated native identity never renews sample age or issues a ticket",()=>{
                var frames=new[]{Eye(2.5,20,160),Eye(2.7,20,160),Eye(2.65,21,168),Eye(2.6,20,160),
                    Eye(2.8,21,160),Eye(2.85,20,161),Eye(2.9,20,160,-1),Eye(3.0,21,168)};
                var entered=new ManualResetEvent[frames.Length+1];var release=new ManualResetEvent[frames.Length+1];
                for(int i=0;i<entered.Length;i++){entered[i]=new ManualResetEvent(false);release[i]=new ManualResetEvent(false);}
                var client=new Client{OnPoll=call=>{int i=Math.Min(call-1,frames.Length);entered[i].Set();Wait(release[i]);return frames[Math.Min(i,frames.Length-1)];}};
                var clock=new ManualClock{Now=2.5};var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Wait(entered[0]);
                    var expected=new[]{ViveProEyeWorker.RecordingTicketDisposition.Issued,ViveProEyeWorker.RecordingTicketDisposition.Duplicate,
                        ViveProEyeWorker.RecordingTicketDisposition.OutOfOrder,
                        ViveProEyeWorker.RecordingTicketDisposition.OutOfOrder,ViveProEyeWorker.RecordingTicketDisposition.OutOfOrder,
                        ViveProEyeWorker.RecordingTicketDisposition.OutOfOrder,ViveProEyeWorker.RecordingTicketDisposition.Unavailable,
                        ViveProEyeWorker.RecordingTicketDisposition.Issued};
                    for(int i=0;i<frames.Length;i++)
                    {
                        release[i].Set();Wait(entered[i+1]);clock.Now=frames[i].Eye.ObservedLocalSeconds;
                        var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket;
                        Check.Equal(expected[i],worker.TryIssueRecordingTicket(poll,out ticket));
                        if(expected[i]==ViveProEyeWorker.RecordingTicketDisposition.Issued)
                        {ViveProEyePoll consumed;Check.True(ticket!=null&&worker.TryConsumeRecordingTicket(ticket,out consumed));}
                        else Check.True(ticket==null);
                        if(i==1)
                        {
                            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                            object run=typeof(ViveProEyeWorker).GetField("current",flags).GetValue(worker);
                            Check.Near(2.5,(double)run.GetType().GetField("LastTicketObserved",flags).GetValue(run));
                            Check.Near(2.7,(double)run.GetType().GetField("LastSeenTicketObserved",flags).GetValue(run));
                        }
                    }
                }
                finally{ReleasedStop(worker,release);foreach(var item in entered)item.Dispose();foreach(var item in release)item.Dispose();}
            });
            suite.Run("VIVE worker recording ticket invalidates on newer poll or stop",()=>{
                var waiting=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                var client=new Client{OnPoll=call=>{if(call==1)return Eye();waiting.Set();Wait(release);return Eye();}};
                var clock=new ManualClock{Now=2.5};var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Wait(waiting);var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(poll,out ticket));
                    worker.Poll();ViveProEyePoll ignored;Check.True(!worker.TryConsumeRecordingTicket(ticket,out ignored));
                    Check.True(!worker.TryConsumeRecordingTicket(ticket,out ignored));worker.StopAndCaptureDiagnostics();
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(poll,out ticket));
                }
                finally{ReleasedStop(worker,release);}
            });
            suite.Run("VIVE worker recording ticket catches intervening invalid native poll",()=>{
                var second=new ManualResetEvent(false);var releaseSecond=new ManualResetEvent(false);
                var fourth=new ManualResetEvent(false);var releaseFourth=new ManualResetEvent(false);
                var client=new Client{OnPoll=call=>{
                    if(call==1)return Eye();if(call==2){second.Set();Wait(releaseSecond);return Eye(2.6,21,168,-1);}
                    if(call==3)return Eye(2.7,22,176);fourth.Set();Wait(releaseFourth);return Eye(2.7,22,176);}};
                var clock=new ManualClock{Now=2.5};var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Wait(second);var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(poll,out ticket));
                    releaseSecond.Set();Wait(fourth);ViveProEyePoll ignored;Check.True(!worker.TryConsumeRecordingTicket(ticket,out ignored));
                    clock.Now=2.7;var resumed=worker.Poll();ViveProEyeWorker.RecordingFrameTicket later;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(resumed,out later));
                    Check.True(later.LossGeneration>ticket.LossGeneration&&!later.InstalledPath);
                }
                finally{ReleasedStop(worker,releaseSecond,releaseFourth);}
            });
            suite.Run("VIVE worker refuses stale poll after unobserved loss and reports native regression",()=>{
                var second=new ManualResetEvent(false);var releaseSecond=new ManualResetEvent(false);
                var third=new ManualResetEvent(false);var releaseThird=new ManualResetEvent(false);
                var fourth=new ManualResetEvent(false);var releaseFourth=new ManualResetEvent(false);
                var client=new Client{OnPoll=call=>{
                    if(call==1)return Eye(2.5,20,160);if(call==2){second.Set();Wait(releaseSecond);return Eye(2.6,21,168,-1);}
                    if(call==3){third.Set();Wait(releaseThird);return Eye(2.7,22,176);}
                    if(call==4){fourth.Set();Wait(releaseFourth);return Eye(2.8,19,150);}
                    return Eye(2.8,19,150);}};
                var clock=new ManualClock{Now=2.5};var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Wait(second);var stale=worker.Poll();releaseSecond.Set();Wait(third);
                    ViveProEyeWorker.RecordingFrameTicket ticket;Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(stale,out ticket));
                    releaseThird.Set();Wait(fourth);clock.Now=2.7;var good=worker.Poll();
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(good,out ticket));
                    ViveProEyePoll captured;Check.True(worker.TryConsumeRecordingTicket(ticket,out captured));
                    releaseFourth.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=4,3000));
                    clock.Now=2.8;var regressed=worker.Poll();Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.OutOfOrder,worker.TryIssueRecordingTicket(regressed,out ticket));
                }
                finally{ReleasedStop(worker,releaseSecond,releaseThird,releaseFourth);}
            });
            suite.Run("VIVE worker owner ticket is spent by a foreign-thread consume attempt",()=>{
                var waiting=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                var client=new Client{OnPoll=call=>{if(call==1)return Eye();waiting.Set();Wait(release);return Eye();}};
                var clock=new ManualClock{Now=2.5};var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Wait(waiting);var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(poll,out ticket));bool accepted=true;
                    var thread=new Thread(()=>{ViveProEyePoll ignored;accepted=worker.TryConsumeRecordingTicket(ticket,out ignored);});
                    thread.Start();Check.True(thread.Join(3000));Check.True(!accepted);ViveProEyePoll output;Check.True(!worker.TryConsumeRecordingTicket(ticket,out output));
                }
                finally{ReleasedStop(worker,release);}
            });
            suite.Run("VIVE worker does not combine old head read with replacement run",()=>{
                var firstWait=new ManualResetEvent(false);var firstRelease=new ManualResetEvent(false);
                var secondWait=new ManualResetEvent(false);var secondRelease=new ManualResetEvent(false);
                var first=new Client{OnPoll=call=>{if(call==1)return Eye();firstWait.Set();Wait(firstRelease);return Eye();}};
                var second=new Client{OnPoll=call=>{if(call==1)return Eye(3,30,240);secondWait.Set();Wait(secondRelease);return Eye(3,30,240);}};
                var clock=new ManualClock{Now=2.5};ViveProEyeWorker worker=null;bool restart=false;int factories=0;
                worker=new ViveProEyeWorker(clock,()=>{
                    if(restart){restart=false;firstRelease.Set();Check.True(worker.Stop(1000));Check.True(worker.Start());Wait(secondWait);}
                    return new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity);
                },()=>Interlocked.Increment(ref factories)==1?first:second);
                try
                {
                    Check.True(worker.Start());Wait(firstWait);restart=true;var poll=worker.Poll();
                    Check.True(poll.Eye==null);ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(poll,out ticket));
                }
                finally{ReleasedStop(worker,firstRelease,secondRelease);}
            });
            suite.Run("VIVE worker recording ticket fails closed under state lock contention",()=>{
                var waiting=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                var client=new Client{OnPoll=call=>{if(call==1)return Eye();waiting.Set();Wait(release);return Eye();}};
                var clock=new ManualClock{Now=2.5};var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                var locked=new ManualResetEvent(false);var unlock=new ManualResetEvent(false);Thread blocker=null;
                try
                {
                    Check.True(worker.Start());Wait(waiting);var poll=worker.Poll();
                    var field=typeof(ViveProEyeWorker).GetField("gate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                    var gate=field.GetValue(worker);blocker=new Thread(()=>{lock(gate){locked.Set();Wait(unlock);}});blocker.Start();Wait(locked);
                    ViveProEyeWorker.RecordingFrameTicket ticket;Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Unavailable,worker.TryIssueRecordingTicket(poll,out ticket));
                }
                finally{unlock.Set();if(blocker!=null)Check.True(blocker.Join(3000));ReleasedStop(worker,release);}
            });
            suite.Run("VIVE worker requires the same clock object rather than a matching label",()=>{
                var clock=new ManualClock {Now=2.5};
                var sameLabel=new ManualClock {Now=2.5};
                Check.Equal(clock.ClockId,sameLabel.ClockId);
                var worker=new ViveProEyeWorker(clock,()=>null,()=>new Client());
                try{Check.True(worker.UsesClock(clock));Check.True(!worker.UsesClock(sameLabel));Check.True(!worker.UsesClock(null));}
                finally{worker.Dispose();}
            });
            suite.Run("VIVE worker exposes only committed eye metadata and clears it on stop",()=>{
                var nextPoll=new ManualResetEvent(false);var releasePoll=new ManualResetEvent(false);
                var client=new Client {OnPoll=call=>{if(call==1)return Eye(2.5,20,160);nextPoll.Set();Wait(releasePoll);return Eye(2.6,21,168);}};
                var worker=new ViveProEyeWorker(new ManualClock {Now=2.5},()=>null,()=>client);
                int frame,nativeTime;double observed;
                try
                {
                    Check.True(!worker.TryPeekLatestEyeMetadata(out frame,out nativeTime,out observed));
                    Check.True(worker.Start());Wait(nextPoll);
                    Check.True(worker.TryPeekLatestEyeMetadata(out frame,out nativeTime,out observed));
                    Check.Equal(20,frame);Check.Equal(160,nativeTime);Check.Near(2.5,observed);
                    worker.StopAndCaptureDiagnostics();
                    Check.True(!worker.TryPeekLatestEyeMetadata(out frame,out nativeTime,out observed));
                    releasePoll.Set();Check.True(worker.Stop(1000));
                    Check.True(!worker.TryPeekLatestEyeMetadata(out frame,out nativeTime,out observed));
                }
                finally{ReleasedStop(worker,releasePoll);}
            });
            suite.Run("VIVE worker owns native lifetime on one background thread and preserves cached observation time",()=>{
                var clock=new ManualClock {Now=2.5};var secondPoll=new ManualResetEvent(false);var releasePoll=new ManualResetEvent(false);
                var raw=Eye();var client=new Client {OnPoll=call=>{if(call==1)return raw;secondPoll.Set();Wait(releasePoll);return raw;}};
                int factoryCalls=0,factoryThread=0,headCalls=0,headThread=0;
                var worker=new ViveProEyeWorker(clock,()=>{headThread=Thread.CurrentThread.ManagedThreadId;return new ViveHeadFrame(++headCalls,clock.Now,true,true,Rotation.Identity);},
                    ()=>{Interlocked.Increment(ref factoryCalls);factoryThread=Thread.CurrentThread.ManagedThreadId;return client;});
                try
                {
                    Check.Equal(0,factoryCalls);Check.Equal(0,headCalls);Check.True(worker.CanStart&&!worker.Started);
                    var stopped=worker.Poll();Check.Equal(null,stopped.Eye);Check.Equal(ViveEyeRuntimeState.Unavailable,stopped.Runtime);Check.Equal(1,headCalls);
                    Check.True(worker.Start());Wait(secondPoll);Check.True(worker.Started&&!worker.Starting);Check.Equal(1,headCalls);
                    var one=worker.Poll();clock.Now=2.55;var two=worker.Poll();
                    Check.True(Object.ReferenceEquals(raw.Eye,one.Eye)&&Object.ReferenceEquals(one.Eye,two.Eye));
                    Check.Near(2.5,two.Eye.ObservedLocalSeconds);Check.Equal(20,two.Eye.FrameSequence);Check.Near(2.55,two.Head.ObservedLocalSeconds);
                    Check.Equal(3,headCalls);Check.True(two.Head.ObservationSequence!=999);Check.Equal(Thread.CurrentThread.ManagedThreadId,headThread);
                    Check.True(factoryThread!=headThread);Check.Equal(factoryThread,client.StartThread);Check.Equal(factoryThread,client.PollThread);
                    clock.Now=3;var source=new ViveProEyeHeadEyeSource("probe",0,0,"vive-pro-eye/operator/worker-epoch",clock,worker,new ViveProEyeRehearsalCoordinates());
                    HeadEyeSample sample;Check.True(source.TryRead(out sample));Check.True(sample.HeadValid&&!sample.EyeValid);
                    Check.Equal(ViveFrameDisposition.Stale,source.LastObservation.EyeDisposition);Check.Near(2.5,source.LastObservation.RawPoll.Eye.ObservedLocalSeconds);
                }
                finally{ReleasedStop(worker,releasePoll);}
                Check.Equal(factoryThread,client.DisposeThread);Check.Equal(1,client.DisposeCalls);
            });
            suite.Run("VIVE worker aggregate diagnostics distinguish frozen and independently changing native counters",()=>{
                var frozenPublished=new ManualResetEvent(false);var releaseChanges=new ManualResetEvent(false);
                var allPublished=new ManualResetEvent(false);var releaseFinalPoll=new ManualResetEvent(false);
                var polls=new[]{Eye(),Eye(),Eye(timestamp:161),Eye(frame:21,timestamp:161),
                    Eye(frame:999,timestamp:999,code:1036),Eye(frame:888,timestamp:888,code:null),null,
                    Eye(frame:21,timestamp:161),Eye(frame:1,timestamp:1)};
                var client=new Client {OnPoll=call=>{
                    if(call==3){frozenPublished.Set();Wait(releaseChanges);}
                    if(call>polls.Length){allPublished.Set();Wait(releaseFinalPoll);return null;}
                    return polls[call-1];}};
                var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>client);
                try
                {
                    CaptureCounts(worker.CaptureDiagnostics,0,0,0,0);
                    Check.True(worker.Start());Wait(frozenPublished);
                    var frozen=worker.CaptureDiagnostics;CaptureCounts(frozen,2,2,0,0);
                    worker.Poll();worker.Poll();CaptureCounts(worker.CaptureDiagnostics,2,2,0,0);
                    releaseChanges.Set();Wait(allPublished);
                    CaptureCounts(worker.CaptureDiagnostics,9,6,2,2);
                    CaptureCounts(frozen,2,2,0,0);
                }
                finally{ReleasedStop(worker,releaseChanges,releaseFinalPoll);}
            });
            suite.Run("VIVE worker aggregate diagnostics exclude late returns and reset after exit and on a new run",()=>{
                var latePollEntered=new ManualResetEvent(false);var releaseLatePoll=new ManualResetEvent(false);var releaseDispose=new ManualResetEvent(false);
                var releaseNewStart=new ManualResetEvent(false);var newPollPublished=new ManualResetEvent(false);var releaseNewPoll=new ManualResetEvent(false);
                var first=new Client {OnPoll=call=>{if(call==1)return Eye();latePollEntered.Set();Wait(releaseLatePoll);return Eye(frame:21);},
                    OnDispose=()=>Wait(releaseDispose)};
                var second=new Client {OnStart=()=>{Wait(releaseNewStart);return true;},
                    OnPoll=call=>{if(call==1)return Eye(frame:42);newPollPublished.Set();Wait(releaseNewPoll);return Eye(frame:43);}};
                int factories=0;var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>++factories==1?first:second);
                try
                {
                    Check.True(worker.Start());Wait(latePollEntered);var beforeStop=worker.CaptureDiagnostics;
                    CaptureCounts(beforeStop,1,1,0,0);Check.True(!worker.Stop());CaptureCounts(worker.CaptureDiagnostics,1,1,0,0);
                    releaseLatePoll.Set();Wait(first.DisposeEntered);CaptureCounts(worker.CaptureDiagnostics,1,1,0,0);
                    releaseDispose.Set();Check.True(worker.Stop(1000));CaptureCounts(worker.CaptureDiagnostics,0,0,0,0);
                    Check.True(worker.Start());Wait(second.StartEntered);CaptureCounts(worker.CaptureDiagnostics,0,0,0,0);
                    releaseNewStart.Set();Wait(newPollPublished);CaptureCounts(worker.CaptureDiagnostics,1,1,0,0);
                    CaptureCounts(beforeStop,1,1,0,0);Check.Equal(2,factories);
                }
                finally{ReleasedStop(worker,releaseLatePoll,releaseDispose,releaseNewStart,releaseNewPoll);}
            });
            suite.Run("VIVE worker stop captures publications after the last live snapshot and excludes later returns",()=>{
                var secondPollEntered=new ManualResetEvent(false);var releaseSecondPoll=new ManualResetEvent(false);
                var thirdPollEntered=new ManualResetEvent(false);var releaseThirdPoll=new ManualResetEvent(false);var releaseDispose=new ManualResetEvent(false);
                var client=new Client {OnPoll=call=>{
                    if(call==2){secondPollEntered.Set();Wait(releaseSecondPoll);}
                    if(call==3){thirdPollEntered.Set();Wait(releaseThirdPoll);}
                    return Eye(frame:20+call);},OnDispose=()=>Wait(releaseDispose)};
                var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>client);
                try
                {
                    Check.True(worker.Start());Wait(secondPollEntered);
                    var liveSnapshot=worker.CaptureDiagnostics;CaptureCounts(liveSnapshot,1,1,0,0);
                    // Reproduce a publication between the preview's last snapshot and Stop.
                    releaseSecondPoll.Set();Wait(thirdPollEntered);
                    var finalSnapshot=worker.StopAndCaptureDiagnostics();
                    CaptureCounts(finalSnapshot,2,2,1,1);
                    Check.True(worker.Stopping&&!worker.CanStart);Check.Equal(null,worker.Poll().Eye);
                    releaseThirdPoll.Set();Wait(client.DisposeEntered);
                    CaptureCounts(worker.CaptureDiagnostics,2,2,1,1);CaptureCounts(finalSnapshot,2,2,1,1);
                    releaseDispose.Set();Check.True(worker.Stop(1000));CaptureCounts(worker.CaptureDiagnostics,0,0,0,0);
                    CaptureCounts(worker.StopAndCaptureDiagnostics(),2,2,1,1);
                    CaptureCounts(liveSnapshot,1,1,0,0);CaptureCounts(finalSnapshot,2,2,1,1);
                }
                finally{ReleasedStop(worker,releaseSecondPoll,releaseThirdPoll,releaseDispose);}
            });
            suite.Run("VIVE worker stop during blocked initialization returns immediately and prevents restart",()=>{
                var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                var client=new Client {OnStart=()=>{entered.Set();Wait(release);return true;}};int factories=0;
                var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>{Interlocked.Increment(ref factories);return client;});
                try
                {
                    Check.True(worker.Start());Wait(entered);Check.True(worker.Starting&&!worker.Started);Check.True(!worker.Start());
                    Check.True(!worker.Stop());Check.True(worker.Stopping&&!worker.Starting&&!worker.Started&&!worker.CanStart);
                    Check.True(!worker.Start());Check.Equal(0,client.DisposeCalls);Check.Equal(null,worker.Poll().Eye);
                    release.Set();Check.True(worker.Stop(1000));Check.Equal(1,factories);Check.Equal(0,client.PollCalls);Check.Equal(1,client.DisposeCalls);Check.True(worker.CanStart);
                }
                finally{ReleasedStop(worker,release);}
            });
            suite.Run("VIVE worker late native return cannot publish after stop and cleanup completion gates restart",()=>{
                var releasePoll=new ManualResetEvent(false);var releaseDispose=new ManualResetEvent(false);
                var client=new Client {OnPoll=call=>{Wait(releasePoll);return Eye();},OnDispose=()=>Wait(releaseDispose)};
                var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>client);
                try
                {
                    Check.True(worker.Start());Wait(client.PollEntered);Check.True(!worker.Stop());Check.True(worker.Stopping&&!worker.CanStart);
                    Check.Equal(0,client.DisposeCalls);Check.Equal(null,worker.Poll().Eye);releasePoll.Set();Wait(client.DisposeEntered);
                    Check.Equal(null,worker.Poll().Eye);Check.True(!worker.Stop());Check.True(!worker.Start());Check.True(!worker.CanStart);
                    releaseDispose.Set();Check.True(worker.Stop(1000));Check.True(worker.CanStart);Check.Equal(null,worker.Poll().Eye);
                }
                finally{ReleasedStop(worker,releasePoll,releaseDispose);}
            });
            suite.Run("VIVE worker Dispose is nonblocking during a native call and permanently prevents restart",()=>{
                var release=new ManualResetEvent(false);var client=new Client {OnPoll=call=>{Wait(release);return Eye();}};
                var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>client);
                try
                {
                    Check.True(worker.Start());Wait(client.PollEntered);worker.Dispose();worker.Dispose();
                    Check.True(worker.Stopping&&!worker.CanStart&&!worker.Started);Check.True(!worker.Start());Check.Equal(0,client.DisposeCalls);
                    Check.Equal(null,worker.Poll().Eye);release.Set();Check.True(worker.Stop(1000));Check.Equal(1,client.DisposeCalls);
                    Check.True(!worker.CanStart&&!worker.Start());
                }
                finally{ReleasedStop(worker,release);}
            });
            suite.Run("VIVE worker native exception clears previous data before blocked cleanup and sanitizes details",()=>{
                var secondPoll=new ManualResetEvent(false);var throwNow=new ManualResetEvent(false);var releaseDispose=new ManualResetEvent(false);
                var client=new Client {OnPoll=call=>{if(call==1)return Eye();secondPoll.Set();Wait(throwNow);throw new InvalidOperationException("private-device-value");},OnDispose=()=>Wait(releaseDispose)};
                var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>client);
                try
                {
                    Check.True(worker.Start());Wait(secondPoll);Check.True(worker.Poll().Eye!=null);throwNow.Set();Wait(client.DisposeEntered);
                    Check.Equal(null,worker.Poll().Eye);Check.True(!worker.Started&&!worker.CanStart);Check.True(!worker.Diagnostic.Contains("private-device-value"));
                    Check.True(worker.Diagnostic.Contains("InvalidOperationException"));releaseDispose.Set();Check.True(worker.Stop(1000));Check.True(worker.CanStart);
                }
                finally{ReleasedStop(worker,throwNow,releaseDispose);}
            });
            suite.Run("VIVE worker factory and initialization failures are contained and dispose created clients",()=>{
                foreach(int mode in new[]{0,1,2})
                {
                    var completed=new ManualResetEvent(false);var client=new Client {OnStart=()=>false,OnDispose=()=>completed.Set()};
                    var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>{if(mode==0){completed.Set();throw new InvalidOperationException("private-factory-value");}if(mode==1){completed.Set();return null;}return client;});
                    try
                    {
                        Check.True(worker.Start());Wait(completed);Check.True(worker.Stop(1000));Check.True(!worker.Started&&worker.CanStart);
                        Check.Equal(null,worker.Poll().Eye);Check.True(!worker.Diagnostic.Contains("private-factory-value"));Check.Equal(mode==2?1:0,client.DisposeCalls);
                    }
                    finally{ReleasedStop(worker);}
                }
            });
            suite.Run("VIVE worker disposal failure blocks restart but unavailable post-disposal diagnostics do not",()=>{
                foreach(bool cleanupThrows in new[]{false,true})
                {
                    bool disposed=false;var client=new Client {OnStart=()=>false,OnDispose=()=>{disposed=true;if(cleanupThrows)throw new InvalidOperationException("private-cleanup-value");},
                        OnDiagnostic=()=>{if(disposed)throw new ObjectDisposedException("client");return "initialization unavailable";}};
                    var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>client);
                    try
                    {
                        Check.True(worker.Start());Wait(client.Disposed);Check.True(worker.Stop(1000));Check.True(worker.HasExited);Check.Equal(!cleanupThrows,worker.CanStart);
                        Check.True(!worker.Diagnostic.Contains("private-cleanup-value"));Check.Equal(1,client.DisposeCalls);
                        Check.True(worker.Diagnostic.Contains(cleanupThrows?"cleanup failed":"cleanup completed"));
                    }
                    finally{ReleasedStop(worker);}
                }
            });
            suite.Run("VIVE worker invokes factory client methods and diagnostics without holding its state lock",()=>{
                var secondPoll=new ManualResetEvent(false);var release=new ManualResetEvent(false);ViveProEyeWorker worker=null;
                var client=new Client {OnStart=()=>{ReadableFromAnotherThread(worker);return true;},
                    OnPoll=call=>{ReadableFromAnotherThread(worker);if(call>1){secondPoll.Set();Wait(release);}return Eye();},
                    OnDiagnostic=()=>{ReadableFromAnotherThread(worker);return "safe diagnostic";},OnDispose=()=>ReadableFromAnotherThread(worker)};
                worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>{ReadableFromAnotherThread(worker);return client;});
                try{Check.True(worker.Start());Wait(secondPoll);Check.True(worker.Started);}
                finally{ReleasedStop(worker,release);}
                Check.Equal(1,client.DisposeCalls);Check.True(!worker.Diagnostic.Contains("failed"));
            });
            suite.Run("VIVE worker preserves contained cleanup quarantine and blocks restart after thread exit",()=>{
                foreach(bool statusThrows in new[]{false,true})
                {
                    var client=new CleanupStatusClient {StatusThrows=statusThrows};
                    var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>client);
                    try
                    {
                        Check.True(worker.Start());Wait(client.Inner.Disposed);Check.True(worker.Stop(1000));
                        Check.True(worker.HasExited&&!worker.CanStart&&!worker.Start());Check.Equal(null,worker.Poll().Eye);
                        Check.True(worker.Diagnostic.Contains("resources are quarantined"));Check.True(!worker.Diagnostic.Contains("private-cleanup-status"));
                        if(statusThrows)Check.True(worker.Diagnostic.Contains("cleanup status unavailable"));
                    }
                    finally{ReleasedStop(worker);}
                }
            });
            suite.Run("VIVE worker head callback stays on its creating thread and failures cannot fabricate a head pose",()=>{
                var secondPoll=new ManualResetEvent(false);var release=new ManualResetEvent(false);bool headThrows=false;int heads=0;
                var client=new Client {OnPoll=call=>{if(call>1){secondPoll.Set();Wait(release);}return Eye();}};
                var worker=new ViveProEyeWorker(new ManualClock(),()=>{Interlocked.Increment(ref heads);if(headThrows)throw new InvalidOperationException("private-head-value");return new ViveHeadFrame(heads,0,true,true,Rotation.Identity);},()=>client);
                try
                {
                    Check.True(worker.Start());Wait(secondPoll);Check.Equal(0,heads);var foreignDone=new ManualResetEvent(false);bool refused=false;
                    var foreign=new Thread(()=>{try{worker.Poll();}catch(InvalidOperationException){refused=true;}finally{foreignDone.Set();}});
                    foreign.Start();Wait(foreignDone);Check.True(foreign.Join(1000));Check.True(refused);Check.Equal(0,heads);
                    headThrows=true;var poll=worker.Poll();Check.Equal(null,poll.Head);Check.True(poll.Eye!=null);Check.Equal(1,heads);
                    Check.True(worker.Diagnostic.Contains("Head observation failed")&&!worker.Diagnostic.Contains("private-head-value"));
                }
                finally{ReleasedStop(worker,release);}
            });
            suite.Run("VIVE worker null poll clears cached eye data and bounded stop validates its wait",()=>{
                var thirdPoll=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                var client=new Client {OnPoll=call=>{if(call==1)return Eye();if(call==2)return null;thirdPoll.Set();Wait(release);return null;}};
                var worker=new ViveProEyeWorker(new ManualClock(),()=>null,()=>client);
                try
                {
                    Check.True(worker.Start());Wait(thirdPoll);Check.True(worker.Started);Check.Equal(null,worker.Poll().Eye);
                    Check.Throws<ArgumentOutOfRangeException>(()=>worker.Stop(-1));Check.Throws<ArgumentOutOfRangeException>(()=>worker.Stop(1001));
                    Check.True(worker.Started);
                }
                finally{ReleasedStop(worker,release);}
            });
        }
    }
}
