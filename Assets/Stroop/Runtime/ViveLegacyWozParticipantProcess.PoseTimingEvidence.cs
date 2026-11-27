using System;
using System.Collections.Generic;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveLegacyWozParticipantProcess
    {
        // The caller is the registered recording scene. A clear observation
        // describes its previously applied immutable pose, never a new sample.
        internal Dictionary<string,string> CapturePoseDisplayFields(Evidence value,bool cleared=false,bool onset=false)
        {
            if(value==null||value.Pose==null)return null;
            double observed,age;bool held;Evidence current;
            if(cleared)
            {observed=transport.Now;age=observed-value.OriginalReceipt.OriginalReceiptSeconds;held=false;if(age<0)return null;}
            else if(!TryGetDisplayPoseTiming(out current,out observed,out age,out held)||!Object.ReferenceEquals(current,value))return null;
            var fields=NetworkEvidenceFields(cleared?"PoseDisplayCleared":onset?"PoseUsed":"PoseDisplay",value.OriginalReceipt,value,"NOT_APPLICABLE");
            if(fields==null)return null;
            fields["woz_schema"]=ViveLegacyWozRecordingSchema.SchemaVersion;
            fields["woz_display_observed_participant_time"]=ViveLegacyWozRecordingSchema.EncodeTime(observed);
            fields["woz_pose_receipt_age_seconds"]=ViveLegacyWozRecordingSchema.EncodeTime(age);
            fields["woz_pose_hold_threshold_seconds"]=ViveLegacyWozRecordingSchema.EncodeTime(PoseHoldThresholdSeconds);
            fields["woz_pose_receipt_budget_seconds"]=ViveLegacyWozRecordingSchema.EncodeTime(LocalReceiptBudgetSeconds);
            fields["woz_pose_hold_state"]=cleared?"CLEARED":held?"HELD":"FRESH";
            fields["woz_display_timing_basis"]="OWNED_SCENE_SOFTWARE_OBSERVATION_NOT_OPTICAL_OR_CONTINUOUS";
            return fields;
        }
    }
}
