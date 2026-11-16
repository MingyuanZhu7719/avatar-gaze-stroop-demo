#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AvatarStroop.Runtime
{
    // Offline descriptive analysis. Original records are never edited. Trial
    // exclusions are explicit, and absence of eye data is never interpolated.
    public sealed class StudyTrial
    {
        public string Session, Id, Condition, Phase, Rule, RuleProvenance, Word, Ink, Response, Expected, DeclaredExpected, DeclaredCorrect, List, RetryOf;
        public string OnsetBasis, TaskClockId, UnityRenderBasis;
        public int Block, Segment, PlannedCount, PresentedCount, ResponseCount;
        public double? Onset, ResponseTime, ResponseEventTime, ReactionSeconds, UnityRenderSeconds;
        internal double? PlanEventTime, PresentationEventTime;
        public bool Interrupted;
        public readonly List<string> Flags = new List<string>();
        public bool Practice { get { return Phase != null && Phase.StartsWith("Practice",StringComparison.Ordinal); } }
        public bool? Correct { get; internal set; }
        public bool AccuracyIncluded { get; internal set; }
        public bool RtIncluded { get; internal set; }
        public string Congruence { get { return !StudyAnalysis.IsColour(Word)||!StudyAnalysis.IsColour(Ink)?"Unknown":Word==Ink?"Congruent":"Incongruent"; } }
        public void Flag(string value) { if(!Flags.Contains(value))Flags.Add(value); }
    }

    public sealed class StudyRating
    {
        public string Session, Condition, Rule, Item, Value, Language, Version, Flags;
        public int Block;
        public double? LocalTime;
    }

    public sealed class StudyEyeGroup
    {
        public int Block, Segment;
        public string Phase;
        public long AcquisitionRows, NativeChanges, AdjacentDuplicates, NonIncreasingNativeFrames,
            CombinedValid, CombinedInvalid, CombinedUnknown, Observations, AoiEligible, AoiMissing;
        public double? FirstCallback, LastCallback;
        public long CallbackTimes, NonIncreasingCallbackTimes;
        public readonly Dictionary<string,long> Calibration = new Dictionary<string,long>();
        public readonly Dictionary<string,long> Runtime = new Dictionary<string,long>();
        public readonly Dictionary<string,long> Aoi = new Dictionary<string,long>();
        public readonly Dictionary<string,long> ObservationReasons = new Dictionary<string,long>();
        internal string PreviousNative;
        internal long? PreviousFrame;
        internal double? PreviousCallback;
        public double? ObservedHz { get { return CallbackTimes>1 && FirstCallback.HasValue && LastCallback>FirstCallback && NonIncreasingCallbackTimes==0
            ?(CallbackTimes-1)/(LastCallback.Value-FirstCallback.Value):(double?)null; } }
        internal void Acquire(IDictionary<string,string> fields)
        {
            AcquisitionRows++;
            string identity=StudyAnalysis.Get(fields,"native_frame")+"/"+StudyAnalysis.Get(fields,"native_timestamp");
            bool changed=identity!=PreviousNative && identity!="/";
            if(changed)
            {
                NativeChanges++;
                long frame; if(Int64.TryParse(StudyAnalysis.Get(fields,"native_frame"),out frame))
                {if(PreviousFrame.HasValue&&frame<=PreviousFrame.Value)NonIncreasingNativeFrames++;PreviousFrame=frame;}
                double? time=StudyAnalysis.Number(fields,"callback_observed");
                if(StudyAnalysis.Truth(fields,"callback_observed_present")==true && time.HasValue)
                {
                    if(PreviousCallback.HasValue&&time.Value<=PreviousCallback.Value)NonIncreasingCallbackTimes++;
                    if(!FirstCallback.HasValue)FirstCallback=time;LastCallback=time;PreviousCallback=time;CallbackTimes++;
                }
            }
            else if(identity==PreviousNative)AdjacentDuplicates++;
            PreviousNative=identity;
            bool? present=StudyAnalysis.Truth(fields,"combined_present"),origin=StudyAnalysis.Truth(fields,"combined_origin_valid"),direction=StudyAnalysis.Truth(fields,"combined_direction_valid");
            if(present==true&&origin==true&&direction==true)CombinedValid++;
            else if(present==false||origin==false||direction==false)CombinedInvalid++;else CombinedUnknown++;
            Count(Calibration,StudyAnalysis.EnumLabel<ViveEyeCalibrationState>(StudyAnalysis.Get(fields,"calibration")));
            Count(Runtime,StudyAnalysis.EnumLabel<ViveEyeRuntimeState>(StudyAnalysis.Get(fields,"runtime")));
        }
        internal void Observe(IDictionary<string,string> fields)
        {
            Observations++;
            // AOI is an observation-time geometry classification, not a native
            // fixation/dwell estimate. Preserve repeats and declare denominator.
            // The original AOI extension v1 has no aoi_present field; its
            // extension version is the presence marker. Later recording
            // envelopes carry an explicit presence flag.
            bool? present=StudyAnalysis.Truth(fields,"aoi_present");
            if(present==false||(present!=true&&StudyAnalysis.Int(fields,"aoi_extension_version",0)!=1)||StudyAnalysis.Truth(fields,"aoi_active_task")!=true||
               StudyAnalysis.Truth(fields,"aoi_origin_valid")!=true||StudyAnalysis.Truth(fields,"aoi_direction_valid")!=true)
            {AoiMissing++;return;}
            string category=StudyAnalysis.Get(fields,"aoi_category");
            if(category=="task")category="Task";else if(category=="eyes")category="Eyes";
            else if(category=="remaining_face")category="RemainingFace";else if(category=="other")category="Other";
            else {AoiMissing++;return;}
            if(StudyAnalysis.Get(fields,"aoi_reason")!="valid_surface_proxy"){AoiMissing++;return;}
            if(category!="Task"&&category!="Eyes"&&category!="RemainingFace"&&category!="Other") {AoiMissing++;return;}
            if((category=="Eyes"||category=="RemainingFace")&&StudyAnalysis.Truth(fields,"aoi_avatar_applicable")!=true)
            {AoiMissing++;return;}
            AoiEligible++;Count(Aoi,category);
        }
        internal static void Count(Dictionary<string,long> counts,string key)
        {long n;counts.TryGetValue(key,out n);counts[key]=n+1;}
    }

    public sealed class StudyBlockSummary
    {
        public int Block;
        public string Condition, Rule, Scope, Congruence;
        public int Trials, Planned, Presented, Responded, Interrupted, AccuracyN, Correct, RtN;
        public double? Accuracy, MeanRt, MedianRt, SdRt, P95Rt;
    }

    public sealed class StudyAnalysis
    {
        public const string Version="1.0.0";
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        readonly Dictionary<string,StudyTrial> byId=new Dictionary<string,StudyTrial>(StringComparer.Ordinal);
        readonly Dictionary<string,StudyEyeGroup> eye=new Dictionary<string,StudyEyeGroup>(StringComparer.Ordinal);
        readonly Dictionary<int,string> conditions=new Dictionary<int,string>();
        readonly Dictionary<int,string> rules=new Dictionary<int,string>();
        readonly HashSet<string> ratingKeys=new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<int> endedBlocks=new HashSet<int>();
        static readonly HashSet<string> RatingItems=new HashSet<string>(new[]{"focus","distraction","observed","evaluated","negative_evaluation_worry"},StringComparer.Ordinal);
        string currentPhase="Unknown";
        bool finalized;
        public string SessionId {get;private set;}
        public string Design {get;private set;}
        public string Source {get;private set;}
        public string RunKind {get;private set;}
        public string SessionEndReason {get;private set;}
        public int? PlannedBlocks {get;private set;}
        public int TaskEvents {get;private set;}
        public readonly List<StudyTrial> Trials=new List<StudyTrial>();
        public readonly List<StudyRating> Ratings=new List<StudyRating>();
        public readonly List<string> Warnings=new List<string>();
        public readonly Dictionary<string,long> EventCounts=new Dictionary<string,long>();
        public IEnumerable<StudyEyeGroup> EyeGroups {get{return eye.Values.OrderBy(x=>x.Block).ThenBy(x=>x.Segment).ThenBy(x=>x.Phase,StringComparer.Ordinal);}}
        public int EndedBlocks {get{return endedBlocks.Count;}}

        public void Accept(StudyDataRecord record)
        {
            if(finalized)throw new InvalidOperationException("Analysis is already finalized.");
            var f=record.Fields;
            if(record.Kind=="manifest")
            {
                string session=Get(f,"session_id");
                if(SessionId!=null&&SessionId!=session)throw new InvalidDataException("Multiple sessions in one source.");
                SessionId=session;Design=Get(f,"design_variant");Source=Get(f,"source");RunKind=Get(f,"run_kind");
                int blocks;if(Int32.TryParse(Get(f,"block_count"),out blocks))
                {
                    PlannedBlocks=blocks;
                    for(int i=0;i<blocks;i++)
                    {
                        string p="block_"+i.ToString(Inv)+"_";string value=Get(f,p+"condition");int code;
                        if(Int32.TryParse(value,out code)&&code>=0&&code<4)value=((char)('A'+code)).ToString();
                        if(value==null){value=Get(f,p+"option");if(value=="0")value="Automatic";else if(value=="2")value="NoAvatar";}
                        conditions[i]=value??"Unknown";
                    }
                }
            }
            else if(record.Kind=="task")Task(f);
            else if(record.Kind=="eye_acquisition"||record.Kind=="observation"||record.Kind=="eye_observation")
            {
                string phase=Get(f,"ingest_phase")??"Unknown";int phaseCode;
                if(Int32.TryParse(phase,out phaseCode)&&Enum.IsDefined(typeof(AvatarStroop.Core.Phase),phaseCode))phase=((AvatarStroop.Core.Phase)phaseCode).ToString();
                int block=Int(f,"ingest_block",-1),segment=Int(f,"ingest_segment",-1);
                // Acquisition and owner diagnostics carry no measured task
                // alignment. FIFO adjacency is not a native sample timestamp.
                if(record.Kind=="eye_acquisition"){phase="NativeAcquisitionUnaligned";block=-1;segment=-1;}
                else if(record.Kind=="eye_observation"){phase="OwnerObservationUnaligned";block=-1;segment=-1;}
                string key=block+"/"+segment+"/"+phase;StudyEyeGroup group;
                if(!eye.TryGetValue(key,out group))
                {if(eye.Count>=100000)throw new InvalidDataException("Too many eye segments.");group=new StudyEyeGroup{Block=block,Segment=segment,Phase=phase};eye.Add(key,group);}
                if(record.Kind=="eye_acquisition")group.Acquire(f);
                else if(record.Kind=="observation")group.Observe(f);
                else StudyEyeGroup.Count(group.ObservationReasons,Get(f,"reason")??"Unknown");
            }
        }

        void Task(IDictionary<string,string> f)
        {
            TaskEvents++;string kind=Get(f,"event_kind")??"Unknown";StudyEyeGroup.Count(EventCounts,kind);
            string session=Get(f,"session_id");if(SessionId!=null&&session!=SessionId)throw new InvalidDataException("Task session does not match manifest.");
            var p=Payload(f);int block=Int(f,"block",-1),segment=Int(f,"segment",-1);
            string phase=Get(p,"legacy_phase")??Get(p,"named_phase")??currentPhase;
            if(kind=="phase_changed")currentPhase=Get(p,"to")??phase;else currentPhase=phase;
            string condition=Get(p,"legacy_condition")??Get(p,"named_option");if(condition!=null&&block>=0)conditions[block]=condition;
            string rule=Get(p,"response_rule");
            if(rule!=null)
            {
                string previous;if(rules.TryGetValue(block,out previous)&&previous!=rule)Warn("Response rule changed within block "+block+"; trials remain separated by rule.");
                rules[block]=rule;
            }
            if(kind=="block_task_ended")endedBlocks.Add(block);
            if(kind=="session_ended")SessionEndReason=Get(p,"reason");
            if(kind=="rating")
            {
                string item=Get(p,"item"),value=Get(p,"value"),flag="";
                if(!ratingKeys.Add(block+"/"+item))
                {
                    flag="duplicate_item";
                    foreach(var previous in Ratings.Where(x=>x.Block==block&&x.Item==item))
                        if(!(previous.Flags??"").Contains("duplicate_item"))previous.Flags=(previous.Flags.Length>0?previous.Flags+";":"")+"duplicate_item";
                }
                if(item==null||!RatingItems.Contains(item))flag+=(flag.Length>0?";":"")+"unknown_item";
                int score;if(!Int32.TryParse(value,out score)||score<1||score>5)flag+=(flag.Length>0?";":"")+"invalid_rating";
                Ratings.Add(new StudyRating{Session=SessionId,Block=block,Condition=Condition(block),Rule=rule??"InkColor (legacy inferred)",Item=item,
                    Value=value,Language=Get(p,"language"),Version=Get(p,"version"),LocalTime=Number(f,"event_local_time"),Flags=flag});
            }
            if(kind!="trial_planned"&&kind!="stimulus_presented"&&kind!="first_response"&&kind!="trial_interrupted")return;
            string id=Get(f,"trial_id");if(String.IsNullOrEmpty(id)){Warn("A trial event has no trial ID; not reconstructed.");return;}
            StudyTrial trial;if(!byId.TryGetValue(id,out trial))
            {
                if(Trials.Count>=1000000)throw new InvalidDataException("Too many trials.");
                trial=new StudyTrial{Session=SessionId,Id=id,Block=block,Segment=segment,Condition=Condition(block),Phase=phase,Rule=rule??"InkColor",
                    RuleProvenance=rule==null?"legacy_inferred":"explicit"};byId.Add(id,trial);Trials.Add(trial);
            }
            if(trial.Block!=block||trial.Segment!=segment)trial.Flag("context_conflict");
            if(rule!=null&&trial.Rule!=rule)trial.Flag("response_rule_conflict");
            if(rule!=null)trial.RuleProvenance="explicit";
            string word=Get(p,"word")??Get(p,"planned_word"),ink=Get(p,"ink")??Get(p,"planned_ink");
            if(word!=null){if(trial.Word!=null&&trial.Word!=word)trial.Flag("stimulus_conflict");trial.Word=word;}
            if(ink!=null){if(trial.Ink!=null&&trial.Ink!=ink)trial.Flag("stimulus_conflict");trial.Ink=ink;}
            string expected=Get(p,"expected_response");
            if(expected!=null){if(trial.DeclaredExpected!=null&&trial.DeclaredExpected!=expected)trial.Flag("expected_conflict");trial.DeclaredExpected=expected;}
            if(kind=="trial_planned")
            {trial.PlannedCount++;trial.List=Get(p,"list");trial.RetryOf=Get(p,"retry_of");if(trial.PlannedCount==1)trial.PlanEventTime=Number(f,"event_local_time");}
            if(kind=="stimulus_presented")
            {
                trial.PresentedCount++;
                if(trial.PresentedCount==1)
                {
                    trial.PresentationEventTime=Number(f,"event_local_time");trial.Onset=Number(p,"task_onset_seconds");
                    trial.OnsetBasis=trial.Onset.HasValue?Get(p,"task_onset_basis")??"declared_task_onset":"not_declared_by_presentation";
                    trial.TaskClockId=Get(p,"task_onset_clock_id");trial.UnityRenderSeconds=Number(p,"unity_render_proxy_seconds");
                    trial.UnityRenderBasis=Get(p,"unity_render_proxy_basis");
                }
            }
            if(kind=="first_response")
            {
                trial.ResponseCount++;if(trial.ResponseCount==1)
                {
                    trial.Response=Get(p,"response");trial.ReactionSeconds=Number(p,"rt_seconds");trial.ResponseEventTime=Number(f,"event_local_time");trial.DeclaredCorrect=Get(p,"correct");
                    double? onset=Number(p,"onset");
                    if(trial.Onset.HasValue&&onset.HasValue&&Math.Abs(trial.Onset.Value-onset.Value)>0.000001)trial.Flag("onset_declaration_conflict");
                    if(!trial.Onset.HasValue&&onset.HasValue){trial.Onset=onset;trial.OnsetBasis="response_declared_task_onset";trial.TaskClockId=Get(f,"clock_id");}
                    if(onset.HasValue&&trial.ReactionSeconds.HasValue)trial.ResponseTime=onset.Value+trial.ReactionSeconds.Value;
                }
            }
            if(kind=="trial_interrupted")trial.Interrupted=true;
        }

        public void Finish()
        {
            if(finalized)return;finalized=true;
            if(String.IsNullOrEmpty(SessionId))throw new InvalidDataException("Study manifest/session ID missing.");
            if(TaskEvents==0)Warn("No task events: behavioural coverage cannot be assessed.");
            foreach(var t in Trials)
            {
                if(t.PlannedCount!=1)t.Flag(t.PlannedCount==0?"missing_plan":"duplicate_plan");
                if(t.PresentedCount!=1)t.Flag(t.PresentedCount==0?"not_presented":"duplicate_presentation");
                if(t.ResponseCount!=1)t.Flag(t.ResponseCount==0?"no_response":"duplicate_response");
                if(t.Interrupted)t.Flag("interrupted");
                if(t.Phase=="Unknown"||(!t.Practice&&t.Phase!="MainTask"))t.Flag("unknown_trial_phase");
                if(t.Rule!="InkColor"&&t.Rule!="WordMeaning")t.Flag("unknown_response_rule");
                t.Expected=t.Rule=="InkColor"?t.Ink:t.Rule=="WordMeaning"?t.Word:null;
                if(!IsColour(t.Word)||!IsColour(t.Ink)||!IsColour(t.Response)||!IsColour(t.Expected))t.Flag("invalid_or_missing_colour");
                if(t.DeclaredExpected!=null&&t.DeclaredExpected!=t.Expected)t.Flag("expected_response_mismatch");
                if(IsColour(t.Expected)&&IsColour(t.Response))t.Correct=t.Expected==t.Response;
                bool declared;if(Boolean.TryParse(t.DeclaredCorrect,out declared)&&t.Correct.HasValue&&declared!=t.Correct.Value)t.Flag("correctness_mismatch");
                if(t.PlanEventTime.HasValue&&t.PresentationEventTime.HasValue&&t.PlanEventTime>t.PresentationEventTime)t.Flag("event_time_order_conflict");
                if(t.PresentationEventTime.HasValue&&t.ResponseEventTime.HasValue&&t.PresentationEventTime>t.ResponseEventTime)t.Flag("event_time_order_conflict");
                t.AccuracyIncluded=t.Flags.Count==0;
                if(!t.ReactionSeconds.HasValue||t.ReactionSeconds.Value<=0)t.Flag("missing_or_nonpositive_rt");
                if(!t.Onset.HasValue)t.Flag("missing_declared_onset");
                // The response event is logged after the input is captured.
                // Do not mistake logging latency for an incorrect reaction time.
                if(t.ResponseTime.HasValue&&t.ResponseEventTime.HasValue&&t.ResponseTime.Value>t.ResponseEventTime.Value+0.0001)t.Flag("response_time_after_event");
                t.RtIncluded=t.AccuracyIncluded&&t.Correct==true&&t.Flags.Count==0;
            }
            if(Trials.Any(x=>x.RuleProvenance=="legacy_inferred"))Warn("Older events omit response_rule: InkColor is inferred and labelled, not merged with WordMeaning.");
            if(eye.Values.Any(x=>x.AcquisitionRows>0))Warn("Native acquisition cadence is session-wide and unaligned to task blocks; ingest observations carry separate software task context.");
            if(PlannedBlocks.HasValue&&EndedBlocks<PlannedBlocks.Value)Warn("Only "+EndedBlocks+" of "+PlannedBlocks+" planned blocks have block_task_ended events.");
            foreach(int block in endedBlocks)
                if(Ratings.Count(x=>x.Block==block&&String.IsNullOrEmpty(x.Flags))!=5)Warn("Block "+block+" does not have five unflagged questionnaire item responses.");
            Warn("Eye/face spatial accuracy validation is deferred. AOI counts are descriptive observation proportions, not validated fixations or dwell time.");
        }

        public IEnumerable<StudyBlockSummary> Summaries()
        {
            Finish();
            foreach(var group in Trials.GroupBy(t=>new{t.Block,t.Condition,t.Rule,Scope=t.Practice?"Practice":"Formal",t.Congruence})
                .OrderBy(g=>g.Key.Block).ThenBy(g=>g.Key.Rule,StringComparer.Ordinal).ThenBy(g=>g.Key.Congruence,StringComparer.Ordinal))
            {
                var rt=group.Where(x=>x.RtIncluded).Select(x=>x.ReactionSeconds.Value).OrderBy(x=>x).ToArray();
                int n=group.Count(x=>x.AccuracyIncluded),correct=group.Count(x=>x.AccuracyIncluded&&x.Correct==true);
                double? mean=rt.Length>0?(double?)rt.Average():null;
                yield return new StudyBlockSummary{Block=group.Key.Block,Condition=group.Key.Condition,Rule=group.Key.Rule,Scope=group.Key.Scope,Congruence=group.Key.Congruence,
                    Trials=group.Count(),Planned=group.Count(x=>x.PlannedCount>0),Presented=group.Count(x=>x.PresentedCount>0),Responded=group.Count(x=>x.ResponseCount>0),
                    Interrupted=group.Count(x=>x.Interrupted),AccuracyN=n,Correct=correct,Accuracy=n>0?(double?)correct/n:null,RtN=rt.Length,MeanRt=mean,
                    MedianRt=Quantile(rt,.5),P95Rt=Quantile(rt,.95),SdRt=rt.Length>1?(double?)Math.Sqrt(rt.Sum(x=>(x-mean.Value)*(x-mean.Value))/(rt.Length-1)):null};
            }
        }
        public string Condition(int block){string value;return block<0?"Practice":conditions.TryGetValue(block,out value)?value:"Unknown";}
        public void Warn(string warning){if(!Warnings.Contains(warning))Warnings.Add(warning);}
        public static bool IsColour(string value){return value=="Red"||value=="Green"||value=="Blue"||value=="Yellow";}
        internal static string EnumLabel<T>(string value)
        {int code;return Int32.TryParse(value,out code)&&Enum.IsDefined(typeof(T),code)?Enum.GetName(typeof(T),code):value??"Unknown/missing";}
        public static string Get(IDictionary<string,string> fields,string key){string value;return fields.TryGetValue(key,out value)?value:null;}
        public static int Int(IDictionary<string,string> fields,string key,int fallback){int n;return Int32.TryParse(Get(fields,key),NumberStyles.Integer,Inv,out n)?n:fallback;}
        public static double? Number(IDictionary<string,string> fields,string key)
        {double n;return Double.TryParse(Get(fields,key),NumberStyles.Float,Inv,out n)&&!Double.IsNaN(n)&&!Double.IsInfinity(n)?n:(double?)null;}
        public static bool? Truth(IDictionary<string,string> fields,string key)
        {string text=Get(fields,key);bool value;if(Boolean.TryParse(text,out value))return value;return text=="1"?true:text=="0"?false:(bool?)null;}
        public static double? Quantile(double[] sorted,double probability)
        {if(sorted.Length==0)return null;double index=(sorted.Length-1)*probability;int lo=(int)index,hi=(int)Math.Ceiling(index);return sorted[lo]+(sorted[hi]-sorted[lo])*(index-lo);}
        static Dictionary<string,string> Payload(IDictionary<string,string> fields)
        {
            int n=Int(fields,"field_count",0);if(n<0||n>128)throw new InvalidDataException("Invalid task payload field count.");
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            for(int i=0;i<n;i++){string prefix="field_"+i.ToString(Inv)+"_",key=Get(fields,prefix+"key");if(key==null||result.ContainsKey(key))throw new InvalidDataException("Invalid task payload keys.");result.Add(key,Get(fields,prefix+"value"));}
            return result;
        }
    }
}
#endif
