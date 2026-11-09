using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Ephemeral owner-thread diagnostic for the actual VIVE raw callback. It
    // has no sample payload, writer, pre-render trial-open proof or mutation
    // capability. It is neither a headset visibility nor photon witness.
    internal sealed class ViveRenderProxyObservation
    {
        internal readonly string Session,Trial,TaskClock;
        internal readonly long TaskGeneration,TaskCursor;
        internal readonly double UnityRenderProxySeconds,TaskLocalObservationSeconds;
        // These are values in the task model at the callback, not a reading of
        // rendered pixels or a claim that a wearer saw them.
        internal readonly Colour TaskModelWord,TaskModelInk;
        internal bool HasPriorTrialOpenProof {get{return false;}}
        internal bool Durable {get{return false;}}
        internal bool AuthorizesMutation {get{return false;}}
        ViveRenderProxyObservation(ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp,Colour word,Colour ink,double unity,double local)
        {Session=stamp.Session;Trial=stamp.TrialId;TaskClock=stamp.Clock.ClockId;TaskGeneration=stamp.Generation;TaskCursor=stamp.EventCursor;
            TaskModelWord=word;TaskModelInk=ink;UnityRenderProxySeconds=unity;TaskLocalObservationSeconds=local;}
        static bool FiniteNonnegative(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value)&&value>=0;}
        internal static bool TryObserve(ViveDevelopmentParticipant participant,MonotonicClock clock,string trialId,double unityProxy,
            out ViveRenderProxyObservation observation)
        {
            observation=null;
            if(participant==null||clock==null||String.IsNullOrEmpty(trialId)||!FiniteNonnegative(unityProxy)||
                participant.IsResearchSession||!participant.NamedRecordingRequested||participant.NamedRawRevocation.IsRevoked||!participant.UsesEventClock(clock))return false;
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp before,after;double local;
                if(!participant.TryCaptureNamedRecordingTaskStamp(clock,out before)||before==null||
                    (before.Phase!=Phase.Practice&&before.Phase!=Phase.MainTask)||!String.Equals(before.TrialId,trialId,StringComparison.Ordinal)||
                    participant.NamedRawRevocation.IsRevoked)return false;
                var trial=participant.CurrentTrial;var view=participant.ParticipantView;
                if(trial==null||trial.Stimulus==null||trial.Outcome!=TrialOutcome.Pending||trial.Onset.HasValue||view==null||
                    !String.Equals(view.TrialId,trialId,StringComparison.Ordinal)||!Object.ReferenceEquals(view.Stimulus,trial.Stimulus))return false;
                var word=trial.Stimulus.Word;var ink=trial.Stimulus.Ink;
                if(!clock.TryReadRecordingAdmissionTime(out local)||!FiniteNonnegative(local)||
                    !participant.TryCaptureNamedRecordingTaskStamp(clock,out after)||after==null||
                    before.Generation!=after.Generation||before.EventCursor!=after.EventCursor||before.Phase!=after.Phase||
                    before.Block!=after.Block||before.Segment!=after.Segment||!Object.ReferenceEquals(before.Owner,after.Owner)||
                    !Object.ReferenceEquals(before.Clock,after.Clock)||!Object.ReferenceEquals(before.Plan,after.Plan)||
                    !Object.ReferenceEquals(before.Intent,after.Intent)||!String.Equals(before.TrialId,after.TrialId,StringComparison.Ordinal)||
                    !String.Equals(before.Session,after.Session,StringComparison.Ordinal)||participant.NamedRawRevocation.IsRevoked||
                    !Object.ReferenceEquals(trial,participant.CurrentTrial)||trial.Outcome!=TrialOutcome.Pending||trial.Onset.HasValue||
                    trial.Stimulus==null||trial.Stimulus.Word!=word||trial.Stimulus.Ink!=ink)return false;
                observation=new ViveRenderProxyObservation(after,word,ink,unityProxy,local);return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {observation=null;return false;}
        }
    }
}
