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
