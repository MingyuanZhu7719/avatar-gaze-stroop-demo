using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Real Experiment code and actual VRAW0003 codec; all inputs synthetic.
    // No headset, participant, file publication, or physical writes.
    public static class ViveRawTrialProjectionTests
    {
        sealed class Clock:IClock {public double Now {get;set;}public string ClockId {get{return "synthetic-clock";}}internal void Advance(double dt){Now+=dt;}}
        internal static byte[] FourBlocks(bool truncateTerminal=false,bool omitFirstPlanned=false)
        {
            var automatic=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,2,"synthetic-geometry");
            var noAvatar=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-plan",automatic,noAvatar,automatic,noAvatar);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-intent",true,true);
            var clock=new Clock();var e=new Experiment(SessionConfiguration.NamedRecordingDevelopment("synthetic-session",37,plan,intent),clock);
            e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
            for(int i=0;i<8;i++){e.MarkPresented(e.CurrentTrial.TrialId);clock.Advance(.4);var answer=e.CurrentTrial.Stimulus.Ink;Check.True(e.ButtonChanged(answer,true));e.ButtonChanged(answer,false);}
            e.ConfirmNamedPlan(Actor.Owner,"synthetic-owner");e.BeginCountdown(Actor.Owner,true);clock.Advance(3);e.Tick();
            for(int block=0;block<4;block++)
            {
                Check.Equal(Phase.MainTask,e.Phase);
                if(block==1){e.MarkPresented(e.CurrentTrial.TrialId);clock.Advance(.2);e.Pause(Actor.Participant);clock.Advance(.3);e.Resume(true);clock.Advance(3);e.Tick();}
                while(e.Phase==Phase.MainTask){e.MarkPresented(e.CurrentTrial.TrialId);clock.Advance(.2);var answer=e.CurrentTrial.Stimulus.Ink;Check.True(e.ButtonChanged(answer,true));e.ButtonChanged(answer,false);}
                Check.Equal(Phase.Questionnaire,e.Phase);Check.True(e.AcknowledgeQuestionnaireInstruction());
                for(int q=0;q<Questionnaire.Count;q++)e.Rate(q+1);
                if(block<3){Check.True(e.Ready());Check.True(e.TryBeginRetainedBlockCountdown(true));clock.Advance(3);e.Tick();}
            }
            Check.Equal(EndReason.Completed,e.EndReason.Value);
            int baseline=0;
            if(omitFirstPlanned){for(int i=0;i<e.Events.Count;i++)if(e.Events[i].Kind=="trial_planned"){baseline=i;break;}}
            var manifest=new ViveNamedSessionFrameAdmission.Manifest("synthetic-session",clock.ClockId,plan,intent,true);manifest.SealChoiceTime(0,baseline);
            using(var output=new MemoryStream())
            {
                byte[] previous=new byte[32];long ordinal=0,tasks=0;
                Action<byte[],bool> append=(payload,magic)=>{byte[] digest,frame=ViveRawSessionJournalCodec.FrameV3(payload,previous,magic,out digest);output.Write(frame,0,frame.Length);previous=digest;};
                append(ViveRawSessionJournalCodec.ManifestPayloadV3(manifest),true);
                for(int i=baseline+1;i<e.Events.Count;i++){ordinal++;append(ViveRawSessionJournalCodec.RecordPayload(ordinal,e.Events[i],manifest),false);tasks++;}
                append(ViveRawSessionJournalCodec.TerminalPayloadV3(ordinal+1,e.Events[e.Events.Count-1].Sequence,tasks,0,0,tasks,clock.Now),false);
                byte[] result=output.ToArray();if(truncateTerminal)Array.Resize(ref result,result.Length-1);return result;
            }
        }
        static ViveRawSessionReadResult Project(byte[] bytes)
        {using(var input=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,5000,CancellationToken.None);}
        static byte[] TooManyTrials()
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"geometry-fixture");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"plan-fixture",option);var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"intent-fixture",true,true);
            var m=new ViveNamedSessionFrameAdmission.Manifest("session-fixture","clock-fixture",plan,intent,true);m.SealChoiceTime(0,-1);
            var fields=new Dictionary<string,string>{{"named_schema","vive-named-recording-intent-v3"},{"named_plan_id",plan.PlanId},{"named_plan_schema","vive-named-simulation-plan-v2"},
                {"named_recording_intent_id",intent.IntentId},{"named_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT"},{"named_task_recording_intent","REQUESTED"},{"named_eye_recording_intent","REQUESTED"},
                {"named_block_index","0"},{"named_option","Automatic"},{"named_list_index","1"},{"named_schedule",option.ScheduleVersion},{"named_geometry",option.GeometryVersion}};
            using(var output=new MemoryStream())
            {
                byte[] prior=new byte[32];Action<byte[],bool> add=(payload,magic)=>{byte[] digest,frame=ViveRawSessionJournalCodec.FrameV3(payload,prior,magic,out digest);output.Write(frame,0,frame.Length);prior=digest;};
                add(ViveRawSessionJournalCodec.ManifestPayloadV3(m),true);
                for(int i=0;i<513;i++)
                {var task=new JournalEvent(i,i+1,"clock-fixture","session-fixture",0,0,"trial-"+i,"trial_planned",fields,null,plan,plan.Blocks[0],intent);
                    add(ViveRawSessionJournalCodec.RecordPayload(i+1,task,m),false);}
                return output.ToArray();
            }
        }
        static byte[] ReorderedTrial(bool interruption,bool backwardsTime=false,bool legacy=false)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"geometry-fixture");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"plan-fixture",option);var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"intent-fixture",true,true);
            var m=new ViveNamedSessionFrameAdmission.Manifest("session-fixture","clock-fixture",plan,intent,true);m.SealChoiceTime(0,-1);
            var fields=new Dictionary<string,string>{{"named_schema","vive-named-recording-intent-v3"},{"named_plan_id",plan.PlanId},{"named_plan_schema","vive-named-simulation-plan-v2"},
                {"named_recording_intent_id",intent.IntentId},{"named_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT"},{"named_task_recording_intent","REQUESTED"},{"named_eye_recording_intent","REQUESTED"},
                {"named_block_index","0"},{"named_option","Automatic"},{"named_list_index","1"},{"named_schedule",option.ScheduleVersion},{"named_geometry",option.GeometryVersion}};
            using(var output=new MemoryStream())
            {
                byte[] prior=new byte[32];Action<byte[],bool> add=(payload,magic)=>{byte[] digest,frame=legacy?ViveRawSessionJournalCodec.Frame(payload,prior,magic,out digest):ViveRawSessionJournalCodec.FrameV3(payload,prior,magic,out digest);output.Write(frame,0,frame.Length);prior=digest;};
                add(legacy?ViveRawSessionJournalCodec.ManifestPayload(m):ViveRawSessionJournalCodec.ManifestPayloadV3(m),true);
                string first=backwardsTime?"trial_planned":interruption?"trial_interrupted":"first_response";
                string second=backwardsTime?"first_response":"trial_planned";
                var a=new JournalEvent(0,backwardsTime?2:1,"clock-fixture","session-fixture",0,0,"x",first,fields,null,plan,plan.Blocks[0],intent);
                var b=new JournalEvent(1,backwardsTime?1:2,"clock-fixture","session-fixture",0,0,"x",second,fields,null,plan,plan.Blocks[0],intent);
                add(ViveRawSessionJournalCodec.RecordPayload(1,a,m),false);add(ViveRawSessionJournalCodec.RecordPayload(2,b,m),false);return output.ToArray();
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VRAW trial projection uses actual four-block experiment events with unknown validity",()=>{
                var result=Project(FourBlocks());Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.True(result.CaptureTerminalObserved);
                var projection=result.TrialProjection;Check.True(projection!=null&&!projection.CompleteSessionProven&&!projection.SourceAuthenticated&&!projection.ParticipantConsentAuthenticated&&!projection.SamplesJoinedToTrials);
                Check.Equal("UNKNOWN",projection.Coverage);Check.Equal("UNKNOWN",projection.CurrentWithdrawalStatus);Check.True(!projection.CurrentAccessAuthorized);
                Check.Equal(104,projection.Trials.Count);Check.Equal(20,projection.Ratings.Count);Check.Equal(0,projection.UnassignedTrialEvents);
                Check.Equal(0,projection.ObservedStatuses.Count);
                Check.Equal("synthetic-session",projection.DeclaredSessionId);Check.Equal("synthetic-clock",projection.DeclaredClockId);Check.Equal("synthetic-plan",projection.DeclaredPlanId);
                Check.Equal(4,projection.DeclaredPlanBlocks.Count);Check.Equal("Automatic",projection.DeclaredPlanBlocks[0].Option);Check.Equal("NoAvatar",projection.DeclaredPlanBlocks[1].Option);
                Check.Equal(2,projection.DeclaredPlanBlocks[0].ListIndex);Check.Equal(1,projection.DeclaredPlanBlocks[1].ListIndex);
                int[] blocks=new int[4];int practice=0,interrupted=0,responses=0;
                foreach(var trial in projection.Trials)
                {Check.Equal("UNKNOWN",trial.Validity);Check.Equal("UNKNOWN",trial.GazeAlignment);Check.True(!trial.EventConflict&&trial.PlannedEvents==1);
                    Check.Equal("DECLARED_V1",trial.PlannedStimulusState);Check.Equal(trial.PlannedWord,trial.Word);Check.Equal(trial.PlannedInk,trial.Ink);
                    if(trial.Block==-1)practice++;else{Check.True(trial.Block>=0&&trial.Block<4);blocks[trial.Block]++;}
                    if(trial.InterruptionEvents>0){interrupted++;Check.Equal(0,trial.ResponseEvents);Check.Equal((string)null,trial.DeclaredCorrect);}
                    if(trial.ResponseEvents>0){Check.True(trial.DeclaredOnset!=null&&trial.DeclaredReactionSeconds!=null);Check.Equal(trial.PlannedList,trial.PresentedList);Check.Equal(trial.PlannedIndex,trial.PresentedIndex);}
                    responses+=trial.ResponseEvents;}
                Check.Equal(8,practice);foreach(int count in blocks)Check.Equal(24,count);Check.Equal(1,interrupted);Check.Equal(103,responses);
                foreach(var rating in projection.Ratings){Check.Equal("UNKNOWN",rating.Validity);Check.True(rating.Item!=null&&rating.DeclaredValue!=null);}
                bool sawPause=false,sawResume=false,sawInterrupted=false,sawEnd=false;
                foreach(var safety in projection.ObservedSafetyEvents)
                {if(safety.Kind=="explicit_pause")sawPause=true;else if(safety.Kind=="explicit_resume")sawResume=true;
                    else if(safety.Kind=="trial_interrupted"){sawInterrupted=true;Check.Equal("Participant",safety.Actor);}
                    else if(safety.Kind=="session_ended"){sawEnd=true;Check.Equal("Completed",safety.Reason);}}
                Check.True(sawPause&&sawResume&&sawInterrupted&&sawEnd);
            });
            suite.Run("VRAW trial projection keeps orphaned declarations and discards corrupt output",()=>{
                var partial=Project(FourBlocks(omitFirstPlanned:true));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,partial.Status);
                Check.Equal(104,partial.TrialProjection.Trials.Count);Check.Equal(0,partial.TrialProjection.Trials[0].PlannedEvents);
                Check.Equal(1,partial.TrialProjection.Trials[0].PresentationEvents);Check.True(partial.TrialProjection.Trials[0].IncompleteEvidence);Check.Equal("UNKNOWN",partial.TrialProjection.Trials[0].Validity);
                Check.Equal("UNKNOWN",partial.TrialProjection.Trials[0].PlannedStimulusState);Check.Equal((string)null,partial.TrialProjection.Trials[0].PlannedWord);
                var truncated=Project(FourBlocks(truncateTerminal:true));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,truncated.Status);
                Check.True(truncated.TrailingFrameTruncated&&!truncated.CaptureTerminalObserved&&truncated.TrialProjection!=null&&!truncated.TrialProjection.CompleteSessionProven);
                byte[] corrupted=FourBlocks();corrupted[corrupted.Length-1]^=1;var invalid=Project(corrupted);
                Check.Equal(ViveRawSessionReadStatus.Invalid,invalid.Status);Check.Equal((ViveRawTrialProjection)null,invalid.TrialProjection);Check.Equal(0L,invalid.VerifiedBytes);
            });
            suite.Run("VRAW trial projection explicitly records duplicate and mismatched context without declaring validity",()=>{
                var accumulator=new ViveRawTrialAccumulator();var fields=new Dictionary<string,string>{{"response","Red"},{"correct","True"},{"rt_seconds",".2"}};
                accumulator.Observe("first_response","trial",0,0,1,1,fields);accumulator.Observe("first_response","trial",1,1,2,2,fields);
                accumulator.Observe("trial_interrupted","trial",0,0,3,3,new Dictionary<string,string>());
                accumulator.Observe("stimulus_presented",null,0,0,4,4,new Dictionary<string,string>());
                var projection=accumulator.Finish();Check.Equal(1,projection.Trials.Count);Check.Equal(1,projection.UnassignedTrialEvents);
                var row=projection.Trials[0];Check.True(row.ContextConflict&&row.EventConflict);Check.Equal(2,row.ResponseEvents);Check.Equal(1,row.InterruptionEvents);Check.Equal("UNKNOWN",row.Validity);
                var changed=new ViveRawTrialAccumulator();changed.Observe("trial_planned","x",0,0,1,1,new Dictionary<string,string>{{"list","one"},{"planned_index","2"}});
                changed.Observe("stimulus_presented","x",0,0,2,2,new Dictionary<string,string>{{"list","two"},{"planned_index","2"}});
                Check.True(changed.Finish().Trials[0].EventConflict);
            });
            suite.Run("VRAW hashed prefix marks reordered or missing trial evidence explicitly",()=>{
                foreach(var bytes in new[]{ReorderedTrial(false),ReorderedTrial(true),ReorderedTrial(false,true)})
                {var result=Project(bytes);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);var trial=result.TrialProjection.Trials[0];
                    Check.True(trial.EventConflict);Check.Equal("UNKNOWN",trial.Validity);}
                var first=Project(ReorderedTrial(false)).TrialProjection.Trials[0];Check.True(first.IncompleteEvidence);
            });
            suite.Run("VRAW old v2 and v3 planned events without stimulus remain UNKNOWN without backfill",()=>{
                foreach(bool legacy in new[]{false,true})
                {var result=Project(ReorderedTrial(false,legacy:legacy));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                    Check.Equal(0,result.TrialProjection.PreparedPlans.Count);Check.Equal(0,result.TrialProjection.UnsupportedPreparedPlanEvents);Check.Equal("UNKNOWN",result.TrialProjection.WriterConfirmation);
                    var trial=result.TrialProjection.Trials[0];Check.Equal(1,trial.PlannedEvents);Check.Equal("UNKNOWN",trial.PlannedStimulusState);
                    Check.Equal((string)null,trial.PlannedStimulusSchema);Check.Equal((string)null,trial.PlannedWord);Check.Equal((string)null,trial.PlannedInk);}
            });
            suite.Run("VRAW planned stimulus declaration keeps unknown schemas and rejects contradictions",()=>{
                Check.Equal("UNKNOWN",ViveRawTrialAccumulator.StimulusState(null,null,null));
                Check.Equal("DECLARED_V1",ViveRawTrialAccumulator.StimulusState("1","Red","Green"));
                Check.Equal("CONFLICT",ViveRawTrialAccumulator.StimulusState("1","Red",null));
                Check.Equal("CONFLICT",ViveRawTrialAccumulator.StimulusState(null,"Red","Green"));
                Check.Equal("CONFLICT",ViveRawTrialAccumulator.StimulusState("1","0","Green"));
                Check.Equal("UNSUPPORTED_SCHEMA",ViveRawTrialAccumulator.StimulusState("2","Red","Green"));
                var acc=new ViveRawTrialAccumulator();acc.Observe("trial_planned","x",0,0,1,1,new Dictionary<string,string>{{"planned_stimulus_schema","1"},{"planned_word","Red"},{"planned_ink","Green"}});
                acc.Observe("stimulus_presented","x",0,0,2,2,new Dictionary<string,string>{{"word","Blue"},{"ink","Green"}});
                var trial=acc.Finish().Trials[0];Check.Equal("DECLARED_V1",trial.PlannedStimulusState);Check.True(trial.EventConflict);Check.Equal("UNKNOWN",trial.Validity);
            });
            suite.Run("VRAW trial projection drops staged results when bounded limits or cancellation are hit",()=>{
                var limited=Project(TooManyTrials());Check.Equal(ViveRawSessionReadStatus.LimitExceeded,limited.Status);
                Check.Equal((ViveRawTrialProjection)null,limited.TrialProjection);Check.Equal(0L,limited.VerifiedBytes);
                using(var token=new CancellationTokenSource())using(var input=new MemoryStream(FourBlocks(),false))
                {token.Cancel();var cancelled=ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,5000,token.Token);
                    Check.Equal(ViveRawSessionReadStatus.Cancelled,cancelled.Status);Check.Equal((ViveRawTrialProjection)null,cancelled.TrialProjection);}
            });
            suite.Run("VRAW trial projection preserves bounded provider status transitions without joining trials",()=>{
                var result=Project(ViveRawSessionTerminalTests.Fixture(withStatus:true));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                var view=result.TrialProjection;Check.Equal(2,view.ObservedStatuses.Count);Check.True(!view.SamplesJoinedToTrials);
                Check.Equal(ViveProEyeWorker.EyeStatusCause.PollMissing,view.ObservedStatuses[0].CauseFlags);
                Check.Equal((bool?)null,view.ObservedStatuses[0].EyeDeviceConnected);Check.Equal(ViveProEyeWorker.EyeStatusCause.None,view.ObservedStatuses[1].CauseFlags);
                Check.Equal((bool?)true,view.ObservedStatuses[1].EyeDeviceConnected);Check.Equal("UNKNOWN",view.Coverage);
            });
            suite.Run("VRAW projection withholds observed withdrawal and unrecognized terminal reasons",()=>{
                foreach(var bytes in new[]{ViveRawSessionTerminalTests.Fixture(reason:"Withdrawn"),ViveRawSessionTerminalTests.Fixture(reason:"unrecognized")})
                {var r=Project(bytes);Check.Equal(ViveRawSessionReadStatus.ProjectionWithheld,r.Status);Check.Equal((ViveRawTrialProjection)null,r.TrialProjection);Check.Equal(0L,r.VerifiedBytes);}
            });
            suite.Run("VRAW nonwithdrawn technical end is an observed failure, not a valid or durable session",()=>{
                var result=Project(ViveRawSessionTerminalTests.Fixture(reason:"TechnicalEnd",omitTerminal:true));
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.True(!result.CaptureTerminalObserved&&!result.CompleteSessionProven);
                Check.Equal(2,result.TrialProjection.ObservedSafetyEvents.Count);
                Check.Equal("session_ended",result.TrialProjection.ObservedSafetyEvents[0].Kind);Check.Equal("TechnicalEnd",result.TrialProjection.ObservedSafetyEvents[0].Reason);
                Check.Equal("phase_changed",result.TrialProjection.ObservedSafetyEvents[1].Kind);Check.Equal("Ended",result.TrialProjection.ObservedSafetyEvents[1].PhaseTo);
                Check.Equal("UNKNOWN",result.TrialProjection.Coverage);Check.True(!result.TrialProjection.CurrentAccessAuthorized);
            });
        }
    }
}
