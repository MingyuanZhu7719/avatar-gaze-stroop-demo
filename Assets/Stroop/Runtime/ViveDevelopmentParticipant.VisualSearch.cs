using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveDevelopmentParticipant
    {
        public TaskKind TaskKind {get{return experiment.Configuration.TaskKind;}}
        public void RecordVisualSearchCueEvidence(string trialId,double firstRender,double elapsed)
        {
            if(observation==null){RunNamedRecordingMutation(()=>{RequireReady();experiment.RecordVisualSearchCueEvidence(trialId,firstRender,elapsed);});return;}
            Observed(()=>{RequireReady();experiment.RecordVisualSearchCueEvidence(trialId,firstRender,elapsed);},false);
        }
        public void SelectVisualSearchItem(string trialId,string itemId)
        {
            if(observation==null){RunNamedRecordingMutation(()=>SelectSearchCore(trialId,itemId));return;}
            Observed(()=>SelectSearchCore(trialId,itemId),false);
        }
        void SelectSearchCore(string trialId,string itemId)
        {RequireReady();experiment.SelectVisualSearchItem(trialId,itemId);}
        public void RecordVisualSearchPointer(string trialId,string itemId,string action)
        {
            if(observation==null){RunNamedRecordingMutation(()=>{RequireReady();experiment.RecordVisualSearchPointer(trialId,itemId,action);});return;}
            Observed(()=>{RequireReady();experiment.RecordVisualSearchPointer(trialId,itemId,action);},false);
        }
    }
}
