using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class TutorialRecordingRoundtripTests
    {
        static SessionConfiguration Configuration(bool legacy,int mode,bool formalOnly=false,string participantId=null,bool operatorWoz=false)
        {
            SessionConfiguration config;
            if(participantId!=null)
            {
                ParticipantAssignment assignment;
                Check.True(ParticipantAssignment.TryCreate(participantId,mode==2?TaskKind.VisualSearch:TaskKind.Stroop,
                    mode==1?ResponseRule.WordMeaning:ResponseRule.InkColor,out assignment));
                var design=ViveLegacyRecordingDesign.SelectParticipant(Actor.Owner,"fixture-design",assignment,"training-geometry",ViveFixedTrackpadMapping.Version,operatorWoz);
                var basis=new SessionConfiguration(Guid.NewGuid().ToString("N"),ParticipantAssignment.StimulusSeed,assignment.Allocation,RunKind.Apparatus,"en",5,30,true,0,true,true,true);
                basis=SessionConfiguration.WithTaskKind(basis,assignment.TaskKind);
                basis=SessionConfiguration.WithResponseRule(basis,assignment.ResponseRule);
                basis=SessionConfiguration.WithFormalEyeRecordingOnly(basis);
                config=SessionConfiguration.ParticipantRecording(basis,design,ViveLegacyRecordingIntent.Select(Actor.Owner,"fixture-intent",true,true));
            }
            else if(legacy)
            {
                var allocation=new Allocation("training-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var design=ViveLegacyRecordingDesign.Select(Actor.Owner,"training-design",allocation,"training-geometry",ViveFixedTrackpadMapping.Version);
                var basis=new SessionConfiguration("training-raw",73,allocation,RunKind.Simulation,"en",5,30,true,0,true,true,true);
                if(formalOnly)basis=SessionConfiguration.WithFormalEyeRecordingOnly(basis);
                if(mode==2)basis=SessionConfiguration.WithTaskKind(basis,TaskKind.VisualSearch);
                config=SessionConfiguration.LegacyRecordingDevelopment(basis,design,ViveLegacyRecordingIntent.Select(Actor.Owner,"training-intent",true,true));
            }
            else
            {
                var a=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"training-geometry");
                var n=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"training-plan",a,n,a,n);
                config=SessionConfiguration.NamedRecordingDevelopment("training-raw",73,plan,ViveNamedRecordingIntent.Select(Actor.Owner,"training-intent",true,true));
                if(formalOnly)config=SessionConfiguration.WithFormalEyeRecordingOnly(config);
            }
            if(mode==1)config=SessionConfiguration.WithResponseRule(config,ResponseRule.WordMeaning);
            return SessionConfiguration.WithTrainingProtocol(config,TrainingProtocol.TutorialExamV1);
        }
        static void Answer(Experiment e,ManualClock clock,bool correct)
        {
            var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);clock.Advance(.3);
            if(trial.VisualSearch!=null)
                Check.True(e.SelectVisualSearchItem(trial.TrialId,correct?trial.VisualSearch.TargetId:trial.VisualSearch.Items.First(x=>x.Id!=trial.VisualSearch.TargetId).Id));
            else
            {var colour=correct?trial.ExpectedResponse:(Colour)(((int)trial.ExpectedResponse+1)%4);Check.True(e.ButtonChanged(colour,true));e.ButtonChanged(colour,false);}
        }
        internal static byte[] Fixture(bool legacy,int mode,out Experiment experiment,bool formalOnly=false,string participantId=null,bool operatorWoz=false)
        {
            var config=Configuration(legacy,mode,formalOnly,participantId,operatorWoz);var clock=new ManualClock();var e=new Experiment(config,clock);experiment=e;
            e.RecordRawSoftwareChoices(clock.ClockId,0,0);
            Check.Throws<InvalidOperationException>(()=>e.PrepareRawRecordingBlockPlan(Actor.Owner,true));
            e.BeginFamiliarization(Actor.Owner);e.BeginTutorial(Actor.Owner);Answer(e,clock,false);e.ContinueTutorial();
            var interrupted=e.CurrentTrial;e.MarkPresented(interrupted.TrialId);clock.Advance(.2);
            e.RecordRawTrackingGapStart(RawTrackingLoss.HeadPose,false);clock.Advance(2);e.RecordRawTrackingGapEnd(RawTrackingLoss.HeadPose,true);
            Check.Equal(TrialOutcome.Interrupted,interrupted.Outcome);Check.Equal(interrupted.PlannedIndex,e.CurrentTrial.PlannedIndex);
            Check.Equal(1,e.Segment);Answer(e,clock,true);
            for(int attempt=1;attempt<=3;attempt++)
            {
                if(attempt>1)Answer(e,clock,true);
                e.RequestPracticeExam();Check.Equal(0,e.Segment);
                var plan=e.PrepareRawRecordingBlockPlan(Actor.Owner,true);Check.Equal("10",plan.Fields["item_count"]);
                Check.True(e.MatchesRawRecordingBlockPlan(plan));e.BeginPractice(Actor.Owner);
                if(attempt==2)
                {
                    var held=e.CurrentTrial;e.MarkPresented(held.TrialId);clock.Advance(.1);
                    e.RecordFocusState(false,false,false);Check.Equal(Phase.Paused,e.Phase);
                    clock.Advance(1);e.RecordFocusState(true,true,false);e.Resume(true);
                    Check.Equal(held.PlannedIndex,e.CurrentTrial.PlannedIndex);Check.Equal(TrialOutcome.Interrupted,held.Outcome);
                }
                int score=attempt<3?7:8;for(int i=0;i<10;i++)Answer(e,clock,i<score);
                Check.Equal(Phase.PracticeExamResult,e.Phase);Check.Equal(score,e.PracticeCorrect);
                e.ContinuePracticeExamResult();Check.Equal(attempt<3?Phase.Tutorial:Phase.BlockPreparation,e.Phase);
            }
            if(legacy)
            {
                if(participantId!=null)
                    Check.Throws<InvalidOperationException>(()=>e.LockBlock(Actor.Owner,operatorWoz?ControlSelection.Automatic:ControlSelection.LiveWoz,"synthetic-owner"));
                e.LockBlock(Actor.Owner,operatorWoz?ControlSelection.LiveWoz:ControlSelection.Automatic,"synthetic-owner");
            }
            else e.ConfirmNamedPlan(Actor.Owner,"synthetic-owner");
            for(int block=0;block<4;block++)
            {
                e.PrepareRawRecordingBlockPlan(Actor.Owner,false);
                if(block==0)e.BeginCountdown(Actor.Owner,true);else Check.True(e.TryBeginRetainedBlockCountdown(true));
                clock.Advance(5);e.Tick();Check.Equal(Phase.MainTask,e.Phase);
                if(operatorWoz)
                {
                    // Explicitly synthetic schema evidence, not transport or
                    // device proof; exercises the typed-event assignment path.
                    var evidence=ViveLegacyWozRawSchemaTests.Fields(participant:clock.Now-3);
                    evidence["woz_participant_clock_id"]=clock.ClockId;
                    e.RecordLegacyWozEvidence(evidence);
                }
                for(int i=0;i<24;i++)Answer(e,clock,true);
                Check.Equal(Phase.Questionnaire,e.Phase);Check.True(e.AcknowledgeQuestionnaireInstruction());
                for(int i=0;i<Questionnaire.Count;i++)e.Rate(3);
                if(block<3)Check.True(e.Ready());
            }
            Check.Equal(EndReason.Completed,e.EndReason);
            var context=ViveRawSessionContext.FromConfiguration(config);
            var manifest=new ViveNamedSessionFrameAdmission.Manifest(context,clock.ClockId,true,0,0);manifest.SealChoiceTime(0,0);
            using(var output=new MemoryStream())
            {
                byte[] previous=new byte[32];long ordinal=0,tasks=0,quality=0;
                Action<byte[],bool> append=(payload,magic)=>
                {byte[] digest;byte[] framed=operatorWoz?ViveRawSessionJournalCodec.FrameV7(payload,previous,magic,out digest):participantId!=null?ViveRawSessionJournalCodec.FrameV6(payload,previous,magic,out digest):legacy?ViveRawSessionJournalCodec.FrameV4(payload,previous,magic,out digest):ViveRawSessionJournalCodec.FrameV3(payload,previous,magic,out digest);
                    output.Write(framed,0,framed.Length);previous=digest;};
                append(ViveRawSessionJournalCodec.CurrentManifestPayload(manifest),true);
                foreach(var item in e.Events.Skip(1))
                {
                    append(ViveRawSessionJournalCodec.RecordPayload(++ordinal,item,manifest),false);tasks++;
                    if(operatorWoz&&item.Kind=="first_response"&&item.BlockIndex==0&&quality<2)
                    {
                        // A missing-client observation is independent of a
                        // completed behavioural response; no gaze is invented.
                        append(ViveRawSessionJournalCodec.EyeObservationPayload(++ordinal,
                            new ViveRawEyeObservation(item.Time,ViveRawEyeObservation.Reason.ClientFaulted,item.Sequence)),false);quality++;
                    }
                }
                append(ViveRawSessionJournalCodec.TerminalPayloadV3(ordinal+1,e.Events.Last().Sequence,tasks,0,0,tasks+quality,clock.Now,quality),false);
                return output.ToArray();
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("Participant VRAW6 and VRAW7 retain assignment across three tasks and explicit control modes",()=>
            {
                var sessions=new System.Collections.Generic.HashSet<string>();
                foreach(bool woz in new[]{false,true})
                for(int mode=0;mode<3;mode++)
                {
                    Experiment e;byte[] bytes=Fixture(true,mode,out e,true,"P017",woz);
                    var assignment=e.Configuration.ParticipantAssignment;
                    Check.True(sessions.Add(e.Configuration.SessionId));
                    Check.Equal(woz?"VRAW0007":"VRAW0006",Encoding.ASCII.GetString(bytes,0,8));
                    var analysis=new StudyAnalysis();
                    using(var input=new MemoryStream(bytes,false))
                        Check.Equal("Complete",StudyDataReader.Read(input,"vraw",analysis.Accept,CancellationToken.None).Status);
                    analysis.Finish();Check.Equal("P017",analysis.ParticipantId);Check.Equal(assignment.TaskKey,analysis.ParticipantTask);
                    Check.Equal(woz?"OperatorWoZ":"Automatic",analysis.ControlMode);
                    if(woz)Check.True(analysis.EyeAvailability.Any(x=>x.State=="MISSING"&&x.Reason=="CLIENT_FAULTED"&&x.ObservationRows==2));
                    Check.Equal(woz?4:0,e.Events.Count(x=>x.Kind==ViveLegacyWozRecordingSchema.EventKind));
                    var sources=e.Events.Where(x=>x.Kind=="block_configuration_locked").ToArray();
                    Check.Equal(4,sources.Length);Check.True(sources.All(x=>x.Fields["selection"]==(woz?"LiveWoz":"Automatic")));
                    Check.Equal(SessionConfiguration.ParticipantSoftwareVersion,analysis.SoftwareVersion);
                    Check.Equal("PARTICIPANT_APPARATUS",analysis.RunKind);Check.Equal(String.Concat(assignment.Allocation.Order),analysis.ConditionOrder);
                    Check.Equal(ParticipantAssignment.SchemeVersion,analysis.AllocationScheme);Check.Equal(assignment.Allocation.AllocationId,analysis.AllocationId);
                    Check.Equal(96,analysis.Trials.Count(x=>x.Scope=="Formal"));Check.Equal(20,analysis.Ratings.Count);Check.Equal(4,analysis.EndedBlocks);
                    Check.Equal("formal-session-v1",analysis.EyeRecordingPolicy);
#if VIVE_RAW_SYNTHETIC_TEST
                    using(var input=new MemoryStream(bytes,false))using(var csv=new MemoryStream())
                    {
                        Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,
                            ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,csv,8388608,5000,134217728,CancellationToken.None));
                        var fromCsv=new StudyAnalysis();
                        using(var readOnly=new MemoryStream(csv.GetBuffer(),0,(int)csv.Length,false))
                            Check.Equal("Complete",StudyDataReader.Read(readOnly,"csv",fromCsv.Accept,CancellationToken.None).Status);
                        fromCsv.Finish();Check.Equal(analysis.SoftwareVersion,fromCsv.SoftwareVersion);Check.Equal(analysis.SessionId,fromCsv.SessionId);Check.Equal(analysis.ParticipantId,fromCsv.ParticipantId);
                        Check.Equal(analysis.ParticipantTask,fromCsv.ParticipantTask);Check.Equal(analysis.ConditionOrder,fromCsv.ConditionOrder);
                        Check.Equal(analysis.ListByBlock,fromCsv.ListByBlock);Check.Equal(analysis.BInitialTarget,fromCsv.BInitialTarget);
                        Check.Equal(analysis.ControlMode,fromCsv.ControlMode);
                    }
#endif
                }
            });
            suite.Run("Formal-only eye policy survives VRAW3 VRAW4 full training and formal roundtrip without invented eye rows",()=>{
                foreach(bool legacy in new[]{false,true})for(int mode=0;mode<(legacy?3:2);mode++)
                {
                    Experiment e;byte[] bytes=Fixture(legacy,mode,out e,true);
                    Check.True(e.Configuration.FormalEyeRecordingOnly);
                    Check.True(e.Events.All(x=>x.Fields["eye_recording_policy"]=="formal-session-v1"));
                    var windows=e.Events.Where(x=>x.Kind=="formal_eye_recording_window_open").ToArray();
                    Check.Equal(1,windows.Length);var window=windows[0];
                    Check.Equal(0,window.BlockIndex);Check.Equal("formal",window.Fields["learning_stage"]);
                    Check.Equal("first_formal_countdown",window.Fields["boundary"]);
                    Check.Equal("declared_window_not_sample_coverage",window.Fields["evidence"]);
                    Check.Equal(window.Time,e.FormalEyeRecordingWindowStart.Value);
                    Check.True(e.Events.Where(x=>x.Fields["learning_stage"]=="tutorial"||x.Fields["learning_stage"]=="practice_exam")
                        .All(x=>x.Sequence<window.Sequence));
                    Check.True(e.Events.Where(x=>x.Kind=="first_response"&&x.Fields["learning_stage"]=="formal")
                        .All(x=>x.Sequence>window.Sequence));

                    var analysis=new StudyAnalysis();StudyDataReadSummary read;
                    using(var stream=new MemoryStream(bytes,false))read=StudyDataReader.Read(stream,"vraw",analysis.Accept,CancellationToken.None);
                    Check.True(read.Accepted,read.Message);analysis.Finish();
                    Check.Equal("formal-session-v1",analysis.EyeRecordingPolicy);
                    Check.Equal("NOT_REQUESTED_TRAINING",analysis.TrainingEyeExpectation);
                    Check.Equal(1,analysis.FormalEyeWindowOpenEvents);
                    Check.Equal(window.Time,analysis.FormalEyeWindowOpenTime.Value);
                    Check.Equal(36,analysis.Trials.Count(x=>x.EyeRecordingExpectation=="NOT_REQUESTED_TRAINING"));
                    Check.Equal(96,analysis.Trials.Count(x=>x.EyeRecordingExpectation=="EXPECTED_FORMAL"));
                    Check.True(analysis.Trials.All(x=>x.EyeRecordingPolicy=="formal-session-v1"));
                    Check.Equal(3,analysis.TrainingAttempts.Count);
                    foreach(var attempt in analysis.TrainingAttempts)
                    {Check.Equal(attempt.Attempt<3?7:8,attempt.ReconstructedCorrect);Check.Equal(attempt.Attempt==3,attempt.Passed);Check.Equal(0,attempt.Flags.Count);}
                    // This task-event fixture never supplies hardware eye data.
                    // The declaration must not manufacture coverage or missing-eye observations.
                    Check.Equal(0,analysis.EyeGroups.Count());Check.True(!analysis.UnexpectedPreformalEyeRows);
                }
            });
            suite.Run("Tutorial VRAW3 and VRAW4 roundtrip keeps training scopes retries tenth items and attempt three",()=>{
                foreach(bool legacy in new[]{false,true})for(int mode=0;mode<(legacy?3:2);mode++)
                {
                    Experiment e;byte[] bytes=Fixture(legacy,mode,out e);ViveRawSessionReadResult result;
                    using(var stream=new MemoryStream(bytes,false))result=ViveRawSessionJournalReader.ProjectTrials(stream,true,true,8388608,5000,CancellationToken.None);
                    if(result.Status!=ViveRawSessionReadStatus.VerifiedPrefix)
                    {
                        string last="no decoded record";
                        using(var stream=new MemoryStream(bytes,false))ViveRawSessionJournalReader.ScanOfflineStudyRows(stream,8388608,5000,CancellationToken.None,rows=>last=rows);
                        throw new Exception("Training raw roundtrip failed: legacy="+legacy+", mode="+mode+", status="+result.Status+", last decoded frame="+last);
                    }
                    Check.True(result.CaptureTerminalObserved);Check.Equal(e.Events.Count-1,result.VerifiedTaskRecords);
                    Check.Equal(e.Trials.Count,result.TrialProjection.Trials.Count);Check.Equal(7,result.TrialProjection.PreparedPlans.Count);
                    Check.Equal(3,result.TrialProjection.PreparedPlans.Count(x=>x.Scope=="PRACTICE"&&x.Items.Count==10));
                    for(int attempt=1;attempt<=3;attempt++)
                    {
                        string prefix=e.Configuration.SessionId+"/p"+attempt+"/";
                        var trials=result.TrialProjection.Trials.Where(x=>x.TrialId.StartsWith(prefix,StringComparison.Ordinal)).ToArray();
                        Check.Equal(attempt==2?11:10,trials.Length);Check.Equal(10,trials.Count(x=>x.ResponseEvents==1));Check.True(trials.All(x=>!x.EventConflict));
                        Check.Equal(attempt<3?7:8,trials.Count(x=>x.DeclaredCorrect=="True"));
                        if(attempt==2)Check.Equal(1,trials.Count(x=>x.InterruptionReason=="training_interruption"));
                    }
                    var tutorial=result.TrialProjection.Trials.Where(x=>x.PlannedList.StartsWith("tutorial-",StringComparison.Ordinal)).ToArray();
                    Check.Equal(5,tutorial.Length);Check.Equal(1,tutorial.Count(x=>x.InterruptionReason=="tracking_gap"));
                    Check.Equal(4,tutorial.Count(x=>x.ResponseEvents==1));Check.True(tutorial.All(x=>x.Block==-1&&!x.EventConflict));
                    var decoded=new StringBuilder();
                    using(var stream=new MemoryStream(bytes,false))
                    {result=ViveRawSessionJournalReader.ScanOfflineStudyRows(stream,8388608,5000,CancellationToken.None,rows=>decoded.Append(rows));}
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                    Check.True(decoded.Length>0,"Reader must return verified field frames for the offline decoder.");
                    Check.True(e.Events.Where(x=>x.Kind=="first_response"&&x.TrialId.Contains("/tutorial-")).All(x=>x.Fields["learning_stage"]=="tutorial"));
                    Check.True(e.Events.Where(x=>x.Kind=="first_response"&&x.TrialId.Contains("/b0/")).All(x=>x.Fields["learning_stage"]=="formal"));
                    var analysis=new StudyAnalysis();StudyDataReadSummary read;
                    using(var stream=new MemoryStream(bytes,false))read=StudyDataReader.Read(stream,"vraw",analysis.Accept,CancellationToken.None);
                    Check.True(read.Accepted,read.Message);analysis.Finish();
                    Check.Equal(5,analysis.Trials.Count(x=>x.Scope=="Tutorial"));Check.Equal(31,analysis.Trials.Count(x=>x.Scope=="PracticeExam"));
                    Check.Equal(96,analysis.Trials.Count(x=>x.Scope=="Formal"));Check.Equal(3,analysis.TrainingAttempts.Count);
                    foreach(var attempt in analysis.TrainingAttempts)
                    {
                        Check.Equal("tutorial-exam-v1",attempt.TrainingProtocol);
                        Check.Equal(10,attempt.Planned);Check.Equal(8,attempt.Threshold);
                        Check.Equal(attempt.Attempt<3?7:8,attempt.ReconstructedCorrect);Check.Equal(attempt.Attempt==3,attempt.Passed);
                        Check.Equal(0,attempt.Flags.Count,String.Join(";",attempt.Flags));
                    }
                }
            });
        }
    }
}
