using System;

namespace AvatarStroop.Runtime
{
    internal sealed partial class ViveLegacyWozRecordingBinding
    {
        ViveLegacyWozParticipantProcess.Evidence lastDisplayedPose;
        string lastDisplayState;
        double lastDisplayObservation;
        bool observingDisplay;

        internal void ObserveAppliedDisplay(ViveLegacyWozControlledScene scene,ViveLegacyWozParticipantProcess.Evidence pose)
        {
            if(observingDisplay||CompletedTaskObserved||participant.Completed||pose==null||process==null||!owner.ControlledSceneCurrent(scene))return;
            lastDisplayedPose=pose;
            // The onset reservation records PoseUsed itself. A scene callback
            // must never start a nested recording mutation during that commit.
            if(!Current)return;
            var fields=process.CapturePoseDisplayFields(pose);if(fields==null)return;
            string state=fields["woz_pose_hold_state"];double now=owner.Clock.Now;
            if(lastDisplayState==state&&(state!="HELD"||now-lastDisplayObservation<.05))return;
            observingDisplay=true;
            try{if(Record(process,fields)){lastDisplayState=state;lastDisplayObservation=now;}}
            finally{observingDisplay=false;}
        }
        internal void ObserveDisplayCleared()
        {
            if(observingDisplay||lastDisplayedPose==null||process==null)return;
            var previous=lastDisplayedPose;lastDisplayedPose=null;lastDisplayState=null;
            if(CompletedTaskObserved||participant.Completed||!Current)return; // No records after the task's terminal phase.
            var fields=process.CapturePoseDisplayFields(previous,true);if(fields==null)return;
            observingDisplay=true;try{Record(process,fields);}finally{observingDisplay=false;}
        }
    }
}
