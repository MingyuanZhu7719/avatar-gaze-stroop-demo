using System;
using System.Threading;
using System.Runtime.CompilerServices;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Cooperative exclusive access: integration must route *all* consumers of
    // the same worker through this owner-thread broker. It cannot prevent code
    // that still holds the underlying provider from bypassing the broker.
    public sealed class ViveExclusiveObservationProvider
    {
        sealed class Registry {internal Lease Active;internal IClock Clock;internal bool Polling;}
        static readonly ConditionalWeakTable<IViveProEyeProvider,Registry> Registries=new ConditionalWeakTable<IViveProEyeProvider,Registry>();
        readonly Registry registry;
        readonly IViveProEyeProvider provider;
        readonly IClock clock;
        readonly int ownerThread;

        public ViveExclusiveObservationProvider(IViveProEyeProvider provider,IClock clock)
        {
            if(provider==null||clock==null)throw new ArgumentNullException("provider/clock");
            var worker=provider as ViveProEyeWorker;
            if(worker!=null&&!worker.UsesClock(clock))throw new ArgumentException("Worker and broker must share the actual clock instance.");
            this.provider=provider;this.clock=clock;registry=Registries.GetValue(provider,_=>new Registry{Clock=clock});
            if(!object.ReferenceEquals(registry.Clock,clock))throw new ArgumentException("All consumers of one provider must share the actual clock instance.");
            ownerThread=Thread.CurrentThread.ManagedThreadId;
        }
        void Owner(){if(Thread.CurrentThread.ManagedThreadId!=ownerThread)throw new InvalidOperationException("Observation broker belongs to its creating thread.");}
        public Lease Acquire()
        {
            Owner();lock(registry)
            {
                if(registry.Active!=null)throw new InvalidOperationException("Another consumer already owns the worker.");
                registry.Active=new Lease(this);return registry.Active;
            }
        }
        public sealed class Lease : IViveProEyeProvider,IDisposable
        {
            readonly ViveExclusiveObservationProvider broker;
            bool closed;object claim;
            internal Lease(ViveExclusiveObservationProvider broker){this.broker=broker;}
            public bool UsesClock(IClock clock){return object.ReferenceEquals(broker.clock,clock);}
            public ViveProEyePoll Poll()
            {
                broker.Owner();lock(broker.registry)
                {
                    if(closed||!object.ReferenceEquals(broker.registry.Active,this))throw new ObjectDisposedException("Lease");
                    if(claim!=null)throw new InvalidOperationException("Lease is claimed by another consumer.");
                    if(broker.registry.Polling)throw new InvalidOperationException("Worker poll is already in progress.");
                    broker.registry.Polling=true;
                }
                try{return broker.provider.Poll();}finally{lock(broker.registry){broker.registry.Polling=false;}}
            }
            sealed class ClaimedProvider : IViveProEyeProvider
            {
                readonly Lease lease;readonly object token;
                public ClaimedProvider(Lease lease,object token){this.lease=lease;this.token=token;}
                public ViveProEyePoll Poll(){return lease.PollClaimed(token);}
            }
            internal IViveProEyeProvider ClaimForPump(object token)
            {
                broker.Owner();lock(broker.registry)
                {
                    if(closed||!object.ReferenceEquals(broker.registry.Active,this))throw new ObjectDisposedException("Lease");
                    if(token==null||claim!=null||broker.registry.Polling)throw new InvalidOperationException("Lease is claimed or a poll is in progress.");
                    claim=token;return new ClaimedProvider(this,token);
                }
            }
            ViveProEyePoll PollClaimed(object token)
            {
                broker.Owner();lock(broker.registry)
                {
                    if(closed||!object.ReferenceEquals(broker.registry.Active,this)||!object.ReferenceEquals(claim,token))
                        throw new ObjectDisposedException("Lease");
                    if(broker.registry.Polling)throw new InvalidOperationException("Worker poll is already in progress.");
                    broker.registry.Polling=true;
                }
                try{return broker.provider.Poll();}finally{lock(broker.registry){broker.registry.Polling=false;}}
            }
            internal void ReleaseClaim(object token)
            {broker.Owner();lock(broker.registry){if(object.ReferenceEquals(claim,token))claim=null;}}
            internal bool IsClaimedBy(object token)
            {broker.Owner();lock(broker.registry){return !closed&&object.ReferenceEquals(broker.registry.Active,this)&&object.ReferenceEquals(claim,token);}}
            internal void CloseClaim(object token)
            {
                broker.Owner();lock(broker.registry)
                {
                    if(closed)return;
                    if(!object.ReferenceEquals(claim,token)||broker.registry.Polling)throw new InvalidOperationException("Only the active consumer can release an idle lease.");
                    closed=true;claim=null;if(object.ReferenceEquals(broker.registry.Active,this))broker.registry.Active=null;
                }
            }
            public void Dispose()
            {
                broker.Owner();lock(broker.registry)
                {
                    if(closed)return;
                    if(claim!=null||broker.registry.Polling)throw new InvalidOperationException("The active consumer must release its lease.");
                    closed=true;
                    if(object.ReferenceEquals(broker.registry.Active,this))broker.registry.Active=null;
                }
            }
        }
    }

    // In-memory *candidate* observation, not a wire proof or readiness decision.
    // Task and challenge authority remain the integrating caller's responsibility;
    // v2 does not establish a remote source lock. Never use this to open a gate.
    public sealed class VivePreflightSampleCandidate
    {
        public ViveProEyeObservation Observation {get;private set;}
        public string Challenge {get;private set;}
        public Phase Phase {get;private set;}
        public double ChallengeReceivedLocalSeconds {get;private set;}
        public long TaskPacketSequence {get;private set;}
        public double TaskReceivedLocalSeconds {get;private set;}
        internal VivePreflightSampleCandidate(ViveProEyeObservation observation,ViveDevelopmentPreflightChallenge challenge,double received,VivePreflightOperatorTask task)
        {Observation=observation;Challenge=challenge.Challenge;Phase=challenge.Phase;ChallengeReceivedLocalSeconds=received;TaskPacketSequence=task.PacketSequence;TaskReceivedLocalSeconds=task.LocalReceivedSeconds;}
    }

    // One challenge and one context per pump. Caller must stop/release before a
    // different phase or main-task consumer acquires the cooperative lease. This
    // class never constructs/sends a proof, opens a socket, starts a device,
    // records data, asserts peer authenticity, or advances the experiment.
    public sealed class ViveWozPreflightSamplePump : IDisposable
    {
        readonly ViveExclusiveObservationProvider.Lease lease;
        readonly object leaseToken=new object();
        readonly IClock clock;
        readonly string clockId;
        readonly ViveDevelopmentPreflightChallenge challenge;
        readonly TaskSnapshot context;
        readonly ViveProEyeHeadEyeSource source;
        readonly double received,maxAge;
        readonly int ownerThread;
        long lastTaskSequence;
        double lastTaskReceipt,lastNow,lastGoodObservation=-1;
        TaskSnapshot lastTask;
        bool faulted,closed,sampling;
        public bool RecoveryDecisionRequired {get{return faulted;}}
        public bool CanEnterCountdown {get{return false;}}
        public bool CanResume {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public string LastIssue {get;private set;}

        public ViveWozPreflightSamplePump(ViveExclusiveObservationProvider.Lease lease,IClock clock,
            IViveProEyeCoordinateMapper mapper,ViveDevelopmentPreflightChallenge challenge,double challengeReceivedLocalSeconds,
            VivePreflightOperatorTask task,double maximumAgeSeconds,bool explicitDevelopmentOnly)
        {
            if(!explicitDevelopmentOnly)throw new InvalidOperationException("Explicit development-only scope required.");
            if(lease==null||clock==null||mapper==null||challenge==null||task==null)throw new ArgumentNullException("Explicit lease, clock, mapper, challenge and task required.");
            if(!lease.UsesClock(clock)||challenge.OperatorClockId!=clock.ClockId||challenge.MappingVersion!=mapper.Version||
                !Finite(maximumAgeSeconds)||maximumAgeSeconds<=0)throw new ArgumentException("Shared clock, mapper and positive freshness limit required.");
            double now=clock.Now;
            if(!Finite(now)||now<0||!Finite(challengeReceivedLocalSeconds)||challengeReceivedLocalSeconds<0||challengeReceivedLocalSeconds>now||
                now-challengeReceivedLocalSeconds>maximumAgeSeconds||!ValidTask(task,challenge,now,maximumAgeSeconds))
                throw new ArgumentException("Fresh matching challenge and task context required.");
            this.lease=lease;this.clock=clock;clockId=clock.ClockId;this.challenge=challenge;received=challengeReceivedLocalSeconds;maxAge=maximumAgeSeconds;
            context=task.Task;lastTask=context;lastTaskSequence=task.PacketSequence;lastTaskReceipt=task.LocalReceivedSeconds;lastNow=now;
            ownerThread=Thread.CurrentThread.ManagedThreadId;
            var claimed=lease.ClaimForPump(leaseToken);
            try{source=new ViveProEyeHeadEyeSource(challenge.SessionId,challenge.Block,challenge.Segment,challenge.OperatorStreamId,clock,claimed,mapper,maximumAgeSeconds);}
            catch{lease.ReleaseClaim(leaseToken);throw;}
        }
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        static bool ValidTask(VivePreflightOperatorTask status,ViveDevelopmentPreflightChallenge challenge,double now,double maxAge)
        {
            var task=status==null?null:status.Task;
            return status!=null&&status.PeerTaskAcceptedAndFresh&&status.LiveSourceStillSelectedAndConfirmed&&task!=null&&task.Condition.HasValue&&
                task.SessionId==challenge.SessionId&&task.BlockIndex==challenge.Block&&task.Segment==challenge.Segment&&task.Phase==challenge.Phase&&
                status.PacketSequence>=0&&Finite(status.LocalReceivedSeconds)&&status.LocalReceivedSeconds>=0&&status.LocalReceivedSeconds<=now&&now-status.LocalReceivedSeconds<=maxAge;
        }
        static bool SameTask(TaskSnapshot a,TaskSnapshot b)
        {return a!=null&&b!=null&&a.SessionId==b.SessionId&&a.BlockIndex==b.BlockIndex&&a.Segment==b.Segment&&a.Phase==b.Phase&&
            a.Condition==b.Condition&&a.TrialId==b.TrialId&&a.Word==b.Word&&a.Ink==b.Ink;}
        void Owner(){if(Thread.CurrentThread.ManagedThreadId!=ownerThread)throw new InvalidOperationException("Preflight pump belongs to its creating thread.");}
        void Fault(string issue){faulted=true;LastIssue=issue;}
        static bool ExplicitInvalid(ViveProEyePoll poll)
        {
            if(poll==null||poll.Head==null||poll.Eye==null||poll.Runtime!=ViveEyeRuntimeState.Working||
                poll.Calibration!=ViveEyeCalibrationState.ReportedReady||!poll.EyeDeviceConnected||
                (poll.NativeResultCode.HasValue&&poll.NativeResultCode.Value!=0)||!poll.Head.DeviceConnected||
                !poll.Head.RotationReportedValid||!poll.Head.RawTrackingRotation.IsUnit||poll.Eye.NoUser||
                poll.Eye.Combined==null||(poll.Eye.Combined.ValidityMask&2UL)==0||!poll.Eye.Combined.DirectionReportedValid)return true;
            Vec3 normalized;return !ViveProEyeSdkDirection.TryGetUnitDirection(poll.Eye.Combined.Direction,out normalized);
        }
        static bool BadDisposition(ViveFrameDisposition value)
        {return value!=ViveFrameDisposition.Accepted&&value!=ViveFrameDisposition.Duplicate;}
        // The callback acknowledges only local proof construction, never socket
        // delivery or remote acceptance. Failure cannot refresh the deadline.
        public bool TrySample(VivePreflightOperatorTask task,bool sourceReady,Func<ViveProEyeObservation,bool> constructLocalProof,out VivePreflightSampleCandidate candidate)
        {
            Owner();candidate=null;if(closed||faulted)return false;
            if(constructLocalProof==null)throw new ArgumentNullException("constructLocalProof");
            if(sampling){Fault("reentrant_observation");return false;}
            sampling=true;try{return SampleCore(task,sourceReady,constructLocalProof,out candidate);}finally{sampling=false;}
        }
        bool SampleCore(VivePreflightOperatorTask task,bool sourceReady,Func<ViveProEyeObservation,bool> constructLocalProof,out VivePreflightSampleCandidate candidate)
        {
            candidate=null;double now;string actualClock;
            try{now=clock.Now;actualClock=clock.ClockId;}catch(Exception){Fault("clock_unavailable");return false;}
            if(actualClock!=clockId||!Finite(now)||now<0||now<lastNow){Fault("clock_fault");return false;}
            lastNow=now;
            // Check the existing deadline *before* consuming a later observation.
            double freshnessBase=lastGoodObservation<0?received:lastGoodObservation;
            if(now-freshnessBase>maxAge){Fault("observation_gap");return false;}
            if(now-lastTaskReceipt>maxAge){Fault("authoritative_task_gap");return false;}
            if(!sourceReady){Fault("source_unavailable");return false;}
            if(!ValidTask(task,challenge,now,maxAge)||!SameContext(task.Task,context)){Fault("task_or_source_context_unavailable");return false;}
            if(task.PacketSequence<lastTaskSequence||task.LocalReceivedSeconds<lastTaskReceipt||
                (task.PacketSequence==lastTaskSequence&&(task.LocalReceivedSeconds!=lastTaskReceipt||!SameTask(task.Task,lastTask))))
            {Fault("task_receipt_regressed_or_changed");return false;}
            if(task.PacketSequence>lastTaskSequence){lastTaskSequence=task.PacketSequence;lastTaskReceipt=task.LocalReceivedSeconds;lastTask=task.Task;}
            HeadEyeSample ignored;
            try{source.TryRead(out ignored);}catch(Exception){Fault("observation_provider_fault");return false;}
            var observation=source.LastObservation;
            if(!lease.IsClaimedBy(leaseToken)){Fault("worker_lease_lost");return false;}
            double afterPoll;string afterPollClock;
            try{afterPoll=clock.Now;afterPollClock=clock.ClockId;}catch(Exception){Fault("clock_unavailable");return false;}
            if(afterPollClock!=clockId||!Finite(afterPoll)||afterPoll<now||
                (observation!=null&&afterPoll<observation.PollCompletedLocalSeconds)||
                afterPoll-freshnessBase>maxAge||afterPoll-lastTaskReceipt>maxAge)
            {Fault("clock_or_observation_gap_after_poll");return false;}
            lastNow=afterPoll;
            if(observation==null||ExplicitInvalid(observation.RawPoll)){Fault("explicit_head_or_eye_invalid");return false;}
            if(BadDisposition(observation.HeadDisposition)||BadDisposition(observation.EyeDisposition))
            {Fault("stale_out_of_order_or_invalid_frame");return false;}
            if(observation.HeadDisposition==ViveFrameDisposition.Duplicate||observation.EyeDisposition==ViveFrameDisposition.Duplicate)
            {LastIssue="waiting_for_new_head_and_eye";return false;}
            if(!observation.HeadUsable||!observation.EyeUsable||observation.Sample==null)
            {Fault("mapping_or_observation_invalid");return false;}
            var head=observation.RawPoll.Head;var eye=observation.RawPoll.Eye;
            if(head.ObservedLocalSeconds<=received||eye.ObservedLocalSeconds<=received)
            {LastIssue="waiting_for_post_challenge_observation";return false;}
            double beforeProof;string beforeProofClock;
            try{beforeProof=clock.Now;beforeProofClock=clock.ClockId;}catch(Exception){Fault("clock_unavailable");return false;}
            double observed=Math.Min(head.ObservedLocalSeconds,eye.ObservedLocalSeconds);
            if(beforeProofClock!=clockId||!Finite(beforeProof)||beforeProof<afterPoll||beforeProof<observation.PollCompletedLocalSeconds||
                beforeProof<head.ObservedLocalSeconds||beforeProof<eye.ObservedLocalSeconds||beforeProof-observed>maxAge||
                beforeProof-freshnessBase>maxAge||beforeProof-lastTaskReceipt>maxAge)
            {Fault("clock_or_observation_gap_before_proof");return false;}
            lastNow=beforeProof;
            bool constructed;
            try{constructed=constructLocalProof(observation);}catch(Exception){Fault("local_proof_construction_fault");return false;}
            if(closed||faulted)return false;
            if(!lease.IsClaimedBy(leaseToken)){Fault("worker_lease_lost");return false;}
            double completed;string completedClock;
            try{completed=clock.Now;completedClock=clock.ClockId;}catch(Exception){Fault("clock_unavailable");return false;}
            if(completedClock!=clockId||!Finite(completed)||completed<beforeProof||completed-observed>maxAge||completed-freshnessBase>maxAge||completed-lastTaskReceipt>maxAge)
            {Fault("clock_or_observation_gap_during_proof");return false;}
            if(!constructed){lastNow=completed;LastIssue="local_proof_not_constructed";return false;}
            lastNow=completed;lastGoodObservation=observed;
            candidate=new VivePreflightSampleCandidate(observation,challenge,received,task);LastIssue=null;return true;
        }
        static bool SameContext(TaskSnapshot a,TaskSnapshot b)
        {return a!=null&&b!=null&&a.SessionId==b.SessionId&&a.BlockIndex==b.BlockIndex&&a.Segment==b.Segment&&a.Phase==b.Phase&&a.Condition==b.Condition;}
        public void Dispose(){Owner();if(closed)return;lease.CloseClaim(leaseToken);closed=true;}
    }
}

