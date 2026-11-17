#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarStroop.Runtime
{
    public sealed partial class StudyAnalysis
    {
        const string FormalEyePolicy="formal-session-v1";
        public string EyeRecordingPolicy {get;private set;}
        public double? FormalEyeWindowOpenTime {get;private set;}
        public int FormalEyeWindowOpenEvents {get;private set;}
        bool eyePolicyConflict;
        public string TrainingEyeExpectation
        {get{return eyePolicyConflict?"CONFLICTING_POLICY":EyeRecordingPolicy==FormalEyePolicy?"NOT_REQUESTED_TRAINING":
            EyeRecordingPolicy==null?"LEGACY_OR_UNDECLARED":"UNKNOWN_POLICY";}}
        public bool UnexpectedPreformalEyeRows {get;private set;}

        void ReadEyeRecordingPolicy(string kind,IDictionary<string,string> fields,double? time)
        {
            string policy=Get(fields,"eye_recording_policy");
            if(policy==null)return;
            if(EyeRecordingPolicy!=null&&EyeRecordingPolicy!=policy)
            {eyePolicyConflict=true;Warn("Conflicting eye_recording_policy declarations; no training eye-data exemption is inferred.");}
            else EyeRecordingPolicy=policy;
            if(kind=="formal_eye_recording_window_open"&&policy==FormalEyePolicy)
            {
                FormalEyeWindowOpenEvents++;
                if(FormalEyeWindowOpenEvents==1)FormalEyeWindowOpenTime=time;
                if(!time.HasValue||time<0||FormalEyeWindowOpenEvents>1)
                    Warn("Invalid or repeated formal eye recording window declaration; it does not establish actual sample coverage.");
            }
        }
        static void ApplyTrialEyePolicy(StudyTrial trial,IDictionary<string,string> fields)
        {
            string policy=Get(fields,"eye_recording_policy");
            if(policy==null)return;
            if(trial.EyeRecordingPolicy!=null&&trial.EyeRecordingPolicy!=policy)trial.EyeRecordingPolicy="CONFLICTING_POLICY";
            else trial.EyeRecordingPolicy=policy;
        }
        void FinishEyeRecordingPolicy()
        {
            foreach(var trial in Trials)
            {
                string policy=trial.EyeRecordingPolicy;
                // Never infer this policy from tutorial-exam-v1: earlier builds
                // used the same teaching protocol while recording training eyes.
                trial.EyeRecordingExpectation=eyePolicyConflict||policy=="CONFLICTING_POLICY"?"CONFLICTING_POLICY":
                    policy==null?"LEGACY_OR_UNDECLARED":policy!=FormalEyePolicy?"UNKNOWN_POLICY":
                    trial.Scope=="Tutorial"||trial.Scope=="PracticeExam"||trial.Scope=="Practice"?"NOT_REQUESTED_TRAINING":
                    trial.Scope=="Formal"?"EXPECTED_FORMAL":"UNKNOWN_SCOPE";
            }
            foreach(var group in eye.Values)
            {
                if(eyePolicyConflict){group.RecordingExpectation="CONFLICTING_POLICY";continue;}
                if(EyeRecordingPolicy==null)continue;
                if(EyeRecordingPolicy!=FormalEyePolicy){group.RecordingExpectation="UNKNOWN_POLICY";continue;}
                // Native callbacks and owner quality markers do not carry a
                // usable task alignment. Policy is a declaration, not a sample
                // timestamp mapping or proof that the gate worked.
                if(group.Phase=="NativeAcquisitionUnaligned"||group.Phase=="OwnerObservationUnaligned")
                {group.RecordingExpectation="EXPECTED_FORMAL_UNALIGNED";continue;}
                bool training=group.Phase.StartsWith("Tutorial",StringComparison.Ordinal)||group.Phase.StartsWith("Practice",StringComparison.Ordinal);
                bool preformal=training||group.Block<0;
                if(preformal&&group.Observations>0)
                {group.RecordingExpectation=training?"UNEXPECTED_TRAINING_ROWS":"UNEXPECTED_PREFORMAL_ROWS";UnexpectedPreformalEyeRows=true;}
                else group.RecordingExpectation="EXPECTED_FORMAL";
            }
            if(UnexpectedPreformalEyeRows)
                Warn("Eye observation rows occur before the formal session despite formal-session-v1. They are preserved and flagged, not dropped or treated as expected training data.");
            if(EyeRecordingPolicy==FormalEyePolicy&&Trials.Any(x=>x.Scope=="Formal")&&FormalEyeWindowOpenEvents==0)
                Warn("Formal trials have no formal_eye_recording_window_open declaration; expected eye recording does not establish when acquisition was enabled.");
        }
    }
}
#endif
