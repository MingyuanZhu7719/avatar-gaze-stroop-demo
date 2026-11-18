#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static partial class ViveFormalEyeRecordingTests
    {
        // Run only from an isolated Windows fixture build with its synthetic
        // sink root fixed to a new empty directory. The default root is closed.
        public static void RunNativeFourBlockSession()
        {
            using(var run=new NativeFourBlockSession())run.Complete();
            string directory=ViveRawSessionJournalSink.LegacyIntegrationFixtureDirectory(
                ViveRawSessionJournalSink.IntegrationFixtureScenario.Success);
            var raw=VerifySessionFile(Directory.GetFiles(directory,"vraw-*.vraw").Single());
            var csv=VerifySessionFile(Directory.GetFiles(directory,"*.csv").Single());
            Check.Equal(raw.VerifiedChainSha256,csv.VerifiedChainSha256);
            Check.Equal(raw.RecordCount,csv.RecordCount);
        }

        public static void Main()
        {
            RunNativeFourBlockSession();
            Console.WriteLine("Passed native four-block recording, close and VRAW/CSV readback.");
        }

        static StudyDataReadSummary VerifySessionFile(string path)
        {
            var analysis=new StudyAnalysis();
            double firstEyeTime=Double.PositiveInfinity;
            StudyDataReadSummary read;
            using(var stream=File.OpenRead(path))
                read=StudyDataReader.Read(stream,Path.GetExtension(path),record=>{
                    analysis.Accept(record);
                    if(record.Kind=="eye_acquisition"||record.Kind=="observation")
                    {
                        var eyeTime=StudyAnalysis.Number(record.Fields,"eye_observed");
                        Check.True(eyeTime.HasValue);
                        firstEyeTime=Math.Min(firstEyeTime,eyeTime.Value);
                    }
                },CancellationToken.None);
            Check.Equal("Complete",read.Status,read.Message);
            Check.True(read.SourceTerminalObserved);
            if(Path.GetExtension(path)==".vraw")Check.True(read.ChainVerified);
            analysis.Finish();
            Check.Equal("Completed",analysis.SessionEndReason);
            Check.Equal(4,analysis.EndedBlocks);
            Check.Equal(1,analysis.Trials.Count(t=>t.Scope=="Tutorial"&&t.AccuracyIncluded));
            var exam=analysis.TrainingAttempts.Single();
            Check.True(exam.Passed==true&&exam.Planned==10&&exam.ReconstructedCorrect==10);
            Check.True(analysis.FormalEyeWindowOpenEvents==1&&analysis.FormalEyeWindowOpenTime.HasValue);
            Check.True(!analysis.UnexpectedPreformalEyeRows&&firstEyeTime>=analysis.FormalEyeWindowOpenTime.Value);
            Check.True(analysis.EyeGroups.Sum(g=>g.AcquisitionRows)>0);
            for(int i=0;i<4;i++)
            {
                Check.Equal(24,analysis.Trials.Count(t=>t.Scope=="Formal"&&t.Block==i&&t.AccuracyIncluded));
                Check.Equal(5,analysis.Ratings.Count(r=>r.Block==i));
                Check.True(analysis.EyeGroups.Any(g=>g.Phase=="MainTask"&&g.Block==i&&g.Observations>0));
            }
            return read;
        }

        sealed class NativeFourBlockSession : IDisposable
        {
            readonly MonotonicClock clock=new MonotonicClock("synthetic-formal-native");
            readonly Client native=new Client(90000);
            readonly ViveProEyeWorker worker;
            readonly ViveDevelopmentParticipant task;
            readonly ViveNamedSessionFrameAdmission admission;
            readonly ViveRawSessionJournalSink sink;
            readonly ViveNamedRawRecordingPump pump;
            int nativeFrame,renderFrame;
            long headSequence;
            bool nativeRequested;

            internal NativeFourBlockSession()
            {
                var geometry=DevelopmentFixtures.Geometry();
                var allocation=new Allocation("native-test",new[]{Condition.B,Condition.C,Condition.A,Condition.D},
                    GazeTarget.Participant,new[]{0,1,2,3});
                var design=ViveLegacyRecordingDesign.Select(Actor.Owner,"formal-native-test",allocation,geometry.Version,ViveFixedTrackpadMapping.Version);
                var config=SessionConfiguration.LegacyRecordingDevelopment(
                    new SessionConfiguration("synthetic-hardware-free",73,allocation,RunKind.Simulation,"en",5,30,true,0,true,true,true),
                    design,ViveLegacyRecordingIntent.Select(Actor.Owner,"formal-native-test-intent",true,true));
                config=SessionConfiguration.WithFormalEyeRecordingOnly(SessionConfiguration.WithTrainingProtocol(config,TrainingProtocol.TutorialExamV1));
                worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++headSequence,clock.Now,true,true,Rotation.Identity),()=>native);
                Check.True(worker.StartTaskOnly());
                task=new ViveDevelopmentParticipant(config,clock,geometry,ViveFixedTrackpadMapping.Create(),.5,true);
                admission=ViveNamedSessionFrameAdmission.CreateLegacySyntheticForTest(task,worker,clock,256,0,0);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                sink=ViveRawSessionJournalSink.CreateLegacyIntegrationFixtureForTest(admission,ViveRawByteBudget.SelectExplicit(128*1024*1024),
                    ViveRawSessionJournalSink.IntegrationFixtureScenario.Success);
                ViveRawExclusiveDestination.CreateIntegrationFixtureForTest(admission,sink,task.NamedRawRevocation);
                pump=ViveNamedRawRecordingPump.StartLegacyNativeFixtureForTest(task,worker,clock,admission,sink,true);
            }
            void Stage()
            {
                if(nativeRequested)native.Send(Eye(clock.Now,++nativeFrame));
                Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus(),pump.FixtureDiagnosticsForTest());
                ViveProEyePoll frame;Check.True(worker.TryPollTaskForRecording(out frame));
                bool waiting;
                Check.True(pump.StageRigFrame(frame,1,false,out waiting)&&!waiting,pump.FixtureDiagnosticsForTest());
            }
            void Mutate(Action action)
            {
                Stage();Check.True(pump.TryRunReservedOrdinaryMutation(action),pump.FixtureDiagnosticsForTest());
            }
            void Neutral()
            {Mutate(()=>task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));}
            void Answer()
            {
                var trial=task.CurrentTrial;Neutral();Stage();
                ViveNamedSessionFrameAdmission.PresentationReservation token;
                int frame=++renderFrame;
                Check.True(pump.TryPreparePresentation(frame,frame,out token),pump.FixtureDiagnosticsForTest());
                Check.True(pump.CommitPresentation(token,frame,frame,trial.TrialId,trial.Stimulus.Word,trial.Stimulus.Ink,clock.Now),pump.FixtureDiagnosticsForTest());
                var colour=trial.ExpectedResponse;
                double x=colour==Colour.Green?.9:colour==Colour.Yellow?-.9:0,y=colour==Colour.Red?.9:colour==Colour.Blue?-.9:0;
                Mutate(()=>task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y)));Neutral();
            }
            void Begin(bool practice)
            {
                JournalEvent plan=null;Mutate(()=>plan=task.PrepareLegacyRecordingBlockPlan(Actor.Owner,practice));
                ViveBlockPlanRequest request;Check.True(ViveBlockPlanRequest.TryCapture(task,clock,plan,out request));
                Check.True(pump.DrainTaskEvents());ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt=null;
                Check.True(SpinWait.SpinUntil(()=>{
                    var state=pump.TryGetBlockPlanReceipt(request,out receipt);
                    Check.True(state!=ViveRawSessionJournalSink.BlockPlanReceiptPoll.Faulted,pump.FixtureDiagnosticsForTest());
                    return state==ViveRawSessionJournalSink.BlockPlanReceiptPoll.Ready;
                },10000),"timed out waiting for a Windows plan write and flush");
                Check.True(receipt.BelongsToNativeFixtureForTest(sink));
                bool began=false;Mutate(()=>began=task.TryBeginNativeFixtureWithPlan(admission,sink,request,receipt,practice));
                Check.True(began&&pump.DrainTaskEvents());
            }
            void Confirm()
            {
                Mutate(()=>task.UpdateAuxiliary(false,false,false,0,true));
                Mutate(()=>task.UpdateAuxiliary(true,false,false,0,true));
                if(!task.Completed)Mutate(()=>task.UpdateAuxiliary(false,false,false,0,true));
            }
            void RunBlock(int block)
            {
                Check.Equal(Phase.Countdown,task.Phase);Stage();
                Check.True(worker.FormalEyePersistenceEnabled);
                int frame=++renderFrame;
                double start=clock.Now;
                Check.True(task.TryStartLegacyRawCountdownFromSoftwareWitness(frame,frame,start));
                Check.True(pump.DrainTaskEvents());
                while(task.Phase==Phase.Countdown&&clock.Now<start+10){Thread.Sleep(30);Mutate(()=>task.Tick());}
                Check.Equal(Phase.MainTask,task.Phase);
                Check.True(clock.Now-start>=5,"formal countdown ended before five seconds");
                for(int i=0;i<24;i++)Answer();
                Check.Equal(Phase.Questionnaire,task.Phase);Confirm();
                for(int i=0;i<Questionnaire.Count;i++)Confirm();
                if(block<3){Check.Equal(Phase.Rest,task.Phase);Confirm();Check.Equal(Phase.BlockReady,task.Phase);}
            }
            internal void Complete()
            {
                Mutate(()=>task.RecordLegacySoftwareChoices(clock,0,0));Neutral();Mutate(()=>task.OwnerBeginTutorial(Actor.Owner));
                Answer();Mutate(task.RequestPracticeExam);Begin(true);
                for(int i=0;i<10;i++)Answer();
                Check.Equal(Phase.PracticeExamResult,task.Phase);Check.Equal(10,task.PracticeCorrect);
                Check.True(!worker.FormalEyePersistenceEnabled&&!task.FormalEyeRecordingWindowStart.HasValue);
                Mutate(task.ContinuePracticeExamResult);Mutate(()=>task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"fixture-owner"));
                Check.True(worker.RequestFormalEyeStart());nativeRequested=true;Check.True(native.Waiting.WaitOne(3000));
                double deadline=clock.Now+5;
                while(!worker.ReadRecordingStartReadiness().Ready&&clock.Now<deadline){Thread.Sleep(20);native.Send(Eye(clock.Now,++nativeFrame));}
                Check.True(worker.ReadRecordingStartReadiness().Ready&&!worker.FormalEyePersistenceEnabled);
                for(int i=0;i<4;i++){Begin(false);RunBlock(i);}
                Check.True(task.Completed&&task.RatingCount==20);
                Check.True(pump.DrainTaskEvents()&&pump.RequestCompletedDrain(),pump.FixtureDiagnosticsForTest());
                Check.True(SpinWait.SpinUntil(()=>{
                    if(pump.HasExited)return true;
                    Check.True(pump.PollRequestedCompletionDrain(),pump.FixtureDiagnosticsForTest());return false;
                },60000),pump.FixtureDiagnosticsForTest());
                Check.Equal(ViveNamedRawRecordingPump.Outcome.CloseCallsReturned,pump.State,pump.FixtureDiagnosticsForTest());
                Check.True(sink.SourceCloseReturned&&sink.CompletionState==7);
            }
            public void Dispose()
            {
                if(!pump.HasExited)pump.Stop();SpinWait.SpinUntil(()=>pump.HasExited,3000);
                worker.TryRequestStop();if(nativeRequested)native.Send(null,false);
                worker.Stop(1000);worker.Dispose();native.Waiting.Dispose();native.Permit.Dispose();
            }
        }
    }
}
#endif
