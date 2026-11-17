using System;
using System.Collections.Generic;
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
        readonly ViveLegacyWozTaskOwner wozOwner;readonly ViveLegacyWozTaskOwner.Binding wozBinding;
        readonly ControlSelection? capturedSelection;readonly string capturedSourceOwner;readonly long capturedSourceGeneration;
        internal readonly long PlanEventSequence;
        internal readonly bool PracticeScope;
        ViveBlockPlanRequest(ViveDevelopmentParticipant p,MonotonicClock c,ViveDevelopmentParticipant.NamedRecordingTaskStamp s,JournalEvent e)
        {participant=p;clock=c;stamp=s;prepared=e;revocation=p.NamedRawRevocation;owner=Thread.CurrentThread;PlanEventSequence=e.Sequence;
            capturedSelection=p.LockedControl;capturedSourceOwner=p.LegacyWozSourceOwner;capturedSourceGeneration=p.LegacyWozSourceGeneration;
            PracticeScope=String.Equals(e.Fields["plan_scope"],"PRACTICE",StringComparison.Ordinal);
            if(s.Context.IsLegacyWoz&&!PracticeScope&&p.LegacyWozRecording!=null)
            {wozOwner=p.LegacyWozRecording.TaskOwner;if(!wozOwner.TryCapture(out wozBinding))throw new InvalidOperationException("Current WoZ plan owner required");}}
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
        static readonly HashSet<string> FocusFields=new HashSet<string>(StringComparer.Ordinal)
        {"focus_schema","desktop_focused","xr_focused","actor","code","reason","detail","source","measurement_quality"};
        static bool ContextField(string key)
        {return key=="response_rule"||key=="task_kind"||key=="search_schema"||key=="training_protocol"||key=="eye_recording_policy"||
            key=="learning_stage"||key=="practice_attempt"||key=="tutorial_round"||
            key.StartsWith("named_",StringComparison.Ordinal)||key.StartsWith("legacy_",StringComparison.Ordinal);}
        bool FocusEvidenceMatches(JournalEvent item)
        {
            if(item==null||item.Kind!="focus_changed"||!stamp.Context.MatchesEvent(item)||item.SessionId!=stamp.Session||
                item.ClockId!=clock.ClockId||item.BlockIndex!=stamp.Block||item.Segment!=stamp.Segment||item.TrialId!=stamp.TrialId||
                !Object.ReferenceEquals(item.NamedOption,prepared.NamedOption)||!Object.ReferenceEquals(item.NamedBlock,prepared.NamedBlock))return false;
            string desktop,xr,reason;
            if(!item.Fields.TryGetValue("desktop_focused",out desktop)||(desktop!="True"&&desktop!="False")||
                !item.Fields.TryGetValue("xr_focused",out xr)||(xr!="True"&&xr!="False")||
                !item.Fields.TryGetValue("reason",out reason)||
                (reason!="focus_baseline"&&reason!=(desktop=="True"&&xr=="True"?"focus_restored":"focus_lost")))return false;
            string value;
            if(!item.Fields.TryGetValue("focus_schema",out value)||value!="1"||
                !item.Fields.TryGetValue("actor",out value)||value!="Owner"||
                !item.Fields.TryGetValue("code",out value)||value!="focus_state"||
                !item.Fields.TryGetValue("source",out value)||value!="owner_window_and_openxr_poll"||
                !item.Fields.TryGetValue("measurement_quality",out value)||value!="NOT_ESTABLISHED"||
                !item.Fields.TryGetValue("detail",out value)||value!="desktop_focused="+desktop+";xr_focused="+xr+
                    ";task_input="+(xr=="True"?"requires_current_readiness":"blocked")+";sampling=physical_validity_required")return false;
            // Only the known focus declaration and the unchanged preparation
            // context may extend a pending receipt. Unknown extra metadata,
            // invented context values or another semantic event are stale.
            foreach(var pair in item.Fields)
            {
                if(FocusFields.Contains(pair.Key))continue;
                if(!ContextField(pair.Key)||
                    !prepared.Fields.TryGetValue(pair.Key,out value)||value!=pair.Value)return false;
            }
            foreach(var pair in prepared.Fields)
                if(ContextField(pair.Key)&&
                    (!item.Fields.TryGetValue(pair.Key,out value)||value!=pair.Value))return false;
            return true;
        }
        bool TrackingEvidenceMatches(JournalEvent item)
        {
            if(item==null||(item.Kind!="tracking_gap_begin"&&item.Kind!="tracking_gap_end")||
                !stamp.Context.MatchesEvent(item)||item.SessionId!=stamp.Session||item.ClockId!=clock.ClockId||
                item.BlockIndex!=stamp.Block||item.Segment!=stamp.Segment||item.TrialId!=stamp.TrialId||
                !Object.ReferenceEquals(item.NamedOption,prepared.NamedOption)||!Object.ReferenceEquals(item.NamedBlock,prepared.NamedBlock))return false;
            string reason,detail,schema,actor,code;
            if(!item.Fields.TryGetValue("tracking_gap_schema",out schema)||schema!="1"||
                !item.Fields.TryGetValue("actor",out actor)||actor!="Owner"||
                !item.Fields.TryGetValue("code",out code)||code!="tracking_gap"||
                !item.Fields.TryGetValue("reason",out reason)||(reason!="HeadPose"&&reason!="ControllerPose"&&reason!="EyeQuality"&&reason!="RecordingAuthority")||
                !item.Fields.TryGetValue("detail",out detail)||detail!=(item.Kind=="tracking_gap_begin"?
                    "task_input=blocked;missing_measurement=UNKNOWN":"task_input=revalidated;missing_measurement=UNKNOWN"))return false;
            foreach(var pair in item.Fields)
            {
                if(pair.Key=="tracking_gap_schema"||pair.Key=="actor"||pair.Key=="code"||pair.Key=="reason"||pair.Key=="detail")continue;
                string value;
                if(!ContextField(pair.Key)||
                    !prepared.Fields.TryGetValue(pair.Key,out value)||value!=pair.Value)return false;
            }
            foreach(var pair in prepared.Fields)
            {
                string value;
                if(ContextField(pair.Key)&&
                    (!item.Fields.TryGetValue(pair.Key,out value)||value!=pair.Value))return false;
            }
            return true;
        }
        bool MatchesCapturedOrEvidenceExtendedStamp(ViveDevelopmentParticipant.NamedRecordingTaskStamp value)
        {
            if(participant.LockedControl!=capturedSelection||participant.LegacyWozSourceOwner!=capturedSourceOwner||
                participant.LegacyWozSourceGeneration!=capturedSourceGeneration)return false;
            if(wozOwner==null&&SameStamp(stamp,value))return true;
            if(value==null||stamp==null||stamp.Context==null||
                !Object.ReferenceEquals(stamp.Owner,value.Owner)||!Object.ReferenceEquals(stamp.Clock,value.Clock)||
                !Object.ReferenceEquals(stamp.Context,value.Context)||!Object.ReferenceEquals(stamp.Configuration,value.Configuration)||
                !Object.ReferenceEquals(stamp.Plan,value.Plan)||!Object.ReferenceEquals(stamp.Intent,value.Intent)||
                stamp.Phase!=value.Phase||stamp.Block!=value.Block||stamp.Segment!=value.Segment||stamp.Session!=value.Session||stamp.TrialId!=value.TrialId||
                stamp.Generation<0||value.Generation<stamp.Generation||value.EventCursor<stamp.EventCursor||value.EventCursor-stamp.EventCursor>256)return false;
            if(wozOwner!=null)
            {
                ViveLegacyWozTaskOwner.Binding current;
                if(wozBinding==null||!stamp.Context.IsLegacyWoz||PracticeScope||!wozOwner.TryCapture(out current)||
                    current.WireRevision!=wozBinding.WireRevision||current.SourceOwner!=wozBinding.SourceOwner||
                    current.SourceGeneration!=wozBinding.SourceGeneration||current.Selection!=wozBinding.Selection)return false;
            }
            if(SameStamp(stamp,value))return true;
            var tail=participant.ReadEventsAfter(stamp.EventCursor,257);
            if(tail.Count!=value.EventCursor-stamp.EventCursor)return false;
            int focusCount=0;string openGap=null;double priorTime=prepared.Time,observed;
            if(!clock.TryReadRecordingAdmissionTime(out observed))return false;
            for(int i=0;i<tail.Count;i++)
            {
                var item=tail[i];string evidenceKind;
                if(item.Sequence!=stamp.EventCursor+i+1||Double.IsNaN(item.Time)||Double.IsInfinity(item.Time)||
                    item.Time<priorTime||item.Time>observed)return false;
                priorTime=item.Time;
                if(item.Kind=="focus_changed")
                {if(!FocusEvidenceMatches(item))return false;focusCount++;continue;}
                if(item.Kind=="tracking_gap_begin"||item.Kind=="tracking_gap_end")
                {
                    if(!TrackingEvidenceMatches(item))return false;
                    string cause=item.Fields["reason"];
                    if(item.Kind=="tracking_gap_begin"){if(openGap!=null)return false;openGap=cause;}
                    else {if(openGap!=cause)return false;openGap=null;}
                    focusCount++;continue;
                }
                if(wozOwner==null||item.Kind!=ViveLegacyWozRecordingSchema.EventKind||!stamp.Context.MatchesEvent(item)||item.ClockId!=clock.ClockId||
                    !item.Fields.TryGetValue("woz_event",out evidenceKind)||(evidenceKind!="TaskReceipt"&&evidenceKind!="Preflight"))return false;
                ViveLegacyWozRecordingSchema.Validate(item.Kind,item.Fields,item.ClockId,item.Time,true);
            }
            // One RecordFocusState call is one named mutation (two generation
            // increments) and one event. Unrelated no-op mutations cannot hide
            // behind a legitimate focus event. Existing WoZ-only receipt/
            // preflight behavior remains separately bound to its source owner.
            if(focusCount>0)return value.Generation-stamp.Generation==2L*tail.Count;
            return wozOwner!=null;
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
                return participant.TryCaptureRawRecordingTaskStamp(clock,out before)&&MatchesCapturedOrEvidenceExtendedStamp(before)&&
                    participant.MatchesRawRecordingBlockPlan(prepared)&&participant.TryCaptureRawRecordingTaskStamp(clock,out after)&&
                    SameStamp(before,after)&&!revocation.IsRevoked;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){return false;}
        }
    }
}
