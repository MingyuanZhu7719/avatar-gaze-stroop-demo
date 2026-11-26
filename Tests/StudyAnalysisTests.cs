#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class StudyAnalysisTests
    {
        static Dictionary<string,string> Fields(params string[] pairs)
        {var result=new Dictionary<string,string>(StringComparer.Ordinal);for(int i=0;i<pairs.Length;i+=2)result.Add(pairs[i],pairs[i+1]);return result;}
        static StudyDataRecord Record(string kind,IDictionary<string,string> fields)
        {return new StudyDataRecord{Kind=kind,Fields=fields};}
        internal static StudyAnalysis Analysis()
        {
            var result=new StudyAnalysis();result.Accept(Record("manifest",Fields("session_id","synthetic","design_variant","LEGACY_ABCD","block_count","4",
                "block_0_condition","1","block_1_condition","2","block_2_condition","0","block_3_condition","3")));return result;
        }
        internal static void Event(StudyAnalysis analysis,string kind,string trial,int block,double time,params string[] payload)
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
        internal static VisualSearchTrial SearchTrial(StudyAnalysis analysis,string id,int size,bool correct=true,bool timeout=false,bool corruptMetadata=false)
        {
            var layout=VisualSearchPlans.MainList(71,0).Select(x=>x.VisualSearch).First(x=>x.SetSize==size);
            Event(analysis,"trial_planned",id,0,1,"legacy_phase","MainTask","task_kind","VisualSearch","search_schema",VisualSearchTrial.Schema,
                "set_size",(corruptMetadata?36:size).ToString(CultureInfo.InvariantCulture),"target_item",layout.TargetId,"target_colour",layout.TargetColour.ToString(),
                "target_pattern",layout.TargetPattern.ToString(),"layout_seed",layout.LayoutSeed.ToString(CultureInfo.InvariantCulture),"visual_search_layout",layout.Serialize(),"list","search-test-list");
            Event(analysis,"stimulus_presented",id,0,2,"legacy_phase","MainTask","task_kind","VisualSearch","task_onset_seconds","2",
                "task_onset_basis","task_clock_after_caller_declared_unity_proxy","task_onset_clock_id","synthetic-clock");
            Event(analysis,"visual_search_pointer",id,0,2.1,"legacy_phase","MainTask","task_kind","VisualSearch","action","hover","item_id",layout.TargetId);
            if(timeout)Event(analysis,"trial_timed_out",id,0,32.01,"legacy_phase","MainTask","task_kind","VisualSearch","onset","2","elapsed_seconds","30");
            else Event(analysis,"first_response",id,0,2.52,"legacy_phase","MainTask","task_kind","VisualSearch","selected_item",correct?layout.TargetId:layout.Items.First(x=>x.Id!=layout.TargetId).Id,
                "correct",correct.ToString(),"onset","2","rt_seconds","0.5");
            return layout;
        }
        internal static void SearchCue(StudyAnalysis analysis,string id)
        {Event(analysis,"visual_search_cue",id,0,2.01,"task_kind","VisualSearch","cue_first_render_unity_seconds","10.25","cue_rendered_elapsed_seconds","1.51","basis","software_camera_callbacks_not_photon");}
        public static void Register(TestSuite suite)
        {
            StudyAnalysisTrainingTests.Register(suite);
            StudyAnalysisEyePolicyTests.Register(suite);
            StudyAnalysisEyeAvailabilityTests.Register(suite);
            suite.Run("study visual search cue timing is separate from pointer observations and response timing",()=>{
                var analysis=Analysis();SearchTrial(analysis,"cue",12);SearchCue(analysis,"cue");analysis.Finish();
                var cue=analysis.SearchEvents.Single(x=>x.Action=="cue");Check.Near(10.25,cue.CueFirstRenderUnitySeconds.Value);Check.Near(1.51,cue.CueRenderedElapsedSeconds.Value);
                Check.Equal("software_camera_callbacks_not_photon",cue.CueBasis);Check.Equal("",cue.Flags);Check.True(cue.Item==null);
                Check.Equal(1,analysis.SearchEvents.Count(x=>x.Action=="hover"));Check.Near(2,analysis.Trials.Single().Onset.Value);Check.Near(.5,analysis.Trials.Single().ReactionSeconds.Value);
            });
            suite.Run("study visual search scores object identity and keeps set sizes separate without Stroop fields",()=>{
                var analysis=Analysis();SearchTrial(analysis,"small",12);SearchTrial(analysis,"large",24,false);SearchTrial(analysis,"timeout",12,timeout:true);analysis.Finish();
                Check.Equal(3,analysis.Trials.Count);Check.True(analysis.Trials.All(x=>x.TaskKind=="VisualSearch"&&x.Rule=="SelectTarget"&&x.Word==null&&x.Ink==null&&x.Congruence=="NotApplicable"));
                Check.True(analysis.Trials.All(x=>x.AccuracyIncluded));Check.Equal(1,analysis.Trials.Count(x=>x.RtIncluded));
                var timed=analysis.Trials.Single(x=>x.Id=="timeout");Check.True(timed.TimedOut&&timed.Correct==false&&timed.ReactionSeconds==null&&timed.ResponseTime==null&&timed.ResponseEventTime==null);Check.Equal("TimedOut",timed.Outcome);Check.Near(32,timed.TimeoutTime.Value);
                var summaries=analysis.Summaries().ToArray();Check.Equal(2,summaries.Length);var small=summaries.Single(x=>x.SetSize==12);
                Check.Equal(2,small.AccuracyN);Check.Equal(1,small.TimedOut);Check.Near(.5,small.Accuracy.Value);Check.Equal(48,analysis.SearchItems.Count);Check.Equal(3,analysis.SearchEvents.Count);
            });
            suite.Run("study visual search refuses contradictory layout metadata and unknown selections",()=>{
                var analysis=Analysis();SearchTrial(analysis,"bad-size",12,corruptMetadata:true);
                SearchTrial(analysis,"duplicate-response",24);
                Event(analysis,"first_response","duplicate-response",0,3,"legacy_phase","MainTask","task_kind","VisualSearch","selected_item","item-99","correct","True","onset","2","rt_seconds","1");
                analysis.Finish();Check.True(analysis.Trials.All(x=>!x.AccuracyIncluded&&!x.RtIncluded));
                Check.True(analysis.Trials.Single(x=>x.Id=="bad-size").Flags.Contains("search_layout_metadata_mismatch"));
                Check.True(analysis.Trials.Single(x=>x.Id=="duplicate-response").Flags.Contains("search_declaration_conflict"));
            });
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
