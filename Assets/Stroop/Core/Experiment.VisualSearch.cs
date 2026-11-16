using System;

namespace AvatarStroop.Core
{
    public sealed partial class Experiment
    {
        Stimulus[] PracticePlan(int attempt)
        {return Configuration.TaskKind==TaskKind.VisualSearch?VisualSearchPlans.Practice(Configuration.Seed,attempt):TrialPlans.Practice(Configuration.Seed,attempt);}
        bool VisualSearchPending(string trialId)
        {return Configuration.TaskKind==TaskKind.VisualSearch&&(Phase==Phase.Practice||Phase==Phase.MainTask)&&
            current!=null&&current.TrialId==trialId&&current.Onset.HasValue&&current.Outcome==TrialOutcome.Pending;}
        public bool SelectVisualSearchItem(string trialId,string itemId)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null)return SelectVisualSearchItemCore(trialId,itemId);
            BeginMutation("SelectVisualSearchItem");bool success=false;
            try{bool result=SelectVisualSearchItemCore(trialId,itemId);success=true;return result;}finally{EndMutation(success);}
        }
        bool SelectVisualSearchItemCore(string trialId,string itemId)
        {
            if(!VisualSearchPending(trialId))return false;
            double now=Time();if(ExpireVisualSearchCore(now))return false;
            if(current.VisualSearch.FindItem(itemId)==null)return false;
            current.SetResponseAt(trialAuthority,now);TraceMutation();current.SetSelectedItem(trialAuthority,itemId);TraceMutation();
            current.SetCorrect(trialAuthority,itemId==current.VisualSearch.TargetId);TraceMutation();current.SetResponded(trialAuthority);TraceMutation();
            Log("first_response","selected_item",itemId,"correct",current.Correct.Value.ToString(),
                "onset",Number(current.Onset.Value),"rt_seconds",Number(current.ReactionSeconds.Value),"rt_meaning","search_and_controller_selection");
            if(Phase==Phase.Practice)
            {if(current.Correct.Value){practiceCorrect++;TraceMutation();AdvancePractice();}else Move(Phase.PracticeCorrection);}
            else AdvanceMain();
            return true;
        }
        // Hover is a controller-ray observation, never evidence of discovery or
        // gaze fixation. The caller reports transitions, not one event/frame.
        public bool RecordVisualSearchPointer(string trialId,string itemId,string action)
        {
            if(action!="hover"&&action!="click_miss")throw new ArgumentException("Unknown search pointer action");
            if(mutationJournal==null&&incrementalMutationJournal==null)return RecordVisualSearchPointerCore(trialId,itemId,action);
            BeginMutation("RecordVisualSearchPointer");bool success=false;
            try{bool result=RecordVisualSearchPointerCore(trialId,itemId,action);success=true;return result;}finally{EndMutation(success);}
        }
        bool RecordVisualSearchPointerCore(string trialId,string itemId,string action)
        {
            if(!VisualSearchPending(trialId)||ExpireVisualSearchCore(Time()))return false;
            if(!String.IsNullOrEmpty(itemId)&&current.VisualSearch.FindItem(itemId)==null)return false;
            if(action=="click_miss"&&!String.IsNullOrEmpty(itemId))return false;
            Log("visual_search_pointer","action",action,"item_id",itemId??String.Empty,"meaning","controller_ray_not_eye_or_discovery");
            return true;
        }
        // The caller latches one record per trial after the complete array has
        // rendered. These are camera-callback observations, not photon times.
        public bool RecordVisualSearchCueEvidence(string trialId,double unityFirstRender,double elapsed)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null)return RecordVisualSearchCueEvidenceCore(trialId,unityFirstRender,elapsed);
            BeginMutation("RecordVisualSearchCueEvidence");bool success=false;
            try{bool result=RecordVisualSearchCueEvidenceCore(trialId,unityFirstRender,elapsed);success=true;return result;}finally{EndMutation(success);}
        }
        bool RecordVisualSearchCueEvidenceCore(string trialId,double unityFirstRender,double elapsed)
        {
            if(!VisualSearchPending(trialId)||Double.IsNaN(unityFirstRender)||Double.IsInfinity(unityFirstRender)||unityFirstRender<0||
                Double.IsNaN(elapsed)||Double.IsInfinity(elapsed)||elapsed<VisualSearchTrial.TargetPreviewSeconds)return false;
            Log("visual_search_cue","cue_first_render_unity_seconds",Number(unityFirstRender),
                "cue_rendered_elapsed_seconds",Number(elapsed),"basis","software_camera_callbacks_not_photon");
            return true;
        }
        bool ExpireVisualSearchCore(double now)
        {
            if(current==null||!VisualSearchPending(current.TrialId)||now<current.Onset.Value+VisualSearchTrial.TimeoutSeconds)return false;
            current.SetTimedOut(trialAuthority);TraceMutation();
            Log("trial_timed_out","onset",Number(current.Onset.Value),"elapsed_seconds",Number(now-current.Onset.Value),
                "timeout_seconds",Number(VisualSearchTrial.TimeoutSeconds));
            if(Phase==Phase.Practice)Move(Phase.PracticeCorrection);else AdvanceMain();
            return true;
        }
    }
}
