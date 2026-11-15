#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Actual desktop callback and pre-recording focus-input hold only. The
    // synthetic choices do not authorize files or initialize a native source.
    public static class ViveRecordingFocusFixture
    {
        static int assertions;
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Recording focus fixture: "+message);assertions++;}
        static object Get(object target,string name)
        {
            var field=target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            Require(field!=null,"missing field "+name);return field.GetValue(target);
        }
        static void Set(object target,string name,object value)
        {
            var field=target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            Require(field!=null,"missing field "+name);field.SetValue(target,value);
        }
        static object Property(object target,string name)
        {
            var property=target.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            Require(property!=null,"missing property "+name);return property.GetValue(target,null);
        }
        static object Call(object target,string name,params object[] args)
        {
            MethodInfo found=null;
            foreach(var method in target.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
            {
                if(method.Name!=name||method.ContainsGenericParameters)continue;
                var parameters=method.GetParameters();if(parameters.Length!=args.Length)continue;
                bool matches=true;
                for(int i=0;i<args.Length;i++)if(args[i]!=null&&!parameters[i].ParameterType.IsInstanceOfType(args[i]))matches=false;
                if(!matches)continue;
                Require(found==null,"ambiguous fixture method "+name);found=method;
            }
            Require(found!=null,"missing fixture method "+name);return found.Invoke(target,args);
        }
        static bool StaticBool(string name,params object[] args)
        {
            var method=typeof(ViveStroopDevelopmentBootstrap).GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
            Require(method!=null,"missing static focus helper "+name);return (bool)method.Invoke(null,args);
        }
        static void PendingOwnerAndReceiptBoundaries(ViveStroopDevelopmentBootstrap owner)
        {
            int executions=0;Action action=()=>executions++;
            Set(owner,"rawFocusMetadataKnown",true);Set(owner,"lastRecordedDesktopFocus",true);Set(owner,"lastRecordedXrFocus",true);
            Set(owner,"pendingRawOwnerAction",action);
            Require(!(bool)Call(owner,"CancelUnexecutedOwnerActionForFocusMetadata",true,true)&&
                object.ReferenceEquals(action,Get(owner,"pendingRawOwnerAction")),"unchanged focus cancelled a current owner request");
            Require((bool)Call(owner,"CancelUnexecutedOwnerActionForFocusMetadata",false,true)&&
                Get(owner,"pendingRawOwnerAction")==null&&executions==0,"desktop focus change executed or retained a stale queued request");
            Set(owner,"pendingRawOwnerAction",action);
            Require((bool)Call(owner,"CancelUnexecutedOwnerActionForFocusMetadata",true,false)&&
                Get(owner,"pendingRawOwnerAction")==null&&executions==0,"XR focus change executed or retained a stale queued request");
            Require((bool)Get(owner,"focusOwnerActionWasCancelled"),"cancelled unexecuted request lacks its owner notice");
            Require(StaticBool("RawPlanWaitObservationAllows",30d,65d,true,5d),"timely observed Ready receipt expired solely during a long focus gap");
            Require(!StaticBool("RawPlanWaitObservationAllows",30d,65d,false,-1d),"genuine unready I/O timeout was ignored");
            Require(!StaticBool("RawPlanWaitObservationAllows",30d,65d,true,31d),"late I/O was reclassified as timely");
            Require(!StaticBool("RawPlanWaitObservationAllows",30d,4d,true,5d),"receipt observation from the future was accepted");
            Require(!StaticBool("RawPlanWaitObservationAllows",30d,double.NaN,true,5d),"invalid current clock was accepted");
            Require(StaticBool("RawPlanStartPhaseMatches",true,Phase.Preparation)&&
                StaticBool("RawPlanStartPhaseMatches",true,Phase.PracticeRetry)&&
                !StaticBool("RawPlanStartPhaseMatches",true,Phase.Familiarization),"practice plan start admitted an active task phase");
            Require(StaticBool("RawPlanStartPhaseMatches",false,Phase.BlockReady)&&
                !StaticBool("RawPlanStartPhaseMatches",false,Phase.BlockPreparation)&&
                !StaticBool("RawPlanStartPhaseMatches",false,Phase.Paused),"main plan start admitted a mismatched or paused phase");
            Call(owner,"RequirePlanConfirmationAfterXrGap");
            Require(!(bool)Get(owner,"focusPlanConfirmationRequired"),"focus gap invented an absent block-plan request");
            Set(owner,"rawPlanStartRequested",1);Call(owner,"RequirePlanConfirmationAfterXrGap");
            Require((bool)Get(owner,"focusPlanConfirmationRequired"),"retained block plan lacks explicit post-gap confirmation");
            Require(!(bool)Call(owner,"ObserveFocusPlanConfirmation",true,false,true,true,false,false),"trigger held before post-gap neutral confirmed a block");
            Require(!(bool)Call(owner,"ObserveFocusPlanConfirmation",true,false,false,false,false,false),"neutral release automatically confirmed a block");
            Require(!(bool)Call(owner,"ObserveFocusPlanConfirmation",true,false,true,false,false,false),"held trigger without a fresh edge confirmed a block");
            Require((bool)Call(owner,"ObserveFocusPlanConfirmation",true,false,true,true,false,false)&&
                !(bool)Get(owner,"focusPlanConfirmationRequired"),"fresh post-neutral trigger did not explicitly confirm the retained request");
            Require((int)Get(owner,"rawPlanStartRequested")==1&&Get(owner,"participant")==null,"focus confirmation started a task or replayed plan preparation");
            Call(owner,"RequirePlanConfirmationAfterXrGap");
            Require(!(bool)Call(owner,"ObservePreparedPlanStartConfirmation",SyntheticControllerFrame(true,true)),"held Trigger armed prepared plan");
            Require(!(bool)Call(owner,"ObservePreparedPlanStartConfirmation",SyntheticControllerFrame(true,false)),"neutral only confirmed prepared plan");
            Require(!(bool)Call(owner,"ObservePreparedPlanStartConfirmation",SyntheticControllerFrame(true,true,true))&&
                (bool)Get(owner,"focusPlanTriggerAccepted"),"fresh Trigger must be remembered without starting while held");
            Require(!(bool)Call(owner,"ObservePreparedPlanStartConfirmation",SyntheticControllerFrame(true,true)),"held Trigger must not start practice");
            Require((bool)Call(owner,"ObservePreparedPlanStartConfirmation",SyntheticControllerFrame(true,false)),"release after fresh Trigger did not confirm");
            Require(!StaticBool("CanClaimRawStart",false,true,true)&&!StaticBool("CanClaimRawStart",true,false,true)&&
                !StaticBool("CanClaimRawStart",true,true,false)&&StaticBool("CanClaimRawStart",true,true,true),"Start claim admitted absent, stale or not-ready request");
            Require(StaticBool("RawIntentTimeCurrent",1d,180d)&&!StaticBool("RawIntentTimeCurrent",1d,181d)&&
                !StaticBool("RawIntentTimeCurrent",1d,0d)&&!StaticBool("RawIntentTimeCurrent",1d,double.NaN),"Start/practice expiry is not monotonic and bounded");
            Set(owner,"rawPlanStartRequested",0);
            Require(executions==0&&Get(owner,"namedRawPump")==null&&Get(owner,"namedRawDestination")==null,
                "focus helper created output or executed a discarded request");
        }
        static object Choices(MonotonicClock clock,bool confirmEye=true)
        {
            var type=typeof(ViveStroopDevelopmentBootstrap).Assembly.GetType("AvatarStroop.Runtime.ViveNamedRecordingSetup",true);
            var prepare=type.GetMethod("TryPrepare",BindingFlags.Static|BindingFlags.NonPublic);
            object[] args={true,@"C:\SyntheticFocusFolderNotOpened","32",new Func<string,bool>(path=>true),null};
            var result=prepare.Invoke(null,args);Require(result!=null,"synthetic folder selection failed");
            Call(result,"Observe",true,true,false,false,false,false,clock);
            Call(result,"Observe",true,true,true,false,false,false,clock);
            if(!confirmEye){Require(Property(result,"Current").ToString()=="Eye","first synthetic choice missing");return result;}
            Call(result,"Observe",true,true,false,false,false,false,clock);
            Call(result,"Observe",true,true,true,false,false,false,clock);
            Require((bool)Property(result,"BothSelected"),"two separate synthetic choices missing");return result;
        }
        static ViveStroopDeviceFrame SyntheticControllerFrame(bool available,bool held,bool down=false)
        {
            object[] args={new AvatarStroop.Runtime.VivePreviewTrackpadSample(available,false,0,0),
                !available||held&&!down,!available,held,down,false,false,"synthetic tracking hold",false,false,false};
            return (ViveStroopDeviceFrame)Activator.CreateInstance(typeof(ViveStroopDeviceFrame),
                BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,args,null);
        }
        static void FocusPollingPreservesOnlyHealthyChoices(ViveStroopDevelopmentBootstrap owner,MonotonicClock clock)
        {
            var input=new ViveStroopDeviceInput(VivePreviewControllerHand.Automatic);
            try
            {
                Set(owner,"device",input);
                foreach(bool confirmed in new[]{false,true})
                {
                    var setup=Choices(clock,confirmed);Set(owner,"recordingSetup",setup);
                    string prior=Property(setup,"Current").ToString();
                    string directory=(string)Property(setup,"DirectoryPath");object budget=Property(setup,"Budget");
                    Call(owner,"PollWithFocus",true,false);
                    Require(Property(setup,"Current").ToString()==prior,"pure focus loss changed choices");
                    // Both the pre-configuration route and the actual reported
                    // configured/eye-worker Poll route must retain the setup.
                    Set(owner,"configured",confirmed);
                    Call(owner,"PollWithFocus",false,false);
                    Require(Property(setup,"Current").ToString()==prior&&(bool)Property(setup,"TrackingSuspended")&&
                        (string)Property(setup,"DirectoryPath")==directory&&object.ReferenceEquals(budget,Property(setup,"Budget")),
                        "temporary head loss discarded pre-start choices or storage");
                    Require((int)Get(owner,"recordingSelectionFreshFrame")==-1&&
                        !(bool)Call(setup,"CanStart",true,true,true,true,7,7),"tracking hold retained start eligibility");
                    Call(owner,"PollWithFocus",false,true);
                    Require(Property(setup,"Current").ToString()==prior,"repeated transient loss invalidated retained setup");
                    Set(owner,"configured",false);
                    if(!confirmed)
                    {
                        Call(setup,"Observe",true,true,true,false,false,false,clock);
                        Require(Property(setup,"Current").ToString()=="Eye","held trigger completed the remaining choice");
                        Call(setup,"Observe",true,true,false,false,false,false,clock);
                        Call(setup,"Observe",true,true,true,false,false,false,clock);
                        Require((bool)Property(setup,"BothSelected"),"remaining fresh choice could not complete");
                    }
                    var unavailable=SyntheticControllerFrame(false,false);
                    Require((bool)Call(owner,"HandlePreStartControllerFrame",unavailable,clock)&&
                        (bool)Property(setup,"BothSelected")&&(bool)Property(setup,"TrackingSuspended"),
                        "temporary controller loss discarded confirmed choices");
                    var held=SyntheticControllerFrame(true,true);
                    Require(!(bool)Call(owner,"HandlePreStartControllerFrame",held,clock)&&
                        (bool)Property(setup,"TrackingSuspended"),"held recovery input unlocked recording");
                    var neutral=SyntheticControllerFrame(true,false);
                    Require(!(bool)Call(owner,"HandlePreStartControllerFrame",neutral,clock)&&
                        !(bool)Property(setup,"TrackingSuspended")&&(bool)Property(setup,"BothSelected"),
                        "same-clock neutral recovery did not revalidate the retained setup");
                    Require((int)Get(owner,"recordingSelectionFreshFrame")==-1&&
                        !(bool)Call(setup,"CanStart",true,true,true,true,-1,7),
                        "revalidation automatically supplied a fresh start frame");
                    Require((string)Property(setup,"DirectoryPath")==directory&&object.ReferenceEquals(budget,Property(setup,"Budget")),
                        "recovery changed the selected folder or budget");
                }
                var absent=Choices(clock,false);Set(owner,"recordingSetup",absent);Set(owner,"device",null);
                Call(owner,"PollWithFocus",true,false);
                Require(Property(absent,"Current").ToString()=="Eye"&&(bool)Property(absent,"TrackingSuspended"),
                    "temporary adapter absence discarded a partial choice");
                Require(Get(owner,"namedRawPump")==null&&Get(owner,"namedRawDestination")==null&&Get(owner,"participant")==null,
                    "tracking recovery started recording or a task");
            }
            finally{Set(owner,"configured",false);Set(owner,"device",null);input.Dispose();}
        }
        public static void Validate()
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            GameObject ownerObject=null,rigObject=null;
            try
            {
                ownerObject=new GameObject("SyntheticFocusOwner");SceneManager.MoveGameObjectToScene(ownerObject,scene);
                rigObject=new GameObject("SyntheticFocusRig");SceneManager.MoveGameObjectToScene(rigObject,scene);
                var rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                var owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();
                Set(owner,"rig",rig);
                var clock=new MonotonicClock("synthetic-focus-clock");var setup=Choices(clock);
                Set(owner,"recordingSetup",setup);Set(owner,"recordingSetupDesign",0);Set(owner,"selectedDesign",0);
                string directory=(string)Property(setup,"DirectoryPath");object budget=Property(setup,"Budget");
                Set(owner,"recordingSelectionFreshFrame",Time.frameCount);
                Call(owner,"OnApplicationFocus",false);
                Require((bool)Property(setup,"BothSelected")&&(string)Property(setup,"DirectoryPath")==directory&&
                    object.ReferenceEquals(budget,Property(setup,"Budget")),"desktop focus loss revoked choices or selected storage");
                Require((int)Get(owner,"recordingSelectionFreshFrame")==-1,"desktop focus loss retained stale start readiness");
                Call(owner,"OnApplicationFocus",true);
                Require((bool)Property(setup,"BothSelected"),"desktop focus return revoked or recreated choices");
                Call(owner,"HoldUnrecordedInputForXrFocus");
                Require((bool)Property(setup,"BothSelected")&&(string)Property(setup,"DirectoryPath")==directory,
                    "XR focus input hold revoked the pre-recording choices");
                Require(Get(owner,"namedRawPump")==null&&Get(owner,"namedRawDestination")==null&&Get(owner,"participant")==null&&
                    Get(rig,"worker")==null&&Get(rig,"source")==null,"focus change started a task, native source or output");
                PendingOwnerAndReceiptBoundaries(owner);
                FocusPollingPreservesOnlyHealthyChoices(owner,clock);
                Set(owner,"recordingSetup",setup);
                var causeType=setup.GetType().GetNestedType("CancellationCause",BindingFlags.NonPublic);
                Call(owner,"CancelRecordingChoices",Enum.Parse(causeType,"ControllerIdentityChanged"));
                string first=(string)Property(setup,"CancellationMessage");
                Call(owner,"OnApplicationFocus",false);Call(owner,"OnApplicationFocus",true);
                Require(Property(setup,"Current").ToString()=="Cancelled"&&!(bool)Property(setup,"BothSelected")&&
                    Property(setup,"DirectoryPath")==null,"returning focus restored actually cancelled authorization");
                Require((string)Property(setup,"CancellationMessage")==first&&first.Contains("identity")&&!first.Contains("expired"),
                    "first genuine cancellation reason was replaced by a focus or timeout claim");
                var stopSetup=Choices(clock);Set(owner,"recordingSetup",stopSetup);
                Require(!(bool)Call(owner,"HandleExplicitDesktopWithdrawal",false)&&(bool)Property(stopSetup,"BothSelected"),
                    "absence of explicit stop revoked a recording choice");
                Require((bool)Call(owner,"HandleExplicitDesktopWithdrawal",true)&&
                    Property(stopSetup,"Current").ToString()=="Cancelled"&&!(bool)Property(stopSetup,"BothSelected"),
                    "explicit desktop stop became unreachable during a focus hold");
                Require(Get(owner,"namedRawPump")==null&&Get(rig,"worker")==null,"explicit stop fixture started a recording source");
                Debug.Log("VIVE_RECORDING_FOCUS_SYNTHETIC_FIXTURE_PASSED: "+assertions+" assertions; actual desktop callbacks and XR input hold retain choices without output; real cancellation stays revoked. No XR, SDK, file capture or hardware acceptance.");
            }
            finally
            {
                if(ownerObject!=null)UnityEngine.Object.DestroyImmediate(ownerObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
                if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
