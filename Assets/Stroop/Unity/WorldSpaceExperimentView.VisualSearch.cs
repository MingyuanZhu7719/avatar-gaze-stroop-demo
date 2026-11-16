using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class WorldSpaceExperimentView
    {
        // Fixed board geometry for both set sizes. A larger set never shrinks
        // an object or its non-overlapping selection region.
        public const string VisualSearchGeometryVersion=VisualSearchTrial.GeometryVersion;
        public const float VisualSearchColumnStep=(float)VisualSearchTrial.ColumnStepMetres,VisualSearchRowStep=(float)VisualSearchTrial.RowStepMetres;
        public const float VisualSearchHitWidth=(float)VisualSearchTrial.HitWidthMetres,VisualSearchHitHeight=(float)VisualSearchTrial.HitHeightMetres;
        const float VisualSearchFaceZ=-.020f;
        Transform visualSearchRoot,visualSearchPointerRoot,visualSearchReticle;
        TextMesh visualSearchInstruction,visualSearchTargetLabel,visualSearchControls,visualSearchCueMessage;
        LineRenderer visualSearchRay;
        VisualSearchBag[] visualSearchBags;
        VisualSearchBag visualSearchCue;
        VisualSearchTrial shownVisualSearch;
        string capturedVisualSearchLayout;
        readonly List<Renderer> visualSearchRequired=new List<Renderer>();
        readonly List<Color> visualSearchRequiredColours=new List<Color>();
        readonly HashSet<int> visualSearchObserved=new HashSet<int>();
        int visualSearchHover=-1;
        string visualSearchCueTrial,visualSearchCueCapturedTrial;
        int visualSearchCueCaptureFrame=-1,visualSearchCueRequiredCount;
        long visualSearchCueCaptureRevision;
        double visualSearchCueElapsed,visualSearchCueLastRender=-1,visualSearchCueFirstRender=-1;
        bool visualSearchRawArrayHeld;
        readonly HashSet<int> visualSearchCueObserved=new HashSet<int>();
        internal bool VisualSearchCuePending {get{return IsVisualSearchTrial&&shownVisualSearch!=null&&
            currentView.TrialId==visualSearchCueTrial&&visualSearchCueElapsed<VisualSearchTrial.TargetPreviewSeconds;}}
        internal double VisualSearchCueFirstRenderSeconds {get{return visualSearchCueFirstRender;}}
        internal double VisualSearchCueElapsedSeconds {get{return visualSearchCueElapsed;}}

        sealed class VisualSearchBag
        {
            internal Transform Root,Hover;
            internal Renderer Body,Pocket;
            internal Transform[] Patterns;
            internal Renderer[][] PatternRenderers;
        }
        bool IsVisualSearchTrial {get{return currentView!=null&&currentView.TaskKind==TaskKind.VisualSearch&&
            currentView.VisualSearch!=null&&(currentView.Phase==Phase.Practice||currentView.Phase==Phase.MainTask);}}
        static string TaskInstructions(TaskView view)
        {return view.TaskKind==TaskKind.VisualSearch?"Find the matching backpack.\nPoint at it and pull the trigger.":ResponseRuleInstructions.Participant(view.ResponseRule);}

        public static Vector3 VisualSearchSlotPosition(int slot)
        {
            if(slot<0||slot>=24)throw new ArgumentOutOfRangeException("slot");
            return new Vector3((slot%6-2.5f)*VisualSearchColumnStep,.21f-(slot/6)*VisualSearchRowStep,VisualSearchFaceZ);
        }
        static Color SearchColour(Colour value)
        {
            switch(value){case Colour.Red:return new Color(.86f,.19f,.19f);case Colour.Green:return new Color(.18f,.64f,.34f);
                case Colour.Blue:return new Color(.18f,.40f,.88f);case Colour.Yellow:return new Color(.95f,.74f,.17f);
                default:throw new ArgumentOutOfRangeException("value");}
        }
        void EnsureVisualSearchLayout()
        {
            if(visualSearchRoot!=null)return;
            visualSearchRoot=Child("VisualSearchBoard",contentRoot,TaskDisplayPosition+new Vector3(0,0,-.012f));
            Primitive("SearchBoardBacking",PrimitiveType.Cube,visualSearchRoot,new Vector3(0,0,.012f),
                new Vector3(1.34f,1.02f,.008f),new Color(.93f,.94f,.91f));
            visualSearchInstruction=Text("SearchInstruction",visualSearchRoot,new Vector3(-.13f,.456f,-.03f),.0070f,64,new Color(.10f,.14f,.18f));
            visualSearchInstruction.text="FIND THIS BACKPACK";
            visualSearchTargetLabel=Text("SearchTargetName",visualSearchRoot,new Vector3(-.13f,.386f,-.03f),.0058f,64,new Color(.10f,.14f,.18f));
            visualSearchCue=BuildSearchBag("TargetExample",visualSearchRoot,new Vector3(.49f,.405f,-.02f));
            visualSearchCue.Root.localScale=Vector3.one*.88f;
            visualSearchControls=Text("SearchControls",visualSearchRoot,new Vector3(0,-.466f,-.03f),.0062f,64,new Color(.12f,.16f,.20f));
            visualSearchControls.text="Point at a backpack, then pull the trigger.";
            visualSearchCueMessage=Text("SearchCueMessage",visualSearchRoot,new Vector3(0,-.04f,-.03f),.009f,64,new Color(.12f,.16f,.20f));
            visualSearchCueMessage.text="Look at the target above.\nThe search starts shortly.";
            visualSearchBags=new VisualSearchBag[24];
            for(int i=0;i<24;i++)visualSearchBags[i]=BuildSearchBag("SearchSlot"+i,visualSearchRoot,VisualSearchSlotPosition(i));
            visualSearchPointerRoot=Child("VisualSearchPointer",contentRoot,Vector3.zero);
            visualSearchRay=visualSearchPointerRoot.gameObject.AddComponent<LineRenderer>();
            visualSearchRay.useWorldSpace=true;visualSearchRay.positionCount=2;
            visualSearchRay.startWidth=.0022f;visualSearchRay.endWidth=.0022f;
            visualSearchRay.sharedMaterial=NewMaterial(Shader.Find("Unlit/Color"),new Color(.14f,.68f,.85f));
            visualSearchRay.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
            visualSearchReticle=Primitive("PointerTip",PrimitiveType.Sphere,visualSearchPointerRoot,Vector3.zero,
                Vector3.one*.010f,new Color(.05f,.32f,.42f)).transform;
            visualSearchRoot.gameObject.SetActive(false);visualSearchPointerRoot.gameObject.SetActive(false);
        }
        VisualSearchBag BuildSearchBag(string name,Transform parent,Vector3 position)
        {
            var bag=new VisualSearchBag{Root=Child(name,parent,position),Patterns=new Transform[4],PatternRenderers=new Renderer[4][]};
            var dark=new Color(.12f,.15f,.18f);
            Primitive("Handle",PrimitiveType.Cube,bag.Root,new Vector3(0,.062f,.003f),new Vector3(.053f,.018f,.009f),dark);
            Primitive("LeftStrap",PrimitiveType.Cube,bag.Root,new Vector3(-.052f,-.006f,.004f),new Vector3(.018f,.097f,.009f),dark);
            Primitive("RightStrap",PrimitiveType.Cube,bag.Root,new Vector3(.052f,-.006f,.004f),new Vector3(.018f,.097f,.009f),dark);
            Primitive("BodyOutline",PrimitiveType.Cube,bag.Root,new Vector3(0,-.004f,0),new Vector3(.108f,.128f,.012f),dark);
            bag.Body=Primitive("BodyColour",PrimitiveType.Cube,bag.Root,new Vector3(0,-.003f,-.008f),new Vector3(.098f,.116f,.008f),Color.white).GetComponent<Renderer>();
            bag.Pocket=Primitive("Pocket",PrimitiveType.Cube,bag.Root,new Vector3(0,-.040f,-.014f),new Vector3(.079f,.035f,.006f),Color.white).GetComponent<Renderer>();
            Primitive("PocketZip",PrimitiveType.Cube,bag.Root,new Vector3(0,-.022f,-.019f),new Vector3(.073f,.004f,.004f),dark);
            for(int p=0;p<4;p++)bag.Patterns[p]=Child("Pattern"+(VisualSearchPattern)p,bag.Root,Vector3.zero);
            for(int stripe=0;stripe<3;stripe++)Primitive("Stripe"+stripe,PrimitiveType.Cube,bag.Patterns[(int)VisualSearchPattern.Striped],
                new Vector3(0,.033f-stripe*.018f,-.016f),new Vector3(.081f,.007f,.004f),Color.white);
            for(int i=0;i<4;i++)Primitive("Dot"+i,PrimitiveType.Sphere,bag.Patterns[(int)VisualSearchPattern.Dotted],
                new Vector3((i%2==0?-.021f:.021f),i<2?.029f:.004f,-.017f),new Vector3(.014f,.014f,.004f),Color.white);
            Primitive("CrossHorizontal",PrimitiveType.Cube,bag.Patterns[(int)VisualSearchPattern.Cross],new Vector3(0,.019f,-.016f),new Vector3(.064f,.012f,.004f),Color.white);
            Primitive("CrossVertical",PrimitiveType.Cube,bag.Patterns[(int)VisualSearchPattern.Cross],new Vector3(0,.019f,-.016f),new Vector3(.012f,.053f,.004f),Color.white);
            for(int p=0;p<4;p++)bag.PatternRenderers[p]=bag.Patterns[p].GetComponentsInChildren<Renderer>(true);
            bag.Hover=Child("HoverOutline",bag.Root,Vector3.zero);
            var highlight=new Color(.02f,.31f,.46f);
            foreach(int side in new[]{-1,1})
            {
                Primitive("Vertical"+side,PrimitiveType.Cube,bag.Hover,new Vector3(side*VisualSearchHitWidth*.5f,0,-.025f),
                    new Vector3(.004f,VisualSearchHitHeight,.003f),highlight);
                Primitive("Horizontal"+side,PrimitiveType.Cube,bag.Hover,new Vector3(0,side*VisualSearchHitHeight*.5f,-.025f),
                    new Vector3(VisualSearchHitWidth,.004f,.003f),highlight);
            }
            bag.Hover.gameObject.SetActive(false);return bag;
        }
        void ConfigureSearchBag(VisualSearchBag bag,Colour colour,VisualSearchPattern pattern)
        {
            Color c=SearchColour(colour);bag.Body.sharedMaterial.color=c;bag.Pocket.sharedMaterial.color=c*.78f;
            for(int i=0;i<4;i++)bag.Patterns[i].gameObject.SetActive(i==(int)pattern);
            var detail=colour==Colour.Yellow?new Color(.16f,.19f,.20f):new Color(.97f,.98f,.96f);
            foreach(var renderer in bag.PatternRenderers[(int)pattern])renderer.sharedMaterial.color=detail;
            bag.Hover.gameObject.SetActive(false);
        }
        void AddSearchWitness(Renderer renderer)
        {
            int index=visualSearchRequired.Count;visualSearchRequired.Add(renderer);
            visualSearchRequiredColours.Add(renderer is MeshRenderer&&renderer.GetComponent<TextMesh>()!=null?renderer.GetComponent<TextMesh>().color:renderer.sharedMaterial.color);
            var witness=renderer.GetComponent<VisualSearchRenderWitness>();
            if(witness==null)witness=renderer.gameObject.AddComponent<VisualSearchRenderWitness>();
            witness.Bind(this,index);
        }
        void AddBagWitnesses(VisualSearchBag bag,VisualSearchPattern pattern)
        {AddSearchWitness(bag.Body);foreach(var renderer in bag.PatternRenderers[(int)pattern])AddSearchWitness(renderer);}
        void ApplyVisualSearchView(TaskView view,bool show)
        {
            if(view.TaskKind!=TaskKind.VisualSearch||view.VisualSearch==null)
            {shownVisualSearch=null;SuppressVisualSearchStimulus();return;}
            EnsureVisualSearchLayout();
            var trial=view.VisualSearch;
            if(!object.ReferenceEquals(shownVisualSearch,trial)||visualSearchCueTrial!=view.TrialId)
            {
                shownVisualSearch=trial;visualSearchRequired.Clear();visualSearchRequiredColours.Clear();
                visualSearchCueTrial=view.TrialId;visualSearchCueElapsed=0;visualSearchCueLastRender=visualSearchCueFirstRender=-1;
                foreach(var bag in visualSearchBags)bag.Root.gameObject.SetActive(false);
                ConfigureSearchBag(visualSearchCue,trial.TargetColour,trial.TargetPattern);
                visualSearchTargetLabel.text=trial.TargetLabel;
                AddSearchWitness(visualSearchInstruction.GetComponent<Renderer>());AddSearchWitness(visualSearchTargetLabel.GetComponent<Renderer>());
                AddBagWitnesses(visualSearchCue,trial.TargetPattern);
                visualSearchCueRequiredCount=visualSearchRequired.Count;
                foreach(var item in trial.Items)
                {
                    var bag=visualSearchBags[item.Slot];bag.Root.gameObject.SetActive(true);
                    ConfigureSearchBag(bag,item.Colour,item.Pattern);AddBagWitnesses(bag,item.Pattern);
                }
                visualSearchHover=-1;
            }
            visualSearchRawArrayHeld=!show;
            RefreshVisualSearchArrayVisibility();
            visualSearchRoot.gameObject.SetActive(show);
            if(!show)SetVisualSearchPointer(Vector3.zero,Vector3.forward,false);
        }
        void SuppressVisualSearchStimulus()
        {
            if(visualSearchRoot!=null)visualSearchRoot.gameObject.SetActive(false);
            if(visualSearchPointerRoot!=null)visualSearchPointerRoot.gameObject.SetActive(false);
            SetSearchHover(-1);
        }
        bool VisualSearchMeshesAvailable()
        {
            if(!IsVisualSearchTrial||VisualSearchCuePending||visualSearchRawArrayHeld||visualSearchRoot==null||!visualSearchRoot.gameObject.activeInHierarchy||
                !object.ReferenceEquals(shownVisualSearch,currentView.VisualSearch)||visualSearchRequired.Count==0)return false;
            foreach(var renderer in visualSearchRequired)if(renderer==null||!renderer.enabled||!renderer.gameObject.activeInHierarchy)return false;
            return true;
        }
        internal bool TryGetViveRawVisualSearchIdentity(string trialId,string layoutId,out long revision)
        {
            revision=-1;
            if(!viveTaskPreview||role!=Actor.Participant||!IsVisualSearchTrial||visualSearchRoot==null||visualSearchRoot.gameObject.activeSelf||
                contentRoot==null||!contentRoot.gameObject.activeInHierarchy||String.IsNullOrEmpty(trialId)||
                !String.Equals(displayedTrial,trialId,StringComparison.Ordinal)||!String.Equals(currentView.TrialId,trialId,StringComparison.Ordinal)||
                !String.Equals(shownVisualSearch.LayoutId,layoutId,StringComparison.Ordinal)||
                !object.ReferenceEquals(shownVisualSearch,currentView.VisualSearch))return false;
            revision=presentationRevision;return revision>=0;
        }
        internal bool TryRevealViveRawVisualSearchTrial(string trialId,string layoutId,long expectedRevision)
        {
            long revision;if(!TryGetViveRawVisualSearchIdentity(trialId,layoutId,out revision)||revision!=expectedRevision)return false;
            if(VisualSearchCuePending)return false;
            visualSearchRawArrayHeld=false;RefreshVisualSearchArrayVisibility();
            visualSearchRoot.gameObject.SetActive(true);return VisualSearchMeshesAvailable()&&presentationRevision==expectedRevision;
        }
        internal bool TryRevealVisualSearchCue(string trialId,string layoutId)
        {
            if(!VisualSearchCuePending||!viveTaskPreview||role!=Actor.Participant||visualSearchRoot==null||
                currentView.TrialId!=trialId||shownVisualSearch.LayoutId!=layoutId||displayedTrial!=trialId)return false;
            visualSearchRawArrayHeld=true;RefreshVisualSearchArrayVisibility();visualSearchRoot.gameObject.SetActive(true);return true;
        }
        void RefreshVisualSearchArrayVisibility()
        {
            if(shownVisualSearch==null||visualSearchBags==null)return;
            bool cue=VisualSearchCuePending;
            foreach(var item in shownVisualSearch.Items)visualSearchBags[item.Slot].Root.gameObject.SetActive(!cue&&!visualSearchRawArrayHeld);
            visualSearchCueMessage.gameObject.SetActive(cue);
            visualSearchControls.text=cue?"Look at the example. Selection begins with the search array.":"Point at a backpack, then pull the trigger.";
            if(cue)SetSearchHover(-1);
        }
        void PrepareVisualSearchCueCapture(Camera camera)
        {
            visualSearchCueCaptureFrame=-1;visualSearchCueObserved.Clear();
            RefreshVisualSearchArrayVisibility();
            if(!VisualSearchCuePending||camera!=presentationCamera||camera==null||!camera.isActiveAndEnabled||
                camera.targetTexture!=null||camera.cullingMask==0||!PresentationIsAvailable()||hasPresentationFault||!isActiveAndEnabled||
                visualSearchRoot==null||!visualSearchRoot.gameObject.activeInHierarchy)return;
            visualSearchCueCaptureFrame=Time.frameCount;visualSearchCueCaptureRevision=presentationRevision;visualSearchCueCapturedTrial=currentView.TrialId;
        }
        void FinishVisualSearchCueCapture(Camera camera)
        {
            if(!VisualSearchCuePending||camera!=presentationCamera||visualSearchCueCaptureFrame!=Time.frameCount||
                visualSearchCueCaptureRevision!=presentationRevision||visualSearchCueCapturedTrial!=currentView.TrialId||
                visualSearchCueObserved.Count!=visualSearchCueRequiredCount||camera.targetTexture!=null||!PresentationIsAvailable()||
                hasPresentationFault||visualSearchRoot==null||!visualSearchRoot.gameObject.activeInHierarchy)return;
            double now=Time.realtimeSinceStartupAsDouble;
            if(visualSearchCueFirstRender<0)visualSearchCueFirstRender=now;
            if(visualSearchCueLastRender>=0)
            {
                double delta=now-visualSearchCueLastRender;
                visualSearchCueElapsed=delta>=0&&delta<=.25?visualSearchCueElapsed+delta:0;
            }
            visualSearchCueLastRender=now;
        }
        void PrepareVisualSearchRenderCapture()
        {if(IsVisualSearchTrial&&VisualSearchMeshesAvailable())capturedVisualSearchLayout=shownVisualSearch.LayoutId;}
        void ResetVisualSearchRenderCapture()
        {capturedVisualSearchLayout=null;visualSearchObserved.Clear();}
        internal void ObserveVisualSearchRender(Camera camera,int index)
        {
            if(VisualSearchCuePending&&camera==presentationCamera&&visualSearchCueCaptureFrame==Time.frameCount&&
                visualSearchCueCaptureRevision==presentationRevision&&visualSearchCueCapturedTrial==currentView.TrialId&&
                index>=0&&index<visualSearchCueRequiredCount)visualSearchCueObserved.Add(index);
            if(camera==presentationCamera&&capturedFrame==Time.frameCount&&capturedTrial==displayedTrial&&
                capturedRevision==presentationRevision&&capturedVisualSearchLayout!=null&&IsVisualSearchTrial&&
                visualSearchRoot!=null&&visualSearchRoot.gameObject.activeInHierarchy&&index>=0&&index<visualSearchRequired.Count)
                visualSearchObserved.Add(index);
        }
        bool VisualSearchObservedByCamera()
        {return capturedVisualSearchLayout!=null&&visualSearchRequired.Count>0&&visualSearchObserved.Count==visualSearchRequired.Count;}
        bool VisualSearchCaptureStable()
        {
            if(!VisualSearchMeshesAvailable()||!VisualSearchObservedByCamera()||
                !String.Equals(capturedVisualSearchLayout,shownVisualSearch.LayoutId,StringComparison.Ordinal)||
                visualSearchTargetLabel.text!=shownVisualSearch.TargetLabel)return false;
            for(int i=0;i<visualSearchRequired.Count;i++)
            {
                var renderer=visualSearchRequired[i];var text=renderer.GetComponent<TextMesh>();
                if(!(text==null?renderer.sharedMaterial.color:text.color).Equals(visualSearchRequiredColours[i]))return false;
            }
            return true;
        }
        static bool FiniteVector(Vector3 value)
        {return !(Single.IsNaN(value.x)||Single.IsNaN(value.y)||Single.IsNaN(value.z)||Single.IsInfinity(value.x)||Single.IsInfinity(value.y)||Single.IsInfinity(value.z));}
        bool VisualSearchPointerPermitted()
        {return IsVisualSearchTrial&&VisualSearchMeshesAvailable()&&PresentationIsAvailable()&&!hasPresentationFault&&
            !faultCoversStimulus&&role==Actor.Participant&&isActiveAndEnabled;}
        public bool TryHitVisualSearch(Vector3 worldOrigin,Vector3 worldDirection,out int itemIndex,out string itemId,out Vector3 hitPoint)
        {
            itemIndex=-1;itemId=null;hitPoint=worldOrigin;
            if(!VisualSearchPointerPermitted())return false;
            return TryHitVisualSearchGeometry(worldOrigin,worldDirection,out itemIndex,out itemId,out hitPoint);
        }
        // During a raw input update the mesh is intentionally masked before
        // task mutation. The owning recorder must additionally validate its
        // committed render receipt and current controller frame. This overload
        // checks the exact previously camera-observed trial/layout revision;
        // it never reveals a mesh or grants a recording/input capability.
        internal bool TryHitAdmittedVisualSearch(Vector3 worldOrigin,Vector3 worldDirection,string trialId,string layoutId,long revision,
            out int itemIndex,out string itemId,out Vector3 hitPoint)
        {
            itemIndex=-1;itemId=null;hitPoint=worldOrigin;
            if(!viveTaskPreview||role!=Actor.Participant||!IsVisualSearchTrial||VisualSearchCuePending||visualSearchRoot==null||
                !isActiveAndEnabled||currentView.TrialId!=trialId||displayedTrial!=trialId||presentationRevision!=revision||
                !acknowledgedTrials.Contains(trialId)||shownVisualSearch.LayoutId!=layoutId||
                !object.ReferenceEquals(shownVisualSearch,currentView.VisualSearch))return false;
            return TryHitVisualSearchGeometry(worldOrigin,worldDirection,out itemIndex,out itemId,out hitPoint);
        }
        bool TryHitVisualSearchGeometry(Vector3 worldOrigin,Vector3 worldDirection,out int itemIndex,out string itemId,out Vector3 hitPoint)
        {
            itemIndex=-1;itemId=null;hitPoint=worldOrigin;
            if(!FiniteVector(worldOrigin)||!FiniteVector(worldDirection)||worldDirection.sqrMagnitude<.00001f)return false;
            var origin=visualSearchRoot.InverseTransformPoint(worldOrigin);
            var direction=visualSearchRoot.InverseTransformVector(worldDirection.normalized);
            if(direction.z<=.00001f)return false;
            float distance=(VisualSearchFaceZ-.026f-origin.z)/direction.z;
            if(distance<=0||distance>10)return false;
            var point=origin+direction*distance;hitPoint=visualSearchRoot.TransformPoint(point);
            for(int i=0;i<shownVisualSearch.Items.Count;i++)
            {
                var item=shownVisualSearch.Items[i];var centre=VisualSearchSlotPosition(item.Slot);
                if(Mathf.Abs(point.x-centre.x)<=VisualSearchHitWidth*.5f&&Mathf.Abs(point.y-centre.y)<=VisualSearchHitHeight*.5f)
                {itemIndex=i;itemId=item.Id;return true;}
            }
            return false;
        }
        public void SetVisualSearchPointer(Vector3 worldOrigin,Vector3 worldDirection,bool visible)
        {
            if(visualSearchPointerRoot==null)return;
            visible=visible&&VisualSearchPointerPermitted()&&FiniteVector(worldOrigin)&&FiniteVector(worldDirection)&&worldDirection.sqrMagnitude>.00001f;
            visualSearchPointerRoot.gameObject.SetActive(visible);
            if(!visible){SetSearchHover(-1);return;}
            int index;string id;Vector3 hit;
            bool found=TryHitVisualSearch(worldOrigin,worldDirection,out index,out id,out hit);
            // The endpoint follows the board plane even over blank space. No
            // correct-target attraction, snapping or answer-dependent styling.
            var direction=worldDirection.normalized;
            if(Vector3.Distance(worldOrigin,hit)<.001f)hit=worldOrigin+direction*2;
            visualSearchRay.SetPosition(0,worldOrigin);visualSearchRay.SetPosition(1,hit);
            visualSearchReticle.position=hit-direction*.003f;
            SetSearchHover(found?shownVisualSearch.Items[index].Slot:-1);
        }
        void SetSearchHover(int slot)
        {
            if(visualSearchHover==slot)return;
            if(visualSearchBags!=null)
            {
                if(visualSearchHover>=0&&visualSearchHover<24)visualSearchBags[visualSearchHover].Hover.gameObject.SetActive(false);
                if(slot>=0&&slot<24)visualSearchBags[slot].Hover.gameObject.SetActive(true);
            }
            visualSearchHover=slot;
        }
    }
}
