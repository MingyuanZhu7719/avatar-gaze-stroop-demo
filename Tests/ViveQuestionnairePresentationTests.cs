using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveQuestionnairePresentationTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE questionnaire displays the actual candidate and one selected option",()=>{
                for(int selected=1;selected<=5;selected++)
                {
                    int marked=0;
                    for(int score=1;score<=5;score++)
                    {
                        string line=ViveQuestionnairePresentation.Option(score,selected,"en");
                        Check.True(line.Contains(Questionnaire.Anchor(score,"en")));
                        if(line.StartsWith("> ",StringComparison.Ordinal)){marked++;Check.Equal(selected,score);}
                    }
                    Check.Equal(1,marked);
                    string selection=ViveQuestionnairePresentation.Selection(selected,"en");
                    Check.True(selection.Contains("SELECTED: "+selected));
                    Check.True(selection.Contains(Questionnaire.Anchor(selected,"en")));
                    Check.True(selection.Contains("not submitted"));
                }
            });
            suite.Run("VIVE guide repeats the actual trackpad and trigger order",()=>{
                Check.Equal(0,ViveQuestionnairePresentation.GuideStep(0));
                Check.Equal(1,ViveQuestionnairePresentation.GuideStep(2));
                Check.Equal(2,ViveQuestionnairePresentation.GuideStep(4));
                Check.Equal(0,ViveQuestionnairePresentation.GuideStep(6));
                Check.Equal(0,ViveQuestionnairePresentation.GuideStep(Double.NaN));
                Check.True(ViveQuestionnairePresentation.GuideCaption(0).Contains("TOP: 3 TO 2"));
                Check.True(ViveQuestionnairePresentation.GuideCaption(1).Contains("BOTTOM: 2 TO 3"));
                Check.True(ViveQuestionnairePresentation.GuideCaption(2).Contains("TRIGGER"));
            });
            suite.Run("VIVE trigger illustration pulls then releases without submitting",()=>{
                Check.Equal(0d,ViveQuestionnairePresentation.GuideTriggerTravel(0));
                Check.Equal(0d,ViveQuestionnairePresentation.GuideTriggerTravel(2));
                Check.Equal(0d,ViveQuestionnairePresentation.GuideTriggerTravel(4));
                Check.True(ViveQuestionnairePresentation.GuideTriggerTravel(4.5)>.6);
                Check.True(ViveQuestionnairePresentation.GuideTriggerTravel(5)>.99);
                Check.True(ViveQuestionnairePresentation.GuideTriggerTravel(5.9)<.2);
                Check.Equal(0d,ViveQuestionnairePresentation.GuideTriggerTravel(6));
                Check.Equal(0d,ViveQuestionnairePresentation.GuideTriggerTravel(Double.NaN));
            });
            suite.Run("VIVE questionnaire display rejects invalid selected ratings",()=>{
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveQuestionnairePresentation.Selection(0,"en"));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveQuestionnairePresentation.Option(6,3,"en"));
            });
        }
    }
}
