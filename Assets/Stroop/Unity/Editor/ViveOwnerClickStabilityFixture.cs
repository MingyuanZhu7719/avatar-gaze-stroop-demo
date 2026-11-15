#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Drives the production owner desktop, including real IMGUI mouse capture.
    // Synthetic display diagnostics do not poll devices or grant recording authority.
    public static class ViveOwnerClickStabilityFixture
    {
        const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        static int assertions,stage,scenario,lastPaint;
        static double deadline;
        static Scene scene;
        static GameObject ownerObject,rigObject,viewObject,cameraObject;
        static ViveOwnerClickStabilityWindow window;
        static ViveDevelopmentParticipant task;
        static Vector2 downPoint;
        static int capturedControl;
        sealed class Clock:IClock {public double Value;public double Now{get{return Value;}}public string ClockId{get{return "synthetic-owner-click-clock";}}}
        internal static object Get(object o,string n){return o.GetType().GetField(n,I).GetValue(o);}
        internal static void Set(object o,string n,object v){o.GetType().GetField(n,I).SetValue(o,v);}
        internal static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,I).Invoke(o,a);}
        static void Require(bool value,string why){if(!value)throw new InvalidOperationException("Owner click stability: "+why);assertions++;}
        static void Neutral(){task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));}
        static void ResetTask()
        {
            var clock=new Clock();
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"synthetic-owner-click-geometry");
            SessionConfiguration configuration;
            if(scenario<3)
            {
                var choice=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,0,null);
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-owner-click-plan",choice);
                configuration=SessionConfiguration.NamedPlanDevelopment("synthetic-owner-click-session",47,plan);
            }
            else
            {
                var allocation=new Allocation("synthetic-owner-click-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                configuration=new SessionConfiguration("synthetic-owner-click-session",47,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            }
            task=new ViveDevelopmentParticipant(configuration,clock,geometry,
                ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold,true);
            Neutral();task.OwnerBeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {
                Neutral();var trial=task.CurrentTrial;task.MarkPresented(trial.TrialId);clock.Value+=.4;
                var colour=trial.Stimulus.Ink;double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0,y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));Neutral();
            }
            Require(task.Phase==Phase.BlockPreparation&&task.InputReady,"practice failed to reach a ready formal gate");
            Set(window.Owner,"participant",task);Set(window.Owner,"configured",true);
            Set(window.Owner,"rawInstallFailed",scenario==6);
            Set(window.Rig,"ownerScroll",Vector2.zero);
            window.DiagnosticReady=scenario%3!=2;window.ResetCapture=true;
        }
        public static void ValidateGui()
        {
            assertions=0;stage=0;scenario=0;
            Require(Application.isBatchMode&&!Application.isPlaying,"isolated batch Editor only");
            Require(SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null,"real IMGUI requires graphics");
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            ownerObject=ObjectInScene("SyntheticClickOwner");rigObject=ObjectInScene("SyntheticClickRig");
            viewObject=ObjectInScene("SyntheticClickView");cameraObject=ObjectInScene("SyntheticClickCamera");
            var owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();var rig=rigObject.AddComponent<ViveStroopPreviewRig>();
            var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(cameraObject.AddComponent<Camera>(),Actor.Participant);
            view.ConfigureViveDevelopmentPreview(ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold);
            owner.Configure(rig,view);Set(rig,"development",owner);
            // Display-only fixture state; no loader, worker, device or recording is installed.
            Set(rig,"running",true);Set(rig,"headReady",true);
            window=ScriptableObject.CreateInstance<ViveOwnerClickStabilityWindow>();window.Rig=rig;window.Owner=owner;
            window.minSize=new Vector2(600,700);window.maxSize=new Vector2(1400,1400);
            window.position=new Rect(30,30,1100,1100);window.titleContent=new GUIContent("Synthetic owner click stability");
            deadline=EditorApplication.timeSinceStartup+90;window.ShowUtility();EditorApplication.update+=Step;
        }
        static GameObject ObjectInScene(string name){var result=new GameObject(name);SceneManager.MoveGameObjectToScene(result,scene);return result;}
        static void Step()
        {
            try
            {
                Require(EditorApplication.timeSinceStartup<deadline,"IMGUI event timeout");
                if(window.Failure!=null)throw window.Failure;
                if(stage==0){ResetTask();lastPaint=window.Paint;stage=1;window.Repaint();return;}
                window.Repaint();if(window.Paint<=lastPaint)return;lastPaint=window.Paint;
                if(stage==1)
                {
                    Require(window.Rectangles.ContainsKey("formal-confirm")&&window.Enabled["formal-confirm"]==(scenario!=6),
                        "formal confirmation enabled state disagrees with the existing fault gate");
                    var rect=window.Rectangles["formal-confirm"];var viewport=window.Rectangles["desktop-scroll"];
                    Require(rect.yMin>=viewport.yMin&&rect.yMax<=viewport.yMax,"confirm is outside the visible viewport");
                    downPoint=rect.center-window.Origin;
                    window.SendEvent(new Event{type=EventType.MouseDown,button=0,mousePosition=downPoint});capturedControl=window.LastHotControl;
                    Require(scenario==6?capturedControl==0:capturedControl!=0,"MouseDown capture disagrees with the enabled state");
                    if(scenario%3!=0)window.DiagnosticReady=!window.DiagnosticReady;
                    stage=2;window.Repaint();return;
                }
                if(stage==2)
                {
                    // A normal click keeps the pointer still while background
                    // status updates can repaint between its down and up.
                    var targetPoint=window.Rectangles["formal-confirm"].center-window.Origin;
                    var upPoint=downPoint;
                    Require(Vector2.Distance(targetPoint,downPoint)<.1f,"live diagnostic reflow moved the pressed confirmation button");
                    window.SendEvent(new Event{type=EventType.MouseUp,button=0,mousePosition=upPoint});
                    Debug.Log("VIVE_OWNER_CLICK_RESULT: scenario="+scenario+" captured="+capturedControl+" afterUp="+window.LastHotControl+
                        " down="+downPoint+" targetAfterRepaint="+targetPoint+" up="+upPoint+" phase="+task.Phase);
                    Require(task.Phase==(scenario==6?Phase.BlockPreparation:Phase.BlockReady),
                        scenario==6?"disabled fault gate accepted a click":"stationary explicit confirmation was lost during a live diagnostic update");
                    if(scenario>=3&&scenario<6)Require(task.LockedControl==ControlSelection.Automatic,"Legacy Confirm Automatic did not lock Automatic");
                    Require(task.CurrentTrial==null&&Get(window.Owner,"namedRawPump")==null&&Get(window.Owner,"namedRawDestination")==null&&Get(window.Rig,"worker")==null,
                        "UI confirmation started a formal trial, recording or eye service");
                    if(++scenario<7){stage=0;return;}
                    Debug.Log("VIVE_OWNER_CLICK_STABILITY_FIXTURE_PASSED: "+assertions+" assertions; actual DrawOwnerDesktop stationary mouse clicks, Named plan and Legacy Automatic, both controller-diagnostic transitions, stable target rectangles and fault-disabled rejection. No formal trial, XR or recording.");
                    Finish(0);
                }
            }
            catch(Exception error){Debug.LogException(error);Finish(1);}
        }
        static void Finish(int code)
        {
            EditorApplication.update-=Step;
            if(window!=null){Set(window.Rig,"running",false);Set(window.Rig,"headReady",false);window.Close();window=null;}
            foreach(var obj in new[]{ownerObject,rigObject,viewObject,cameraObject})if(obj!=null)UnityEngine.Object.DestroyImmediate(obj);
            if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            EditorApplication.Exit(code);
        }
    }
    public sealed class ViveOwnerClickStabilityWindow:EditorWindow
    {
        internal ViveStroopPreviewRig Rig;
        internal ViveStroopDevelopmentBootstrap Owner;
        internal bool DiagnosticReady,ResetCapture;
        internal int Paint,LastHotControl;
        internal Vector2 Origin;
        internal Exception Failure;
        internal readonly Dictionary<string,Rect> Rectangles=new Dictionary<string,Rect>();
        internal readonly Dictionary<string,bool> Enabled=new Dictionary<string,bool>();
        void OnGUI()
        {
            if(Rig==null||Owner==null)return;
            try
            {
                if(ResetCapture){GUIUtility.hotControl=0;ResetCapture=false;}
                ViveOwnerClickStabilityFixture.Call(Owner,"RecordControllerUiPoll",true,DiagnosticReady,"Synthetic transient controller diagnostic.");
                if(Event.current.type==EventType.Repaint){Rectangles.Clear();Enabled.Clear();Origin=GUIUtility.GUIToScreenPoint(Vector2.zero);}
                ViveResponsiveOwnerLayoutFixture.SetObserver((key,rect)=>{Rectangles[key]=rect;Enabled[key]=GUI.enabled;});
                ViveOwnerClickStabilityFixture.Call(Rig,"DrawOwnerDesktop",position.width,position.height);
                LastHotControl=GUIUtility.hotControl;
                if(Event.current.type==EventType.Repaint)Paint++;
            }
            catch(Exception error){Failure=error;}
            finally{ViveResponsiveOwnerLayoutFixture.SetObserver(null);}
        }
    }
}
#endif
