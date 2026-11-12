#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.Reflection;
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
        static int assertions;
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
        {
            var property=rig.GetType().GetProperty("LastViewFailure",BindingFlags.Instance|BindingFlags.NonPublic);
            if(property==null)throw new InvalidOperationException("Diagnostic fixture property missing: LastViewFailure");
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
                Debug.Log("VIVE_VIEW_FAILURE_DIAGNOSTIC_SYNTHETIC_FIXTURE_PASSED: "+assertions+
                    " assertions; first-cause persistence, guarded start and message-free diagnostic stack. No XR startup, tracking, files or headset validation.");
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
