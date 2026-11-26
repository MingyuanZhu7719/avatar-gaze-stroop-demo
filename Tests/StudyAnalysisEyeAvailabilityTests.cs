#if !UNITY_5_3_OR_NEWER
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class StudyAnalysisEyeAvailabilityTests
    {
        internal static void Missing(StudyAnalysis a,long ordinal,double time,int reason,long cursor=12)
        {a.Accept(new StudyDataRecord{Ordinal=ordinal,Kind="eye_observation",Fields=new Dictionary<string,string>{
            {"eye_observation",reason==0?"0":"1"},{"reason",reason.ToString(CultureInfo.InvariantCulture)},
            {"owner_observed_local_seconds",time.ToString("R",CultureInfo.InvariantCulture)},
            {"task_cursor",cursor.ToString(CultureInfo.InvariantCulture)}}});}
        internal static void Sample(StudyAnalysis a,long ordinal,double time)
        {a.Accept(new StudyDataRecord{Ordinal=ordinal,Kind="observation",Fields=new Dictionary<string,string>{
            {"checked_at",time.ToString("R",CultureInfo.InvariantCulture)},{"task_cursor","13"},
            {"combined_direction_valid","false"},{"calibration","1"}}});}
        public static void Register(TestSuite suite)
        {
            suite.Run("eye availability reports initial failure heartbeat fault and returned data without changing behavior",()=>{
                var a=StudyAnalysisTests.Analysis();StudyAnalysisTests.SearchTrial(a,"kept",12);
                Missing(a,1,4,6);Missing(a,2,5,6);Missing(a,3,5.2,0);Sample(a,4,6);Sample(a,5,6.2);Missing(a,6,7,7);a.Finish();
                Check.Equal(3,a.EyeAvailability.Count);var initial=a.EyeAvailability[0];
                Check.Equal("INITIAL_OBSERVATION",initial.Transition);Check.Equal("CLIENT_INITIALIZATION_FAILED",initial.Reason);
                Check.Equal(2L,initial.ObservationRows);Check.Near(4,initial.FirstObservedLocalSeconds.Value);
                Check.Near(5,initial.LastObservedLocalSeconds.Value);Check.Near(6,initial.FollowingObservedLocalSeconds.Value);
                var returned=a.EyeAvailability[1];Check.Equal("DATA_RESUMED",returned.Transition);
                Check.Equal("SAMPLE_RECORDED",returned.State);Check.Equal("RECORDED_SAMPLE_NOT_VALIDITY_OR_COVERAGE",returned.Reason);
                Check.Equal(2L,returned.ObservationRows);Check.Near(7,returned.FollowingObservedLocalSeconds.Value);
                var final=a.EyeAvailability[2];Check.Equal("CLIENT_FAULTED",final.Reason);Check.Equal("MISSING_OBSERVED",final.Transition);
                Check.True(!final.FollowingObservedLocalSeconds.HasValue);Check.True(a.Trials.Single().AccuracyIncluded&&a.Trials.Single().RtIncluded);
            });
            suite.Run("eye availability missing reason changes preserve uncertainty and reject unknown states",()=>{
                var a=StudyAnalysisTests.Analysis();Missing(a,1,4,3);Missing(a,2,4.2,2);Missing(a,3,3,2);
                Check.Equal(3,a.EyeAvailability.Count);Check.Equal("MISSING_REASON_CHANGED",a.EyeAvailability[1].Transition);
                Check.Equal("NONMONOTONIC_OWNER_TIME",a.EyeAvailability[2].Transition);
                Check.True(!a.EyeAvailability[1].FollowingObservedLocalSeconds.HasValue);
                Check.True(a.Warnings.Any(x=>x.Contains("moved backwards")));
                Check.Throws<InvalidDataException>(()=>Missing(a,4,4,8));
            });
            suite.Run("eye availability does not invent coverage from duplicate or absent evidence",()=>{
                var a=StudyAnalysisTests.Analysis();Missing(a,1,4,0);a.Finish();Check.Equal(0,a.EyeAvailability.Count);
                a=StudyAnalysisTests.Analysis();a.Finish();Check.Equal(0,a.EyeAvailability.Count);
            });
        }
    }
}
#endif
