#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Memory-only state classification. No native writer, directory, target,
    // stage factory or installed completion method is run. Reflection creates
    // metadata only and invokes a non-I/O failure helper while all gates stay
    // closed. These tests do not prove actual native cleanup or installation.
    public static class ViveInstalledOutputFailureTests
    {
        const BindingFlags PrivateInstance=BindingFlags.Instance|BindingFlags.NonPublic;
        // A metadata-only placeholder derived from the current volume; never opened.
        static string UnusedSyntheticDirectory {get{return System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.CurrentDirectory),"synthetic-unused-recording");}}
        sealed class WaitingClient:IViveProEyeClient
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "synthetic output-failure classification";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Entered.Set();Release.WaitOne();return null;}
            public void Dispose(){}
        }
        sealed class Fixture:IDisposable
        {
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveNamedSessionFrameAdmission Admission;
            internal readonly ViveRawSessionJournalSink Sink;
            readonly WaitingClient client;
            readonly ViveProEyeWorker worker;
            internal Fixture()
            {
                var clock=new MonotonicClock("output-failure-memory");
                client=new WaitingClient();
                worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                Check.True(worker.Start()&&client.Entered.WaitOne(3000));
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"output-failure-geometry");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"output-failure-plan",option);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"output-failure-intent",true,true);
                var config=SessionConfiguration.NamedRecordingDevelopment("output-failure-session",37,plan,intent);
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),
                    new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"output-failure-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},
                    {ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
                Admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(Task,worker,clock,32);
                Sink=new ViveRawSessionJournalSink(Admission,UnusedSyntheticDirectory,
                    ViveRawByteBudget.SelectExplicit(1048576),true);
            }
            internal ViveRawExclusiveDestination AttachMetadata(bool csv,bool startFailed)
            {
                var types=new List<Type>{typeof(ViveNamedSessionFrameAdmission),typeof(ViveRawSessionJournalSink),
                    typeof(ViveRawRevocation),typeof(long),typeof(int),typeof(string)};
                var values=new List<object>{Admission,Sink,Task.NamedRawRevocation,1048576L,1000,UnusedSyntheticDirectory};
                if(csv){types.Add(typeof(long));types.Add(typeof(int));values.Add(1048576L);values.Add(1000);}
                var constructor=typeof(ViveRawExclusiveDestination).GetConstructor(PrivateInstance,null,types.ToArray(),null);
                Check.True(constructor!=null);
                var destination=(ViveRawExclusiveDestination)constructor.Invoke(values.ToArray());
                Set("installedDestination",destination);Set("installedStageStartFailed",startFailed);
                return destination;
            }
            internal void Set(string name,object value)
            {
                var field=typeof(ViveRawSessionJournalSink).GetField(name,PrivateInstance);
                Check.True(field!=null);field.SetValue(Sink,value);
            }
            internal bool Reject()
            {
                var method=typeof(ViveRawSessionJournalSink).GetMethod("RejectFailedInstalledOutputStartup",PrivateInstance);
                Check.True(method!=null);return (bool)method.Invoke(Sink,null);
            }
            internal void CheckNoIo()
            {
                foreach(string name in new[]{"file","capability","directoryLease","installedPrivateStage"})
                    Check.True(typeof(ViveRawSessionJournalSink).GetField(name,PrivateInstance).GetValue(Sink)==null);
                Check.Equal(0,(int)typeof(ViveRawSessionJournalSink).GetField("openAttempted",PrivateInstance).GetValue(Sink));
                Check.True(!Sink.ProductionGateOpen&&!Sink.CompletionGateOpen&&!ViveRawRelativeFileCapability.ProductionGateOpen);
                Check.True(!ViveRawExclusiveDestination.ProductionGateOpen&&!ViveRawReadableAnalysisCsv.ProductionGateOpen);
                Check.True(!ViveNamedRecordingUiAdmission.RecordingAvailable&&!Sink.SourceCloseReturned);
            }
            public void Dispose(){worker.TryRequestStop();client.Release.Set();worker.Stop(1000);worker.Dispose();}
        }
        static void FailedRequest(bool csv,bool withdrawn)
        {
            using(var fixture=new Fixture())
            {
                var destination=fixture.AttachMetadata(csv,true);
                Check.Equal(csv,destination.CsvRequested);
                if(withdrawn)fixture.Task.ParticipantWithdraw();
                Check.True(fixture.Reject());
                Check.Equal(5,fixture.Sink.CompletionState);
                Check.Equal(ViveRawExclusiveDestination.Outcome.Unknown,destination.State);
                Check.True(fixture.Admission.IoFence.IsRevoked&&!fixture.Task.NamedRawRevocation.MayReserve);
                Check.Equal(withdrawn?2:3,fixture.Admission.State);
                Check.Equal(withdrawn,fixture.Task.NamedRawRevocation.ParticipantWithdrawalRequested);
                ViveRawExclusiveDestination.LocalCandidateReceipt receipt;
                Check.True(!destination.TryGetInstalledLocalCandidate(true,out receipt)&&receipt==null);
                Check.True(fixture.Reject());Check.Equal(5,fixture.Sink.CompletionState);
                fixture.CheckNoIo();
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("requested raw output startup failure remains technical failure in memory",()=>FailedRequest(false,false));
            suite.Run("requested raw and CSV startup failure remains technical failure in memory",()=>FailedRequest(true,false));
            suite.Run("output startup failure preserves explicit participant withdrawal in memory",()=>FailedRequest(true,true));
            suite.Run("no requested output is not reclassified by failure helper",()=>{
                using(var fixture=new Fixture())
                {
                    fixture.Set("installedStageStartFailed",true);
                    Check.True(!fixture.Reject()&&!fixture.Admission.IoFence.IsRevoked);
                    Check.Equal(0,fixture.Sink.CompletionState);fixture.CheckNoIo();
                }
            });
            suite.Run("failure helper cannot claim successful output or completion",()=>{
                using(var fixture=new Fixture())
                {
                    var destination=fixture.AttachMetadata(true,false);
                    Check.True(!fixture.Reject()&&!fixture.Admission.IoFence.IsRevoked);
                    Check.Equal(0,fixture.Sink.CompletionState);
                    Check.Equal(ViveRawExclusiveDestination.Outcome.NotStarted,destination.State);
                    fixture.CheckNoIo();
                }
            });
        }
    }
}
#endif

