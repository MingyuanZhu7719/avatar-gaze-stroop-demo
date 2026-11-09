#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Synthetic Edit Mode check of the actual configuration and cleanup path.
    // This never starts XR, reads a device, calls BeginEye, or opens files.
    public static class VivePreSessionChoiceFixture
    {
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Pre-session fixture: "+message);}
        static FieldInfo Field(object instance,string name)
        {
            var field=instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null)throw new InvalidOperationException("Missing fixture field: "+name);
            return field;
        }
        static object Get(object instance,string name){return Field(instance,name).GetValue(instance);}
        static void Set(object instance,string name,object value){Field(instance,name).SetValue(instance,value);}
        static void Call(object instance,string name,params object[] args)
        {
            var method=Array.Find(instance.GetType().GetMethods(BindingFlags.Instance|BindingFlags.NonPublic),
                candidate=>candidate.Name==name&&candidate.GetParameters().Length==args.Length);
            if(method==null)throw new InvalidOperationException("Missing fixture method: "+name);
            method.Invoke(instance,args);
        }
        static GameObject Root(string name,Scene scene)
        {
            var result=new GameObject(name);SceneManager.MoveGameObjectToScene(result,scene);
            Require(result.scene==scene,"fixture object must stay in the isolated scene");
            result.hideFlags=HideFlags.HideAndDontSave;return result;
        }
        static string Step(object choice)
        {
            var property=choice.GetType().GetProperty("Current",BindingFlags.Instance|BindingFlags.NonPublic);
            if(property==null)throw new InvalidOperationException("Missing fixture choice step");
            return property.GetValue(choice,null).ToString();
        }
        static object ConfigureRequest(ViveStroopDevelopmentBootstrap bootstrap,
            ViveStroopPreviewRig rig,WorldSpaceExperimentView view)
        {
            Set(bootstrap,"selectedDesign",3);Set(bootstrap,"namedPlanCount",0);
            ((int[])Get(bootstrap,"namedPlanOptions"))[0]=0;((int[])Get(bootstrap,"namedPlanLists"))[0]=0;
            Set(bootstrap,"selectedHand",0);
            Require(typeof(ViveStroopDevelopmentBootstrap).GetField("thresholdText",BindingFlags.Instance|BindingFlags.NonPublic)==null,
                "startup must not require or retain a threshold input");
            Require(ViveFixedTrackpadMapping.SectorThreshold==0.5,"the VIVE sector threshold must remain fixed at 0.5");
            var mappingMethod=typeof(ViveStroopDevelopmentBootstrap).GetMethod("ConfiguredTrackpadMapping",BindingFlags.Instance|BindingFlags.NonPublic);
            Require(mappingMethod!=null,"configured VIVE mapping is unavailable");
            var mapping=(IDictionary<ViveTrackpadDirection,Colour>)mappingMethod.Invoke(bootstrap,null);
            Require(mapping.Count==4&&mapping[ViveTrackpadDirection.Up]==Colour.Red&&mapping[ViveTrackpadDirection.Right]==Colour.Green&&
                mapping[ViveTrackpadDirection.Down]==Colour.Blue&&mapping[ViveTrackpadDirection.Left]==Colour.Yellow,
                "the fixed mapping must be available without a colour selection");
            Set(bootstrap,"namedIntentV3",true);Set(bootstrap,"namedTaskRecordingChoice",true);Set(bootstrap,"namedEyeRecordingChoice",true);
            Call(bootstrap,"TryConfigure");
            var choice=Get(bootstrap,"preSessionChoice");
            Require(choice!=null&&Step(choice)=="Task","actual requested configuration must open the task rehearsal");
            Require(!bootstrap.Configured&&!bootstrap.RecordingEnabled&&Get(bootstrap,"participant")==null&&Get(bootstrap,"sharedClock")==null,
                "the requested rehearsal must not create a session or enable recording");
            Require(Get(bootstrap,"device")==null&&Get(rig,"worker")==null&&Get(rig,"eyePump")==null&&Get(rig,"source")==null,
                "configuration must not start XR input or an eye worker");
            var text=(string)Get(view,"previewDiagnostic");var controls=(string)Get(view,"previewControls");
            Require(text.Contains("Input rehearsal only")&&text.Contains("not consent or permission")&&text.Contains("No recording or session will start")&&
                text.Contains("task-event")&&controls.Contains("rehearse choice"),"task prompt must describe rehearsal and its limits");
            // Synthetic input to the pure gate checks the second real UI prompt;
            // it does not simulate a physical controller or display witness.
            Call(choice,"Observe",true,true,false,false,false,false);Call(choice,"Observe",true,true,true,false,false,false);
            Call(bootstrap,"RenderPreSessionChoice");text=(string)Get(view,"previewDiagnostic");
            Require(Step(choice)=="Eye"&&text.Contains("eye recording")&&text.Contains("not consent or permission")&&
                text.Contains("No recording or session will start"),"eye prompt must describe rehearsal and its limits");
            return choice;
        }
        public static void Validate()
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            Scene fixture=default(Scene);bool opened=false;
            GameObject cameraObject=null,originObject=null,viewObject=null,rigObject=null,bootstrapObject=null;
            try
            {
                fixture=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                opened=fixture.IsValid()&&fixture.isLoaded;Require(opened,"cannot create an isolated scene");
                cameraObject=Root("SyntheticPreSessionCamera",fixture);var camera=cameraObject.AddComponent<Camera>();
                originObject=Root("SyntheticPreSessionOrigin",fixture);var offset=new GameObject("SyntheticOffset");offset.transform.SetParent(originObject.transform,false);
                viewObject=Root("SyntheticPreSessionView",fixture);var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                rigObject=Root("SyntheticPreSessionRig",fixture);var rig=rigObject.AddComponent<ViveStroopPreviewRig>();rig.Configure(camera,originObject.transform,offset.transform,view);
                bootstrapObject=Root("SyntheticPreSessionBootstrap",fixture);var bootstrap=bootstrapObject.AddComponent<ViveStroopDevelopmentBootstrap>();bootstrap.Configure(rig,view);rig.ConfigureDevelopment(bootstrap);
                // Runtime internals stay internal across Unity's Editor assembly boundary.
                var runtimeAssembly=typeof(ViveStroopDevelopmentBootstrap).Assembly;
                var policy=runtimeAssembly.GetType("AvatarStroop.Runtime.ViveControlledRecordingBuildPolicy",true);
                var availability=policy.GetProperty("Available",BindingFlags.Static|BindingFlags.NonPublic);
                Require(availability!=null&&!(bool)availability.GetValue(null,null),"Editor must never enable controlled recording");
                var notice=typeof(ViveStroopDevelopmentBootstrap).GetMethod("ControlledRecordingRequestAllowed",BindingFlags.Static|BindingFlags.NonPublic);
                Require(notice!=null&&!(bool)notice.Invoke(null,new object[]{true,false})&&
                    (bool)notice.Invoke(null,new object[]{true,true})&&
                    (bool)notice.Invoke(null,new object[]{false,false}),"controlled notice eligibility mismatch");
                Require(!(bool)Get(bootstrap,"controlledRecordingNoticeAccepted"),"owner notice must default to unaccepted");
                var setupType=runtimeAssembly.GetType("AvatarStroop.Runtime.ViveNamedRecordingSetup",true);
                var prepare=setupType.GetMethod("TryPrepare",BindingFlags.Static|BindingFlags.NonPublic);
                Require(prepare!=null,"pure synthetic setup entry unavailable");
                object[] setupArgs={true,System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.CurrentDirectory),"synthetic-not-opened"),"32",new Func<string,bool>(path=>true),null};
                var localChoice=prepare.Invoke(null,setupArgs);
                Require(localChoice!=null,"pure synthetic choice setup unavailable");
                Set(bootstrap,"recordingSetup",localChoice);Call(bootstrap,"RenderPreSessionChoice");
                Require(((string)Get(view,"previewDiagnostic")).Contains("Local development self-test only")&&
                    ((string)Get(view,"previewDiagnostic")).Contains("questionnaire"),"task notice missing scope");
                var choiceClock=new MonotonicClock("synthetic-notice-clock");
                Call(localChoice,"Observe",true,true,false,false,false,false,choiceClock);
                Call(localChoice,"Observe",true,true,true,false,false,false,choiceClock);Call(bootstrap,"RenderPreSessionChoice");
                Require(((string)Get(view,"previewDiagnostic")).Contains("real eye")&&
                    ((string)Get(view,"previewDiagnostic")).Contains("Withdrawal does not erase"),"eye notice missing privacy boundary");
                Set(bootstrap,"recordingSetup",null);
                Require(Get(rig,"worker")==null&&Get(bootstrap,"participant")==null,"notice fixture started hardware or task");
                var choice=ConfigureRequest(bootstrap,rig,view);
                Call(bootstrap,"OnApplicationFocus",false);
                Require(Step(choice)=="Cancelled","focus loss must cancel the rehearsal");
                Require(Get(bootstrap,"participant")==null&&Get(rig,"worker")==null,"focus loss must not create a task or worker");

                // The owner can explicitly clear a stopped rehearsal. Resetting
                // just this fixture field creates a new independent attempt.
                Set(bootstrap,"preSessionChoice",null);
                choice=ConfigureRequest(bootstrap,rig,view);
                bootstrap.AfterXrStop();
                Require(Step(choice)=="Cancelled","XR stop must cancel the rehearsal");
                Require(Get(bootstrap,"participant")==null&&Get(rig,"worker")==null,"stop must not create a task or worker");
                // Exercise normal named configuration without supplying a threshold.
                Set(bootstrap,"preSessionChoice",null);Set(bootstrap,"namedIntentV3",false);
                Set(bootstrap,"selectedDesign",2);Set(bootstrap,"namedListIndex",0);
                Call(bootstrap,"TryConfigure");
                Require(bootstrap.Configured,"normal preview must configure without a threshold setting");
                var participant=Get(bootstrap,"participant");
                Require(participant!=null&&(double)Get(Get(participant,"trackpad"),"threshold")==0.5,
                    "actual task input must use the fixed 0.5 threshold");
                Require((double)Get(Get(bootstrap,"questionnaireGate"),"threshold")==0.5,
                    "actual questionnaire navigation must use the same fixed threshold");
                Require(!bootstrap.RecordingEnabled&&Get(bootstrap,"device")==null&&Get(rig,"worker")==null,
                    "normal configuration must not start a device or recording");
                Debug.Log("VIVE_PRE_SESSION_SYNTHETIC_FIXTURE_PASSED: actual configuration without threshold input, fixed 0.5 task/questionnaire, prompt state, focus and stop; no XR, device, recording or research consent.");
            }
            finally
            {
                if(bootstrapObject!=null)UnityEngine.Object.DestroyImmediate(bootstrapObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);
                if(originObject!=null)UnityEngine.Object.DestroyImmediate(originObject);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(opened&&fixture.IsValid()&&fixture.isLoaded)Require(EditorSceneManager.CloseScene(fixture,true),"cannot close fixture scene");
            }
        }
    }
}
#endif
