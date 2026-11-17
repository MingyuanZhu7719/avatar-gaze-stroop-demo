using System;
using System.Collections.Generic;
using System.Globalization;

namespace AvatarStroop.Core
{
    public sealed partial class Experiment
    {
        public bool UsesTutorial {get{return Configuration.UsesTutorial;}}
        // Declaration on the shared task clock, not evidence that any eye
        // sample has arrived, been written, or reached durable storage.
        public double? FormalEyeRecordingWindowStart
        {
            get
            {
                if(!Configuration.FormalEyeRecordingOnly)return null;
                for(int i=0;i<events.Count;i++)
                    if(events[i].Kind=="formal_eye_recording_window_open")return events[i].Time;
                return null;
            }
        }
        public bool? PracticePassed {get{return UsesTutorial&&InPhaseOrPaused(Phase.PracticeExamResult)?
            (bool?)(practiceCorrect>=Configuration.PracticePassCorrect):null;}}
        public int TutorialRound
        {
            get
            {
                for(int i=events.Count-1;i>=0;i--)
                {
                    string value;int round;
                    if(events[i].Kind=="tutorial_started"&&events[i].Fields.TryGetValue("round",out value)&&
                        Int32.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out round))return round;
                }
                return 0;
            }
        }
        void TrainingMutation(string operation,Action action)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){action();return;}
            BeginMutation(operation);bool success=false;
            try{action();success=true;}finally{EndMutation(success);}
        }
        void RequireTutorialProtocol()
        {if(!UsesTutorial)throw new InvalidOperationException("Tutorial protocol was not configured for this session.");}

        public void BeginTutorial(Actor actor)
        {
            TrainingMutation("BeginTutorial",()=>
            {RequireOwner(actor);RequireTutorialProtocol();Require(Phase.Familiarization);BeginTutorialCore("initial_training");});
        }
        void BeginTutorialCore(string reason)
        {
            int round=checked(TutorialRound+1);
            current=null;TraceMutation();trialIndex=0;TraceMutation();Segment=0;TraceMutation();
            Move(Phase.Tutorial);
            Log("tutorial_started","round",round.ToString(CultureInfo.InvariantCulture),"reason",reason,
                "question_limit","unlimited","response_deadline","none");
            CreateTutorialTrial(false);
        }
        public void ContinueTutorial()
        {
            TrainingMutation("ContinueTutorial",()=>
            {
                RequireTutorialProtocol();Require(Phase.TutorialFeedback);
                Log("tutorial_continue","selection","continue_tutorial");
                trialIndex=checked(trialIndex+1);TraceMutation();Move(Phase.Tutorial);CreateTutorialTrial(false);
            });
        }
        public void RequestPracticeExam()
        {
            TrainingMutation("RequestPracticeExam",()=>
            {
                RequireTutorialProtocol();Require(Phase.TutorialFeedback);
                Log("tutorial_completed","selection","start_practice_exam","round",TutorialRound.ToString(CultureInfo.InvariantCulture),
                    "questions_answered",(trialIndex+1).ToString(CultureInfo.InvariantCulture));
                current=null;TraceMutation();Segment=0;TraceMutation();Move(Phase.PracticeExamReady);
            });
        }
        public void ContinuePracticeExamResult()
        {
            TrainingMutation("ContinuePracticeExamResult",()=>
            {
                RequireTutorialProtocol();Require(Phase.PracticeExamResult);
                bool passed=practiceCorrect>=Configuration.PracticePassCorrect;
                Log("practice_result_acknowledged","attempt",PracticeAttempt.ToString(CultureInfo.InvariantCulture),"passed",passed.ToString());
                if(passed){BlockIndex=0;TraceMutation();Move(Phase.BlockPreparation);}
                else BeginTutorialCore("practice_exam_below_threshold");
            });
        }
        void CreateTutorialTrial(bool retry)
        {
            int round=TutorialRound;string retryOf=retry&&current!=null?current.TrialId:null;
            Stimulus stimulus=retry&&current!=null?current.Stimulus:Configuration.TaskKind==TaskKind.VisualSearch?
                VisualSearchPlans.Tutorial(Configuration.Seed,round,trialIndex):TrialPlans.Tutorial(Configuration.Seed,round,trialIndex);
            string list="tutorial-"+Configuration.Seed+"-"+round;
            string id=Configuration.SessionId+"/tutorial-"+round+"/t"+trialIndex;
            if(retryOf!=null)id+="/retry-s"+Segment.ToString(CultureInfo.InvariantCulture);
            current=new TrialRecord(trialAuthority,id,Configuration.SessionId,list,true,0,-1,trialIndex,Segment,null,stimulus,
                Configuration.NamedPlan==null?Configuration.NamedOption:null,Configuration.NamedPlan,null,Configuration.ResponseRule);
            TraceMutation();trials.Add(current);TraceMutation();
            var fields=new List<string>{"list",list,"planned_index",trialIndex.ToString(CultureInfo.InvariantCulture)};
            if(stimulus.IsVisualSearch)fields.AddRange(new[]{"planned_stimulus_schema",VisualSearchTrial.Schema,
                "visual_search_layout",stimulus.VisualSearch.Serialize(),"layout_seed",stimulus.VisualSearch.LayoutSeed.ToString(CultureInfo.InvariantCulture),
                "target_colour",stimulus.VisualSearch.TargetColour.ToString(),"target_pattern",stimulus.VisualSearch.TargetPattern.ToString(),
                "timeout_seconds","none"});
            else fields.AddRange(new[]{"planned_stimulus_schema","1","planned_word",stimulus.Word.ToString(),"planned_ink",stimulus.Ink.ToString()});
            if(retryOf!=null)fields.AddRange(new[]{"tracking_retry_schema","1","retry_of",retryOf});
            Log("trial_planned",fields.ToArray());
        }
        string TutorialExpectedAnswer
        {get{return current==null?null:current.VisualSearch!=null?current.VisualSearch.TargetLabel:current.ExpectedResponse.ToString();}}
        string TutorialFeedbackText
        {
            get
            {
                if(Phase!=Phase.TutorialFeedback||current==null)return null;
                string result=current.Correct==true?"Correct!":"Incorrect.";
                if(current.VisualSearch!=null)return result+" Match both colour and pattern. The target is the "+TutorialExpectedAnswer+".";
                return result+" "+(Configuration.ResponseRule==ResponseRule.WordMeaning?
                    "Choose the colour named by the word: ":"Choose the font colour: ")+TutorialExpectedAnswer+".";
            }
        }
        string LearningStage
        {
            get
            {
                Phase active=Phase==Phase.Paused?beforePause:Phase;
                if(active==Phase.Tutorial||active==Phase.TutorialFeedback||current!=null&&current.IsTutorial)return "tutorial";
                if(active==Phase.Practice||active==Phase.PracticeExamReady||active==Phase.PracticeExamResult)return "practice_exam";
                return BlockIndex>=0?"formal":"setup";
            }
        }
        bool PracticePlanPhase
        {get{return UsesTutorial?Phase==Phase.PracticeExamReady:
            Phase==Phase.Preparation||Phase==Phase.Familiarization||Phase==Phase.PracticeRetry;}}
    }
}
