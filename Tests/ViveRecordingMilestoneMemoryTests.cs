#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
namespace AvatarStroop.Tests
{
    // Integration evidence for software serialization only. All gaze, time and
    // responses are fabricated. No worker, native handle, path or device opens.
    public static class ViveRecordingMilestoneMemoryTests
    {
        sealed class Clock:IClock {public double Now {get;set;} public string ClockId {get{return "milestone-synthetic-clock";}}}
        sealed class Fixture
        {
            internal Experiment Task; internal byte[] Bytes;
            internal readonly List<Tuple<long,double,string,int>> Eyes=new List<Tuple<long,double,string,int>>();
            internal Fixture()
            {
                var automatic=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"synthetic-geometry");
                var hidden=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"milestone-synthetic-plan",automatic,hidden);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"milestone-synthetic-intent",true,true);
                var clock=new Clock(); Task=new Experiment(SessionConfiguration.NamedRecordingDevelopment("milestone-synthetic-session",73,plan,intent),clock);
                Task.BeginFamiliarization(Actor.Owner);Task.BeginPractice(Actor.Owner);
                Action answer=()=>{Task.MarkPresented(Task.CurrentTrial.TrialId);clock.Now+=.25;var ink=Task.CurrentTrial.Stimulus.Ink;
                    Check.True(Task.ButtonChanged(ink,true));Task.ButtonChanged(ink,false);};
                for(int i=0;i<8;i++)answer();
                Task.ConfirmNamedPlan(Actor.Owner,"synthetic-owner");Task.BeginCountdown(Actor.Owner,true);clock.Now+=3;Task.Tick();
                for(int block=0;block<2;block++)
                {
                    while(Task.Phase==Phase.MainTask)answer();
                    Check.Equal(Phase.Questionnaire,Task.Phase);Check.True(Task.AcknowledgeQuestionnaireInstruction());
                    for(int q=0;q<Questionnaire.Count;q++){clock.Now+=.125;Task.Rate(q+1);}
                    if(block==0){clock.Now+=.375;Check.True(Task.Ready());Check.True(Task.TryBeginRetainedBlockCountdown(true));clock.Now+=3;Task.Tick();}
                }
                Check.Equal(EndReason.Completed,Task.EndReason.Value);
                var manifest=new ViveNamedSessionFrameAdmission.Manifest(Task.Configuration.SessionId,clock.ClockId,plan,intent,true);manifest.SealChoiceTime(0,0);
                using(var output=new MemoryStream())
                {
                    byte[] previous=new byte[32];long ordinal=0,tasks=0,eyes=0;
                    Action<byte[],bool> append=(payload,magic)=>{byte[] digest;var framed=ViveRawSessionJournalCodec.FrameV3(payload,previous,magic,out digest);
                        output.Write(framed,0,framed.Length);previous=digest;};
                    append(ViveRawSessionJournalCodec.ManifestPayloadV3(manifest),true);
                    foreach(var item in Task.Events.Skip(1))
                    {
                        append(ViveRawSessionJournalCodec.RecordPayload(++ordinal,item,manifest),false);tasks++;
                        if(item.Kind!="stimulus_presented")continue;
                        double observed=item.Time+.01;long frame=++eyes;
                        var ray=new ViveEyeRay(3,true,true,new Vec3(1,2,3),new Vec3(0,0,1));
                        var poll=new ViveProEyePoll(new ViveHeadFrame(frame,observed,true,true,Rotation.Identity),
                            new ViveEyeFrame((int)frame,(int)(1000+frame),observed,false,ray,ray,ray),
                            ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
                        var stamp=new ViveDevelopmentParticipant.NamedRecordingTaskStamp(null,null,plan,intent,1,item.Sequence,manifest.Session,
                            item.BlockIndex<0?Phase.Practice:Phase.MainTask,item.BlockIndex,item.Segment,item.TrialId);
                        append(ViveRawSessionJournalCodec.RecordPayload(++ordinal,new ViveNamedSessionFrameAdmission.Frame(poll,stamp,0,observed+.001),manifest),false);
                        Eyes.Add(Tuple.Create(frame,observed,item.TrialId,item.BlockIndex));
                    }
                    append(ViveRawSessionJournalCodec.TerminalPayloadV3(ordinal+1,Task.Events.Last().Sequence,tasks,eyes,0,ordinal,clock.Now),false);
                    Bytes=output.ToArray();
                }
            }
        }
        static ViveRawSessionReadResult Project(byte[] bytes)
        {using(var stream=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.ProjectTrials(stream,true,true,4194304,5000,CancellationToken.None);}
        static string Bits(double value){return BitConverter.DoubleToInt64Bits(value).ToString("X16",CultureInfo.InvariantCulture);}
        // Independent CSV parser handles quoting, doubled quotes and newlines.
        static List<string[]> Parse(string csv)
        {
            var rows=new List<string[]>();var fields=new List<string>();var cell=new StringBuilder();bool quoted=false;
            for(int i=0;i<csv.Length;i++)
            {
                char c=csv[i];if(c=='"'){if(quoted&&i+1<csv.Length&&csv[i+1]=='"'){cell.Append('"');i++;}else quoted=!quoted;}
                else if(!quoted&&(c==','||c=='\n')){fields.Add(cell.ToString());cell.Clear();if(c=='\n'){rows.Add(fields.ToArray());fields.Clear();}}
                else cell.Append(c);
            }
            Check.True(!quoted&&fields.Count==0&&cell.Length==0);return rows;
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("milestone two-mode memory capture preserves every trial rating and clock",()=>{
                var f=new Fixture();var result=Project(f.Bytes);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                Check.True(result.CaptureTerminalObserved);Check.Equal(56L,result.VerifiedObservationRecords);
                var p=result.TrialProjection;Check.Equal(56,p.Trials.Count);Check.Equal(10,p.Ratings.Count);
                Check.Equal("Automatic",p.DeclaredPlanBlocks[0].Option);Check.Equal("NoAvatar",p.DeclaredPlanBlocks[1].Option);
                Check.Equal(24,f.Eyes.Count(x=>x.Item4==1)); // No Avatar still retains participant gaze.
                foreach(var row in p.Trials)
                {
                    var trial=f.Task.Trials.Single(t=>t.TrialId==row.TrialId);
                    Check.True(!row.EventConflict&&!row.IncompleteEvidence);Check.Equal(1,row.ResponseEvents);
                    Check.Equal(trial.Onset,row.PresentationEventLocalSeconds);Check.Equal(trial.ResponseAt,row.ResponseEventLocalSeconds);
                    Check.Equal(trial.ReactionSeconds.Value,Double.Parse(row.DeclaredReactionSeconds,CultureInfo.InvariantCulture));
                    Check.Equal(trial.Stimulus.Ink.ToString(),row.Ink);Check.Equal(trial.Response.Value.ToString(),row.Response);
                }
                for(int i=0;i<p.Ratings.Count;i++){var actual=p.Ratings[i];var source=f.Task.Ratings[i];Check.Equal(source.Time,actual.EventLocalSeconds);
                    Check.Equal(source.ItemId,actual.Item);Check.Equal(source.Value.ToString(CultureInfo.InvariantCulture),actual.DeclaredValue);Check.Equal(source.BlockIndex,actual.Block);}
                Check.Equal("milestone-synthetic-clock",p.DeclaredClockId);
            });
            suite.Run("milestone readable CSV preserves each synthetic gaze timestamp and ingest context",()=>{
                var f=new Fixture();using(var input=new MemoryStream(f.Bytes,false))using(var output=new MemoryStream())
                {
                    Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,CancellationToken.None));
                    string text=Encoding.UTF8.GetString(output.ToArray());var rows=Parse(text);var header=rows[0];
                    int kind=Array.IndexOf(header,"record_kind"),timestamp=Array.IndexOf(header,"eye_native_timestamp"),observed=Array.IndexOf(header,"eye_eye_observed_raw"),
                        trial=Array.IndexOf(header,"eye_ingest_trial_id"),block=Array.IndexOf(header,"eye_ingest_block");
                    Check.True(kind>=0&&timestamp>=0&&observed>=0&&trial>=0&&block>=0);
                    var eyeRows=rows.Skip(1).Where(r=>r[kind]=="observation").ToList();Check.Equal(56,eyeRows.Count);
                    for(int i=0;i<eyeRows.Count;i++){var row=eyeRows[i];var eye=f.Eyes[i];Check.Equal(header.Length,row.Length);
                        Check.Equal((1000+eye.Item1).ToString(CultureInfo.InvariantCulture),row[timestamp]);Check.Equal("raw:f64_bits_hex:"+Bits(eye.Item2),row[observed]);
                        Check.Equal(eye.Item3,row[trial]);Check.Equal(eye.Item4.ToString(CultureInfo.InvariantCulture),row[block]);}
                    Check.True(text.Contains("NATIVE_TIME_OPAQUE")&&text.Contains("INGEST_NOT_SAMPLE_ALIGNMENT")&&text.Contains("SYNTHETIC_ONLY"));
                }
            });
            suite.Run("milestone rejects altered bytes and does not promote a truncated terminal",()=>{
                var f=new Fixture();var corrupt=(byte[])f.Bytes.Clone();corrupt[corrupt.Length-1]^=1;var rejected=Project(corrupt);
                Check.Equal(ViveRawSessionReadStatus.Invalid,rejected.Status);Check.True(!rejected.CaptureTerminalObserved);
                var partial=(byte[])f.Bytes.Clone();Array.Resize(ref partial,partial.Length-1);var prefix=Project(partial);
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,prefix.Status);Check.True(prefix.TrailingFrameTruncated&&!prefix.CaptureTerminalObserved&&!prefix.CompleteSessionProven);
            });
            suite.Run("milestone memory success cannot open installed gates or imply durable real data",()=>{
                var result=Project(new Fixture().Bytes);Check.True(!result.SourceAuthenticated&&!result.ConsentAuthenticated&&!result.CompleteSessionProven&&!result.DurableStorageProven);
                Check.True(!result.TrialProjection.SamplesJoinedToTrials&&!ViveNamedRecordingUiAdmission.RecordingAvailable&&!ViveRawRelativeFileCapability.ProductionGateOpen&&!ViveRawReadableAnalysisCsv.ProductionGateOpen);
                Check.Equal("UNKNOWN",result.TrialProjection.WriterConfirmation);Check.Equal("UNKNOWN",result.TrialProjection.Coverage);
            });
        }
    }
}
#endif

