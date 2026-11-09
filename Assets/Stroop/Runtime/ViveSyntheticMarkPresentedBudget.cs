using System;
using System.Collections.Generic;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Only a frozen-source/schema synthetic upper-bound experiment. This is
    // not an owner-thread fence, reservation, write receipt, or camera permit.
    // A supplied source label does not authenticate the assembly. Manifest,
    // eye/status records, actual peak memory and first-write cost are excluded.
    internal sealed class ViveSyntheticMarkPresentedBudget
    {
        internal const string SourceAssumption="synthetic-mark-presented-source-20261106-v1";
        const int MaxText=32768,MaxFrame=1048616,FixedFrameAndPayload=97;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static readonly string[] Shared={"named_schema","named_plan_id","named_scope","named_phase","named_plan_schema",
            "named_recording_intent_id","named_eye_sample_payload","named_task_recording_intent","named_eye_recording_intent"};
        static readonly string[] Block={"named_block_index","named_block_started","named_option","named_list_index",
            "named_schedule","named_geometry","named_source","named_avatar_aoi"};
        readonly Experiment task;readonly TrialRecord trial;readonly ViveNamedSessionFrameAdmission.Manifest manifest;
        readonly Dictionary<string,string> expected;readonly int cursor;readonly Phase phase;readonly int block,segment;
        internal readonly int TaskFrameUpperBound;
        internal bool AuthorizesAction {get{return false;}}
        internal bool ActualDisplayProven {get{return false;}}
        internal bool FirstManifestIncluded {get{return false;}}
        internal string Durability {get{return "UNKNOWN";}}
        ViveSyntheticMarkPresentedBudget(Experiment t,TrialRecord r,ViveNamedSessionFrameAdmission.Manifest m,
            Dictionary<string,string> fields,int c,int upper)
        {task=t;trial=r;manifest=m;expected=fields;cursor=c;phase=t.Phase;block=t.BlockIndex;segment=t.Segment;TaskFrameUpperBound=upper;}
        static bool Fits(string value,bool required)
        {
            if(value!=null&&value.Length>MaxText)return false;
            if(required&&String.IsNullOrWhiteSpace(value))return false;
            return value==null||Utf8.GetByteCount(value)<=MaxText;
        }
        static int Size(string value){return value==null?0:Utf8.GetByteCount(value);}
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        static bool Field(JournalEvent e,string key,string value)
        {string actual;return e.Fields.TryGetValue(key,out actual)&&String.Equals(actual,value,StringComparison.Ordinal);}
        // `sourceAssumption` is a caller declaration, not a binary hash. An
        // integration must independently freeze and attest the actual source.
        internal static bool TryCapture(Experiment task,ViveNamedSessionFrameAdmission.Manifest manifest,string sourceAssumption,
            string eventSchema,string planSchema,int maximumTaskFrameBytes,out ViveSyntheticMarkPresentedBudget budget)
        {
            budget=null;
            if(task==null||manifest==null||!manifest.Synthetic||sourceAssumption!=SourceAssumption||
                eventSchema!="vive-named-recording-intent-v3"||planSchema!="vive-named-simulation-plan-v2"||
                ViveNamedRecordingIntent.EventSchemaVersion!=eventSchema||ViveNamedSimulationPlan.SchemaVersion!=planSchema||
                maximumTaskFrameBytes<0||maximumTaskFrameBytes>MaxFrame)return false;
            try
            {
                var config=task.Configuration;var trial=task.CurrentTrial;
                if(config==null||config.RunKind!=RunKind.Simulation||config.NamedPlan==null||config.NamedRecordingIntent==null||
                    !Fits(config.SessionId,true)||!config.SessionId.StartsWith("synthetic-",StringComparison.Ordinal)||
                    !Object.ReferenceEquals(manifest.Plan,config.NamedPlan)||!Object.ReferenceEquals(manifest.Intent,config.NamedRecordingIntent)||
                    !String.Equals(manifest.Session,config.SessionId,StringComparison.Ordinal)||manifest.ChoiceTime<0||
                    (task.Phase!=Phase.Practice&&task.Phase!=Phase.MainTask)||trial==null||trial.Onset.HasValue||trial.Outcome!=TrialOutcome.Pending||
                    trial.Stimulus==null||trial.BlockIndex!=task.BlockIndex||trial.Segment!=task.Segment||
                    !Object.ReferenceEquals(trial.NamedPlan,config.NamedPlan)||!String.Equals(trial.SessionId,config.SessionId,StringComparison.Ordinal)||
                    trial.IsPractice!=(task.Phase==Phase.Practice)||task.Events.Count==0)return false;
                int cursor=task.Events.Count-1;var planned=task.Events[cursor];
                if(!Fits(manifest.ClockId,true)||!Fits(manifest.Session,true)||!Fits(trial.TrialId,true)||!Fits(trial.ListId,true)||
                    planned.Fields.Count>128)return false;
                foreach(var pair in planned.Fields)if(!Fits(pair.Key,true)||!Fits(pair.Value,false))return false;
                if(planned.Kind!="trial_planned"||planned.Sequence!=cursor||!String.Equals(planned.TrialId,trial.TrialId,StringComparison.Ordinal)||
                    !String.Equals(planned.ClockId,manifest.ClockId,StringComparison.Ordinal)||planned.BlockIndex!=task.BlockIndex||planned.Segment!=task.Segment||
                    !Object.ReferenceEquals(planned.NamedBlock,trial.NamedBlock)||!Field(planned,"list",trial.ListId)||
                    !Field(planned,"planned_index",trial.PlannedIndex.ToString())||!Field(planned,"planned_stimulus_schema","1")||
                    !Field(planned,"planned_word",trial.Stimulus.Word.ToString())||!Field(planned,"planned_ink",trial.Stimulus.Ink.ToString())||
                    !Field(planned,"named_phase",task.Phase.ToString()))return false;
                ViveRawSessionJournalCodec.ValidateTaskRecord(planned,manifest);
                bool hasBlock=task.Phase==Phase.MainTask;
                if(hasBlock!=(trial.NamedBlock!=null)||planned.Fields.Count!=(hasBlock?23:15))return false;
                var fields=new Dictionary<string,string>();
                foreach(string key in Shared){string value;if(!planned.Fields.TryGetValue(key,out value))return false;fields.Add(key,value);}
                if(hasBlock)foreach(string key in Block){string value;if(!planned.Fields.TryGetValue(key,out value))return false;fields.Add(key,value);}
                fields.Add("word",trial.Stimulus.Word.ToString());fields.Add("ink",trial.Stimulus.Ink.ToString());
                fields.Add("list",trial.ListId);fields.Add("planned_index",trial.PlannedIndex.ToString());
                // The future dynamic active-seconds string is deliberately not
                // predicted; the whole VRAW per-text allowance is reserved.
                long upper=FixedFrameAndPayload+8L*(fields.Count+1)+MaxText;
                if(!Fits(manifest.ClockId,true)||!Fits(config.SessionId,true)||!Fits(trial.TrialId,true))return false;
                upper=checked(upper+Size(manifest.ClockId)+Size(config.SessionId)+Size(trial.TrialId)+Size("stimulus_presented"));
                foreach(var field in fields)
                {if(!Fits(field.Key,true)||!Fits(field.Value,false))return false;upper=checked(upper+Size(field.Key)+Size(field.Value));}
                upper=checked(upper+Size("named_active_seconds"));
                // Check metadata used by a future first manifest, but never
                // include or claim its bytes were reserved or written here.
                foreach(var b in config.NamedPlan.Blocks)
                    if(b==null||b.Selection==null||!Fits(b.Selection.GeometryVersion,true)||!Fits(b.Selection.ScheduleVersion,true))return false;
                if(upper>MaxFrame||upper>maximumTaskFrameBytes)return false;
                budget=new ViveSyntheticMarkPresentedBudget(task,trial,manifest,fields,cursor,checked((int)upper));return true;
            }
            catch(Exception e) when(!Fatal(e)){return false;}
        }
        // After the synthetic action, only the exact source event in the same
        // Experiment is eligible for comparison. This does not attest that no
        // external actor saw or changed anything or authorize future actions.
        internal bool TryVerifySyntheticResult(out int exactTaskFrameBytes)
        {
            exactTaskFrameBytes=0;
            try
            {
                if(task.Phase!=phase||task.BlockIndex!=block||task.Segment!=segment||!Object.ReferenceEquals(task.CurrentTrial,trial)||
                    !trial.Onset.HasValue||Double.IsNaN(trial.Onset.Value)||Double.IsInfinity(trial.Onset.Value)||trial.Onset.Value<0||
                    trial.Outcome!=TrialOutcome.Pending||task.Events.Count!=cursor+2)return false;
                var result=task.Events[cursor+1];
                if(result.Kind!="stimulus_presented"||result.Sequence!=cursor+1||!String.Equals(result.TrialId,trial.TrialId,StringComparison.Ordinal)||
                    result.Time<trial.Onset.Value||result.BlockIndex!=block||result.Segment!=segment||result.Fields.Count!=expected.Count+1||
                    !Object.ReferenceEquals(result.NamedBlock,trial.NamedBlock))return false;
                foreach(var pair in expected)if(!Field(result,pair.Key,pair.Value))return false;
                string active;if(!result.Fields.TryGetValue("named_active_seconds",out active)||!Fits(active,false))return false;
                int measured;if(!ViveRawTaskFrameMeasurement.TryMeasure(1,result,manifest,TaskFrameUpperBound,out measured))return false;
                exactTaskFrameBytes=measured;return true;
            }
            catch(Exception e) when(!Fatal(e)){return false;}
        }
    }
}
