using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Display-only labels for the VIVE development questionnaire. The owner
    // of the task still decides the candidate and the submitted 1-5 value.
    public static class ViveQuestionnairePresentation
    {
        public static string Option(int score,int selected,string language)
        {
            if(score<1||score>5||selected<1||selected>5)throw new ArgumentOutOfRangeException();
            return (score==selected?"> ":"  ")+score+"  "+Questionnaire.Anchor(score,language)+(score==selected?"  <":"");
        }
        public static string Selection(int selected,string language)
        {
            if(selected<1||selected>5)throw new ArgumentOutOfRangeException("selected");
            return "SELECTED: "+selected+"  "+Questionnaire.Anchor(selected,language)+"   (not submitted)";
        }
        public static int GuideStep(double seconds)
        {
            if(Double.IsNaN(seconds)||Double.IsInfinity(seconds)||seconds<0)seconds=0;
            return (int)((seconds%6)/2);
        }
        // Display-only. A full pull and release occurs during the third step;
        // this cannot confirm, submit or otherwise create a controller input.
        public static double GuideTriggerTravel(double seconds)
        {
            if(Double.IsNaN(seconds)||Double.IsInfinity(seconds)||seconds<0||GuideStep(seconds)!=2)return 0;
            return Math.Sin(((seconds%2)/2)*Math.PI);
        }
        public static string GuideCaption(int step)
        {
            switch(step)
            {
                case 0:return "1  CLICK TOP: 3 TO 2";
                case 1:return "2  CLICK BOTTOM: 2 TO 3";
                case 2:return "3  PRESS TRIGGER TO CONFIRM";
                default:throw new ArgumentOutOfRangeException("step");
            }
        }
    }
}
