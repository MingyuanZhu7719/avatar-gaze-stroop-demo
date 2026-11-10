using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Owns one cooperative provider lease and one fixed mapping for an epoch.
    // Source recreation is legitimate; provider/run replacement is not. This
    // component emits candidates, never transport or experiment admission.
    internal sealed class ViveLegacyWozObservationOwner : IDisposable
    {
        readonly Thread thread=Thread.CurrentThread;
        readonly MonotonicClock clock;
        readonly ViveProEyeWorker worker;
        readonly object run;
        readonly ViveExclusiveObservationProvider.Lease lease;
        readonly object claim=new object();
        readonly ViveWozOperatorSourceFactory factory;
        readonly IViveProEyeProvider claimedProvider;
        readonly string session;
        ViveProEyeHeadEyeSource preflightSource;
        int preflightBlock=-1,preflightSegment=-1;
        readonly double maxAge;
        long emission,headSequence=-1;
        int eyeSequence,eyeTimestamp;
        double headTime=-1,eyeTime=-1;
        bool seen,terminal,cleanupUncertain;
        internal bool Terminal {get{return terminal;}}
        internal bool BindingCurrent {get{try{return Current();}catch{terminal=true;cleanupUncertain=true;LastIssue="source_close_uncertain";return false;}}}
        internal string StreamId {get{return factory.StreamId;}}
        internal string Mapping {get{return ViveProEyeRehearsalCoordinates.MappingVersion;}}
        internal string DeviceId {get{return factory.DeviceId;}}
        internal bool UsesClock(MonotonicClock candidate){return Object.ReferenceEquals(clock,candidate);}
        internal string LastIssue {get;private set;}
        internal sealed class Candidate
        {
            internal readonly HeadEyeSample Sample;
            internal readonly long SourceSequence,HeadSequence;
            internal readonly int NativeEyeSequence,NativeEyeTimestamp;
            internal readonly double HeadObserved,EyeObserved;
            internal Candidate(HeadEyeSample sample,ViveProEyeObservation observation)
            {Sample=sample;SourceSequence=observation.Sample.Sequence;HeadSequence=observation.RawPoll.Head.ObservationSequence;
                NativeEyeSequence=observation.RawPoll.Eye.FrameSequence;NativeEyeTimestamp=observation.RawPoll.Eye.Timestamp;
                HeadObserved=observation.RawPoll.Head.ObservedLocalSeconds;EyeObserved=observation.RawPoll.Eye.ObservedLocalSeconds;}
        }
        ViveLegacyWozObservationOwner(string session,string epoch,MonotonicClock clock,IViveProEyeProvider provider,
            ViveProEyeWorker worker,object run,double maximumAge)
        {
            this.clock=clock;this.worker=worker;this.run=run;maxAge=maximumAge;this.session=session;
            lease=new ViveExclusiveObservationProvider(provider,clock).Acquire();
            try{claimedProvider=lease.ClaimForPump(claim);factory=new ViveWozOperatorSourceFactory(session,epoch,clock,claimedProvider,new ViveProEyeRehearsalCoordinates(),maximumAge,true);}
            catch{lease.ReleaseClaim(claim);lease.Dispose();throw;}
        }
        // No Start, client factory or caller-supplied provenance flag. This
        // binds only an already-running fixed installed worker and exact clock.
        // The current memory bridge never calls this factory.
        internal static ViveLegacyWozObservationOwner BindInstalled(string session,string epoch,MonotonicClock clock,ViveProEyeWorker worker,double maximumAge)
        {
            object run;
            if(clock==null||worker==null||!worker.UsesClock(clock)||!worker.UsesInstalledSdkPath||!worker.TryGetRecordingRunIdentity(out run))
                throw new InvalidOperationException("Exact running installed worker and sealed shared clock required.");
            return new ViveLegacyWozObservationOwner(session,epoch,clock,worker,worker,run,maximumAge);
        }
        bool Current()
        {
            if(terminal||!Object.ReferenceEquals(thread,Thread.CurrentThread))return false;
            object current;
            if(!lease.IsClaimedBy(claim)||(worker!=null&&(!worker.UsesClock(clock)||!worker.TryGetRecordingRunIdentity(out current)||!Object.ReferenceEquals(run,current))))
            {Fail("provider_or_worker_run_lost");return false;}
            return true;
        }
        internal bool TryRead(WozLanTaskContext task,double taskReceived,out Candidate candidate)
        {return TryReadOwned(task.Task,taskReceived,false,out candidate);}
        internal bool TryReadPreflight(TaskSnapshot task,double challengeReceived,out Candidate candidate)
        {return TryReadOwned(task,challengeReceived,true,out candidate);}
        bool TryReadOwned(TaskSnapshot task,double taskReceived,bool preflight,out Candidate candidate)
        {
            candidate=null;if(!Current())return false;
            try
            {
                if(emission==Int64.MaxValue){Fail("emission_exhausted");return false;}
                ViveProEyeHeadEyeSource source;
                if(preflight)
                {
                    if(task==null||task.SessionId!=session||task.BlockIndex<0||task.Segment<0||!task.Condition.HasValue||task.Condition==Condition.D||
                        (task.Phase!=Phase.BlockReady&&task.Phase!=Phase.Countdown&&task.Phase!=Phase.MainTask))return false;
                    if(task.BlockIndex<preflightBlock||(task.BlockIndex==preflightBlock&&task.Segment<preflightSegment))
                    {Fail("preflight_context_regressed");return false;}
                    if(preflightSource==null||task.BlockIndex!=preflightBlock||task.Segment!=preflightSegment)
                    {
                        preflightSource=new ViveProEyeHeadEyeSource(session,task.BlockIndex,task.Segment,StreamId,clock,claimedProvider,new ViveProEyeRehearsalCoordinates(),maxAge);
                        preflightBlock=task.BlockIndex;preflightSegment=task.Segment;
                    }
                    source=preflightSource;
                }
                else source=factory.Create(task);
                HeadEyeSample original;
                if(!source.TryRead(out original)||original==null||!original.HeadValid||!original.EyeValid)return false;
                var observation=source.LastObservation;var head=observation.RawPoll.Head;var eye=observation.RawPoll.Eye;
                double now=clock.Now;
                if(!Current())return false;
                if(head.ObservedLocalSeconds<=taskReceived||eye.ObservedLocalSeconds<=taskReceived||
                    now-head.ObservedLocalSeconds>maxAge||now-eye.ObservedLocalSeconds>maxAge)return false;
                if(seen&&(head.ObservationSequence<=headSequence||eye.FrameSequence<=eyeSequence||eye.Timestamp<=eyeTimestamp||
                    head.ObservedLocalSeconds<=headTime||eye.ObservedLocalSeconds<=eyeTime))return false;
                // Source.Sequence is local to its block/segment. This counter is
                // the bridge emission ordinal, NEVER a sensor frame counter.
                var sample=new HeadEyeSample(original.SessionId,original.StreamId,original.DeviceId,original.ClockId,
                    original.BlockIndex,original.Segment,emission++,original.CaptureLocalSeconds,now,original.Source,original.Role,
                    original.RawHeadRotation,original.RawEyeDirection,true,true);
                headSequence=head.ObservationSequence;eyeSequence=eye.FrameSequence;eyeTimestamp=eye.Timestamp;
                headTime=head.ObservedLocalSeconds;eyeTime=eye.ObservedLocalSeconds;seen=true;
                candidate=new Candidate(sample,observation);return true;
            }
            catch(Exception){Fail("source_poll_or_context_failed");return false;}
        }
        internal void Fail(string reason){LastIssue=reason;try{Dispose();}catch{LastIssue="source_close_uncertain";}}
        public void Dispose()
        {
            terminal=true;
            if(cleanupUncertain)throw new InvalidOperationException("Source cleanup outcome remains uncertain.");
            try{lease.CloseClaim(claim);}catch{cleanupUncertain=true;LastIssue="source_close_uncertain";throw;}
        }
#if STROOP_TEST_BUILD
        // A sealed fixture provider cannot be supplied to the installed factory.
        internal sealed class SyntheticObservations : IViveProEyeProvider
        {
            internal readonly MonotonicClock clock;
            internal ViveProEyePoll Next;
            internal Action DuringPoll;
            internal SyntheticObservations(MonotonicClock clock){this.clock=clock;}
            internal void Fresh(long head,int eye,int timestamp)
            {
                // Wait for genuinely later sealed-clock observations; never
                // add fabricated time offsets or relax strict post-task checks.
                Thread.Sleep(1);
                double h=clock.Now,e=clock.Now;
                var ray=new ViveEyeRay(2,true,true,new Vec3(0,0,0),new Vec3(0,0,1));
                Next=new ViveProEyePoll(new ViveHeadFrame(head,h,true,true,Rotation.Identity),new ViveEyeFrame(eye,timestamp,e,false,ray,ray,ray),
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true);
            }
            public ViveProEyePoll Poll(){var action=DuringPoll;DuringPoll=null;if(action!=null)action();return Next;}
        }
        internal static ViveLegacyWozObservationOwner BindSynthetic(string session,string epoch,MonotonicClock clock,SyntheticObservations provider,double maximumAge)
        {
            if(!Object.ReferenceEquals(clock,provider.clock))throw new InvalidOperationException("Synthetic source clock differs.");
            return new ViveLegacyWozObservationOwner(session,epoch,clock,provider,null,null,maximumAge);
        }
        internal void ExhaustForTest(){emission=Int64.MaxValue;}
#endif
    }
}
