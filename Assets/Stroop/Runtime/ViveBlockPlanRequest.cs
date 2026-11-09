using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Ephemeral request for a plan produced by the same Experiment. It is not
    // consent, a file write, durable storage, presentation or validity proof.
    internal sealed class ViveBlockPlanRequest
    {
        readonly ViveDevelopmentParticipant participant;readonly MonotonicClock clock;
        readonly ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
        readonly JournalEvent prepared;readonly ViveRawRevocation revocation;readonly Thread owner;
        internal readonly long PlanEventSequence;
        internal readonly bool PracticeScope;
        ViveBlockPlanRequest(ViveDevelopmentParticipant p,MonotonicClock c,ViveDevelopmentParticipant.NamedRecordingTaskStamp s,JournalEvent e)
        {participant=p;clock=c;stamp=s;prepared=e;revocation=p.NamedRawRevocation;owner=Thread.CurrentThread;PlanEventSequence=e.Sequence;
            PracticeScope=String.Equals(e.Fields["plan_scope"],"PRACTICE",StringComparison.Ordinal);}
        static bool SameStamp(ViveDevelopmentParticipant.NamedRecordingTaskStamp a,ViveDevelopmentParticipant.NamedRecordingTaskStamp b)
        {return a!=null&&b!=null&&Object.ReferenceEquals(a.Owner,b.Owner)&&Object.ReferenceEquals(a.Clock,b.Clock)&&
            Object.ReferenceEquals(a.Context,b.Context)&&Object.ReferenceEquals(a.Configuration,b.Configuration)&&Object.ReferenceEquals(a.Plan,b.Plan)&&Object.ReferenceEquals(a.Intent,b.Intent)&&a.Generation==b.Generation&&a.EventCursor==b.EventCursor&&
            a.Phase==b.Phase&&a.Block==b.Block&&a.Segment==b.Segment&&String.Equals(a.Session,b.Session,StringComparison.Ordinal)&&
            String.Equals(a.TrialId,b.TrialId,StringComparison.Ordinal);}
        // The FIFO has immutable copies, not the Experiment's event object.
        // Match every field, all typed in-process identities, and both clocks;
        // an equal-looking event alone is never an authorization.
        static bool SameEvent(JournalEvent a,JournalEvent b)
        {
            if(a==null||b==null||a.Sequence!=b.Sequence||BitConverter.DoubleToInt64Bits(a.Time)!=BitConverter.DoubleToInt64Bits(b.Time)||
                !String.Equals(a.ClockId,b.ClockId,StringComparison.Ordinal)||!String.Equals(a.SessionId,b.SessionId,StringComparison.Ordinal)||
                a.BlockIndex!=b.BlockIndex||a.Segment!=b.Segment||!String.Equals(a.TrialId,b.TrialId,StringComparison.Ordinal)||
                !String.Equals(a.Kind,b.Kind,StringComparison.Ordinal)||!Object.ReferenceEquals(a.NamedOption,b.NamedOption)||
                !Object.ReferenceEquals(a.LegacyRecordingDesign,b.LegacyRecordingDesign)||!Object.ReferenceEquals(a.LegacyRecordingIntent,b.LegacyRecordingIntent)||!Object.ReferenceEquals(a.NamedPlan,b.NamedPlan)||!Object.ReferenceEquals(a.NamedBlock,b.NamedBlock)||
                !Object.ReferenceEquals(a.NamedRecordingIntent,b.NamedRecordingIntent)||a.Fields.Count!=b.Fields.Count)return false;
            foreach(var pair in a.Fields){string value;if(!b.Fields.TryGetValue(pair.Key,out value)||!String.Equals(pair.Value,value,StringComparison.Ordinal))return false;}
            return true;
        }
        internal static bool TryCapture(ViveDevelopmentParticipant p,MonotonicClock c,JournalEvent plan,out ViveBlockPlanRequest request)
        {
            request=null;if(p==null||c==null||plan==null||p.IsResearchSession||!p.RawRecordingRequested||p.NamedRawRevocation.IsRevoked||
                !p.UsesEventClock(c))return false;
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp before,after;
                if(!p.TryCaptureRawRecordingTaskStamp(c,out before)||before.Context==null||
                    plan.Kind!="block_plan_prepared"||plan.TrialId!=null||plan.Sequence!=before.EventCursor||
                    !before.Context.MatchesEvent(plan)||
                    !String.Equals(plan.SessionId,before.Session,StringComparison.Ordinal)||!String.Equals(plan.ClockId,c.ClockId,StringComparison.Ordinal)||
                    !p.MatchesRawRecordingBlockPlan(plan)||!p.TryCaptureRawRecordingTaskStamp(c,out after)||!SameStamp(before,after)||
                    p.NamedRawRevocation.IsRevoked)return false;
                request=new ViveBlockPlanRequest(p,c,after,plan);return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){return false;}
        }
        internal bool BelongsTo(ViveDevelopmentParticipant p,MonotonicClock c,ViveNamedSimulationPlan plan,
            ViveNamedRecordingIntent intent,ViveRawRevocation revoke)
        {return stamp.Context!=null&&!stamp.Context.IsLegacy&&plan!=null&&intent!=null&&Object.ReferenceEquals(participant,p)&&Object.ReferenceEquals(clock,c)&&Object.ReferenceEquals(stamp.Plan,plan)&&
            Object.ReferenceEquals(stamp.Intent,intent)&&Object.ReferenceEquals(revocation,revoke);}
        internal bool BelongsToContext(ViveDevelopmentParticipant p,MonotonicClock c,ViveRawSessionContext context,ViveRawRevocation revoke)
        {return context!=null&&Object.ReferenceEquals(participant,p)&&Object.ReferenceEquals(clock,c)&&Object.ReferenceEquals(stamp.Context,context)&&
            Object.ReferenceEquals(stamp.Configuration,context.Configuration)&&Object.ReferenceEquals(revocation,revoke);}
        internal bool MatchesWriterPlannedEvent(JournalEvent item)
        {return !revocation.IsRevoked&&SameEvent(prepared,item)&&!revocation.IsRevoked;}
        // Called only by the participant inside its own owner-thread mutation.
        // The mutation has already advanced its generation, so it cannot use
        // the pre-mutation stamp as a live stamp at this point.
        internal bool PlanMatchesInsideMutation(ViveDevelopmentParticipant p,MonotonicClock c)
        {return Object.ReferenceEquals(Thread.CurrentThread,owner)&&Object.ReferenceEquals(participant,p)&&Object.ReferenceEquals(clock,c)&&
            !revocation.IsRevoked&&participant.MatchesRawRecordingBlockPlan(prepared)&&!revocation.IsRevoked;}
        // Instantaneous only: the caller must independently check the exact
        // worker run/epoch and handle a later fault or withdrawal as UNKNOWN.
        internal bool TryValidateCurrentForReceipt(ViveDevelopmentParticipant p,MonotonicClock c)
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,owner)||!Object.ReferenceEquals(participant,p)||!Object.ReferenceEquals(clock,c)||revocation.IsRevoked)return false;
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp before,after;
                return participant.TryCaptureRawRecordingTaskStamp(clock,out before)&&SameStamp(stamp,before)&&
                    participant.MatchesRawRecordingBlockPlan(prepared)&&participant.TryCaptureRawRecordingTaskStamp(clock,out after)&&
                    SameStamp(before,after)&&!revocation.IsRevoked;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){return false;}
        }
    }
}
