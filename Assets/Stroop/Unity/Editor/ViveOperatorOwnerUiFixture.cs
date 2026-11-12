#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Synthetic owner-form validation only. No headset, eye worker, socket,
    // source forwarding, session recording or participant trial is started.
    public static class ViveOperatorOwnerUiFixture
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Operator owner UI fixture: "+message);}
        static FieldInfo Field(string name)
        {return typeof(ViveWozOperatorDevelopmentBootstrap).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);}
        static object Get(object owner,string name){return Field(name).GetValue(owner);}
        static void Set(object owner,string name,object value){Field(name).SetValue(owner,value);}
        static object Call(object owner,string name,params object[] args)
        {
            var method=typeof(ViveWozOperatorDevelopmentBootstrap).GetMethod(name,BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic);
            Require(method!=null,"missing owner form method "+name);return method.Invoke(owner,args);
        }
        public static void Validate()
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            Scene fixture=default(Scene);GameObject root=null;bool opened=false;
            try
            {
                fixture=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                opened=fixture.IsValid()&&fixture.isLoaded;Require(opened,"isolated scene unavailable");
                root=new GameObject("SyntheticOperatorOwnerUi");root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root,fixture);root.hideFlags=HideFlags.HideAndDontSave;
                var owner=root.AddComponent<ViveWozOperatorDevelopmentBootstrap>();
                Require(Get(owner,"rig")==null&&Get(owner,"process")==null,"inactive fixture must not construct XR or LAN source");
                Require(((string)Call(owner,"NextOwnerAction")).Contains("Start the headset"),"initial next action must identify the first required step");
                Require(!owner.NetworkOpen&&!owner.Configured&&!owner.CanRecordOrExport&&!owner.CanEnterLiveCountdown,
                    "operator form must not gain task, recording or countdown authority");

                Set(owner,"localIp","192.168.50.2");Set(owner,"remoteIp","192.168.50.3");
                Require((bool)Call(owner,"ConnectionSettingsValid"),"valid isolated LAN form rejected");
                Set(owner,"remoteIp","not-an-address");Require(!(bool)Call(owner,"ConnectionSettingsValid"),"invalid address accepted");
                Set(owner,"remoteIp","192.168.50.3");Set(owner,"localPort","65536");
                Require(!(bool)Call(owner,"ConnectionSettingsValid"),"out of range port accepted");
                Set(owner,"localPort","45172");Set(owner,"maximumAge","NaN");
                Require(!(bool)Call(owner,"ConnectionSettingsValid"),"non-finite timeout accepted");
                Set(owner,"maximumAge","2");

                foreach(string field in new[]{"offerReviewed","participantLocalRecording","scope","networkNotice","forwarding","forwardingAndStorage"})Set(owner,field,true);
                Set(owner,"token","invalid-offer");Call(owner,"ReviewOfferMode");
                foreach(string field in new[]{"offerReviewed","participantLocalRecording","scope","networkNotice","forwarding","forwardingAndStorage"})
                    Require(!(bool)Get(owner,field),"editing an offer must clear previous consent: "+field);
                bool rejected=false;
                try{Call(owner,"OpenFromOwnerForm");}
                catch(TargetInvocationException error){rejected=error.InnerException is InvalidOperationException;}
                Require(rejected&&!owner.NetworkOpen&&Get(owner,"process")==null,"invalid offer must fail before any connection is created");
                Set(owner,"closed",true);
                Require(((string)Call(owner,"NextOwnerAction")).Contains("Session ended"),"terminal source must require a new pairing");
                Debug.Log("VIVE_OPERATOR_OWNER_UI_SYNTHETIC_FIXTURE_PASSED: owner form validation, permission reset, fail-closed connect, authority boundaries and next steps; no XR, source, LAN or recording.");
            }
            finally
            {
                if(root!=null)UnityEngine.Object.DestroyImmediate(root);
                if(opened&&fixture.IsValid()&&fixture.isLoaded)Require(EditorSceneManager.CloseScene(fixture,true),"cannot close isolated scene");
            }
        }
    }
}
#endif
