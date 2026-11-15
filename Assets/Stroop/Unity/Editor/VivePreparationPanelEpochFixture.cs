#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
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
    // Actual owner/view callbacks; the worker, render witness and writer are
    // software fixtures. No XR runtime, hardware input or files are opened.
    public static class VivePreparationPanelEpochFixture
    {
        const BindingFlags Instance=BindingFlags.Instance|BindingFlags.NonPublic;
        const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
        sealed class Client:IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> values=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            public string Diagnostic {get{return "synthetic preparation epoch Editor fixture";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll()
            {
                Waiting.Set();
                if(!Permit.WaitOne(10000))throw new InvalidOperationException("synthetic poll timeout");
                lock(values)return values.Dequeue();
            }
            internal void Send(ViveProEyePoll value){Release(value);Require(Waiting.WaitOne(3000),"worker did not enter next poll");}
            internal void Release(ViveProEyePoll value){lock(values)values.Enqueue(value);Permit.Set();}
            public void Dispose(){}
        }
        static void Require(bool ok,string detail){if(!ok)throw new InvalidOperationException("Preparation panel epoch fixture: "+detail);}
        static FieldInfo Field(object value,string name)
        {return value.GetType().GetField(name,Instance)??throw new MissingFieldException(value.GetType().FullName,name);}
        static void Set(object value,string name,object fieldValue){Field(value,name).SetValue(value,fieldValue);}
        static object Get(object value,string name){return Field(value,name).GetValue(value);}
        static object Prop(object value,string name){return value.GetType().GetProperty(name,Instance).GetValue(value,null);}
        static object Call(object value,string name,params object[] args)
        {var method=value.GetType().GetMethods(Instance).SingleOrDefault(candidate=>candidate.Name==name&&candidate.GetParameters().Length==args.Length);return (method??throw new MissingMethodException(value.GetType().FullName,name)).Invoke(value,args);}
        static object Factory(Type type,string name,params object[] args)
        {return (type.GetMethod(name,Static)??throw new MissingMethodException(type.FullName,name)).Invoke(null,args);}
        static GameObject Root(Scene scene,string name)
        {var root=new GameObject(name);SceneManager.MoveGameObjectToScene(root,scene);root.hideFlags=HideFlags.HideAndDontSave;return root;}
        static ViveDevelopmentParticipant LegacyTask(MonotonicClock clock)
        {
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"synthetic-panel-geometry");
            var allocation=new Allocation("synthetic-panel-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var design=ViveLegacyRecordingDesign.Select(Actor.Owner,"synthetic-panel-design",allocation,geometry.Version,ViveFixedTrackpadMapping.Version);
            var config=SessionConfiguration.LegacyRecordingDevelopment(
                new SessionConfiguration("synthetic-panel-session",73,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true),
                design,ViveLegacyRecordingIntent.Select(Actor.Owner,"synthetic-panel-intent",true,true));
            return new ViveDevelopmentParticipant(config,clock,geometry,ViveFixedTrackpadMapping.Create(),.5,true);
        }
        static void Send(Client client,MonotonicClock clock,int number,int mask)
        {
            var ray=new ViveEyeRay((ulong)mask,(mask&1)!=0,(mask&2)!=0,new Vec3(0,0,0),new Vec3(.6,0,.8));
            client.Send(new ViveProEyePoll(null,new ViveEyeFrame(number,number*8,clock.Now,false,null,null,ray),
                ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2));
        }
        static void Stage(object pump,ViveProEyeWorker worker)
        {
            object[] frame={null};
            Require((bool)Call(worker,"TryPollTaskForRecording",frame)&&
                (bool)Call(pump,"StageRigFrame",new[]{frame[0],(object)1.0}),"fresh raw frame could not be staged");
        }
        static void Mutate(object pump,Action operation)
        {Require((bool)Call(pump,"TryRunReservedOrdinaryMutation",operation),"synthetic reserved mutation failed");}
        static void BeginPanel(ViveStroopDevelopmentBootstrap owner,WorldSpaceExperimentView view,Camera camera,ViveStroopPreviewRig rig)
        {
            Require((bool)Call(owner,"PrepareRawNonTrialScene"),"owner could not prepare the initial non-trial panel");
            Call(rig,"GuardRawSceneBeforeCull",camera);
            Require(camera.cullingMask!=0,"prepared panel was not revealed");
            Call(view,"BeforeCameraCull",camera);
            Require((int)Get(view,"rawPanelRequired")==3,"actual panel cull did not request both Preparation renderers");
            Call(view,"ObserveViveRawPanelElement",camera,1);
            Call(view,"ObserveViveRawPanelElement",camera,2);
        }
        static void PracticeWitness(ViveStroopDevelopmentBootstrap owner,WorldSpaceExperimentView view,Camera camera,object pump,
            ViveDevelopmentParticipant task,ViveProEyeWorker worker,Client client,MonotonicClock clock,ViveStroopPreviewRig rig)
        {
            // Exercise all real guards from the first existing Legacy trial
            // onward. The Editor memory writer cannot provide a native plan
            // write receipt. Explicit fixture-only core setup below is not a
            // simulated receipt and does NOT test production plan admission.
            Send(client,clock,6,2);Stage(pump,worker);
            bool requiresReceipt=false;
            try{task.OwnerBeginPractice(Actor.Owner);}catch(InvalidOperationException){requiresReceipt=true;}
            Require(requiresReceipt&&task.CurrentTrial==null,"normal practice began without a plan receipt");
            Mutate(pump,()=>{
                Call(task,"PrepareLegacyRecordingBlockPlan",Actor.Owner,true);
                Call(task,"OwnerBeginPracticeCore",Actor.Owner);
            });
            Require((bool)Call(pump,"DrainTaskEvents"),"fixture practice setup did not drain");
            var trial=task.CurrentTrial;
            Require(task.Phase==Phase.Practice&&trial!=null&&!trial.Onset.HasValue,"fixture-only practice start did not create a pending trial");
            Call(view,"PrepareViveRawTaskView",task.ParticipantView);
            camera.cullingMask=0;
            Send(client,clock,7,2);Stage(pump,worker);
            // A new phase intentionally disarms held controls. Supply the
            // real neutral-release sample before the new response edge.
            Mutate(pump,()=>task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));
            Require((bool)Call(owner,"PrepareRawScenePresentation"),"real practice scene reservation was refused");
            Require(!trial.Onset.HasValue,"trial accepted an onset without a render callback");
            Call(rig,"GuardRawSceneBeforeCull",camera);
            Call(view,"BeforeCameraCull",camera);
            Call(view,"ObserveStimulusRender",camera);
            Call(view,"AfterCameraRender",camera);
            Require(trial.Onset.HasValue&&!owner.RecordingBlocked,"real software witness did not commit the practice onset");
            Send(client,clock,8,2);Stage(pump,worker);
            var ink=trial.Stimulus.Ink;
            double x=ink==Colour.Green?1:ink==Colour.Yellow?-1:0;
            double y=ink==Colour.Red?1:ink==Colour.Blue?-1:0;
            Mutate(pump,()=>task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y)));
            Require((bool)Call(pump,"DrainTaskEvents"),"practice response FIFO did not drain");
            Require(trial.Outcome!=TrialOutcome.Pending&&!owner.RecordingBlocked&&task.CurrentTrial!=trial,"first real guarded response did not advance practice: outcome="+trial.Outcome+", blocked="+owner.RecordingBlocked+", phase="+task.Phase);
        }
        // mode: 0 healthy metadata; 1 invalid→good; 2 aged+invalid; 3 stopped identity; 4 latched owner fault.
        static void Run(Scene scene,int mode,bool includePractice)
        {
            GameObject cameraObject=null,viewObject=null,rigObject=null,ownerObject=null;
            ViveProEyeWorker worker=null;object pump=null;Client client=null;
            try
            {
                cameraObject=Root(scene,"SyntheticCamera");var camera=cameraObject.AddComponent<Camera>();camera.cullingMask=0;
                viewObject=Root(scene,"SyntheticView");var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                view.ConfigureViveDevelopmentPreview(ViveFixedTrackpadMapping.Create(),.5);
                rigObject=Root(scene,"SyntheticRig");var rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                ownerObject=Root(scene,"SyntheticOwner");var owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();owner.Configure(rig,view);
                var clock=new MonotonicClock("synthetic-panel-epoch-clock");client=new Client();
                int headSequence=0;
                worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++headSequence,clock.Now,true,true,Rotation.Identity),()=>client);
                Require(worker.Start()&&client.Waiting.WaitOne(3000),"worker failed to start");
                var task=LegacyTask(clock);var a=typeof(ViveDevelopmentParticipant).Assembly;
                var admissionType=a.GetType("AvatarStroop.Runtime.ViveNamedSessionFrameAdmission",true);
                double choice=clock.Now;
                var admission=Factory(admissionType,"CreateLegacySyntheticForTest",task,worker,clock,64,choice,choice);
                Require((bool)Call(admission,"ConfirmTask")&&(bool)Call(admission,"ConfirmEye"),"fixture choice confirmation failed");
                var budgetType=a.GetType("AvatarStroop.Runtime.ViveRawByteBudget",true);
                Require((bool)Call(admission,"TryBindQueueBudget",Factory(budgetType,"SelectExplicit",33554432L)),"fixture budget bind failed");
                var pumpType=a.GetType("AvatarStroop.Runtime.ViveNamedRawRecordingPump",true);
                var writer=Activator.CreateInstance(pumpType.GetNestedType("SyntheticWriter",BindingFlags.NonPublic),Instance,null,new[]{admission},null);
                pump=Factory(pumpType,"StartLegacySyntheticWithStatusForTest",task,worker,clock,admission,writer);
                Set(owner,"SyntheticRawOwnerForFixture",true);Set(owner,"configured",true);Set(owner,"participant",task);Set(owner,"sharedClock",clock);Set(owner,"namedRawPump",pump);
                Set(rig,"previewCamera",camera);Set(rig,"view",view);Set(rig,"development",owner);Set(rig,"worker",worker);Set(rig,"running",true);
                Set(rig,"visibleMask",~0);Set(rig,"SyntheticRawTrackingForFixture",true);
                Call(owner,"Awake");
                client.Send(null);Send(client,clock,1,3);
                Require(Call(pump,"PrepareStatus").ToString()=="Ready","no initial authorized synthetic status baseline");
                Stage(pump,worker);
                Mutate(pump,()=>task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));
                Call(view,"PrepareViveRawTaskView",task.ParticipantView);
                view.SetPresentationAvailable(true);
                // Keep a real queued practice request, but no plan activity or
                // trial may result from the rejected Preparation witness.
                Call(owner,"CaptureRawPracticeRequest");
                int before=task.ReadEventsAfter(-1,4096).Count;
                BeginPanel(owner,view,camera,rig);
                var staged=Get(pump,"stagedEpoch");
                var run=Get(pump,"statusRun");
                if(mode==0||mode==4)Send(client,clock,2,2);
                if(mode==1||mode==2){Send(client,clock,2,1);Send(client,clock,3,3);}
                if(mode==2)Set(pump,"stagedMaximumAge",1e-12);
                if(mode==3)Require((bool)Call(worker,"TryRequestStop"),"synthetic stop request failed");
                if(mode==4)Set(owner,"observationFaultLatched",true);
                if(mode==0||mode==4)Require((int)Get(run,"StatusCount")==1,"did not publish exactly one benign status revision in render gap");
                Require(Get(pump,"stagedEpoch")==staged,"staged ticket changed before post-render");
                Call(view,"AfterCameraRender",camera);
                if(mode==3||mode==4)
                {
                    string reason=(string)Prop(pump,"FirstFault");
                    Require(camera.cullingMask==0&&Get(view,"rawPanelSeen")==null&&
                        (int)Get(owner,"pendingRawPanelFrame")==-1&&task.CurrentTrial==null&&
                        owner.RecordingBlocked&&(bool)Get(owner,"rawSafetyLatched")&&
                        (mode==4||reason!=null&&reason.Contains("WorkerRun")),
                        "real missing worker/task authority was not quarantined: "+reason);
                    return;
                }
                Require(!owner.RecordingBlocked&&task.Phase==Phase.Preparation&&task.ReadEventsAfter(-1,4096).Count==before&&
                    (bool)Get(owner,"rawPracticeRequested")&&(int)Get(owner,"rawPlanStartRequested")==0&&Get(owner,"rawPendingBlockPlan")==null,
                    "eye-only metadata changed the task or terminated its independent head authority");
                if(mode==2)
                    Require(camera.cullingMask==0&&Get(view,"rawPanelSeen")==null&&Get(pump,"stagedEpoch")==null,
                        "expired actual head snapshot was accepted as a current panel witness");
                else
                    Require(camera.cullingMask!=0&&Get(view,"rawPanelSeen")!=null&&(int)Get(owner,"pendingRawPanelFrame")==-1,
                        "healthy or invalid eye-only transition blocked the actual panel callback");
                Require((int)Get(run,"StatusCount")==((mode==1||mode==2)?2:1),
                    "render callback dropped or invented independent eye status records");
                Require(Call(pump,"PrepareStatus").ToString()=="Ready"&&(int)Get(run,"StatusCount")==0&&
                    Call(pump,"PrepareStatus").ToString()=="Ready","independent eye status FIFO did not drain exactly once");
                // Edit-mode harness executes the subsequent pass synchronously;
                // there is a new native eye sample and panel revision, not an
                // exported HMD frame or an optical latency assertion.
                Send(client,clock,5,2);Stage(pump,worker);
                Require(Get(pump,"stagedEpoch")!=staged,"the next pass reused the rejected epoch");
                Call(view,"PrepareViveRawTaskView",task.ParticipantView);view.SetPresentationAvailable(true);
                BeginPanel(owner,view,camera,rig);
                Call(view,"AfterCameraRender",camera);
                Require(!owner.RecordingBlocked&&Get(view,"rawPanelSeen")!=null&&(int)Get(owner,"pendingRawPanelFrame")==-1&&
                    task.CurrentTrial==null&&task.ReadEventsAfter(-1,4096).Count==before,
                    "fresh Preparation callback did not accept exactly the new witness");
                if(includePractice)PracticeWitness(owner,view,camera,pump,task,worker,client,clock,rig);
            }
            finally
            {
                if(pump!=null)Call(pump,"Stop");
                if(worker!=null){Call(worker,"TryRequestStop");client.Release(null);worker.Stop(1000);worker.Dispose();}
                if(ownerObject!=null)UnityEngine.Object.DestroyImmediate(ownerObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }
        static void Execute(Action<Scene> test)
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            try{Require(scene.IsValid()&&scene.isLoaded,"no isolated scene");test(scene);}
            finally{if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);}
        }
        // Baseline uses the same synthetic tracking/design seam and this test;
        // only the old post-render epoch logic is restored.
        public static void ValidateHealthyOnly(){Execute(scene=>Run(scene,0,false));}
        public static void Validate()
        {
            Execute(scene=>{Run(scene,0,true);Run(scene,1,false);Run(scene,2,false);Run(scene,3,false);Run(scene,4,false);});
            UnityEngine.Debug.Log("VIVE_PREPARATION_PANEL_EPOCH_SYNTHETIC_FIXTURE_PASSED: actual Preparation callback continues across healthy/invalid eye metadata, fresh head witness, fixture-only Legacy practice onset/response, expired head retry, stopped worker and latched owner quarantine. No XR, files, or production plan receipt.");
        }
    }
}
#endif
