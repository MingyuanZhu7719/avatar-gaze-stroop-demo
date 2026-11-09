using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // These are bounded observations of task-event declarations in one
    // verified prefix, not reconstructed true presentation or trial validity.
    internal sealed class ViveRawObservedTrial
    {
        internal readonly string TrialId,PlannedList,PlannedIndex,PlannedStimulusSchema,PlannedWord,PlannedInk,PresentedList,PresentedIndex,Word,Ink,Response,DeclaredCorrect,DeclaredOnset,DeclaredReactionSeconds,InterruptionActor,InterruptionReason,InterruptionOnsetAvailable;
        // Declared software evidence, not a hardware onset or an inferred
        // replacement for an absent field in a legacy event.
        internal readonly string DeclaredPresentationTaskOnset,DeclaredPresentationTaskClock,DeclaredPresentationTaskBasis,
            DeclaredUnityRenderSeconds,DeclaredUnityRenderClock,DeclaredUnityRenderFrame,DeclaredUnityRenderRevision,DeclaredUnityRenderBasis;
        internal readonly int Block,Segment,PlannedEvents,PresentationEvents,ResponseEvents,InterruptionEvents;
        // These are JournalEvent log times, not hardware/render onset times.
        internal readonly double? PlannedEventLocalSeconds,PresentationEventLocalSeconds,ResponseEventLocalSeconds,InterruptionEventLocalSeconds;
        internal readonly long? PlannedEventSequence,PresentationEventSequence,ResponseEventSequence,InterruptionEventSequence;
        internal readonly bool ContextConflict;
        internal readonly bool InterruptionActorPresent,InterruptionReasonPresent,InterruptionOnsetAvailablePresent;
        internal string Validity {get{return "UNKNOWN";}}
        internal string GazeAlignment {get{return "UNKNOWN";}}
        internal string OpenFlushEvidence {get{return "UNKNOWN";}}
        internal string RenderProxyEvidence {get{return "UNKNOWN";}}
        internal string ResponseCommitEvidence {get{return "UNKNOWN";}}
        internal string PlannedStimulusState {get{return ViveRawTrialAccumulator.StimulusState(PlannedStimulusSchema,PlannedWord,PlannedInk);}}
        internal bool EventConflict {get{return ContextConflict||PlannedStimulusState=="CONFLICT"||PlannedEvents>1||PresentationEvents>1||ResponseEvents>1||InterruptionEvents>1||(ResponseEvents>0&&InterruptionEvents>0);}}
        // Missing a planned declaration or final observed outcome, or a
        // response without a presentation declaration, remains incomplete.
        internal bool IncompleteEvidence {get{return PlannedEvents==0||(ResponseEvents>0&&PresentationEvents==0)||(ResponseEvents==0&&InterruptionEvents==0);}}
        internal ViveRawObservedTrial(ViveRawTrialAccumulator.Trial x)
        {TrialId=x.Id;Block=x.Block;Segment=x.Segment;PlannedEvents=x.Planned;PresentationEvents=x.Presented;ResponseEvents=x.Responded;InterruptionEvents=x.Interrupted;
            PlannedList=x.List;PlannedIndex=x.Index;PlannedStimulusSchema=x.StimulusSchema;PlannedWord=x.PlannedWord;PlannedInk=x.PlannedInk;
            PresentedList=x.PresentedList;PresentedIndex=x.PresentedIndex;Word=x.Word;Ink=x.Ink;Response=x.Response;
            DeclaredCorrect=x.Correct;DeclaredOnset=x.Onset;DeclaredReactionSeconds=x.Reaction;
            DeclaredPresentationTaskOnset=x.PresentationTaskOnset;DeclaredPresentationTaskClock=x.PresentationTaskClock;DeclaredPresentationTaskBasis=x.PresentationTaskBasis;
            DeclaredUnityRenderSeconds=x.UnityRenderSeconds;DeclaredUnityRenderClock=x.UnityRenderClock;DeclaredUnityRenderFrame=x.UnityRenderFrame;
            DeclaredUnityRenderRevision=x.UnityRenderRevision;DeclaredUnityRenderBasis=x.UnityRenderBasis;
            InterruptionActor=x.InterruptionActor;InterruptionReason=x.InterruptionReason;InterruptionOnsetAvailable=x.InterruptionOnsetAvailable;
            InterruptionActorPresent=x.InterruptionActorPresent;InterruptionReasonPresent=x.InterruptionReasonPresent;InterruptionOnsetAvailablePresent=x.InterruptionOnsetAvailablePresent;
            PlannedEventLocalSeconds=x.PlannedAt;PresentationEventLocalSeconds=x.PresentedAt;ResponseEventLocalSeconds=x.RespondedAt;InterruptionEventLocalSeconds=x.InterruptedAt;
            PlannedEventSequence=x.PlannedSequence;PresentationEventSequence=x.PresentedSequence;ResponseEventSequence=x.RespondedSequence;InterruptionEventSequence=x.InterruptedSequence;ContextConflict=x.Conflict;}
    }
    internal sealed class ViveRawObservedRating
    {
        internal readonly int Block,Segment;internal readonly double EventLocalSeconds;internal readonly string Item,DeclaredValue,Language,Version;
        internal ViveRawObservedRating(int block,int segment,double time,string item,string value,string language,string version)
        {Block=block;Segment=segment;EventLocalSeconds=time;Item=item;DeclaredValue=value;Language=language;Version=version;}
        internal string Validity {get{return "UNKNOWN";}}
    }
    internal sealed class ViveRawObservedPlanBlock
    {
        internal readonly int Index,ListIndex;internal readonly string Option,Schedule,Geometry;
        internal ViveRawObservedPlanBlock(int index,int list,string option,string schedule,string geometry)
        {Index=index;ListIndex=list;Option=option;Schedule=schedule;Geometry=geometry;}
    }
    internal sealed class ViveRawObservedSafetyEvent
    {
        internal readonly long Sequence;internal readonly int Block,Segment;internal readonly double EventLocalSeconds;
        internal readonly string Kind,TrialId,Actor,Code,Reason,Detail,OnsetAvailable,PausedSeconds,ActiveSeconds,PhaseFrom,PhaseTo,DurationSeconds,RestSeconds;
        internal readonly bool PhaseFromPresent,PhaseToPresent,ActiveSecondsPresent,DurationSecondsPresent,RestSecondsPresent;
        internal readonly bool ActorPresent,CodePresent,ReasonPresent,DetailPresent,OnsetAvailablePresent,PausedSecondsPresent;
        internal ViveRawObservedSafetyEvent(long seq,int block,int segment,double time,string kind,string trial,string actor,string code,string reason,string detail,string onset,string paused,string active,string phaseFrom,string phaseTo,bool phaseFromPresent,bool phaseToPresent,bool activePresent,string duration,bool durationPresent,string rest,bool restPresent,bool actorPresent,bool codePresent,bool reasonPresent,bool detailPresent,bool onsetPresent,bool pausedPresent)
        {Sequence=seq;Block=block;Segment=segment;EventLocalSeconds=time;Kind=kind;TrialId=trial;Actor=actor;Code=code;Reason=reason;Detail=detail;
            OnsetAvailable=onset;PausedSeconds=paused;ActiveSeconds=active;PhaseFrom=phaseFrom;PhaseTo=phaseTo;PhaseFromPresent=phaseFromPresent;PhaseToPresent=phaseToPresent;
            ActiveSecondsPresent=activePresent;DurationSeconds=duration;DurationSecondsPresent=durationPresent;RestSeconds=rest;RestSecondsPresent=restPresent;
            ActorPresent=actorPresent;CodePresent=codePresent;ReasonPresent=reasonPresent;DetailPresent=detailPresent;OnsetAvailablePresent=onsetPresent;PausedSecondsPresent=pausedPresent;}
    }
    internal sealed class ViveRawTrialProjection
    {
        readonly ReadOnlyCollection<ViveRawObservedTrial> trials;readonly ReadOnlyCollection<ViveRawObservedRating> ratings;
        readonly ReadOnlyCollection<ViveRawObservedPlanBlock> blocks;
        readonly ReadOnlyCollection<ViveProEyeWorker.ObservedEyeStatus> statuses;
        readonly ReadOnlyCollection<ViveRawObservedSafetyEvent> safetyEvents;
        readonly ReadOnlyCollection<ViveRawPreparedPlan> preparedPlans;
        internal IReadOnlyList<ViveRawObservedTrial> Trials {get{return trials;}}
        internal IReadOnlyList<ViveRawObservedRating> Ratings {get{return ratings;}}
        internal IReadOnlyList<ViveRawObservedPlanBlock> DeclaredPlanBlocks {get{return blocks;}}
        // Each status is a local provider-return declaration, not a precise
        // interruption interval, lost-sample count, or eye-to-trial join.
        internal IReadOnlyList<ViveProEyeWorker.ObservedEyeStatus> ObservedStatuses {get{return statuses;}}
        internal IReadOnlyList<ViveRawObservedSafetyEvent> ObservedSafetyEvents {get{return safetyEvents;}}
        internal IReadOnlyList<ViveRawPreparedPlan> PreparedPlans {get{return preparedPlans;}}
        internal readonly string DeclaredSessionId,DeclaredClockId,DeclaredPlanId,DeclaredLegacyDesignId;
        internal readonly string DesignVariant;
        internal readonly int UnassignedTrialEvents,UnsupportedPreparedPlanEvents;
        internal string Coverage {get{return "UNKNOWN";}}
        internal bool CompleteSessionProven {get{return false;}}
        internal bool SourceAuthenticated {get{return false;}}
        internal bool ParticipantConsentAuthenticated {get{return false;}}
        internal bool CurrentAccessAuthorized {get{return false;}}
        internal string CurrentWithdrawalStatus {get{return "UNKNOWN";}}
        internal bool SamplesJoinedToTrials {get{return false;}}
        internal string WriterConfirmation {get{return "UNKNOWN";}}
        internal ViveRawTrialProjection(List<ViveRawObservedTrial> t,List<ViveRawObservedRating> r,List<ViveRawObservedPlanBlock> b,List<ViveProEyeWorker.ObservedEyeStatus> s,List<ViveRawObservedSafetyEvent> e,List<ViveRawPreparedPlan> prepared,
            string session,string clock,string plan,int unassigned,int unsupported,bool legacy=false)
        {trials=new ReadOnlyCollection<ViveRawObservedTrial>(t);ratings=new ReadOnlyCollection<ViveRawObservedRating>(r);blocks=new ReadOnlyCollection<ViveRawObservedPlanBlock>(b);
            statuses=new ReadOnlyCollection<ViveProEyeWorker.ObservedEyeStatus>(s);safetyEvents=new ReadOnlyCollection<ViveRawObservedSafetyEvent>(e);preparedPlans=new ReadOnlyCollection<ViveRawPreparedPlan>(prepared);
            DesignVariant=legacy?"LEGACY_ABCD":"NAMED_OPTIONS";DeclaredSessionId=session;DeclaredClockId=clock;DeclaredPlanId=legacy?null:plan;DeclaredLegacyDesignId=legacy?plan:null;UnassignedTrialEvents=unassigned;UnsupportedPreparedPlanEvents=unsupported;}
    }
    // Internal reader-owned accumulator, never a caller callback. It retains at
    // most 512 trials, 128 ratings, 2,048 local statuses and 2,048 safety
    // events with bounded fields; overflow discards
    // the entire projection. No stream/path/file operation occurs here.
    internal sealed class ViveRawTrialAccumulator
    {
        internal sealed class ProjectionLimit:Exception {}
        internal sealed class ProjectionWithheld:Exception {}
        internal sealed class Trial
        {
            internal string Id,List,Index,StimulusSchema,PlannedWord,PlannedInk,PresentedList,PresentedIndex,Word,Ink,Response,Correct,Onset,Reaction,InterruptionActor,InterruptionReason,InterruptionOnsetAvailable;
            internal string PresentationTaskOnset,PresentationTaskClock,PresentationTaskBasis,UnityRenderSeconds,UnityRenderClock,UnityRenderFrame,UnityRenderRevision,UnityRenderBasis;
            internal int Block,Segment,Planned,Presented,Responded,Interrupted;
            internal double? PlannedAt,PresentedAt,RespondedAt,InterruptedAt;internal bool Conflict;
            internal bool InterruptionActorPresent,InterruptionReasonPresent,InterruptionOnsetAvailablePresent;
            internal long? PlannedSequence,PresentedSequence,RespondedSequence,InterruptedSequence;
        }
        const int MaxTrials=512,MaxRatings=128,MaxStatuses=2048,MaxSafetyEvents=2048,MaxFieldChars=256,MaxTotalChars=524288;
        readonly Dictionary<string,Trial> byId=new Dictionary<string,Trial>(StringComparer.Ordinal);
        readonly List<Trial> trials=new List<Trial>();readonly List<ViveRawObservedRating> ratings=new List<ViveRawObservedRating>();
        readonly List<ViveRawObservedPlanBlock> blocks=new List<ViveRawObservedPlanBlock>();
        readonly List<ViveProEyeWorker.ObservedEyeStatus> statuses=new List<ViveProEyeWorker.ObservedEyeStatus>();
        readonly List<ViveRawObservedSafetyEvent> safetyEvents=new List<ViveRawObservedSafetyEvent>();
        readonly List<ViveRawPreparedPlan> preparedPlans=new List<ViveRawPreparedPlan>();
        string session,clock,plan;int chars,unassigned,unsupportedPlans;bool legacy;
        string Keep(string s)
        {if(s==null)return null;if(s.Length>MaxFieldChars||s.Length>MaxTotalChars-chars)throw new ProjectionLimit();chars+=s.Length;return s;}
        string Field(IReadOnlyDictionary<string,string> fields,string key)
        {string value;return fields.TryGetValue(key,out value)?Keep(value):null;}
        // Called only after the strict manifest parser has checked these
        // declarations; they still do not authenticate source or assignment.
        internal void SetManifest(string sessionValue,string clockValue,string planValue,int[] options,int[] lists,string[] schedules,string[] geometries)
        {
            if(session!=null||options==null||lists==null||schedules==null||geometries==null||options.Length<1||options.Length>4||
                lists.Length!=options.Length||schedules.Length!=options.Length||geometries.Length!=options.Length)throw new ProjectionLimit();
            session=Keep(sessionValue);clock=Keep(clockValue);plan=Keep(planValue);
            for(int i=0;i<options.Length;i++)blocks.Add(new ViveRawObservedPlanBlock(i,lists[i],options[i]==0?"Automatic":"NoAvatar",Keep(schedules[i]),Keep(geometries[i])));
        }
        internal void SetLegacyManifest(string sessionValue,string clockValue,string designValue,int[] conditions,int[] lists,string[] mappings,string[] geometries)
        {
            if(session!=null||conditions==null||lists==null||mappings==null||geometries==null||conditions.Length!=4||lists.Length!=4||mappings.Length!=4||geometries.Length!=4)throw new ProjectionLimit();
            session=Keep(sessionValue);clock=Keep(clockValue);plan=Keep(designValue);legacy=true;
            for(int i=0;i<4;i++)blocks.Add(new ViveRawObservedPlanBlock(i,lists[i],((Condition)conditions[i]).ToString(),Keep(mappings[i]),Keep(geometries[i])));
        }
        static bool WrongOrder(long? earlier,long? later){return earlier.HasValue&&later.HasValue&&earlier.Value>=later.Value;}
        static bool WrongTime(double? earlier,double? later){return earlier.HasValue&&later.HasValue&&earlier.Value>later.Value;}
        static bool ColourName(string value)
        {Colour colour;return value!=null&&Enum.TryParse<Colour>(value,false,out colour)&&Enum.IsDefined(typeof(Colour),colour)&&String.Equals(colour.ToString(),value,StringComparison.Ordinal);}
        internal static string StimulusState(string schema,string word,string ink)
        {
            if(schema==null&&word==null&&ink==null)return "UNKNOWN";
            if(schema==null)return "CONFLICT";
            if(schema!="1")return "UNSUPPORTED_SCHEMA";
            return ColourName(word)&&ColourName(ink)?"DECLARED_V1":"CONFLICT";
        }
        internal void Observe(string kind,string id,int block,int segment,long sequence,double time,IReadOnlyDictionary<string,string> fields)
        {
            if(kind=="trial_interrupted"||kind=="explicit_pause"||kind=="explicit_resume"||kind=="headset_removed"||kind=="technical_issue"||kind=="session_ended"||kind=="phase_changed"||
                kind=="block_task_ended"||kind=="questionnaire_completed"||kind=="rest_ready")
            {
                if(safetyEvents.Count>=MaxSafetyEvents)throw new ProjectionLimit();
                string phaseFrom=null,phaseTo=null,active=null,duration=null,rest=null,actor=null,code=null,reason=null,detail=null,onset=null,paused=null;
                bool fromPresent=kind=="phase_changed"&&fields.TryGetValue("from",out phaseFrom);
                bool toPresent=kind=="phase_changed"&&fields.TryGetValue("to",out phaseTo);
                bool activePresent=fields.TryGetValue("active_seconds",out active);
                bool durationPresent=kind=="questionnaire_completed"&&fields.TryGetValue("duration_seconds",out duration);
                bool restPresent=kind=="rest_ready"&&fields.TryGetValue("rest_seconds",out rest);
                bool actorPresent=fields.TryGetValue("actor",out actor),codePresent=fields.TryGetValue("code",out code);
                bool reasonPresent=fields.TryGetValue("reason",out reason),detailPresent=fields.TryGetValue("detail",out detail);
                bool onsetPresent=fields.TryGetValue("onset_available",out onset),pausedPresent=fields.TryGetValue("paused_seconds",out paused);
                safetyEvents.Add(new ViveRawObservedSafetyEvent(sequence,block,segment,time,Keep(kind),Keep(id),Keep(actor),Keep(code),
                    Keep(reason),Keep(detail),Keep(onset),Keep(paused),Keep(active),Keep(phaseFrom),Keep(phaseTo),fromPresent,toPresent,
                    activePresent,Keep(duration),durationPresent,Keep(rest),restPresent,actorPresent,codePresent,reasonPresent,detailPresent,onsetPresent,pausedPresent));
            }
            if(kind=="rating")
            {if(ratings.Count>=MaxRatings)throw new ProjectionLimit();ratings.Add(new ViveRawObservedRating(block,segment,time,Field(fields,"item"),Field(fields,"value"),Field(fields,"language"),Field(fields,"version")));return;}
            if(kind!="trial_planned"&&kind!="stimulus_presented"&&kind!="first_response"&&kind!="trial_interrupted")return;
            if(String.IsNullOrEmpty(id)){checked{unassigned++;}return;}
            Trial trial;
            if(!byId.TryGetValue(id,out trial))
            {if(trials.Count>=MaxTrials)throw new ProjectionLimit();trial=new Trial{Id=Keep(id),Block=block,Segment=segment};byId.Add(trial.Id,trial);trials.Add(trial);}
            else if(trial.Block!=block||trial.Segment!=segment)trial.Conflict=true;
            if(kind=="trial_planned")
            {if(trial.Planned++==0){trial.PlannedAt=time;trial.PlannedSequence=sequence;trial.List=Field(fields,"list");trial.Index=Field(fields,"planned_index");
                trial.StimulusSchema=Field(fields,"planned_stimulus_schema");trial.PlannedWord=Field(fields,"planned_word");trial.PlannedInk=Field(fields,"planned_ink");}}
            else if(kind=="stimulus_presented")
            {if(trial.Presented++==0){trial.PresentedAt=time;trial.PresentedSequence=sequence;trial.PresentedList=Field(fields,"list");trial.PresentedIndex=Field(fields,"planned_index");trial.Word=Field(fields,"word");trial.Ink=Field(fields,"ink");
                trial.PresentationTaskOnset=Field(fields,"task_onset_seconds");trial.PresentationTaskClock=Field(fields,"task_onset_clock_id");trial.PresentationTaskBasis=Field(fields,"task_onset_basis");
                trial.UnityRenderSeconds=Field(fields,"unity_render_proxy_seconds");trial.UnityRenderClock=Field(fields,"unity_render_proxy_clock_id");trial.UnityRenderFrame=Field(fields,"unity_render_proxy_frame");
                trial.UnityRenderRevision=Field(fields,"unity_render_proxy_revision");trial.UnityRenderBasis=Field(fields,"unity_render_proxy_basis");}}
            else if(kind=="first_response")
            {if(trial.Responded++==0){trial.RespondedAt=time;trial.RespondedSequence=sequence;trial.Response=Field(fields,"response");trial.Correct=Field(fields,"correct");trial.Onset=Field(fields,"onset");trial.Reaction=Field(fields,"rt_seconds");}}
            else if(trial.Interrupted++==0){trial.InterruptedAt=time;trial.InterruptedSequence=sequence;
                string actor,reason,onset;trial.InterruptionActorPresent=fields.TryGetValue("actor",out actor);
                trial.InterruptionReasonPresent=fields.TryGetValue("reason",out reason);trial.InterruptionOnsetAvailablePresent=fields.TryGetValue("onset_available",out onset);
                trial.InterruptionActor=Keep(actor);trial.InterruptionReason=Keep(reason);trial.InterruptionOnsetAvailable=Keep(onset);}
            if(WrongOrder(trial.PlannedSequence,trial.PresentedSequence)||WrongOrder(trial.PlannedSequence,trial.RespondedSequence)||
                WrongOrder(trial.PlannedSequence,trial.InterruptedSequence)||WrongOrder(trial.PresentedSequence,trial.RespondedSequence)||
                WrongOrder(trial.PresentedSequence,trial.InterruptedSequence)||WrongTime(trial.PlannedAt,trial.PresentedAt)||
                WrongTime(trial.PlannedAt,trial.RespondedAt)||WrongTime(trial.PlannedAt,trial.InterruptedAt)||
                WrongTime(trial.PresentedAt,trial.RespondedAt)||WrongTime(trial.PresentedAt,trial.InterruptedAt))trial.Conflict=true;
            if(trial.List!=null&&trial.PresentedList!=null&&!String.Equals(trial.List,trial.PresentedList,StringComparison.Ordinal))trial.Conflict=true;
            if(trial.Index!=null&&trial.PresentedIndex!=null&&!String.Equals(trial.Index,trial.PresentedIndex,StringComparison.Ordinal))trial.Conflict=true;
            // Compare only declarations that actually exist. Legacy records
            // without the new presentation field remain missing, not inferred
            // from event time or from a later response.
            if(trial.PresentationTaskOnset!=null&&trial.Onset!=null)
            {
                double presented,response;
                if(!Double.TryParse(trial.PresentationTaskOnset,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out presented)||
                    !Double.TryParse(trial.Onset,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out response)||
                    Double.IsNaN(presented)||Double.IsInfinity(presented)||Double.IsNaN(response)||Double.IsInfinity(response)||presented!=response)
                    trial.Conflict=true;
            }
            bool onsetAvailable;
            if(trial.PresentationTaskOnset!=null&&trial.InterruptionOnsetAvailablePresent&&
                Boolean.TryParse(trial.InterruptionOnsetAvailable,out onsetAvailable)&&!onsetAvailable)trial.Conflict=true;
            if(StimulusState(trial.StimulusSchema,trial.PlannedWord,trial.PlannedInk)=="DECLARED_V1")
            {
                if(trial.Word!=null&&!String.Equals(trial.PlannedWord,trial.Word,StringComparison.Ordinal))trial.Conflict=true;
                if(trial.Ink!=null&&!String.Equals(trial.PlannedInk,trial.Ink,StringComparison.Ordinal))trial.Conflict=true;
            }
        }
        internal void ObserveStatus(ViveProEyeWorker.ObservedEyeStatus status)
        {if(status==null||statuses.Count>=MaxStatuses)throw new ProjectionLimit();statuses.Add(status);}
        internal void ObservePreparedPlan(ViveRawPreparedPlan prepared)
        {if(prepared==null){checked{unsupportedPlans++;}return;}if(preparedPlans.Count>=6)throw new ProjectionLimit();preparedPlans.Add(prepared);}
        internal ViveRawTrialProjection Finish()
        {var observed=new List<ViveRawObservedTrial>(trials.Count);foreach(var t in trials)observed.Add(new ViveRawObservedTrial(t));
            return new ViveRawTrialProjection(observed,new List<ViveRawObservedRating>(ratings),new List<ViveRawObservedPlanBlock>(blocks),
                new List<ViveProEyeWorker.ObservedEyeStatus>(statuses),new List<ViveRawObservedSafetyEvent>(safetyEvents),new List<ViveRawPreparedPlan>(preparedPlans),session,clock,plan,unassigned,unsupportedPlans,legacy);}
    }
}
