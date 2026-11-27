#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Exercises the production input decision and one-use worker claim with
    // real fake-provider preflight metadata. No SDK, XR, writer or policy bypass.
    // Native isolated tests separately exercise the installed writer route.
    public static class ViveRecordingStartReadinessFixture
    {
        const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static int assertions;
        static void Require(bool value,string label)
        {if(!value)throw new InvalidOperationException("Recording start readiness: "+label);assertions++;}
        static void Wait(WaitHandle value){Require(value.WaitOne(3000),"synthetic provider timed out");}
        static object Get(object target,string name){return target.GetType().GetField(name,Instance).GetValue(target);}
        static void Set(object target,string name,object value){target.GetType().GetField(name,Instance).SetValue(target,value);}
        static object Property(object target,string name){return target.GetType().GetProperty(name,Instance).GetValue(target,null);}
        static object Call(object target,string name,params object[] args)
        {return target.GetType().GetMethod(name,Instance).Invoke(target,args);}
        static object Factory(Type type,string name,params object[] args)
        {return type.GetMethod(name,Static).Invoke(null,args);}
        sealed class Client : IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> values=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            public string Diagnostic {get{return "synthetic asynchronous recording-start client";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Waiting.Set();if(!Permit.WaitOne(5000))throw new TimeoutException();lock(values)return values.Dequeue();}
            internal void Queue(ViveProEyePoll value){lock(values)values.Enqueue(value);Permit.Set();}
            internal void Send(ViveProEyePoll value){Queue(value);Wait(Waiting);}
            public void Dispose(){}
        }
        static ViveProEyePoll Poll(double time,int frame,bool valid)
        {
            var ray=new ViveEyeRay(valid?2UL:0UL,false,valid,new Vec3(0,0,0),new Vec3(0,0,1));
            return new ViveProEyePoll(null,new ViveEyeFrame(frame,frame*8,time,false,null,null,ray),
                ViveEyeRuntimeState.Working,valid?ViveEyeCalibrationState.ReportedReady:ViveEyeCalibrationState.Required,true,0,2);
        }
        static object Readiness(ViveProEyeWorker worker){return Call(worker,"ReadRecordingStartReadiness");}
        static bool Ready(ViveProEyeWorker worker){return (bool)Property(Readiness(worker),"Ready");}
        static void MakeReady(ViveProEyeWorker worker,Client client,MonotonicClock clock,ref int frame)
        {
            double deadline=clock.Now+4;
            do{client.Send(Poll(clock.Now,frame++,true));if(Ready(worker))return;Thread.Sleep(20);}while(clock.Now<deadline);
            Require(false,"real synthetic worker never completed its bounded valid eye streak");
        }
        static object Choices(MonotonicClock clock,bool confirmEye=true)
        {
            var type=typeof(ViveProEyeWorker).Assembly.GetType("AvatarStroop.Runtime.ViveNamedRecordingSetup",true);
            object[] args={true,@"C:\SyntheticStartFolderNotOpened","32",new Func<string,bool>(path=>true),null};
            var choices=Factory(type,"TryPrepare",args);Require(choices!=null,"file-free choice setup failed");
            Call(choices,"Observe",true,true,false,false,false,false,clock);
            Call(choices,"Observe",true,true,true,false,false,false,clock);
            if(confirmEye)
            {
                Call(choices,"Observe",true,true,false,false,false,false,clock);
                Call(choices,"Observe",true,true,true,false,false,false,clock);
            }
            return choices;
        }
        static bool Inputs(object choices,ViveProEyeWorker worker,bool focus=true,bool head=true,bool workerReady=true,int neutral=41,int current=41)
        {return (bool)Factory(typeof(ViveStroopDevelopmentBootstrap),"RawStartInputsReady",choices,focus,head,workerReady,Readiness(worker),neutral,current);}
        static object Claim(ViveProEyeWorker worker)
        {
            var type=typeof(ViveProEyeWorker).Assembly.GetType("AvatarStroop.Runtime.ViveRecordingStartClaim",true);
            return Activator.CreateInstance(type,Instance,null,new object[]{worker},null);
        }
        static string Attempt(object claim,Func<bool> context,Func<bool> inputs,Action action)
        {return Call(claim,"TryExecute",context,inputs,action).ToString();}
        public static void ValidateTaskOnly()
        {
            ValidateTaskOnlyMode(false);
            ValidateTaskOnlyMode(true);
        }
        static void ValidateTaskOnlyMode(bool participantMode)
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var rigObject=new GameObject("TaskOnlyTrainingRig");var ownerObject=new GameObject("TaskOnlyTrainingOwner");
            SceneManager.MoveGameObjectToScene(rigObject,scene);SceneManager.MoveGameObjectToScene(ownerObject,scene);
            ViveProEyeWorker worker=null;object rawPump=null;int factories=0;
            try
            {
                var clock=new MonotonicClock("task-only-training-input-fixture");
                long headSequence=0;
                worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++headSequence,clock.Now,true,true,Rotation.Identity),
                    ()=>{Interlocked.Increment(ref factories);throw new InvalidOperationException("SDK must remain unopened");});
                Require((bool)Call(worker,"StartTaskOnly"),"dormant carrier did not start");
                var choices=Choices(clock);
                Func<bool> inputs=()=>(bool)Factory(typeof(ViveStroopDevelopmentBootstrap),"RawTaskOnlyStartInputsReady",choices,true,true,true,41,41);
                Require(inputs()&&!Ready(worker)&&!worker.Started,"training unexpectedly required native eye readiness");
                foreach(int missing in new[]{0,1,2,3})
                    Require(!(bool)Factory(typeof(ViveStroopDevelopmentBootstrap),"RawTaskOnlyStartInputsReady",choices,
                        missing!=0,missing!=1,missing!=2,missing==3?40:41,41),"training lost a head/focus/worker/fresh-input gate");
                Require(!(bool)Factory(typeof(ViveStroopDevelopmentBootstrap),"RawTaskOnlyStartInputsReady",Choices(clock,false),true,true,true,41,41),"training accepted incomplete recording choices");
                var claimType=typeof(ViveProEyeWorker).Assembly.GetType("AvatarStroop.Runtime.ViveRecordingStartClaim",true);
                var claim=Activator.CreateInstance(claimType,Instance,null,new object[]{worker,true},null);int attempts=0;
                Require(Attempt(claim,()=>true,inputs,()=>attempts++)=="Attempted"&&attempts==1,"dormant carrier could not claim task-only recording");
                Require(Attempt(claim,()=>true,inputs,()=>attempts++)=="Invalid"&&attempts==1,"task-only claim replayed");
                Require(!(bool)Factory(typeof(ViveStroopDevelopmentBootstrap),"FormalEyeStartRequired",false,true,0),"Preview incorrectly requires eyes");
                Require((bool)Factory(typeof(ViveStroopDevelopmentBootstrap),"FormalEyeStartRequired",true,true,0),"first recorded formal block did not require eyes");
                Require(!(bool)Factory(typeof(ViveStroopDevelopmentBootstrap),"FormalEyeStartRequired",true,false,0),"historical protocol changed");
                var rig=rigObject.AddComponent<ViveStroopPreviewRig>();var owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"synthetic-task-only-geometry");
                var choice=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,0,null);
                var config=SessionConfiguration.WithFormalEyeRecordingOnly(SessionConfiguration.WithTrainingProtocol(
                    SessionConfiguration.NamedNoAvatarDevelopment("task-only-ui-preview",51,choice),TrainingProtocol.TutorialExamV1));
                var preview=new ViveDevelopmentParticipant(config,clock,geometry,ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold,true);
                Set(owner,"participant",preview);Set(owner,"configured",true);Set(owner,"sharedClock",clock);Set(owner,"rig",rig);
                Set(rig,"development",owner);Set(rig,"worker",worker);Set(rig,"clock",clock);Set(rig,"running",true);Set(rig,"headReady",true);Set(rig,"taskOnlyEyeSource",true);
                var pumpType=typeof(ViveStroopPreviewRig).Assembly.GetType("AvatarStroop.Unity.ViveStroopEyePump",true);
                Set(rig,"eyePump",Activator.CreateInstance(pumpType,Instance,null,new object[]{worker},null));
                Require((bool)Property(rig,"TaskOnlyRecordingWorkerReady")&&!rig.RecordingWorkerReady,"native and task-only readiness were conflated");
                Require((bool)Property(owner,"FormalEyeReadyForFirstBlock")&&!(bool)Property(owner,"CanWarmupFormalEye"),"Preview requires/warmed native eye tracking");
                Call(rig,"ServiceFormalEyeWarmup");
                Require(!(bool)Property(rig,"FormalEyeWarmupRequested")&&Volatile.Read(ref factories)==0,"training or Preview opened the SDK");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"task-only-ui-plan",choice);
                var rawConfig=SessionConfiguration.WithFormalEyeRecordingOnly(SessionConfiguration.WithTrainingProtocol(
                    SessionConfiguration.NamedRecordingDevelopment("task-only-ui-record",51,plan,ViveNamedRecordingIntent.Select(Actor.Owner,"task-only-ui-intent",true,true)),TrainingProtocol.TutorialExamV1));
                if(participantMode)
                {
                    ParticipantAssignment assignment;
                    Require(ParticipantAssignment.TryCreate("P005",TaskKind.Stroop,ResponseRule.InkColor,out assignment),"participant ID fixture");
                    var sceneConfig=typeof(ViveStroopDevelopmentBootstrap).Assembly.GetType("AvatarStroop.Unity.ViveLegacySceneConfiguration",true);
                    rawConfig=SessionConfiguration.WithFormalEyeRecordingOnly(SessionConfiguration.WithTrainingProtocol(
                        (SessionConfiguration)Factory(sceneConfig,"CreateParticipantRecording","participant-ui-fixture",assignment,geometry.Version,
                        ViveFixedTrackpadMapping.Version,"test-design","test-intent",false),TrainingProtocol.TutorialExamV1));
                }
                var recorded=new ViveDevelopmentParticipant(rawConfig,clock,geometry,ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold,true);
                var runtime=typeof(ViveDevelopmentParticipant).Assembly;
                var admissionType=runtime.GetType("AvatarStroop.Runtime.ViveNamedSessionFrameAdmission",true);
                var admission=participantMode?Factory(admissionType,"CreateLegacySyntheticForTest",recorded,worker,clock,64,0d,0d):
                    Factory(admissionType,"CreateSyntheticForTest",recorded,worker,clock,64);
                Require((bool)Call(admission,"ConfirmTask")&&(bool)Call(admission,"ConfirmEye"),"task-only pump choices failed");
                var budgetType=runtime.GetType("AvatarStroop.Runtime.ViveRawByteBudget",true);
                Require((bool)Call(admission,"TryBindQueueBudget",Factory(budgetType,"SelectExplicit",33554432L)),"task-only queue budget bind failed");
                var rawPumpType=runtime.GetType("AvatarStroop.Runtime.ViveNamedRawRecordingPump",true);
                var writer=Activator.CreateInstance(rawPumpType.GetNestedType("SyntheticWriter",BindingFlags.NonPublic),Instance,null,new[]{admission},null);
                rawPump=participantMode?Factory(rawPumpType,"StartLegacySyntheticWithStatusForTest",recorded,worker,clock,admission,writer):
                    Factory(rawPumpType,"StartSyntheticWithStatusForTest",recorded,worker,clock,admission,writer,false);
                Require(Call(rawPump,"PrepareStatus").ToString()=="Ready","task-only status preparation was unavailable");
                object[] headFrame={null};
                Require((bool)Call(worker,"TryPollTaskForRecording",headFrame),"task-only head poll failed");
                var stage=rawPumpType.GetMethod("StageRigFrame",Instance,null,new[]{typeof(ViveProEyePoll),typeof(double)},null);
                Require((bool)stage.Invoke(rawPump,new[]{headFrame[0],(object)1.0})&&Get(rawPump,"stagedEpoch")!=null,
                    "fixture did not stage a real task-only epoch before the formal boundary");
                var experiment=Get(recorded,"experiment");
                experiment.GetType().GetProperty("BlockIndex",Instance).SetValue(experiment,0,null);
                experiment.GetType().GetProperty("Phase",Instance).SetValue(experiment,Phase.BlockReady,null);
                Set(owner,"participant",recorded);
                Require(!(bool)Property(owner,"FormalEyeReadyForFirstBlock"),"recorded formal block accepted a dormant eye source");
                string formalNotice=(string)Property(owner,"FormalEyeFirstBlockNotice");
                Require(formalNotice.Contains("task input service")&&formalNotice.Contains("optional"),
                    "formal service preparation notice must distinguish optional eye data from required task input");
                Set(owner,"namedRawPump",rawPump);
                if(participantMode)
                {
                    experiment.GetType().GetProperty("LockedControl",Instance).SetValue(experiment,ControlSelection.Automatic,null);
                    Require(Get(owner,"firstBlockRequest")==null&&!(bool)Call(owner,"ObserveFirstBlockRequest"),"formal plan must wait for a click");
                    Call(owner,"RequestParticipantFirstBlock");
                    var request=Get(owner,"firstBlockRequest");
                    Require(request!=null&&(bool)Call(owner,"ObserveFirstBlockRequest"),"click during eye warm-up was lost");
                    Call(owner,"RequestNamedBlockPlanStart",false);Call(owner,"RequestParticipantFirstBlock");
                    Require((int)Get(owner,"rawPlanStartRequested")==0&&object.ReferenceEquals(request,Get(owner,"firstBlockRequest")),
                        "dormant eye started the plan or duplicate click reset the request");
                    Set(request,"RequestedAt",-1d);
                    Require(!(bool)Call(owner,"ObserveFirstBlockRequest")&&Get(owner,"firstBlockRequest")==null,"expired intent survived");
                    Call(owner,"RequestParticipantFirstBlock");Set(owner,"sharedClock",new MonotonicClock("replacement-clock"));
                    Require(!(bool)Call(owner,"ObserveFirstBlockRequest")&&Get(owner,"firstBlockRequest")==null,"intent survived replaced session clock");
                    Set(owner,"sharedClock",clock);Call(owner,"RequestParticipantFirstBlock");Call(owner,"CancelFirstBlockRequest","operator cancelled");
                    Require(!(bool)Call(owner,"ObserveFirstBlockRequest")&&Get(owner,"firstBlockRequest")==null,"cancelled intent survived");
                }
                // Fixture-only setup creates the declared formal boundary. It
                // is not a native block-plan receipt or a hardware readiness
                // claim. The actual pump/rig hold must still refuse to expose
                // or spend the five-second countdown without eye activation.
                Call(experiment,"BeginCountdownCore",Actor.Owner,true);
                Require(recorded.FormalEyeRecordingWindowStart.HasValue&&(bool)Property(recorded,"RawCountdownAwaitingSoftwareWitness"),
                    "fixture did not create a pending formal countdown witness");
                Require((bool)Property(rawPump,"FormalEyeActivationPending")&&(bool)Property(owner,"WaitingForFormalEyeActivation"),
                    "real dormant pump is not recognized as the exact activation wait");
                var camera=rigObject.AddComponent<Camera>();Set(rig,"previewCamera",camera);camera.cullingMask=~0;
                Set(owner,"pendingRawPanelFrame",Time.frameCount);Set(owner,"pendingRawPanelRevision",1L);
                Require((bool)Call(owner,"PrepareRawNonTrialScene")&&camera.cullingMask==0&&
                    (int)Get(owner,"pendingRawPanelFrame")==-1&&Get(rawPump,"stagedEpoch")==null&&!owner.RecordingBlocked,
                    "same-frame formal panel revealed a countdown or retained its old training epoch");
                camera.cullingMask=~0;Call(rig,"GuardRawTaskBeforeCull",camera);
                Require(camera.cullingMask==0&&!owner.RecordingBlocked,"pre-cull did not retain the intentional formal activation hold");
                Require(!(bool)Call(owner,"CompleteRawPanelRender",new object[]{null})&&!owner.RecordingBlocked&&
                    (bool)Property(recorded,"RawCountdownAwaitingSoftwareWitness"),
                    "a pending activation callback witnessed the countdown or faulted an intentional hold");
                using(var held=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {
                    var gate=Property(worker,"RecordingTicketGate");
                    var holder=new Thread(()=>{lock(gate){held.Set();release.WaitOne(3000);}}){IsBackground=true};holder.Start();
                    try
                    {
                        Wait(held);
                        Require(Call(rawPump,"PrepareStatus").ToString()=="WaitingBaseline",
                            "contended formal activation did not request a nonterminal wait");
                    }
                    finally{release.Set();Require(holder.Join(3000),"formal activation contention did not release");}
                }
                Set(rig,"rawBusyFrames",120);Set(rig,"rawBusySince",Time.realtimeSinceStartupAsDouble-3);
                int eventCount=recorded.ReadEventsAfter(-1,4096).Count;
                for(int retry=0;retry<3;retry++)Require((bool)Call(rig,"HoldRawBusy"),"formal readiness wait was mistaken for active task failure");
                Require(!owner.RecordingBlocked&&recorded.Phase==Phase.Countdown&&
                    (bool)Property(recorded,"RawCountdownAwaitingSoftwareWitness")&&recorded.ReadEventsAfter(-1,4096).Count==eventCount,
                    "waiting advanced/faulted the formal countdown or emitted task events");
                Require((int)Get(rig,"rawBusyFrames")==120,"formal activation consumed the unrelated initial startup contention budget");
                experiment.GetType().GetProperty("Phase",Instance).SetValue(experiment,Phase.MainTask,null);
                Require(!(bool)Property(owner,"WaitingForFormalEyeActivation"),"pending native eyes allow ordinary active-task waits");
                Require(Volatile.Read(ref factories)==0,"input/eligibility checks opened the SDK");
                Debug.Log("VIVE_TASK_ONLY_TRAINING_UI_FIXTURE_PASSED: "+assertions+" assertions; no SDK initialization, eye readiness bypass only for training, preserved head/controller/choice gates, Preview independence and formal block rejection without eyes.");
                Set(rig,"worker",null);Set(rig,"eyePump",null);Set(rig,"running",false);
            }
            finally
            {
                if(rawPump!=null)Call(rawPump,"Stop");
                if(worker!=null){Call(worker,"TryRequestStop");worker.Stop(1000);worker.Dispose();}
                UnityEngine.Object.DestroyImmediate(ownerObject);UnityEngine.Object.DestroyImmediate(rigObject);EditorSceneManager.CloseScene(scene,true);
            }
        }
        public static void Validate()
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            GameObject rigObject=null,ownerObject=null;ViveProEyeWorker worker=null;Client client=null;
            try
            {
                rigObject=new GameObject("SyntheticAsynchronousStartRig");SceneManager.MoveGameObjectToScene(rigObject,scene);
                ownerObject=new GameObject("SyntheticAsynchronousStartOwner");SceneManager.MoveGameObjectToScene(ownerObject,scene);
                var rig=rigObject.AddComponent<ViveStroopPreviewRig>();var owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();
                Set(owner,"rig",rig);Set(rig,"recordingEyeFrame",Time.frameCount-1);
                Require(!(bool)Property(rig,"CurrentRecordingEyeFrame"),"fixture accidentally accepted an eye frame in the current Unity update");
                var clock=new MonotonicClock("synthetic-asynchronous-start-clock");client=new Client();
                worker=new ViveProEyeWorker(clock,()=>null,()=>client);
                Require(worker.Start(),"worker start failed");Wait(client.Waiting);
                int frame=1;MakeReady(worker,client,clock,ref frame);var choices=Choices(clock);
                Require(Inputs(choices,worker),"bounded asynchronous eye readiness was incorrectly coupled to this Unity eye frame");
                Require(!Inputs(choices,worker,focus:false)&&!Inputs(choices,worker,head:false)&&!Inputs(choices,worker,workerReady:false),
                    "missing current XR/head/worker evidence was accepted");
                Require(!Inputs(choices,worker,neutral:-1)&&!Inputs(choices,worker,neutral:40),"held or old neutral controls supplied current readiness");
                Require(!Inputs(Choices(clock,false),worker),"one choice supplied both separate choices");
                Call(choices,"SuspendInputForTracking");
                Require(!Inputs(choices,worker),"tracking suspension retained start eligibility");
                Call(choices,"Observe",true,true,true,false,false,false,clock);
                Require(!Inputs(choices,worker),"held Trigger cleared tracking suspension");
                Call(choices,"Observe",true,true,false,false,false,false,clock);
                client.Send(Poll(clock.Now,frame++,true));
                Require(Inputs(choices,worker),"same-controller neutral recovery did not restore bounded readiness");
                int attempts=0;Action install=()=>attempts++;var claim=Claim(worker);
                Require(Attempt(claim,()=>true,()=>Inputs(choices,worker,neutral:-1),install)=="Waiting"&&attempts==0,
                    "held controls attempted installation");
                Require(Attempt(claim,()=>true,()=>Inputs(choices,worker),install)=="Attempted"&&attempts==1,
                    "valid asynchronous eye/current head and neutral inputs could not claim one start");
                Require(Attempt(claim,()=>true,()=>Inputs(choices,worker),install)=="Invalid"&&attempts==1,"start claim replayed");
                var waiting=Claim(worker);client.Send(Poll(clock.Now,frame++,false));
                Require(!Inputs(choices,worker)&&Attempt(waiting,()=>true,()=>Inputs(choices,worker),install)=="Waiting"&&attempts==1,
                    "genuinely invalid eye status attempted installation");
                MakeReady(worker,client,clock,ref frame);Thread.Sleep(300);
                Require(!Inputs(choices,worker)&&Attempt(waiting,()=>true,()=>Inputs(choices,worker),install)=="Waiting"&&attempts==1,
                    "stale last eye sample attempted installation");
                MakeReady(worker,client,clock,ref frame);
                using(var held=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {
                    var gate=Property(worker,"RecordingTicketGate");
                    var holder=new Thread(()=>{lock(gate){held.Set();release.WaitOne(3000);}}){IsBackground=true};holder.Start();
                    try{Wait(held);Require(Readiness(worker).GetType().GetField("Reason",Instance).GetValue(Readiness(worker)).ToString()=="LockBusy"&&
                        Attempt(waiting,()=>true,()=>Inputs(choices,worker),install)=="Waiting"&&attempts==1,"worker contention attempted installation");}
                    finally{release.Set();Require(holder.Join(3000),"worker lock fixture did not release");}
                }
                int revision=(int)Property(choices,"Revision");var changed=Claim(worker);
                Call(choices,"Observe",true,true,false,false,false,true,clock);
                Require(Attempt(changed,()=>(int)Property(choices,"Revision")==revision,()=>true,install)=="Invalid"&&attempts==1,
                    "changed choices retained a start request");
                var stopped=Claim(worker);Require((bool)Call(worker,"TryRequestStop"),"worker stop request failed");
                Require(Attempt(stopped,()=>true,()=>true,install)=="Invalid"&&attempts==1,"changed worker generation retained a start claim");
                Require(Get(owner,"namedRawPump")==null&&Get(owner,"namedRawDestination")==null&&Get(rig,"worker")==null,
                    "input/claim fixture installed recording or a physical source");
                Debug.Log("VIVE_RECORDING_START_READINESS_FIXTURE_PASSED: "+assertions+" assertions; bounded asynchronous eye readiness, current head/XR/neutral controls, stale/invalid/lock/choice/run rejection and one-use claim. No SDK, XR, output or hardware acceptance.");
            }
            finally
            {
                if(worker!=null){Call(worker,"TryRequestStop");if(client!=null)client.Queue(null);worker.Stop(1000);worker.Dispose();}
                if(client!=null){client.Waiting.Dispose();client.Permit.Dispose();}
                if(ownerObject!=null)UnityEngine.Object.DestroyImmediate(ownerObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
                if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
