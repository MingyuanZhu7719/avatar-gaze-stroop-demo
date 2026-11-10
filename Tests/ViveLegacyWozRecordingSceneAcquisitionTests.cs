#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Unity;

namespace AvatarStroop.Tests
{
    internal static class ViveLegacyWozRecordingSceneAcquisitionTests
    {
        static ViveLegacyWozRawSceneAcquisition.State State(ViveLegacyWozRecordingPumpTests.Fixture f,bool started=true)
        {return ViveLegacyWozRawSceneAcquisition.Evaluate(f.Task,f.Scene,started);}
        static void Waiting(ViveLegacyWozRecordingPumpTests.Fixture f,bool started=true)
        {Check.Equal(ViveLegacyWozRawSceneAcquisition.State.Waiting,State(f,started));Check.True(!f.P.Terminal&&f.Recording.Current&&f.Sink.Output==null);}
        static void Ready(ViveLegacyWozRecordingPumpTests.Fixture f)
        {Check.Equal(ViveLegacyWozRawSceneAcquisition.State.Ready,State(f));Check.True(!f.P.Terminal&&f.Recording.Current);}
        static int Main()
        {
            var suite=new TestSuite();
            suite.Run("Recorded dormant offer waits masked without task input or raw failure",()=>{
                using(var f=new ViveLegacyWozRecordingPumpTests.Fixture())
                {var phase=f.Task.Phase;Waiting(f,false);Waiting(f,false);Check.Equal(phase,f.Task.Phase);Check.Equal(0,f.Writer.CompleteCalls);}
            });
            suite.Run("Actual countdown trial questionnaire rest and retained-block mutations wait before render without false stop",()=>{
                using(var f=new ViveLegacyWozRecordingPumpTests.Fixture())
                {
                    f.BeginMain();Check.Equal(Phase.MainTask,f.Task.Phase);Waiting(f);
                    for(int i=0;i<24;i++)
                    {
                        f.Grant();f.Preflight();f.Pose();Ready(f);f.Answer();Waiting(f);
                    }
                    Check.Equal(Phase.Questionnaire,f.Task.Phase);f.Grant();Ready(f);f.Confirm();
                    for(int i=0;i<Questionnaire.Count;i++)f.Confirm();
                    Check.Equal(Phase.Rest,f.Task.Phase);Waiting(f);f.Grant();Ready(f);f.Confirm();
                    Check.Equal(Phase.BlockReady,f.Task.Phase);Waiting(f);f.Grant();f.Preflight();Ready(f);
                    Check.Equal(0,f.Writer.CompleteCalls);
                }
            });
            suite.Run("Already presented recorded trial cannot regain display after proof-age gap",()=>{
                using(var f=new ViveLegacyWozRecordingPumpTests.Fixture())
                {var token=f.ReserveFirstMain();f.Scene.Present();Check.True(f.Commit(token));f.Scene.NotePresented(token.Trial.TrialId);
                    Check.True(token.Trial.Onset.HasValue);Thread.Sleep(180);
                    Check.Equal(ViveLegacyWozRawSceneAcquisition.State.Stopped,State(f));Check.Equal(Phase.Paused,f.Task.Phase);
                    Check.True(!f.Recording.Current&&f.P.Terminal&&f.Sink.Output==null&&f.Writer.CompleteCalls==0);}
            });
            Console.WriteLine("RESULT recorded scene acquisition "+suite.Passed+" passed; "+suite.Failed+" failed.");return suite.Failed==0?0:1;
        }
    }
}
#endif
