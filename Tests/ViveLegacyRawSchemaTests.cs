using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // No device, file, output binding or participant capture. All bytes are synthetic memory.
    public static class ViveLegacyRawSchemaTests
    {
        static ViveNamedSessionFrameAdmission.Manifest Manifest(out Experiment experiment,double task=1,double eye=2)
        {
            var allocation=new Allocation("synthetic-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var design=ViveLegacyRecordingDesign.Select(Actor.Owner,"synthetic-design",allocation,"synthetic-geometry","synthetic-mapping");
            var intent=ViveLegacyRecordingIntent.Select(Actor.Owner,"synthetic-intent",true,true);
            var config=SessionConfiguration.LegacyRecordingDevelopment(new SessionConfiguration("synthetic-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true),design,intent);
            experiment=new Experiment(config,new ManualClock());
            var manifest=new ViveNamedSessionFrameAdmission.Manifest(ViveRawSessionContext.FromConfiguration(config),"test-monotonic",true,task,eye);
            manifest.SealChoiceTime(3,-1);return manifest;
        }
        static byte[] Framed(byte[] payload,bool v3=false)
        {byte[] digest;return v3?ViveRawSessionJournalCodec.FrameV3(payload,new byte[32],true,out digest):ViveRawSessionJournalCodec.FrameV4(payload,new byte[32],true,out digest);}
        static ViveRawSessionReadResult Read(byte[] bytes)
        {using(var stream=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.ProjectTrials(stream,true,true,1048576,4096,CancellationToken.None);}
        static byte[] Replace(byte[] input,string from,string to)
        {
            if(from.Length!=to.Length)throw new Exception("Fixed-length fixture mutation required.");
            byte[] result=(byte[])input.Clone(),a=Encoding.UTF8.GetBytes(from),b=Encoding.UTF8.GetBytes(to);
            for(int i=0;i<=result.Length-a.Length;i++)
            {bool found=true;for(int j=0;j<a.Length;j++)if(result[i+j]!=a[j]){found=false;break;}if(found){Buffer.BlockCopy(b,0,result,i,b.Length);return result;}}
            throw new Exception("Fixture token absent.");
        }
        static byte[] WithChoices(byte[] input,double task,double eye)
        {
            var result=(byte[])input.Clone();using(var memory=new MemoryStream(result,true))using(var reader=new BinaryReader(memory))
            {memory.Position=9;for(int i=0;i<20;i++){int count=reader.ReadInt32();memory.Position+=count;}memory.Position+=3+8+8+16;
                using(var writer=new BinaryWriter(memory)){writer.Write(task);writer.Write(eye);}}
            return result;
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("legacy VRAW4 explicit allocation projects B C A D without named aliases",()=>{
                Experiment e;var m=Manifest(out e);var result=Read(Framed(ViveRawSessionJournalCodec.ManifestPayloadV4(m)));
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.Equal("LEGACY_ABCD",result.TrialProjection.DesignVariant);
                string[] order={"B","C","A","D"};for(int i=0;i<4;i++){Check.Equal(order[i],result.TrialProjection.DeclaredPlanBlocks[i].Option);Check.Equal(i,result.TrialProjection.DeclaredPlanBlocks[i].ListIndex);}
                Check.True(!result.CompleteSessionProven&&!result.CaptureTerminalObserved&&!result.SourceAuthenticated);
                ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(result,true,out report));Check.True(report.BlocksCsv.StartsWith("block,legacy_condition,list_index,mapping,geometry\n",StringComparison.Ordinal));
            });
            suite.Run("legacy VRAW4 refuses named formats and wrong discriminator",()=>{
                Experiment e;var m=Manifest(out e);var payload=ViveRawSessionJournalCodec.ManifestPayloadV4(m);
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.ManifestPayload(m));Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.ManifestPayloadV3(m));
                Check.Equal(ViveRawSessionReadStatus.Invalid,Read(Framed(payload,true)).Status);
                Check.Equal(ViveRawSessionReadStatus.Invalid,Read(Framed(Replace(payload,"LEGACY_ABCD","NAMED__ABCD"))).Status);
            });
            suite.Run("legacy VRAW4 rejects altered allocation instead of inferring condition",()=>{
                Experiment e;var m=Manifest(out e);var payload=ViveRawSessionJournalCodec.ManifestPayloadV4(m);
                // Four fixed-width allocation rows end the manifest. Change first actual condition B to A.
                payload[payload.Length-44]=0;
                Check.Equal(ViveRawSessionReadStatus.Invalid,Read(Framed(payload)).Status);
            });
            suite.Run("legacy VRAW4 preserves all actual initial task events and exact measurement",()=>{
                Experiment e;var m=Manifest(out e);e.BeginFamiliarization(Actor.Owner);e.PrepareRawRecordingBlockPlan(Actor.Owner,true);
                byte[] previous=new byte[32];long ordinal=0;using(var output=new MemoryStream())
                {
                    Action<byte[]> append=payload=>{byte[] digest;var bytes=ViveRawSessionJournalCodec.FrameV4(payload,previous,ordinal++==0,out digest);output.Write(bytes,0,bytes.Length);previous=digest;};
                    append(ViveRawSessionJournalCodec.ManifestPayloadV4(m));
                    foreach(var item in e.Events){byte[] payload=ViveRawSessionJournalCodec.RecordPayload(ordinal,item,m);int size;Check.True(ViveRawTaskFrameMeasurement.TryMeasure(ordinal,item,m,1048576,out size));Check.Equal(payload.Length+40,size);append(payload);}
                    var result=Read(output.ToArray());Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.Equal((long)e.Events.Count,result.VerifiedTaskRecords);
                    Check.Equal(1,result.TrialProjection.PreparedPlans.Count);Check.Equal(8,result.TrialProjection.PreparedPlans[0].Items.Count);
                }
            });
            suite.Run("legacy VRAW4 rejects named provenance in task bytes and typed events",()=>{
                Experiment e;var m=Manifest(out e);var item=e.Events[0];var fields=new Dictionary<string,string>();foreach(var field in item.Fields)fields.Add(field.Key,field.Value);fields.Add("named_schema","vive-named-recording-intent-v3");
                var mixed=new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,item.TrialId,item.Kind,fields,null,
                    legacyRecordingDesign:item.LegacyRecordingDesign,legacyRecordingIntent:item.LegacyRecordingIntent);
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(1,mixed,m));
                byte[] digest;var manifest=ViveRawSessionJournalCodec.FrameV4(ViveRawSessionJournalCodec.ManifestPayloadV4(m),new byte[32],true,out digest);
                var task=ViveRawSessionJournalCodec.FrameV4(Replace(ViveRawSessionJournalCodec.RecordPayload(1,item,m),"legacy_phase","named__phase"),digest,false,out digest);
                using(var stream=new MemoryStream()){stream.Write(manifest,0,manifest.Length);stream.Write(task,0,task.Length);Check.Equal(ViveRawSessionReadStatus.Invalid,Read(stream.ToArray()).Status);}
            });
            suite.Run("legacy prepared-plan schema requires legacy phase and actual declared list",()=>{
                var fields=new Dictionary<string,string>{{"recording_plan_schema","1"},{"plan_scope","MAIN"},{"attempt","0"},{"list","main-v1-0-12345678"},{"item_count","24"},
                    {"legacy_phase","BlockReady"},{"legacy_block_started","False"},{"legacy_list_index","0"}};
                for(int i=0;i<24;i++){string prefix="item_"+i.ToString("D2",System.Globalization.CultureInfo.InvariantCulture);fields.Add(prefix+"_word",((Colour)0).ToString());fields.Add(prefix+"_ink",((Colour)1).ToString());}
                Check.Equal(24,new ViveRawPreparedPlanAudit().Add(fields,null,0,0,1,2,true).Items.Count);
                Check.Throws<ViveRawPreparedPlanAudit.InvalidPlan>(()=>new ViveRawPreparedPlanAudit().Add(fields,null,0,0,1,2));
                fields["legacy_list_index"]="1";Check.Throws<ViveRawPreparedPlanAudit.InvalidPlan>(()=>new ViveRawPreparedPlanAudit().Add(fields,null,0,0,1,2,true));
            });
            suite.Run("legacy VRAW4 software choice sentinel is explicit and finite",()=>{
                Experiment e;Check.Throws<InvalidOperationException>(()=>Manifest(out e,-1,-1));Check.Throws<InvalidOperationException>(()=>Manifest(out e,Double.NaN,2));
                var payload=ViveRawSessionJournalCodec.ManifestPayloadV4(Manifest(out e));
                foreach(var pair in new[]{new[]{-1d,-1d},new[]{-1d,2d},new[]{Double.NaN,2d},new[]{1d,Double.PositiveInfinity},new[]{2d,1d},new[]{1d,4d}})
                    Check.Equal(ViveRawSessionReadStatus.Invalid,Read(Framed(WithChoices(payload,pair[0],pair[1]))).Status);
            });
        }
    }
}
