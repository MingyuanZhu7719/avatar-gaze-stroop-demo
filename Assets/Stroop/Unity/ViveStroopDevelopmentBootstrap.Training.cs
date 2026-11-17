#if STROOP_XR_PREVIEW
using AvatarStroop.Core;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        internal bool PracticeIsExam {get{return participant!=null&&participant.UsesTutorial;}}
        int TrainingPanelSelection
        {get{return participant!=null&&participant.Phase==Phase.TutorialFeedback?participant.TutorialMenuSelection:participant==null?0:participant.CandidateRating;}}
        string TrainingStartLabel
        {get{return participant!=null&&participant.UsesTutorial&&participant.Phase==Phase.Preparation?"Begin tutorial":"Begin practice";}}
    }
}
#endif
