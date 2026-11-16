#if STROOP_XR_PREVIEW
using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        string searchHoverTrial,searchHoverItem,searchCueRecordedTrial;
        readonly ViveVisualSearchTriggerGate searchTriggerGate=new ViveVisualSearchTriggerGate();
        Vector3 searchRayOrigin,searchRayDirection;int searchRayFrame=-1;
        bool SearchTrialInputCurrent(ViveStroopDeviceFrame frame)
        {
            if(participant==null||participant.TaskKind!=TaskKind.VisualSearch||view==null||rig==null||
                !frame.InputReady||!participant.InputReady||PresentationBlocked||!rig.HeadReady||!rig.XrFocused)return false;
            var trial=participant.CurrentTrial;
            if(trial==null||!trial.Stimulus.IsVisualSearch||!trial.Onset.HasValue||trial.Outcome!=TrialOutcome.Pending||
                (participant.Phase!=Phase.Practice&&participant.Phase!=Phase.MainTask))return false;
            return namedRawPump==null||committedRawTrial==trial.TrialId;
        }
        bool SearchRay(ViveStroopDeviceFrame frame,out Vector3 origin,out Vector3 direction)
        {
            origin=direction=Vector3.zero;ViveStroopControllerPose pose;
            return frame.InputReady&&device!=null&&device.TryGetCurrentControllerPose(out pose)&&
                pose.Frame==Time.frameCount&&rig!=null&&rig.TryGetVisualSearchWorldRay(pose,out origin,out direction);
        }
        bool ServiceVisualSearchInput(ViveStroopDeviceFrame frame)
        {
            if(!SearchTrialInputCurrent(frame)){searchHoverTrial=searchHoverItem=null;return false;}
            var trial=participant.CurrentTrial;string id=trial.TrialId;
            if(searchCueRecordedTrial!=id&&view.VisualSearchCueFirstRenderSeconds>=0&&view.VisualSearchCueElapsedSeconds>=VisualSearchTrial.TargetPreviewSeconds)
            {
                if(!Mutate(()=>participant.RecordVisualSearchCueEvidence(id,view.VisualSearchCueFirstRenderSeconds,view.VisualSearchCueElapsedSeconds)))return true;
                searchCueRecordedTrial=id;
            }
            Vector3 origin,direction,hit;int index;string item;
            if(!SearchRay(frame,out origin,out direction))return true;
            bool matched=namedRawPump==null?view.TryHitVisualSearch(origin,direction,out index,out item,out hit):
                view.TryHitAdmittedVisualSearch(origin,direction,id,trial.Stimulus.VisualSearch.LayoutId,committedRawRevision,out index,out item,out hit);
            if(searchHoverTrial!=id||searchHoverItem!=(matched?item:null))
            {
                searchHoverTrial=id;searchHoverItem=matched?item:null;
                if(!Mutate(()=>participant.RecordVisualSearchPointer(id,searchHoverItem,"hover")))return true;
            }
            if(!frame.TriggerDown)return false;
            if(matched)Mutate(()=>participant.SelectVisualSearchItem(id,item));
            else Mutate(()=>participant.RecordVisualSearchPointer(id,null,"click_miss"));
            // A selection can immediately open a correction/questionnaire.
            // This press must not also confirm that newly entered phase.
            return true;
        }
        void RefreshVisualSearchPointer(ViveStroopDeviceFrame frame)
        {
            if(view==null)return;Vector3 origin=Vector3.zero,direction=Vector3.forward;
            bool visible=SearchTrialInputCurrent(frame)&&SearchRay(frame,out origin,out direction);
            searchRayFrame=visible?Time.frameCount:-1;searchRayOrigin=origin;searchRayDirection=direction;
            if(visible)view.SetVisualSearchPointer(origin,direction,true);
            else view.SetVisualSearchPointer(Vector3.zero,Vector3.forward,false);
        }
        void ApplyCachedSearchPointer()
        {if(view!=null)view.SetVisualSearchPointer(searchRayOrigin,searchRayDirection,searchRayFrame==Time.frameCount);}
    }
}
#endif
