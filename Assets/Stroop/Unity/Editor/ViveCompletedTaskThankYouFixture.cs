#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity.Editor
{
    // Real task transitions with synthetic input. No native device or writer
    // is started: the owner's pending/completed export state is injected only
    // after driving every trial and questionnaire through the task APIs.
    public static class ViveCompletedTaskThankYouFixture
    {
        const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
        sealed class Clock:IClock {public double Value;public double Now{get{return Value;}}public string ClockId{get{return "synthetic-final-thank-you";}}}
        static void Set(object item,string name,object value){item.GetType().GetField(name,Fields).SetValue(item,value);}
        static bool Ready(ViveStroopDevelopmentBootstrap owner)
        {return (bool)owner.GetType().GetProperty("CompletedTaskThankYouReady",Fields).GetValue(owner,null);}
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException("Completed task thank-you: "+message);}
        static void Neutral(ViveDevelopmentParticipant task){task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));}
        static void Answer(ViveDevelopmentParticipant task,Clock clock)
        {
            Neutral(task);var trial=task.CurrentTrial;task.MarkPresented(trial.TrialId);clock.Value+=.4;
            var colour=trial.ExpectedResponse;
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,colour==Colour.Green?1:colour==Colour.Yellow?-1:0,
                colour==Colour.Red?1:colour==Colour.Blue?-1:0));Neutral(task);
        }
        static void Trigger(ViveDevelopmentParticipant task)
        {task.UpdateAuxiliary(false,false,false,0,true);task.UpdateAuxiliary(true,false,false,0,true);}
        public static void Validate()
        {
            GameObject root=null;
            try
            {
                root=new GameObject("SyntheticCompletedTaskThankYou");root.hideFlags=HideFlags.HideAndDontSave;
                var owner=root.AddComponent<ViveStroopDevelopmentBootstrap>();var clock=new Clock();
                var config=new SessionConfiguration("synthetic-final-thank-you",42,
                    new Allocation("fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",5,30,true,0,true,true,true);
                var task=new ViveDevelopmentParticipant(SessionConfiguration.WithResponseRule(config,ResponseRule.WordMeaning),clock,
                    DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"synthetic-final-geometry"),
                    ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold,true);
                Set(owner,"participant",task);Set(owner,"rawCompletionRequested",true);
                Require(!Ready(owner),"an unfinished task received a thank-you");
                Neutral(task);task.OwnerBeginPractice(Actor.Owner);
                while(task.Phase==Phase.Practice)Answer(task,clock);
                task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"synthetic-final-owner");task.OwnerBeginBlock(Actor.Owner);
                for(int block=0;block<4;block++)
                {
                    clock.Value+=5;task.Tick();
                    Require(task.Phase==Phase.MainTask,"block did not start after five seconds");
                    while(task.Phase==Phase.MainTask)Answer(task,clock);
                    Require(!Ready(owner),"questionnaire received the final thank-you too early");
                    Trigger(task);for(int item=0;item<5;item++)Trigger(task);
                    if(block<3){Trigger(task);task.Tick();}
                }
                Require(task.Completed&&task.TrialCount==104&&task.RatingCount==20,"full WordMeaning task did not complete");
                Require(Ready(owner),"completed task waits for a CSV writer or native cleanup");
                Set(owner,"rawSafetyLatched",true);
                Require(Ready(owner),"post-task export failure replaced the thank-you with a hold screen");
                Set(owner,"rawWithdrawalRequested",true);
                Require(!Ready(owner),"explicit withdrawal was celebrated");Set(owner,"rawWithdrawalRequested",false);
                Set(owner,"rawInstallFailed",true);
                Require(!Ready(owner),"failed recorder installation was celebrated");Set(owner,"rawInstallFailed",false);
                Set(owner,"rawCompletionRequested",false);
                Require(!Ready(owner),"thank-you preceded the completion-drain request");
                Debug.Log("VIVE_COMPLETED_TASK_THANK_YOU_FIXTURE_PASSED: completed104trials20ratings; pending_export; failed_export; withdrawal.");
            }
            finally{if(root!=null)UnityEngine.Object.DestroyImmediate(root);}
        }
    }
}
#endif
