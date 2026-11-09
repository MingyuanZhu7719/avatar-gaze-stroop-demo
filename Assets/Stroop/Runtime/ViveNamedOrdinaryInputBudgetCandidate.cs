using System;
using System.Collections.Generic;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Diagnostic only: this reads a real Experiment's public state but is not
    // an atomic task snapshot, reservation, recording permit, or product gate.
    // It never accepts a budget as sufficient. No safety action is gated here.
    internal static class ViveNamedOrdinaryInputBudgetCandidate
    {
        internal enum Status { Unknown, RejectedInput, SafetyNotGated }
        internal sealed class Result
        {
            internal readonly Status State;internal readonly string Reason;
            internal bool AuthorizesAction {get{return false;}}
            internal bool CapacityProven {get{return false;}}
            internal Result(Status state,string reason){State=state;Reason=reason;}
        }
        const int MaxUtf8TextBytes=32768,MaxFields=128;
        const string KnownEventSchema="vive-named-recording-intent-v3",KnownPlanSchema="vive-named-simulation-plan-v2";
        static readonly UTF8Encoding StrictUtf8=new UTF8Encoding(false,true);
        static Result Invalid(string reason){return new Result(Status.RejectedInput,reason);}
        static Result Unknown(string reason){return new Result(Status.Unknown,reason);}
        // Null is allowed by the VRAW Text() representation. Required strings
        // are checked separately, before this byte-count check.
        static bool TextFits(string value)
        {
            if(value==null)return true;
            if(value.Length>MaxUtf8TextBytes)return false;
            try{return StrictUtf8.GetByteCount(value)<=MaxUtf8TextBytes;}
            catch(EncoderFallbackException){return false;}
        }
        static bool RequiredFits(string value){return !String.IsNullOrWhiteSpace(value)&&TextFits(value);}
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        // `knownInputValues` are explicit prospective ordinary-action inputs,
        // not labels or a claim that future generated fields were enumerated.
        // The caller must identify safety before invoking this helper. This
        // caller-supplied bool is not an authority and must never be used as a
        // production bypass or to decide whether a safety action may execute.
        // Nothing invokes it from a participant action or changes that input.
        internal static Result Inspect(Experiment task,string format,string eventSchema,string planSchema,
            IReadOnlyList<string> knownInputValues,bool safetyAction)
        {
            if(safetyAction)return new Result(Status.SafetyNotGated,"SAFETY_NOT_INSPECTED_OR_BLOCKED");
            if(task==null||knownInputValues==null)return Unknown("ACTUAL_TASK_OR_INPUTS_UNAVAILABLE");
            if(format!="VRAW0003"||eventSchema!=KnownEventSchema||planSchema!=KnownPlanSchema||
                ViveNamedRecordingIntent.EventSchemaVersion!=KnownEventSchema||ViveNamedSimulationPlan.SchemaVersion!=KnownPlanSchema)
                return Unknown("UNSUPPORTED_SCHEMA");
            try
            {
                if(knownInputValues.Count>MaxFields)return Invalid("INPUT_COUNT_EXCEEDS_FIELD_LIMIT");
                var config=task.Configuration;
                if(config==null||config.RunKind!=RunKind.Simulation||config.NamedPlan==null||config.NamedRecordingIntent==null)
                    return Unknown("NAMED_DEVELOPMENT_CONTEXT_UNAVAILABLE");
                var plan=config.NamedPlan;var intent=config.NamedRecordingIntent;
                if(!RequiredFits(config.SessionId)||!RequiredFits(plan.PlanId)||!RequiredFits(intent.IntentId))return Invalid("SESSION_PLAN_OR_INTENT_TEXT_INVALID");
                if(plan.Blocks.Count<1||plan.Blocks.Count>4)return Unknown("BLOCK_COUNT_UNSUPPORTED");
                foreach(var block in plan.Blocks)
                    if(block==null||block.Selection==null||!RequiredFits(block.Selection.ScheduleVersion)||!RequiredFits(block.Selection.GeometryVersion))
                        return Invalid("BLOCK_PROVENANCE_TEXT_INVALID");
                for(int i=0;i<knownInputValues.Count;i++)if(!TextFits(knownInputValues[i]))return Invalid("ORDINARY_INPUT_TEXT_INVALID");
                var events=task.Events;if(events==null||events.Count==0)return Unknown("CURRENT_EVENT_UNAVAILABLE");
                var item=events[events.Count-1];if(item==null||!RequiredFits(item.ClockId)||!RequiredFits(item.SessionId)||!RequiredFits(item.Kind)||
                    !TextFits(item.TrialId)||item.Fields==null||item.Fields.Count>MaxFields)return Invalid("CURRENT_EVENT_FIELDS_INVALID");
                if(item.Sequence!=events.Count-1||!String.Equals(item.SessionId,config.SessionId,StringComparison.Ordinal)||
                    !Object.ReferenceEquals(item.NamedPlan,plan)||!Object.ReferenceEquals(item.NamedRecordingIntent,intent))
                    return Unknown("CURRENT_CONTEXT_CHANGED_OR_UNAVAILABLE");
                string actualEventSchema,actualPlanSchema;
                if(!item.Fields.TryGetValue("named_schema",out actualEventSchema)||!item.Fields.TryGetValue("named_plan_schema",out actualPlanSchema)||
                    actualEventSchema!=eventSchema||actualPlanSchema!=planSchema)return Unknown("CURRENT_EVENT_SCHEMA_UNAVAILABLE");
                foreach(var pair in item.Fields)if(!RequiredFits(pair.Key)||!TextFits(pair.Value))return Invalid("CURRENT_EVENT_FIELDS_INVALID");
                var trial=task.CurrentTrial;
                if(trial!=null&&(!RequiredFits(trial.TrialId)||!RequiredFits(trial.ListId)))return Invalid("CURRENT_TRIAL_TEXT_INVALID");
                // The present API exposes no exact future cursor delta, durable
                // batch reservation, all retained inputs or reentrant outcomes.
                // Field-level fit alone cannot prove the 1 MiB payload limit or
                // guarantee any action can safely run.
                return Unknown("FUTURE_EVENT_COUNT_AND_PAYLOAD_NOT_PROVEN");
            }
            catch(Exception e) when(!Fatal(e)){return Unknown("SNAPSHOT_OR_ENCODING_UNAVAILABLE");}
        }
    }
}
