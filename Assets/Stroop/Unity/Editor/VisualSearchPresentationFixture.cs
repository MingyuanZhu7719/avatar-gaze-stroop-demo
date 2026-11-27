#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Synthetic board and picking verification, plus actual camera renders to
    // PNG. This never starts XR, an eye source, a physical device or recording.
    public static class VisualSearchPresentationFixture
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        static int checks;
        static void Require(bool value,string detail){checks++;if(!value)throw new InvalidOperationException("Visual search presentation: "+detail);}
        static void Set(object target,string field,object value){target.GetType().GetProperty(field).SetValue(target,value,null);}
        static object Call(object target,string name,params object[] args)
        {return target.GetType().GetMethod(name,Private).Invoke(target,args);}
        static object Field(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
        static bool CuePending(WorldSpaceExperimentView view)
        {return (bool)typeof(WorldSpaceExperimentView).GetProperty("VisualSearchCuePending",Private).GetValue(view,null);}
        static TaskView View(VisualSearchTrial layout,string id,Phase phase=Phase.MainTask)
        {
            var view=new TaskView();Set(view,"Phase",phase);Set(view,"BlockIndex",0);Set(view,"TrialId",id);
            Set(view,"TaskKind",TaskKind.VisualSearch);Set(view,"VisualSearch",layout);
            Set(view,"Stimulus",new Stimulus(layout));return view;
        }
        static void Screenshot(Camera camera,string name)
        {
            var target=new RenderTexture(1600,1000,24,RenderTextureFormat.ARGB32);
            var pixels=new Texture2D(1600,1000,TextureFormat.RGB24,false);
            var prior=RenderTexture.active;var old=camera.targetTexture;
            try
            {
                camera.targetTexture=target;camera.Render();RenderTexture.active=target;
                pixels.ReadPixels(new Rect(0,0,1600,1000),0,0);pixels.Apply();
                string path=Path.Combine(Path.GetDirectoryName(Application.dataPath),name);
                File.WriteAllBytes(path,pixels.EncodeToPNG());Debug.Log("VISUAL_SEARCH_SCREENSHOT: "+path);
            }
            finally{camera.targetTexture=old;RenderTexture.active=prior;UnityEngine.Object.DestroyImmediate(pixels);UnityEngine.Object.DestroyImmediate(target);}
        }
        static Color32[] ReadPixels(Camera camera,RenderTexture target,Texture2D pixels)
        {
            camera.Render();RenderTexture.active=target;
            pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0);pixels.Apply();return pixels.GetPixels32();
        }
        static void RenderOperatorBoard(Camera camera,WorldSpaceExperimentView view,Transform root,VisualSearchTrial layout,bool array)
        {
            var target=new RenderTexture(1600,1000,24,RenderTextureFormat.ARGB32);
            var pixels=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
            var old=camera.targetTexture;var prior=RenderTexture.active;
            try
            {
                camera.targetTexture=target;
                var shown=ReadPixels(camera,target,pixels);
                string name="visual-search-operator-"+layout.SetSize+(array?"-array.png":"-target.png");
                string path=Path.Combine(Path.GetDirectoryName(Application.dataPath),name);
                File.WriteAllBytes(path,pixels.EncodeToPNG());Debug.Log("VISUAL_SEARCH_OPERATOR_SCREENSHOT: "+path);
                // Freeze the production layout only after the first actual
                // render. A differential render proves each required bag is
                // visible from the operator seat, not merely active in Unity.
                Call(view,"OnDisable");view.enabled=false;
                var bags=new List<Transform>{root.Find("TargetExample")};
                if(array)foreach(var item in layout.Items)bags.Add(root.Find("SearchSlot"+item.Slot));
                foreach(var bag in bags)
                {
                    bag.gameObject.SetActive(false);var hidden=ReadPixels(camera,target,pixels);int changed=0;
                    for(int i=0;i<shown.Length;i++)
                        if(Math.Abs(shown[i].r-hidden[i].r)+Math.Abs(shown[i].g-hidden[i].g)+Math.Abs(shown[i].b-hidden[i].b)>90)changed++;
                    bag.gameObject.SetActive(true);
                    Require(changed>=20,"operator cannot see "+bag.name+" in "+layout.SetSize+" items ("+changed+" pixels)");
                }
            }
            finally
            {
                view.enabled=true;Call(view,"OnDisable");Call(view,"OnEnable");camera.targetTexture=old;RenderTexture.active=prior;
                UnityEngine.Object.DestroyImmediate(pixels);UnityEngine.Object.DestroyImmediate(target);
            }
        }
        public static void ValidateOperator()
        {
            checks=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            Require(SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null,"operator render fixture requires graphics; omit -nographics");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var cameraObject=new GameObject("OperatorVisualSearchCamera");var viewObject=new GameObject("OperatorVisualSearchView");
            SceneManager.MoveGameObjectToScene(cameraObject,scene);SceneManager.MoveGameObjectToScene(viewObject,scene);
            try
            {
                var camera=cameraObject.AddComponent<Camera>();var view=viewObject.AddComponent<WorldSpaceExperimentView>();
                camera.transform.position=new Vector3(.2f,1.6f,-.1f);camera.fieldOfView=70;camera.aspect=1.6f;
                camera.cullingMask=1<<28;camera.nearClipPlane=.05f;camera.farClipPlane=20;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.09f,.12f,.15f);
                view.Initialize(camera,Actor.Operator);Call(view,"OnDisable");Call(view,"OnEnable");
                view.PlaceWozOperatorView(camera.transform.position);view.ConfigureWozOperatorHeadsetPresentation();
                view.SetFaults(new string[0]);view.SetPresentationAvailable(true);
                var plans=VisualSearchPlans.MainList(20261116,0);
                foreach(int count in new[]{12,24})
                {
                    VisualSearchTrial layout=null;foreach(var stimulus in plans)if(stimulus.VisualSearch.SetSize==count){layout=stimulus.VisualSearch;break;}
                    Require(layout!=null,"missing frozen "+count+"-item source layout");
                    foreach(bool array in new[]{false,true})
                    {
                        var snapshot=new TaskSnapshot("operator-search-fixture",0,0,"frozen-trial-"+count,null,null,
                            Phase.MainTask,Condition.A,TaskKind.VisualSearch,ResponseRule.InkColor,layout,array);
                        var task=ViveWozOperatorTaskView.FromFreshTask(snapshot);
                        Require(System.Object.ReferenceEquals(task.VisualSearch,layout)&&task.VisualSearchArrayPresented==array,"adapter regenerated layout or lost participant stage");
                        view.ApplyTaskView(task);
                        foreach(var child in viewObject.GetComponentsInChildren<Transform>(true))child.gameObject.layer=28;
                        var root=view.transform.Find("WorldSpaceContent/VisualSearchBoard");
                        Require(root!=null&&root.gameObject.activeInHierarchy&&!CuePending(view),"operator invented a local cue countdown or hid current task");
                        Require(System.Object.ReferenceEquals(Field(view,"shownVisualSearch"),layout),"operator did not retain exact received layout");
                        Require(root.Find("SearchCueMessage").gameObject.activeSelf==!array,"operator stage differs from participant stage");
                        Require(root.Find("SearchControls").GetComponent<TextMesh>().text.Contains("View only"),"operator controls invite answering");
                        Require(root.Find("SearchTargetName").GetComponent<TextMesh>().text==layout.TargetLabel,"target label differs from received layout");
                        var expected=new HashSet<int>();foreach(var item in layout.Items)expected.Add(item.Slot);
                        for(int slot=0;slot<24;slot++)
                            Require(root.Find("SearchSlot"+slot).gameObject.activeSelf==(array&&expected.Contains(slot)),"wrong active operator slot "+slot);
                        foreach(var item in layout.Items)
                        {
                            var bag=root.Find("SearchSlot"+item.Slot);
                            Require(Vector3.Distance(bag.localPosition,WorldSpaceExperimentView.VisualSearchSlotPosition(item.Slot))<.00001f,"received slot moved");
                            foreach(VisualSearchPattern pattern in Enum.GetValues(typeof(VisualSearchPattern)))
                                Require(bag.Find("Pattern"+pattern).gameObject.activeSelf==(pattern==item.Pattern),"received pattern changed");
                            int index;string id;Vector3 hit;var aim=bag.position-camera.transform.position;
                            Require(!view.TryHitVisualSearch(camera.transform.position,aim,out index,out id,out hit),"operator acquired a participant picker");
                            view.SetVisualSearchPointer(camera.transform.position,aim,true);
                            Require(!bag.Find("HoverOutline").gameObject.activeSelf,"operator created a hover outline");
                        }
                        Require(!view.transform.Find("WorldSpaceContent/VisualSearchPointer").gameObject.activeSelf,"operator pointer became visible");
                        Require(Quaternion.Angle(root.rotation,view.transform.Find("WorldSpaceContent/Stimulus").rotation)<.01f,"search board does not face operator writing surface");
                        camera.transform.LookAt(root.position);RenderOperatorBoard(camera,view,root,layout,array);
                        if(array)
                        {
                            var previous=root.position;var shift=new Vector3(.3f,-.2f,.1f);
                            camera.transform.position+=shift;view.PlaceWozOperatorView(camera.transform.position);
                            Require(Vector3.Distance(root.position,previous+shift)<.0001f&&
                                Quaternion.Angle(root.rotation,view.transform.Find("WorldSpaceContent/Stimulus").rotation)<.01f,
                                "explicit view re-placement did not preserve search alignment");
                            view.ApplyTaskView(task);Require(!CuePending(view)&&root.Find("SearchSlot"+layout.Items[0].Slot).gameObject.activeSelf,
                                "repeated task update restarted a local cue or regenerated array");
                        }
                    }
                }
                view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(null));
                Require(!view.transform.Find("WorldSpaceContent/VisualSearchBoard").gameObject.activeSelf,"expired task retained an operator search layout");
            }
            finally
            {
                if(viewObject!=null){var view=viewObject.GetComponent<WorldSpaceExperimentView>();if(view!=null)Call(view,"OnDisable");}
                UnityEngine.Object.DestroyImmediate(viewObject);UnityEngine.Object.DestroyImmediate(cameraObject);EditorSceneManager.CloseScene(scene,true);
            }
            Debug.Log("VISUAL_SEARCH_OPERATOR_FIXTURE_PASSED: "+checks+" checks; received 12/24 layouts, target/array stages, view-only controls and actual operator-seat renders. Synthetic mono only.");
        }
        public static void Validate()
        {
            checks=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            Require(VisualSearchTrial.Columns==6&&VisualSearchTrial.Rows==4,"recorded grid matches view geometry");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var cameraObject=new GameObject("VisualSearchFixtureCamera");var viewObject=new GameObject("VisualSearchFixtureView");
            SceneManager.MoveGameObjectToScene(cameraObject,scene);SceneManager.MoveGameObjectToScene(viewObject,scene);
            try
            {
                var camera=cameraObject.AddComponent<Camera>();var view=viewObject.AddComponent<WorldSpaceExperimentView>();
                view.Initialize(camera,Actor.Participant);
                // This is Edit Mode, so explicitly establish the production
                // MonoBehaviour's camera-event subscriptions that Play Mode
                // normally installs through OnEnable.
                Call(view,"OnDisable");Call(view,"OnEnable");
                view.ConfigureViveDevelopmentPreview(new Dictionary<ViveTrackpadDirection,Colour>{
                    {ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}},.5);
                camera.transform.position=WorldSpaceExperimentView.CenteredParticipantReference;
                camera.transform.LookAt(WorldSpaceExperimentView.TaskDisplayPosition);
                camera.fieldOfView=43;camera.aspect=1.6f;camera.cullingMask=1<<28;camera.nearClipPlane=.05f;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.09f,.12f,.15f);
                view.SetFaults(new string[0]);view.SetPresentationAvailable(true);
                var plans=VisualSearchPlans.MainList(20261116,0);
                foreach(int count in new[]{12,24})
                {
                    VisualSearchTrial layout=null;foreach(var stimulus in plans)if(stimulus.VisualSearch.SetSize==count){layout=stimulus.VisualSearch;break;}
                    Require(layout!=null,"both set sizes generated");var task=View(layout,"visual-search-fixture-"+count);view.ApplyTaskView(task);
                    // The saved isolation anchor contains its own classroom.
                    // Render only this additive fixture's geometry, never the
                    // anchor's coincident boards or development HUD panels.
                    foreach(var child in viewObject.GetComponentsInChildren<Transform>(true))child.gameObject.layer=28;
                    foreach(var child in cameraObject.GetComponentsInChildren<Transform>(true))child.gameObject.layer=28;
                    var root=view.transform.Find("WorldSpaceContent/VisualSearchBoard");Require(root!=null&&root.gameObject.activeInHierarchy,"board visible");
                    Require(!view.transform.Find("WorldSpaceContent/Stimulus").gameObject.activeSelf,"Stroop word hidden");
                    Require(!view.transform.Find("WorldSpaceContent/ViveControllerLegend").gameObject.activeSelf,"colour mapping legend hidden");
                    Require(root.Find("TargetExample").gameObject.activeInHierarchy,"target example present");
                    Require(CuePending(view),"new trial begins with target-only cue");
                    for(int slot=0;slot<24;slot++)Require(!root.Find("SearchSlot"+slot).gameObject.activeSelf,"cue hides searchable items");
                    int cueIndex;string cueId;Vector3 cuePoint;
                    Require(!view.TryHitVisualSearch(camera.transform.position,Vector3.forward,out cueIndex,out cueId,out cuePoint),"cue cannot be selected");
                    Screenshot(camera,"visual-search-"+count+"-target-preview.png");
                    var cueTimer=System.Diagnostics.Stopwatch.StartNew();
                    while(CuePending(view)&&cueTimer.Elapsed.TotalSeconds<4)
                    {camera.Render();System.Threading.Thread.Sleep(30);}
                    Require(!CuePending(view)&&cueTimer.Elapsed.TotalSeconds>=VisualSearchTrial.TargetPreviewSeconds,"actual cue render lasts configured duration");
                    camera.Render();
                    int active=0;for(int slot=0;slot<24;slot++)if(root.Find("SearchSlot"+slot).gameObject.activeSelf)active++;
                    Require(active==count,"exact object count");
                    foreach(var item in layout.Items)
                    {
                        int index;string id;Vector3 hit;Vector3 at=root.TransformPoint(WorldSpaceExperimentView.VisualSearchSlotPosition(item.Slot));
                        Require(view.TryHitVisualSearch(camera.transform.position,at-camera.transform.position,out index,out id,out hit)&&id==item.Id,"every object centre is selectable");
                    }
                    var first=layout.Items[0];var aim=root.TransformPoint(WorldSpaceExperimentView.VisualSearchSlotPosition(first.Slot));
                    view.SetVisualSearchPointer(camera.transform.position,aim-camera.transform.position,true);
                    Require(root.Find("SearchSlot"+first.Slot+"/HoverOutline").gameObject.activeSelf,"any hovered object gets outline");
                    Require(root.Find("TargetExample/HoverOutline").gameObject.activeSelf==false,"cue is not selectable");
                    int unused;string noId;Vector3 noHit;
                    var blank=root.TransformPoint(new Vector3(-.41f,.21f,-.02f));
                    Require(!view.TryHitVisualSearch(camera.transform.position,blank-camera.transform.position,out unused,out noId,out noHit),"gap does not select nearest object");
                    Require(!view.TryHitVisualSearch(camera.transform.position,Vector3.back,out unused,out noId,out noHit),"reverse ray rejected");
                    Screenshot(camera,"visual-search-"+count+"-preview.png");

                    // A raw input update masks the board. Without a completed
                    // trial observation its admitted-only picker still rejects.
                    Call(view,"SuppressViveRawStimulus");
                    Require(!view.TryHitVisualSearch(camera.transform.position,aim-camera.transform.position,out unused,out noId,out noHit),"ordinary hidden picker rejects");
                    long revision=(long)Field(view,"presentationRevision");
                    object[] args={camera.transform.position,aim-camera.transform.position,task.TrialId,layout.LayoutId,revision,-1,null,Vector3.zero};
                    Require(!(bool)Call(view,"TryHitAdmittedVisualSearch",args),"unrendered hidden trial rejects");
                    view.ApplyTaskView(task);int acknowledgments=0;
                    Action<string,double> acknowledged=(id,time)=>{if(id==task.TrialId)acknowledgments++;};
                    view.StimulusRendered+=acknowledged;
                    int witnessCount=((System.Collections.IList)Field(view,"visualSearchRequired")).Count;
                    Call(view,"BeforeCameraCull",camera);
                    for(int witness=0;witness<witnessCount-1;witness++)Call(view,"ObserveVisualSearchRender",camera,witness);
                    Call(view,"AfterCameraRender",camera);
                    Require(acknowledgments==0,"missing object or pattern renderer cannot acknowledge array");
                    camera.Render();
                    Require(acknowledgments==1,"actual camera render observes every cue and object pattern");
                    Call(view,"BeforeCameraCull",camera);
                    for(int witness=0;witness<witnessCount;witness++)Call(view,"ObserveVisualSearchRender",camera,witness);
                    Call(view,"AfterCameraRender",camera);
                    Require(acknowledgments==1,"complete synthetic renderer pass acknowledges exact array once");
                    view.StimulusRendered-=acknowledged;Call(view,"SuppressViveRawStimulus");
                    Require((bool)Call(view,"TryHitAdmittedVisualSearch",args)&&(string)args[6]==first.Id,"exact previously observed masked trial can be picked");
                    args[4]=revision+1;Require(!(bool)Call(view,"TryHitAdmittedVisualSearch",args),"wrong admitted revision rejects");
                    args[4]=revision;args[3]="other-layout";Require(!(bool)Call(view,"TryHitAdmittedVisualSearch",args),"wrong admitted layout rejects");
                    view.ApplyTaskView(View(layout,task.TrialId,Phase.Paused));
                    Require(!root.gameObject.activeSelf,"pause hides array");
                    Require(!(bool)Call(view,"TryHitAdmittedVisualSearch",args),"paused phase rejects selection");
                }
            }
            finally
            {
                if(viewObject!=null){var view=viewObject.GetComponent<WorldSpaceExperimentView>();if(view!=null)Call(view,"OnDisable");}
                UnityEngine.Object.DestroyImmediate(viewObject);UnityEngine.Object.DestroyImmediate(cameraObject);EditorSceneManager.CloseScene(scene,true);
            }
            Debug.Log("VISUAL_SEARCH_PRESENTATION_FIXTURE_PASSED: "+checks+" checks; synthetic geometry and input only, no headset acceptance.");
        }
    }
}
#endif
