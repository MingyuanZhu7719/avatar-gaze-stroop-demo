using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class WorldSpaceExperimentView
    {
        bool wozOperatorHeadset;
        Transform wozOperatorInstructionsBacking;
        string wozOperatorInstructions="Use the desktop to connect the participant.";
        readonly Dictionary<TextMesh,string> wozOperatorWarmedText=new Dictionary<TextMesh,string>();

        // The operator is also wearing a headset. It must not inherit the broad,
        // edge-aligned desktop HUD or the participant's whiteboard menu.
        public void ConfigureWozOperatorHeadsetPresentation()
        {
            RequireLayout();
            if(role!=Actor.Operator)throw new InvalidOperationException("Operator presentation required.");
            wozOperatorHeadset=true;
            EnsureHudLayout();
            if(wozOperatorInstructionsBacking==null)
            {
                var backing=Primitive("OperatorInstructionsBacking",PrimitiveType.Quad,warningRoot,Vector3.zero,Vector3.one,Color.black);
                var material=NewMaterial(Shader.Find("AvatarStroop/WorldSpaceOverlay"),new Color(.025f,.025f,.035f,.94f));
                material.renderQueue=4990;backing.GetComponent<MeshRenderer>().sharedMaterial=material;
                wozOperatorInstructionsBacking=backing.transform;
            }
            LayoutHud();
        }

        // Display only: this does not start acquisition, connect a peer or alter
        // task readiness. The owner bootstrap supplies its current next action.
        public void SetWozOperatorHeadsetStatus(string instructions)
        {
            if(!wozOperatorHeadset)return;
            wozOperatorInstructions=instructions??"";
        }

        void FitWozOperatorLabel(TextMesh label,string value,Rect frustum,Rect area,float lineHeight,int lines)
        {
            if(label==null)return;
            value=value??"";
            string previous;
            if(!wozOperatorWarmedText.TryGetValue(label,out previous)||previous!=value)
            {
                // This player creates its TextMeshes at runtime. Populate the
                // atlas before the first bounds-based fit, including characters
                // that did not exist in the initial empty mesh.
                label.font.RequestCharactersInTexture(value,label.fontSize,label.fontStyle);
                label.GetComponent<MeshRenderer>().sharedMaterial.mainTexture=label.font.material.mainTexture;
                wozOperatorWarmedText[label]=value;
            }
            label.gameObject.SetActive(true);
            FitHud(label,value,frustum,area,lineHeight,lines);
        }

        void LayoutWozOperatorHud(Rect frustum)
        {
            bool live=currentView!=null&&currentView.Phase==Phase.MainTask;
            string phase=currentView==null||currentView.Phase==Phase.Preparation?"Setup":
                currentView.Phase==Phase.MainTask?(currentView.TaskKind==TaskKind.VisualSearch&&!currentView.VisualSearchArrayPresented?
                    "Participant is viewing the target":"Participant is answering"):
                currentView.Phase==Phase.Questionnaire?"Participant questionnaire":
                currentView.Phase==Phase.Countdown?"Block starting":
                currentView.Phase==Phase.Rest?"Between blocks":
                currentView.Phase==Phase.Ended?"Session ended":currentView.Phase.ToString();
            if(currentView!=null&&currentView.BlockIndex>=0)phase="Block "+(currentView.BlockIndex+1)+" | "+phase;

            foreach(var unused in new[]{provisionalText,avatarLabel,previewStatusText,previewInputText})
                if(unused!=null){unused.text="";unused.GetComponent<MeshRenderer>().enabled=false;}
            if(phaseAnimationRoot!=null)phaseAnimationRoot.gameObject.SetActive(false);
            if(worldMenuBacking!=null)worldMenuBacking.gameObject.SetActive(false);

            FitWozOperatorLabel(developmentBanner,live?"":"WoZ Operator",frustum,new Rect(.05f,.025f,.90f,.09f),.055f,1);
            FitWozOperatorLabel(phaseText,live?"":phase,frustum,new Rect(.05f,.13f,.90f,.09f),.047f,2);
            // Keep the participant's head and gaze unobscured during a trial.
            FitWozOperatorLabel(messageText,live?"":wozOperatorInstructions,frustum,new Rect(.07f,.31f,.86f,.34f),.060f,6);
            PositionBacking(wozOperatorInstructionsBacking,frustum,new Rect(.02f,.27f,.96f,.42f),!live&&!String.IsNullOrWhiteSpace(wozOperatorInstructions));
            string footer=hasPresentationFault?faultCaption:live?phase+"\n"+wozOperatorInstructions:"Ready to test the operator view.";
            FitWozOperatorLabel(faultText,footer,frustum,new Rect(.05f,.77f,.90f,.19f),.037f,4);
            PositionBacking(headerBacking,frustum,new Rect(0,0,1,.24f),!live&&
                (developmentBanner.GetComponent<MeshRenderer>().enabled||phaseText.GetComponent<MeshRenderer>().enabled));
            PositionBacking(footerBacking,frustum,new Rect(0,.74f,1,.26f),faultText.GetComponent<MeshRenderer>().enabled);
        }
    }
}
