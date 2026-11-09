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
    // Explicit batch fixture, only for an isolated synthetic Editor project.
    // No XR loader, input action, eye worker, socket, or recording file is opened.
    // A synthetic gate edge is deliberately passed to the real handler: this is
    // not proof that an OpenXR device or a physical headset received input.
    public static class ViveSafetyMenuPresentationFixture
    {
        sealed class FixtureClock:IClock
        {
            public string ClockId {get{return "synthetic-editor-safety-clock";}}
            public double Now {get{return 1;}}
        }
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Synthetic VIVE safety fixture: "+message);}
        static FieldInfo Field(Type type,string name)
        {
            var field=type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null)throw new InvalidOperationException("Missing fixture field: "+type.Name+"."+name);
            return field;
        }
        static void Set(object target,string name,object value)
        {Field(target.GetType(),name).SetValue(target,value);}
        static object Get(object target,string name)
        {return Field(target.GetType(),name).GetValue(target);}
        static void Call(object target,string name,params object[] args)
        {
            var method=target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(method==null)throw new InvalidOperationException("Missing fixture method: "+target.GetType().Name+"."+name);
            method.Invoke(target,args);
        }
        static Dictionary<ViveTrackpadDirection,Colour> SyntheticMapping()
        {
            return new Dictionary<ViveTrackpadDirection,Colour>{
                {ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
        }
        static ViveDevelopmentParticipant Participant()
        {
            var allocation=new Allocation("synthetic-editor-safety-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},
                GazeTarget.Participant,new[]{0,1,2,3});
            var config=new SessionConfiguration("synthetic-editor-safety-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"synthetic-editor-safety-geometry");
            var task=new ViveDevelopmentParticipant(config,new FixtureClock(),geometry,SyntheticMapping(),.5,true,
                new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
            task.OwnerBeginPractice(Actor.Owner);
            Require(task.Phase==Phase.Practice&&task.CurrentTrial!=null,"practice fixture did not start");
            task.MarkPresented(task.CurrentTrial.TrialId);
            return task;
        }
        static int CountPause(ViveDevelopmentParticipant task,string actor)
        {
            int count=0;
            foreach(var item in task.ReadEventsAfter(-1,4096))
            {
                if(item.Kind!="explicit_pause")continue;
                string actual;Require(item.Fields.TryGetValue("actor",out actual),"pause actor missing");
                Require(actual==actor,"pause actor was not "+actor);count++;
            }
            return count;
        }
        static ViveDevelopmentParticipant V3Participant()
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"synthetic-editor-safety-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-editor-safety-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-editor-safety-intent",true,true);
            var config=SessionConfiguration.NamedRecordingDevelopment("synthetic-editor-v3-safety",42,plan,intent);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"synthetic-editor-safety-geometry");
            var task=new ViveDevelopmentParticipant(config,new FixtureClock(),geometry,SyntheticMapping(),.5,true);
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));task.OwnerBeginPractice(Actor.Owner);
            return task;
        }
        public static void Validate()
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            GameObject cameraObject=null,viewObject=null,rigObject=null,bootstrapObject=null;
            ViveStroopPreviewRig rig=null;
            Scene previousScene=SceneManager.GetActiveScene(),fixtureScene=default(Scene);
            bool sceneOpened=false;
            try
            {
                fixtureScene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                sceneOpened=fixtureScene.IsValid()&&fixtureScene.isLoaded;
                Require(sceneOpened,"cannot create isolated synthetic scene (valid="+fixtureScene.IsValid()+", loaded="+fixtureScene.isLoaded+")");
                // Mirror the camera fixture: do not rely on making the additive
                // scene active in batch mode. Move each root before hiding it.
                cameraObject=new GameObject("SyntheticSafetyFixtureCamera");SceneManager.MoveGameObjectToScene(cameraObject,fixtureScene);Require(cameraObject.scene==fixtureScene,"camera root not in fixture scene");cameraObject.hideFlags=HideFlags.HideAndDontSave;
                var camera=cameraObject.AddComponent<Camera>();camera.cullingMask=-1;
                viewObject=new GameObject("SyntheticSafetyFixtureView");SceneManager.MoveGameObjectToScene(viewObject,fixtureScene);Require(viewObject.scene==fixtureScene,"view root not in fixture scene");viewObject.hideFlags=HideFlags.HideAndDontSave;
                var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                view.ConfigureViveDevelopmentPreview(SyntheticMapping(),.5);
                rigObject=new GameObject("SyntheticSafetyFixtureRig");SceneManager.MoveGameObjectToScene(rigObject,fixtureScene);Require(rigObject.scene==fixtureScene,"rig root not in fixture scene");rigObject.hideFlags=HideFlags.HideAndDontSave;
                rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                bootstrapObject=new GameObject("SyntheticSafetyFixtureBootstrap");SceneManager.MoveGameObjectToScene(bootstrapObject,fixtureScene);Require(bootstrapObject.scene==fixtureScene,"bootstrap root not in fixture scene");bootstrapObject.hideFlags=HideFlags.HideAndDontSave;
                var bootstrap=bootstrapObject.AddComponent<ViveStroopDevelopmentBootstrap>();bootstrap.Configure(rig,view);
                var task=Participant();view.ApplyTaskView(task.ParticipantView);
                var trial=task.CurrentTrial;int before=task.ReadEventsAfter(-1,4096).Count;
                Set(bootstrap,"participant",task);Set(bootstrap,"configured",true);
                // Simulate the already-blocked status only; no recorder was
                // created or disposed and no recording/persistence is claimed.
                Set(bootstrap,"recordingTerminal",true);
                Set(rig,"previewCamera",camera);Set(rig,"development",bootstrap);Set(rig,"running",true);
                Require(bootstrap.RecordingBlocked&&camera.cullingMask!=0,"blocked fixture not prepared");
                Call(rig,"Update");
                Require(camera.cullingMask==0,"recording-blocked Rig.Update did not cull the actual camera");
                // Blocked frames drain safety work without participant controls.
                Require((bool)Get(rig,"running"),"blocked branch did not remain active");
                Require(((string)Get(rig,"status")).Contains("Recording stopped or faulted"),"blocked branch status missing");
                Require(task.Phase==Phase.Practice&&task.CurrentTrial==trial&&trial.Outcome==TrialOutcome.Pending,
                    "blocked Rig.Update unexpectedly advanced or paused the task");
                Require(task.ReadEventsAfter(-1,4096).Count==before,"blocked Rig.Update produced a task event");
                Require(Get(bootstrap,"device")==null&&Get(rig,"worker")==null&&Get(rig,"display")==null,
                    "fixture must not create an XR device, worker, or display");
                // The actual blocked-safety entry is safe without a device. A
                // second frame must keep the mask and must not advance a trial.
                bootstrap.PollRecordingBlockedSafety();
                Call(rig,"Update");
                Require(camera.cullingMask==0&&task.Phase==Phase.Practice&&task.CurrentTrial==trial,
                    "repeated blocked frame changed the task or restored the camera");
                Require(task.ReadEventsAfter(-1,4096).Count==before,"blocked menu without a device produced a task event");

                var gate=new ViveStroopSafetyMenuGate();
                Require(!gate.Observe(true,false,true),"held menu on entry caused an edge");
                Require(!gate.Observe(true,false,true),"held menu repeated an edge");
                Require(!gate.Observe(true,false,false),"release caused an edge");
                bool syntheticEdge=gate.Observe(true,false,true);Require(syntheticEdge,"re-armed synthetic menu press did not edge");
                if(syntheticEdge)Call(bootstrap,"HandleParticipantMenu");
                Require(!gate.Observe(true,false,true),"held press repeated an edge");
                Require(task.Phase==Phase.Practice&&trial.Outcome==TrialOutcome.Pending&&task.ReadEventsAfter(-1,4096).Count==before,
                    "participant menu must not pause, interrupt, or add an event");
                Require(camera.cullingMask==0,"menu handler restored the blocked camera");
                Call(bootstrap,"PauseTask");
                Require(task.Phase==Phase.Paused&&trial.Outcome==TrialOutcome.Interrupted,"owner pause was removed");
                Require(CountPause(task,"Owner")==1,"owner must remain the only explicit pause actor");
                Call(rig,"Update");
                Require(camera.cullingMask==0&&task.Phase==Phase.Paused&&CountPause(task,"Owner")==1,
                    "subsequent blocked frame restored the camera or repeated the pause");
                Call(bootstrap,"WithdrawTask");
                Require(task.Phase==Phase.Ended&&task.EndReason==EndReason.Withdrawn,"withdraw handler did not end the synthetic task");
                Require(camera.cullingMask==0,"withdraw handler restored the blocked camera");
                Require(!task.IsResearchSession&&!task.UsesNetwork&&!task.RecordsTaskFiles&&!task.RecordsEyeSamples,
                    "fixture unexpectedly entered a research, network, or recording flow");
                // The real input-loss helper latches the first exact diagnostic.
                // A healthy sample, later owner request, or render must not replace it.
                var focusTask=Participant();Set(bootstrap,"participant",focusTask);
                Set(bootstrap,"recordingTerminal",false);
                string focusReason="Automatic safety: OpenXR session is not focused.";
                Call(bootstrap,"ApplyInputUnavailable",focusReason);
                Require(focusTask.Phase==Phase.Paused&&(string)Get(bootstrap,"pauseDiagnostic")==focusReason,
                    "first focus-loss diagnostic was not captured");
                focusTask.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Call(bootstrap,"PauseTask");
                Require((string)Get(bootstrap,"pauseDiagnostic")==focusReason&&focusTask.PauseDiagnosticRevision==1,
                    "recovery or repeated owner pause replaced the first reason");
                Require((string)Get(view,"vivePauseDiagnostic")==focusReason,"headset pause reason missing");
                Set(rig,"headStatus","Old ready text");Set(rig,"headDiagnosticFrame",Time.frameCount-1);
                Require(rig.CurrentHeadDiagnostic.Contains("No complete"),"stale head diagnostic was exposed as current");
                Call(bootstrap,"WithdrawTask");
                var unseen=Participant();Set(bootstrap,"participant",unseen);
                unseen.UpdateTrackpad(false,new VivePreviewTrackpadSample(true,false,0,0));
                Call(bootstrap,"PauseTask");
                Require((string)Get(bootstrap,"pauseDiagnostic")==unseen.LastPauseDiagnostic&&
                    ((string)Get(bootstrap,"pauseDiagnostic")).Contains("head/input"),
                    "later owner request misattributed an unseen automatic pause");
                Call(bootstrap,"WithdrawTask");
                // A foreign withdrawal can make v3 recording unavailable while
                // ordinary input is blocked. The real Rig.Update must drain it
                // on the owner without relying on a new controller menu edge.
                var v3=V3Participant();Set(bootstrap,"participant",v3);Set(bootstrap,"recordingTerminal",false);
                var thread=new System.Threading.Thread(v3.ParticipantWithdraw);thread.Start();Require(thread.Join(3000),"synthetic withdrawal thread did not finish");
                Require(bootstrap.RecordingBlocked&&v3.Phase==Phase.Practice,"v3 pending withdrawal not prepared");
                Call(rig,"Update");
                Require(camera.cullingMask==0&&v3.Phase==Phase.Ended&&v3.EndReason==EndReason.Withdrawn,
                    "blocked rig failed to drain v3 withdrawal without a menu edge");
                Require(Get(rig,"worker")==null&&Get(rig,"display")==null,"v3 fixture unexpectedly used XR or eye capture");
                Debug.Log("VIVE_SAFETY_MENU_SYNTHETIC_FIXTURE_PASSED: participant menu ignored, owner pause, withdrawal and camera mask only; no XR, headset or recording validation.");
            }
            finally
            {
                // Avoid invoking cleanup for a fabricated running XR state.
                if(rig!=null)Set(rig,"running",false);
                if(bootstrapObject!=null)UnityEngine.Object.DestroyImmediate(bootstrapObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(previousScene.IsValid()&&previousScene.isLoaded&&SceneManager.GetActiveScene()!=previousScene)SceneManager.SetActiveScene(previousScene);
                if(sceneOpened&&fixtureScene.IsValid()&&fixtureScene.isLoaded)
                    Require(EditorSceneManager.CloseScene(fixtureScene,true),"cannot close isolated synthetic scene");
            }
        }
    }
}
#endif
