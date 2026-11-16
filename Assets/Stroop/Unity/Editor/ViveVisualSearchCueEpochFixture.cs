#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Calls the actual owner cue branch between successive independent head
    // polls. No native eye device, file receipt or hardware acceptance is used.
    public static class ViveVisualSearchCueEpochFixture
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
        sealed class SilentClient:IViveProEyeClient
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "Editor synthetic cue epoch";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Entered.Set();Release.WaitOne();return null;}
            public void Dispose(){}
        }
        static void Require(bool ok,string detail){if(!ok)throw new InvalidOperationException("Visual search cue epoch: "+detail);}
        static object Call(object target,string name,params object[] args)
        {return target.GetType().GetMethods(Private).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target,args);}
        static object Factory(Type type,string name,params object[] args){return type.GetMethod(name,Static).Invoke(null,args);}
        static void Set(object target,string name,object value){target.GetType().GetField(name,Private).SetValue(target,value);}
        static object Get(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
        static GameObject Root(Scene scene,string name)
        {var root=new GameObject(name);root.hideFlags=HideFlags.HideAndDontSave;SceneManager.MoveGameObjectToScene(root,scene);return root;}
        public static void Validate()
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            GameObject cameraObject=null,viewObject=null,rigObject=null,ownerObject=null;
            ViveStroopDevelopmentBootstrap owner=null;ViveStroopPreviewRig rig=null;
            var client=new SilentClient();ViveProEyeWorker worker=null;object pump=null;
            try
            {
                cameraObject=Root(scene,"CueEpochCamera");var camera=cameraObject.AddComponent<Camera>();camera.cullingMask=0;
                viewObject=Root(scene,"CueEpochView");var view=viewObject.AddComponent<WorldSpaceExperimentView>();
                view.Initialize(camera,Actor.Participant);view.ConfigureViveDevelopmentPreview(ViveFixedTrackpadMapping.Create(),.5);
                rigObject=Root(scene,"CueEpochRig");rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                ownerObject=Root(scene,"CueEpochOwner");owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();owner.Configure(rig,view);
                var clock=new MonotonicClock("synthetic-search-cue-epoch");int sequence=0;
                worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++sequence,clock.Now,true,true,Rotation.Identity),()=>client);
                Require(worker.Start()&&client.Entered.WaitOne(3000),"worker did not start");
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"search-cue-epoch-geometry");
                var allocation=new Allocation("search-cue-epoch-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var design=ViveLegacyRecordingDesign.Select(Actor.Owner,"search-cue-epoch-design",allocation,geometry.Version,ViveFixedTrackpadMapping.Version);
                var config=SessionConfiguration.WithTaskKind(SessionConfiguration.LegacyRecordingDevelopment(
                    new SessionConfiguration("synthetic-search-cue-epoch",73,allocation,RunKind.Simulation,"en",5,30,true,0,true,true,true),
                    design,ViveLegacyRecordingIntent.Select(Actor.Owner,"search-cue-epoch-intent",true,true)),TaskKind.VisualSearch);
                var task=new ViveDevelopmentParticipant(config,clock,geometry,ViveFixedTrackpadMapping.Create(),.5,true);
                var assembly=typeof(ViveDevelopmentParticipant).Assembly;
                var admission=Factory(assembly.GetType("AvatarStroop.Runtime.ViveNamedSessionFrameAdmission",true),
                    "CreateLegacySyntheticForTest",task,worker,clock,64,clock.Now,clock.Now);
                var budget=Factory(assembly.GetType("AvatarStroop.Runtime.ViveRawByteBudget",true),"SelectExplicit",33554432L);
                Require((bool)Call(admission,"ConfirmTask")&&(bool)Call(admission,"ConfirmEye")&&
                    (bool)Call(admission,"TryBindQueueBudget",budget),"admission refused");
                var pumpType=assembly.GetType("AvatarStroop.Runtime.ViveNamedRawRecordingPump",true);
                var writer=Activator.CreateInstance(pumpType.GetNestedType("SyntheticWriter",BindingFlags.NonPublic),Private,null,new[]{admission},null);
                pump=Factory(pumpType,"StartLegacySyntheticWithStatusForTest",task,worker,clock,admission,writer);
                Set(owner,"SyntheticRawOwnerForFixture",true);Set(owner,"configured",true);Set(owner,"participant",task);
                Set(owner,"sharedClock",clock);Set(owner,"namedRawPump",pump);
                Set(rig,"previewCamera",camera);Set(rig,"view",view);Set(rig,"development",owner);Set(rig,"worker",worker);
                Set(rig,"running",true);Set(rig,"visibleMask",~0);Set(rig,"SyntheticRawTrackingForFixture",true);
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                var engine=(Experiment)Get(task,"experiment");engine.BeginFamiliarization(Actor.Owner);engine.BeginPractice(Actor.Owner);
                Set(engine,"<LockedControl>k__BackingField",(ControlSelection?)ControlSelection.Automatic);
                int passes=0;
                for(int block=-1;block<4;block++)
                {
                    // As in the existing raw avatar fixture, only phase setup
                    // bypasses native plan receipts. All cue owner calls and
                    // following worker/admission/pump epochs are production.
                    if(block>=0)
                    {Set(engine,"<Phase>k__BackingField",Phase.MainTask);Set(engine,"<BlockIndex>k__BackingField",block);
                        Set(engine,"trialIndex",0);Set(engine,"activeAccum",0.0);Set(engine,"activeSince",null);Call(engine,"CreateTrial",false);}
                    Call(view,"PrepareViveRawTaskView",task.ParticipantView);
                    for(int pass=0;pass<4;pass++)
                    {
                        object[] frame={null};
                        Require((bool)Call(worker,"TryPollTaskForRecording",frame),"next independent head poll failed");
                        Require((bool)Call(pump,"StageRigFrame",frame[0],1.0),
                            "next cue frame rejected after actual owner reveal; block="+block+" pass="+pass);
                        Require((bool)Call(owner,"PrepareRawScenePresentation"),"actual cue reveal failed");
                        Require(!task.CurrentTrial.Onset.HasValue&&!owner.RecordingBlocked&&camera.cullingMask!=0,
                            "cue changed trial onset or blocked presentation");
                        Require(Get(owner,"pendingRawPresentation")==null,"cue reserved an array onset");
                        passes++;
                    }
                }
                // The final cue must release its frame as well.
                object[] last={null};Require((bool)Call(worker,"TryPollTaskForRecording",last)&&
                    (bool)Call(pump,"StageRigFrame",last[0],1.0),"final cue left an obsolete epoch");
                Require((bool)Call(pump,"DrainTaskEvents"),"final fixture frame did not drain");
                Debug.Log("VIVE_VISUAL_SEARCH_CUE_EPOCH_FIXTURE_PASSED: "+passes+" actual owner cue reveals across Practice and BCAD; no array onset; fresh epochs accepted.");
            }
            finally
            {
                if(pump!=null)Call(pump,"Stop");
                if(worker!=null){Call(worker,"TryRequestStop");client.Release.Set();worker.Stop(1000);worker.Dispose();}
                if(owner!=null)Set(owner,"namedRawPump",null);
                if(rig!=null){Set(rig,"worker",null);Set(rig,"running",false);}
                foreach(var root in new[]{ownerObject,rigObject,viewObject,cameraObject})if(root!=null)UnityEngine.Object.DestroyImmediate(root);
                EditorSceneManager.CloseScene(scene,true);client.Entered.Dispose();client.Release.Dispose();
            }
        }
    }
}
#endif
