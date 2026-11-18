using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class WorldSpaceExperimentView
    {
        int viveTutorialMenuSelection;
        bool practicePlanConfirmation;
        internal void SetVivePracticePlanConfirmation(bool required){practicePlanConfirmation=required;}
        int tutorialPanelCaptureFrame=-1,tutorialPanelSeenFrame=-1,tutorialPanelObserved;
        string tutorialPanelCapturedIdentity,tutorialPanelSeenIdentity,tutorialPanelCapturedBody,tutorialPanelCapturedOptions;
        TextMesh tutorialBoardInstructions;
        Transform tutorialAnswerMark,firstAnswerStroke,secondAnswerStroke;
        string markedTutorialTrial;
        bool? markedTutorialCorrect;
        double tutorialMarkStarted;
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
            if(task.Phase==Phase.TutorialFeedback)
            {
                if(String.IsNullOrEmpty(task.TutorialExpectedAnswer))return task.TutorialFeedback??"Review the answer before continuing.";
                if(task.TaskKind==TaskKind.VisualSearch)return "Target: "+task.TutorialExpectedAnswer+".";
                return (task.ResponseRule==ResponseRule.WordMeaning?"The word names ":"The ink is ")+task.TutorialExpectedAnswer+".";
            }
            if(task.Phase==Phase.PracticeExamReady)return "10 questions. At least 8 correct answers pass.\nYour score appears at the end.";
            if(task.Phase==Phase.PracticeExamResult)return "You answered "+task.PracticeCorrect+" of "+task.PracticeTrialCount+" correctly.\n"+
                (task.PracticePassed==true?"You passed. You are ready for the experiment.":"You need at least 8 correct answers.\nReturn to the tutorial, then try again when ready.");
            return "";
        }
        internal static string TutorialMenuText(int selection)
        {
            return (selection==0?">  ":"   ")+"Continue tutorial\n"+
                (selection==1?">  ":"   ")+"Start practice exam";
        }
        void LayoutTutorialPanel(Rect frustum)
        {
            if(phaseAnimationRoot!=null)phaseAnimationRoot.gameObject.SetActive(false);
            PositionBacking(headerBacking,frustum,new Rect(),false);
            PositionBacking(footerBacking,frustum,new Rect(),false);
            PositionBacking(worldMenuBacking,frustum,new Rect(0,0,1,1),true);
            foreach(var label in new[]{developmentBanner,provisionalText,avatarLabel})
                FitHud(label,"",frustum,new Rect(),.02f,1);
            if(currentView.Phase==Phase.TutorialFeedback)
            {
                LayoutTutorialAnswerMark(frustum);
                string outcome=!currentView.TutorialCorrect.HasValue?"Review answer":currentView.TutorialCorrect.Value?"Correct!":"Incorrect";
                FitHud(phaseText,outcome,frustum,new Rect(.27f,.08f,.68f,.15f),.086f,2);
                FitHud(messageText,TrainingPanelMessage(currentView),frustum,new Rect(.07f,.28f,.86f,.20f),.063f,100);
                FitHud(previewInputText,TutorialMenuText(viveTutorialMenuSelection),frustum,new Rect(.07f,.51f,.86f,.22f),.062f,100);
                FitHud(previewStatusText,"Trackpad up/down to choose\nPull trigger to confirm",frustum,new Rect(.07f,.77f,.86f,.15f),.043f,100);
                FitHud(faultText,faultCaption,frustum,new Rect(.04f,.93f,.92f,.06f),.025f,100);
                return;
            }
            string heading=currentView.Phase==Phase.PracticeExamResult?
                (currentView.PracticePassed==true?"Practice exam passed":"Keep practising"):"Practice exam";
            FitHud(phaseText,heading+" | "+TrainingTaskName(currentView),frustum,new Rect(.04f,.05f,.92f,.09f),.044f,3);
            FitHud(messageText,TrainingPanelMessage(currentView),frustum,new Rect(.06f,.20f,.88f,.34f),.043f,100);
            string action=currentView.Phase==Phase.PracticeExamResult?(currentView.PracticePassed==true?"Release, then pull the trigger to continue.":"Release, then pull the trigger to return to the tutorial."):
                practicePlanConfirmation?"Release all controls. Then pull and release the trigger to restart the countdown.":
                "Release the controls for the five-second countdown.";
            FitHud(previewInputText,action,frustum,new Rect(.06f,.57f,.88f,.22f),.039f,100);
            FitHud(previewStatusText,"",frustum,new Rect(.04f,.84f,.92f,.07f),.028f,3);
            FitHud(faultText,faultCaption,frustum,new Rect(.04f,.93f,.92f,.06f),.025f,100);
        }
        void LayoutTutorialAnswerMark(Rect frustum)
        {
            if(!currentView.TutorialCorrect.HasValue)return;
            bool correct=currentView.TutorialCorrect.Value;
            if(tutorialAnswerMark==null)
            {
                tutorialAnswerMark=Child("TutorialAnswerMark",warningRoot,Vector3.zero);
                firstAnswerStroke=FlowShape("FirstStroke",PrimitiveType.Quad,tutorialAnswerMark,Vector3.zero,Vector3.one,Color.white);
                secondAnswerStroke=FlowShape("SecondStroke",PrimitiveType.Quad,tutorialAnswerMark,Vector3.zero,Vector3.one,Color.white);
                foreach(var part in new[]{tutorialAnswerMark,firstAnswerStroke,secondAnswerStroke})part.gameObject.layer=warningRoot.gameObject.layer;
            }
            if(markedTutorialTrial!=currentView.TrialId||markedTutorialCorrect!=correct)
            {
                markedTutorialTrial=currentView.TrialId;markedTutorialCorrect=correct;
                tutorialMarkStarted=Time.realtimeSinceStartupAsDouble;
                Color colour=correct?new Color(.31f,.94f,.65f):new Color(1f,.43f,.39f);
                SetAnswerStroke(firstAnswerStroke,correct?new Vector2(-.48f,.01f):new Vector2(-.40f,.40f),
                    correct?new Vector2(-.15f,-.32f):new Vector2(.40f,-.40f),colour);
                SetAnswerStroke(secondAnswerStroke,correct?new Vector2(-.21f,-.32f):new Vector2(.40f,.40f),
                    correct?new Vector2(.48f,.39f):new Vector2(-.40f,-.40f),colour);
            }
            // The whole symbol is visible immediately and stays visible after the pop.
            float progress=Mathf.Clamp01((float)(Time.realtimeSinceStartupAsDouble-tutorialMarkStarted)/.32f);
            float scale=Mathf.Lerp(.55f,1f,Mathf.SmoothStep(0,1,progress))+.12f*Mathf.Sin(progress*Mathf.PI);
            var area=HudBox(frustum,new Rect(.08f,.055f,.14f,.14f));
            tutorialAnswerMark.localPosition=new Vector3(area.center.x,area.center.y,0);
            tutorialAnswerMark.localScale=Vector3.one*(Mathf.Min(area.width,area.height)*scale);
            tutorialAnswerMark.gameObject.SetActive(true);
        }
        static void SetAnswerStroke(Transform stroke,Vector2 start,Vector2 end,Color colour)
        {
            Vector2 delta=end-start;
            stroke.localPosition=(start+end)*.5f;
            stroke.localRotation=Quaternion.Euler(0,0,Mathf.Atan2(delta.y,delta.x)*Mathf.Rad2Deg);
            stroke.localScale=new Vector3(delta.magnitude,.15f,1);
            stroke.GetComponent<Renderer>().sharedMaterial.color=colour;
        }
        static void FitBoardInstruction(TextMesh label,string value,float size,float width,float height)
        {
            if(label.text==value&&label.characterSize==size)return;
            label.text=value;label.characterSize=size;
            var bounds=label.GetComponent<Renderer>().localBounds.size;
            label.transform.localScale=Vector3.one*Mathf.Min(1f,width/Mathf.Max(bounds.x,.001f),height/Mathf.Max(bounds.y,.001f));
        }
        void RefreshTutorialInstructions()
        {
            if(!IsTutorialPanel||currentView.Phase!=Phase.TutorialFeedback||!currentView.TutorialCorrect.HasValue)
            {
                markedTutorialCorrect=null;
                if(tutorialAnswerMark!=null)tutorialAnswerMark.gameObject.SetActive(false);
            }
            bool show=viveTaskPreview&&role==Actor.Participant&&currentView!=null&&
                currentView.Phase==Phase.Tutorial&&currentView.TaskKind==TaskKind.Stroop;
            if(!show){if(tutorialBoardInstructions!=null)tutorialBoardInstructions.gameObject.SetActive(false);return;}
            if(tutorialBoardInstructions==null)
                tutorialBoardInstructions=Text("TutorialInstructions",contentRoot,BoardPosition+new Vector3(0,-.33f,-.065f),.016f,64,new Color(.93f,.97f,1f));
            tutorialBoardInstructions.gameObject.SetActive(true);
            FitBoardInstruction(tutorialBoardInstructions,currentView.ResponseRule==ResponseRule.WordMeaning?
                "Read the word. Ignore its ink.\nClick the matching trackpad colour.":
                "Use the ink colour. Ignore the word.\nClick the matching trackpad colour.",.016f,1.25f,.26f);
        }
    }
}
