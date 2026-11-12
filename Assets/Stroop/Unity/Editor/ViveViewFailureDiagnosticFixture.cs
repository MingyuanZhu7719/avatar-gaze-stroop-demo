#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Edit-mode diagnostics only: no loader, native eye source, controller,
    // recording writer or live player is started. An intentional Error log is
    // captured to verify that exception messages cannot leak into diagnostics.
    public static class ViveViewFailureDiagnosticFixture
    {
        const string Secret="SYNTHETIC_EXCEPTION_MESSAGE_MUST_NOT_BE_LOGGED";
        const string EyeSecret="SYNTHETIC_PROVIDER_DETAIL_UI_ONLY";
        static int assertions;
        sealed class FailedEyeClient : IViveProEyeClient
        {
            public int PollCalls,DisposeCalls;
            public string Diagnostic {get{return SrAnipalViveProEyeProvider.NativeMessage("Initialization",1023)+" "+EyeSecret;}}
            public bool Start(){return false;}
            public ViveProEyePoll Poll(){Interlocked.Increment(ref PollCalls);return null;}
            public void Dispose(){Interlocked.Increment(ref DisposeCalls);}
        }
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("View failure diagnostic fixture: "+message);assertions++;}
        static FieldInfo Field(object target,string name)
        {
            var field=target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null)throw new InvalidOperationException("Diagnostic fixture field missing: "+name);
            return field;
        }
        static object Get(object target,string name){return Field(target,name).GetValue(target);}
        static void Set(object target,string name,object value){Field(target,name).SetValue(target,value);}
        static object Call(object target,string name,params object[] args)
        {
            var method=Array.Find(target.GetType().GetMethods(BindingFlags.Instance|BindingFlags.NonPublic),
                value=>value.Name==name&&value.GetParameters().Length==args.Length);
            if(method==null)throw new InvalidOperationException("Diagnostic fixture method missing: "+name);
            return method.Invoke(target,args);
        }
        static string Failure(ViveStroopPreviewRig rig)
        {return Diagnostic(rig,"LastViewFailure");}
        static string EyeFailure(ViveStroopPreviewRig rig)
        {return Diagnostic(rig,"LastEyeClientFailure");}
        static string Diagnostic(ViveStroopPreviewRig rig,string name)
        {
            var property=rig.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(property==null)throw new InvalidOperationException("Diagnostic fixture property missing: "+name);
            return (string)property.GetValue(rig,null);
        }
        static Exception SyntheticError()
        {
            try{throw new InvalidOperationException(Secret);}
            catch(InvalidOperationException error){return error;}
        }
        static void NoHardwareOrOutput(ViveStroopPreviewRig rig)
        {
            Require(Get(rig,"manager")==null&&Get(rig,"display")==null&&Get(rig,"input")==null,
                "synthetic diagnostics created an XR subsystem");
            Require(Get(rig,"worker")==null&&Get(rig,"source")==null&&Get(rig,"eyePump")==null,
                "synthetic diagnostics created an eye source");
            Require(Get(rig,"development")==null&&!rig.ViewRunning,"synthetic diagnostics started a task or view");
        }
        static void AsynchronousEyeFailure(ViveStroopPreviewRig rig,List<string> messages)
        {
            Require(EyeFailure(rig)==null,"new view retained an eye client failure");
            var client=new FailedEyeClient();
            var worker=new ViveProEyeWorker(new MonotonicClock("synthetic-eye-failure-clock"),()=>null,()=>client);
            try
            {
                Require(worker.Start(),"synthetic asynchronous eye request was rejected");
                Require(SpinWait.SpinUntil(()=>worker.HasExited,5000),"synthetic failed eye client did not finish");
                Require(!worker.Starting&&!worker.Started&&!worker.Stopping,"failed eye worker was not terminal");
                string expected=worker.Diagnostic;
                Require(expected.Contains("1023")&&expected.Contains(EyeSecret),"synthetic worker lost numeric initialization failure");
                Set(rig,"worker",worker);Set(rig,"running",true);
                // Exercise the actual owner Update branch. There is no camera,
                // display, input, pump or native provider; terminal worker
                // handling must return before any head/XR polling is attempted.
                Call(rig,"Update");
                Require(EyeFailure(rig)==expected&&rig.Status==expected,"asynchronous native failure was replaced by generic head status");
                Require(Get(rig,"worker")==null&&!((bool)Get(rig,"stopping")),"finished failed eye worker was not cleaned up");
                Require(client.PollCalls==0&&client.DisposeCalls==1,"failed initialization polled or disposed the client incorrectly");
                Require(Get(rig,"source")==null&&Get(rig,"eyePump")==null&&Get(rig,"manager")==null&&
                    Get(rig,"display")==null&&Get(rig,"input")==null&&Get(rig,"development")==null,
                    "diagnostic update created an XR, eye-data or recording source");
                Require(String.Join("\n",messages.ToArray()).Contains("VIVE_EYE_CLIENT_FAILURE_RETAINED"),"retained eye failure was not announced");
                Require(!String.Join("\n",messages.ToArray()).Contains(EyeSecret),"provider detail leaked from the owner UI into logs");

                Call(rig,"StopView");Call(rig,"FinishStop");Call(rig,"SetStatus","Generic head-only status after eye cleanup.");
                Require(EyeFailure(rig)==expected,"stop or generic status erased the native first cause");
                Call(rig,"RetainEyeClientFailure","Secondary client failure.");
                Require(EyeFailure(rig)==expected,"later eye failure replaced the native first cause");
                Call(rig,"BeginEye");
                Require(EyeFailure(rig)==expected,"rejected eye start erased the native first cause");
                NoHardwareOrOutput(rig);
                Require(!String.Join("\n",messages.ToArray()).Contains(EyeSecret),"retained provider detail leaked during later cleanup");
            }
            finally
            {
                // This is only a fake background worker, never a native client.
                // Ensure no fixture thread can outlive this isolated scene.
                worker.Stop(1000);worker.Dispose();
                Set(rig,"worker",null);Set(rig,"running",false);Set(rig,"stopping",false);
            }
        }
        public static void Validate()
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            Scene fixture=default(Scene);bool opened=false;GameObject root=null;
            var messages=new List<string>();
            Application.LogCallback observed=(condition,stack,type)=>
            {if(type==LogType.Error||type==LogType.Exception||type==LogType.Warning)messages.Add(condition+"\n"+stack);};
            string priorMarker=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable);
            string priorRuntime=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable);
            try
            {
                fixture=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                opened=fixture.IsValid()&&fixture.isLoaded;Require(opened,"isolated scene unavailable");
                root=new GameObject("SyntheticViewFailureDiagnosticRig");
                SceneManager.MoveGameObjectToScene(root,fixture);root.hideFlags=HideFlags.HideAndDontSave;
                var rig=root.AddComponent<ViveStroopPreviewRig>();
                Require(Failure(rig)==null,"new view retained a failure");NoHardwareOrOutput(rig);
                Application.logMessageReceived+=observed;
                // Fail exercises the real exception-to-safe-stop path. The
                // exception message represents data that must not be printed.
                const string first="View error: InvalidOperationException";
                Call(rig,"Fail",first,SyntheticError());
                Require(Failure(rig)==first,"safe stop discarded the original failure");
                Require(rig.Status==first,"the immediate failure status was replaced by generic stopped text");
                Require(messages.Count>0,"the failure diagnostic was not logged");
                Require(String.Join("\n",messages.ToArray()).Contains("InvalidOperationException"),"error type missing from diagnostic");
                Require(!String.Join("\n",messages.ToArray()).Contains(Secret),"exception message leaked into diagnostic");
                Require(String.Join("\n",messages.ToArray()).Contains("SyntheticError"),"development stack was not retained");
                NoHardwareOrOutput(rig);

                Call(rig,"StopView");Call(rig,"FinishStop");Call(rig,"SetStatus","Generic stopped cleanup status.");
                Require(Failure(rig)==first,"later stop or status update erased first cause");
                Call(rig,"RememberViewFailure","Secondary cleanup failure: ArgumentException",new ArgumentException(Secret));
                Require(Failure(rig)==first,"a secondary cleanup exception replaced first cause");
                Call(rig,"Fail","Later view failure: ArgumentException",new ArgumentException(Secret));
                Require(Failure(rig)==first&&rig.Status==first,"subsequent failure obscured original cause");
                Require(!String.Join("\n",messages.ToArray()).Contains(Secret),"secondary exception message leaked");
                NoHardwareOrOutput(rig);

                // A rejected Start is not a new XR attempt and must preserve
                // the original diagnosis. Neither guard can enter a coroutine.
                Set(rig,"starting",true);rig.BeginView();
                Require(Failure(rig)==first,"busy start erased failure");Set(rig,"starting",false);
                Environment.SetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable,null);
                Environment.SetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable,null);
                rig.BeginView();Require(Failure(rig)==first,"invalid launcher environment erased failure");
                NoHardwareOrOutput(rig);
                AsynchronousEyeFailure(rig,messages);
                Debug.Log("VIVE_VIEW_FAILURE_DIAGNOSTIC_SYNTHETIC_FIXTURE_PASSED: "+assertions+
                    " assertions; view/async-eye first-cause persistence, numeric native failure, guarded starts and message-free logs. No XR startup, tracking, files or headset validation.");
            }
            finally
            {
                Application.logMessageReceived-=observed;
                Environment.SetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable,priorMarker);
                Environment.SetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable,priorRuntime);
                if(root!=null)UnityEngine.Object.DestroyImmediate(root);
                if(opened&&fixture.IsValid()&&fixture.isLoaded)Require(EditorSceneManager.CloseScene(fixture,true),"isolated scene could not close");
            }
        }
    }
}
#endif
