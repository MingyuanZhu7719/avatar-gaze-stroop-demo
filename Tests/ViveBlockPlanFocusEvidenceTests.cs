using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveBlockPlanFocusEvidenceTests
    {
        sealed class Fixture
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("focus-plan-receipt-clock");
            internal readonly ViveDevelopmentParticipant Participant;
            internal readonly JournalEvent Plan;
            internal readonly ViveBlockPlanRequest Request;
            internal readonly Experiment Engine;
            internal Fixture(int design=0,bool training=false,TaskKind taskKind=TaskKind.Stroop)
            {
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"focus-plan-geometry");
                SessionConfiguration config;
                if(design==2)
                {
                    var allocation=new Allocation("focus-plan-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                    var legacy=ViveLegacyRecordingDesign.Select(Actor.Owner,"focus-plan-design",allocation,geometry.Version,ViveFixedTrackpadMapping.Version);
                    config=SessionConfiguration.LegacyRecordingDevelopment(new SessionConfiguration("focus-plan-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true),
                        legacy,ViveLegacyRecordingIntent.Select(Actor.Owner,"focus-plan-intent",true,true));
                }
                else
                {
                    var option=ViveNamedOptionProvenance.Select(Actor.Owner,design==0?ViveNamedOption.Automatic:ViveNamedOption.NoAvatar,0,design==0?geometry.Version:null);
                    var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"focus-plan",option);
                    config=SessionConfiguration.NamedRecordingDevelopment("focus-plan-session",42,plan,ViveNamedRecordingIntent.Select(Actor.Owner,"focus-plan-intent",true,true));
                }
                if(training)config=SessionConfiguration.WithFormalEyeRecordingOnly(SessionConfiguration.WithTrainingProtocol(config,TrainingProtocol.TutorialExamV1));
                if(taskKind!=TaskKind.Stroop)config=SessionConfiguration.WithTaskKind(config,taskKind);
                Participant=new ViveDevelopmentParticipant(config,Clock,geometry,ViveFixedTrackpadMapping.Create(),.5,true);
                Participant.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Engine=(Experiment)typeof(ViveDevelopmentParticipant).GetField("experiment",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Participant);
                if(training)
                {
                    Participant.OwnerBeginTutorial(Actor.Owner);
                    Participant.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                    var trial=Participant.CurrentTrial;Participant.MarkPresented(trial.TrialId);
                    if(trial.VisualSearch!=null)Participant.SelectVisualSearchItem(trial.TrialId,trial.VisualSearch.TargetId);
                    else
                    {
                        var colour=trial.ExpectedResponse;
                        double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0,y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
                        Participant.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));
                    }
                    Participant.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                    Participant.RequestPracticeExam();Check.Equal(Phase.PracticeExamReady,Participant.Phase);
                }
                Plan=design==2?Participant.PrepareLegacyRecordingBlockPlan(Actor.Owner,true):Participant.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
                Check.True(ViveBlockPlanRequest.TryCapture(Participant,Clock,Plan,out Request));
            }
            internal bool Current(){return Request.TryValidateCurrentForReceipt(Participant,Clock);}
            internal void Focus(bool desktop,bool xr=true){Participant.RecordFocusState(desktop,xr);}
            internal List<JournalEvent> Events {get{return (List<JournalEvent>)typeof(Experiment).GetField("events",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Engine);}}
            internal void ReplaceTail(Action<Dictionary<string,string>> change,string kind=null,long sequenceDelta=0,double timeDelta=0,string clock=null,string session=null)
            {
                var original=Events[Events.Count-1];var fields=original.Fields.ToDictionary(pair=>pair.Key,pair=>pair.Value);
                if(change!=null)change(fields);
                Events[Events.Count-1]=new JournalEvent(original.Sequence+sequenceDelta,original.Time+timeDelta,clock??original.ClockId,session??original.SessionId,
                    original.BlockIndex,original.Segment,original.TrialId,kind??original.Kind,fields,original.NamedOption,original.NamedPlan,
                    original.NamedBlock,original.NamedRecordingIntent,original.LegacyRecordingDesign,original.LegacyRecordingIntent);
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("pending tutorial exam plan survives XR focus gap without consuming receipt or starting examination",()=>{
                foreach(int design in new[]{0,1,2})foreach(var kind in design==2?new[]{TaskKind.Stroop,TaskKind.VisualSearch}:new[]{TaskKind.Stroop})
                {
                    var f=new Fixture(design,true,kind);var copied=f.Participant.ReadEventsAfter(f.Plan.Sequence-1,1)[0];
                    Check.Equal("10",f.Plan.Fields["item_count"]);Check.True(f.Current());
                    f.Focus(false,false);
                    Check.Equal(Phase.PracticeExamReady,f.Participant.Phase,"XR focus loss must leave the prepared exam context unchanged");
                    Check.True(f.Current(),"XR focus loss invalidated the pending exam plan");
                    Check.True(f.Participant.IsXrFocusInteractionHeld);Check.Equal(0,f.Participant.PracticeAttempt);
                    Check.Equal((TrialRecord)null,f.Participant.CurrentTrial);
                    f.Focus(true,true);Check.True(f.Current(),"XR focus restoration invalidated the pending exam plan");
                    Check.True(!f.Participant.IsXrFocusInteractionHeld);Check.True(f.Request.MatchesWriterPlannedEvent(copied));
                    Check.Equal(Phase.PracticeExamReady,f.Participant.Phase);Check.Equal(0,f.Participant.PracticeAttempt);
                    Check.Equal(2,f.Participant.ReadEventsAfter(f.Plan.Sequence,10).Count);
                    Check.True(!f.Participant.ReadEventsAfter(f.Plan.Sequence,10).Any(x=>x.Kind=="focus_pause"||x.Kind=="practice_started"));
                    f.Participant.RecordRawTrackingGapStart(RawTrackingLoss.HeadPose,true);Check.True(f.Current());
                    f.Participant.RecordRawTrackingGapEnd(RawTrackingLoss.HeadPose);Check.True(f.Current());
                    Check.Equal(Phase.PracticeExamReady,f.Participant.Phase);Check.Equal(0,f.Participant.PracticeAttempt);
                }
            });
            suite.Run("prepared Ink colour exam keeps its plan through overlapping controller and XR focus gaps",()=>{
                var f=new Fixture(2,true);
                var copied=f.Participant.ReadEventsAfter(f.Plan.Sequence-1,1)[0];
                f.Participant.RecordRawTrackingGapStart(RawTrackingLoss.ControllerPose,true);
                Check.True(f.Current(),"controller gap invalidated the pending exam plan");
                f.Focus(true,false);
                Check.True(f.Current()&&f.Participant.IsXrFocusInteractionHeld,"overlapping XR focus loss invalidated the plan");
                f.Focus(true,true);
                Check.True(f.Current()&&!f.Participant.IsXrFocusInteractionHeld&&f.Participant.RawTrackingInteractionHeld,
                    "restoring focus granted controller input or invalidated the plan");
                f.Participant.RecordRawTrackingGapEnd(RawTrackingLoss.ControllerPose);
                Check.True(f.Current()&&f.Request.MatchesWriterPlannedEvent(copied),"recovery invalidated the original writer plan");
                Check.True(f.Participant.InputReady&&!f.Participant.RawTrackingInteractionHeld);
                Check.Equal(Phase.PracticeExamReady,f.Participant.Phase);Check.Equal(0,f.Participant.PracticeAttempt);
                Check.Equal((TrialRecord)null,f.Participant.CurrentTrial);
                var events=f.Participant.ReadEventsAfter(f.Plan.Sequence,10);
                Check.True(events.Select(x=>x.Kind).SequenceEqual(new[]{"tracking_gap_begin","focus_changed","focus_changed","tracking_gap_end"}));
            });
            suite.Run("pending block receipt survives only validated desktop focus loss and restoration",()=>{
                foreach(int design in new[]{0,1,2})
                {
                    var f=new Fixture(design);var copied=f.Participant.ReadEventsAfter(f.Plan.Sequence-1,1)[0];
                    Check.True(f.Current()&&f.Request.MatchesWriterPlannedEvent(copied));
                    f.Focus(false);Check.True(f.Current(),"desktop focus loss invalidated a matching request");
                    f.Focus(true);Check.True(f.Current(),"desktop focus restore invalidated a matching request");
                    Check.True(f.Request.MatchesWriterPlannedEvent(copied));Check.Equal(f.Plan.Sequence,f.Request.PlanEventSequence);
                    Check.Equal(Phase.Preparation,f.Participant.Phase);
                    Check.Equal(2,f.Participant.ReadEventsAfter(f.Plan.Sequence,10).Count);
                }
            });
            suite.Run("preparation XR focus gap keeps a matching pending plan but never starts it",()=>{
                var f=new Fixture();f.Focus(true,false);Check.True(f.Current());
                f.Focus(true,true);Check.True(f.Current());
                Check.Equal(Phase.Preparation,f.Participant.Phase);Check.Equal((TrialRecord)null,f.Participant.CurrentTrial);
            });
            suite.Run("pending receipt permits exact tracking gaps but does not consume or start practice",()=>{
                foreach(int design in new[]{0,1,2})
                {
                    foreach(var cause in new[]{RawTrackingLoss.HeadPose,RawTrackingLoss.EyeQuality})
                    {
                    var f=new Fixture(design);
                    f.Participant.RecordRawTrackingGapStart(cause,true);
                    Check.True(f.Current());Check.Equal(Phase.Preparation,f.Participant.Phase);
                    f.Participant.RecordRawTrackingGapEnd(cause);
                    Check.True(f.Current());Check.True(f.Participant.InputReady);
                    Check.Equal((TrialRecord)null,f.Participant.CurrentTrial);
                    f.ReplaceTail(fields=>fields["reason"]="ControllerPose");
                    Check.True(!f.Current());
                    }
                }
            });
            suite.Run("focus receipt evidence does not hide unrelated no-op or phase mutations",()=>{
                var noOp=new Fixture();noOp.Focus(false);noOp.Participant.Tick();Check.True(!noOp.Current());
                var phase=new Fixture();phase.Focus(false);phase.Participant.OwnerPause(Actor.Owner);Check.True(!phase.Current());
            });
            suite.Run("focus receipt tail rejects unknown or inconsistent focus declarations",()=>{
                var bad=new Dictionary<string,string>{{"focus_schema","2"},{"desktop_focused","true"},{"xr_focused","unknown"},
                    {"actor","Participant"},{"code","other"},{"reason","focus_restored"},{"detail","inconsistent"},
                    {"source","unverified_other_source"},{"measurement_quality","VALID"},{"unexpected_metadata","value"},{"named_phase","MainTask"}};
                foreach(var pair in bad)
                {
                    var f=new Fixture();f.Focus(false);Check.True(f.Current());f.ReplaceTail(fields=>fields[pair.Key]=pair.Value);
                    Check.True(!f.Current(),"unvalidated focus declaration accepted: "+pair.Key);
                }
                var missing=new Fixture();missing.Focus(false);missing.ReplaceTail(fields=>fields.Remove("focus_schema"));Check.True(!missing.Current());
            });
            suite.Run("focus receipt tail rejects another event clock session sequence or future time",()=>{
                foreach(int changed in new[]{0,1,2,3,4})
                {
                    var f=new Fixture();f.Focus(false);
                    if(changed==0)f.ReplaceTail(null,kind:"unrelated_event");
                    else if(changed==1)f.ReplaceTail(null,sequenceDelta:1);
                    else if(changed==2)f.ReplaceTail(null,timeDelta:10000);
                    else if(changed==3)f.ReplaceTail(null,clock:"foreign-clock");
                    else f.ReplaceTail(null,session:"foreign-session");
                    Check.True(!f.Current());
                }
            });
            suite.Run("focus receipt extension cannot absorb generation segment or source changes",()=>{
                foreach(int changed in new[]{0,1,2,3,4})
                {
                    var f=new Fixture();f.Focus(false);Check.True(f.Current());
                    if(changed==0)
                    {
                        var generation=typeof(ViveDevelopmentParticipant).GetField("namedRecordingMutationGeneration",BindingFlags.Instance|BindingFlags.NonPublic);
                        generation.SetValue(f.Participant,(long)generation.GetValue(f.Participant)+2);
                    }
                    else if(changed==1)typeof(Experiment).GetField("<Segment>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(f.Engine,1);
                    else if(changed==2)typeof(Experiment).GetField("sourceConfirmationOwner",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(f.Engine,"other-source");
                    else if(changed==3)typeof(Experiment).GetField("sourceConfirmationSequence",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(f.Engine,1L);
                    else typeof(Experiment).GetField("<LockedControl>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(f.Engine,(ControlSelection?)ControlSelection.LiveWoz);
                    Check.True(!f.Current());
                }
            });
            suite.Run("focus-only receipt evidence is bounded and cannot replay the prepared plan",()=>{
                var f=new Fixture();
                for(int i=0;i<256;i++)f.Focus(i%2==0);
                Check.True(f.Current());Check.True(f.Request.MatchesWriterPlannedEvent(f.Plan));
                f.Focus(false);Check.True(!f.Current());
                Check.True(!ViveBlockPlanRequest.TryCapture(f.Participant,f.Clock,f.Plan,out var replacement)&&replacement==null);
                Check.Equal(1,f.Events.Count(item=>item.Kind=="block_plan_prepared"));
            });
        }
    }
}
