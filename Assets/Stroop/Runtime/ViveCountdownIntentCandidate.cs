using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum ViveCountdownIntent { BeginBlock, ResumeCountdown }

    // A caller-supplied description of an actual host snapshot, NOT evidence of
    // host identity, physical devices, authorization or apparatus readiness.
    // ResumeAfterMainRequiresCountdown must be verified from the real host; a
    // last interrupted trial can instead proceed to Questionnaire on resume.
    public sealed class ViveCountdownIntentContext
    {
        public TaskSnapshot Task {get;private set;}
        public ViveCountdownIntent Intent {get;private set;}
        public Phase? PausedFrom {get;private set;}
        public int ExpectedCountdownSegment {get;private set;}
        public long SourceConfirmationSequence {get;private set;}
        public string SourceOwner {get;private set;}
        public string Epoch {get;private set;}
        public string PreviousEpoch {get;private set;}
        public string OperatorStream {get;private set;}
        public string MappingVersion {get;private set;}
        public string ParticipantClock {get;private set;}
        public string OperatorClock {get;private set;}
        public ViveCountdownIntentContext(TaskSnapshot task,ViveCountdownIntent intent,Phase? pausedFrom,int expectedCountdownSegment,
            long sourceConfirmationSequence,string sourceOwner,string epoch,string previousEpoch,string operatorStream,string mappingVersion,
            string participantClock,string operatorClock,bool ownerConfirmedLiveSource,bool actualHostSnapshotConfirmed,
            bool explicitResumeConfirmed,bool resumeAfterMainRequiresCountdown)
        {
            if(task==null||!Enum.IsDefined(typeof(ViveCountdownIntent),intent)||task.BlockIndex<0||!task.Condition.HasValue||
                sourceConfirmationSequence<0||!ownerConfirmedLiveSource||!actualHostSnapshotConfirmed)
                throw new ArgumentException("Explicit authoritative context and confirmed Live source are required");
            WireCodec.RequireIdentity(epoch);
            WireCodec.RequireIdentity(sourceOwner);WireCodec.RequireIdentity(mappingVersion);
            WireCodec.RequireIdentity(participantClock);WireCodec.RequireIdentity(operatorClock);
            if(previousEpoch!=null)WireCodec.RequireIdentity(previousEpoch);
            if(previousEpoch==epoch||participantClock==operatorClock||operatorStream!=ViveDevelopmentTransport.OperatorStream(mappingVersion,epoch))
                throw new ArgumentException("A fresh epoch, bound stream and distinct clocks are required");
            if(intent==ViveCountdownIntent.BeginBlock)
            {
                if(task.Phase!=Phase.BlockReady||task.Segment!=0||pausedFrom.HasValue||expectedCountdownSegment!=task.Segment||explicitResumeConfirmed||resumeAfterMainRequiresCountdown)
                    throw new ArgumentException("A real BlockReady context is required for block countdown intent");
            }
            else
            {
                if(task.Phase!=Phase.Paused||previousEpoch==null||!explicitResumeConfirmed||
                    (pausedFrom!=Phase.Countdown&&pausedFrom!=Phase.MainTask))
                    throw new ArgumentException("Explicit paused recovery, prior epoch and supported origin are required");
                if(pausedFrom==Phase.MainTask)
                {
                    if(!resumeAfterMainRequiresCountdown||task.Segment==Int32.MaxValue||expectedCountdownSegment!=task.Segment+1)
                        throw new ArgumentException("A verified next trial and incremented segment are required");
                }
                else if(resumeAfterMainRequiresCountdown||expectedCountdownSegment!=task.Segment)
                    throw new ArgumentException("A paused countdown retains its segment");
            }
            Task=task;Intent=intent;PausedFrom=pausedFrom;ExpectedCountdownSegment=expectedCountdownSegment;
            SourceConfirmationSequence=sourceConfirmationSequence;Epoch=epoch;PreviousEpoch=previousEpoch;
            SourceOwner=sourceOwner;OperatorStream=operatorStream;MappingVersion=mappingVersion;ParticipantClock=participantClock;OperatorClock=operatorClock;
        }
        internal bool Same(ViveCountdownIntentContext other)
        {
            if(other==null)return false;
            var a=Task;var b=other.Task;
            return Intent==other.Intent&&PausedFrom==other.PausedFrom&&ExpectedCountdownSegment==other.ExpectedCountdownSegment&&
                SourceConfirmationSequence==other.SourceConfirmationSequence&&SourceOwner==other.SourceOwner&&Epoch==other.Epoch&&PreviousEpoch==other.PreviousEpoch&&
                OperatorStream==other.OperatorStream&&MappingVersion==other.MappingVersion&&ParticipantClock==other.ParticipantClock&&OperatorClock==other.OperatorClock&&
                a.SessionId==b.SessionId&&a.BlockIndex==b.BlockIndex&&a.Segment==b.Segment&&a.Phase==b.Phase&&a.Condition==b.Condition&&
                a.TrialId==b.TrialId&&a.Word==b.Word&&a.Ink==b.Ink;
        }
    }

    // Mapped intervals are declarations by the caller, not verified hardware
    // capture timestamps. Their derivation and peer transport must be validated
    // independently before any future real host could rely on this candidate.
    public sealed class ViveCountdownIntentEvidence
    {
        public ViveCountdownIntentContext Context {get;private set;}
        public string Challenge {get;private set;}
        public long ProofSequence {get;private set;}
        public long HeadSequence {get;private set;}
        public long EyeSequence {get;private set;}
        public double HeadEarliest {get;private set;}
        public double HeadLatest {get;private set;}
        public double EyeEarliest {get;private set;}
        public double EyeLatest {get;private set;}
        public bool HeadUsable {get;private set;}
        public bool EyeUsable {get;private set;}
        public ViveCountdownIntentEvidence(ViveCountdownIntentContext context,string challenge,long proofSequence,long headSequence,long eyeSequence,
            double headEarliest,double headLatest,double eyeEarliest,double eyeLatest,bool headUsable,bool eyeUsable)
        {
            if(context==null||proofSequence<0||headSequence<0||eyeSequence<0||
                !ValidInterval(headEarliest,headLatest)||!ValidInterval(eyeEarliest,eyeLatest))throw new ArgumentException("Invalid bounded observation evidence");
            WireCodec.RequireIdentity(challenge);Context=context;Challenge=challenge;ProofSequence=proofSequence;HeadSequence=headSequence;EyeSequence=eyeSequence;
            HeadEarliest=headEarliest;HeadLatest=headLatest;EyeEarliest=eyeEarliest;EyeLatest=eyeLatest;HeadUsable=headUsable;EyeUsable=eyeUsable;
        }
        static bool ValidInterval(double earliest,double latest)
        {return WireCodec.Finite(earliest)&&WireCodec.Finite(latest)&&earliest>=0&&latest>=earliest;}
    }

    // Pure, local, development-only lifecycle. It does not start/resume/tick an
    // Experiment, open a channel, prove physical identity or grant Live access.
    // A real host must provide independently checked current context every call.
    public sealed class ViveCountdownIntentCandidate
    {
        readonly ViveCountdownIntentContext context;
        readonly double issued,maximumAge;
        double lastNow,headEarliest,headLatest,eyeEarliest,eyeLatest;
        long lastProof=-1,lastHead=-1,lastEye=-1;
        bool candidate,consumed,latched;
        readonly int ownerThread=Thread.CurrentThread.ManagedThreadId;
        int foreignThreadAccess;
        public string Challenge {get;private set;}
        public string LastIssue {get;private set;}
        public bool RecoveryDecisionRequired {get{return latched||Interlocked.CompareExchange(ref foreignThreadAccess,0,0)!=0;}}
        public bool CanEnterCountdown {get{return false;}}
        public bool CanResume {get{return false;}}
        public bool CryptographicallyAuthenticated {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public ViveCountdownIntentCandidate(ViveCountdownIntentContext context,double localNow,double maximumAgeSeconds)
        {
            if(context==null||!WireCodec.Finite(localNow)||localNow<0||!WireCodec.Finite(maximumAgeSeconds)||maximumAgeSeconds<=0)
                throw new ArgumentException("Explicit context, monotonic time and finite positive freshness bound required");
            this.context=context;issued=localNow;lastNow=localNow;maximumAge=maximumAgeSeconds;Challenge=Guid.NewGuid().ToString("N");
        }
        bool Validate(ViveCountdownIntentContext current,double now)
        {
            Owner();
            if(Interlocked.CompareExchange(ref foreignThreadAccess,0,0)!=0){Latch("foreign_thread_access");return false;}
            if(latched||consumed)return false;
            if(!WireCodec.Finite(now)||now<lastNow){Latch("local_clock_failed");return false;}
            lastNow=now;
            if(!context.Same(current)){Latch("authoritative_context_or_source_changed");return false;}
            if((!candidate&&now-issued>maximumAge)||(candidate&&(now-headEarliest>maximumAge||now-eyeEarliest>maximumAge)))
            {Latch("proof_or_observation_expired");return false;}
            return true;
        }
        void Owner()
        {if(Thread.CurrentThread.ManagedThreadId!=ownerThread){Interlocked.Exchange(ref foreignThreadAccess,1);throw new InvalidOperationException("Intent candidate belongs to its owner thread");}}
        void Latch(string issue){candidate=false;latched=true;LastIssue=issue;}
        public void SignalLoss(string reason)
        {
            Owner();
            if(String.IsNullOrWhiteSpace(reason))throw new ArgumentException("Explicit loss reason required");
            Latch("loss:"+reason);
        }
        public bool Observe(ViveCountdownIntentContext current,ViveCountdownIntentEvidence evidence,double now)
        {
            if(!Validate(current,now))return false;
            if(evidence==null||!context.Same(evidence.Context)||evidence.Challenge!=Challenge)
            {LastIssue="foreign_context_or_challenge";return false;}
            if(evidence.ProofSequence<=lastProof)
            {LastIssue="duplicate_or_old_evidence";return false;}
            // Reserve every current-context proof number even when it describes
            // invalid data; the sender cannot later rewrite the same proof.
            lastProof=evidence.ProofSequence;
            // A newer proof can report invalid tracking without a new native
            // sample sequence. Never retain a prior ready candidate in that case.
            if(!evidence.HeadUsable||!evidence.EyeUsable)
            {Latch("tracking_unavailable");return false;}
            if(candidate&&(evidence.HeadEarliest<headEarliest||evidence.HeadLatest<headLatest||evidence.EyeEarliest<eyeEarliest||evidence.EyeLatest<eyeLatest))
            {Latch("observation_time_rollback");return false;}
            if(evidence.HeadEarliest<=issued||evidence.EyeEarliest<=issued||evidence.HeadLatest>now||evidence.EyeLatest>now||
                now-evidence.HeadEarliest>maximumAge||now-evidence.EyeEarliest>maximumAge)
            {Latch("current_evidence_time_invalid");return false;}
            if(evidence.HeadSequence<=lastHead||evidence.EyeSequence<=lastEye)
            {LastIssue="duplicate_or_old_evidence";return false;}
            lastHead=evidence.HeadSequence;lastEye=evidence.EyeSequence;
            headEarliest=evidence.HeadEarliest;headLatest=evidence.HeadLatest;eyeEarliest=evidence.EyeEarliest;eyeLatest=evidence.EyeLatest;
            candidate=true;LastIssue=null;return true;
        }
        public bool CandidateReady(ViveCountdownIntentContext current,double now)
        {return Validate(current,now)&&candidate;}
        // One-shot candidate consumption is NOT an authorization or a host action.
        public bool ConsumeCandidate(ViveCountdownIntentContext current,double now)
        {if(!CandidateReady(current,now))return false;candidate=false;consumed=true;LastIssue="candidate_consumed_no_host_action";return true;}
    }
}
