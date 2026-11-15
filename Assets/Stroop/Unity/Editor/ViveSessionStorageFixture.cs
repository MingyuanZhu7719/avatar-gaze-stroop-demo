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
    // Tests actual async owner/folder integration behind the unchanged Editor
    // production gate. Positive automatic arming belongs to the separate
    // authorized native fixture; this never starts a worker or writes data.
    public static class ViveSessionStorageFixture
    {
        const BindingFlags Members=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        static int assertions;
        static void Require(bool condition,string reason)
        {if(!condition)throw new InvalidOperationException("Session storage fixture: "+reason);assertions++;}
        static object Call(object target,string name,params object[] args)
        {return target.GetType().GetMethod(name,Members).Invoke(target,args);}
        static object Get(object target,string name)
        {return target.GetType().GetField(name,Members).GetValue(target);}
        static void Set(object target,string name,object value)
        {target.GetType().GetField(name,Members).SetValue(target,value);}
        static object Prop(object target,string name)
        {return target.GetType().GetProperty(name,Members).GetValue(target,null);}
        static object Offer(ViveStroopDevelopmentBootstrap owner,string parent)
        {
            Require((bool)Call(owner,"OfferFreshSessionDirectory",parent),"valid parent proposal was refused");
            var offer=Get(owner,"sessionDirectoryOffer");
            Require(offer!=null&&Path.GetDirectoryName((string)Prop(offer,"DirectoryPath"))==parent&&
                (string)Get(owner,"recordingDirectory")==PathFor(offer)&&!Directory.Exists(PathFor(offer))&&
                !(bool)Prop(offer,"Attempted")&&!(bool)Prop(offer,"Created"),"offer touched storage or selected a path outside the parent");
            return offer;
        }
        static string PathFor(object offer){return (string)Prop(offer,"DirectoryPath");}
        static object Queue(ViveStroopDevelopmentBootstrap owner)
        {
            Require((bool)Call(owner,"TryQueueSessionDirectoryPreparation"),"explicit fixture folder work was not queued");
            object request=Get(owner,"sessionDirectoryPreparation");
            Require(request!=null&&(bool)Prop(owner,"SessionDirectoryPreparationPending"),"request was not retained for owner-thread service");
            return request;
        }
        static void Await(object request)
        {
            Require(SpinWait.SpinUntil(()=>{Thread.MemoryBarrier();return (int)Get(request,"Done")!=0;},5000),
                "native folder operation did not finish within the test deadline");
        }
        static void RequireUnarmed(ViveStroopDevelopmentBootstrap owner)
        {
            Require(Get(owner,"consentAutoStart")==null&&Get(owner,"recordingSetup")==null&&
                Get(owner,"rawStartIntent")==null&&Get(owner,"namedRawPump")==null&&
                !(bool)Get(owner,"rawPracticeRequested"),"folder operation improperly armed consent, writer, or Practice");
        }
        static void RequireEmpty(string path)
        {Require(Directory.Exists(path)&&!Directory.EnumerateFileSystemEntries(path).Any(),"created folder was not present and empty");}
        static GameObject Root(string name,Scene scene)
        {var root=new GameObject(name);SceneManager.MoveGameObjectToScene(root,scene);root.hideFlags=HideFlags.HideAndDontSave;return root;}
        public static void Validate()
        {
            assertions=0;
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            string workspace=Path.GetFullPath(Environment.GetEnvironmentVariable("STROOP_VIVE_FIXTURE_WORKSPACE"));
            string fixtureRoot=Path.Combine(workspace,"storage-editor-"+Guid.NewGuid().ToString("N").Substring(0,8));
            Require(!Directory.Exists(fixtureRoot)&&!File.Exists(fixtureRoot),"fixture root already exists");
            Directory.CreateDirectory(fixtureRoot);
            string parent=Directory.CreateDirectory(Path.Combine(fixtureRoot,"sessions")).FullName;
            string historical=Directory.CreateDirectory(Path.Combine(parent,"prior-run")).FullName;
            byte[] sentinel={3,19,7,0,255};
            string oldData=Path.Combine(historical,"historical-synthetic.vraw"),oldCsv=Path.Combine(historical,"historical-synthetic.csv");
            File.WriteAllBytes(oldData,sentinel);File.WriteAllText(oldCsv,"synthetic,retained");
            string custom=Directory.CreateDirectory(Path.Combine(fixtureRoot,"custom")).FullName;
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            GameObject a=null,b=null,r=null;
            object pending=null;
            try
            {
                a=Root("Session storage fixture owner A",scene);b=Root("Session storage fixture owner B",scene);
                r=Root("Session storage fixture unstarted rig",scene);
                var owner=a.AddComponent<ViveStroopDevelopmentBootstrap>();
                var other=b.AddComponent<ViveStroopDevelopmentBootstrap>();
                var rig=r.AddComponent<ViveStroopPreviewRig>();
                Set(owner,"rig",rig);Set(other,"rig",rig);Set(owner,"ownerRunMode",1);
                Set(owner,"selectedDesign",0);Set(owner,"controlledRecordingNoticeAccepted",true);
                var first=Offer(owner,parent);var sibling=Offer(other,parent);
                Require(PathFor(first)!=PathFor(sibling)&&Directory.GetDirectories(parent).Length==1,
                    "separate window instances reused a session or wrote during proposal");
                Call(owner,"ArmConsentAutoStart");
                Require(!(bool)Prop(owner,"SessionDirectoryPreparationPending")&&!(bool)Prop(first,"Attempted"),
                    "Editor closed gate unexpectedly allowed normal Arm to touch storage");
                RequireUnarmed(owner);

                // The reflection test reaches actual background directory work
                // without overriding or bypassing any recording/sensor gate.
                pending=Queue(owner);
                Require((bool)Call(owner,"TryQueueSessionDirectoryPreparation")&&object.ReferenceEquals(pending,Get(owner,"sessionDirectoryPreparation")),
                    "duplicate Prepare did not retain the single original operation");
                Require(!(bool)Call(owner,"OfferFreshSessionDirectory",parent),"offer changed while pending");
                Call(owner,"CancelSessionDirectoryPreparation","fixture canceled pending acceptance; keep any empty folder");
                Require(!(bool)Prop(owner,"SessionDirectoryPreparationPending"),"cancellation left authorization pending");
                Await(pending);
                Require((bool)Get(pending,"Success")&&(bool)Prop(first,"Created"),"canceled acceptance incorrectly aborted native operation");
                RequireEmpty(PathFor(first));
                Call(owner,"ServiceSessionDirectoryPreparation");
                RequireUnarmed(owner);
                Require((bool)Call(owner,"TryQueueSessionDirectoryPreparation")&&!(bool)Prop(owner,"SessionDirectoryPreparationPending")&&
                    ((string)Get(owner,"sessionDirectoryNotice")).Contains("cannot be resumed"),"old canceled offer became reusable after native success");

                var replaced=Offer(owner,parent);
                Require(PathFor(replaced)!=PathFor(first)&&PathFor(replaced)!=PathFor(sibling),"new explicit proposal reused a name");
                Set(owner,"recordingDirectory",custom);
                Require(!(bool)Call(owner,"TryQueueSessionDirectoryPreparation")&&!(bool)Prop(replaced,"Attempted")&&
                    !(bool)Prop(owner,"SessionDirectoryPreparationPending"),"custom existing folder wrongly created a fresh child");
                RequireEmpty(custom);RequireUnarmed(owner);

                Set(owner,"recordingDirectory",PathFor(replaced));
                string oldLimit=(string)Get(owner,"recordingBudgetMiB");
                pending=Queue(owner);
                Require((string)Get(pending,"Directory")==PathFor(replaced)&&(string)Get(pending,"RawLimit")==oldLimit&&
                    (int)Get(pending,"Design")==0,"original setup not snapshotted");
                Await(pending);Require((bool)Get(pending,"Success"),"native creation for changed-context test failed");
                Set(owner,"recordingDirectory",custom);Set(owner,"recordingBudgetMiB","128");
                Call(owner,"ServiceSessionDirectoryPreparation");
                Require(!(bool)Prop(owner,"SessionDirectoryPreparationPending")&&
                    ((string)Get(owner,"sessionDirectoryNotice")).Contains("Setup changed"),"changed selection was allowed to retain the result");
                RequireUnarmed(owner);RequireEmpty(PathFor(replaced));RequireEmpty(custom);
                Set(owner,"recordingDirectory",PathFor(replaced));Set(owner,"recordingBudgetMiB",oldLimit);
                Require((bool)Call(owner,"TryQueueSessionDirectoryPreparation")&&!(bool)Prop(owner,"SessionDirectoryPreparationPending"),
                    "restoring old context resumed canceled folder work");
                RequireUnarmed(owner);

                var collision=Offer(owner,parent);
                string collided=Directory.CreateDirectory(PathFor(collision)).FullName;
                string note=Path.Combine(collided,"unrelated.keep");File.WriteAllText(note,"do not overwrite or adopt");
                pending=Queue(owner);Await(pending);
                Require(!(bool)Get(pending,"Success")&&(bool)Prop(collision,"Attempted")&&!(bool)Prop(collision,"Created")&&
                    ((string)Get(pending,"Reason")).Contains("already uses"),"asynchronous native collision was adopted");
                Call(owner,"ServiceSessionDirectoryPreparation");
                Require((bool)Call(owner,"TryQueueSessionDirectoryPreparation")&&!(bool)Prop(owner,"SessionDirectoryPreparationPending")&&
                    File.ReadAllText(note)=="do not overwrite or adopt","failed asynchronous creation retried or altered existing collision");
                RequireUnarmed(owner);
                Require(sentinel.SequenceEqual(File.ReadAllBytes(oldData))&&File.ReadAllText(oldCsv)=="synthetic,retained",
                    "older data was changed");
                Require(Directory.GetFiles(fixtureRoot,"*",SearchOption.AllDirectories).Length==3,
                    "an unintended raw, CSV, or other data file appeared");
                Debug.Log("VIVE_SESSION_STORAGE_FIXTURE_PASSED: "+assertions+
                    " assertions; no sensors or recording; isolated synthetic directories kept at "+Path.GetFileName(fixtureRoot));
            }
            finally
            {
                if(pending!=null)SpinWait.SpinUntil(()=>(int)Get(pending,"Done")!=0,5000);
                foreach(var root in new[]{a,b,r})if(root!=null)UnityEngine.Object.DestroyImmediate(root);
                EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
