using System;

namespace AvatarStroop.Core
{
    public sealed partial class Experiment
    {
        // Local software observations only. Focus is not a gaze-quality,
        // headset-presence or optical presentation measurement. A focus hold
        // interrupts the current trial without ending the recording/session.
        internal void RecordFocusState(bool desktopFocused,bool xrFocused,bool baseline)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null)
            {RecordFocusStateCore(desktopFocused,xrFocused,baseline);return;}
            BeginMutation("RecordFocusState");bool success=false;
            try{RecordFocusStateCore(desktopFocused,xrFocused,baseline);success=true;}
            finally{EndMutation(success);}
        }
        void RecordFocusStateCore(bool desktopFocused,bool xrFocused,bool baseline)
        {
            if(Phase==Phase.Ended||Phase==Phase.NeedsProtocolDecision)
                throw new InvalidOperationException("Focus observations require a current task.");
            string detail="desktop_focused="+desktopFocused+";xr_focused="+xrFocused+
                ";task_input="+(xrFocused?"requires_current_readiness":"blocked")+
                ";sampling=physical_validity_required";
            Log("focus_changed","focus_schema","1","desktop_focused",desktopFocused.ToString(),
                "xr_focused",xrFocused.ToString(),"actor",Actor.Owner.ToString(),"code","focus_state",
                "reason",baseline?"focus_baseline":desktopFocused&&xrFocused?"focus_restored":"focus_lost",
                "detail",detail,"source","owner_window_and_openxr_poll","measurement_quality","NOT_ESTABLISHED");
            if(!xrFocused&&FocusHoldPhase(Phase))PauseCore(Actor.Owner,detail);
        }
        static bool FocusHoldPhase(Phase phase)
        {
            return phase==Phase.Familiarization||phase==Phase.Practice||phase==Phase.PracticeCorrection||
                phase==Phase.Countdown||phase==Phase.MainTask||phase==Phase.Questionnaire||phase==Phase.Rest;
        }
        internal void RecordFocusForwardingSuspension(bool acknowledged,bool cleanupUnknown,bool noPositiveGrant)
        {
            if(Phase==Phase.Ended||Phase==Phase.NeedsProtocolDecision)
                throw new InvalidOperationException("Forwarding suspension requires a current held task.");
            Action record=()=>Log("focus_forwarding_suspended","focus_schema","1","actor",Actor.Owner.ToString(),
                "code","local_positive_acceptance_revoked","reason","focus_hold_no_source_resume",
                "detail","local_positive_acceptance=REVOKED;remote_software_ack="+
                    (acknowledged?"RECEIVED":noPositiveGrant?"NOT_APPLICABLE_NO_POSITIVE_GRANT":"UNKNOWN")+
                    ";remote_cleanup="+(cleanupUnknown?"UNKNOWN":"SOFTWARE_ACK_ONLY")+
                    ";remote_physical_stop=NOT_ESTABLISHED;source_resume=UNAVAILABLE",
                "source","negative_revoke_channel","measurement_quality","NOT_ESTABLISHED");
            if(mutationJournal==null&&incrementalMutationJournal==null){record();return;}
            BeginMutation("RecordFocusForwardingSuspension");bool success=false;
            try{record();success=true;}finally{EndMutation(success);}
        }
    }
}
