#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Synthetic owner-flow checks only. No IMGUI event, XR device, eye client,
    // socket, file writer or study participant is started by this fixture.
    public static class ViveOwnerFlowFixture
    {
        static int assertions;
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Owner flow fixture: "+message);assertions++;}
        static FieldInfo Field(object target,string name)
        {
            var field=target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null)throw new InvalidOperationException("Owner fixture field missing: "+name);
            return field;
        }
        static object Get(object target,string name){return Field(target,name).GetValue(target);}
        static void Set(object target,string name,object value){Field(target,name).SetValue(target,value);}
        static object Call(object target,string name,params object[] args)
        {
            var method=Array.Find(target.GetType().GetMethods(BindingFlags.Instance|BindingFlags.NonPublic),
                value=>value.Name==name&&value.GetParameters().Length==args.Length);
            if(method==null)throw new InvalidOperationException("Owner fixture method missing: "+name);
            return method.Invoke(target,args);
        }
        static GameObject Root(string name,Scene scene)
        {
            var value=new GameObject(name);SceneManager.MoveGameObjectToScene(value,scene);
            value.hideFlags=HideFlags.HideAndDontSave;return value;
        }
        static void NoHardwareOrRecording(ViveStroopDevelopmentBootstrap bootstrap,ViveStroopPreviewRig rig)
        {
            Require(Get(bootstrap,"device")==null,"configuration must not start controller input");
            Require(Get(rig,"worker")==null&&Get(rig,"source")==null&&Get(rig,"eyePump")==null,
                "configuration must not start the eye source");
            Require(!rig.ViewRunning&&!bootstrap.RecordingEnabled&&Get(bootstrap,"namedRawPump")==null&&Get(bootstrap,"namedRawDestination")==null,
                "configuration must not start XR or recording/output");
        }
        public static void Validate()
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            Scene fixture=default(Scene);bool opened=false;
            GameObject cameraObject=null,originObject=null,viewObject=null,rigObject=null,bootstrapObject=null;
            try
            {
                fixture=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                opened=fixture.IsValid()&&fixture.isLoaded;Require(opened,"isolated scene unavailable");
                cameraObject=Root("SyntheticOwnerCamera",fixture);var camera=cameraObject.AddComponent<Camera>();
                originObject=Root("SyntheticOwnerOrigin",fixture);var offset=new GameObject("SyntheticOffset");offset.transform.SetParent(originObject.transform,false);
                viewObject=Root("SyntheticOwnerView",fixture);var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                rigObject=Root("SyntheticOwnerRig",fixture);var rig=rigObject.AddComponent<ViveStroopPreviewRig>();rig.Configure(camera,originObject.transform,offset.transform,view);
                bootstrapObject=Root("SyntheticOwnerBootstrap",fixture);var bootstrap=bootstrapObject.AddComponent<ViveStroopDevelopmentBootstrap>();bootstrap.Configure(rig,view);rig.ConfigureDevelopment(bootstrap);

                string[] transient={"controlledRecordingNoticeAccepted","observationOptIn","legacyWozRecordingSelected","ownerPreviewWoz",
                    "namedIntentV3","namedTaskRecordingChoice","namedEyeRecordingChoice","ownerAdvanced","ownerStorageLimits","ownerOutputOptions"};
                foreach(int mode in new[]{1,0})
                {
                    foreach(string field in transient)Set(bootstrap,field,true);
                    Set(bootstrap,"selectedDesign",3);
                    Call(bootstrap,"SetOwnerRunMode",mode);
                    Require((int)Get(bootstrap,"ownerRunMode")==mode&&(int)Get(bootstrap,"selectedDesign")==-1,"mode change must require a fresh design");
                    foreach(string field in transient)Require(!(bool)Get(bootstrap,field),"mode change retained hidden state: "+field);
                    NoHardwareOrRecording(bootstrap,rig);
                }
                // A setup click without a design must not create a task or sources.
                Call(bootstrap,"TryConfigure");
                Require(!bootstrap.Configured&&Get(bootstrap,"participant")==null,"missing design accepted");
                NoHardwareOrRecording(bootstrap,rig);
                Set(bootstrap,"selectedDesign",2);Set(bootstrap,"namedListIndex",0);
                Call(bootstrap,"TryConfigure");
                Require(bootstrap.Configured,"explicit no-avatar preview rejected");
                NoHardwareOrRecording(bootstrap,rig);
                var preview=(ViveDevelopmentParticipant)Get(bootstrap,"participant");
                Require(((string)Call(bootstrap,"NextOwnerAction")).Contains("start the classroom"),"stopped prepared preview needs a start-view action");
                preview.ParticipantWithdraw();
                string ended=(string)Call(bootstrap,"NextOwnerAction");
                Require(preview.Phase==Phase.Ended&&ended.StartsWith("Preview ended.",StringComparison.Ordinal)&&ended.Contains("No files were saved"),
                    "ended preview must not ask to start XR or repair input");

                // A synthetic intent is enough to test owner status precedence;
                // it grants no recording admission and creates no pump or output.
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"synthetic-owner-geometry");
                var choice=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,0,null);
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-owner-plan",choice);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-owner-intent",true,true);
                var configuration=SessionConfiguration.NamedRecordingDevelopment("synthetic-owner-task",37,plan,intent);
                var raw=new ViveDevelopmentParticipant(configuration,new MonotonicClock("synthetic-owner-clock"),geometry,
                    ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold,true);
                Set(bootstrap,"participant",raw);Set(bootstrap,"rawInstallFailed",true);
                raw.ParticipantWithdraw();
                ended=(string)Call(bootstrap,"NextOwnerAction");
                Require(raw.Phase==Phase.Ended&&ended.StartsWith("Task ended.",StringComparison.Ordinal)&&ended.Contains("unconfirmed"),
                    "ended recording intent must show output review without a save claim even when blocked");
                NoHardwareOrRecording(bootstrap,rig);
                Debug.Log("VIVE_OWNER_FLOW_SYNTHETIC_FIXTURE_PASSED: "+assertions+" assertions; actual preview configuration, no-source/no-output gates, ended preview and recording-intent status. No XR, native eye SDK, files or headset validation.");
            }
            finally
            {
                if(bootstrapObject!=null)UnityEngine.Object.DestroyImmediate(bootstrapObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);
                if(originObject!=null)UnityEngine.Object.DestroyImmediate(originObject);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(opened&&fixture.IsValid()&&fixture.isLoaded)Require(EditorSceneManager.CloseScene(fixture,true),"isolated scene could not close");
            }
        }
    }
}
#endif
