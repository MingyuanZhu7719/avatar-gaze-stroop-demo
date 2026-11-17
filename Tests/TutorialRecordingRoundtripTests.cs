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
        static SessionConfiguration Configuration(bool legacy,int mode)
        {
            SessionConfiguration config;
            if(legacy)
            {
                var allocation=new Allocation("training-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var design=ViveLegacyRecordingDesign.Select(Actor.Owner,"training-design",allocation,"training-geometry",ViveFixedTrackpadMapping.Version);
                var basis=new SessionConfiguration("training-raw",73,allocation,RunKind.Simulation,"en",5,30,true,0,true,true,true);
                if(mode==2)basis=SessionConfiguration.WithTaskKind(basis,TaskKind.VisualSearch);
                config=SessionConfiguration.LegacyRecordingDevelopment(basis,design,ViveLegacyRecordingIntent.Select(Actor.Owner,"training-intent",true,true));
            }
            else
            {
                var a=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"training-geometry");
                var n=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"training-plan",a,n,a,n);
                config=SessionConfiguration.NamedRecordingDevelopment("training-raw",73,plan,ViveNamedRecordingIntent.Select(Actor.Owner,"training-intent",true,true));
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
        static byte[] Fixture(bool legacy,int mode,out Experiment experiment)
        {
            var config=Configuration(legacy,mode);var clock=new ManualClock();var e=new Experiment(config,clock);experiment=e;
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
            if(legacy)e.LockBlock(Actor.Owner,ControlSelection.Automatic,"synthetic-owner");else e.ConfirmNamedPlan(Actor.Owner,"synthetic-owner");
            for(int block=0;block<4;block++)
            {
                e.PrepareRawRecordingBlockPlan(Actor.Owner,false);
                if(block==0)e.BeginCountdown(Actor.Owner,true);else Check.True(e.TryBeginRetainedBlockCountdown(true));
                clock.Advance(5);e.Tick();Check.Equal(Phase.MainTask,e.Phase);
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
                byte[] previous=new byte[32];long ordinal=0,tasks=0;
                Action<byte[],bool> append=(payload,magic)=>
                {byte[] digest;byte[] framed=legacy?ViveRawSessionJournalCodec.FrameV4(payload,previous,magic,out digest):ViveRawSessionJournalCodec.FrameV3(payload,previous,magic,out digest);
                    output.Write(framed,0,framed.Length);previous=digest;};
                append(legacy?ViveRawSessionJournalCodec.ManifestPayloadV4(manifest):ViveRawSessionJournalCodec.ManifestPayloadV3(manifest),true);
                foreach(var item in e.Events.Skip(1))
                {append(ViveRawSessionJournalCodec.RecordPayload(++ordinal,item,manifest),false);tasks++;}
                append(ViveRawSessionJournalCodec.TerminalPayloadV3(ordinal+1,e.Events.Last().Sequence,tasks,0,0,tasks,clock.Now),false);
                return output.ToArray();
            }
        }
        public static void Register(TestSuite suite)
        {
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
