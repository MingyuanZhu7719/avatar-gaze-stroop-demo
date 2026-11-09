using System;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class Check
    {
        public static void True(bool value, string message="Expected true") { if(!value) throw new Exception(message); }
        public static void Equal<T>(T expected, T actual, string message="")
        { if(!Object.Equals(expected,actual)) throw new Exception(message+" Expected "+expected+", actual "+actual); }
        public static void Near(double expected,double actual,double tolerance=0.000001)
        { if(Double.IsNaN(actual) || Math.Abs(expected-actual)>tolerance) throw new Exception("Expected "+expected+", actual "+actual); }
        public static void Throws<T>(Action action) where T:Exception
        { try { action(); } catch(T) { return; } throw new Exception("Expected "+typeof(T).Name); }
    }
    public sealed class TestSuite
    {
        public int Passed, Failed;
        public void Run(string name, Action test)
        {
            try { test(); Passed++; Console.WriteLine("PASS "+name); }
            catch(Exception error) { Failed++; Console.WriteLine("FAIL "+name+"\n"+error); }
        }
    }
    public sealed class ManualClock : IClock
    {
        public double Now {get;set;}
        public string ClockId {get{return "test-monotonic";}}
        public void Advance(double seconds) {Now+=seconds;}
    }
    public static class Fixtures
    {
        public static Experiment New(ManualClock clock,Condition first=Condition.A, RunKind runKind=RunKind.Simulation)
        {
            string order=first==Condition.A?"ABDC":first==Condition.B?"BCAD":first==Condition.C?"CDBA":"DACB";
            var conditions=Array.ConvertAll(order.ToCharArray(),x=>(Condition)Enum.Parse(typeof(Condition),x.ToString()));
            return new Experiment(new SessionConfiguration("test-session",42,new Allocation("test-allocation",conditions,GazeTarget.Participant,new[]{0,1,2,3}),runKind,"en",1),clock);
        }
        public static void Practice(Experiment e,ManualClock c,int correct)
        {
            if(e.Phase==Phase.Preparation) e.BeginFamiliarization(Actor.Owner);
            e.BeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {
                e.MarkPresented(e.CurrentTrial.TrialId); c.Advance(.4);
                Colour ink=e.CurrentTrial.Stimulus.Ink;
                Colour answer=i<correct?ink:(Colour)(((int)ink+1)%4);
                Check.True(e.ButtonChanged(answer,true)); e.ButtonChanged(answer,false);
                if(e.Phase==Phase.PracticeCorrection) e.ContinuePractice();
            }
        }
        public static void Start(Experiment e,ManualClock c,ControlSelection source=ControlSelection.Automatic)
        {
            e.LockBlock(Actor.Owner,source,"test-owner"); e.BeginCountdown(Actor.Owner,true);
            c.Advance(1); e.Tick(); Check.Equal(Phase.MainTask,e.Phase);
        }
        public static void Answer(Experiment e,ManualClock c,bool correct=true)
        {
            e.MarkPresented(e.CurrentTrial.TrialId); c.Advance(.2);
            var answer=correct?e.CurrentTrial.Stimulus.Ink:(Colour)(((int)e.CurrentTrial.Stimulus.Ink+1)%4);
            Check.True(e.ButtonChanged(answer,true)); e.ButtonChanged(answer,false);
        }
        public static void Ratings(Experiment e,ManualClock c)
        {for(int i=0;i<5;i++){c.Advance(2);e.Rate(i+1);}}
    }
    public static class Program
    {
        public static int Main(string[] args)
        {
            var suite=new TestSuite(); TrialPlanTests.Register(suite); ExperimentTests.Register(suite);
            PreviewFlowTests.Register(suite);
            AppLanguageTests.Register(suite);
            GazeControlTests.Register(suite);
            FaultTransportTests.Register(suite);
            ClockBoundaryTests.Register(suite);
            DataTests.Register(suite);
            RuntimeTests.Register(suite);
            SimulationInMemoryPairTests.Register(suite);
            LifecycleFileTests.Register(suite);
            GeometryTests.Register(suite);
            OwnerCommandTests.Register(suite);
            DesktopPreviewTests.Register(suite);
            QuestPreviewInputTests.Register(suite);
            VivePreviewTrackpadInputTests.Register(suite);
            ViveFixedTrackpadMappingTests.Register(suite);
            ViveStartPoseStabilityTests.Register(suite);
            ViveStartupAlignmentGateTests.Register(suite);
            ViveQuestionnairePresentationTests.Register(suite);
            ViveProEyeCaptureTests.Register(suite);
            ViveProEyeClockGuardTests.Register(suite);
            ViveParticipantEyeSourceTests.Register(suite);
            ViveParticipantRecordingBridgeTests.Register(suite);
            ViveRecordingSessionCoordinatorTests.Register(suite);
            ViveRecordingSinkPortTests.Register(suite);
            ViveDevelopmentProvenanceTests.Register(suite);
            ViveDevelopmentStreamIdentityTests.Register(suite);
            ViveDevelopmentJournalTests.Register(suite);
            ViveDevelopmentJournalExtractionTests.Register(suite);
            ViveDevelopmentAnalysisEntryTests.Register(suite);
            ViveDevelopmentReadOnlyAnalysisControllerTests.Register(suite);
            ViveSessionEvidenceAnalyzerTests.Register(suite);
            ViveSessionEvidenceControllerTests.Register(suite);
            ViveSessionFrameScannerTests.Register(suite);
            ViveSessionJournalAsyncWriterTests.Register(suite);
            ViveSyntheticSessionJournalReaderTests.Register(suite);
            VivePracticeJournalV3Tests.Register(suite);
            VivePracticeConsentAuthorityTests.Register(suite);
            VivePracticePreviewCoordinatorTests.Register(suite);
            VivePracticeWithdrawConsentTests.Register(suite);
            VivePracticeStartBatchTests.Register(suite);
            VivePracticeStartBindingTests.Register(suite);
            VivePracticeStartReservationTests.Register(suite);
            VivePracticeStartEnvelopeTests.Register(suite);
            VivePracticeStartMemoryStageTests.Register(suite);
            ViveExclusiveMemoryStoreTests.Register(suite);
            VivePracticeOwnedMemoryStartTests.Register(suite);
            VivePracticeCursorIntegrationTests.Register(suite);
            VivePracticeTaskEventCursorTests.Register(suite);
            VivePracticeCursorAppendTests.Register(suite);
            VivePracticeMultiBatchTests.Register(suite);
            VivePracticeMultiPreparedTests.Register(suite);
            VivePracticeTerminalFlowTests.Register(suite);
            VivePracticeTerminalValidatorTests.Register(suite);
            VivePracticeTerminalAdversarialTests.Register(suite);
            VivePracticeMultiAttemptTwoTests.Register(suite);
            VivePracticeRetryFlowTests.Register(suite);
            VivePracticeRetryPublicationInterleavingTests.Register(suite);
            VivePracticeJournalCursorContractTests.Register(suite);
            VivePracticeWriteTicketFixtureTests.Register(suite);
            VivePracticeCreateNewFixtureTests.Register(suite);
            VivePracticeNativeTargetTests.Register(suite);
            VivePracticeEyeSourceTests.Register(suite);
            VivePracticeEyeBoundaryTests.Register(suite);
            VivePracticeEyeFencedDifferentialTests.Register(suite);
            VivePracticeEyeSyntheticTicketTests.Register(suite);
            VivePracticeSyntheticEyeJoinTests.Register(suite);
            VivePracticeHeadFenceTests.Register(suite);
            VivePracticeClockMapperBoundaryTests.Register(suite);
            VivePracticeSealedMapperTests.Register(suite);
            ViveDevelopmentJournalSinkTests.Register(suite);
            ViveDevelopmentParticipantTests.Register(suite);
            ViveAutomaticObservationIntegrationTests.Register(suite);
            ViveParticipantPauseSafetyTests.Register(suite);
            ViveStroopSafetyMenuGateTests.Register(suite);
            ViveDevelopmentTransportTests.Register(suite);
            ViveDevelopmentPeerControllerTests.Register(suite);
            ViveDevelopmentAppAdapterTests.Register(suite);
            ViveDevelopmentPreflightTests.Register(suite);
            ViveDevelopmentPreflightWireTests.Register(suite);
            ViveDevelopmentPreflightOrchestrationTests.Register(suite);
            ViveDevelopmentPreflightChannelTests.Register(suite);
            ViveDevelopmentPreflightCoordinatorTests.Register(suite);
            ViveCountdownIntentCandidateTests.Register(suite);
            ViveAuthoritativeIntentCandidateTests.Register(suite);
            ViveMutationCursorCandidateTests.Register(suite);
            VivePersistentTrialVectorCandidateTests.Register(suite);
            VivePersistentTrialMirrorCandidateTests.Register(suite);
            VivePersistentTrialTransactionCandidateTests.Register(suite);
            ViveActualExperimentTrialBindingTests.Register(suite);
            TrialRecordOwnershipTests.Register(suite);
            RatingRecordOwnershipTests.Register(suite);
            ExperimentMutationObservationTests.Register(suite);
            ExperimentIncrementalObservationTests.Register(suite);
            ViveIncrementalAggregatePumpTests.Register(suite);
            ViveSyntheticCommitWorkerTests.Register(suite);
            ViveIsolatedCommitCaptureTests.Register(suite);
            ExperimentPersistentTrialsTests.Register(suite);
            ViveWozPresentationPolicyTests.Register(suite);
            ViveWozConservativeTimingTests.Register(suite);
            ViveWozFrameMapperTests.Register(suite);
            ViveWozReferenceAlignmentTests.Register(suite);
            ViveWozSourceConfirmationTests.Register(suite);
            ViveWozPreflightSamplePumpTests.Register(suite);
            ViveWozAuthoritySeamCandidateTests.Register(suite);
            ViveWozParticipantHostTests.Register(suite);
            ViveWozNamedContextTests.Register(suite);
            ViveLivePresentationTests.Register(suite);
            ViveSimulationTrialPoseTests.Register(suite);
            ViveTlsSimulationCandidateTests.Register(suite);
            ViveAutomaticConditionTests.Register(suite);
            ViveNamedOptionExperimentTests.Register(suite);
            ViveNamedTaskMemoryViewTests.Register(suite);
            ViveNamedRecordingUiAdmissionTests.Register(suite);
            ViveControlledRecordingBuildPolicyTests.Register(suite);
            ViveRawRecordingPresentationTests.Register(suite);
            ViveNamedRecordingTaskStampTests.Register(suite);
            NamedRecordingPlanPreparationTests.Register(suite);
            ViveNamedSessionFrameAdmissionTests.Register(suite);
            ViveNamedRawRecordingPumpTests.Register(suite);
            ViveNamedRawRecordingPumpTests.RegisterSceneInstallBoundaryTests(suite);
            ViveNamedRawSafetyTests.Register(suite);
            ViveTrialTransactionCandidateTests.Register(suite);
            ViveWorkerStatusEvidenceTests.Register(suite);
            VivePreSessionRecordingChoiceTests.Register(suite);
            ViveNamedRecordingSetupTests.Register(suite);
            ViveRawSessionJournalReaderTests.Register(suite);
            ViveRawSessionStreamingTests.Register(suite);
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
            ViveRawPrivateStageTests.Register(suite);
            VivePrivateTargetFactoryRaceTests.Register(suite);
            ViveRawReadableAnalysisCsvTests.Register(suite);
#endif
            ViveRawSessionPageTests.Register(suite);
            ViveRawPreparedPlanTests.Register(suite);
            ViveRawAsynchronousPrefixTests.Register(suite);
            ViveTaskEventWindowCandidateTests.Register(suite);
            ViveExperimentMutationFootprintTests.Register(suite);
            ViveNamedOrdinaryInputBudgetCandidateTests.Register(suite);
            ViveSyntheticBlockTransactionTests.Register(suite);
            ViveRawSessionTerminalTests.Register(suite);
            ViveSameFileReadbackTests.Register(suite);
            ViveRawTrialProjectionTests.Register(suite);
            ViveRawTrialReportTests.Register(suite);
            ViveRawCountdownWitnessTests.Register(suite);
            ViveRawPanelWitnessInputTests.Register(suite);
            ViveRawPanelPolicyTests.Register(suite);
            ViveRawTaskFrameMeasurementTests.Register(suite);
            ViveSyntheticMarkPresentedBudgetTests.Register(suite);
            ViveRawByteBudgetTests.Register(suite);
            ViveRawIoFenceTests.Register(suite);
            ViveRawNativeBindingTests.Register(suite);
            ViveSharedCompletionFenceTests.Register(suite);
            ViveRawCompletionFenceTests.Register(suite);
            ViveSyntheticAdmissionBatchTests.Register(suite);
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            ViveSyntheticParticipantBatchFixtureTests.Register(suite);
            ViveNamedRawRecordingPumpTests.RegisterFrameBoundaryTests(suite);
#endif
            ViveRawRelativeFileCapabilityTests.Register(suite);
            ViveRawDirectoryLeaseTests.Register(suite);
            ViveRawFileIdTests.Register(suite);
            ViveRawSessionEvidenceControllerTests.Register(suite);
            ViveNamedPlanTaskMemoryProjectionTests.Register(suite);
            ViveWozOperatorSourceFactoryTests.Register(suite);
            ViveProEyeWorkerTests.Register(suite);
            VivePreviewDiagnosticsTests.Register(suite);
            VivePreviewMarkerContinuityTests.Register(suite);
            VivePreviewLaunchPlanTests.Register(suite);
            SrAnipalInteropTests.Register(suite);
            JournalTests.Register(suite);
            JournalCaptureTests.Register(suite);
            JournalRuntimeTests.Register(suite);
            RetentionTests.Register(suite);
            PreviewRecoveryTests.Register(suite);
            PreviewRestartWorkflowTests.Register(suite);
            PreviewExportReceiptTests.Register(suite);
            Console.WriteLine("RESULT "+suite.Passed+" passed; "+suite.Failed+" failed");
            return suite.Failed==0?0:1;
        }
    }
}
