#if !UNITY_5_3_OR_NEWER
using System.Collections.Generic;
using System.Linq;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class StudyAnalysisEyePolicyTests
    {
        internal const string Policy="formal-session-v1";
        internal static void Open(StudyAnalysis a)
        {StudyAnalysisTests.Event(a,"formal_eye_recording_window_open",null,0,3,"legacy_phase","Countdown","eye_recording_policy",Policy);}
        static void Eye(StudyAnalysis a,string phase,int block)
        {a.Accept(new StudyDataRecord{Kind="observation",Fields=new Dictionary<string,string>{{"ingest_phase",phase},{"ingest_block",block.ToString()},{"ingest_segment","0"},{"aoi_present","false"}}});}
        public static void Register(TestSuite suite)
        {
            suite.Run("study formal eye policy labels training not requested without manufacturing missing eye rows",()=>{
                var a=StudyAnalysisTests.Analysis();
                StudyAnalysisTrainingTests.Trial(a,"tutorial","tutorial",0,1,eyePolicy:Policy);
                StudyAnalysisTrainingTests.Trial(a,"exam","practice_exam",1,1,eyePolicy:Policy);
                Open(a);StudyAnalysisTrainingTests.Trial(a,"formal","formal",1,1,eyePolicy:Policy,onset:5);a.Finish();
                Check.Equal("NOT_REQUESTED_TRAINING",a.TrainingEyeExpectation);
                Check.True(a.Trials.Where(x=>x.Scope!="Formal").All(x=>x.EyeRecordingExpectation=="NOT_REQUESTED_TRAINING"));
                Check.Equal("EXPECTED_FORMAL",a.Trials.Single(x=>x.Scope=="Formal").EyeRecordingExpectation);
                Check.True(a.Trials.All(x=>x.AccuracyIncluded));Check.Equal(0,a.EyeGroups.Count());Check.True(!a.UnexpectedPreformalEyeRows);
                Check.Equal(1,a.FormalEyeWindowOpenEvents);Check.Near(3,a.FormalEyeWindowOpenTime.Value);
            });
            suite.Run("study older teaching logs do not inherit new formal-only eye policy",()=>{
                var a=StudyAnalysisTests.Analysis();StudyAnalysisTrainingTests.Trial(a,"tutorial","tutorial",0,1);Eye(a,"Tutorial",-1);a.Finish();
                Check.Equal("LEGACY_OR_UNDECLARED",a.TrainingEyeExpectation);Check.Equal("LEGACY_OR_UNDECLARED",a.Trials.Single().EyeRecordingExpectation);
                Check.Equal("LEGACY_OR_UNDECLARED",a.EyeGroups.Single().RecordingExpectation);Check.True(!a.UnexpectedPreformalEyeRows);
            });
            suite.Run("study formal eye policy retains and flags unexpected training observations",()=>{
                var a=StudyAnalysisTests.Analysis();StudyAnalysisTrainingTests.Trial(a,"tutorial","tutorial",0,1,eyePolicy:Policy);
                Eye(a,"Tutorial",-1);Eye(a,"Practice",-1);Eye(a,"Paused",-1);Open(a);Eye(a,"MainTask",0);a.Finish();
                Check.True(a.UnexpectedPreformalEyeRows);Check.Equal(4L,a.EyeGroups.Sum(x=>x.Observations));
                Check.Equal("UNEXPECTED_TRAINING_ROWS",a.EyeGroups.Single(x=>x.Phase=="Tutorial").RecordingExpectation);
                Check.Equal("UNEXPECTED_TRAINING_ROWS",a.EyeGroups.Single(x=>x.Phase=="Practice").RecordingExpectation);
                Check.Equal("UNEXPECTED_PREFORMAL_ROWS",a.EyeGroups.Single(x=>x.Phase=="Paused").RecordingExpectation);
                Check.Equal("EXPECTED_FORMAL",a.EyeGroups.Single(x=>x.Phase=="MainTask").RecordingExpectation);
                Check.True(a.Warnings.Any(x=>x.Contains("preserved and flagged")));Check.True(a.Trials.Single().AccuracyIncluded);
            });
            suite.Run("study conflicting and unknown eye policy cannot establish a training exemption",()=>{
                var a=StudyAnalysisTests.Analysis();StudyAnalysisTrainingTests.Trial(a,"unknown","tutorial",0,1,eyePolicy:"future-policy");a.Finish();
                Check.Equal("UNKNOWN_POLICY",a.TrainingEyeExpectation);Check.Equal("UNKNOWN_POLICY",a.Trials.Single().EyeRecordingExpectation);
                a=StudyAnalysisTests.Analysis();StudyAnalysisTrainingTests.Trial(a,"known","tutorial",0,1,eyePolicy:Policy);
                StudyAnalysisTests.Event(a,"diagnostic",null,-1,4,"eye_recording_policy","future-policy");a.Finish();
                Check.Equal("CONFLICTING_POLICY",a.TrainingEyeExpectation);Check.Equal("CONFLICTING_POLICY",a.Trials.Single().EyeRecordingExpectation);
            });
            suite.Run("study formal eye boundary absence and duplicates are declarations not invented coverage",()=>{
                var a=StudyAnalysisTests.Analysis();StudyAnalysisTrainingTests.Trial(a,"formal","formal",1,1,eyePolicy:Policy);a.Finish();
                Check.True(!a.FormalEyeWindowOpenTime.HasValue&&a.Warnings.Any(x=>x.Contains("no formal_eye_recording_window_open")));
                a=StudyAnalysisTests.Analysis();Open(a);Open(a);a.Finish();Check.Equal(2,a.FormalEyeWindowOpenEvents);
                Check.True(a.Warnings.Any(x=>x.Contains("repeated formal eye")));Check.Equal(0,a.EyeGroups.Count());
            });
        }
    }
}
#endif
