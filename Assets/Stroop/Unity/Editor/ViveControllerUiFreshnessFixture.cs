#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Display-only synthetic regression. No IMGUI, XR loader, Input System
    // device, controller/eye poll, participant recording, socket or file output.
    // Headset flags below are synthetic fields, never hardware evidence.
    public static class ViveControllerUiFreshnessFixture
    {
        static int assertions;
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Controller UI freshness fixture: "+message);assertions++;}
        static FieldInfo Field(object target,string name)
        {
            var field=target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null)throw new InvalidOperationException("Missing fixture field: "+name);
            return field;
        }
        static object Get(object target,string name){return Field(target,name).GetValue(target);}
        static void Set(object target,string name,object value){Field(target,name).SetValue(target,value);}
        static object Property(object target,string name)
        {
            var property=target.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            if(property==null)throw new InvalidOperationException("Missing fixture property: "+name);
            return property.GetValue(target,null);
        }
        static object Call(object target,string name,params object[] arguments)
        {
            var method=target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(method==null)throw new InvalidOperationException("Missing fixture method: "+name);
            return method.Invoke(target,arguments);
        }
        static GameObject Root(string name,Scene scene)
        {
            var root=new GameObject(name);SceneManager.MoveGameObjectToScene(root,scene);
            root.hideFlags=HideFlags.HideAndDontSave;return root;
        }
        static void HeadFlags(ViveStroopPreviewRig rig,bool ready)
        {Set(rig,"running",true);Set(rig,"headReady",ready);}
        static object Setup()
        {
            var type=typeof(ViveStroopDevelopmentBootstrap).Assembly.GetType("AvatarStroop.Runtime.ViveNamedRecordingSetup",true);
            var prepare=type.GetMethod("TryPrepare",BindingFlags.Static|BindingFlags.NonPublic);
            Require(prepare!=null,"pure recording-choice constructor unavailable");
            object[] arguments={true,@"C:\synthetic-controller-ui-not-opened","32",new Func<string,bool>(path=>true),null};
            var setup=prepare.Invoke(null,arguments);Require(setup!=null,"synthetic recording-choice fixture rejected");return setup;
        }
        static void NoSources(ViveStroopDevelopmentBootstrap owner,ViveStroopPreviewRig rig)
        {
            Require(Get(owner,"device")==null,"UI freshness must not create or poll a device adapter");
            Require(Get(rig,"worker")==null&&Get(rig,"eyePump")==null&&Get(rig,"source")==null,"UI freshness must not create an eye source");
            Require(Get(owner,"namedRawPump")==null&&Get(owner,"namedRawDestination")==null&&!owner.RecordingEnabled,"UI freshness must not start recording or output");
        }
        public static void Validate()
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            Scene scene=default(Scene);bool opened=false;
            GameObject cameraObject=null,originObject=null,viewObject=null,rigObject=null,ownerObject=null;
            ViveStroopPreviewRig rig=null;
            try
            {
                scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                opened=scene.IsValid()&&scene.isLoaded;Require(opened,"isolated scene unavailable");
                cameraObject=Root("SyntheticControllerUiCamera",scene);var camera=cameraObject.AddComponent<Camera>();
                originObject=Root("SyntheticControllerUiOrigin",scene);var offset=new GameObject("SyntheticOffset");offset.transform.SetParent(originObject.transform,false);
                viewObject=Root("SyntheticControllerUiView",scene);var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,AvatarStroop.Core.Actor.Participant);
                rigObject=Root("SyntheticControllerUiRig",scene);rig=rigObject.AddComponent<ViveStroopPreviewRig>();rig.Configure(camera,originObject.transform,offset.transform,view);
                ownerObject=Root("SyntheticControllerUiOwner",scene);var owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();owner.Configure(rig,view);rig.ConfigureDevelopment(owner);
                Set(owner,"selectedDesign",2);Set(owner,"namedListIndex",0);Call(owner,"TryConfigure");
                Require(owner.Configured,"explicit synthetic preview did not configure");
                var participant=(ViveDevelopmentParticipant)Get(owner,"participant");
                participant.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Require(participant.InputReady,"fixture must retain old model readiness to expose the original display bug");
                HeadFlags(rig,true);
                const string stale="OLD Right VIVE position/rotation/tracked all OK";
                Call(owner,"RecordControllerUiPoll",true,true,stale);
                Require((bool)Property(owner,"ControllerUiReady")&&(string)Property(owner,"ControllerUiState")=="ready","same-frame synthetic result must be displayable");

                var setup=Setup();Set(owner,"recordingSetup",setup);Set(owner,"recordingSetupDesign",3);
                HeadFlags(rig,false);owner.Poll(false);
                Require(participant.InputReady,"display invalidation must not rewrite task/model input state");
                Require(Property(setup,"Current").ToString()=="Cancelled","existing head-loss cancellation must remain effective");
                Require(!(bool)Property(owner,"ControllerUiReady")&&(string)Property(owner,"ControllerUiState")=="not checked","head-loss early return must not display cached ready");
                string diagnostic=(string)Property(owner,"ControllerUiDiagnostic");
                Require(diagnostic.Contains("headset tracking or focus is unavailable")&&!diagnostic.Contains("Right"),"head-loss early return leaked a prior controller diagnostic");
                NoSources(owner,rig);

                HeadFlags(rig,true);
                Require((string)Property(owner,"ControllerUiDiagnostic")=="Controller not checked in this update.","head recovery without a new controller poll must not report old head loss or cached controller status");
                Call(owner,"RecordControllerUiPoll",true,true,stale);owner.Poll(true);
                Require(!(bool)Property(owner,"ControllerUiReady")&&(string)Property(owner,"ControllerUiState")=="not checked","cancelled choice must not reuse readiness when head flags recover");
                Require((string)Property(owner,"ControllerUiDiagnostic")=="Controller not checked in this update.","cancelled-choice early return retained an old diagnostic");
                Require(Property(setup,"Current").ToString()=="Cancelled"&&participant.InputReady,"cancelled choice/model state unexpectedly changed");
                Set(owner,"configured",false);Call(owner,"RecordControllerUiPoll",true,true,stale);owner.Poll(true);
                Require((string)Property(owner,"ControllerUiState")=="not checked"&&!((string)Property(owner,"ControllerUiDiagnostic")).Contains("Right"),"unconfigured terminal choice bypassed invalidation");
                NoSources(owner,rig);

                Set(owner,"recordingSetup",null);Set(owner,"configured",true);
                Call(owner,"RecordControllerUiPoll",true,true,"Synthetic current result");
                Set(owner,"inputDiagnosticFrame",Time.frameCount-1);
                Require(!(bool)Property(owner,"ControllerUiReady")&&(string)Property(owner,"ControllerUiState")=="not checked","cross-frame result was displayed as current");
                Require((string)Property(owner,"ControllerUiDiagnostic")=="Controller not checked in this update.","cross-frame detailed status was not hidden");
                Require(((string)Call(owner,"NextOwnerAction")).Contains("restore headset tracking"),"stale task InputReady still advertised Begin practice");
                Call(owner,"RecordControllerUiPoll",true,false,"Synthetic current neutral wait");
                Require((string)Property(owner,"ControllerUiState")=="not ready"&&(string)Property(owner,"ControllerUiDiagnostic")=="Synthetic current neutral wait","current unavailable result must remain visible");
                Call(owner,"RecordControllerUiPoll",true,true,stale);HeadFlags(rig,false);
                Require((string)Property(owner,"ControllerUiState")=="not checked"&&!((string)Property(owner,"ControllerUiDiagnostic")).Contains("Right"),"head readiness loss in the same frame retained ready status");
                owner.AfterXrStop();
                Require((int)Get(owner,"inputDiagnosticFrame")==-1&&!(bool)Property(owner,"ControllerUiReady"),"view stop retained a current controller result");
                NoSources(owner,rig);Set(rig,"running",false);Set(rig,"headReady",false);
                Debug.Log("VIVE_CONTROLLER_UI_FRESHNESS_SYNTHETIC_FIXTURE_PASSED: "+assertions+" assertions; actual early returns, cancelled choices, stale model readiness, frame expiry and stop; no XR/device/eye polls or recording.");
            }
            finally
            {
                if(rig!=null){Set(rig,"running",false);Set(rig,"headReady",false);}
                if(ownerObject!=null)UnityEngine.Object.DestroyImmediate(ownerObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(originObject!=null)UnityEngine.Object.DestroyImmediate(originObject);
                if(opened&&scene.IsValid()&&scene.isLoaded)Require(EditorSceneManager.CloseScene(scene,true),"cannot close isolated scene");
            }
        }
    }
}
#endif
