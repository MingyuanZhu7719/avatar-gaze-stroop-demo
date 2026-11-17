#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed class StudySearchItem
    {
        public string Session,Trial,Condition,Id,Colour,Pattern,LayoutId,LayoutSeed;
        public string Scope,TrainingProtocol,LearningStage;
        public int? PracticeAttempt,TutorialRound;
        public int Block,Segment,Slot,SetSize;
        public bool IsTarget;
    }
    public sealed class StudySearchEvent
    {
        public string Session,Trial,Condition,Action,Item,Flags,CueBasis;
        public string Scope,TrainingProtocol,LearningStage;
        public int? PracticeAttempt,TutorialRound;
        public int Block,Segment;
        public double? LocalTime,CueFirstRenderUnitySeconds,CueRenderedElapsedSeconds;
    }
    public sealed partial class StudyAnalysis
    {
        readonly Dictionary<string,VisualSearchTrial> searchLayouts=new Dictionary<string,VisualSearchTrial>(StringComparer.Ordinal);
        public readonly List<StudySearchItem> SearchItems=new List<StudySearchItem>();
        public readonly List<StudySearchEvent> SearchEvents=new List<StudySearchEvent>();

        static void SearchText(StudyTrial trial,string incoming,ref string current)
        {
            if(incoming==null)return;
            if(current!=null&&incoming!=current)trial.Flag("search_declaration_conflict");
            else current=incoming;
        }
        void SearchPayload(StudyTrial trial,string kind,IDictionary<string,string> fields,IDictionary<string,string> payload)
        {
            SearchText(trial,Get(payload,"search_schema"),ref trial.SearchSchema);
            SearchText(trial,Get(payload,"target_item"),ref trial.TargetItem);
            SearchText(trial,Get(payload,"target_colour"),ref trial.TargetColour);
            SearchText(trial,Get(payload,"target_pattern"),ref trial.TargetPattern);
            SearchText(trial,Get(payload,"layout_seed"),ref trial.LayoutSeed);
            string size=Get(payload,"set_size");
            if(size!=null)
            {
                int n=Int(payload,"set_size",-1);
                if(n!=12&&n!=24)trial.Flag("invalid_search_set_size");
                if(trial.SetSize.HasValue&&trial.SetSize.Value!=n)trial.Flag("search_declaration_conflict");
                else trial.SetSize=n;
            }
            if(kind=="trial_planned")
            {
                string encoded=Get(payload,"visual_search_layout");
                if(encoded==null){trial.Flag("missing_search_layout");return;}
                VisualSearchTrial layout;
                try{layout=VisualSearchTrial.Parse(encoded);}
                catch(Exception e) when(e is ArgumentException||e is FormatException||e is OverflowException||e is InvalidOperationException)
                {trial.Flag("invalid_search_layout");return;}
                VisualSearchTrial previous;
                if(searchLayouts.TryGetValue(trial.Id,out previous))
                {if(previous.Serialize()!=layout.Serialize())trial.Flag("search_layout_conflict");return;}
                searchLayouts.Add(trial.Id,layout);
                foreach(var item in layout.Items)SearchItems.Add(new StudySearchItem{Session=SessionId,Trial=trial.Id,Block=trial.Block,Segment=trial.Segment,
                    Condition=trial.Condition,Id=item.Id,Slot=item.Slot,Colour=item.Colour.ToString(),Pattern=item.Pattern.ToString(),SetSize=layout.SetSize,
                    IsTarget=item.Id==layout.TargetId,LayoutId=layout.LayoutId,LayoutSeed=layout.LayoutSeed.ToString(CultureInfo.InvariantCulture),
                    Scope=trial.Scope,TrainingProtocol=trial.TrainingProtocol,LearningStage=trial.LearningStage,PracticeAttempt=trial.PracticeAttempt,TutorialRound=trial.TutorialRound});
            }
            if(kind=="first_response")SearchText(trial,Get(payload,"selected_item"),ref trial.SelectedItem);
            if(kind=="trial_timed_out")
            {
                trial.TimeoutCount++;trial.TimedOut=true;
                if(trial.TimeoutCount==1)
                {
                    trial.TimeoutSeconds=Number(payload,"elapsed_seconds");trial.TimeoutEventTime=Number(fields,"event_local_time");
                    double? onset=Number(payload,"onset");
                    if(trial.Onset.HasValue&&onset.HasValue&&Math.Abs(trial.Onset.Value-onset.Value)>0.000001)trial.Flag("onset_declaration_conflict");
                    if(!trial.Onset.HasValue&&onset.HasValue){trial.Onset=onset;trial.OnsetBasis="timeout_declared_task_onset";trial.TaskClockId=Get(fields,"clock_id");}
                    if(onset.HasValue&&trial.TimeoutSeconds.HasValue)trial.TimeoutTime=onset.Value+trial.TimeoutSeconds.Value;
                }
            }
        }
        void SearchPointer(IDictionary<string,string> fields,IDictionary<string,string> payload,int block,int segment)
        {
            if(SearchEvents.Count>=1000000)throw new InvalidDataException("Too many visual search pointer events.");
            string action=Get(payload,"action"),trial=Get(fields,"trial_id");
            string flags=String.IsNullOrEmpty(trial)?"missing_trial_id":"";
            if(action!="hover"&&action!="click_miss"&&action!="click")flags+=(flags.Length>0?";":"")+"unknown_pointer_action";
            double? time=Number(fields,"event_local_time");
            if(!time.HasValue)flags+=(flags.Length>0?";":"")+"missing_event_time";
            SearchEvents.Add(new StudySearchEvent{Session=SessionId,Trial=trial,Block=block,Segment=segment,Condition=Condition(block),Action=action,
                Item=Get(payload,"item_id"),LocalTime=time,Flags=flags,
                Scope=TrialScope(Get(payload,"training_protocol"),Get(payload,"learning_stage"),Get(payload,"legacy_phase")??Get(payload,"named_phase")??currentPhase),
                TrainingProtocol=Get(payload,"training_protocol"),LearningStage=Get(payload,"learning_stage"),PracticeAttempt=OptionalInt(payload,"practice_attempt"),TutorialRound=OptionalInt(payload,"tutorial_round")});
        }
        void SearchCue(IDictionary<string,string> fields,IDictionary<string,string> payload,int block,int segment)
        {
            if(SearchEvents.Count>=1000000)throw new InvalidDataException("Too many visual search events.");
            string trial=Get(fields,"trial_id"),basis=Get(payload,"basis");
            var flags=new List<string>();
            if(String.IsNullOrEmpty(trial))flags.Add("missing_trial_id");
            double? time=Number(fields,"event_local_time"),first=Number(payload,"cue_first_render_unity_seconds"),elapsed=Number(payload,"cue_rendered_elapsed_seconds");
            if(!time.HasValue)flags.Add("missing_event_time");
            if(!first.HasValue||first.Value<0)flags.Add("invalid_cue_first_render_time");
            if(!elapsed.HasValue||elapsed.Value<0)flags.Add("invalid_cue_elapsed_time");
            if(basis!="software_camera_callbacks_not_photon")flags.Add("unknown_cue_timing_basis");
            SearchEvents.Add(new StudySearchEvent{Session=SessionId,Trial=trial,Block=block,Segment=segment,Condition=Condition(block),Action="cue",
                LocalTime=time,CueFirstRenderUnitySeconds=first,CueRenderedElapsedSeconds=elapsed,CueBasis=basis,Flags=String.Join(";",flags),
                Scope=TrialScope(Get(payload,"training_protocol"),Get(payload,"learning_stage"),Get(payload,"legacy_phase")??Get(payload,"named_phase")??currentPhase),
                TrainingProtocol=Get(payload,"training_protocol"),LearningStage=Get(payload,"learning_stage"),PracticeAttempt=OptionalInt(payload,"practice_attempt"),TutorialRound=OptionalInt(payload,"tutorial_round")});
        }
        void FinishSearch(StudyTrial trial)
        {
            trial.Rule="SelectTarget";trial.Expected=trial.TargetItem;
            if(trial.SearchSchema!="visual-search-v1")trial.Flag("unknown_search_schema");
            if(trial.Word!=null||trial.Ink!=null)trial.Flag("search_has_stroop_fields");
            if(!trial.SetSize.HasValue||(trial.SetSize!=12&&trial.SetSize!=24))trial.Flag("invalid_search_set_size");
            VisualSearchTrial layout;
            if(!searchLayouts.TryGetValue(trial.Id,out layout))trial.Flag("missing_search_layout");
            else
            {
                if(trial.SetSize!=layout.SetSize||trial.TargetItem!=layout.TargetId||trial.TargetColour!=layout.TargetColour.ToString()||
                    trial.TargetPattern!=layout.TargetPattern.ToString()||trial.LayoutSeed!=layout.LayoutSeed.ToString(CultureInfo.InvariantCulture))trial.Flag("search_layout_metadata_mismatch");
                if(!trial.TimedOut&&(trial.SelectedItem==null||!layout.Items.Any(x=>x.Id==trial.SelectedItem)))trial.Flag("invalid_search_selection");
                if(trial.SelectedItem!=null&&layout.Items.Any(x=>x.Id==trial.SelectedItem))trial.Correct=trial.SelectedItem==layout.TargetId;
            }
            if(trial.TimedOut)
            {
                trial.Correct=false;
                if(trial.SelectedItem!=null||trial.ResponseCount!=0)trial.Flag("conflicting_trial_outcomes");
                if(!trial.TimeoutSeconds.HasValue||trial.TimeoutSeconds.Value<=0)trial.Flag("invalid_timeout_duration");
            }
        }
    }
}
#endif
