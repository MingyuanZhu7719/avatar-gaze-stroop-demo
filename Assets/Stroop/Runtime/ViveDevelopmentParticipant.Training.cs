using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveDevelopmentParticipant
    {
        int tutorialMenuSelection;
        public bool UsesTutorial {get{return experiment.Configuration.UsesTutorial;}}
        public int PracticeTrialCount {get{return experiment.Configuration.PracticeTrialCount;}}
        public int PracticeAttempt {get{return experiment.PracticeAttempt;}}
        public int PracticeCorrect {get{return experiment.PracticeCorrect;}}
        public int TutorialMenuSelection {get{return tutorialMenuSelection;}}

        // These use the same task mutation/admission boundary as answers.
        // Recording hosts must call through their current FIFO reservation.
        void TrainingMutation(Action action)
        {
            if(observation==null)RunNamedRecordingMutation(action);
            else Observed(action,false);
        }
        public void OwnerBeginTutorial(Actor actor)
        {
            TrainingMutation(()=>{
                RequireOwner(actor);RequireReady();
                if(!UsesTutorial)throw new InvalidOperationException("Tutorial protocol required.");
                if(experiment.Phase==Phase.Preparation)
                {experiment.BeginFamiliarization(actor);if(!MayContinueOrdinaryMutation())return;}
                experiment.BeginTutorial(actor);
            });
        }
        public void ContinueTutorial()
        {TrainingMutation(()=>{RequireReady();experiment.ContinueTutorial();});}
        public void RequestPracticeExam()
        {TrainingMutation(()=>{RequireReady();experiment.RequestPracticeExam();});}
        public void ContinuePracticeExamResult()
        {TrainingMutation(()=>{RequireReady();experiment.ContinuePracticeExamResult();});}
    }
}
