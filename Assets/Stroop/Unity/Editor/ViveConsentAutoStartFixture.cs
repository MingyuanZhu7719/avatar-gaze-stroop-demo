#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // No policy override: validates the exact selection matcher and lifecycle
    // fences. The separate native fixture exercises authorized positive I/O.
    public static class ViveConsentAutoStartFixture
    {
        const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        const BindingFlags S=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static int assertions;
        static void Require(bool value,string why){if(!value)throw new InvalidOperationException("Consent auto-start: "+why);assertions++;}
        static object Get(object target,string name){return target.GetType().GetField(name,I).GetValue(target);}
        static void Set(object target,string name,object value){target.GetType().GetField(name,I).SetValue(target,value);}
        static object Prop(object target,string name){return target.GetType().GetProperty(name,I).GetValue(target,null);}
        static object Call(object target,string name,params object[] args){return target.GetType().GetMethod(name,I).Invoke(target,args);}
        static object Choices()
        {
            var type=typeof(ViveProEyeWorker).Assembly.GetType("AvatarStroop.Runtime.ViveNamedRecordingSetup",true);
            object[] args={true,@"C:\SyntheticAutoStartFolderNotOpened","32",new Func<string,bool>(p=>true),null};
            return type.GetMethod("TryPrepare",S).Invoke(null,args);
        }
        static void Choose(object choices,MonotonicClock clock)
        {
            Call(choices,"Observe",true,true,false,false,false,false,clock);
            Call(choices,"Observe",true,true,true,false,false,false,clock);
        }
        static object Arm(ViveStroopDevelopmentBootstrap owner)
        {
            var choices=Choices();Set(owner,"recordingSetup",choices);Set(owner,"ownerRunMode",1);
            Set(owner,"selectedDesign",0);Set(owner,"recordingSetupDesign",0);
            var type=typeof(ViveStroopDevelopmentBootstrap).GetNestedType("ConsentAutoStartArm",BindingFlags.NonPublic);
            var arm=Activator.CreateInstance(type,true);var clock=new MonotonicClock("synthetic-owner-arm-clock");
            Set(arm,"Choices",choices);Set(arm,"Budget",Prop(choices,"Budget"));Set(arm,"Folder",Prop(choices,"DirectoryPath"));
            Set(arm,"DirectorySelection",Get(owner,"recordingDirectory"));Set(arm,"RawLimit",Get(owner,"recordingBudgetMiB"));Set(arm,"CsvLimit",Get(owner,"csvBudgetMiB"));
            Set(arm,"CsvTimeout",Get(owner,"csvCleanupSeconds"));Set(arm,"Design",0);Set(arm,"InitialRevision",Prop(choices,"Revision"));
            Set(arm,"PlanCount",Get(owner,"namedPlanCount"));Set(arm,"Options",((int[])Get(owner,"namedPlanOptions")).Clone());
            Set(arm,"Lists",((int[])Get(owner,"namedPlanLists")).Clone());Set(arm,"Woz",Get(owner,"legacyWozRecordingSelected"));
            Set(arm,"DeadlineClock",clock);Set(arm,"ArmedAt",clock.Now);Set(owner,"consentAutoStart",arm);
            return arm;
        }
        static bool Matches(ViveStroopDevelopmentBootstrap owner,object arm)
        {return (bool)Call(owner,"ConsentAutoStartSelectionsMatch",arm);}
        static void VerifyChoiceLayout(Scene scene)
        {
            GameObject cameraObject=null,viewObject=null,ownerObject=null;
            try
            {
                cameraObject=new GameObject("SyntheticAutoChoiceCamera");SceneManager.MoveGameObjectToScene(cameraObject,scene);
                var camera=cameraObject.AddComponent<Camera>();camera.cullingMask=-1;camera.aspect=1;
                viewObject=new GameObject("SyntheticAutoChoiceView");SceneManager.MoveGameObjectToScene(viewObject,scene);
                var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                ownerObject=new GameObject("SyntheticAutoChoiceLayoutOwner");SceneManager.MoveGameObjectToScene(ownerObject,scene);
                var owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();Set(owner,"view",view);
                var match=typeof(WorldSpaceExperimentView).GetMethod("RecordingChoiceTextMatches",S);
                var clock=new MonotonicClock("synthetic-choice-layout");
                foreach(int design in new[]{0,3})foreach(bool woz in new[]{false,true})foreach(bool eye in new[]{false,true})foreach(bool canceled in new[]{false,true})
                {
                    Set(owner,"consentAutoStartNotice",null);Arm(owner);
                    Set(owner,"selectedDesign",design);Set(owner,"recordingSetupDesign",design);Set(owner,"legacyWozRecordingSelected",woz);
                    if(eye)Choose(Get(owner,"recordingSetup"),clock);
                    if(canceled)Call(owner,"CancelConsentAutoStart","synthetic canceled or expired arm");
                    Call(owner,"RenderPreSessionChoice");Call(view,"BeforeCameraCull",camera);
                    var label=(TextMesh)Get(view,"previewStatusText");var controls=(TextMesh)Get(view,"previewInputText");
                    string expected=(string)Get(view,"recordingChoiceExpected");
                    Require((bool)match.Invoke(null,new object[]{label.text,expected}),"actual consent layout clipped a full disclosure: "+design+"/"+woz+"/"+eye+"/"+canceled);
                    Require(label.GetComponent<MeshRenderer>().enabled&&controls.GetComponent<MeshRenderer>().enabled&&
                        label.GetComponent<MeshRenderer>().bounds.min.y>controls.GetComponent<MeshRenderer>().bounds.max.y,
                        "choice text or independent controls hidden/overlapping");
                    // Exercise the real mesh fitting function in a narrower
                    // synthetic panel, not merely string length estimates.
                    Call(view,"FitHud",label,expected,new Rect(-.4f,-.5f,.8f,1),new Rect(.04f,.28f,.92f,.38f),.034f,10);
                    Require((bool)match.Invoke(null,new object[]{label.text,expected}),"narrow measured consent panel clipped disclosure");
                }
                Require(!(bool)match.Invoke(null,new object[]{"Store task ... (see owner/log)","Store task responses"}),"truncation was accepted as consent");
                view.ConfigureViveDevelopmentPreview(new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}},.5);
                view.SetViveDevelopmentStatus("Synthetic task status","Synthetic task controls");Call(view,"BeforeCameraCull",camera);
                Require(!(bool)Get(view,"viveUnconfiguredPreview")&&((TextMesh)Get(view,"phaseText")).text.IndexOf("Recording choices",StringComparison.Ordinal)<0,
                    "consent-only layout leaked into configured task presentation");
            }
            finally
            {
                if(ownerObject!=null)UnityEngine.Object.DestroyImmediate(ownerObject);
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }
        public static void Validate()
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            GameObject ownerObject=null,rigObject=null;ViveProEyeWorker first=null,second=null;
            try
            {
                ownerObject=new GameObject("SyntheticConsentAutoOwner");SceneManager.MoveGameObjectToScene(ownerObject,scene);
                rigObject=new GameObject("SyntheticConsentAutoRig");SceneManager.MoveGameObjectToScene(rigObject,scene);
                var owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();var rig=rigObject.AddComponent<ViveStroopPreviewRig>();
                var time=typeof(ViveStroopDevelopmentBootstrap).GetMethod("ConsentAutoArmTimeCurrent",S);
                Require((bool)time.Invoke(null,new object[]{1d,900d})&&!(bool)time.Invoke(null,new object[]{1d,901d})&&
                    !(bool)time.Invoke(null,new object[]{1d,0d})&&!(bool)time.Invoke(null,new object[]{1d,double.NaN}),"setup arm is not bounded to a monotonic 15-minute window");
                Set(owner,"rig",rig);Set(owner,"recordingSetup",Choices());Call(owner,"ServiceConsentAutoStart");
                Require(Get(owner,"rawStartIntent")==null&&!(bool)Get(owner,"rawPracticeRequested"),"directory/choices without owner arm created requests");
                var clock=new MonotonicClock("synthetic-consent-view-clock");var arm=Arm(owner);var choices=Get(arm,"Choices");
                Require(Matches(owner,arm),"fresh selection snapshot rejected");
                Choose(choices,clock);Require(Matches(owner,arm)&&!(bool)Prop(choices,"BothSelected"),"Task choice sequence invalid");
                for(int i=0;i<4;i++)Call(choices,"Observe",true,true,true,false,false,false,clock);
                Require(!(bool)Prop(choices,"BothSelected"),"held first consent leaked into Eye choice");
                Choose(choices,clock);Require(Matches(owner,arm)&&(bool)Prop(choices,"BothSelected"),"two independent choices rejected");
                Call(owner,"ServiceConsentAutoStart");
                Require(Get(owner,"rawStartIntent")==null&&!(bool)Get(owner,"rawPracticeRequested"),"Editor closed gate created Start or Practice authority");
                foreach(var field in new[]{"recordingDirectory","recordingBudgetMiB","csvBudgetMiB","csvCleanupSeconds"})
                {
                    var old=Get(owner,field);Set(owner,field,"999");Require(!Matches(owner,arm),"changed output limit retained arm: "+field);Set(owner,field,old);
                }
                Set(owner,"selectedDesign",3);Require(!Matches(owner,arm),"changed design retained arm");Set(owner,"selectedDesign",0);
                Set(owner,"legacyWozRecordingSelected",true);Require(!Matches(owner,arm),"changed WoZ scope retained arm");Set(owner,"legacyWozRecordingSelected",false);
                Set(owner,"recordingSetup",Choices());Require(!Matches(owner,arm),"replacement setup with same path retained arm");Set(owner,"recordingSetup",choices);
                var budget=Get(arm,"Budget");Set(arm,"Budget",null);Require(!Matches(owner,arm),"replacement budget retained arm");Set(arm,"Budget",budget);
                var folder=Get(arm,"Folder");Set(arm,"Folder",@"C:\DifferentSyntheticFolder");Require(!Matches(owner,arm),"changed destination retained arm");Set(arm,"Folder",folder);
                var options=(int[])Get(owner,"namedPlanOptions");int option=options[0];options[0]=0;Require(!Matches(owner,arm),"changed named plan retained arm");options[0]=option;
                Set(rig,"running",true);Set(rig,"clock",clock);Set(arm,"ViewClock",clock);Require(Matches(owner,arm),"same view clock rejected");
                Set(rig,"clock",new MonotonicClock("replacement-view"));Require(!Matches(owner,arm),"replacement view clock retained arm");Set(rig,"clock",clock);
                first=new ViveProEyeWorker(clock,()=>null,()=>null);second=new ViveProEyeWorker(clock,()=>null,()=>null);
                Set(rig,"worker",first);Set(arm,"Worker",first);Set(arm,"WorkerGeneration",Prop(first,"RecordingStartGeneration"));Require(Matches(owner,arm),"same worker identity rejected");
                Set(rig,"worker",second);Require(!Matches(owner,arm),"replacement worker retained arm");Set(rig,"worker",first);
                Call(first,"TryRequestStop");Require(!Matches(owner,arm),"stopped worker generation retained arm");Set(rig,"worker",null);Set(rig,"running",false);
                arm=Arm(owner);choices=Get(arm,"Choices");Choose(choices,clock);
                Call(choices,"Observe",true,true,false,false,false,true,clock);Require(!Matches(owner,arm),"choice reset revision retained arm");
                arm=Arm(owner);choices=Get(arm,"Choices");Choose(choices,clock);Choose(choices,clock);
                Call(owner,"HoldPreStartTracking","synthetic-controller-loss",null);Require((bool)Get(arm,"Interrupted"),"late controller loss did not latch recovery");
                Set(arm,"Interrupted",false);Call(owner,"HoldUnrecordedInputForXrFocus");Require((bool)Get(arm,"Interrupted"),"same-update late XR loss did not latch recovery");
                Set(arm,"Interrupted",false);Call(owner,"RequirePlanConfirmationAfterXrGap");Require((bool)Get(arm,"Interrupted")&&(int)Get(owner,"rawPlanStartRequested")==0,"gap before plan creation was forgotten");
                Set(owner,"rawPracticeFromConsent",true);Set(owner,"rawConsentInterrupted",true);
                Call(owner,"InitializeRawFocusObservations",true,true);Require((bool)Get(owner,"rawConsentInterrupted")&&(bool)Get(arm,"Interrupted"),"recording attach erased earlier interruption");
                Set(arm,"RequestsCaptured",true);Set(owner,"rawPracticeRequested",true);Call(owner,"ClearRawPracticeRequest");
                Require(Get(owner,"consentAutoStart")==null&&!(bool)Get(owner,"rawPracticeFromConsent")&&!(bool)Get(owner,"rawPracticeRequested"),"handoff/cancel leaked automatic authority to a later Practice");
                arm=Arm(owner);Call(owner,"CancelConsentAutoStart","synthetic explicit cancellation");Call(owner,"ServiceConsentAutoStart");
                Require(Get(owner,"consentAutoStart")==null&&Get(owner,"rawStartIntent")==null&&!(bool)Get(owner,"rawPracticeRequested"),"cancelled arm retried");
                Require((string)Prop(owner,"ConsentAutoStartStatus")=="synthetic explicit cancellation","passive cancellation notice missing");
                var intentType=typeof(ViveStroopDevelopmentBootstrap).GetNestedType("RawStartIntent",BindingFlags.NonPublic);
                Set(owner,"rawStartIntent",Activator.CreateInstance(intentType,true));
                Require(Prop(owner,"ConsentAutoStartStatus")==null,"stale auto cancellation overrode a later explicit manual Start");Set(owner,"rawStartIntent",null);
                Set(owner,"focusPlanConfirmationRequired",true);Require(Prop(owner,"ConsentAutoStartStatus")==null,"passive cancellation hid recovery confirmation");Set(owner,"focusPlanConfirmationRequired",false);
                arm=Arm(owner);Set(arm,"ArmedAt",double.NaN);Call(owner,"ObserveConsentAutoStartFrame",true,true);
                Require(Get(owner,"consentAutoStart")==null,"invalid/expired arm clock remained active");
                Require(Get(owner,"namedRawPump")==null&&Get(owner,"namedRawDestination")==null&&Get(owner,"participant")==null,"fixture created recording or task output");
                VerifyChoiceLayout(scene);
                Debug.Log("VIVE_CONSENT_AUTO_START_FIXTURE_PASSED: "+assertions+" assertions; explicit-arm isolation, exact choices/config/clock/worker identity, held consent, sticky recovery and terminal cancellation. No XR, SDK, writer or file output.");
            }
            finally
            {
                if(rigObject!=null)Set(rigObject.GetComponent<ViveStroopPreviewRig>(),"worker",null);
                if(first!=null)first.Dispose();if(second!=null)second.Dispose();
                if(ownerObject!=null)UnityEngine.Object.DestroyImmediate(ownerObject);
                if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
                if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
