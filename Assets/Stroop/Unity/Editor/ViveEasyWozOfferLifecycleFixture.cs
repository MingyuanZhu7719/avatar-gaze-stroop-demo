#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Net;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Exercise the real source-selection transition and the same session guard
    // used by every pairing poll. The owned transport is never started: no
    // sockets, Photon connection, headset, eye worker or recording is created.
    public static class ViveEasyWozOfferLifecycleFixture
    {
        const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        static int assertions;
        static object Get(object target,string name){return target.GetType().GetField(name,I).GetValue(target);}
        static void Set(object target,string name,object value){target.GetType().GetField(name,I).SetValue(target,value);}
        static object Call(object target,string name,params object[] args){return target.GetType().GetMethod(name,I).Invoke(target,args);}
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException("Easy WoZ offer lifecycle: "+message);assertions++;}
        static void Neutral(ViveDevelopmentParticipant task){task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));}
        static ViveDevelopmentParticipant PreparedTask(MonotonicClock clock)
        {
            var allocation=new Allocation("synthetic-offer-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var config=new SessionConfiguration("synthetic-offer-session",43,allocation,RunKind.Simulation,"en",5,30,true,0,true,true,true);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"synthetic-offer-geometry");
            var task=new ViveDevelopmentParticipant(config,clock,geometry,ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold,true);
            Neutral(task);task.OwnerBeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {
                Neutral(task);var trial=task.CurrentTrial;task.MarkPresented(trial.TrialId);
                var colour=trial.Stimulus.Ink;
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,
                    colour==Colour.Green?1:colour==Colour.Yellow?-1:0,colour==Colour.Red?1:colour==Colour.Blue?-1:0));
                Neutral(task);
            }
            Require(task.Phase==Phase.BlockPreparation,"real practice did not reach source choice");return task;
        }
        static void ChooseAvatar(ViveStroopDevelopmentBootstrap owner,ViveDevelopmentParticipant task,bool share)
        {
            object choice=Get(owner,"avatarChoice");Call(choice,"Reset");Call(choice,"Pending",task);
            Call(choice,"Observe",1,true,true,false,false,false);
            Call(choice,"Observe",2,true,true,false,false,false);
            Call(choice,"Observe",3,true,true,share,!share,false);
            Call(choice,"Observe",4,true,true,false,false,false);
            Require((bool)Call(choice,"Answered",task),"synthetic release sequence did not commit choice");
        }
        static bool Current(ViveStroopDevelopmentBootstrap owner){return (bool)Call(owner,"EasyWozSessionCurrent");}
        public static void Validate()
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            GameObject gameObject=null;ViveLegacyWozParticipantProcess process=null;
            try
            {
                gameObject=new GameObject("SyntheticEasyWozOwner");SceneManager.MoveGameObjectToScene(gameObject,scene);
                var owner=gameObject.AddComponent<ViveStroopDevelopmentBootstrap>();
                var clock=new MonotonicClock("synthetic-offer-participant-clock");var task=PreparedTask(clock);
                Set(owner,"participant",task);Set(owner,"sharedClock",clock);Set(owner,"configured",true);Set(owner,"ownerPreviewWoz",true);
                Set(owner,"legacyWozScope",true);Set(owner,"legacyWozPlaintext",true);
                Set(owner,"easyWozParticipant",task);Set(owner,"easyWozSession",task);Set(owner,"easyWozAvatarShared",true);
                ChooseAvatar(owner,task,true);Require(Current(owner),"current pre-offer session rejected");
                var taskOwner=owner.CreateLegacyWozTaskBinding();
                using(var offer=ViveLegacyWozProcessPairing.CreateFreshParticipantOffer(taskOwner,Actor.Owner,"synthetic-local-owner","synthetic-offer-operator-clock",true))
                    process=ViveLegacyWozParticipantProcess.Create(offer,taskOwner,new IPEndPoint(IPAddress.Loopback,45171),new IPEndPoint(IPAddress.Loopback,45172),2);
                Set(owner,"legacyWozProcess",process);
                Require(task.Phase==Phase.BlockReady&&task.LockedControl==ControlSelection.LiveWoz,"real offer did not lock source and reach BlockReady");
                Require(!Current(owner),"unpublished source was allowed to wait as a published offer");
                Set(owner,"easyWozOfferPublished",true);
                for(int poll=0;poll<2;poll++)
                {
                    Require(Current(owner),"valid published offer rejected on following pairing guard poll "+poll);
                    Require(task.Phase==Phase.BlockReady&&!process.Terminal&&!taskOwner.Terminal&&process.LocalChoiceCurrent,
                        "waiting for review stopped the current source or changed the task");
                }
                Set(owner,"easyWozSession",new object());Require(!Current(owner),"replacement session inherited pairing");Set(owner,"easyWozSession",task);
                Set(owner,"easyWozParticipant",null);Require(!Current(owner),"replacement participant inherited pairing");Set(owner,"easyWozParticipant",task);
                Set(owner,"easyWozRecorded",true);Require(!Current(owner),"changed recording mode inherited pairing");Set(owner,"easyWozRecorded",false);
                ChooseAvatar(owner,task,false);Require(!Current(owner),"changed avatar choice inherited pairing");ChooseAvatar(owner,task,true);
                Set(owner,"legacyWozScope",false);Require(!Current(owner),"withdrawn scope inherited pairing");Set(owner,"legacyWozScope",true);
                Set(owner,"legacyWozProcess",null);Require(!Current(owner),"published flag alone replaced the owned process");Set(owner,"legacyWozProcess",process);
                Set(owner,"legacyWozTaskOwner",null);Require(!Current(owner),"published flag alone replaced the task owner");Set(owner,"legacyWozTaskOwner",taskOwner);
                Require(Current(owner),"restoring unchanged current references did not preserve pending connection");
                Require((bool)Call(taskOwner,"PauseAuthoritativeTask"),"fixture pause failed");
                Require(!Current(owner),"paused/revoked owner kept the pending connection");
                Require(Get(owner,"namedRawPump")==null&&Get(owner,"rig")==null&&Get(owner,"easyWozLobby")==null,
                    "fixture unexpectedly created raw recording, headset or cloud session");
                Debug.Log("VIVE_EASY_WOZ_OFFER_LIFECYCLE_PASSED: "+assertions+" assertions; real BlockPreparation to BlockReady selection survives two pairing guards; changed sessions, choices and stopped owners rejected. No network or eye acquisition.");
            }
            finally
            {
                if(gameObject!=null)UnityEngine.Object.DestroyImmediate(gameObject);
                if(process!=null&&!process.Terminal)process.Dispose();
                if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
