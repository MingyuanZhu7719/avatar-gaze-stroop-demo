using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum QuestPreviewAction { None, Respond, AdjustRating, SubmitRating, ContinuePractice, Ready, Resume, Pause, Withdraw }

    // One complete controller sample. The Unity adapter may set DeviceReady only
    // when the XR runtime/head pose are valid and both controllers are readable.
    // Controller/head tracking is not eye tracking and provides no gaze sample.
    public sealed class QuestPreviewControls
    {
        public bool DeviceReady {get;private set;}
        public bool X {get;private set;}
        public bool Y {get;private set;}
        public bool A {get;private set;}
        public bool B {get;private set;}
        public bool RightTrigger {get;private set;}
        public bool LeftMenu {get;private set;}
        public double LeftStickY {get;private set;}
        public QuestPreviewControls(bool deviceReady,bool x,bool y,bool a,bool b,bool rightTrigger,bool leftMenu,double leftStickY)
        {DeviceReady=deviceReady;X=x;Y=y;A=a;B=b;RightTrigger=rightTrigger;LeftMenu=leftMenu;LeftStickY=leftStickY;}
        internal int Buttons {get{return (X?1:0)|(Y?2:0)|(A?4:0)|(B?8:0)|(RightTrigger?16:0)|(LeftMenu?32:0);}}
        internal bool Usable {get{return DeviceReady&&WireCodec.Finite(LeftStickY)&&LeftStickY>=-1&&LeftStickY<=1;}}
    }

    public sealed class QuestPreviewInputFrame
    {
        public Phase StartPhase {get;private set;}
        public int QuestionnaireItem {get;private set;}
        public bool DeviceReady {get;private set;}
        public QuestPreviewAction Action {get;private set;}
        public Colour? Response {get;private set;}
        public int? Rating {get;private set;}
        public int? CandidateRating {get;private set;}
        public IReadOnlyList<Colour> ReleasedColours {get;private set;}
        internal QuestPreviewInputFrame(Phase phase,int item,bool ready,QuestPreviewAction action,Colour? response,int? rating,int? candidate,IReadOnlyList<Colour> released)
        {StartPhase=phase;QuestionnaireItem=item;DeviceReady=ready;Action=action;Response=response;Rating=rating;CandidateRating=candidate;ReleasedColours=released;}
    }

    // Development-preview input intent only. This class neither advances the
    // experiment nor chooses a source, rates a default, starts practice, or changes
    // the apparatus gate. Call the existing core method for the one returned action.
    public sealed class QuestPreviewInputRouter
    {
        public const double StickPressThreshold=.65;
        public const double StickReleaseThreshold=.25;
        const int Faces=15,Trigger=16,Menu=32;
        bool sampled,stickArmed;
        Phase previousPhase;
        int previousItem,previousButtons,emittedColours,candidate=3;

        // Releases are returned separately and must be forwarded in every phase,
        // before the action. Reset/loss releases only colours whose down intent
        // this router actually emitted; it never invents a response or pause.
        public IReadOnlyList<Colour> Reset()
        {
            var released=Colours(emittedColours);
            sampled=false;stickArmed=false;previousButtons=0;emittedColours=0;previousItem=-1;candidate=3;
            return released;
        }

        public QuestPreviewInputFrame Update(Phase startPhase,int questionnaireItem,QuestPreviewControls controls)
        {
            if(!Enum.IsDefined(typeof(Phase),startPhase))throw new ArgumentOutOfRangeException("startPhase");
            if(startPhase==Phase.Questionnaire&&(questionnaireItem<0||questionnaireItem>=Questionnaire.Count))
                throw new ArgumentOutOfRangeException("questionnaireItem");
            if(controls==null)throw new ArgumentNullException("controls");
            int item=startPhase==Phase.Questionnaire?questionnaireItem:-1;
            if(!controls.Usable)return Frame(startPhase,item,false,QuestPreviewAction.None,null,null,Reset());

            int buttons=controls.Buttons;
            var released=Colours(emittedColours&~buttons&Faces);
            emittedColours&=buttons;
            bool boundary=!sampled||startPhase!=previousPhase||item!=previousItem;
            if(boundary)
            {
                sampled=true;previousPhase=startPhase;previousItem=item;previousButtons=buttons;candidate=3;
                stickArmed=Math.Abs(controls.LeftStickY)<=StickReleaseThreshold;
                // A phase/question/reconnection starts with an observation, never
                // an action. Held controls must be released or centred first.
                return Frame(startPhase,item,true,QuestPreviewAction.None,null,null,released);
            }

            // Consume every physical edge before choosing one action. Simultaneous
            // unselected buttons cannot be replayed on the following trial/frame.
            int pressed=buttons&~previousButtons;previousButtons=buttons;
            int stick=StickEdge(controls.LeftStickY);
            if((pressed&Menu)!=0&&startPhase!=Phase.Paused&&startPhase!=Phase.Ended&&startPhase!=Phase.NeedsProtocolDecision)
                return Frame(startPhase,item,true,QuestPreviewAction.Pause,null,null,released);

            if(startPhase==Phase.Practice||startPhase==Phase.MainTask)
            {
                // Explicit deterministic simultaneous-press priority: X, Y, A, B.
                for(int colour=0;colour<4;colour++)if((pressed&(1<<colour))!=0)
                {emittedColours|=1<<colour;return Frame(startPhase,item,true,QuestPreviewAction.Respond,(Colour)colour,null,released);}
            }
            else if(startPhase==Phase.Questionnaire)
            {
                // Changing the displayed candidate and confirming cannot happen
                // in one frame. A held stick never repeats without a neutral sample.
                // Scores are displayed top-to-bottom as 1..5: +Y/up moves to
                // the smaller score, while -Y/down moves to the larger score.
                if(stick!=0)
                {candidate=Math.Max(1,Math.Min(5,candidate-stick));return Frame(startPhase,item,true,QuestPreviewAction.AdjustRating,null,null,released);}
                if((pressed&4)!=0)return Frame(startPhase,item,true,QuestPreviewAction.SubmitRating,null,candidate,released);
            }
            else if(startPhase==Phase.Paused)
            {
                if((pressed&8)!=0)return Frame(startPhase,item,true,QuestPreviewAction.Withdraw,null,null,released);
                if((pressed&Trigger)!=0)return Frame(startPhase,item,true,QuestPreviewAction.Resume,null,null,released);
            }
            else if(startPhase==Phase.NeedsProtocolDecision)
            {
                // Withdrawal remains the participant's existing core capability;
                // it does not resolve, resume or bypass the pending protocol issue.
                if((pressed&8)!=0)return Frame(startPhase,item,true,QuestPreviewAction.Withdraw,null,null,released);
            }
            else if((pressed&Trigger)!=0)
            {
                if(startPhase==Phase.PracticeCorrection)return Frame(startPhase,item,true,QuestPreviewAction.ContinuePractice,null,null,released);
                if(startPhase==Phase.Rest)return Frame(startPhase,item,true,QuestPreviewAction.Ready,null,null,released);
            }
            return Frame(startPhase,item,true,QuestPreviewAction.None,null,null,released);
        }

        int StickEdge(double axis)
        {
            if(Math.Abs(axis)<=StickReleaseThreshold){stickArmed=true;return 0;}
            if(!stickArmed)return 0;
            if(axis>=StickPressThreshold){stickArmed=false;return 1;}
            if(axis<=-StickPressThreshold){stickArmed=false;return -1;}
            return 0;
        }
        QuestPreviewInputFrame Frame(Phase phase,int item,bool ready,QuestPreviewAction action,Colour? response,int? rating,IReadOnlyList<Colour> released)
        {return new QuestPreviewInputFrame(phase,item,ready,action,response,rating,ready&&phase==Phase.Questionnaire?(int?)candidate:null,released);}
        static IReadOnlyList<Colour> Colours(int mask)
        {
            var result=new List<Colour>();for(int i=0;i<4;i++)if((mask&(1<<i))!=0)result.Add((Colour)i);
            return new ReadOnlyCollection<Colour>(result);
        }
    }
}
