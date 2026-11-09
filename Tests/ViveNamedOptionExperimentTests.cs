using System;
using System.Collections.Generic;
using System.IO;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveNamedOptionExperimentTests
    {
        sealed class NoPollProvider:IViveProEyeProvider {public int Calls;public ViveProEyePoll Poll(){Calls++;throw new InvalidOperationException("Synthetic source must not be polled");}}
        sealed class NeverOpenFactory:IViveJournalSinkFactory
        {public int Opens;public IViveJournalSinkHandle Open(string path,FileMode mode){Opens++;throw new InvalidOperationException("Unexpected open");}}
        static AutomaticGazeGeometry Geometry()
        {return new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"synthetic-geometry");}
        static Dictionary<ViveTrackpadDirection,Colour> Mapping()
        {return new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};}
        static void Update(ViveDevelopmentParticipant p,bool pressed=false,Colour colour=Colour.Red)
        {
            double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0;
            double y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
            p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,pressed,x,y));
        }
        static ViveDevelopmentParticipant Participant(ManualClock c)
        {return new ViveDevelopmentParticipant(Config(Selection()),c,Geometry(),Mapping(),.5,true);}
        static void Start(ViveDevelopmentParticipant p,ManualClock c)
        {
            Update(p);p.OwnerBeginPractice(Actor.Owner);
            for(int i=0;i<8;i++){Update(p);p.MarkPresented(p.CurrentTrial.TrialId);var ink=p.CurrentTrial.Stimulus.Ink;c.Advance(.4);Update(p,true,ink);Update(p);}
            p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"synthetic-owner");p.OwnerBeginBlock(Actor.Owner);c.Advance(3);p.Tick();Check.Equal(Phase.MainTask,p.Phase);
        }
        static ViveNamedOptionProvenance Selection()
        {return ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,2,"synthetic-geometry");}
        static SessionConfiguration Config(ViveNamedOptionProvenance selected)
        {return SessionConfiguration.NamedAutomaticDevelopment("named-session",37,selected);}
        static void Start(Experiment e,ManualClock c)
        {
            Fixtures.Practice(e,c,8);e.LockBlock(Actor.Owner,ControlSelection.Automatic,"test-owner");
            e.BeginCountdown(Actor.Owner,true);c.Advance(3);e.Tick();Check.Equal(Phase.MainTask,e.Phase);
        }
        static JournalEvent LegacyCopy(JournalEvent source)
        {
            var fields=new Dictionary<string,string>();foreach(var pair in source.Fields)fields.Add(pair.Key,pair.Value);
            return new JournalEvent(source.Sequence,source.Time,source.ClockId,source.SessionId,source.BlockIndex,source.Segment,
                source.TrialId,source.Kind,fields);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("named recording planned stimulus is versioned across practice retry and interruption",()=>{
                var c=new ManualClock();var option=Selection();
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"stimulus-plan",option);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"stimulus-intent",true,true);
                var e=new Experiment(SessionConfiguration.NamedRecordingDevelopment("stimulus-session",37,plan,intent),c);
                e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                var interrupted=e.CurrentTrial;e.MarkPresented(interrupted.TrialId);e.Pause(Actor.Participant);e.Resume(true);
                Check.Equal(TrialOutcome.Interrupted,interrupted.Outcome);
                while(e.Phase==Phase.Practice)
                {
                    var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);c.Advance(.2);
                    var wrong=trial.Stimulus.Ink==Colour.Red?Colour.Green:Colour.Red;
                    Check.True(e.ButtonChanged(wrong,true));e.ButtonChanged(wrong,false);
                    if(e.Phase==Phase.PracticeCorrection)e.ContinuePractice();
                }
                Check.Equal(Phase.PracticeRetry,e.Phase);e.BeginPractice(Actor.Owner);
                while(e.Phase==Phase.Practice)
                {
                    var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);c.Advance(.2);
                    Check.True(e.ButtonChanged(trial.Stimulus.Ink,true));e.ButtonChanged(trial.Stimulus.Ink,false);
                }
                Check.Equal(Phase.BlockPreparation,e.Phase);e.ConfirmNamedPlan(Actor.Owner,"owner");
                e.BeginCountdown(Actor.Owner,true);c.Advance(3);e.Tick();
                var main=e.CurrentTrial;e.MarkPresented(main.TrialId);e.Pause(Actor.Participant);e.Resume(true);c.Advance(3);e.Tick();
                Check.Equal(TrialOutcome.Interrupted,main.Outcome);
                int planned=0;foreach(var ev in e.Events)if(ev.Kind=="trial_planned")
                {
                    TrialRecord match=null;foreach(var t in e.Trials)if(t.TrialId==ev.TrialId)match=t;
                    Check.True(match!=null);Check.Equal("1",ev.Fields["planned_stimulus_schema"]);
                    Check.Equal(match.Stimulus.Word.ToString(),ev.Fields["planned_word"]);
                    Check.Equal(match.Stimulus.Ink.ToString(),ev.Fields["planned_ink"]);planned++;
                }
                Check.Equal(e.Trials.Count,planned);Check.True(planned>=18);
            });
            suite.Run("pre-v3 Quest and named v1/v2 canonical event bytes remain unchanged",()=>{
                // Frozen from the separately saved pre-v3 20261106q Unity source;
                // each digest covers all fields in deterministic synthetic events.
                Check.Equal("399:badf7eda928a3696cee63d99df0b4a357c5091e53fc1a4af4b5e5ea320c1b28a",ViveHistoricalEventBytesFixture.Digest(0));
                Check.Equal("131:c0af3b99a88db83ec1250be156d1a5895a0f5be7114067550bd426ee7067e1f3",ViveHistoricalEventBytesFixture.Digest(1));
                Check.Equal("223:7ca4c7e83aeac070098b9d8e05c9eb45d8758cf1c2b2063e042aefab828aaefd",ViveHistoricalEventBytesFixture.Digest(2));
            });
            suite.Run("named recording intent is explicit pre-session v3 without claiming capture or coverage",()=>{
                var a=Selection();var n=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"intent-plan",a,n);
                Check.Throws<UnauthorizedAccessException>(()=>ViveNamedRecordingIntent.Disabled(Actor.Participant,"choice"));
                Check.Throws<UnauthorizedAccessException>(()=>ViveNamedRecordingIntent.Select(Actor.Operator,"choice",true,true));
                Check.Throws<ArgumentException>(()=>ViveNamedRecordingIntent.Select(Actor.Owner," ",true,true));
                Check.Throws<ArgumentNullException>(()=>SessionConfiguration.NamedRecordingDevelopment("session",37,plan,null));
                var off=ViveNamedRecordingIntent.Disabled(Actor.Owner,"disabled-choice");Check.True(!off.TaskRequested&&!off.EyeRequested);
                foreach(bool taskRequested in new[]{false,true})foreach(bool eyeRequested in new[]{false,true})
                {
                    var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-choice",taskRequested,eyeRequested);
                    var config=SessionConfiguration.NamedRecordingDevelopment("intent-session",37,plan,intent);var c=new ManualClock();
                    Check.True(Object.ReferenceEquals(config.NamedRecordingIntent,intent)&&config.RunKind==RunKind.Simulation);
                    var e=new Experiment(config,c);Fixtures.Practice(e,c,8);e.ConfirmNamedPlan(Actor.Owner,"owner");
                    foreach(var item in e.Events)
                    {
                        Check.True(Object.ReferenceEquals(item.NamedPlan,plan)&&Object.ReferenceEquals(item.NamedRecordingIntent,intent));
                        Check.Equal(ViveNamedRecordingIntent.EventSchemaVersion,item.Fields["named_schema"]);
                        Check.Equal(ViveNamedSimulationPlan.SchemaVersion,item.Fields["named_plan_schema"]);
                        Check.Equal("synthetic-choice",item.Fields["named_recording_intent_id"]);
                        Check.Equal("NOT_INCLUDED_IN_TASK_EVENT",item.Fields["named_eye_sample_payload"]);
                        Check.Equal(taskRequested?"REQUESTED":"OFF",item.Fields["named_task_recording_intent"]);
                        Check.Equal(eyeRequested?"REQUESTED":"OFF",item.Fields["named_eye_recording_intent"]);
                        Check.True(!item.Fields.ContainsKey("named_participant_eye")&&!item.Fields.ContainsKey("eye_recording_started")&&!item.Fields.ContainsKey("eye_coverage"));
                    }
                    Check.Throws<InvalidOperationException>(()=>new ViveWozParticipantHost(e,()=>{},true));
                    Check.Throws<InvalidOperationException>(()=>new Experiment(config,c,new ExperimentIncrementalOptions(16,128,65536)));
                    var participant=new ViveDevelopmentParticipant(config,c,Geometry(),Mapping(),.5,true);
                    var copy=participant.ReadEventsAfter(-1,32)[0];
                    Check.True(Object.ReferenceEquals(copy.NamedPlan,plan)&&Object.ReferenceEquals(copy.NamedRecordingIntent,intent));
                    Check.Equal(ViveNamedRecordingIntent.EventSchemaVersion,copy.Fields["named_schema"]);
                    var factory=new NeverOpenFactory();
                    Check.Throws<InvalidOperationException>(()=>ViveDevelopmentJournalSink.OpenV2WithIdentityFactory("unused",null,c,null,
                        ViveDevelopmentDataSource.SyntheticFixture,0,0,true,true,factory,participant));Check.Equal(0,factory.Opens);
                    string eventCsv=null,recordCsv=null;int trialCount=0,ratingCount=0;
                    var snapshot=new ViveNamedTaskRecordSnapshot(e.Trials,e.Ratings);
                    Check.Throws<InvalidOperationException>(()=>ViveNamedPlanTaskMemoryProjection.Render(plan,e.Events,snapshot,"intent-session",c.ClockId,
                        out eventCsv,out recordCsv,out trialCount,out ratingCount));
                    Check.True(eventCsv==null&&recordCsv==null);
                    var memory=new MemoryStream();var start=e.Events[0];
                    Check.Throws<InvalidOperationException>(()=>new VivePracticeJournalV3Writer(memory,c,null,start,ViveDevelopmentDataSource.SyntheticFixture,true,true,8,4096));
                    Check.Equal(0L,memory.Length);
                }
                var v2=new Experiment(SessionConfiguration.NamedPlanDevelopment("existing-v2",37,plan),new ManualClock());
                Check.True(v2.Configuration.NamedRecordingIntent==null&&v2.Events[0].NamedRecordingIntent==null);
                Check.Equal(ViveNamedSimulationPlan.SchemaVersion,v2.Events[0].Fields["named_schema"]);
                Check.Equal("NOT_RECORDED",v2.Events[0].Fields["named_participant_eye"]);
            });
            suite.Run("named simulation plan preserves explicitly selected block order and distinct identities",()=>{
                var automatic=Selection();var absent=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);
                var supplied=new[]{automatic,absent,automatic,absent};
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-plan",supplied);
                supplied[0]=absent;
                Check.Equal(4,plan.Blocks.Count);Check.Equal("synthetic-plan",plan.PlanId);
                Check.True(Object.ReferenceEquals(automatic,plan.Blocks[0].Selection));
                Check.True(Object.ReferenceEquals(absent,plan.Blocks[1].Selection));
                for(int i=0;i<4;i++){Check.Equal(i,plan.Blocks[i].Index);Check.True(Object.ReferenceEquals(plan,plan.Blocks[i].Plan));}
                Check.True(!Object.ReferenceEquals(plan.Blocks[0],plan.Blocks[2]));
                Check.Throws<UnauthorizedAccessException>(()=>ViveNamedSimulationPlan.Select(Actor.Participant,"x",automatic));
                Check.Throws<ArgumentException>(()=>ViveNamedSimulationPlan.Select(Actor.Owner," ",automatic));
                Check.Throws<ArgumentException>(()=>ViveNamedSimulationPlan.Select(Actor.Owner,"x",new ViveNamedOptionProvenance[0]));
                Check.Throws<ArgumentException>(()=>ViveNamedSimulationPlan.Select(Actor.Owner,"x",new[]{automatic,automatic,automatic,automatic,automatic}));
                Check.Throws<InvalidOperationException>(()=>ViveNamedSimulationPlan.Select(Actor.Owner,"x",automatic,null));
            });
            suite.Run("named four-block simulation confirms once and preserves typed block timing and rest",()=>{
                var a=Selection();var n=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);
                var a2=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,3,"synthetic-geometry");
                var n2=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,0,null);
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-plan",a,n,a2,n2);
                var config=SessionConfiguration.NamedPlanDevelopment("synthetic-plan-session",37,plan);var c=new ManualClock();
                Check.Throws<InvalidOperationException>(()=>{var ignored=config.NamedOption;});
                var e=new Experiment(config,c);Check.Throws<InvalidOperationException>(()=>new ViveWozParticipantHost(e,()=>{},true));
                Check.Throws<InvalidOperationException>(()=>new Experiment(config,c,new ExperimentIncrementalOptions(16,128,65536)));
                Fixtures.Practice(e,c,8);
                foreach(var t in e.Trials){Check.True(t.IsPractice&&Object.ReferenceEquals(plan,t.NamedPlan)&&t.NamedBlock==null&&t.NamedOption==null);Check.Throws<InvalidOperationException>(()=>{var ignored=t.Condition;});}
                Check.Throws<InvalidOperationException>(()=>e.LockBlock(Actor.Owner,ControlSelection.Automatic,"synthetic-owner"));
                Check.Throws<UnauthorizedAccessException>(()=>e.ConfirmNamedPlan(Actor.Participant,"synthetic-owner"));
                e.ConfirmNamedPlan(Actor.Owner,"synthetic-owner");e.BeginCountdown(Actor.Owner,true);c.Advance(3);e.Tick();
                for(int b=0;b<4;b++)
                {
                    Check.Equal(Phase.MainTask,e.Phase);Check.True(Object.ReferenceEquals(plan.Blocks[b],e.CurrentNamedPlanBlock));
                    Check.True(e.View(Actor.Participant).OperatorAvatarVisible==(b%2==0));
                    Check.True((b%2==0)==e.LockedControl.HasValue);
                    Check.Equal(TrialPlans.MainListId(37,plan.Blocks[b].Selection.ListIndex),e.CurrentTrial.ListId);
                    if(b==1)
                    {
                        e.MarkPresented(e.CurrentTrial.TrialId);c.Advance(.7);e.Pause(Actor.Participant);c.Advance(8);Check.Near(.7,e.ActiveSeconds);
                        e.Resume(true);c.Advance(3);e.Tick();Check.Equal(1,e.Segment);
                    }
                    while(e.Phase==Phase.MainTask)
                    {
                        var t=e.CurrentTrial;Check.True(Object.ReferenceEquals(plan,t.NamedPlan)&&Object.ReferenceEquals(plan.Blocks[b],t.NamedBlock)&&t.NamedOption==null);
                        e.MarkPresented(t.TrialId);c.Advance(.2);var ink=t.Stimulus.Ink;Check.True(e.ButtonChanged(ink,true));e.ButtonChanged(ink,false);
                    }
                    Check.Equal(Phase.Questionnaire,e.Phase);Check.True(e.AcknowledgeQuestionnaireInstruction());
                    for(int q=0;q<Questionnaire.Count;q++)e.Rate(3);
                    if(b<3)
                    {
                        Check.Equal(Phase.Rest,e.Phase);Check.True(e.Ready());Check.Equal(Phase.BlockReady,e.Phase);
                        Check.Near(0,e.ActiveSeconds);Check.True(!e.TryBeginRetainedBlockCountdown(false));
                        var last=e.Events[e.Events.Count-1];Check.Equal("0",last.Fields["named_active_seconds"]);
                        Check.Equal("False",last.Fields["named_block_started"]);Check.True(Object.ReferenceEquals(plan.Blocks[b+1],last.NamedBlock));
                        Check.True(e.TryBeginRetainedBlockCountdown(true));c.Advance(3);e.Tick();
                    }
                }
                Check.Equal(Phase.Ended,e.Phase);Check.Equal((EndReason?)EndReason.Completed,e.EndReason);Check.Equal(104,e.Trials.Count);Check.Equal(20,e.Ratings.Count);
                int confirms=0,items=0;foreach(var ev in e.Events)
                {
                    Check.True(Object.ReferenceEquals(plan,ev.NamedPlan)&&ev.NamedOption==null&&ev.CarriesNamedProvenance);
                    if(ev.Kind=="named_plan_confirmed"){confirms++;Check.True(ev.NamedBlock==null&&ev.BlockIndex==-1);}
                    if(ev.Kind=="named_plan_item_confirmed"){items++;Check.True(ev.NamedBlock==null&&ev.BlockIndex==-1);}
                    if(ev.NamedBlock!=null)Check.Equal(ev.BlockIndex,ev.NamedBlock.Index);
                }
                Check.Equal(1,confirms);Check.Equal(4,items);
            });
            suite.Run("named plan old consumers reject typed and copied provenance before output",()=>{
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-plan",Selection());var c=new ManualClock();
                var config=SessionConfiguration.NamedPlanDevelopment("synthetic-plan-session",37,plan);var e=new Experiment(config,c);
                Check.Throws<InvalidOperationException>(()=>new Experiment(config,c,true,16));
                Check.Throws<InvalidOperationException>(()=>new Experiment(config,c,new ExperimentIncrementalOptions(16,128,65536)));
                Check.Throws<InvalidOperationException>(()=>new ViveWozParticipantHost(e,()=>{},true));
                var audit=new AuditLog("synthetic-plan-session",RunKind.Simulation);
                Check.Throws<InvalidOperationException>(()=>AuditExporter.Export(e,audit,"unused-path-never-created",new ExportProvenance("synthetic","onset","response")));
                Check.True(!Directory.Exists("unused-path-never-created"));
                var participant=new ViveDevelopmentParticipant(config,c,Geometry(),Mapping(),.5,true);var factory=new NeverOpenFactory();
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentJournalSink.OpenV2WithIdentityFactory("unused",null,c,null,
                    ViveDevelopmentDataSource.SyntheticFixture,0,0,true,true,factory,participant));Check.Equal(0,factory.Opens);
                e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                JournalEvent start=null;foreach(var item in e.Events)if(item.Kind=="practice_started")start=item;
                foreach(var item in new[]{start,LegacyCopy(start)})
                {
                    Check.True(item.CarriesNamedProvenance);var bytes=new MemoryStream();
                    Check.Throws<InvalidOperationException>(()=>new VivePracticeJournalV3Writer(bytes,c,null,item,ViveDevelopmentDataSource.SyntheticFixture,true,true,8,4096));
                    Check.Equal(0L,bytes.Length);Check.Throws<InvalidOperationException>(()=>VivePracticeMultiCodec.EncodeEvent(item,4096));
                    var provider=new NoPollProvider();var memory=new MemoryStream();
                    var source=new ViveParticipantEyeSource("synthetic-plan-session",1,2,"participant/vive/epoch","participant-device",
                        "operator/vive/epoch","operator-device",c,provider,new ViveProEyeRehearsalCoordinates());
                    var identity=new ViveDevelopmentStreamIdentity("synthetic-plan-session","participant","operator",ViveDevelopmentMeasurementRole.Participant,
                        "participant/vive/epoch","participant-device","test-monotonic",ViveProEyeRehearsalCoordinates.MappingVersion,ViveDevelopmentOperatorStatus.Present);
                    var bridge=ViveParticipantRecordingBridge.CreateV2WithIdentity(source,c,identity,ViveDevelopmentDataSource.SyntheticFixture,1,2,memory,()=>{},true,true);
                    long before=memory.Length;Check.True(!bridge.RecordAuthoritativeTaskEvent(item));Check.Equal(before,memory.Length);Check.Equal(0,provider.Calls);
                }
                var context=new VivePracticeContextV3("synthetic-plan-session","participant","stream","device",
                    "test-monotonic","mapping",1,"epoch",0,null,VivePracticeEpochReasonV3.Initial,start.Time);
                foreach(bool copied in new[]{false,true})
                {
                    var subset=new List<JournalEvent>();for(int i=0;i<3;i++){var ev=e.Events[(int)start.Sequence+i];subset.Add(copied?LegacyCopy(ev):ev);}
                    var batch=new VivePracticeStartBatch("synthetic-plan-session","test-monotonic",1,1,subset);
                    Check.Throws<InvalidOperationException>(()=>VivePracticeStartEnvelopeCodec.Encode(context,batch,8192));
                }
            });
            suite.Run("named multi-block participant uses same plan clock, hides absent avatar and handles withdrawal",()=>{
                var n=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);var a=Selection();
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-participant-plan",n,a);var c=new ManualClock();
                var p=new ViveDevelopmentParticipant(SessionConfiguration.NamedPlanDevelopment("participant-plan",37,plan),c,Geometry(),Mapping(),.5,true);
                Update(p);p.OwnerBeginPractice(Actor.Owner);
                for(int i=0;i<8;i++){Update(p);p.MarkPresented(p.CurrentTrial.TrialId);var ink=p.CurrentTrial.Stimulus.Ink;c.Advance(.4);Update(p,true,ink);Update(p);}
                Check.Equal(Phase.BlockPreparation,p.Phase);p.OwnerConfirmNamedPlan(Actor.Owner,"owner");p.OwnerBeginBlock(Actor.Owner);
                c.Advance(3);p.Tick();Update(p);Check.True(p.CurrentAvatar==null&&!p.ParticipantView.OperatorAvatarVisible);
                while(p.Phase==Phase.MainTask){p.MarkPresented(p.CurrentTrial.TrialId);var ink=p.CurrentTrial.Stimulus.Ink;c.Advance(.2);Update(p,true,ink);Update(p);}
                p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);p.UpdateAuxiliary(false,false,false,0,true);
                for(int i=0;i<Questionnaire.Count;i++){p.UpdateAuxiliary(true,false,false,0,true);p.UpdateAuxiliary(false,false,false,0,true);}
                Check.Equal(Phase.Rest,p.Phase);p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);
                Check.Equal(Phase.BlockReady,p.Phase);Check.True(Object.ReferenceEquals(plan.Blocks[1],p.CurrentNamedPlanBlock));
                p.UpdateTrackpad(false,new VivePreviewTrackpadSample(true,false,0,0));p.Tick();Check.Equal(Phase.BlockReady,p.Phase);
                Update(p);p.Tick();Check.Equal(Phase.Countdown,p.Phase);c.Advance(3);p.Tick();Update(p);Check.Equal(Phase.MainTask,p.Phase);
                p.MarkPresented(p.CurrentTrial.TrialId);c.Advance(3.25);var shown=p.CurrentAvatar;
                Check.True(shown!=null&&Object.ReferenceEquals(plan.Blocks[1],shown.NamedBlock));Check.Near(3.25,shown.Reference.ActiveSeconds);
                p.ParticipantPause();c.Advance(4);Check.Near(3.25,p.CurrentAvatar.Reference.ActiveSeconds);
                p.ParticipantWithdraw();Check.Equal((EndReason?)EndReason.Withdrawn,p.EndReason);Check.True(p.NamedViewWithdrawalRequested);
                foreach(var item in p.ReadEventsAfter(-1,4096))Check.True(Object.ReferenceEquals(plan,item.NamedPlan));
            });
            suite.Run("named VIVE selection is explicit owner-only Simulation with unimplemented choices rejected",()=>{
                Check.Throws<UnauthorizedAccessException>(()=>ViveNamedOptionProvenance.Select(Actor.Participant,ViveNamedOption.Automatic,0,"g"));
                foreach(var option in new[]{ViveNamedOption.LiveWoz,ViveNamedOption.DirectParticipantEyes})
                    Check.Throws<InvalidOperationException>(()=>ViveNamedOptionProvenance.Select(Actor.Owner,option,0,"g"));
                Check.Throws<ArgumentException>(()=>ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,0,"not-applicable"));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveNamedOptionProvenance.Select(Actor.Owner,(ViveNamedOption)99,0,"g"));
                Check.Throws<ArgumentOutOfRangeException>(()=>ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,4,"g"));
                Check.Throws<ArgumentException>(()=>ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0," "));
                Check.Throws<ArgumentException>(()=>SessionConfiguration.NamedAutomaticDevelopment("",37,Selection()));
                var config=Config(Selection());Check.Equal(RunKind.Simulation,config.RunKind);Check.Near(3,config.CountdownSeconds);
                Check.Throws<InvalidOperationException>(()=>{var ignored=config.Allocation;});
            });
            suite.Run("named VIVE authority records one real Experiment block and never emits old condition provenance",()=>{
                var selected=Selection();var c=new ManualClock();var e=new Experiment(Config(selected),c);
                Check.Throws<InvalidOperationException>(()=>{var ignored=e.CurrentCondition;});
                Check.Throws<InvalidOperationException>(()=>e.View(Actor.Operator));Start(e,c);
                Check.True(e.View(Actor.Participant).OperatorAvatarVisible);
                var first=e.CurrentTrial;Check.True(Object.ReferenceEquals(selected,first.NamedOption));
                Check.Throws<InvalidOperationException>(()=>{var ignored=first.Condition;});
                Check.Equal(TrialPlans.MainListId(37,2),first.ListId);
                e.MarkPresented(first.TrialId);c.Advance(3.25);double active=e.ActiveSeconds;
                var answer=first.Stimulus.Ink;Check.True(e.ButtonChanged(answer,true));e.ButtonChanged(answer,false);
                Check.Near(active,e.ActiveSeconds);Check.Equal(TrialOutcome.Responded,first.Outcome);
                Check.True(first.Onset.HasValue&&first.ResponseAt.HasValue);Check.Near(3.25,first.ReactionSeconds.Value);
                var interrupted=e.CurrentTrial;e.MarkPresented(interrupted.TrialId);e.Pause(Actor.Participant);
                Check.Equal(TrialOutcome.Interrupted,interrupted.Outcome);c.Advance(30);Check.Near(active,e.ActiveSeconds);
                e.Resume(true);c.Advance(3);e.Tick();Check.Equal(2,e.CurrentTrial.PlannedIndex);Check.Equal(1,e.Segment);
                while(e.Phase==Phase.MainTask)
                {
                    var trial=e.CurrentTrial;Check.True(Object.ReferenceEquals(selected,trial.NamedOption));
                    e.MarkPresented(trial.TrialId);c.Advance(.2);var ink=trial.Stimulus.Ink;
                    Check.True(e.ButtonChanged(ink,true));e.ButtonChanged(ink,false);
                }
                Check.Equal(Phase.Questionnaire,e.Phase);Check.True(e.AcknowledgeQuestionnaireInstruction());
                for(int i=0;i<Questionnaire.Count;i++)e.Rate(3);
                Check.Equal(Phase.Ended,e.Phase);Check.Equal((EndReason?)EndReason.Completed,e.EndReason);Check.Equal(0,e.BlockIndex);
                Check.Equal(24,e.Trials.Count-8);Check.Equal(Questionnaire.Count,e.Ratings.Count);
                foreach(var ev in e.Events)
                {
                    Check.True(Object.ReferenceEquals(selected,ev.NamedOption));
                    Check.Equal(ViveNamedOptionProvenance.SchemaVersion,ev.Fields["named_schema"]);
                    Check.Equal("Automatic",ev.Fields["named_option"]);
                    Check.Equal(ViveAutomaticCondition.ScheduleVersion,ev.Fields["named_schedule"]);
                    Check.Equal("synthetic-geometry",ev.Fields["named_geometry"]);
                    Check.True(ev.Fields.ContainsKey("named_phase")&&ev.Fields.ContainsKey("named_active_seconds"));
                    Check.True(!ev.Fields.ContainsKey("condition")&&!ev.Fields.ContainsKey("b_initial_target")&&!ev.Fields.ContainsKey("order"));
                    Check.Equal("test-monotonic",ev.ClockId);Check.Equal("named-session",ev.SessionId);
                }
            });
            suite.Run("named VIVE authority rejects legacy observers, CSV and WoZ host before I/O",()=>{
                var c=new ManualClock();var config=Config(Selection());var e=new Experiment(config,c);
                Check.Throws<InvalidOperationException>(()=>new Experiment(config,c,true,16));
                Check.Throws<InvalidOperationException>(()=>new Experiment(config,c,new ExperimentIncrementalOptions(16,128,65536)));
                Check.Throws<InvalidOperationException>(()=>new ViveWozParticipantHost(e,()=>{},true));
                var audit=new AuditLog("named-session",RunKind.Simulation);
                var provenance=new ExportProvenance("synthetic","test-onset","test-response");
                Check.Throws<InvalidOperationException>(()=>AuditExporter.Export(e,audit,"unused-path-never-created",provenance));
                Check.True(!System.IO.Directory.Exists("unused-path-never-created"));
            });
            suite.Run("legacy event copies carrying named fields are refused before v3 journal writes",()=>{
                var c=new ManualClock();var e=new Experiment(Config(Selection()),c);e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                JournalEvent start=null;foreach(var item in e.Events)if(item.Kind=="practice_started")start=item;
                Check.True(start!=null&&start.CarriesNamedProvenance);var copy=LegacyCopy(start);
                Check.True(copy.NamedOption==null&&copy.CarriesNamedProvenance);
                foreach(var candidate in new[]{start,copy})
                {
                    var memory=new MemoryStream();
                    Check.Throws<InvalidOperationException>(()=>new VivePracticeJournalV3Writer(memory,c,null,candidate,
                        ViveDevelopmentDataSource.SyntheticFixture,true,true,8,4096));
                    Check.Equal(0L,memory.Length);
                    Check.Throws<InvalidOperationException>(()=>VivePracticeMultiCodec.EncodeEvent(candidate,4096));
                }
                var events=new[]{start,e.Events[(int)start.Sequence+1],e.Events[(int)start.Sequence+2]};
                var context=new VivePracticeContextV3("named-session","synthetic-participant","synthetic-stream","synthetic-device",
                    "test-monotonic","synthetic-map",1,"synthetic-epoch",0,null,VivePracticeEpochReasonV3.Initial,start.Time);
                foreach(bool copyFields in new[]{false,true})
                {
                    var selectedEvents=new List<JournalEvent>();foreach(var item in events)selectedEvents.Add(copyFields?LegacyCopy(item):item);
                    var batch=new VivePracticeStartBatch("named-session","test-monotonic",1,1,selectedEvents);
                    Check.Throws<InvalidOperationException>(()=>VivePracticeStartEnvelopeCodec.Encode(context,batch,8192));
                }
                var fields=new Dictionary<string,string>{{"NaMeD_schema","copy"}};
                var mixedCase=new JournalEvent(0,0,"clock","session",-1,0,null,"practice_started",fields);
                Check.True(mixedCase.CarriesNamedProvenance);
            });
            suite.Run("legacy VIVE v2 bridge refuses named event and copied fields without appending",()=>{
                var c=new ManualClock();var e=new Experiment(Config(Selection()),c);var original=e.Events[0];
                foreach(var item in new[]{original,LegacyCopy(original)})
                {
                    var provider=new NoPollProvider();var memory=new MemoryStream();
                    var source=new ViveParticipantEyeSource("named-session",1,2,"participant/vive/epoch","participant-device",
                        "operator/vive/epoch","operator-device",c,provider,new ViveProEyeRehearsalCoordinates());
                    var identity=new ViveDevelopmentStreamIdentity("named-session","participant","operator",ViveDevelopmentMeasurementRole.Participant,
                        "participant/vive/epoch","participant-device","test-monotonic",ViveProEyeRehearsalCoordinates.MappingVersion,ViveDevelopmentOperatorStatus.Present);
                    var bridge=ViveParticipantRecordingBridge.CreateV2WithIdentity(source,c,identity,ViveDevelopmentDataSource.SyntheticFixture,1,2,memory,()=>{},true,true);
                    long before=memory.Length;Check.True(!bridge.RecordAuthoritativeTaskEvent(item));
                    Check.Equal(before,memory.Length);Check.Equal(0,provider.Calls);
                }
            });
            suite.Run("open legacy practice writer refuses named event copies without modifying bytes",()=>{
                var c=new ManualClock();var e=new Experiment(Config(Selection()),c);var named=e.Events[0];
                foreach(var item in new[]{named,LegacyCopy(named)})
                {
                    var memory=new MemoryStream();var context=new VivePracticeContextV3("named-session","participant","stream","device",
                        "test-monotonic","mapping",1,"epoch",0,null,VivePracticeEpochReasonV3.Initial,1);
                    var preparation=VivePracticeRecordingPreparationV3.Create(c,"named-session",1,"epoch",true,true);c.Now=2;
                    var legacyStart=new JournalEvent(0,1,"test-monotonic","named-session",-1,0,null,"practice_started",new Dictionary<string,string>{{"attempt","1"}});
                    var writer=new VivePracticeJournalV3Writer(memory,c,context,legacyStart,ViveDevelopmentDataSource.SyntheticFixture,true,true,20,65536,preparation);
                    long before=memory.Length;Check.Throws<InvalidOperationException>(()=>writer.WriteTaskEvent(item));
                    Check.Equal(before,memory.Length);c.Now=0;
                }
            });
            suite.Run("named participant renders from its own Experiment clock and records the same typed selection",()=>{
                var c=new ManualClock();var p=Participant(c);Check.True(p.HasNamedOption);Start(p,c);
                Update(p);p.MarkPresented(p.CurrentTrial.TrialId);c.Advance(3.25);var before=p.CurrentAvatar;
                Check.Equal(ReferencePhase.TurnToParticipant,before.Reference.Phase);Check.Near(.5,before.Reference.TransitionFraction);
                Check.Near(3.25,before.Reference.ActiveSeconds);Check.Equal(ViveNamedOption.Automatic,before.NamedOption.Option);
                Check.Equal("synthetic-geometry",before.NamedOption.GeometryVersion);Check.True(before.AvatarVisible);
                var trial=p.CurrentTrial;var ink=trial.Stimulus.Ink;Update(p,true,ink);Update(p);Check.Equal(TrialOutcome.Responded,trial.Outcome);
                var after=p.CurrentAvatar;Check.Near(before.Reference.ActiveSeconds,after.Reference.ActiveSeconds);
                Check.Near(before.ActualPose.Value.EyeDirection.X,after.ActualPose.Value.EyeDirection.X);
                Check.True(Object.ReferenceEquals(trial.NamedOption,after.NamedOption));
                var events=p.ReadEventsAfter(-1,4096);JournalEvent response=null;
                foreach(var item in events){Check.True(Object.ReferenceEquals(after.NamedOption,item.NamedOption));if(item.Kind=="first_response")response=item;}
                Check.True(response!=null);Check.Near(3.25,Double.Parse(response.Fields["named_active_seconds"],System.Globalization.CultureInfo.InvariantCulture));
                Check.Equal(ViveAutomaticCondition.ScheduleVersion,response.Fields["named_schedule"]);
                Check.Throws<InvalidOperationException>(()=>new AvatarPresentationRecord(new AuditContext(0,0),c.ClockId,c.Now,"synthetic",Phase.MainTask,after));
            });
            suite.Run("named participant is rejected before old sink opens or writes",()=>{
                var c=new ManualClock();var p=Participant(c);var factory=new NeverOpenFactory();
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentJournalSink.OpenV2WithIdentityFactory("unused",null,c,null,
                    ViveDevelopmentDataSource.SyntheticFixture,0,0,true,true,factory,p));
                Check.Equal(0,factory.Opens);
                var memory=new MemoryStream();
                Check.Throws<InvalidOperationException>(()=>ViveParticipantRecordingBridge.CreateV2WithIdentity(null,c,null,
                    ViveDevelopmentDataSource.SyntheticFixture,0,0,memory,()=>{},true,true,p));
                Check.Equal(0L,memory.Length);
            });
            suite.Run("named No Avatar uses same trials, no source or pose and explicit structural not-applicable provenance",()=>{
                var selected=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,2,null);
                Check.Equal(ViveNamedOptionProvenance.NoAvatarNotApplicable,selected.ScheduleVersion);
                Check.Equal(ViveNamedOptionProvenance.NoAvatarNotApplicable,selected.GeometryVersion);
                var c=new ManualClock();var config=SessionConfiguration.NamedNoAvatarDevelopment("named-no-avatar",37,selected);
                Check.Throws<ArgumentException>(()=>SessionConfiguration.NamedAutomaticDevelopment("wrong",37,selected));
                var p=new ViveDevelopmentParticipant(config,c,Geometry(),Mapping(),.5,true);
                var neverOpen=new NeverOpenFactory();
                Check.Throws<InvalidOperationException>(()=>ViveDevelopmentJournalSink.OpenV2WithIdentityFactory("unused",null,c,null,
                    ViveDevelopmentDataSource.SyntheticFixture,0,0,true,true,neverOpen,p));
                Check.Equal(0,neverOpen.Opens);
                var noAvatarExperiment=new Experiment(config,c);
                Check.Throws<InvalidOperationException>(()=>new Experiment(config,c,new ExperimentIncrementalOptions(16,128,65536)));
                Check.Throws<InvalidOperationException>(()=>new ViveWozParticipantHost(noAvatarExperiment,()=>{},true));
                Check.True(p.CurrentAvatar==null&&!p.ParticipantView.OperatorAvatarVisible);
                Update(p);p.OwnerBeginPractice(Actor.Owner);
                for(int i=0;i<8;i++)
                {
                    Check.True(p.CurrentAvatar==null&&!p.ParticipantView.OperatorAvatarVisible);
                    Update(p);p.MarkPresented(p.CurrentTrial.TrialId);var ink=p.CurrentTrial.Stimulus.Ink;c.Advance(.4);Update(p,true,ink);Update(p);
                }
                Check.Equal(Phase.BlockPreparation,p.Phase);Update(p);
                Check.Throws<InvalidOperationException>(()=>p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"owner"));
                Check.Equal(Phase.BlockPreparation,p.Phase);p.OwnerConfirmNamedNoAvatar(Actor.Owner,"owner");
                Check.Equal(Phase.BlockReady,p.Phase);Check.True(p.CurrentAvatar==null&&!p.ParticipantView.OperatorAvatarVisible);
                p.OwnerBeginBlock(Actor.Owner);c.Advance(3);p.Tick();Update(p);Check.Equal(Phase.MainTask,p.Phase);
                Check.True(p.CurrentAvatar==null&&!p.ParticipantView.OperatorAvatarVisible);
                var trial=p.CurrentTrial;var expected=TrialPlans.MainList(37,2)[0];
                Check.Equal(expected.Word,trial.Stimulus.Word);Check.Equal(expected.Ink,trial.Stimulus.Ink);
                Check.Equal(ViveNamedOption.NoAvatar,trial.NamedOption.Option);
                Check.Throws<InvalidOperationException>(()=>{var ignored=trial.Condition;});
                p.MarkPresented(trial.TrialId);c.Advance(1);p.ParticipantPause();Check.Equal(Phase.Paused,p.Phase);
                Check.True(p.CurrentAvatar==null&&!p.ParticipantView.OperatorAvatarVisible);Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
                Update(p);p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);
                Check.Equal(Phase.Countdown,p.Phase);c.Advance(3);p.Tick();Update(p);Check.Equal(Phase.MainTask,p.Phase);
                while(p.Phase==Phase.MainTask)
                {
                    Check.True(p.CurrentAvatar==null&&!p.ParticipantView.OperatorAvatarVisible);
                    p.MarkPresented(p.CurrentTrial.TrialId);var ink=p.CurrentTrial.Stimulus.Ink;c.Advance(.2);Update(p,true,ink);Update(p);
                }
                Check.Equal(Phase.Questionnaire,p.Phase);Check.True(p.CurrentAvatar==null&&!p.ParticipantView.OperatorAvatarVisible);
                var events=p.ReadEventsAfter(-1,4096);bool confirmed=false,interrupted=false;
                foreach(var item in events)
                {
                    Check.Equal("NoAvatar",item.Fields["named_option"]);Check.Equal(ViveNamedOptionProvenance.NoAvatarNotApplicable,item.Fields["named_source"]);
                    Check.Equal(ViveNamedOptionProvenance.NoAvatarNotApplicable,item.Fields["named_schedule"]);
                    Check.Equal(ViveNamedOptionProvenance.NoAvatarNotApplicable,item.Fields["named_geometry"]);
                    Check.Equal(ViveNamedOptionProvenance.NoAvatarNotApplicable,item.Fields["named_avatar_aoi"]);
                    Check.Equal("NOT_RECORDED",item.Fields["named_participant_eye"]);
                    Check.True(!item.Fields.ContainsKey("condition")&&!item.Fields.ContainsKey("b_initial_target"));
                    if(item.Kind=="named_option_confirmed")confirmed=true;if(item.Kind=="trial_interrupted")interrupted=true;
                }
                Check.True(confirmed&&interrupted);
                p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);Check.True(p.CurrentAvatar==null&&!p.ParticipantView.OperatorAvatarVisible);
            });
            suite.Run("named No Avatar completes one 24-trial block and the existing five ratings without a source",()=>{
                var selected=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,2,null);var c=new ManualClock();
                var e=new Experiment(SessionConfiguration.NamedNoAvatarDevelopment("no-avatar-complete",37,selected),c);
                Fixtures.Practice(e,c,8);e.ConfirmNamedNoAvatar(Actor.Owner,"owner");
                Check.True(!e.LockedControl.HasValue&&e.NamedOptionConfirmed);e.BeginCountdown(Actor.Owner,true);c.Advance(3);e.Tick();
                var plan=TrialPlans.MainList(37,2);int count=0;
                while(e.Phase==Phase.MainTask)
                {
                    var trial=e.CurrentTrial;Check.Equal(plan[count].Word,trial.Stimulus.Word);Check.Equal(plan[count].Ink,trial.Stimulus.Ink);
                    Check.True(!e.View(Actor.Participant).OperatorAvatarVisible);e.MarkPresented(trial.TrialId);c.Advance(.2);
                    var ink=trial.Stimulus.Ink;Check.True(e.ButtonChanged(ink,true));e.ButtonChanged(ink,false);count++;
                }
                Check.Equal(24,count);Check.Equal(Phase.Questionnaire,e.Phase);Check.True(e.AcknowledgeQuestionnaireInstruction());
                for(int i=0;i<Questionnaire.Count;i++)e.Rate(3);
                Check.Equal(Phase.Ended,e.Phase);Check.Equal((EndReason?)EndReason.Completed,e.EndReason);
                Check.True(!e.LockedControl.HasValue&&!e.View(Actor.Participant).OperatorAvatarVisible);Check.Equal(5,e.Ratings.Count);
            });
            suite.Run("legacy Experiment remains A/B/C/D with no named metadata",()=>{
                var c=new ManualClock();var e=Fixtures.New(c,Condition.B);
                Check.Equal<ViveNamedOptionProvenance>(null,e.Configuration.NamedOption);Fixtures.Practice(e,c,8);
                e.LockBlock(Actor.Owner,ControlSelection.Automatic,"legacy-owner");
                Check.Equal((Condition?)Condition.B,e.CurrentCondition);Check.True(e.Events[0].NamedOption==null);
                Check.True(e.Events[0].Fields.ContainsKey("order")&&!e.Events[0].Fields.ContainsKey("named_schema"));
            });
        }
    }
}
