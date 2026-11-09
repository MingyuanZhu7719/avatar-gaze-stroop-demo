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
    // Synthetic, edit-mode checks only. No XR loader, player, device, eye data,
    // network or participant file is opened. Nothing is saved to a scene.
    public static class ViveObservationCameraFixture
    {
        sealed class FixtureClock : IClock
        {
            public double Now { get { return 0; } }
            public string ClockId { get { return "synthetic-observation-camera-clock"; } }
        }

        sealed class Fixture : IDisposable
        {
            readonly Scene scene,previousActive;
            public readonly Camera Camera;
            public readonly ViveStroopPreviewRig Rig;
            public readonly ViveStroopDevelopmentBootstrap Bootstrap;
            public readonly ViveDevelopmentParticipant Participant;
            public readonly Experiment Experiment;
            public const int VisibleMask = 5;

            public Fixture(string session)
            {
                previousActive=SceneManager.GetActiveScene();
                scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                try
                {
                var origin=new GameObject("SyntheticObservationOrigin");SceneManager.MoveGameObjectToScene(origin,scene);
                var offset=new GameObject("SyntheticObservationOffset");offset.transform.SetParent(origin.transform,false);
                var cameraObject=new GameObject("SyntheticObservationCamera");cameraObject.transform.SetParent(offset.transform,false);
                Camera=cameraObject.AddComponent<Camera>();Camera.cullingMask=VisibleMask;
                var viewObject=new GameObject("SyntheticObservationView");SceneManager.MoveGameObjectToScene(viewObject,scene);
                var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(Camera,Actor.Participant);
                var rigObject=new GameObject("SyntheticObservationRig");SceneManager.MoveGameObjectToScene(rigObject,scene);
                Rig=rigObject.AddComponent<ViveStroopPreviewRig>();Bootstrap=rigObject.AddComponent<ViveStroopDevelopmentBootstrap>();
                Rig.Configure(Camera,origin.transform,offset.transform,view);Bootstrap.Configure(Rig,view);Rig.ConfigureDevelopment(Bootstrap);
                SetField(Rig,"visibleMask",VisibleMask);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"synthetic-observation-camera");
                var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},
                    {ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                var config=new SessionConfiguration(session,42,new Allocation("synthetic-camera-fixture",new[]{Condition.B,Condition.C,Condition.A,Condition.D},
                    GazeTarget.Participant,new[]{0,1,2,3}),RunKind.Simulation,"en",3,30,true,0,true,true,true);
                Participant=new ViveDevelopmentParticipant(config,new FixtureClock(),geometry,mapping,.5,true,
                    new ExperimentIncrementalOptions(128,128,16L*1024*1024),4096);
                SetField(Bootstrap,"participant",Participant);SetField(Bootstrap,"configured",true);
                Experiment=(Experiment)GetField(Participant,"experiment");
                Participant.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Participant.OwnerBeginPractice(Actor.Owner);
                Require(Participant.Phase==Phase.Practice&&Participant.CurrentTrial!=null,"synthetic practice setup failed");
                Participant.MarkPresented(Participant.CurrentTrial.TrialId);
                Require(Participant.CurrentTrial.Onset.HasValue,"synthetic stimulus onset missing");
                }
                catch
                {
                    CloseFixtureScene();
                    throw;
                }
            }
            public void Dispose()
            {
                // Close only the additive unsaved fixture scene. This does not
                // save or replace any other scene in the isolated project.
                CloseFixtureScene();
            }
            void CloseFixtureScene()
            {
                if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
                if(previousActive.IsValid()&&previousActive.isLoaded&&SceneManager.GetActiveScene()!=previousActive)
                    SceneManager.SetActiveScene(previousActive);
            }
        }

        static void Require(bool ok,string message)
        {if(!ok)throw new InvalidOperationException("Synthetic VIVE observation camera fixture: "+message);}
        static object GetField(object instance,string name)
        {
            var field=instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null)throw new InvalidOperationException("Missing fixture field "+name);return field.GetValue(instance);
        }
        static void SetField(object instance,string name,object value)
        {
            var field=instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null)throw new InvalidOperationException("Missing fixture field "+name);field.SetValue(instance,value);
        }
        static object Invoke(object instance,string name,params object[] args)
        {
            var method=instance.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(method==null)throw new InvalidOperationException("Missing fixture method "+name);return method.Invoke(instance,args);
        }
        static bool Check(ViveStroopDevelopmentBootstrap bootstrap)
        {return (bool)Invoke(bootstrap,"CheckObservation",true);}
        static void ApplyTrackedMask(ViveStroopPreviewRig rig,bool syntheticReady)
        {Invoke(rig,"ApplyTrackedMask",syntheticReady);}

        static void BacklogStopsAndDoesNotResume()
        {
            using(var fixture=new Fixture("synthetic-camera-backlog"))
            {
                var participant=fixture.Participant;var trial=participant.CurrentTrial;
                // Direct mutation only in this isolated simulation fixture: it
                // deliberately bypasses the participant's automatic drain.
                for(int i=0;i<100;i++)fixture.Experiment.RecordHeadsetRemoval(Actor.Owner);
                Require(!Check(fixture.Bootstrap),"bounded backlog unexpectedly reported current");
                Require(fixture.Camera.cullingMask==0,"backlog did not cull actual camera");
                Require(participant.Phase==Phase.Paused&&trial.Outcome==TrialOutcome.Interrupted,"active practice was not safely interrupted");
                Require(participant.ObservationPending,"fixture did not retain a pending backlog after safe pause");
                ApplyTrackedMask(fixture.Rig,true);Require(fixture.Camera.cullingMask==0,"synthetic tracked head exposed a pending backlog");
                ApplyTrackedMask(fixture.Rig,false);Require(fixture.Camera.cullingMask==0,"untracked synthetic head exposed camera");
                bool caughtUp=false;for(int i=0;i<16&&!caughtUp;i++)caughtUp=Check(fixture.Bootstrap);
                Require(caughtUp&&!participant.ObservationFaulted&&!participant.ObservationPending,"backlog did not drain");
                Require(participant.Phase==Phase.Paused&&trial.Outcome==TrialOutcome.Interrupted,"draining resumed or altered interrupted trial");
                ApplyTrackedMask(fixture.Rig,true);
                Require(fixture.Camera.cullingMask==Fixture.VisibleMask,"current paused simulation did not restore configured mask");
                Require(participant.Phase==Phase.Paused,"mask restore resumed the task");
            }
        }

        static void SourceFaultPermanentlyCulls()
        {
            using(var fixture=new Fixture("synthetic-camera-fault"))
            {
                ExperimentIncrementalCommit next;ExperimentIncrementalSnapshot current;long head;
                // Deliberately invalid cursor on this simulation only. The
                // producer must latch rather than silently bridge the fault.
                Require(!fixture.Experiment.TryReadIncrementalAfter(Int64.MaxValue-1,out next,out head,out current),"invalid cursor accepted");
                Require(fixture.Participant.ObservationFaulted,"source failure did not latch");
                Require(!Check(fixture.Bootstrap),"fault was reported current");
                Require(fixture.Camera.cullingMask==0&&fixture.Participant.Phase==Phase.Paused,"fault did not cull and pause");
                ApplyTrackedMask(fixture.Rig,true);
                Require(fixture.Camera.cullingMask==0&&fixture.Bootstrap.PresentationBlocked,"tracked mask reopened after terminal fault");
                Require(!Check(fixture.Bootstrap)&&fixture.Camera.cullingMask==0,"terminal fault was cleared");
            }
        }

        // Invoke using Unity's -batchmode -executeMethod in a separate project.
        public static void Run()
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            BacklogStopsAndDoesNotResume();SourceFaultPermanentlyCulls();
            Debug.Log("VIVE_OBSERVATION_CAMERA_FIXTURE_PASS: synthetic Editor camera mask and pause only; no XR or headset validation.");
        }
    }
}
#endif
