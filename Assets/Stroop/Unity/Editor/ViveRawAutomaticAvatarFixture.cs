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
    // Actual raw scene preparation, camera pre-cull and View callback flow.
    // Engine phase/onset below are fixture-only; the installed native fixture
    // separately exercises real plan receipts and first-onset reservation.
    public static class ViveRawAutomaticAvatarFixture
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
        sealed class SilentClient:IViveProEyeClient
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "Editor synthetic silent gaze";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Entered.Set();Release.WaitOne();return null;}
            public void Dispose(){}
        }
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException("Raw automatic avatar fixture: "+message);}
        static object Call(object target,string name,params object[] args)
        {
            var member=target.GetType().GetMethods(Private).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length);
            return member.Invoke(target,args);
        }
        static object Factory(Type target,string name,params object[] args)
        {return target.GetMethod(name,Static).Invoke(null,args);}
        static void Set(object target,string name,object value)
        {target.GetType().GetField(name,Private).SetValue(target,value);}
        static object Get(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
        static GameObject Root(Scene scene,string name)
        {
            var root=new GameObject(name);root.hideFlags=HideFlags.HideAndDontSave;SceneManager.MoveGameObjectToScene(root,scene);return root;
        }
        static void Stage(object pump,ViveProEyeWorker worker)
        {
            object[] poll={null};
            Require((bool)Call(worker,"TryPollTaskForRecording",poll)&&((ViveProEyePoll)poll[0]).Eye==null&&
                (bool)Call(pump,"StageRigFrame",poll[0],1.0),"raw fresh head did not pass without gaze");
        }
        static ViveEyeFrame Eye(Camera camera,Vector3 target,IClock clock)
        {
            var local=Quaternion.Inverse(camera.transform.rotation)*(target-camera.transform.position).normalized;
            var ray=new ViveEyeRay(3UL,true,true,new Vec3(0,0,0),new Vec3(-local.x,local.y,local.z));
            return new ViveEyeFrame(41,937,clock.Now,false,ray,ray,ray);
        }
        static void CheckAoiAfterRawGuard(WorldSpaceExperimentView view,ViveStroopPreviewRig rig,Camera camera,
            IClock clock,bool visible,Condition condition)
        {
            var board=view.transform.Find("WorldSpaceContent/ClassroomEnvironment/Whiteboard/WritingArea");
            Require(board!=null,"production classroom writing area missing");
            var eye=Eye(camera,board.position,clock);
            var before=view.CaptureViveEyeAoi(eye,clock);
            Require(before.Category=="task"&&before.AvatarAoiApplicable==visible,
                "rendered production geometry invalid in "+condition+": "+before.Reason);
            Require(before.Geometry.Count==6&&before.Geometry[0].Enabled&&
                before.Geometry.Skip(1).All(g=>g.Enabled==visible),"rendered BCAD geometry visibility incorrect");
            // This is the exact production guard, including the owner callback
            // and ClearRawAutomaticAvatar -> ClearAvatarOutput -> RefreshWarnings.
            // Merely SetActive(false) never reproduces its transient warning.
            rig.BlockTaskPresentation();
            Require(camera.cullingMask==0&&!view.AvatarRoot.gameObject.activeInHierarchy,
                "real per-frame guard did not hide the production scene");
            Require((bool)Get(view,"hasPresentationFault")==visible,
                "real A/B/C missing-source warning or D control was not reproduced");
            var after=view.CaptureViveEyeAoi(eye,clock);
            Require(after.Category=="task"&&after.Reason=="valid_surface_proxy"&&after.AvatarAoiApplicable==visible,
                "mask-after-render invalidated completed BCAD geometry in "+condition+": "+after.Reason);
            Require(after.SceneUnityFrame==before.SceneUnityFrame&&after.TaskRevision==before.TaskRevision&&
                after.AvatarRevision==before.AvatarRevision&&after.SceneCaptureLocalSeconds==before.SceneCaptureLocalSeconds&&
                after.Geometry.Zip(before.Geometry,(a,b)=>a.Enabled==b.Enabled&&a.PositionWorld.Equals(b.PositionWorld)&&
                    a.RotationWorld.Equals(b.RotationWorld)&&a.ScaleWorld.Equals(b.ScaleWorld)).All(equal=>equal),
                "per-frame guard changed immutable completed-render geometry");
        }
        public static void Validate()
        {
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            GameObject cameraObject=null,otherObject=null,viewObject=null,rigObject=null,ownerObject=null;
            var client=new SilentClient();ViveProEyeWorker worker=null;object pump=null;
            ViveStroopDevelopmentBootstrap owner=null;ViveStroopPreviewRig rig=null;
            try
            {
                cameraObject=Root(scene,"RawAvatarParticipantCamera");
                var camera=cameraObject.AddComponent<Camera>();camera.cullingMask=0;
                camera.transform.position=new Vector3(0,1.6f,0);
                otherObject=Root(scene,"RawAvatarWrongCamera");var other=otherObject.AddComponent<Camera>();other.enabled=false;
                viewObject=Root(scene,"RawAvatarView");var view=viewObject.AddComponent<WorldSpaceExperimentView>();
                view.Initialize(camera,Actor.Participant);view.ConfigureViveDevelopmentPreview(ViveFixedTrackpadMapping.Create(),.5);
                rigObject=Root(scene,"RawAvatarRig");rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                ownerObject=Root(scene,"RawAvatarOwner");owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();owner.Configure(rig,view);
                var clock=new MonotonicClock("synthetic-raw-avatar");
                view.BindViveEyeAoiRenderClock(clock);
                int sequence=0;worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++sequence,clock.Now,true,true,Rotation.Identity),()=>client);
                Require(worker.Start()&&client.Entered.WaitOne(3000),"synthetic worker unavailable");
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"raw-avatar-editor-geometry");
                var allocation=new Allocation("synthetic-avatar-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var intent=ViveLegacyRecordingIntent.Select(Actor.Owner,"raw-avatar-editor-intent",true,true);
                var design=ViveLegacyRecordingDesign.Select(Actor.Owner,"raw-avatar-editor-design",allocation,geometry.Version,ViveFixedTrackpadMapping.Version);
                var config=SessionConfiguration.LegacyRecordingDevelopment(
                    new SessionConfiguration("synthetic-raw-avatar",73,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true),design,intent);
                var task=new ViveDevelopmentParticipant(config,clock,geometry,ViveFixedTrackpadMapping.Create(),.5,true);
                var assembly=typeof(ViveDevelopmentParticipant).Assembly;
                var admission=Factory(assembly.GetType("AvatarStroop.Runtime.ViveNamedSessionFrameAdmission",true),
                    "CreateLegacySyntheticForTest",task,worker,clock,64,clock.Now,clock.Now);
                var budget=Factory(assembly.GetType("AvatarStroop.Runtime.ViveRawByteBudget",true),"SelectExplicit",33554432L);
                Require((bool)Call(admission,"ConfirmTask")&&(bool)Call(admission,"ConfirmEye")&&(bool)Call(admission,"TryBindQueueBudget",budget),"synthetic admission refused");
                var pumpType=assembly.GetType("AvatarStroop.Runtime.ViveNamedRawRecordingPump",true);
                var writer=Activator.CreateInstance(pumpType.GetNestedType("SyntheticWriter",BindingFlags.NonPublic),Private,null,new[]{admission},null);
                pump=Factory(pumpType,"StartLegacySyntheticWithStatusForTest",task,worker,clock,admission,writer);
                Set(owner,"SyntheticRawOwnerForFixture",true);Set(owner,"configured",true);Set(owner,"participant",task);
                Set(owner,"sharedClock",clock);Set(owner,"namedRawPump",pump);
                Set(rig,"previewCamera",camera);Set(rig,"view",view);Set(rig,"development",owner);Set(rig,"worker",worker);
                Set(rig,"running",true);Set(rig,"visibleMask",~0);Set(rig,"SyntheticRawTrackingForFixture",true);
                // No StimulusRendered hook here: the committed-identity setup
                // below tests the already-witnessed reveal path, not onset.
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                var engine=(Experiment)Get(task,"experiment");
                AvatarPresentationObservation observed=null;view.AvatarPresented+=item=>observed=item;
                Action<bool> reveal=(visible)=>{
                    camera.cullingMask=0;
                    Call(view,"PrepareViveRawTaskView",task.ParticipantView);
                    object[] identity={task.CurrentTrial.TrialId,task.CurrentTrial.Stimulus.Word,task.CurrentTrial.Stimulus.Ink,-1L};
                    Require((bool)Call(view,"TryGetViveRawTrialIdentity",identity),"fixture stimulus identity missing");
                    Set(owner,"committedRawTrial",task.CurrentTrial.TrialId);Set(owner,"committedRawRevision",identity[3]);
                    Stage(pump,worker);
                    Require((bool)Call(owner,"PrepareRawScenePresentation"),"real raw reveal refused");
                    Require(view.AvatarRoot.gameObject.activeInHierarchy==visible,
                        "raw automatic avatar visibility mismatch in block "+task.Phase+"/"+task.BlockIndex);
                    Require(camera.cullingMask!=0&&!owner.RecordingBlocked,"camera remained masked");
                };
                Action<bool,bool> cameraPass=(expected,realAvatar)=>{
                    observed=null;
                    Call(rig,"GuardRawSceneBeforeCull",camera);
                    Require(camera.cullingMask!=0,"participant camera pre-cull refused correct avatar");
                    Call(view,"BeforeCameraCull",camera);
                    Call(view,"ObserveAvatarRender",other);
                    if(realAvatar)Call(view,"ObserveAvatarRender",camera);
                    Call(view,"AfterCameraRender",camera);
                    Require(observed!=null&&observed.TrialId==task.CurrentTrial.TrialId&&
                        observed.VisibilityRequested==expected&&observed.AnyAvatarRendererObserved==realAvatar,
                        "camera witness does not match this exact scene/pass");
                };
                engine.BeginFamiliarization(Actor.Owner);engine.BeginPractice(Actor.Owner);
                engine.MarkPresented(task.CurrentTrial.TrialId);
                reveal(false);
                cameraPass(false,false);
                Set(engine,"<LockedControl>k__BackingField",(ControlSelection?)ControlSelection.Automatic);
                Set(engine,"<Phase>k__BackingField",Phase.MainTask);
                var order=new[]{Condition.B,Condition.C,Condition.A,Condition.D};
                for(int block=0;block<order.Length;block++)
                {
                    Set(engine,"<BlockIndex>k__BackingField",block);Set(engine,"trialIndex",0);
                    Set(engine,"activeAccum",0.0);Set(engine,"activeSince",null);
                    Call(engine,"CreateTrial",false);engine.MarkPresented(task.CurrentTrial.TrialId);
                    bool visible=order[block]!=Condition.D;
                    reveal(visible);
                    cameraPass(visible,visible);
                    if(visible)
                    {
                        Require(observed.ActualInputSource==ControlSource.Automatic&&!observed.HeadHeld&&!observed.EyesHeld,
                            "avatar did not use its actual automatic pose");
                        var outcome=observed.CapturedOutput.Reference;
                        var target=order[block]==Condition.C?GazeTarget.Task:GazeTarget.Participant;
                        Require(outcome!=null&&outcome.ToTarget==target,"first frame decoded wrong BCAD target");
                    }
                    if(order[block]==Condition.B)
                    {
                        var first=view.AppliedPose.HeadRotation;
                        Set(engine,"activeAccum",3.75);
                        reveal(true);
                        cameraPass(true,true);
                        var second=view.AppliedPose.HeadRotation;
                        Require(!first.Equals(second)&&observed.CapturedOutput.Reference.ToTarget==GazeTarget.Task,
                            "B did not update pose on the same ongoing trial");
                        cameraPass(true,false);
                        Require(!(bool)Call(owner,"RawAutomaticAvatarRendered"),
                            "wrong-camera or older render witness was reused");
                        Require((bool)Call(owner,"RawAutomaticAvatarCurrent",camera),
                            "absence of frustum evidence did not preserve structural authority");
                        Call(owner,"ClearRawAutomaticAvatar");
                        Require(!view.AvatarRoot.gameObject.activeInHierarchy,"hold left a stale automatic avatar");
                    }
                    // Repeat the actual reveal/render/guard/reveal path for all
                    // four conditions, retaining production classroom transforms
                    // and automatic head/eye rotations (including nonuniform
                    // ellipsoid scale). This catches condition-dependent gates.
                    for(int cycle=0;cycle<2;cycle++)
                    {
                        reveal(visible);cameraPass(visible,visible);
                        CheckAoiAfterRawGuard(view,rig,camera,clock,visible,order[block]);
                    }
                    // A fault present during the completed camera pass must
                    // remain invalid even after the current warning is cleared.
                    reveal(visible);view.SetFaults(new[]{"Synthetic presentation fault before render"});
                    cameraPass(visible,visible);
                    view.SetFaults(new string[0]);
                    Require(!(bool)Get(view,"hasPresentationFault"),"fault control did not restore current presentation");
                    var board=view.transform.Find("WorldSpaceContent/ClassroomEnvironment/Whiteboard/WritingArea");
                    var faulted=view.CaptureViveEyeAoi(Eye(camera,board.position,clock),clock);
                    Require(faulted.Category=="invalid"&&faulted.Reason=="rendered_scene_unavailable",
                        "fault-before-render scene was accepted after current warning cleared in "+order[block]+": "+faulted.Reason);
                    reveal(visible);cameraPass(visible,visible);
                    CheckAoiAfterRawGuard(view,rig,camera,clock,visible,order[block]);
                }
                Debug.Log("VIVE_RAW_AUTOMATIC_AVATAR_FIXTURE_PASSED: real BCAD raw reveal/render/guard/reveal AOI; render-time fault rejected, later mask retained; B updates, C/A fixed, D/Practice hidden; software renderer seam only.");
            }
            finally
            {
                if(pump!=null)Call(pump,"Stop");
                if(worker!=null){Call(worker,"TryRequestStop");client.Release.Set();worker.Stop(1000);worker.Dispose();}
                if(owner!=null)Set(owner,"namedRawPump",null);
                if(rig!=null){Set(rig,"worker",null);Set(rig,"running",false);}
                foreach(var root in new[]{ownerObject,rigObject,viewObject,otherObject,cameraObject})
                    if(root!=null)UnityEngine.Object.DestroyImmediate(root);
                EditorSceneManager.CloseScene(scene,true);
                client.Entered.Dispose();client.Release.Dispose();
            }
        }
    }
}
#endif
