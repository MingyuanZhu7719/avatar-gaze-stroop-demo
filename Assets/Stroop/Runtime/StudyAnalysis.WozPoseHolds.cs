#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed class StudyWozPoseHold
    {
        public int Block,Segment;
        public string Trial,Condition,ReceiptOrigin,ClockId,PoseKey,EndObservation="OPEN_NO_FOLLOWING_OBSERVATION";
        public double OriginalReceiptTime,FirstObservedTime,LastObservedTime,FirstReceiptAge,LastReceiptAge,Threshold,Cap;
        public double? FollowingObservedTime;
        public long ObservationCount;
    }
    public sealed partial class StudyAnalysis
    {
        public readonly List<StudyWozPoseHold> WozPoseHolds=new List<StudyWozPoseHold>();
        StudyWozPoseHold openWozHold;
        static double WozBits(string value)
        {return BitConverter.Int64BitsToDouble(unchecked((long)UInt64.Parse(value,NumberStyles.AllowHexSpecifier,Inv)));}
        void ObserveWozDisplay(string kind,Dictionary<string,string> p,int block,int segment,string trial,double? eventTime)
        {
            if(kind!=ViveLegacyWozRecordingSchema.EventKind)
            {
                if(openWozHold!=null&&(block!=openWozHold.Block||segment!=openWozHold.Segment||
                    (trial!=null&&trial!=openWozHold.Trial)||kind=="session_ended"||kind=="block_task_ended"))
                    CloseWozHold(null,kind=="session_ended"?"OPEN_SESSION_ENDED_WITHOUT_CLEAR":"OPEN_TASK_CONTEXT_CHANGED_WITHOUT_CLEAR");
                return;
            }
            if(Get(p,"woz_schema")!=ViveLegacyWozRecordingSchema.SchemaVersion)return;
            string state=Get(p,"woz_pose_hold_state");double observed=WozBits(p["woz_display_observed_participant_time"]);
            if(state!="HELD")
            {CloseWozHold(observed,state=="FRESH"?"FRESH_POSE_OBSERVED":"DISPLAY_CLEARED_OBSERVED");return;}
            string key=Get(p,"woz_epoch")+"/"+Get(p,"woz_source_binding")+"/"+Get(p,"woz_task_revision")+"/"+
                Get(p,"woz_remote_sequence")+"/"+Get(p,"woz_receipt_participant_time");
            if(openWozHold!=null&&(openWozHold.PoseKey!=key||openWozHold.Block!=block||openWozHold.Trial!=trial))
                CloseWozHold(observed,"DIFFERENT_HELD_POSE_OBSERVED");
            if(openWozHold!=null&&observed<openWozHold.LastObservedTime)
                CloseWozHold(null,"NONMONOTONIC_OBSERVATION");
            double age=WozBits(p["woz_pose_receipt_age_seconds"]);
            if(openWozHold==null)
            {
                if(WozPoseHolds.Count>=100000)throw new System.IO.InvalidDataException("Too many WoZ hold observations.");
                openWozHold=new StudyWozPoseHold {Block=block,Segment=segment,Trial=trial,Condition=Condition(block),PoseKey=key,
                    ReceiptOrigin=p["woz_receipt_origin"],ClockId=p["woz_participant_clock_id"],OriginalReceiptTime=WozBits(p["woz_receipt_participant_time"]),
                    FirstObservedTime=observed,FirstReceiptAge=age,Threshold=WozBits(p["woz_pose_hold_threshold_seconds"]),Cap=WozBits(p["woz_pose_receipt_budget_seconds"])};
                WozPoseHolds.Add(openWozHold);
            }
            openWozHold.LastObservedTime=observed;openWozHold.LastReceiptAge=age;openWozHold.ObservationCount++;
        }
        void CloseWozHold(double? observed,string reason)
        {
            if(openWozHold==null)return;
            if(observed.HasValue&&observed.Value>=openWozHold.LastObservedTime)openWozHold.FollowingObservedTime=observed;
            openWozHold.EndObservation=reason;openWozHold=null;
        }
    }
}
#endif
