#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Real production preflight and background directory operation, no sensor,
    // writer, participant consent, production gate override, or old data writes.
    public static class ViveSessionSetupValidationFixture
    {
        const BindingFlags Members=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        static int assertions;
        static object Get(object o,string n){return o.GetType().GetField(n,Members).GetValue(o);}
        static void Set(object o,string n,object v){o.GetType().GetField(n,Members).SetValue(o,v);}
        static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,Members).Invoke(o,a);}
        static object Prop(object o,string n){return o.GetType().GetProperty(n,Members).GetValue(o,null);}
        static void Require(bool ok,string why){if(!ok)throw new InvalidOperationException("Session setup validation fixture: "+why);assertions++;}
        static string Reason(object o){return (string)Call(o,"RecordingSetupValidationReason");}
        static void Unattempted(object owner,object offer,string path)
        {
            Require(!(bool)Prop(offer,"Attempted")&&!(bool)Prop(offer,"Created")&&!Directory.Exists(path)&&
                Get(owner,"sessionDirectoryPreparation")==null,"invalid setup requested a directory operation");
            Require(Get(owner,"recordingSetup")==null&&Get(owner,"consentAutoStart")==null&&
                Get(owner,"rawStartIntent")==null&&Get(owner,"namedRawPump")==null,"invalid setup acquired recording authority");
        }
        public static void ReproduceBaseline(){Run(true);}
        public static void Validate(){Run(false);}
        static void Run(bool baseline)
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            string workspace=Path.GetFullPath(Environment.GetEnvironmentVariable("STROOP_VIVE_FIXTURE_WORKSPACE"));
            string root=Path.Combine(workspace,"setup-validation-"+Guid.NewGuid().ToString("N").Substring(0,8));
            Require(!Directory.Exists(root),"fixture root exists");Directory.CreateDirectory(root);
            string sentinel=Path.Combine(root,"prior-session.synthetic");File.WriteAllText(sentinel,"preserve existing bytes");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var go=new GameObject("Session setup validation owner");SceneManager.MoveGameObjectToScene(go,scene);
            var rg=new GameObject("Unstarted rig");SceneManager.MoveGameObjectToScene(rg,scene);object pending=null;
            try
            {
                var owner=go.AddComponent<ViveStroopDevelopmentBootstrap>();var rig=rg.AddComponent<ViveStroopPreviewRig>();
                Set(owner,"rig",rig);Set(owner,"ownerRunMode",1);Set(owner,"selectedDesign",3);Set(owner,"namedPlanCount",0);
                Set(owner,"controlledRecordingNoticeAccepted",true);
                Require((bool)Call(owner,"OfferFreshSessionDirectory",root),"proposal refused");
                var offer=Get(owner,"sessionDirectoryOffer");string path=(string)Prop(offer,"DirectoryPath");
                Require((bool)Call(owner,"TryQueueSessionDirectoryPreparation"),"incomplete request unexpectedly fell through");
                string rejection=(string)Get(owner,"message");Unattempted(owner,offer,path);
                if(baseline)
                {
                    Require(rejection=="Folder was not created: Select an available option and stimulus list for every block.",
                        "exact DQ17 screenshot rejection was not reproduced: "+rejection);
                    Debug.Log("VIVE_SESSION_SETUP_BASELINE_REPRODUCED: "+rejection+"; no I/O, all option/list indices -1");return;
                }
                Require(rejection.StartsWith("Setup incomplete: Block 1:")&&rejection.Contains("Automatic or No Avatar")&&
                    rejection.Contains("stimulus list")&&rejection.Contains("No folder was requested")&&!rejection.Contains("Folder was not created"),
                    "configuration rejection is still mislabeled as storage failure: "+rejection);
                var options=(int[])Get(owner,"namedPlanOptions");var lists=(int[])Get(owner,"namedPlanLists");
                Require(options.All(x=>x==-1)&&lists.All(x=>x==-1),"validation silently selected a condition or stimulus list");
                Set(owner,"namedPlanCount",-1);Require(Reason(owner)=="Select the number of blocks (1-4).","missing count not actionable");
                Set(owner,"namedPlanCount",4);Require(Reason(owner)=="Select the number of blocks (1-4).","out of range count accepted");
                for(int i=0;i<4;i++)
                {
                    Set(owner,"namedPlanCount",i);
                    for(int j=0;j<i;j++){options[j]=j%2;lists[j]=j;}
                    Require(Reason(owner).StartsWith("Block "+(i+1)+":")&&Reason(owner).Contains("and a stimulus list"),"missing row not identified");
                    options[i]=0;Require(Reason(owner)=="Block "+(i+1)+": select a stimulus list (0-3).","missing list not identified");
                    options[i]=-1;lists[i]=i;Require(Reason(owner)=="Block "+(i+1)+": select Automatic or No Avatar.","missing condition not identified");
                    options[i]=2;Require(Reason(owner)!=null,"out of range option accepted");
                    options[i]=i%2;lists[i]=4;Require(Reason(owner)!=null,"out of range list accepted");
                    lists[i]=i;Require(Reason(owner)==null,"complete selected rows rejected");
                    Unattempted(owner,offer,path);
                }
                Require(((string)Prop(owner,"SessionDirectoryStatusNotice")).StartsWith("Setup selections are complete."),
                    "corrected selections left a stale failure status");
                Set(owner,"namedPlanCount",0);options[3]=-1;lists[3]=-1;
                Require(Reason(owner)==null,"unused future row was incorrectly required");
                foreach(string invalid in new[]{"","0","-1","8193","999999999999999999999999","1.0"})
                {Set(owner,"recordingBudgetMiB",invalid);Require(Reason(owner).Contains("raw maximum"),"invalid raw limit accepted");}
                Set(owner,"recordingBudgetMiB","8192");Require(Reason(owner)==null,"supported maximum rejected");
                Set(owner,"recordingBudgetMiB","256");Set(owner,"csvBudgetMiB","0");Require(Reason(owner).Contains("CSV maximum"),"invalid CSV limit accepted");
                Set(owner,"csvBudgetMiB","64");Set(owner,"csvCleanupSeconds","3601");Require(Reason(owner).Contains("cleanup timeout"),"invalid cleanup limit accepted");
                Set(owner,"csvCleanupSeconds","300");Set(owner,"observationOptIn",true);Require(Reason(owner).Contains("Disable preview observation"),"preview observation accepted");
                Set(owner,"observationOptIn",false);Set(owner,"selectedDesign",0);Set(owner,"namedPlanCount",-1);
                Require(Reason(owner)==null,"Legacy wrongly required Named rows");
                Set(owner,"selectedDesign",3);Set(owner,"namedPlanCount",3);options[3]=1;lists[3]=3;
                Require(Reason(owner)==null,"fully explicit four-block plan rejected");Unattempted(owner,offer,path);
                Require((bool)Call(owner,"TryQueueSessionDirectoryPreparation"),"corrected proposal did not queue");
                pending=Get(owner,"sessionDirectoryPreparation");Require(pending!=null,"corrected proposal lost operation");
                Require((bool)Call(owner,"TryQueueSessionDirectoryPreparation")&&object.ReferenceEquals(pending,Get(owner,"sessionDirectoryPreparation")),
                    "repeated click created another request");
                Require(SpinWait.SpinUntil(()=>{Thread.MemoryBarrier();return (int)Get(pending,"Done")!=0;},5000),"native create timeout");
                Require((bool)Get(pending,"Success")&&(bool)Prop(offer,"Created")&&Directory.Exists(path)&&
                    !Directory.EnumerateFileSystemEntries(path).Any(),"corrected proposal did not create exactly an empty folder");
                Require(Directory.GetDirectories(root).Length==1&&File.ReadAllText(sentinel)=="preserve existing bytes"&&
                    Directory.GetFiles(root,"*",SearchOption.AllDirectories).Length==1,"extra folder/data appeared or prior bytes changed");
                Call(owner,"CancelSessionDirectoryPreparation","fixture ends acceptance; leave empty folder untouched");
                Require(Get(owner,"recordingSetup")==null&&Get(owner,"consentAutoStart")==null&&Get(owner,"namedRawPump")==null,
                    "folder creation alone armed or started recording");
                Debug.Log("VIVE_SESSION_SETUP_VALIDATION_FIXTURE_PASSED: "+assertions+" assertions; actual corrected proposal created once; no recording or sensors");
            }
            finally
            {
                if(pending!=null)SpinWait.SpinUntil(()=>(int)Get(pending,"Done")!=0,5000);
                UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(rg);EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
