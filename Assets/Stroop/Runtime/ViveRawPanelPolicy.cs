using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Small fail-closed predicates shared by the actual Unity render path and
    // portable tests. They do not authorize recording or prove HMD visibility.
    internal static class ViveRawPanelPolicy
    {
        internal static bool RejectOtherCamera(bool rawAttached,bool available,bool participantCamera,bool trialActive,bool panelExposed)
        {return rawAttached&&available&&!participantCamera&&(trialActive||panelExposed);}
        internal static bool CountdownTextMatches(string expected,string before,string after)
        {return !String.IsNullOrEmpty(expected)&&String.Equals(expected,before,StringComparison.Ordinal)&&String.Equals(expected,after,StringComparison.Ordinal);}
        internal static bool IsPanelPhase(Phase phase)
        {
            switch(phase)
            {
                case Phase.Preparation: case Phase.Familiarization: case Phase.PracticeCorrection:
                case Phase.PracticeRetry: case Phase.BlockPreparation: case Phase.BlockReady:
                case Phase.Countdown: case Phase.Questionnaire: case Phase.Rest: case Phase.Paused:
                case Phase.TutorialFeedback: case Phase.PracticeExamReady: case Phase.PracticeExamResult:return true;
                default:return false;
            }
        }
        internal static string PanelCorrection(TaskView task)
        {
            if(task==null)return null;
            if(task.Phase==Phase.TutorialFeedback)
                return "tutorial|"+task.TrialId+"|"+task.TutorialFeedback+"|"+task.TutorialExpectedAnswer;
            if(task.Phase==Phase.PracticeExamResult)
                return "exam|"+task.PracticeAttempt+"|"+task.PracticeCorrect+"/"+task.PracticeTrialCount+"|"+task.PracticePassed;
            return task.PracticeCorrection;
        }
        internal static bool SamePanelState(Phase expectedPhase,int expectedBlock,bool expectedIntro,int expectedRating,
            int expectedAnswered,bool expectedReady,string expectedItem,string expectedCorrection,
            TaskView task,bool intro,int rating,int answered)
        {
            return task!=null&&IsPanelPhase(task.Phase)&&expectedPhase==task.Phase&&expectedBlock==task.BlockIndex&&
                expectedIntro==(task.Phase==Phase.Questionnaire&&intro)&&
                expectedRating==((task.Phase==Phase.Questionnaire&&!intro)||task.Phase==Phase.TutorialFeedback?rating:0)&&
                expectedAnswered==(task.Phase==Phase.Questionnaire?answered:0)&&expectedReady==task.ReadyEnabled&&
                String.Equals(expectedItem,task.QuestionnaireItem,StringComparison.Ordinal)&&
                String.Equals(expectedCorrection,PanelCorrection(task),StringComparison.Ordinal);
        }
    }
}
