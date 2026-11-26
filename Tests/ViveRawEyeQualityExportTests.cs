#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic memory bytes only. No sensor, app, file or native launch.
    public static class ViveRawEyeQualityExportTests
    {
        static byte[] Fixture(ViveRawEyeObservation[] observations,bool terminal=true,Action<byte[]> alter=null,bool late=false)
        {
            var original=ViveRawSessionTerminalTests.Fixture();var payloads=new List<byte[]>();
            for(int p=8;p<original.Length;)
            {int n=ContinuousJournal.Int(original,p);var value=new byte[n];Buffer.BlockCopy(original,p+8,value,0,n);payloads.Add(value);p+=40+n;}
            using(var output=new MemoryStream())
            {
                byte[] prior=new byte[32];long ordinal=0;
                Action<byte[]> append=payload=>{Buffer.BlockCopy(BitConverter.GetBytes(ordinal),0,payload,0,8);
                    byte[] digest,frame=ViveRawSessionJournalCodec.FrameV3(payload,prior,ordinal==0,out digest);
                    output.Write(frame,0,frame.Length);prior=digest;ordinal++;};
                append(payloads[0]);
                if(late){append(payloads[1]);append(payloads[2]);}
                foreach(var observation in observations)
                {byte[] payload=ViveRawSessionJournalCodec.EyeObservationPayload(ordinal,observation);if(alter!=null)alter(payload);append(payload);}
                if(!late){append(payloads[1]);append(payloads[2]);}
                if(terminal)append(ViveRawSessionJournalCodec.TerminalPayloadV3(ordinal,13,2,0,0,ordinal-1,2,observations.Length));
                return output.ToArray();
            }
        }
        static ViveRawEyeObservation Q(double time,ViveRawEyeObservation.Reason reason,long cursor=11)
        {return new ViveRawEyeObservation(time,reason,cursor);}
        static ViveRawSessionReadResult Scan(byte[] bytes,bool project=false)
        {using(var input=new MemoryStream(bytes,false))return project?
            ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,10000,CancellationToken.None):
            ViveRawSessionJournalReader.Scan(input,true,true,4194304,10000,CancellationToken.None);}
        static List<string> Cells(string line)
        {
            var result=new List<string>();var cell=new StringBuilder();bool quoted=false;
            for(int i=0;i<line.Length;i++)
            {char c=line[i];if(c=='"'){if(quoted&&i+1<line.Length&&line[i+1]=='"'){cell.Append('"');i++;}else quoted=!quoted;}
                else if(c==','&&!quoted){result.Add(cell.ToString());cell.Length=0;}else cell.Append(c);}
            Check.True(!quoted);result.Add(cell.ToString());return result;
        }
        static byte[] Digest(string hex)
        {var b=new byte[hex.Length/2];for(int i=0;i<b.Length;i++)b[i]=Byte.Parse(hex.Substring(i*2,2),NumberStyles.HexNumber,CultureInfo.InvariantCulture);return b;}
        public static void Register(TestSuite suite)
        {
            suite.Run("eye quality kind5 roundtrip separates missing markers from native samples and task events",()=>{
                var bytes=Fixture(new[]{Q(.6,ViveRawEyeObservation.Reason.Duplicate),Q(.7,ViveRawEyeObservation.Reason.InvalidQuality),Q(.8,ViveRawEyeObservation.Reason.NoEye)});
                var before=(byte[])bytes.Clone();var read=Scan(bytes,true);
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,read.Status);Check.True(read.CaptureTerminalObserved);
                Check.Equal(7L,read.VerifiedRecordCount);Check.Equal(2L,read.VerifiedTaskRecords);
                Check.Equal(0L,read.VerifiedObservationRecords);Check.Equal(0L,read.VerifiedStatusRecords);Check.Equal(3L,read.VerifiedEyeQualityRecords);
                Check.True(!read.CompleteSessionProven&&!read.DurableStorageProven);Check.Equal(3,read.TrialProjection.EyeQualityObservations.Count);
                Check.Equal(11L,read.TrialProjection.EyeQualityObservations[0].TaskCursor);
                Check.True(ViveRawSessionJournalSink.ReadbackMatches(read,bytes.Length,7,2,0,0,Digest(read.VerifiedPrefixChainDigestHex)));
                read.VerifiedEyeQualityRecords=2;
                Check.True(!ViveRawSessionJournalSink.ReadbackMatches(read,bytes.Length,7,2,0,0,Digest(read.VerifiedPrefixChainDigestHex)));
                for(int i=0;i<bytes.Length;i++)Check.Equal(before[i],bytes[i]);
            });
            suite.Run("eye quality readable CSV has filterable states exact cursor and no gaze fill",()=>{
                var marks=new[]{Q(.6,ViveRawEyeObservation.Reason.Duplicate),Q(.7,ViveRawEyeObservation.Reason.FrozenOrStale),
                    Q(.8,ViveRawEyeObservation.Reason.InvalidQuality),Q(.9,ViveRawEyeObservation.Reason.NoEye),Q(1,ViveRawEyeObservation.Reason.OutOfOrder),
                    Q(1.1,ViveRawEyeObservation.Reason.SnapshotBusy),Q(1.2,ViveRawEyeObservation.Reason.ClientInitializationFailed),
                    Q(1.3,ViveRawEyeObservation.Reason.ClientFaulted)};
                using(var input=new MemoryStream(Fixture(marks),false))using(var output=new MemoryStream())
                {
                    Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,10000,33554432,CancellationToken.None));
                    string csv=Encoding.UTF8.GetString(output.ToArray());var lines=csv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);var header=Cells(lines[0]);int count=0;
                    foreach(var line in lines)
                    {
                        var cells=Cells(line);Check.Equal(header.Count,cells.Count);
                        if(cells[1]!="eye_observation")continue;
                        Check.Equal(count==0?"NO_NEW_SAMPLE":"MISSING",cells[header.IndexOf("eye_observation")]);
                        Check.Equal(ViveRawReadableAnalysisCsv.EyeReasonLabel(count),cells[header.IndexOf("eye_observation_reason")]);
                        Check.Equal(marks[count].ObservedLocalSeconds.ToString("R",CultureInfo.InvariantCulture),cells[header.IndexOf("eye_observation_owner_local_seconds")]);
                        Check.Equal("11",cells[header.IndexOf("eye_observation_task_cursor")]);
                        for(int i=7;i<header.IndexOf("eye_observation");i++)Check.Equal(String.Empty,cells[i]);
                        Check.Equal("NO_GAZE_VALUES_NO_FILL",cells[6]);count++;
                    }
                    Check.Equal(8,count);Check.True(csv.Contains("UNKNOWN_NOT_ZERO_MISSING_SAMPLES")&&csv.Contains("verified_owner_eye_quality_records,text,8")&&
                        csv.Contains("OWNER_SNAPSHOT_CONTENTION_OR_COHERENCE_CHANGE_NOT_PHYSICAL_SENSOR_LOSS"));
                }
            });
            suite.Run("eye quality typed report retains bounded declarations without claiming missing sample counts",()=>{
                var read=Scan(Fixture(new[]{Q(.6,ViveRawEyeObservation.Reason.NoEye)}),true);ViveRawTrialReport report;
                Check.True(ViveRawTrialReport.TryBuild(read,true,out report));
                Check.True(report.EyeQualityCsv.Contains("state:MISSING,state:NO_EYE,state:unknown,bool:false"));
                Check.True(report.ManifestCsv.Contains("owner_eye_quality_records,i64:1")&&report.ManifestCsv.Contains("sampling_coverage,state:unknown"));
                Check.True(!report.CompleteSessionProven&&!report.Saved);
            });
            suite.Run("eye quality new reader preserves old files and never relabels legacy invalid gaze as valid",()=>{
                var old=Scan(ViveRawSessionTerminalTests.Fixture(),true);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,old.Status);Check.Equal(0L,old.VerifiedEyeQualityRecords);
                Check.Equal(0,old.TrialProjection.EyeQualityObservations.Count);
                long a,b;byte[] bytes=ViveRawSessionJournalReaderTests.Fixture(out a,out b);
                using(var input=new MemoryStream(bytes,false)){var read=ViveRawSessionJournalReader.Read(input,true,true,4194304,10000);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,read.Status);Check.Equal(1L,read.VerifiedObservationRecords);}
                var method=typeof(ViveRawReadableAnalysisCsv).GetMethod("ConvertFrame",BindingFlags.NonPublic|BindingFlags.Static);
                string csv=(string)method.Invoke(null,new object[]{"2,observation,combined_direction_x,f64_bits_hex,7FF8000000000000\n"});
                Check.True(csv.Contains("SAMPLE_RECORDED")&&!csv.Contains("VALID_NEW_SAMPLE")&&csv.Contains("raw:f64_bits_hex:7FF8000000000000"));
            });
            suite.Run("eye quality strict reader rejects forged enums state pairing clocks and cursor",()=>{
                var marks=new[]{Q(.6,ViveRawEyeObservation.Reason.NoEye)};
                var mutations=new Action<byte[]>[]{p=>Buffer.BlockCopy(BitConverter.GetBytes(2),0,p,17,4),
                    p=>Buffer.BlockCopy(BitConverter.GetBytes(99),0,p,21,4),p=>Buffer.BlockCopy(BitConverter.GetBytes(0),0,p,17,4),
                    p=>Buffer.BlockCopy(BitConverter.GetBytes(Double.NaN),0,p,9,8),p=>Buffer.BlockCopy(BitConverter.GetBytes(Double.PositiveInfinity),0,p,9,8),
                    p=>Buffer.BlockCopy(BitConverter.GetBytes(.4),0,p,9,8),p=>Buffer.BlockCopy(BitConverter.GetBytes(12L),0,p,25,8)};
                foreach(var mutate in mutations)Check.Equal(ViveRawSessionReadStatus.Invalid,Scan(Fixture(marks,alter:mutate)).Status);
                Check.Equal(ViveRawSessionReadStatus.Invalid,Scan(Fixture(new[]{Q(.8,ViveRawEyeObservation.Reason.NoEye),Q(.7,ViveRawEyeObservation.Reason.NoEye)})).Status);
                Check.Equal(ViveRawSessionReadStatus.Invalid,Scan(Fixture(new[]{Q(1.8,ViveRawEyeObservation.Reason.NoEye,13)},late:true)).Status);
            });
            suite.Run("eye quality incomplete prefix cannot become successful readable export",()=>{
                var bytes=Fixture(new[]{Q(.6,ViveRawEyeObservation.Reason.NoEye)},terminal:false);var read=Scan(bytes);
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,read.Status);Check.True(!read.CaptureTerminalObserved&&!read.CompleteSessionProven);
                using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {Check.Equal(ViveRawReadableAnalysisCsv.Outcome.OutputUncertain,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,10000,33554432,CancellationToken.None));
                    Check.True(!Encoding.UTF8.GetString(output.ToArray()).Contains("DECLARED_AND_CHAIN_VERIFIED_NOT_EXPORT_SUCCESS"));}
            });
            suite.Run("eye quality frame byte budget covers exact payload and rejects unknown reason",()=>{
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"g");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"p",option);var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"i",true,true);
                var m=new ViveNamedSessionFrameAdmission.Manifest("s","c",plan,intent,true);m.SealChoiceTime(.5,11);
                var ledger=new ViveRawByteBudget.QueueLedger(ViveRawByteBudget.SelectExplicit(1048576),m);var q=Q(.6,ViveRawEyeObservation.Reason.NoEye);int upper;
                Check.True(ledger.TryFrameBound(0,q,out upper));Check.Equal(73,ViveRawSessionJournalCodec.EyeObservationPayload(1,q).Length+40);Check.True(upper>=73&&upper<=256);
                Check.True(ledger.TryCharge(0,q));Check.Throws<ArgumentOutOfRangeException>(()=>Q(.6,(ViveRawEyeObservation.Reason)99));
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.TerminalPayloadV3(4,13,2,0,0,3,2));
                Check.True(ViveRawSessionJournalCodec.TerminalPayloadV3(4,13,2,0,0,3,2,1).Length>0);
            });
            suite.Run("eye quality typed prefix bundle includes markers without claiming completion",()=>{
                var bytes=Fixture(new[]{Q(.6,ViveRawEyeObservation.Reason.NoEye)},terminal:false);
                using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {
                    Check.Equal(ViveRawSessionStreamExport.Outcome.WrittenAndFlushed,ViveRawSessionStreamExport.Write(input,output,CancellationToken.None));
                    string text=Encoding.UTF8.GetString(output.ToArray());
                    Check.True(text.Contains("SECTION eye_quality ")&&text.Contains("state:MISSING,state:NO_EYE,state:unknown,bool:false")&&
                        text.Contains("session_complete_proven=false")&&text.Contains("capture_terminal_declared,bool:false"));
                }
            });
            suite.Run("eye quality bounded projection fails explicitly while streaming CSV retains long marker stream",()=>{
                var markers=new ViveRawEyeObservation[2049];for(int i=0;i<markers.Length;i++)markers[i]=Q(.6+i*.0001,ViveRawEyeObservation.Reason.NoEye);
                var bytes=Fixture(markers);Check.Equal(ViveRawSessionReadStatus.LimitExceeded,Scan(bytes,true).Status);
                var read=Scan(bytes);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,read.Status);Check.Equal(2049L,read.VerifiedEyeQualityRecords);
                using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,10000,33554432,CancellationToken.None));
                    Check.True(Encoding.UTF8.GetString(output.ToArray()).Contains("verified_owner_eye_quality_records,text,2049"));}
            });
            suite.Run("snapshot busy is typed missing owner evidence not a native sample or behavioral mutation",()=>{
                var mark=Q(.6,ViveRawEyeObservation.Reason.SnapshotBusy);
                Check.Equal(ViveRawEyeObservation.Disposition.Missing,mark.State);
                var read=Scan(Fixture(new[]{mark}),true);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,read.Status);
                Check.Equal(1L,read.VerifiedEyeQualityRecords);Check.Equal(0L,read.VerifiedObservationRecords);Check.Equal(0L,read.VerifiedStatusRecords);
                Check.Equal(2L,read.VerifiedTaskRecords);Check.Equal(5,read.TrialProjection.EyeQualityObservations[0].Reason);
                ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(read,true,out report));
                Check.True(report.EyeQualityCsv.Contains("state:MISSING,state:SNAPSHOT_BUSY,state:unknown,bool:false"));
                Check.Equal(ViveRawSessionReadStatus.Invalid,Scan(Fixture(new[]{mark},alter:p=>Buffer.BlockCopy(BitConverter.GetBytes(0),0,p,17,4))).Status);
                Check.Equal(ViveRawSessionReadStatus.Invalid,Scan(Fixture(new[]{mark},alter:p=>Buffer.BlockCopy(BitConverter.GetBytes(8),0,p,21,4))).Status);
            });
        }
    }
}
#endif
