using System;
using System.Globalization;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Transient request derived from a real named Simulation participant. This
    // class cannot write, mint a receipt, authorize a camera or enter a study.
    // A task journal event is not evidence that a pre-open was flushed to disk.
    internal sealed class ViveTrialOpenRequest
    {
        internal readonly string Session,Trial,ClockId,PlanId,IntentId,ListId;
        internal readonly Colour PlannedWord,PlannedInk;
        internal readonly int Block,Segment,PlannedIndex,PracticeAttempt;
        internal readonly long TaskGeneration,TaskCursor,PlannedEventSequence;
        internal readonly double TaskLocalObserved;
        readonly ViveDevelopmentParticipant participant;readonly MonotonicClock clock;
        readonly ViveDevelopmentParticipant.NamedRecordingTaskStamp capturedStamp;
        readonly TrialRecord capturedTrial;readonly ViveNamedSimulationBlock capturedBlock;
        readonly ViveRawRevocation revocation;readonly Thread ownerThread;
        internal bool HasWriteReceipt {get{return false;}}
        internal bool HasFlushReceipt {get{return false;}}
        internal bool MayShowStimulus {get{return false;}}
        internal string Validity {get{return "UNKNOWN";}}
        ViveTrialOpenRequest(ViveDevelopmentParticipant participant,MonotonicClock clock,ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp,TrialRecord trial,JournalEvent planned,double observed)
        {Session=stamp.Session;Trial=stamp.TrialId;ClockId=stamp.Clock.ClockId;PlanId=stamp.Context.DesignId;IntentId=stamp.Context.IntentId;ListId=trial.ListId;Block=stamp.Block;Segment=stamp.Segment;
            PlannedIndex=trial.PlannedIndex;PracticeAttempt=trial.PracticeAttempt;TaskGeneration=stamp.Generation;TaskCursor=stamp.EventCursor;
            PlannedEventSequence=planned.Sequence;TaskLocalObserved=observed;this.participant=participant;this.clock=clock;
            PlannedWord=trial.Stimulus.Word;PlannedInk=trial.Stimulus.Ink;
            capturedStamp=stamp;capturedTrial=trial;capturedBlock=trial.NamedBlock;revocation=participant.NamedRawRevocation;ownerThread=Thread.CurrentThread;}
        static bool Same(ViveDevelopmentParticipant.NamedRecordingTaskStamp a,ViveDevelopmentParticipant.NamedRecordingTaskStamp b)
        {return a!=null&&b!=null&&Object.ReferenceEquals(a.Owner,b.Owner)&&Object.ReferenceEquals(a.Clock,b.Clock)&&
            Object.ReferenceEquals(a.Context,b.Context)&&Object.ReferenceEquals(a.Configuration,b.Configuration)&&Object.ReferenceEquals(a.Plan,b.Plan)&&Object.ReferenceEquals(a.Intent,b.Intent)&&a.Generation==b.Generation&&a.EventCursor==b.EventCursor&&
            a.Phase==b.Phase&&a.Block==b.Block&&a.Segment==b.Segment&&String.Equals(a.Session,b.Session,StringComparison.Ordinal)&&
            String.Equals(a.TrialId,b.TrialId,StringComparison.Ordinal);}
        // Exact in-process references, not authenticated participant/device
        // identity or permission to write. Writer-owned checks additionally
        // need the same admission, native operations and one-use receipt.
        internal bool BelongsTo(ViveDevelopmentParticipant candidate,MonotonicClock candidateClock,ViveNamedSimulationPlan plan,
            ViveNamedRecordingIntent intent,ViveRawRevocation candidateRevocation)
        {return capturedStamp.Context!=null&&!capturedStamp.Context.IsLegacy&&plan!=null&&intent!=null&&Object.ReferenceEquals(participant,candidate)&&Object.ReferenceEquals(clock,candidateClock)&&
            Object.ReferenceEquals(capturedStamp.Plan,plan)&&Object.ReferenceEquals(capturedStamp.Intent,intent)&&
            Object.ReferenceEquals(revocation,candidateRevocation);}
        // Only the owner can compare this immutable request with the current
        // task. It is an instantaneous check, not a revocable lease: state can
        // change immediately afterwards. A planned-event flush cannot prove a
        // later task cursor, so that case remains unavailable.
        internal bool TryValidateCurrentForReceipt(ViveDevelopmentParticipant candidate,MonotonicClock candidateClock)
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,ownerThread)||!Object.ReferenceEquals(participant,candidate)||
                !Object.ReferenceEquals(clock,candidateClock)||revocation.IsRevoked||TaskCursor!=PlannedEventSequence)return false;
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp before,after;
                if(!participant.TryCaptureRawRecordingTaskStamp(clock,out before)||!Same(capturedStamp,before)||
                    !Object.ReferenceEquals(capturedTrial,participant.CurrentTrial)||capturedTrial.Outcome!=TrialOutcome.Pending||capturedTrial.Onset.HasValue||
                    capturedTrial.Stimulus==null||capturedTrial.Stimulus.Word!=PlannedWord||capturedTrial.Stimulus.Ink!=PlannedInk||
                    !participant.TryCaptureRawRecordingTaskStamp(clock,out after)||!Same(before,after)||revocation.IsRevoked)return false;
                return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){return false;}
        }
        // Writer receives the actual immutable FIFO item, not caller-supplied
        // arbitrary bytes. Matching it is not itself a Write/Flush receipt.
        internal bool MatchesWriterPlannedEvent(JournalEvent value)
        {
            string list,index,schema,word,ink;
            return !revocation.IsRevoked&&value!=null&&value.Kind=="trial_planned"&&value.Sequence==PlannedEventSequence&&
                TaskCursor==PlannedEventSequence&&String.Equals(value.TrialId,Trial,StringComparison.Ordinal)&&
                String.Equals(value.SessionId,Session,StringComparison.Ordinal)&&String.Equals(value.ClockId,ClockId,StringComparison.Ordinal)&&
                value.BlockIndex==Block&&value.Segment==Segment&&capturedStamp.Context.MatchesEvent(value)&&Object.ReferenceEquals(value.NamedPlan,capturedStamp.Plan)&&
                Object.ReferenceEquals(value.NamedBlock,capturedBlock)&&Object.ReferenceEquals(value.NamedRecordingIntent,capturedStamp.Intent)&&
                value.Fields.TryGetValue("list",out list)&&String.Equals(list,ListId,StringComparison.Ordinal)&&
                value.Fields.TryGetValue("planned_index",out index)&&String.Equals(index,PlannedIndex.ToString(CultureInfo.InvariantCulture),StringComparison.Ordinal)&&
                value.Fields.TryGetValue("planned_stimulus_schema",out schema)&&schema=="1"&&
                value.Fields.TryGetValue("planned_word",out word)&&String.Equals(word,PlannedWord.ToString(),StringComparison.Ordinal)&&
                value.Fields.TryGetValue("planned_ink",out ink)&&String.Equals(ink,PlannedInk.ToString(),StringComparison.Ordinal)&&
                !revocation.IsRevoked;
        }
        // This comparison grants no write receipt or presentation authority.
        internal static bool MatchesPlannedEvent(JournalEvent planned,ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp,TrialRecord trial,MonotonicClock clock)
        {
            string list,index,schema,word,ink;
            return planned!=null&&stamp!=null&&trial!=null&&trial.Stimulus!=null&&clock!=null&&planned.Kind=="trial_planned"&&
                planned.Sequence>=0&&planned.Sequence<=stamp.EventCursor&&String.Equals(planned.TrialId,stamp.TrialId,StringComparison.Ordinal)&&
                String.Equals(planned.SessionId,stamp.Session,StringComparison.Ordinal)&&String.Equals(planned.ClockId,clock.ClockId,StringComparison.Ordinal)&&
                planned.BlockIndex==stamp.Block&&planned.Segment==stamp.Segment&&stamp.Context.MatchesEvent(planned)&&Object.ReferenceEquals(planned.NamedPlan,stamp.Plan)&&
                Object.ReferenceEquals(planned.NamedBlock,trial.NamedBlock)&&Object.ReferenceEquals(planned.NamedRecordingIntent,stamp.Intent)&&
                planned.Fields.TryGetValue("list",out list)&&String.Equals(list,trial.ListId,StringComparison.Ordinal)&&
                planned.Fields.TryGetValue("planned_index",out index)&&String.Equals(index,trial.PlannedIndex.ToString(CultureInfo.InvariantCulture),StringComparison.Ordinal)&&
                planned.Fields.TryGetValue("planned_stimulus_schema",out schema)&&schema=="1"&&
                planned.Fields.TryGetValue("planned_word",out word)&&String.Equals(word,trial.Stimulus.Word.ToString(),StringComparison.Ordinal)&&
                planned.Fields.TryGetValue("planned_ink",out ink)&&String.Equals(ink,trial.Stimulus.Ink.ToString(),StringComparison.Ordinal);
        }
        static bool LegacyTrialMatches(ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp,TrialRecord trial)
        {
            if(trial.NamedPlan!=null||trial.NamedBlock!=null||trial.NamedOption!=null)return false;
            if(trial.IsPractice)return stamp.Block==-1&&trial.PracticeAttempt>=1&&trial.PracticeAttempt<=2&&
                trial.ListId=="practice-"+stamp.Configuration.Seed+"-"+trial.PracticeAttempt;
            return stamp.Block>=0&&stamp.Block<4&&trial.Condition==stamp.Configuration.Allocation.Order[stamp.Block]&&
                trial.ListId==TrialPlans.MainListId(stamp.Configuration.Seed,stamp.Configuration.Allocation.ListByBlock[stamp.Block]);
        }
        internal static bool TryCapture(ViveDevelopmentParticipant participant,MonotonicClock clock,out ViveTrialOpenRequest result)
        {
            result=null;if(participant==null||clock==null||participant.IsResearchSession||!participant.RawRecordingRequested||participant.NamedRawRevocation.IsRevoked||
                !participant.UsesEventClock(clock))return false;
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp before,after;double now;
                if(!participant.TryCaptureRawRecordingTaskStamp(clock,out before)||before==null||before.Context==null||
                    (before.Phase!=Phase.Practice&&before.Phase!=Phase.MainTask)||String.IsNullOrEmpty(before.TrialId)||before.EventCursor<0)return false;
                var trial=participant.CurrentTrial;
                if(trial==null||trial.Outcome!=TrialOutcome.Pending||trial.Onset.HasValue||!Object.ReferenceEquals(trial.NamedPlan,before.Plan)||
                    !String.Equals(trial.SessionId,before.Session,StringComparison.Ordinal)||!String.Equals(trial.TrialId,before.TrialId,StringComparison.Ordinal)||
                    trial.BlockIndex!=before.Block||trial.Segment!=before.Segment||trial.IsPractice!=(before.Phase==Phase.Practice)||
                    trial.PlannedIndex<0||String.IsNullOrWhiteSpace(trial.ListId)||
                    (before.Context.IsLegacy?!LegacyTrialMatches(before,trial):
                        (trial.IsPractice?trial.NamedBlock!=null:(trial.NamedBlock==null||before.Block<0||before.Block>=before.Plan.Blocks.Count||
                        !Object.ReferenceEquals(trial.NamedBlock,before.Plan.Blocks[before.Block])))))return false;
                // The bound is deliberate: failure to find a recent producer
                // event fails closed rather than scanning an unbounded session.
                long cursor=Math.Max(-1,before.EventCursor-64);
                var events=participant.ReadEventsAfter(cursor,64);JournalEvent planned=null;
                foreach(var item in events)
                    if(item.Kind=="trial_planned"&&String.Equals(item.TrialId,before.TrialId,StringComparison.Ordinal))
                    {if(planned!=null)return false;planned=item;}
                if(!MatchesPlannedEvent(planned,before,trial,clock)||
                    !clock.TryReadRecordingAdmissionTime(out now)||!participant.TryCaptureRawRecordingTaskStamp(clock,out after)||!Same(before,after)||
                    !Object.ReferenceEquals(trial,participant.CurrentTrial)||trial.Outcome!=TrialOutcome.Pending||trial.Onset.HasValue||participant.NamedRawRevocation.IsRevoked)return false;
                result=new ViveTrialOpenRequest(participant,clock,after,trial,planned,now);return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {result=null;return false;}
        }
    }
}
