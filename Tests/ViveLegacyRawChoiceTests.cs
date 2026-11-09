using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Deliberately untrusted, re-chained bytes. These do not issue an admission,
    // receipt or source authority and never leave memory.
    public static class ViveLegacyRawChoiceTests
    {
        static void Text(BinaryWriter w,string value)
        {if(value==null){w.Write(-1);return;}var bytes=Encoding.UTF8.GetBytes(value);w.Write(bytes.Length);w.Write(bytes);}
        static SessionConfiguration Config()
        {
            var allocation=new Allocation("choice-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            return SessionConfiguration.LegacyRecordingDevelopment(new SessionConfiguration("choice-session",12,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true),
                ViveLegacyRecordingDesign.Select(Actor.Owner,"choice-design",allocation,"choice-geometry",ViveFixedTrackpadMapping.Version),
                ViveLegacyRecordingIntent.Select(Actor.Owner,"choice-intent",true,true));
        }
        static byte[] Bytes(bool include=true,bool duplicate=false,bool late=false,bool mismatch=false,bool conflictingAllocation=false,string conflictField="condition")
        {
            var config=Config();var experiment=new Experiment(config,new ManualClock());
            var manifest=new ViveNamedSessionFrameAdmission.Manifest(ViveRawSessionContext.FromConfiguration(config),"test-monotonic",true,1,2);
            manifest.SealChoiceTime(3,-1);byte[] previous=new byte[32];long ordinal=0,sequence=0;
            using(var output=new MemoryStream())
            {
                Action<byte[]> append=payload=>{byte[] digest;var bytes=ViveRawSessionJournalCodec.FrameV4(payload,previous,ordinal++==0,out digest);output.Write(bytes,0,bytes.Length);previous=digest;};
                append(ViveRawSessionJournalCodec.ManifestPayloadV4(manifest));
                Action<string,string> task=(kind,phase)=>{
                    var fields=new Dictionary<string,string>();foreach(var pair in experiment.Events[0].Fields)fields.Add(pair.Key,pair.Value);
                    fields["legacy_phase"]=phase;
                    if(kind=="participant_recording_software_choices")
                    {fields["task_choice_poll_seconds"]=mismatch?"0":"1";fields["eye_choice_poll_seconds"]="2";fields["choice_clock_id"]="test-monotonic";
                        fields["choice_time_basis"]="controller_poll_not_hardware_event";fields["identity_or_study_consent"]="NOT_AUTHENTICATED";}
                    if(kind=="session_ended")fields["reason"]="Completed";
                    if(kind=="phase_changed")fields["to"]=phase;
                    int block=-1;
                    if(kind=="block_configuration_locked")
                    {block=0;fields["legacy_condition"]="B";fields["legacy_list_index"]="0";fields["condition"]=conflictField=="condition"?"A":"B";fields["list_index"]=conflictField=="list_index"?"1":"0";fields["b_initial_target"]=conflictField=="b_initial_target"?"Task":"Participant";}
                    using(var memory=new MemoryStream())using(var w=new BinaryWriter(memory,Encoding.UTF8))
                    {
                        w.Write(ordinal);w.Write((byte)1);w.Write(sequence++);w.Write(4d);Text(w,"test-monotonic");Text(w,config.SessionId);
                        w.Write(block);w.Write(0);Text(w,null);Text(w,kind);w.Write(block);
                        var keys=new List<string>(fields.Keys);keys.Sort(StringComparer.Ordinal);w.Write(keys.Count);foreach(var key in keys){Text(w,key);Text(w,fields[key]);}
                        w.Flush();append(memory.ToArray());
                    }
                };
                if(late)task("phase_changed","Familiarization");
                if(include)task("participant_recording_software_choices","Preparation");
                if(duplicate)task("participant_recording_software_choices","Preparation");
                if(conflictingAllocation)task("block_configuration_locked","BlockReady");
                task("session_ended","Questionnaire");task("phase_changed","Ended");
                append(ViveRawSessionJournalCodec.TerminalPayloadV3(ordinal,sequence-1,sequence,0,0,sequence,4));return output.ToArray();
            }
        }
        static ViveRawSessionReadResult Read(byte[] bytes)
        {using(var source=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.ProjectTrials(source,true,true,1048576,4096,CancellationToken.None);}
        public static void Register(TestSuite suite)
        {
            suite.Run("legacy recorded configuration validates actual mapping threshold and geometry without changing preview",()=>{
                var config=Config();var clock=new MonotonicClock("mapping-clock");
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"choice-geometry");
                var wrong=ViveFixedTrackpadMapping.Create();wrong[ViveTrackpadDirection.Up]=Colour.Green;wrong[ViveTrackpadDirection.Right]=Colour.Red;
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentParticipant(config,clock,geometry,wrong,.5,true));
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentParticipant(config,clock,geometry,ViveFixedTrackpadMapping.Create(),.25,true));
                var otherGeometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"different-geometry");
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentParticipant(config,clock,otherGeometry,ViveFixedTrackpadMapping.Create(),.5,true));
                var preview=new SessionConfiguration("unchanged-preview",12,config.Allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
                Check.True(new ViveDevelopmentParticipant(preview,clock,geometry,wrong,.25,true).RawContext==null);
            });
            suite.Run("legacy design rejects wrong explicit allocation list order and B target",()=>{
                Check.Throws<ArgumentException>(()=>ViveLegacyRecordingDesign.Select(Actor.Owner,"wrong-order",
                    new Allocation("wrong-order",new[]{Condition.A,Condition.B,Condition.D,Condition.C},GazeTarget.Participant,new[]{0,1,2,3}),"g",ViveFixedTrackpadMapping.Version));
                Check.Throws<ArgumentException>(()=>ViveLegacyRecordingDesign.Select(Actor.Owner,"wrong-list",
                    new Allocation("wrong-list",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{1,0,2,3}),"g",ViveFixedTrackpadMapping.Version));
                Check.Throws<ArgumentException>(()=>ViveLegacyRecordingDesign.Select(Actor.Owner,"wrong-target",
                    new Allocation("wrong-target",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Task,new[]{0,1,2,3}),"g",ViveFixedTrackpadMapping.Version));
            });
            suite.Run("legacy normal terminal requires exactly one matching pre-practice software-choice event",()=>{
                var valid=Read(Bytes());Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,valid.Status);Check.True(valid.CaptureTerminalObserved&&!valid.CompleteSessionProven);
                var missing=Bytes(include:false);Check.Equal(ViveRawSessionReadStatus.Invalid,Read(missing).Status);
                Array.Resize(ref missing,missing.Length-ViveRawByteBudget.TerminalFrameBytes);
                var prefix=Read(missing);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,prefix.Status);Check.True(!prefix.CaptureTerminalObserved&&!prefix.CompleteSessionProven);
                Check.Equal(ViveRawSessionReadStatus.Invalid,Read(Bytes(duplicate:true)).Status);
                Check.Equal(ViveRawSessionReadStatus.Invalid,Read(Bytes(late:true)).Status);
                Check.Equal(ViveRawSessionReadStatus.Invalid,Read(Bytes(mismatch:true)).Status);
            });
            suite.Run("legacy raw reader rejects contradictory ordinary allocation declarations",()=>{
                Check.Equal(ViveRawSessionReadStatus.Invalid,Read(Bytes(conflictingAllocation:true,conflictField:"condition")).Status);
                Check.Equal(ViveRawSessionReadStatus.Invalid,Read(Bytes(conflictingAllocation:true,conflictField:"list_index")).Status);
                Check.Equal(ViveRawSessionReadStatus.Invalid,Read(Bytes(conflictingAllocation:true,conflictField:"b_initial_target")).Status);
            });
            suite.Run("legacy raw provenance remains explicit after a type-erasing event copy",()=>{
                var e=new Experiment(Config(),new ManualClock()).Events[0];Check.True(e.CarriesLegacyRawProvenance&&!e.CarriesNamedProvenance);
                var fields=new Dictionary<string,string>();foreach(var pair in e.Fields)fields.Add(pair.Key,pair.Value);
                var copied=new JournalEvent(e.Sequence,e.Time,e.ClockId,e.SessionId,e.BlockIndex,e.Segment,e.TrialId,e.Kind,fields);
                Check.True(copied.CarriesLegacyRawProvenance&&!copied.CarriesNamedProvenance);
                Check.Throws<InvalidOperationException>(()=>VivePracticeJournalV3Codec.TaskDetail(e));
                Check.Throws<InvalidOperationException>(()=>VivePracticeJournalV3Codec.TaskDetail(copied));
                Check.Throws<InvalidOperationException>(()=>VivePracticeMultiCodec.EncodeEvent(e,65536));
                Check.Throws<InvalidOperationException>(()=>VivePracticeMultiCodec.EncodeEvent(copied,65536));
            });
            suite.Run("legacy V4 report does not relabel participant choices as owner admission",()=>{
                ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(Read(Bytes()),true,out report));
                Check.True(!report.ManifestCsv.Contains("legacy_second_choice_local_observed_semantics"));
            });
        }
    }
}
