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
        static bool inputEventOrder;
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
            if(!inputEventOrder&&scenario<3)
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
            Neutral();if(!inputEventOrder)task.OwnerBeginPractice(Actor.Owner);
            for(int i=0;i<(inputEventOrder?0:8);i++)
            {
                Neutral();var trial=task.CurrentTrial;task.MarkPresented(trial.TrialId);clock.Value+=.4;
                var colour=trial.Stimulus.Ink;double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0,y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));Neutral();
            }
            Require(task.Phase==(inputEventOrder?Phase.Preparation:Phase.BlockPreparation)&&task.InputReady,"task failed to reach its ready fixture phase");
            Set(window.Owner,"participant",task);Set(window.Owner,"configured",true);
            Set(window.Owner,"rawInstallFailed",!inputEventOrder&&scenario==6);
            if(inputEventOrder)
            {
                Set(window.Owner,"previewPracticeCountdownTask",null);Call(window.Owner,"ResetPracticeCountdown");
                Set(window.Owner,"ownerAdvanced",false);Set(window.Owner,"message","Synthetic ready task.");
            }
            Set(window.Rig,"ownerScroll",Vector2.zero);
            window.DiagnosticReady=inputEventOrder||scenario%3!=2;window.ResetCapture=true;
        }
        public static void ValidateGui(){Begin(false);}
        public static void ValidateInputEventOrderGui(){Begin(true);}
        static void Begin(bool eventOrder)
        {
            assertions=0;stage=0;scenario=0;inputEventOrder=eventOrder;
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
            if(inputEventOrder)
            {
                Set(rig,"previewCamera",cameraObject.GetComponent<Camera>());
                Set(rig,"SyntheticOwnerFocusForFixture",true);
            }
            window=ScriptableObject.CreateInstance<ViveOwnerClickStabilityWindow>();window.Rig=rig;window.Owner=owner;
            window.StaleMouseEvents=inputEventOrder;
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
                if(inputEventOrder){InputEventOrderStep();return;}
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
        static void InputEventOrderStep()
        {
            string key=scenario==0?"begin-practice":scenario==1?"advanced-task-tools":"desktop-close";
            Require(window.Rectangles.ContainsKey(key)&&window.Enabled[key],"ready paint omitted or disabled "+key);
            var rectangle=window.Rectangles[key];var point=rectangle.center-window.Origin;
            if(stage==1)
            {
                downPoint=point;window.SendEvent(new Event{type=EventType.MouseDown,button=0,mousePosition=downPoint});
                capturedControl=window.LastHotControl;
                Debug.Log("VIVE_OWNER_EVENT_ORDER_DOWN: target="+key+" stale="+window.MouseDiagnosticWasStale+" capture="+capturedControl);
                Require(window.MouseDiagnosticWasStale&&!window.MouseControllerWasReady,"mouse event incorrectly manufactured fresh controller authority");
                Require(capturedControl!=0,"ready painted "+key+" rejected MouseDown before the next controller poll");
                stage=2;window.Repaint();return;
            }
            Require(window.LastHotControl==capturedControl,"repaint lost the captured control for "+key);
            Require(Vector2.Distance(point,downPoint)<.1f,"fresh repaint moved "+key+" during the click");
            window.SendEvent(new Event{type=EventType.MouseUp,button=0,mousePosition=downPoint});
            Require(window.MouseDiagnosticWasStale&&!window.MouseControllerWasReady,"MouseUp incorrectly manufactured current input authority");
            Require(window.LastHotControl==0,"MouseUp retained a stale control ID for "+key);
            if(scenario==0)
            {
                Require(object.ReferenceEquals(Get(window.Owner,"previewPracticeCountdownTask"),task),"explicit click failed to capture practice intent");
                Require((int)Get(window.Owner,"practiceCountdownFrame")==-1&&task.Phase==Phase.Preparation&&task.CurrentTrial==null,
                    "desktop click started a countdown or trial without a subsequent current poll");
            }
            else if(scenario==1)Require((bool)Get(window.Owner,"ownerAdvanced"),"Advanced tools click was lost across event freshness changes");
            else Require((bool)Get(window.Rig,"quitRequested")&&!window.Rig.ViewRunning,"Close click was lost across event freshness changes");
            Require(Get(window.Owner,"device")==null&&Get(window.Owner,"namedRawPump")==null&&Get(window.Rig,"worker")==null,
                "GUI event fixture started a device, recording or eye source");
            Debug.Log("VIVE_OWNER_EVENT_ORDER_CLICK: target="+key+" phase="+task.Phase+" capture="+capturedControl+" released="+window.LastHotControl);
            if(++scenario<3){stage=0;return;}
            Debug.Log("VIVE_OWNER_INPUT_EVENT_ORDER_GUI_FIXTURE_PASSED: "+assertions+" assertions; actual production mouse events with fresh Layout/Repaint and previous-frame controller diagnostics on MouseDown/Up; practice intent only, Advanced toggle and Close. No XR, device or recording.");
            Finish(0);
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
        internal bool DiagnosticReady,ResetCapture,StaleMouseEvents,MouseDiagnosticWasStale,MouseControllerWasReady;
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
                bool mouse=Event.current.type==EventType.MouseDown||Event.current.type==EventType.MouseUp;
                if(!StaleMouseEvents||!mouse)
                    ViveOwnerClickStabilityFixture.Call(Owner,"RecordControllerUiPoll",true,DiagnosticReady,"Synthetic transient controller diagnostic.");
                else ViveOwnerClickStabilityFixture.Set(Owner,"inputDiagnosticFrame",Time.frameCount-1);
                if(mouse)
                {
                    MouseDiagnosticWasStale=(int)ViveOwnerClickStabilityFixture.Get(Owner,"inputDiagnosticFrame")!=Time.frameCount;
                    MouseControllerWasReady=(bool)Owner.GetType().GetProperty("ControllerUiReady",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Owner,null);
                }
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
