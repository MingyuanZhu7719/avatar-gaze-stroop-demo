#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Exercises the actual pure scene configuration helper, not a duplicate
    // configuration. No installed gate, task, device, file or source is opened.
    public static class ViveLegacySceneConfigurationFixture
    {
        static int assertions;
        static SessionConfiguration Call(string method,params object[] arguments)
        {
            var type=typeof(SessionConfiguration).Assembly.GetType("AvatarStroop.Unity.ViveLegacySceneConfiguration",true);
            var member=type.GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic);
            if(member==null)throw new InvalidOperationException("Missing scene configuration helper: "+method);
            return (SessionConfiguration)member.Invoke(null,arguments);
        }
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Legacy scene configuration: "+message);assertions++;}
        static void Reject(Action action,string message)
        {
            try{action();}
            catch(TargetInvocationException error)
            {if(error.InnerException is ArgumentException||error.InnerException is InvalidOperationException){assertions++;return;}throw;}
            throw new InvalidOperationException("Legacy scene configuration accepted "+message);
        }
        public static void ValidateFirstBlockModes()
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var root=new GameObject("SyntheticAssignedFirstBlockOwner");root.SetActive(false);
            SceneManager.MoveGameObjectToScene(root,scene);
            const BindingFlags instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
            var owner=root.AddComponent<ViveStroopDevelopmentBootstrap>();
            var participantField=typeof(ViveStroopDevelopmentBootstrap).GetField("participant",instance);
            try
            {
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"synthetic-first-block-mode");
                for(int task=0;task<3;task++)foreach(bool woz in new[]{false,true})
                {
                    var kind=task==2?TaskKind.VisualSearch:TaskKind.Stroop;
                    var rule=task==1?ResponseRule.WordMeaning:ResponseRule.InkColor;
                    ParticipantAssignment assignment;Require(ParticipantAssignment.TryCreate("P012",kind,rule,out assignment),"first-block assignment unavailable");
                    var config=SessionConfiguration.WithTaskKind(new SessionConfiguration("first-block-"+task+"-"+woz,
                        ParticipantAssignment.StimulusSeed,assignment.Allocation,RunKind.Apparatus,"en",5,30,true,0,true,true,true,assignment.ResponseRule),assignment.TaskKind);
                    var design=ViveLegacyRecordingDesign.SelectParticipant(Actor.Owner,"first-block-design",assignment,
                        geometry.Version,ViveFixedTrackpadMapping.Version,woz);
                    config=SessionConfiguration.ParticipantRecording(config,design,ViveLegacyRecordingIntent.Select(Actor.Owner,"first-block-intent",true,true));
                    var participant=new ViveDevelopmentParticipant(config,new MonotonicClock("first-block-mode-clock"),geometry,
                        ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold,true);
                    participantField.SetValue(owner,participant);
                    var experiment=typeof(ViveDevelopmentParticipant).GetField("experiment",instance).GetValue(participant);
                    var phase=experiment.GetType().GetProperty("Phase",instance);var block=experiment.GetType().GetProperty("BlockIndex",instance);
                    phase.SetValue(experiment,Phase.BlockReady,null);block.SetValue(experiment,0,null);
                    var first=typeof(ViveStroopDevelopmentBootstrap).GetProperty("ParticipantFirstBlockReady",instance);
                    Require((bool)first.GetValue(owner,null)==!woz,
                        "assigned WoZ must use its own Begin current block flow, Automatic its bounded first-block request: task "+task);
                    block.SetValue(experiment,1,null);Require(!(bool)first.GetValue(owner,null),"later block entered first-block request path");
                    block.SetValue(experiment,0,null);phase.SetValue(experiment,Phase.BlockPreparation,null);
                    Require(!(bool)first.GetValue(owner,null),"unconfirmed block entered first-block request path");
                }
                Debug.Log("VIVE_PARTICIPANT_FIRST_BLOCK_MODES_PASSED: "+assertions+" assertions; all three tasks use separate Automatic/WoZ first-block paths. No source, writer, socket or task start.");
            }
            finally
            {
                participantField.SetValue(owner,null);UnityEngine.Object.DestroyImmediate(root);
                Require(EditorSceneManager.CloseScene(scene,true),"first-block fixture scene did not close");
            }
        }
        public static void Validate()
        {
            assertions=0;
            var preview=Call("CreatePreview","synthetic-legacy-scene");
            Require(preview.SessionId=="synthetic-legacy-scene"&&preview.Seed==20261028,"original session identity and seed");
            Require(preview.RunKind==RunKind.Simulation&&preview.QuestionnaireLanguage=="en","original simulation and language");
            Require(preview.Allocation.AllocationId=="development-fixture-BCAD","original allocation identity");
            var order=preview.Allocation.Order;var lists=preview.Allocation.ListByBlock;
            var expected=new[]{Condition.B,Condition.C,Condition.A,Condition.D};
            for(int i=0;i<4;i++){Require(order[i]==expected[i],"BCAD at block "+i);Require(lists[i]==i,"stimulus list at block "+i);}
            Require(preview.Allocation.BInitialTarget==GazeTarget.Participant,"B initially faces Participant");
            Require(preview.CountdownSeconds==5&&preview.MinimumRestSeconds==30&&preview.SkipFinalRest,"five-second countdown and rest timing");
            Require(preview.QuestionnaireInstructionSeconds==0&&preview.QuestionnaireInstructionRequiresAcknowledgement,"original questionnaire timing and acknowledgement");
            Require(preview.ParticipantPacedRest&&preview.RetainControlAcrossBlocks,"participant-paced rest and retained source");
            Require(!preview.IsNamedDesign&&preview.NamedPlan==null&&preview.NamedOption==null&&preview.NamedRecordingIntent==null,"preview is not a named alias");
            Require(preview.LegacyRecordingDesign==null&&preview.LegacyRecordingIntent==null,"preview has no recording intent");
            var recorded=Call("SelectRecording",preview,"synthetic-geometry","synthetic-mapping","synthetic-design","synthetic-intent",false);
            Require(!System.Object.ReferenceEquals(preview,recorded),"recording configuration is a copy");
            Require(!recorded.LegacyRecordingDesign.OperatorWozRequested,"original local mode remains Automatic");
            Reject(()=>Call("SelectRecording",preview,"synthetic-geometry","synthetic-mapping","synthetic-design","synthetic-intent",true),"WoZ recording gate remains closed in Editor");
            Require(System.Object.ReferenceEquals(preview.Allocation,recorded.Allocation),"same original immutable Allocation object");
            Require(System.Object.ReferenceEquals(recorded.Allocation,recorded.LegacyRecordingDesign.Allocation),"design binds that exact Allocation object");
            Require(recorded.LegacyRecordingDesign.DesignId=="synthetic-design"&&recorded.LegacyRecordingDesign.GeometryVersion=="synthetic-geometry"&&recorded.LegacyRecordingDesign.MappingVersion=="synthetic-mapping","explicit design identity and version binding");
            Require(recorded.LegacyRecordingIntent.IntentId=="synthetic-intent"&&recorded.LegacyRecordingIntent.TaskRequested&&recorded.LegacyRecordingIntent.EyeRequested,"explicit separate legacy task and eye intent");
            Require(!recorded.IsNamedDesign&&recorded.NamedPlan==null&&recorded.NamedOption==null&&recorded.NamedRecordingIntent==null,"recording is never a named alias");
            Require(recorded.SessionId==preview.SessionId&&recorded.Seed==preview.Seed&&recorded.RunKind==preview.RunKind&&recorded.QuestionnaireLanguage==preview.QuestionnaireLanguage,"identity and mode unchanged by recording wrapper");
            Require(recorded.CountdownSeconds==preview.CountdownSeconds&&recorded.MinimumRestSeconds==preview.MinimumRestSeconds&&recorded.SkipFinalRest==preview.SkipFinalRest,"all rest/countdown settings preserved");
            Require(recorded.QuestionnaireInstructionSeconds==preview.QuestionnaireInstructionSeconds&&recorded.QuestionnaireInstructionRequiresAcknowledgement==preview.QuestionnaireInstructionRequiresAcknowledgement,"all questionnaire timing preserved");
            Require(recorded.ParticipantPacedRest==preview.ParticipantPacedRest&&recorded.RetainControlAcrossBlocks==preview.RetainControlAcrossBlocks,"retained source and paced rest preserved");
            Require(preview.LegacyRecordingDesign==null&&preview.LegacyRecordingIntent==null,"original preview remains non-recording");
            order[0]=Condition.A;lists[0]=3;
            Require(recorded.Allocation.Order[0]==Condition.B&&recorded.Allocation.ListByBlock[0]==0,"public array copies cannot mutate bound allocation");
            Reject(()=>Call("SelectRecording",preview,"synthetic-geometry","synthetic-mapping","","intent",false),"missing design identity");
            Reject(()=>Call("SelectRecording",preview,"synthetic-geometry","synthetic-mapping","design","",false),"missing intent identity");
            Reject(()=>Call("SelectRecording",recorded,"synthetic-geometry","synthetic-mapping","design-2","intent-2",false),"already-recording configuration");
            var named=SessionConfiguration.NamedNoAvatarDevelopment("synthetic-named",1,ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,0,null));
            Reject(()=>Call("SelectRecording",named,"synthetic-geometry","synthetic-mapping","design","intent",false),"named configuration alias");
            for(int task=0;task<3;task++)
            {
                var kind=task==2?TaskKind.VisualSearch:TaskKind.Stroop;
                var rule=task==0?ResponseRule.InkColor:ResponseRule.WordMeaning;
                ParticipantAssignment assignment;
                Require(ParticipantAssignment.TryCreate("p012",kind,rule,out assignment),"valid participant ID");
                var config=Call("CreateParticipantRecording","run-"+task,assignment,"geometry","mapping","design","intent",false);
                Require(config.RunKind==RunKind.Apparatus&&config.ParticipantAssignment.ParticipantId=="P012","participant identity and run kind");
                Require(config.TaskKind==kind&&config.ResponseRule==assignment.ResponseRule,"task bound before raw recording");
                Require(System.Object.ReferenceEquals(config.Allocation,assignment.Allocation)&&!config.LegacyRecordingDesign.OperatorWozRequested,
                    "exact Automatic study allocation");
                Require(config.CountdownSeconds==5&&config.MinimumRestSeconds==30&&config.SkipFinalRest&&
                    config.ParticipantPacedRest&&config.RetainControlAcrossBlocks,"participant timing remains shared across tasks");
                // An Editor fixture must not override the installed-player
                // build gate. Validate that gate through the real scene helper,
                // then exercise the real pure Core selection for the WoZ mode.
                Reject(()=>Call("CreateParticipantRecording","run-woz-"+task,assignment,"geometry","mapping","design","intent",true),
                    "participant WoZ installed recording gate remains closed in Editor for task "+task);
                var baseConfig=SessionConfiguration.WithTaskKind(new SessionConfiguration("run-woz-"+task,
                    ParticipantAssignment.StimulusSeed,assignment.Allocation,RunKind.Apparatus,"en",5,30,true,0,true,true,true,assignment.ResponseRule),assignment.TaskKind);
                var wozDesign=ViveLegacyRecordingDesign.SelectParticipant(Actor.Owner,"design-woz",assignment,"geometry","mapping",true);
                var woz=SessionConfiguration.ParticipantRecording(baseConfig,wozDesign,ViveLegacyRecordingIntent.Select(Actor.Owner,"intent-woz",true,true));
                Require(woz.LegacyRecordingDesign.OperatorWozRequested&&System.Object.ReferenceEquals(woz.ParticipantAssignment,assignment),
                    "explicit WoZ mode preserves exact assigned participant for task "+task);
                Require(woz.TaskKind==config.TaskKind&&woz.ResponseRule==config.ResponseRule&&
                    System.Object.ReferenceEquals(woz.Allocation,config.Allocation)&&woz.Seed==config.Seed,
                    "Automatic/WoZ modes must share task, order, lists and stimulus seed");
                Require(!config.LegacyRecordingDesign.OperatorWozRequested,"WoZ selection mutated the Automatic configuration");
            }
            Console.WriteLine("PASS "+assertions+" pure legacy scene configuration assertions. No task/device/recording or native execution.");
        }
    }
}
#endif
