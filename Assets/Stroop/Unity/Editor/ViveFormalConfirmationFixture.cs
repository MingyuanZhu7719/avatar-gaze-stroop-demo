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
    // No production-gate override. Real preview task confirms only after
    // Practice; raw positive execution is covered by the native writer fixture.
    public static class ViveFormalConfirmationFixture
    {
        const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        static int assertions;
        sealed class Clock:IClock {public double Value;public double Now{get{return Value;}}public string ClockId{get{return "synthetic-formal-clock";}}}
        static object Get(object o,string n){return o.GetType().GetField(n,I).GetValue(o);}
        static void Set(object o,string n,object v){o.GetType().GetField(n,I).SetValue(o,v);}
        static object Call(object o,string n,params object[] args){return o.GetType().GetMethod(n,I).Invoke(o,args);}
        static object CallOwnerStatic(string n,params object[] args)
        {return typeof(ViveStroopDevelopmentBootstrap).GetMethod(n,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Invoke(null,args);}
        static void Require(bool v,string why){if(!v)throw new InvalidOperationException("Formal confirmation: "+why);assertions++;}
        static ViveDevelopmentParticipant Task(Clock clock,out ViveNamedSimulationPlan plan)
        {
            var choice=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,0,null);
            plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-formal-plan",choice);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"synthetic-formal-geometry");
            return new ViveDevelopmentParticipant(SessionConfiguration.NamedPlanDevelopment("synthetic-formal-session",47,plan),clock,geometry,
                ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold,true);
        }
        static void Neutral(ViveDevelopmentParticipant task){task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));}
        static void PassPractice(ViveDevelopmentParticipant task,Clock clock)
        {
            Neutral(task);task.OwnerBeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {
                Neutral(task);var trial=task.CurrentTrial;task.MarkPresented(trial.TrialId);clock.Value+=.4;
                var c=trial.Stimulus.Ink;double x=c==Colour.Green?1:c==Colour.Yellow?-1:0,y=c==Colour.Red?1:c==Colour.Blue?-1:0;
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));Neutral(task);
            }
            Require(task.Phase==Phase.BlockPreparation,"Practice did not reach explicit formal gate");
        }
        static object Intent(ViveStroopDevelopmentBootstrap owner,ViveDevelopmentParticipant task,MonotonicClock clock,WorldSpaceExperimentView view)
        {
            var type=typeof(ViveStroopDevelopmentBootstrap).GetNestedType("FormalConfirmationRequest",BindingFlags.NonPublic);
            var r=Activator.CreateInstance(type,true);
            Set(r,"Task",task);Set(r,"Plan",task.NamedPlan);Set(r,"Context",null);Set(r,"Pump",null);
            Set(r,"Clock",clock);Set(r,"View",view);Set(r,"Block",task.BlockIndex);Set(r,"RequestedAt",clock.Now);
            Set(owner,"formalConfirmationRequest",r);return r;
        }
        static ViveStroopDeviceFrame Input(bool ready=true,bool neutralPending=false,bool trigger=false,bool menu=false,bool trackpad=false)
        {return (ViveStroopDeviceFrame)Activator.CreateInstance(typeof(ViveStroopDeviceFrame),I,null,
            new object[]{new VivePreviewTrackpadSample(ready,trackpad,0,0),neutralPending,false,trigger,false,menu,false,"synthetic formal input",false,false,false},null);}
        static string InputWait(ViveStroopDeviceFrame frame,bool ready)
        {return (string)CallOwnerStatic("FormalConfirmationInputWait",frame,ready);}
        static void ValidateDiagnostics(ViveStroopDevelopmentBootstrap owner,ViveDevelopmentParticipant task,object intent)
        {
            Require(InputWait(null,true)=="controller_not_polled","missing sample reason was hidden");
            Require(InputWait(Input(ready:false),true)=="controller_unavailable","lost device was described as a held control");
            Require(InputWait(Input(neutralPending:true),true)=="controller_neutral_pending","neutral arming reason was hidden");
            Require(InputWait(Input(),false)=="task_input_not_ready","task readiness wait was hidden");
            Require(InputWait(Input(trigger:true),true)=="trigger_held","held trigger did not prevent execution");
            Require(InputWait(Input(menu:true),true)=="menu_held","held menu did not prevent execution");
            Require(InputWait(Input(trackpad:true),true)=="trackpad_held","held trackpad did not prevent execution");
            Require(InputWait(Input(),true)==null,"neutral sample gained a new execution blocker");
            int waits=0,callbacks=0,refusals=0;
            Application.LogCallback capture=(message,stack,type)=>
            {if(message.StartsWith("VIVE_FORMAL_CONFIRM_WAITING:",StringComparison.Ordinal))waits++;
                if(message.StartsWith("VIVE_FORMAL_CONFIRM_CALLBACK:",StringComparison.Ordinal))callbacks++;
                if(message.StartsWith("VIVE_FORMAL_CONFIRM_NOT_REQUESTED:",StringComparison.Ordinal))refusals++;};
            Application.logMessageReceived+=capture;
            try
            {
                // Observe the same already-polled frame, including the path
                // that returns before Service while neutral arming is held.
                Call(owner,"ObserveFormalConfirmationInputs",Input(trigger:true));
                Call(owner,"ObserveFormalConfirmationInputs",Input(trigger:true));
                Require(waits==1&&(string)Get(intent,"WaitReason")=="trigger_held","stable input wait spammed logs or lost the held control");
                for(int i=0;i<100;i++)Call(owner,"ObserveFormalConfirmationInputs",i%2==0?Input(menu:true):Input(trackpad:true));
                Call(owner,"ReportFormalConfirmationWait",intent,"panel_not_witnessed");
                Require(waits==32&&(int)Get(intent,"WaitLogCount")==32,"request wait diagnostics were not bounded");
                Require((string)Get(intent,"WaitReason")=="panel_not_witnessed"&&(int)Get(intent,"WaitFrame")==Time.frameCount,
                    "log cap stopped the live status from updating");
                Require(((string)CallOwnerStatic("FormalConfirmationWaitNotice",(string)Get(intent,"WaitReason"))).Contains("plan panel"),
                    "panel witness wait was not explained");
                Call(owner,"RequestFormalConfirmation");
                Require(callbacks==1&&refusals==1&&object.ReferenceEquals(intent,Get(owner,"formalConfirmationRequest")),
                    "accepted desktop callback could not be distinguished from a refused duplicate request");
                Require(Get(owner,"pendingRawOwnerAction")==null&&task.Phase==Phase.BlockPreparation,
                    "diagnostic observation applied confirmation or advanced the task");
            }
            finally{Application.logMessageReceived-=capture;}
        }
        public static void Validate()
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            GameObject ownerObject=null,viewObject=null,rigObject=null,cameraObject=null;
            try
            {
                ownerObject=new GameObject("SyntheticFormalOwner");SceneManager.MoveGameObjectToScene(ownerObject,scene);
                viewObject=new GameObject("SyntheticFormalView");SceneManager.MoveGameObjectToScene(viewObject,scene);
                rigObject=new GameObject("SyntheticFormalRig");SceneManager.MoveGameObjectToScene(rigObject,scene);
                cameraObject=new GameObject("SyntheticFormalCamera");SceneManager.MoveGameObjectToScene(cameraObject,scene);
                var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(cameraObject.AddComponent<Camera>(),Actor.Participant);
                view.ConfigureViveDevelopmentPreview(ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold);
                var rig=rigObject.AddComponent<ViveStroopPreviewRig>();var owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();owner.Configure(rig,view);
                ViveNamedSimulationPlan plan;var clock=new Clock();var task=Task(clock,out plan);
                Set(owner,"participant",task);Set(owner,"configured",true);
                Call(owner,"RequestFormalConfirmation");
                Require(task.Phase==Phase.Preparation&&Get(owner,"formalConfirmationRequest")==null,"desktop callback skipped Practice");
                Require(((string)Call(owner,"FormalConfirmationUnavailableReason")).Contains("Finish Practice"),"wrong-phase blocker not explained");
                PassPractice(task,clock);
                Require(Get(owner,"formalConfirmationRequest")==null&&Get(owner,"pendingRawOwnerAction")==null&&Get(owner,"consentAutoStart")==null,
                    "Practice completion automatically created formal or recording intent");
                Set(owner,"rawInstallFailed",true);Call(owner,"RequestFormalConfirmation");
                Require(task.Phase==Phase.BlockPreparation&&Get(owner,"formalConfirmationRequest")==null&&
                    ((string)Call(owner,"FormalConfirmationUnavailableReason")).Contains("cannot continue"),"faulted recorder allowed confirmation or hid reason");
                Set(owner,"rawInstallFailed",false);
                var monotonic=new MonotonicClock("synthetic-formal-intent");Set(owner,"sharedClock",monotonic);
                var intent=Intent(owner,task,monotonic,view);
                ValidateDiagnostics(owner,task,intent);
                Require((bool)Call(owner,"FormalConfirmationIdentityMatches",intent),"exact request identity did not match");
                Call(owner,"RequestFormalConfirmation");Require(object.ReferenceEquals(intent,Get(owner,"formalConfirmationRequest")),"repeated click replaced/extended pending request");
                Require(!(bool)Call(owner,"FormalConfirmationStillCurrent",intent),"identity alone granted recording authority without installed pump");
                var otherClock=new MonotonicClock("other-formal-clock");Set(owner,"sharedClock",otherClock);
                Require(!(bool)Call(owner,"FormalConfirmationIdentityMatches",intent),"changed clock retained request");Set(owner,"sharedClock",monotonic);
                Set(intent,"Block",task.BlockIndex+1);Require(!(bool)Call(owner,"FormalConfirmationIdentityMatches",intent),"changed block retained request");Set(intent,"Block",task.BlockIndex);
                Set(intent,"Plan",null);Require(!(bool)Call(owner,"FormalConfirmationIdentityMatches",intent),"changed plan retained request");Set(intent,"Plan",plan);
                Set(intent,"View",null);Require(!(bool)Call(owner,"FormalConfirmationIdentityMatches",intent),"changed render view retained request");Set(intent,"View",view);
                ViveNamedSimulationPlan otherPlan;var other=Task(new Clock(),out otherPlan);Set(owner,"participant",other);
                Require(!(bool)Call(owner,"FormalConfirmationIdentityMatches",intent),"replacement task retained request");Set(owner,"participant",task);
                Require(!(bool)Call(owner,"FormalConfirmationPanelCurrent"),"unrendered panel granted confirmation");
                Call(owner,"ObserveFormalConfirmationRequest");
                Require(Get(owner,"formalConfirmationRequest")==null&&Get(owner,"pendingRawOwnerAction")==null,"missing recorder did not cancel without action");
                Intent(owner,task,monotonic,view);Call(owner,"CancelFormalConfirmation","synthetic explicit cancel");
                Require(Get(owner,"formalConfirmationRequest")==null&&task.Phase==Phase.BlockPreparation,"cancel applied formal confirmation");
                Call(owner,"RequestFormalConfirmation");
                Require(task.Phase==Phase.BlockReady&&Get(owner,"formalConfirmationRequest")==null&&Get(owner,"pendingRawOwnerAction")==null,
                    "explicit preview callback failed to confirm once");
                Require((int)Get(owner,"rawPlanStartRequested")==0&&task.CurrentTrial==null,"formal confirmation implicitly began a block");
                Call(owner,"RequestFormalConfirmation");Require(task.Phase==Phase.BlockReady&&task.CurrentTrial==null,"repeat after success started formal task");
                Require(Get(owner,"namedRawPump")==null&&Get(owner,"namedRawDestination")==null&&Get(rig,"worker")==null&&!rig.ViewRunning,
                    "fixture started recording, eye service or XR");
                Debug.Log("VIVE_FORMAL_CONFIRMATION_FIXTURE_PASSED: "+assertions+" assertions; explicit formal boundary, exact intent identity, missing authority, repeated click, fault/cancel, bounded wait diagnostics and no automatic first block. No XR or files.");
            }
            finally
            {
                if(ownerObject!=null)UnityEngine.Object.DestroyImmediate(ownerObject);
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
