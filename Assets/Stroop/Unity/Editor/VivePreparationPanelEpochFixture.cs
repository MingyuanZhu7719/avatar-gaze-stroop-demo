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
                (bool)Call(pump,"StageRigFrame",new[]{frame[0],(object)1.0}),"fresh raw frame could not be staged: "+Prop(pump,"FailureSnapshot"));
        }
        static void Mutate(object pump,Action operation)
        {Require((bool)Call(pump,"TryRunReservedOrdinaryMutation",operation),"synthetic reserved mutation failed: "+Prop(pump,"FailureSnapshot"));}
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

            var unrendered=task.CurrentTrial;
            Send(client,clock,9,2);Stage(pump,worker);
            Mutate(pump,()=>task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));
            Call(owner,"Render");
            Require((bool)Call(owner,"PrepareRawScenePresentation")&&Get(owner,"pendingRawPresentation")!=null,
                "next scored question did not reserve a presentation");
            Require((bool)Call(owner,"ExpirePendingRawPresentation")&&owner.RecordingBlocked&&!unrendered.Onset.HasValue,
                "missing trial callback was accepted as a retryable status panel");
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
        static ViveStroopDeviceFrame ControllerFrame(ViveStroopNeutralGate gate, bool phaseChanged,
            bool triggerHeld, bool padHeld, Colour colour, bool connected=true,bool poseLoss=false,bool deviceChanged=false,
            bool actionsPending=false)
        {
            var edges=gate.Observe(connected,phaseChanged,false,padHeld,triggerHeld,false);
            double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0;
            double y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
            var pad=new VivePreviewTrackpadSample(connected,padHeld,padHeld?x:0,padHeld?y:0);
            return (ViveStroopDeviceFrame)Activator.CreateInstance(typeof(ViveStroopDeviceFrame),Instance,null,
                new object[]{pad,edges.NeutralPending,edges.LostReady,triggerHeld,edges.TriggerDown,false,false,"synthetic wand",deviceChanged,actionsPending,poseLoss},null);
        }

        static void RenderTutorialFrame(ViveStroopDevelopmentBootstrap owner,ViveStroopPreviewRig rig,
            WorldSpaceExperimentView view,Camera camera)
        {
            Call(owner,"Render");
            Require((bool)Call(owner,"PrepareRawScenePresentation"),"released tutorial frame was not admitted");
            Call(rig,"GuardRawSceneBeforeCull",camera);
            Call(view,"BeforeCameraCull",camera);Call(view,"ObserveStimulusRender",camera);Call(view,"AfterCameraRender",camera);
        }

        static void TutorialControllerBoundaries(ViveStroopDevelopmentBootstrap owner,ViveStroopPreviewRig rig,
            WorldSpaceExperimentView view,Camera camera,object pump,ViveProEyeWorker worker,ViveDevelopmentParticipant task,bool enterExam)
        {
            var gate=new ViveStroopNeutralGate();
            var first=task.CurrentTrial;
            var onset=first.Onset;
            Stage(pump,worker);
            var held=ControllerFrame(gate,true,true,true,first.ExpectedResponse);
            Require(held.DeviceReady&&held.NeutralPending,"expected tracked wand waiting for release");
            Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,held));
            Require(!task.InputReady&&(bool)Prop(owner,"NamedRawInputBoundaryPending"),"tracked neutral wait entered trial preparation");
            Require((bool)Call(owner,"AwaitControllerRelease",held),"held input was accepted");
            Call(owner,"Render");
            Require(camera.cullingMask==0&&first.Onset==onset&&!first.Response.HasValue,"hold altered presented tutorial or exposed it");
            Require((bool)Call(owner,"DrainNamedRawTaskEvents")&&!owner.RecordingBlocked,"held tutorial faulted");

            Stage(pump,worker);
            var released=ControllerFrame(gate,false,false,false,first.ExpectedResponse);
            Require(!(bool)Call(owner,"AwaitControllerRelease",released),"release did not lift the mask");
            Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,released));
            RenderTutorialFrame(owner,rig,view,camera);
            Require(task.InputReady&&first.Onset==onset&&!first.Response.HasValue,"release duplicated onset or answered");
            var freshPress=ControllerFrame(gate,false,false,true,first.ExpectedResponse);
            Stage(pump,worker);Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,freshPress));
            Require(task.Phase==Phase.TutorialFeedback&&first.Correct==true,"fresh colour press did not score exactly after release");
            Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,released));
            Require((bool)Call(owner,"ApplyAuxiliaryInput",false,0,true),"feedback neutral failed");
            Require((bool)Call(owner,"ApplyAuxiliaryInput",true,0,true),"feedback Continue failed");
            var next=task.CurrentTrial;
            Require(task.Phase==Phase.Tutorial&&next!=first&&!next.Onset.HasValue&&
                (bool)Prop(owner,"NamedRawInputBoundaryPending"),"Continue permitted an onset on the held confirmation frame");
            Call(owner,"Render");
            Require((bool)Call(owner,"AwaitControllerRelease",ControllerFrame(gate,true,true,false,next.ExpectedResponse))&&
                camera.cullingMask==0&&!next.Response.HasValue&&!next.Onset.HasValue,"held trigger escaped the new trial boundary");
            Require((bool)Call(owner,"DrainNamedRawTaskEvents"),"Continue hold failed to drain");
            Stage(pump,worker);
            Require(!(bool)Call(owner,"AwaitControllerRelease",released),"second release did not arm the next trial");
            Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,released));RenderTutorialFrame(owner,rig,view,camera);
            Require(next.Onset.HasValue&&next.Outcome==TrialOutcome.Pending&&!owner.RecordingBlocked,"fresh trial onset failed");

            if(enterExam)
            {
                released=ControllerFrame(gate,false,false,false,next.ExpectedResponse);
                Stage(pump,worker);
                Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,released));
                Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,ControllerFrame(gate,false,false,true,next.ExpectedResponse)));
                Require(next.Correct==true&&task.Phase==Phase.TutorialFeedback,"second ink-colour answer did not reach feedback");
                Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,released));
                Require((bool)Call(owner,"ApplyAuxiliaryInput",false,0,true)&&
                    (bool)Call(owner,"ApplyAuxiliaryInput",false,1,true)&&task.TutorialMenuSelection==1,
                    "Start practice exam option could not be selected");
                Require((bool)Call(owner,"ApplyAuxiliaryInput",true,0,true)&&task.Phase==Phase.PracticeExamReady&&task.CurrentTrial==null,
                    "fresh trigger did not request the exam");
                Require((bool)Call(owner,"DrainNamedRawTaskEvents"),"exam request events did not drain");
                CheckExamHandoff(owner,rig,view,camera,pump,worker,task,gate,released);
                return;
            }
            Stage(pump,worker);
            var disconnected=ControllerFrame(gate,false,false,false,next.ExpectedResponse,false);
            Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,disconnected));
            Require(task.Phase==Phase.Paused&&!task.InputReady&&!(bool)Prop(owner,"NamedRawInputBoundaryPending")&&
                !next.Response.HasValue,"controller disconnection was treated as an ordinary held control");
        }

        static void CheckExamHandoff(ViveStroopDevelopmentBootstrap owner,ViveStroopPreviewRig rig,
            WorldSpaceExperimentView view,Camera camera,object pump,ViveProEyeWorker worker,ViveDevelopmentParticipant task,
            ViveStroopNeutralGate gate,ViveStroopDeviceFrame neutral)
        {
            // The real adapter owns the recovery gate; frames are supplied by this test.
            using(var adapter=new ViveStroopDeviceInput(VivePreviewControllerHand.Automatic))
            {
                Action nextFrame=()=>{
                    Require((bool)Call(owner,"DrainNamedRawTaskEvents"),"exam frame events did not drain");
                    Stage(pump,worker);
                };
                Set(owner,"device",adapter);
                Set(rig,"headReady",true);
                nextFrame();Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,neutral));
                Call(owner,"CaptureRawPracticeRequest");Call(owner,"RecordControllerUiPoll",true,true,"synthetic wand");
                Require(Call(owner,"PracticeExecutionBlocker")==null,"neutral exam request remained blocked");
                Call(owner,"ClearRawPracticeRequest");Call(owner,"RequestNamedBlockPlanStart",true);

                nextFrame();
                Require((bool)Call(owner,"HandleActiveRawTrackingController",ControllerFrame(gate,false,false,false,Colour.Red,false,true))&&
                    (bool)Prop(pump,"TrackingGapOpen")&&!owner.RecordingBlocked&&task.Phase==Phase.PracticeExamReady,
                    "loss of the same controller pose closed the pending exam");
                neutral=ControllerFrame(gate,false,false,false,Colour.Red);
                for(int i=0;i<3;i++)
                {
                    // This Editor fixture runs within one frame. Model successive frame numbers
                    // while the production recovery code counts each neutral observation.
                    Set(owner,"trackingNeutralFrame",Time.frameCount-1);
                    nextFrame();
                    Require((bool)Call(owner,"HandleActiveRawTrackingController",neutral)&&!owner.RecordingBlocked,
                        "recovering the same controller stopped the recording");
                }
                Require(!(bool)Prop(pump,"TrackingGapOpen")&&task.Phase==Phase.PracticeExamReady&&task.PracticeAttempt==0,
                    "tracking recovery consumed the pending exam start");
                // Receipt-ready polling sets this frame when the neutral controller can
                // act on the prompt. The in-memory writer cannot publish a native receipt.
                Set(owner,"focusPlanPromptFrame",Time.frameCount);
                Call(owner,"Render");Call(view,"LayoutHud");
                Require(((TextMesh)Get(view,"previewInputText")).text.IndexOf("trigger",StringComparison.OrdinalIgnoreCase)>=0,
                    "exam recovery panel does not tell the wearer to confirm with the trigger");
                var trigger=ControllerFrame(gate,false,true,false,Colour.Red);
                Require(!(bool)Call(owner,"ObservePreparedPlanStartConfirmation",neutral)&&
                    !(bool)Call(owner,"ObservePreparedPlanStartConfirmation",trigger)&&
                    (bool)Call(owner,"ObservePreparedPlanStartConfirmation",neutral),"prepared exam requires neutral, confirmation and release");
                Call(owner,"AwaitControllerRelease",neutral);
                nextFrame();
                Call(owner,"ProcessNamedBlockPlanStart",true,neutral);
                Require(Get(owner,"rawPendingBlockPlan")!=null&&task.Phase==Phase.PracticeExamReady&&task.PracticeAttempt==0&&
                    task.ReadEventsAfter(-1,4096).Count(e=>e.Kind=="block_plan_prepared"&&e.Fields["item_count"]=="10")==1,
                    "missing or duplicate ten-question practice plan");
                Require((bool)Call(owner,"DrainNamedRawTaskEvents"),"exam plan events did not drain");
                Require(!(bool)Call(owner,"ServicePracticeCountdown")&&(bool)Prop(owner,"PracticeCountdownVisible"),
                    "exam did not start the visible countdown");
                Call(rig,"GuardRawSceneBeforeCull",camera);
                var hold=Get(rig,"rawHoldView");
                Require(hold!=null&&(bool)Get(rig,"practiceCountdownRenderPending")&&
                    ((TextMesh)Get(hold,"text")).text.Contains("Practice exam starts in"),"countdown was hidden in the headset hold layer");
                Call(rig,"AfterRawHoldCameraRender",camera);
                var countdown=Get(owner,"practiceCountdown");
                int first=(int)Get(countdown,"lastFrame");double start=(double)Get(countdown,"lastTime");
                Require(first==Time.frameCount&&camera.cullingMask==0,"countdown camera callback did not acknowledge the frame");
                // Advance the same timer with consecutive software render times. Duplicate
                // stereo callbacks cannot advance it; no five seconds pass off screen.
                for(int i=1;i<=100;i++)
                {
                    Require(!(bool)Call(countdown,"CompleteForFrame",start+i*.05,first+i),"exam countdown completed early");
                    Call(countdown,"ObserveRendered",start+i*.05,first+i);
                }
                Require((bool)Call(countdown,"CompleteForFrame",start+5.05,first+101),"five rendered seconds did not release the countdown");
                // The synthetic Editor writer cannot return a native file receipt. Start at
                // its accepted-plan boundary, then exercise the real first question camera/input.
                Call(owner,"ResetPracticeCountdown");nextFrame();
                Action startExam=()=>Call(task,"OwnerBeginPracticeCore",Actor.Owner);
                Mutate(pump,()=>Call(task,"RunNamedRecordingMutation",startExam,0,null,false));
                Set(owner,"rawPlanStartRequested",0);Set(owner,"rawPendingBlockPlan",null);
                var firstExam=task.CurrentTrial;
                Require(task.Phase==Phase.Practice&&task.PracticeAttempt==1&&firstExam!=null&&!firstExam.Onset.HasValue,"missing first exam question");
                nextFrame();
                var examHold=ControllerFrame(gate,true,true,false,Colour.Red);
                Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,examHold));
                Require((bool)Prop(owner,"NamedRawInputBoundaryPending")&&(bool)Call(owner,"AwaitControllerRelease",examHold)&&
                    !firstExam.Onset.HasValue&&task.Phase==Phase.Practice&&!owner.RecordingBlocked,"held trigger blocked or answered the first exam question");
                nextFrame();neutral=ControllerFrame(gate,false,false,false,Colour.Red);
                Require(!(bool)Call(owner,"AwaitControllerRelease",neutral),"exam release did not arm the new trial");
                Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,neutral));
                RenderTutorialFrame(owner,rig,view,camera);
                Require(firstExam.Onset.HasValue&&!firstExam.Response.HasValue&&task.FormalEyeRecordingWindowStart==null&&
                    !owner.RecordingBlocked,"first exam onset or dormant eye boundary failed");
                Set(owner,"device",null);
            }
        }

        public static void ValidateTaskOnlyTutorial(){RunTaskOnlyTutorial(false);}
        public static void ValidatePracticeExamHandoff(){RunTaskOnlyTutorial(true);}
        public static void ValidateFirstFormalEyeCountdown(){RunTaskOnlyTutorial(true,true);}
        static void CheckFormalPreparation(ViveStroopDevelopmentBootstrap owner,ViveStroopPreviewRig rig,
            WorldSpaceExperimentView view,Camera camera,object pump,ViveProEyeWorker worker,ViveDevelopmentParticipant task,
            Client client,MonotonicClock clock,bool startFormal)
        {
            var gate=new ViveStroopNeutralGate();
            var neutral=ControllerFrame(gate,false,false,false,Colour.Red);
            for(int i=0;i<10;i++)
            {
                var trial=task.CurrentTrial;
                Require(trial!=null&&task.Phase==Phase.Practice,"practice exam ended before question "+(i+1));
                Require((bool)Call(pump,"DrainTaskEvents"),"exam event drain failed");Stage(pump,worker);
                Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,neutral));
                if(!trial.Onset.HasValue)
                {
                    RenderTutorialFrame(owner,rig,view,camera);
                    Stage(pump,worker);
                }
                Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,ControllerFrame(gate,false,false,true,trial.ExpectedResponse)));
                Require(trial.Correct==true,"practice exam answer failed");
            }
            Require(task.Phase==Phase.PracticeExamResult&&task.PracticeCorrect==10&&!worker.Started,
                "formal warm-up started during training or exam did not pass");
            Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,neutral));
            Mutate(pump,()=>task.ContinuePracticeExamResult());
            Require(task.Phase==Phase.BlockPreparation&&task.FormalEyeRecordingWindowStart==null,
                "exam result did not reach the formal plan gate");
            Set(rig,"taskOnlyEyeSource",true);
            Call(rig,"ServiceFormalEyeWarmup");
            Require((bool)Prop(rig,"FormalEyeWarmupRequested")&&client.Waiting.WaitOne(3000),
                "eye SDK warm-up did not start at formal preparation");
            Send(client,clock,1,3);

            Require((bool)Call(pump,"DrainTaskEvents"),"formal preparation events did not drain");Stage(pump,worker);
            Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,neutral));Call(owner,"Render");
            Require((bool)Call(owner,"PrepareRawNonTrialScene")&&Get(view,"rawPanelExpected")!=null,
                "formal plan panel did not reserve its first render");
            Call(rig,"GuardRawSceneBeforeCull",camera);Call(view,"BeforeCameraCull",camera);
            Call(view,"ObserveViveRawPanelElement",camera,1);Call(view,"AfterCameraRender",camera);
            Require(Get(view,"rawPanelSeen")==null&&Get(view,"rawPanelExpected")!=null,"partial panel generated a witness");
            Call(owner,"RequestFormalConfirmation");
            Call(owner,"ServiceFormalConfirmationRequest",neutral);
            Require(Get(owner,"formalConfirmationRequest")!=null&&Get(owner,"pendingRawOwnerAction")==null,
                "partial panel allowed confirmation");
            Require(!(bool)Call(owner,"ExpirePendingRawPresentation")&&!owner.RecordingBlocked&&camera.cullingMask==0&&
                Get(view,"rawPanelSeen")==null&&Get(view,"rawPanelExpected")==null&&Get(pump,"stagedEpoch")==null,
                "missing formal plan panel revoked the recording or retained its stale witness");
            Call(view,"ObserveViveRawPanelElement",camera,2);Call(view,"AfterCameraRender",camera);
            Require(Get(view,"rawPanelSeen")==null&&!(bool)Call(owner,"FormalConfirmationPanelCurrent"),
                "late callback authorized the discarded formal panel");
            Require(task.Phase==Phase.BlockPreparation&&task.CurrentTrial==null&&task.FormalEyeRecordingWindowStart==null&&
                !(bool)Prop(worker,"FormalEyePersistenceEnabled"),"panel retry changed the task or started eye recording");
            Stage(pump,worker);Call(owner,"Render");BeginPanel(owner,view,camera,rig);Call(view,"AfterCameraRender",camera);
            var seen=Get(view,"rawPanelSeen");
            Require(seen!=null&&!owner.RecordingBlocked&&(bool)Call(view,"HasPriorViveRawPanelWitness",
                task.ParticipantView,task.QuestionnaireInstructionPending,task.CandidateRating,0,Time.frameCount+1),
                "fresh formal plan render did not create a new valid witness");
            // The batch entry point runs in one Unity frame; move the accepted render
            // to the immediately preceding frame before exercising the owner poll.
            Set(seen,"Frame",Time.frameCount-1);
            Stage(pump,worker);Call(owner,"ServiceFormalConfirmationRequest",neutral);
            var confirmation=(Action)Get(owner,"pendingRawOwnerAction");
            Require(confirmation!=null&&(bool)Call(owner,"PendingRawOwnerStillCurrent"),"fresh panel did not authorize the owner request");
            Call(owner,"ClearPendingRawOwnerAction");Mutate(pump,confirmation);
            Require(task.Phase==Phase.BlockReady&&task.CurrentTrial==null&&!owner.RecordingBlocked&&
                task.FormalEyeRecordingWindowStart==null,"owner confirmation did not reach BlockReady safely");
            Require((bool)Call(pump,"DrainTaskEvents"),"formal confirmation events did not drain");
            Stage(pump,worker);Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,neutral));Call(owner,"Render");
            Require((bool)Call(owner,"PrepareRawNonTrialScene")&&Get(view,"rawPanelExpected")!=null&&
                !(bool)Call(owner,"ExpirePendingRawPresentation")&&!owner.RecordingBlocked&&
                Get(view,"rawPanelExpected")==null&&task.Phase==Phase.BlockReady&&task.CurrentTrial==null,
                "missing first BlockReady status panel started a trial or stopped the recording");
            if(startFormal)CheckFirstFormalEyeCountdown(owner,rig,view,camera,pump,worker,task,client,clock);
            else CheckFormalControllerRecovery(owner,rig,camera,pump,worker,task);
        }

        static void WhileEyePublicationLocked(ViveProEyeWorker worker,Action action)
        {
            var gate=Prop(worker,"RecordingTicketGate");
            using(var acquired=new ManualResetEventSlim())
            using(var release=new ManualResetEventSlim())
            {
                var thread=new Thread(()=>{lock(gate){acquired.Set();release.Wait(5000);}}){IsBackground=true};
                thread.Start();
                try
                {
                    Require(acquired.Wait(3000),"could not create eye publisher contention");
                    action();
                    Require(thread.IsAlive,"owner waited for the publisher lock instead of using the independent head frame");
                }
                finally{release.Set();thread.Join();}
            }
        }

        static void CheckFirstFormalEyeCountdown(ViveStroopDevelopmentBootstrap owner,ViveStroopPreviewRig rig,
            WorldSpaceExperimentView view,Camera camera,object pump,ViveProEyeWorker worker,ViveDevelopmentParticipant task,
            Client client,MonotonicClock clock)
        {
            Set(rig,"clock",clock);
            var eyePumpType=typeof(ViveStroopPreviewRig).Assembly.GetType("AvatarStroop.Unity.ViveStroopEyePump",true);
            Set(rig,"eyePump",Activator.CreateInstance(eyePumpType,Instance,null,new object[]{worker},null));
            int sample=2;
            double deadline=clock.Now+3;
            while(!(bool)Prop(owner,"FormalEyeReadyForFirstBlock")&&clock.Now<deadline)
            {Thread.Sleep(20);Send(client,clock,sample++,3);}
            Require((bool)Prop(owner,"FormalEyeReadyForFirstBlock")&&!(bool)Prop(worker,"FormalEyePersistenceEnabled"),
                "eye warm-up failed to enable the formal start button");
            var gate=new ViveStroopNeutralGate();
            var neutral=ControllerFrame(gate,true,false,false,Colour.Red);
            using(var adapter=new ViveStroopDeviceInput(VivePreviewControllerHand.Automatic))
            {
                Set(owner,"device",adapter);Stage(pump,worker);
                Call(owner,"RequestNamedBlockPlanStart",false);Call(owner,"ProcessNamedBlockPlanStart",true,neutral);
                Require(Get(owner,"rawPendingBlockPlan")!=null&&task.Phase==Phase.BlockReady,"formal plan did not reach the receipt boundary");
                // The Editor memory writer cannot issue the native file receipt. Exercise
                // the same formal task mutation immediately following an accepted receipt.
                Require((bool)Call(pump,"DrainTaskEvents"),"formal plan failed to drain");Stage(pump,worker);
                Action begin=()=>Call(task,"OwnerBeginBlockCore",Actor.Owner);
                Mutate(pump,()=>Call(task,"RunNamedRecordingMutation",begin,0,null,false));
                Set(owner,"rawPendingBlockPlan",null);Set(owner,"rawPlanStartRequested",0);
                Require(task.Phase==Phase.Countdown&&task.FormalEyeRecordingWindowStart.HasValue&&
                    (bool)Call(owner,"HoldFormalEyeActivation")&&camera.cullingMask==0,
                    "training epoch was permitted to present the first formal countdown");

                Func<bool> stage=()=>{
                    object[] poll={null};
                    Require((bool)Call(worker,"TryPollTaskForRecording",poll),"current independent head observation unavailable");
                    object[] args={poll[0],false};
                    bool admitted=(bool)Call(owner,"StageNamedRawFrame",args);
                    if(!admitted&&(bool)args[1])Call(rig,"HoldRawBusy");
                    return admitted;
                };
                WhileEyePublicationLocked(worker,()=>{
                    Require(!stage()&&!owner.RecordingBlocked&&!(bool)Prop(worker,"FormalEyePersistenceEnabled")&&
                        (bool)Prop(task,"RawCountdownAwaitingSoftwareWitness"),"contended initial activation did not remain masked");
                });
                Send(client,clock,sample++,3);
                Require(Call(owner,"PrepareNamedRawStatus").ToString()=="Ready"&&(bool)Prop(worker,"FormalEyePersistenceEnabled"),
                    "eye persistence never activated on the warm worker");
                // The first completed poll was already waiting inside the native
                // client before persistence opened. The following poll starts inside it.
                Send(client,clock,sample++,3);
                Send(client,clock,sample++,3);
                Require(Call(owner,"PrepareNamedRawStatus").ToString()=="Ready","first formal SDK acquisition did not drain");
                Require(stage(),"first post-boundary eye frame was not admitted");
                var payload=Get(pump,"stagedEpoch");
                Require(!(bool)Prop(payload,"HeadOnly")&&(double)Get(payload,"EyeObserved")>=task.FormalEyeRecordingWindowStart.Value,
                    "formal head frame lacks an actual post-boundary eye sample: "+Get(payload,"EyeUnavailable"));
                Require((bool)Call(owner,"DrainNamedRawTaskEvents"),"formal eye frame failed to drain");

                Action<Action> contended=work=>WhileEyePublicationLocked(worker,()=>{
                    Require(stage()&&!owner.RecordingBlocked,"active publisher contention blocked task authority");
                    var ticket=Get(pump,"stagedEpoch");
                    Require((bool)Prop(ticket,"HeadOnly")&&!(bool)Get(ticket,"EyeNotRequested")&&Get(ticket,"EyeUnavailable")!=null,
                        "contended formal frame claimed a gaze sample");
                    work();
                });
                contended(()=>{
                    Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,neutral));Call(owner,"Render");
                    Require((bool)Call(owner,"PrepareRawNonTrialScene"),"formal countdown panel refused before its first witness");
                    Call(rig,"GuardRawSceneBeforeCull",camera);Call(view,"BeforeCameraCull",camera);
                    Require((int)Get(view,"rawPanelRequired")==7,"formal countdown omitted the numbered renderer");
                    foreach(int bit in new[]{1,2,4})Call(view,"ObserveViveRawPanelElement",camera,bit);
                    Call(view,"AfterCameraRender",camera);
                    Require(!(bool)Prop(task,"RawCountdownAwaitingSoftwareWitness")&&Get(view,"rawPanelSeen")!=null&&
                        !owner.RecordingBlocked&&task.CurrentTrial==null,"first formal countdown witness failed under contention");
                });
                double firstWitness=clock.Now;
                while(task.Phase==Phase.Countdown&&clock.Now<firstWitness+7)
                {
                    Thread.Sleep(50);Send(client,clock,sample++,3);
                    Require(stage(),"formal countdown lost an admitted frame");Mutate(pump,task.Tick);
                    Require((bool)Call(owner,"DrainNamedRawTaskEvents"),"formal countdown journals did not drain");
                }
                Require(task.Phase==Phase.MainTask&&clock.Now>=firstWitness+4.9&&task.CurrentTrial!=null,
                    "five-second formal countdown did not start a trial");
                var first=task.CurrentTrial;
                contended(()=>{
                    var held=ControllerFrame(gate,true,true,false,first.ExpectedResponse);
                    Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,held));
                    Require((bool)Prop(owner,"NamedRawInputBoundaryPending")&&(bool)Call(owner,"AwaitControllerRelease",held)&&
                        !first.Onset.HasValue&&task.Phase==Phase.MainTask&&!owner.RecordingBlocked,"held trigger interrupted the first formal trial");
                    Require((bool)Call(owner,"DrainNamedRawTaskEvents"),"formal trigger hold did not drain");
                });
                contended(()=>{
                    neutral=ControllerFrame(gate,false,false,false,first.ExpectedResponse);
                    Require(!(bool)Call(owner,"AwaitControllerRelease",neutral),"formal release did not arm the new trial");
                    Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,neutral));RenderTutorialFrame(owner,rig,view,camera);
                    Require(first.Onset.HasValue&&!first.Response.HasValue&&!owner.RecordingBlocked,"formal onset failed under publisher contention");
                });
                contended(()=>Mutate(pump,()=>Call(owner,"ApplyInputFrame",true,ControllerFrame(gate,false,false,true,first.ExpectedResponse))));
                Require(first.Correct==true&&task.CurrentTrial!=first&&!owner.RecordingBlocked,"first formal answer failed under publisher contention");
                Require((bool)Call(owner,"DrainNamedRawTaskEvents"),"first formal answer failed to drain");
                object[] beforeRevoke={null};Require((bool)Call(worker,"TryPollTaskForRecording",beforeRevoke),"no candidate for revocation control");
                Call(pump,"Withdraw");
                WhileEyePublicationLocked(worker,()=>{
                    object[] args={beforeRevoke[0],false};
                    Require(!(bool)Call(owner,"StageNamedRawFrame",args)&&!(bool)args[1]&&owner.RecordingBlocked,
                        "revoked formal recording accepted a new frame while its publisher was busy");
                });
                Set(owner,"device",null);
            }
        }
        static void CheckFormalControllerRecovery(ViveStroopDevelopmentBootstrap owner,ViveStroopPreviewRig rig,
            Camera camera,object pump,ViveProEyeWorker worker,ViveDevelopmentParticipant task)
        {
            using(var wand=new SyntheticVivePoseDevice(useVendorOverlay:false))
            using(var adapter=new ViveStroopDeviceInput(VivePreviewControllerHand.Automatic))
            {
                Set(owner,"device",adapter);
                Set(owner,"questionnaireGate",new ViveQuestionnaireTrackpadGate(.5));
                wand.SetPose(Vector3.zero,Quaternion.identity);
                wand.PrepareBindings(adapter);
                var gate=new ViveStroopNeutralGate();
                Func<ViveStroopDeviceFrame,bool> dispatch=frame=>{
                    Require((bool)Call(pump,"DrainTaskEvents"),"formal controller frame events did not drain");
                    Stage(pump,worker);
                    return (bool)Call(owner,"HandleRawControllerBeforeDispatch",frame);
                };
                Call(owner,"InitializeRawFocusObservations",true,true);
                Stage(pump,worker);
                Require((bool)Call(owner,"ProcessRawFocusFrame",true),"healthy formal focus baseline missing");
                int baseline=task.ReadEventsAfter(-1,4096).Count(e=>e.Kind=="focus_changed");

                // The synthetic rig reports focused. In a batch editor without an XR loader,
                // the actual action adapter reads the real vendor focus value as false.
                Require((bool)Prop(rig,"XrFocused"),"fixture has no focused rig");
                Call(owner,"PollWithFocus",true,true);
                Require(!owner.RecordingBlocked&&(bool)Get(owner,"rawXrFocusLossUnrecorded")&&camera.cullingMask==0,
                    "focus loss during the actual controller poll was treated as a disconnected wand");
                Require((bool)Call(owner,"PrepareRawFocusBoundary",true),"focus restoration guard refused the reacquired session");
                Require((bool)Call(pump,"DrainTaskEvents"),"focus boundary failed to drain");Stage(pump,worker);
                Require((bool)Call(owner,"ProcessRawFocusFrame",true),"transient XR focus change was lost");
                var changes=task.ReadEventsAfter(-1,4096).Where(e=>e.Kind=="focus_changed").Skip(baseline).ToArray();
                Require(changes.Length==2&&changes[0].Fields["xr_focused"]=="False"&&changes[1].Fields["xr_focused"]=="True",
                    "short XR focus loss and restoration were not both journaled");
                Require(dispatch(ControllerFrame(gate,false,true,false,Colour.Red))&&!owner.RecordingBlocked,
                    "held trigger resumed the task on return from the dashboard");
                var neutral=ControllerFrame(gate,false,false,false,Colour.Red);
                Require(!dispatch(neutral)&&!(bool)Call(owner,"AwaitControllerRelease",neutral)&&
                    task.Phase==Phase.BlockReady&&task.CurrentTrial==null&&task.FormalEyeRecordingWindowStart==null&&
                    !(bool)Prop(worker,"FormalEyePersistenceEnabled")&&(int)Get(owner,"rawPlanStartRequested")==0,
                    "recovery started a block, recorded eye data or left neutral input blocked");
                // Action deactivation may precede any reported XR focus loss.
                var inactive=ControllerFrame(gate,false,false,false,Colour.Red,connected:false,actionsPending:true);
                Require(dispatch(inactive)&&!owner.RecordingBlocked&&camera.cullingMask==0&&
                    task.Phase==Phase.BlockReady&&task.CurrentTrial==null,"inactive actions stopped or started the waiting formal task");
                Require(dispatch(ControllerFrame(gate,false,true,false,Colour.Red))&&!owner.RecordingBlocked,
                    "held trigger crossed the action recovery boundary");
                neutral=ControllerFrame(gate,false,false,false,Colour.Red);
                Require(!dispatch(neutral)&&!(bool)Call(owner,"AwaitControllerRelease",neutral),
                    "neutral same-wand recovery did not release the action hold");
                Require(dispatch(inactive)&&!owner.RecordingBlocked,"repeat action hold incorrectly stopped recording");
                Require(dispatch(ControllerFrame(gate,false,false,false,Colour.Red,deviceChanged:true))&&owner.RecordingBlocked,
                    "a different controller was accepted during the action hold");
                Set(owner,"device",null);
            }
        }
        static void RunTaskOnlyTutorial(bool enterExam,bool startFormal=false)
        {
            Execute(scene=>{
                GameObject cameraObject=null,viewObject=null,rigObject=null,ownerObject=null;
                ViveProEyeWorker worker=null;object pump=null;var client=new Client();
                try
                {
                    cameraObject=Root(scene,"TaskOnlyTutorialCamera");var camera=cameraObject.AddComponent<Camera>();camera.cullingMask=0;
                    viewObject=Root(scene,"TaskOnlyTutorialView");var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                    view.ConfigureViveDevelopmentPreview(ViveFixedTrackpadMapping.Create(),.5);
                    rigObject=Root(scene,"TaskOnlyTutorialRig");var rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                    ownerObject=Root(scene,"TaskOnlyTutorialOwner");var owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();owner.Configure(rig,view);
                    var clock=new MonotonicClock("task-only-tutorial-render");int heads=0;
                    worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++heads,clock.Now,true,true,Rotation.Identity),()=>{
                        if(!enterExam)throw new InvalidOperationException("Tutorial must not start the SDK");
                        return client;
                    });
                    Require((bool)Call(worker,"StartTaskOnly"),"dormant worker failed to start");
                    var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"task-only-tutorial-geometry");
                    var allocation=new Allocation("task-only-tutorial-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                    var design=ViveLegacyRecordingDesign.Select(Actor.Owner,"task-only-tutorial-design",allocation,geometry.Version,ViveFixedTrackpadMapping.Version);
                    var config=SessionConfiguration.WithFormalEyeRecordingOnly(SessionConfiguration.WithTrainingProtocol(
                        SessionConfiguration.LegacyRecordingDevelopment(new SessionConfiguration("task-only-tutorial",73,allocation,RunKind.Simulation,"en",5,30,true,0,true,true,true),
                            design,ViveLegacyRecordingIntent.Select(Actor.Owner,"task-only-tutorial-intent",true,true)),TrainingProtocol.TutorialExamV1));
                    var task=new ViveDevelopmentParticipant(config,clock,geometry,ViveFixedTrackpadMapping.Create(),.5,true);var a=typeof(ViveDevelopmentParticipant).Assembly;
                    var admission=Factory(a.GetType("AvatarStroop.Runtime.ViveNamedSessionFrameAdmission",true),"CreateLegacySyntheticForTest",task,worker,clock,256,clock.Now,clock.Now);
                    Require((bool)Call(admission,"ConfirmTask")&&(bool)Call(admission,"ConfirmEye"),"task-only tutorial choices failed");
                    Require((bool)Call(admission,"TryBindQueueBudget",Factory(a.GetType("AvatarStroop.Runtime.ViveRawByteBudget",true),"SelectExplicit",33554432L)),"task-only tutorial budget failed");
                    var pumpType=a.GetType("AvatarStroop.Runtime.ViveNamedRawRecordingPump",true);
                    var writer=Activator.CreateInstance(pumpType.GetNestedType("SyntheticWriter",BindingFlags.NonPublic),Instance,null,new[]{admission},null);
                    pump=Factory(pumpType,"StartLegacySyntheticWithStatusForTest",task,worker,clock,admission,writer);
                    Set(owner,"SyntheticRawOwnerForFixture",true);Set(owner,"configured",true);Set(owner,"participant",task);Set(owner,"sharedClock",clock);Set(owner,"namedRawPump",pump);
                    Set(rig,"previewCamera",camera);Set(rig,"view",view);Set(rig,"development",owner);Set(rig,"worker",worker);Set(rig,"running",true);
                    Set(rig,"visibleMask",~0);Set(rig,"SyntheticRawTrackingForFixture",true);Call(owner,"Awake");
                    Require(Call(pump,"PrepareStatus").ToString()=="Ready","dormant status unavailable");Stage(pump,worker);
                    Mutate(pump,()=>task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));
                    Mutate(pump,()=>task.OwnerBeginTutorial(Actor.Owner));Call(owner,"Render");
                    Require(task.Phase==Phase.Tutorial&&task.CurrentTrial!=null&&!task.CurrentTrial.Onset.HasValue,"tutorial did not create pending first trial");
                    Require((bool)Call(pump,"DrainTaskEvents"),"tutorial start events did not drain");
                    Require(Call(pump,"PrepareStatus").ToString()=="Ready","next dormant status unavailable");Stage(pump,worker);
                    Mutate(pump,()=>task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));Call(owner,"Render");
                    Require((bool)Call(owner,"PrepareRawScenePresentation"),"actual dormant tutorial scene preparation refused; input="+task.InputReady+"; identity="+Get(view,"displayedTrial")+"; blocked="+owner.PresentationBlocked);
                    Call(rig,"GuardRawSceneBeforeCull",camera);Call(view,"BeforeCameraCull",camera);Call(view,"ObserveStimulusRender",camera);Call(view,"AfterCameraRender",camera);
                    Require(task.CurrentTrial.Onset.HasValue&&!owner.RecordingBlocked,"tutorial first actual camera witness failed");
                    TutorialControllerBoundaries(owner,rig,view,camera,pump,worker,task,enterExam);
                    if(enterExam)CheckFormalPreparation(owner,rig,view,camera,pump,worker,task,client,clock,startFormal);
                    Debug.Log(startFormal?"VIVE_FIRST_FORMAL_EYE_COUNTDOWN_PASSED: real formal eye activation, contended first countdown witness, five-second countdown, formal trial onset and answer, revoked recording rejected.":
                        enterExam?"VIVE_PRACTICE_EXAM_HANDOFF_PASSED: exam, formal eye warm-up, panel redraw, BlockReady dashboard focus loss, inactive actions, neutral recovery and rejected replacement wand.":
                        "VIVE_TASK_ONLY_TUTORIAL_RENDER_FIXTURE_PASSED: dormant-worker tutorial, initial camera onset, tracked controller release, fresh answer, feedback Continue, second camera onset and disconnected controller.");
                }
                finally
                {
                    if(pump!=null)Call(pump,"Stop");
                    if(worker!=null){Call(worker,"TryRequestStop");client.Release(null);worker.Stop(1000);worker.Dispose();}
                    client.Waiting.Dispose();client.Permit.Dispose();
                    foreach(var item in new[]{ownerObject,rigObject,viewObject,cameraObject})if(item!=null)UnityEngine.Object.DestroyImmediate(item);
                }
            });
        }
        public static void Validate()
        {
            Execute(scene=>{Run(scene,0,true);Run(scene,1,false);Run(scene,2,false);Run(scene,3,false);Run(scene,4,false);});
            UnityEngine.Debug.Log("VIVE_PREPARATION_PANEL_EPOCH_SYNTHETIC_FIXTURE_PASSED: actual Preparation callback continues across healthy/invalid eye metadata, fresh head witness, fixture-only Legacy practice onset/response, expired head retry, stopped worker and latched owner quarantine. No XR, files, or production plan receipt.");
        }
    }
}
#endif
