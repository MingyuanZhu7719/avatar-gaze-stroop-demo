#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Pure synthetic bytes. These tests establish neither hardware rate nor AOI accuracy.
    public static class ViveRawEyeEvidenceTests
    {
        static ViveNamedSessionFrameAdmission.Manifest Manifest()
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"geometry-fixture");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"plan-fixture",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"intent-fixture",true,true);
            var value=new ViveNamedSessionFrameAdmission.Manifest("session-fixture","clock-fixture",plan,intent,true);value.SealChoiceTime(.5,11);return value;
        }
        static ViveRawEyeAcquisition Acquisition(bool badTime=false)
        {
            var diagnostics=new ViveNativePollDiagnostics(20,.6,.66,.61,.62,.63,.64,badTime ? .7 : .65,0,0,true,0);
            var ray=new ViveEyeRay(0,false,false,new Vec3(Double.NaN,0,0),new Vec3(0,0,0));
            var poll=new ViveProEyePoll(null,new ViveEyeFrame(42,9000,.65,false,ray,null,ray),ViveEyeRuntimeState.Working,
                ViveEyeCalibrationState.Required,true,0,2,diagnostics);
            return new ViveRawEyeAcquisition(0,.67,poll,3,2,0,0);
        }
        static ViveEyeAoiObservation Aoi(int nativeFrame=43,bool missing=false)
        {
            var zero=new Vec3(0,0,0);var rotation=new Rotation(0,0,0,1);var nan=new Vec3(Double.NaN,Double.NaN,Double.NaN);
            return new ViveEyeAoiObservation(.9,.91,4,123,missing?(double?)null:.85,missing?(double?)null:.86,missing?(double?)null:3.99,missing?-1:123,
                missing?-1:5,missing?-1:7,122,3.98,4,6,Phase.MainTask,0,"scene-trial",nativeFrame,9001,.75,
                missing?"invalid":"task",missing?"scene_unavailable":"hit_task",!missing,false,!missing,!missing,
                missing?nan:zero,rotation,missing?nan:zero,missing?nan:new Vec3(0,0,1),
                new[]{new ViveEyeAoiGeometry("task",!missing,missing?nan:zero,rotation,missing?nan:new Vec3(1,1,1))});
        }
        static ViveNamedSessionFrameAdmission.Frame Frame(ViveNamedSessionFrameAdmission.Manifest manifest,ViveEyeAoiObservation aoi,ViveNativePollDiagnostics diagnostics=null)
        {
            var ray=new ViveEyeRay(3,true,true,new Vec3(0,0,0),new Vec3(0,0,1));
            var poll=new ViveProEyePoll(new ViveHeadFrame(3,.8,true,true,new Rotation(0,0,0,1)),
                new ViveEyeFrame(43,9001,.75,false,ray,ray,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2,diagnostics);
            var stamp=new ViveDevelopmentParticipant.NamedRecordingTaskStamp(null,null,manifest.Plan,manifest.Intent,1,11,manifest.Session,Phase.Countdown,0,0,"ingest-trial");
            return new ViveNamedSessionFrameAdmission.Frame(poll,stamp,0,1,aoi);
        }
        static List<byte[]> Payloads(byte[] bytes)
        {
            var values=new List<byte[]>();for(int p=8;p<bytes.Length;){int size=ContinuousJournal.Int(bytes,p);var value=new byte[size];Buffer.BlockCopy(bytes,p+8,value,0,size);values.Add(value);p+=40+size;}return values;
        }
        static byte[] Encode(List<byte[]> payloads)
        {
            using(var output=new MemoryStream())
            {
                byte[] previous=new byte[32];for(int i=0;i<payloads.Count;i++)
                {byte[] payload=payloads[i];Buffer.BlockCopy(BitConverter.GetBytes((long)i),0,payload,0,8);byte[] digest,frame=ViveRawSessionJournalCodec.FrameV3(payload,previous,i==0,out digest);output.Write(frame,0,frame.Length);previous=digest;}
                return output.ToArray();
            }
        }
        static byte[] Fixture(bool withAoi=true,bool missingAoi=false,Action<List<byte[]>> change=null)
        {
            var old=Payloads(ViveRawSessionTerminalTests.Fixture());var m=Manifest();var frames=new List<byte[]>{old[0],
                ViveRawSessionJournalCodec.AcquisitionPayload(1,Acquisition()),
                ViveRawSessionJournalCodec.RecordPayload(2,Frame(m,withAoi?Aoi(missing:missingAoi):null),m),old[1],old[2],
                ViveRawSessionJournalCodec.TerminalPayloadV3(5,13,2,1,0,4,2,0,1)};
            if(change!=null)change(frames);return Encode(frames);
        }
        static ViveRawSessionReadResult Scan(byte[] data)
        {using(var input=new MemoryStream(data,false))return ViveRawSessionJournalReader.Scan(input,true,true,4194304,1000,CancellationToken.None);}
        static ViveNativePollDiagnostics CallbackDiagnostics(double healthEnd=.64)
        {return new ViveNativePollDiagnostics(20,.82,.83,.61,.62,.63,null,null,0,0,false,null,true,.75,.6,healthEnd,0);}
        static ViveRawEyeAcquisition CallbackAcquisition(double healthEnd=.64)
        {
            var ray=new ViveEyeRay(3,true,true,new Vec3(0,0,0),new Vec3(0,0,1));
            var poll=new ViveProEyePoll(null,new ViveEyeFrame(43,9001,.75,false,ray,ray,ray),ViveEyeRuntimeState.Working,
                ViveEyeCalibrationState.ReportedReady,true,0,2,CallbackDiagnostics(healthEnd));
            return new ViveRawEyeAcquisition(0,.84,poll,1,0,0,0);
        }
        // Build historical byte variants without asking the new writer to emit
        // old semantics. Prefix lengths and the complete chain are rebuilt.
        static byte[] ReplaceText(byte[] input,string before,string after,bool remove=false)
        {
            byte[] needle=Encoding.UTF8.GetBytes(before),replacement=Encoding.UTF8.GetBytes(after);int found=-1;
            for(int at=4;at<=input.Length-needle.Length;at++)
            {
                if(BitConverter.ToInt32(input,at-4)!=needle.Length)continue;bool match=true;
                for(int i=0;i<needle.Length;i++)if(input[at+i]!=needle[i]){match=false;break;}
                if(match){Check.Equal(-1,found);found=at-4;}
            }
            Check.True(found>=0);using(var output=new MemoryStream())
            {
                output.Write(input,0,found);if(!remove){byte[] length=BitConverter.GetBytes(replacement.Length);output.Write(length,0,4);output.Write(replacement,0,replacement.Length);}
                int tail=found+4+needle.Length;output.Write(input,tail,input.Length-tail);return output.ToArray();
            }
        }
        static List<string> Cells(string text)
        {
            var values=new List<string>();var cell=new StringBuilder();bool quoted=false;
            for(int i=0;i<text.Length;i++){char c=text[i];if(c=='"'){if(quoted&&i+1<text.Length&&text[i+1]=='"'){cell.Append('"');i++;}else quoted=!quoted;}else if(c==','&&!quoted){values.Add(cell.ToString());cell.Length=0;}else cell.Append(c);}
            Check.True(!quoted);values.Add(cell.ToString());return values;
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("native acquisition retains invalid rays and raw calibration while terminal counts every stream",()=>{
                var bytes=Fixture();var result=Scan(bytes);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                Check.True(result.CaptureTerminalObserved);Check.Equal(1L,result.VerifiedAcquisitionRecords);Check.Equal(1L,result.VerifiedObservationRecords);Check.Equal(2L,result.VerifiedTaskRecords);
                using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {
                    Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,1000,33554432,CancellationToken.None));
                    string[] lines=Encoding.UTF8.GetString(output.ToArray()).Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);var header=Cells(lines[0]);int native=0,aoi=0;
                    foreach(string line in lines)
                    {
                        var row=Cells(line);Check.Equal(header.Count,row.Count);
                        if(row[1]=="eye_acquisition")
                        {native++;Check.Equal("42",row[header.IndexOf("eye_native_frame")]);Check.Equal("0",row[header.IndexOf("eye_combined_mask")]);
                            Check.Equal("true",row[header.IndexOf("eye_calibration_needed")]);Check.Equal("3",row[header.IndexOf("eye_eligible_polls")]);
                            Check.Equal("2",row[header.IndexOf("eye_duplicate_polls")]);Check.Equal("0.64",row[header.IndexOf("eye_read_started")]);
                            Check.Equal(String.Empty,row[header.IndexOf("eye_head_observed")]);Check.Equal(String.Empty,row[header.IndexOf("eye_aoi_category")]);}
                        if(row[1]=="observation")
                        {aoi++;Check.Equal("task",row[header.IndexOf("eye_aoi_category")]);Check.Equal("false",row[header.IndexOf("eye_aoi_avatar_applicable")]);
                            Check.Equal("scene-trial",row[header.IndexOf("eye_aoi_trial")]);Check.Equal("ingest-trial",row[header.IndexOf("eye_ingest_trial_id")]);
                            Check.Equal("8",row[header.IndexOf("eye_aoi_phase")]);Check.Equal("7",row[header.IndexOf("eye_ingest_phase")]);
                            Check.Equal("0",row[header.IndexOf("eye_aoi_scene_frame_age")]);}
                    }
                    Check.Equal(1,native);Check.Equal(1,aoi);
                }
            });
            suite.Run("native acquisition and AOI reject fabricated timing or mismatched native identity",()=>{
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.AcquisitionPayload(1,Acquisition(true)));
                var m=Manifest();Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(1,Frame(m,Aoi(999)),m));
                // Rehash malformed bytes: schema validation must still reject them.
                var malformed=Fixture(change:frames=>Buffer.BlockCopy(BitConverter.GetBytes(-1.0),0,frames[1],21,8));
                Check.Equal(ViveRawSessionReadStatus.Invalid,Scan(malformed).Status);
                var count=Fixture(change:frames=>Buffer.BlockCopy(BitConverter.GetBytes(0L),0,frames[5],frames[5].Length-8,8));
                Check.Equal(ViveRawSessionReadStatus.Invalid,Scan(count).Status);
                var sequence=Fixture(change:frames=>Buffer.BlockCopy(BitConverter.GetBytes(1L),0,frames[1],13,8));
                Check.Equal(ViveRawSessionReadStatus.Invalid,Scan(sequence).Status);
                var denominator=Fixture(change:frames=>Buffer.BlockCopy(BitConverter.GetBytes(0L),0,frames[1],29,8));
                Check.Equal(ViveRawSessionReadStatus.Invalid,Scan(denominator).Status);
            });
            suite.Run("missing AOI geometry remains explicit invalid evidence without preventing capture",()=>{
                var bytes=Fixture(missingAoi:true);var result=Scan(bytes);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.True(result.CaptureTerminalObserved);
                using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {
                    Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,1000,33554432,CancellationToken.None));
                    var lines=Encoding.UTF8.GetString(output.ToArray()).Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);var header=Cells(lines[0]);int observed=0;
                    foreach(string line in lines)
                    {
                        var row=Cells(line);if(row[1]!="observation")continue;observed++;
                        Check.Equal("-1",row[header.IndexOf("eye_aoi_scene_unity_frame")]);
                        Check.Equal(String.Empty,row[header.IndexOf("eye_aoi_scene_frame_age")]);
                        Check.Equal(String.Empty,row[header.IndexOf("eye_aoi_scene_frame_age_raw")]);
                    }
                    Check.Equal(1,observed);
                }
                var old=Scan(Fixture(withAoi:false));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,old.Status);Check.True(old.CaptureTerminalObserved);
            });
            suite.Run("new reader accepts historical terminal without acquisition extension",()=>{
                var payloads=Payloads(ViveRawSessionTerminalTests.Fixture());byte[] terminal=payloads[payloads.Count-1];Array.Resize(ref terminal,terminal.Length-12);payloads[payloads.Count-1]=terminal;
                var result=Scan(Encode(payloads));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.True(result.CaptureTerminalObserved);Check.Equal(0L,result.VerifiedAcquisitionRecords);
            });
            suite.Run("byte ledger bounds both acquisition and optional geometry payloads",()=>{
                var m=Manifest();var ledger=new ViveRawByteBudget.QueueLedger(ViveRawByteBudget.SelectExplicit(1048576),m);int bound;
                var acquisition=Acquisition();Check.True(ledger.TryFrameBound(1,acquisition,out bound));Check.True(ViveRawSessionJournalCodec.AcquisitionPayload(2,acquisition).Length+40<=bound);
                var frame=Frame(m,Aoi());Check.True(ledger.TryFrameBound(2,frame,out bound));Check.True(ViveRawSessionJournalCodec.RecordPayload(3,frame,m).Length+40<=bound);
            });
            suite.Run("callback capture preserves original receipt time cached health and registration result source",()=>{
                var bytes=Fixture(change:frames=>{
                    frames[1]=ViveRawSessionJournalCodec.AcquisitionPayload(1,CallbackAcquisition());
                    var m=Manifest();frames[2]=ViveRawSessionJournalCodec.RecordPayload(2,Frame(m,Aoi(),CallbackDiagnostics()),m);
                });
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,Scan(bytes).Status);
                using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {
                    Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,1000,33554432,CancellationToken.None));
                    var lines=Encoding.UTF8.GetString(output.ToArray()).Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);var header=Cells(lines[0]);int samples=0;
                    foreach(string line in lines)
                    {
                        var row=Cells(line);if(row[1]!="eye_acquisition"&&row[1]!="observation")continue;samples++;
                        Check.Equal("native_callback",row[header.IndexOf("eye_capture_mode")]);Check.Equal("callback_registration",row[header.IndexOf("eye_native_result_source")]);
                        if(row[1]=="eye_acquisition")
                        {Check.Equal("0.75",row[header.IndexOf("eye_callback_observed")]);Check.Equal("0.82",row[header.IndexOf("eye_poll_started")]);
                            Check.Equal("false",row[header.IndexOf("eye_read_result_present")]);Check.Equal("0",row[header.IndexOf("eye_callback_registration_result")]);}
                    }
                    Check.Equal(2,samples);
                }
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.AcquisitionPayload(1,CallbackAcquisition(.8)));
                var m2=Manifest();var noAoi=Frame(m2,null,CallbackDiagnostics());int bound;
                Check.True(new ViveRawByteBudget.QueueLedger(ViveRawByteBudget.SelectExplicit(1048576),m2).TryFrameBound(2,noAoi,out bound));
                Check.True(ViveRawSessionJournalCodec.RecordPayload(3,noAoi,m2).Length+40<=bound);
            });
            suite.Run("new reader retains native acquisition v1 and historical AOI policy without relabelling",()=>{
                var bytes=Fixture(change:frames=>{
                    var native=ReplaceText(ReplaceText(frames[1],"native_poll","",true),"eye_read","",true);
                    // Polling diagnostics end in five absent callback fields.
                    Array.Resize(ref native,native.Length-5);Buffer.BlockCopy(BitConverter.GetBytes(1),0,native,9,4);frames[1]=native;
                    frames[2]=ReplaceText(ReplaceText(frames[2],ViveEyeAoiObservation.PolicyVersion,"vive-classroom-exclusive-surface-proxies-v1"),
                        ViveEyeAoiObservation.ClassificationMeaning,"before-mask-scene-analytic-surface-proxy-not-validated-fixation-or-full-scene-occlusion");
                });
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,Scan(bytes).Status);
                using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {
                    Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,1000,33554432,CancellationToken.None));
                    string csv=Encoding.UTF8.GetString(output.ToArray());Check.True(csv.Contains("vive-classroom-exclusive-surface-proxies-v1"));
                    Check.True(!csv.Contains("vive-classroom-exclusive-render-surface-proxies-v2"));
                }
            });
        }
    }
}
#endif
