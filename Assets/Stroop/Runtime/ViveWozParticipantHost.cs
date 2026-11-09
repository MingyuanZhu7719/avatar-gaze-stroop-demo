using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // An opt-in, owner-thread bridge to an actual development Experiment. It
    // does not start a network, construct an eye source, record data or admit an
    // apparatus session. Existing Automatic/Quest rendering must be a separate
    // layer: clearLivePose clears ONLY a previously rendered live-control pose.
    //
    // The current phase/challenge-bound preflight is not wired to either app.
    // Therefore this bridge deliberately cannot start a live countdown, render a
    // live pose or automatically resume. It can safely expose authoritative task
    // state and apply real pause/invalidations while the integration is completed.
    public sealed class ViveWozParticipantHost : IViveDevelopmentParticipantHost
    {
        readonly Experiment experiment;
        readonly Action clearLivePose;
        readonly ViveDevelopmentParticipant namedParticipant;
        readonly IClock namedClock;
        readonly Thread exactOwner;
        readonly int ownerThread;

        public ViveWozParticipantHost(Experiment experiment,Action clearLivePose,bool explicitDevelopmentOnly)
        {
            if(!explicitDevelopmentOnly)throw new InvalidOperationException("Explicit development-only opt-in is required");
            if(experiment==null||clearLivePose==null)throw new ArgumentNullException("experiment/clearLivePose");
            if(experiment.Configuration.IsNamedDesign)throw new InvalidOperationException("Legacy WoZ host cannot represent named-option provenance");
            if(experiment.Configuration.RunKind!=RunKind.Simulation)
                throw new InvalidOperationException("Apparatus sessions remain unavailable");
            this.experiment=experiment;this.clearLivePose=clearLivePose;
            ownerThread=Thread.CurrentThread.ManagedThreadId;exactOwner=Thread.CurrentThread;
        }
        // Named context is local and read-only. No network, pose or countdown
        // authority is produced, and the legacy TaskSnapshot cannot encode it.
        internal ViveWozParticipantHost(ViveDevelopmentParticipant participant,IClock clock,bool explicitDevelopmentOnly)
        {
            if(!explicitDevelopmentOnly||participant==null||clock==null)throw new ArgumentException("Explicit named development participant and clock required");
            namedParticipant=participant;namedClock=clock;ownerThread=Thread.CurrentThread.ManagedThreadId;exactOwner=Thread.CurrentThread;
            NamedContext context;if(!participant.TryCaptureNamedWozContext(clock,out context))
                throw new InvalidOperationException("Named simulation context unavailable");
        }
        internal sealed class NamedContext
        {
            internal readonly ViveDevelopmentParticipant Owner;
            internal readonly ViveDevelopmentParticipant.NamedRecordingTaskStamp Stamp;
            internal readonly string Session,PlanId;internal readonly Phase Phase;
            internal readonly int Block,Segment;internal readonly int? PlanBlock,ListIndex;
            internal readonly ViveNamedOption? DeclaredOption;internal readonly bool PlanConfirmed;
            internal NamedContext(ViveDevelopmentParticipant owner,ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp,
                string session,Phase phase,int block,int segment,string planId,int? planBlock,ViveNamedOption? option,int? listIndex,bool confirmed)
            {Owner=owner;Stamp=stamp;Session=session;Phase=phase;Block=block;Segment=segment;PlanId=planId;PlanBlock=planBlock;DeclaredOption=option;ListIndex=listIndex;PlanConfirmed=confirmed;}
        }
        internal bool TryReadNamedContext(out NamedContext context)
        {RequireOwnerThread();context=null;return namedParticipant!=null&&namedParticipant.TryCaptureNamedWozContext(namedClock,out context);}
        internal bool IsNamedContextCurrent(NamedContext context)
        {RequireOwnerThread();return namedParticipant!=null&&namedParticipant.IsNamedWozContextCurrent(context);}
        void RequireOwnerThread()
        {
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread||!Object.ReferenceEquals(Thread.CurrentThread,exactOwner))
                throw new InvalidOperationException("The authoritative task and presentation must be accessed on their owning thread");
        }
        public bool LiveSourceSelected
        {get{RequireOwnerThread();return namedParticipant==null&&experiment.LockedControl==ControlSelection.LiveWoz;}}
        public bool CanEnterLiveCountdown {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}

        // TaskSnapshot is the existing response-free operator wire view, not a
        // second task engine. Trial/word/ink are omitted outside MainTask.
        public TaskSnapshot ReadAuthoritativeTask()
        {
            RequireOwnerThread();
            if(namedParticipant!=null)throw new InvalidOperationException("Legacy task snapshots cannot represent named context");
            var view=experiment.View(Actor.Operator);
            var stimulus=view.Stimulus;
            return new TaskSnapshot(experiment.Configuration.SessionId,experiment.BlockIndex,experiment.Segment,
                view.TrialId,stimulus==null?(Colour?)null:stimulus.Word,stimulus==null?(Colour?)null:stimulus.Ink,
                experiment.Phase,experiment.CurrentCondition);
        }

        public void ClearLivePose()
        {RequireOwnerThread();if(namedParticipant==null)clearLivePose();}

        public void ApplyLivePose(ViveDevelopmentRemotePose pose)
        {
            RequireOwnerThread();
            if(pose==null)throw new ArgumentNullException("pose");
            if(namedParticipant!=null)throw new InvalidOperationException("Named live presentation is unavailable");
            // Even apparently fresh v2 data is not a phase-bound proof. Do not
            // pass the pose to a renderer until a later validated integration
            // replaces this intentionally closed development-only boundary.
            clearLivePose();
            throw new InvalidOperationException("Live presentation is unavailable until phase-bound preflight and apparatus mapping are integrated");
        }

        // Called by the explicit owner/participant update loop BEFORE ticking the
        // Experiment. A dropped source in Countdown must invoke Pause, not merely
        // skip Tick: the underlying countdown clock otherwise continues running.
        // Because no preflight is integrated, every active LiveWoz countdown/task
        // is paused, its current trial interrupted by Experiment when applicable,
        // and the live layer cleared. Never resume automatically.
        public bool EnforceUnavailableLivePreflight()
        {
            RequireOwnerThread();
            if(namedParticipant!=null)return false;
            if(!LiveSourceSelected||(experiment.Phase!=Phase.Countdown&&experiment.Phase!=Phase.MainTask))return false;
            experiment.Pause(Actor.Owner);
            clearLivePose();
            return true;
        }
        public void PauseByOperator(){Pause(Actor.Operator);}
        public void PauseForTransportLoss(){Pause(Actor.Owner);}
        void Pause(Actor actor)
        {
            RequireOwnerThread();
            if(namedParticipant!=null)throw new InvalidOperationException("Named host is read-only");
            // Pause first: a failing renderer cleanup must not prevent a safety
            // stop, while the caller must still treat that failure as a fault.
            experiment.Pause(actor);
            clearLivePose();
        }
    }
}
