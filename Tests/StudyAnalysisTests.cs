#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class StudyAnalysisTests
    {
        static Dictionary<string,string> Fields(params string[] pairs)
        {var result=new Dictionary<string,string>(StringComparer.Ordinal);for(int i=0;i<pairs.Length;i+=2)result.Add(pairs[i],pairs[i+1]);return result;}
        static StudyDataRecord Record(string kind,IDictionary<string,string> fields)
        {return new StudyDataRecord{Kind=kind,Fields=fields};}
        static StudyAnalysis Analysis()
        {
            var result=new StudyAnalysis();result.Accept(Record("manifest",Fields("session_id","synthetic","design_variant","LEGACY_ABCD","block_count","4",
                "block_0_condition","1","block_1_condition","2","block_2_condition","0","block_3_condition","3")));return result;
        }
        static void Event(StudyAnalysis analysis,string kind,string trial,int block,double time,params string[] payload)
        {
            var fields=Fields("session_id","synthetic","clock_id","synthetic-clock","event_kind",kind,"trial_id",trial,
                "block",block.ToString(CultureInfo.InvariantCulture),"segment","0","event_local_time",time.ToString("R",CultureInfo.InvariantCulture),"field_count",(payload.Length/2).ToString(CultureInfo.InvariantCulture));
            for(int i=0;i<payload.Length;i+=2){fields.Add("field_"+(i/2)+"_key",payload[i]);fields.Add("field_"+(i/2)+"_value",payload[i+1]);}
            analysis.Accept(Record("task",fields));
        }
        static void Trial(StudyAnalysis analysis,string id,string rule,string response,string correct,double rt=.5,double eventDelay=.02,bool declareOnset=true)
        {
            Event(analysis,"trial_planned",id,0,1,"legacy_phase","MainTask","response_rule",rule,"planned_word","Red","planned_ink","Green","list","list-0");
            var presentation=new List<string>{"legacy_phase","MainTask","response_rule",rule,"word","Red","ink","Green"};
            if(declareOnset)presentation.AddRange(new[]{"task_onset_seconds","2","task_onset_basis","task_clock_after_caller_declared_unity_proxy","task_onset_clock_id","synthetic-clock"});
            Event(analysis,"stimulus_presented",id,0,2,presentation.ToArray());
            Event(analysis,"first_response",id,0,2+rt+eventDelay,"legacy_phase","MainTask","response_rule",rule,"response",response,
                "correct",correct,"onset","2","rt_seconds",rt.ToString("R",CultureInfo.InvariantCulture));
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("study analysis scores ink and word rules separately and tolerates event logging latency",()=>{
                var analysis=Analysis();Trial(analysis,"ink","InkColor","Green","True");Trial(analysis,"word","WordMeaning","Red","True");
                Trial(analysis,"error","WordMeaning","Green","False");analysis.Finish();
                Check.Equal(3,analysis.Trials.Count);Check.True(analysis.Trials.All(x=>x.AccuracyIncluded));
                Check.Equal(2,analysis.Trials.Count(x=>x.RtIncluded));Check.Equal("Green",analysis.Trials[0].Expected);Check.Equal("Red",analysis.Trials[1].Expected);
                Check.Near(2.5,analysis.Trials[0].ResponseTime.Value);Check.Near(2.52,analysis.Trials[0].ResponseEventTime.Value);
                var word=analysis.Summaries().Single(x=>x.Rule=="WordMeaning");Check.Equal(2,word.AccuracyN);Check.Equal(1,word.Correct);Check.Near(.5,word.Accuracy.Value);Check.Equal(1,word.RtN);
                Check.True(analysis.Trials.All(x=>!x.Flags.Contains("rt_timestamp_mismatch")));
            });
            suite.Run("study analysis keeps missing interrupted conflicting and RT-invalid trials explicitly flagged",()=>{
                var analysis=Analysis();Trial(analysis,"wrong-scoring","WordMeaning","Red","False");Trial(analysis,"zero-rt","InkColor","Green","True",0);
                Trial(analysis,"input-after-log","InkColor","Green","True",.5,-.02);
                Event(analysis,"trial_planned","interrupted",0,1,"legacy_phase","MainTask","planned_word","Red","planned_ink","Green");
                Event(analysis,"trial_interrupted","interrupted",0,2,"legacy_phase","MainTask","reason","HeadsetRemoved");
                Event(analysis,"first_response","unpresented",0,2,"legacy_phase","MainTask","response","Green","correct","True","onset","1","rt_seconds","1");
                analysis.Finish();Check.Equal(5,analysis.Trials.Count);Check.Equal(0,analysis.Trials.Count(x=>x.RtIncluded));
                Check.True(analysis.Trials.Single(x=>x.Id=="wrong-scoring").Flags.Contains("correctness_mismatch"));
                Check.True(analysis.Trials.Single(x=>x.Id=="zero-rt").AccuracyIncluded);
                Check.True(analysis.Trials.Single(x=>x.Id=="input-after-log").Flags.Contains("response_time_after_event"));
                Check.True(analysis.Trials.Single(x=>x.Id=="interrupted").Flags.Contains("interrupted"));
                Check.True(analysis.Trials.Single(x=>x.Id=="unpresented").Flags.Contains("not_presented"));
            });
            suite.Run("study analysis labels response-declared legacy onset without inventing a presentation timestamp",()=>{
                var analysis=Analysis();Trial(analysis,"legacy-onset","InkColor","Green","True",.5,.02,false);analysis.Finish();
                var trial=analysis.Trials.Single();Check.Equal("response_declared_task_onset",trial.OnsetBasis);Check.Equal("synthetic-clock",trial.TaskClockId);
                Check.Near(2,trial.Onset.Value);Check.True(trial.RtIncluded);
            });
            suite.Run("study acquisition cadence stays unaligned and does not count rendered observations as native samples",()=>{
                var analysis=Analysis();Event(analysis,"phase_changed",null,2,1,"legacy_phase","MainTask","to","MainTask");
                for(int i=0;i<3;i++)analysis.Accept(Record("eye_acquisition",Fields("native_frame",(10+i).ToString(),"native_timestamp",(100+i).ToString(),
                    "callback_observed_present","true","callback_observed",(1+i*.01).ToString("R",CultureInfo.InvariantCulture),
                    "combined_present","true","combined_origin_valid","true","combined_direction_valid","true","runtime","1","calibration","2")));
                analysis.Accept(Record("observation",Fields("ingest_block","2","ingest_segment","0","ingest_phase","8","native_frame","12","native_timestamp","102")));
                analysis.Finish();var native=analysis.EyeGroups.Single(x=>x.AcquisitionRows>0);Check.Equal(-1,native.Block);Check.Equal("NativeAcquisitionUnaligned",native.Phase);
                Check.Equal(3L,native.NativeChanges);Check.Near(100,native.ObservedHz.Value);Check.Equal(3L,native.Calibration["ReportedReady"]);Check.Equal(3L,native.Runtime["Working"]);
                Check.Equal(1L,analysis.EyeGroups.Single(x=>x.Block==2).Observations);
            });
            suite.Run("study AOI accepts original extension v1 and preserves social not-applicable and invalid observations",()=>{
                var analysis=Analysis();
                foreach(string category in new[]{"task","eyes","remaining_face","other","invalid"})
                    analysis.Accept(Record("observation",Fields("ingest_block","0","ingest_segment","0","ingest_phase","8","aoi_extension_version","1",
                        "aoi_active_task","true","aoi_origin_valid","true","aoi_direction_valid","true","aoi_avatar_applicable","true","aoi_category",category,"aoi_reason","valid_surface_proxy")));
                analysis.Accept(Record("observation",Fields("ingest_block","3","ingest_segment","0","ingest_phase","8","aoi_present","true",
                    "aoi_active_task","true","aoi_origin_valid","true","aoi_direction_valid","true","aoi_avatar_applicable","false","aoi_category","eyes","aoi_reason","valid_surface_proxy")));
                analysis.Finish();var avatar=analysis.EyeGroups.Single(x=>x.Block==0);Check.Equal(5L,avatar.Observations);Check.Equal(4L,avatar.AoiEligible);Check.Equal(1L,avatar.AoiMissing);
                Check.Equal(1L,avatar.Aoi["Task"]);Check.Equal(1L,avatar.Aoi["Eyes"]);Check.Equal(1L,avatar.Aoi["RemainingFace"]);
                Check.Equal(0L,analysis.EyeGroups.Single(x=>x.Block==3).AoiEligible);
            });
            suite.Run("study ratings flag both duplicate responses and unknown questionnaire items",()=>{
                var analysis=Analysis();foreach(string item in new[]{"evaluated","evaluated","invented"})Event(analysis,"rating",null,0,3,"item",item,"value","4","language","en","version","custom-five-items-2026-10-26");
                analysis.Finish();Check.Equal(3,analysis.Ratings.Count);Check.True(analysis.Ratings.Take(2).All(x=>x.Flags.Contains("duplicate_item")));
                Check.True(analysis.Ratings[2].Flags.Contains("unknown_item"));
            });
        }
    }
}
#endif
