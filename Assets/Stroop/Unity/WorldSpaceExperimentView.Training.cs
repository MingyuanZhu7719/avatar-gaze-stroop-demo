using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class WorldSpaceExperimentView
    {
        int viveTutorialMenuSelection;
        int tutorialPanelCaptureFrame=-1,tutorialPanelSeenFrame=-1,tutorialPanelObserved;
        string tutorialPanelCapturedIdentity,tutorialPanelSeenIdentity,tutorialPanelCapturedBody,tutorialPanelCapturedOptions;
        TextMesh tutorialBoardInstructions;
        int CurrentPanelSelection
        {get{return currentView!=null&&currentView.Phase==Phase.TutorialFeedback?viveTutorialMenuSelection:viveQuestionnaireRating;}}
        bool IsTutorialPanel
        {get{return viveTaskPreview&&role==Actor.Participant&&currentView!=null&&currentView.UsesTutorial&&
            (currentView.Phase==Phase.TutorialFeedback||currentView.Phase==Phase.PracticeExamReady||currentView.Phase==Phase.PracticeExamResult);}}
        public void SetViveTutorialMenuSelection(int selection)
        {
            if(selection<0||selection>1)throw new ArgumentOutOfRangeException("selection");
            viveTutorialMenuSelection=selection;
        }
        static string TutorialPanelIdentity(TaskView task,int selection)
        {return task==null?null:task.Phase+"|"+task.BlockIndex+"|"+ViveRawPanelPolicy.PanelCorrection(task)+"|"+selection;}
        internal bool HasPriorTutorialPanelWitness(TaskView task,int selection,int frame)
        {
            return !ViveRawAttached()&&frame>=0&&(long)frame-tutorialPanelSeenFrame==1&&
                String.Equals(tutorialPanelSeenIdentity,TutorialPanelIdentity(task,selection),StringComparison.Ordinal)&&
                String.Equals(tutorialPanelSeenIdentity,TutorialPanelIdentity(currentView,CurrentPanelSelection),StringComparison.Ordinal);
        }
        void PrepareTutorialPanelCull(Camera camera)
        {
            tutorialPanelCaptureFrame=-1;tutorialPanelObserved=0;
            if(ViveRawAttached()||!IsTutorialPanel||camera!=presentationCamera||!camera.isActiveAndEnabled||camera.targetTexture!=null||
                camera.cullingMask==0||!PresentationIsAvailable()||hasPresentationFault)return;
            if(!BindRawPanelElement(phaseText.transform,this,1)||!BindRawPanelElement(messageText.transform,this,2)||
                !BindRawPanelElement(previewInputText.transform,this,4096))return;
            tutorialPanelCaptureFrame=Time.frameCount;tutorialPanelCapturedIdentity=TutorialPanelIdentity(currentView,CurrentPanelSelection);
            tutorialPanelCapturedBody=messageText.text;tutorialPanelCapturedOptions=previewInputText.text;
        }
        void FinishTutorialPanelRender(Camera camera)
        {
            if(ViveRawAttached()||camera!=presentationCamera||tutorialPanelCaptureFrame!=Time.frameCount||
                (tutorialPanelObserved&(1|2|4096))!=(1|2|4096)||!PresentationIsAvailable()||hasPresentationFault||
                camera.targetTexture!=null||camera.cullingMask==0||!camera.isActiveAndEnabled||
                tutorialPanelCapturedIdentity!=TutorialPanelIdentity(currentView,CurrentPanelSelection)||
                tutorialPanelCapturedBody!=messageText.text||tutorialPanelCapturedOptions!=previewInputText.text)return;
            tutorialPanelSeenFrame=Time.frameCount;tutorialPanelSeenIdentity=tutorialPanelCapturedIdentity;
        }
        internal static string TrainingTaskName(TaskView task)
        {return task.TaskKind==TaskKind.VisualSearch?"Visual search":task.ResponseRule==ResponseRule.WordMeaning?"Word meaning":"Ink colour";}
        internal static string TrainingPanelMessage(TaskView task)
        {
            if(task.Phase==Phase.TutorialFeedback)return task.TutorialFeedback??"Review the answer before continuing.";
            if(task.Phase==Phase.PracticeExamReady)return "10 questions. At least 8 correct answers pass.\nYour score appears at the end.\nRelease the controls for the five-second countdown.";
            if(task.Phase==Phase.PracticeExamResult)return "You answered "+task.PracticeCorrect+" of "+task.PracticeTrialCount+" correctly.\n"+
                (task.PracticePassed==true?"You passed. You are ready for the experiment.":"You need at least 8 correct answers.\nReturn to the tutorial, then try again when ready.");
            return "";
        }
        internal static string TutorialMenuText(int selection)
        {
            return (selection==0?">  ":"   ")+"Continue tutorial\n\n"+
                (selection==1?">  ":"   ")+"I am ready: start practice exam";
        }
        void LayoutTutorialPanel(Rect frustum)
        {
            if(phaseAnimationRoot!=null)phaseAnimationRoot.gameObject.SetActive(false);
            PositionBacking(headerBacking,frustum,new Rect(),false);
            PositionBacking(footerBacking,frustum,new Rect(),false);
            PositionBacking(worldMenuBacking,frustum,new Rect(0,0,1,1),true);
            foreach(var label in new[]{developmentBanner,provisionalText,avatarLabel})
                FitHud(label,"",frustum,new Rect(),.02f,1);
            bool feedback=currentView.Phase==Phase.TutorialFeedback;
            string heading=feedback?"Tutorial feedback":currentView.Phase==Phase.PracticeExamResult?
                (currentView.PracticePassed==true?"Practice exam passed":"Keep practising"):"Practice exam";
            FitHud(phaseText,heading+" | "+TrainingTaskName(currentView),frustum,new Rect(.04f,.05f,.92f,.09f),.044f,3);
            FitHud(messageText,TrainingPanelMessage(currentView),frustum,new Rect(.06f,.20f,.88f,.34f),.043f,100);
            string action=feedback?TutorialMenuText(viveTutorialMenuSelection):
                currentView.Phase==Phase.PracticeExamResult?(currentView.PracticePassed==true?"Release, then pull the trigger to continue.":"Release, then pull the trigger to return to the tutorial."):
                "Keep the headset on and release the controls.";
            FitHud(previewInputText,action,frustum,new Rect(.06f,.57f,.88f,.22f),.039f,100);
            FitHud(previewStatusText,feedback?"Trackpad TOP / BOTTOM: select | Trigger: confirm":"",frustum,new Rect(.04f,.84f,.92f,.07f),.028f,3);
            FitHud(faultText,faultCaption,frustum,new Rect(.04f,.93f,.92f,.06f),.025f,100);
        }
        void RefreshTutorialInstructions()
        {
            bool show=viveTaskPreview&&role==Actor.Participant&&currentView!=null&&
                currentView.Phase==Phase.Tutorial&&currentView.TaskKind==TaskKind.Stroop;
            if(!show){if(tutorialBoardInstructions!=null)tutorialBoardInstructions.gameObject.SetActive(false);return;}
            if(tutorialBoardInstructions==null)
                tutorialBoardInstructions=Text("TutorialInstructions",contentRoot,BoardPosition+new Vector3(0,-.35f,-.065f),.012f,64,new Color(.83f,.91f,.94f));
            tutorialBoardInstructions.gameObject.SetActive(true);
            tutorialBoardInstructions.text=currentView.ResponseRule==ResponseRule.WordMeaning?
                "TUTORIAL: choose the colour named by the word.\nIgnore its ink. Click the matching trackpad colour.":
                "TUTORIAL: choose the font colour. Ignore the word.\nClick the matching trackpad colour. Take your time.";
            tutorialBoardInstructions.transform.localScale=Vector3.one;
            var size=tutorialBoardInstructions.GetComponent<Renderer>().localBounds.size;
            if(size.x>1.25f)tutorialBoardInstructions.transform.localScale=Vector3.one*(1.25f/size.x);
        }
    }
}
