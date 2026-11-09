using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Real task events and VRAW3 codec, using only a synthetic clock and
    // memory streams. This does not establish rendering, disk or HMD output.
    public static class VivePresentationProxyJournalTests
    {
        sealed class StepClock:IClock
        {
            double current;
            public double Now {get{current+=.125;return current;}}
            public string ClockId {get{return "proxy-task-clock";}}
        }
        sealed class Fixture
        {
            internal readonly StepClock Clock=new StepClock();
            internal readonly ViveNamedSimulationPlan Plan;
            internal readonly ViveNamedRecordingIntent Intent;
            internal readonly Experiment Task;
            internal Fixture()
            {
                var selection=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"proxy-geometry");
                Plan=ViveNamedSimulationPlan.Select(Actor.Owner,"proxy-plan",selection);
                Intent=ViveNamedRecordingIntent.Select(Actor.Owner,"proxy-intent",true,true);
                Task=new Experiment(SessionConfiguration.NamedRecordingDevelopment("proxy-session",37,Plan,Intent),Clock);
                Task.BeginFamiliarization(Actor.Owner);Task.BeginPractice(Actor.Owner);
            }
            static void Text(BinaryWriter writer,string value)
            {if(value==null){writer.Write(-1);return;}var bytes=Encoding.UTF8.GetBytes(value);writer.Write(bytes.Length);writer.Write(bytes);}
            // Construct a syntactically framed, hash-valid adversarial task
            // record without the producer's validation, to test the reader's
            // independent validation rather than a corrupt checksum.
            static byte[] UncheckedTaskPayload(JournalEvent item,Dictionary<string,string> fields)
            {
                using(var memory=new MemoryStream())using(var writer=new BinaryWriter(memory,Encoding.UTF8))
                {
                    writer.Write(item.Sequence+1);writer.Write((byte)1);writer.Write(item.Sequence);writer.Write(item.Time);
                    Text(writer,item.ClockId);Text(writer,item.SessionId);writer.Write(item.BlockIndex);writer.Write(item.Segment);
                    Text(writer,item.TrialId);Text(writer,item.Kind);writer.Write(item.NamedBlock==null?-1:item.NamedBlock.Index);
                    var keys=new List<string>(fields.Keys);keys.Sort(StringComparer.Ordinal);writer.Write(keys.Count);
                    foreach(string key in keys){Text(writer,key);Text(writer,fields[key]);}
                    writer.Flush();return memory.ToArray();
                }
            }
            internal byte[] Encode(Action<Dictionary<string,string>,JournalEvent> corrupt=null,string corruptKind="stimulus_presented",bool producerRejects=true)
            {
                var manifest=new ViveNamedSessionFrameAdmission.Manifest("proxy-session",Clock.ClockId,Plan,Intent,true);
                manifest.SealChoiceTime(0,-1);
                using(var output=new MemoryStream())
                {
                    byte[] prior=new byte[32],digest;
                    var frame=ViveRawSessionJournalCodec.FrameV3(ViveRawSessionJournalCodec.ManifestPayloadV3(manifest),prior,true,out digest);
                    output.Write(frame,0,frame.Length);prior=digest;
                    foreach(var item in Task.Events)
                    {byte[] payload;
                        if(corrupt!=null&&item.Kind==corruptKind)
                        {var fields=new Dictionary<string,string>();foreach(var pair in item.Fields)fields.Add(pair.Key,pair.Value);
                            corrupt(fields,item);var forged=new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,
                                item.TrialId,item.Kind,fields,null,Plan,item.NamedBlock,Intent);
                            if(producerRejects)
                            {Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(item.Sequence+1,forged,manifest));
                                payload=UncheckedTaskPayload(item,fields);}
                            else payload=ViveRawSessionJournalCodec.RecordPayload(item.Sequence+1,forged,manifest);}
                        else payload=ViveRawSessionJournalCodec.RecordPayload(item.Sequence+1,item,manifest);
                        frame=ViveRawSessionJournalCodec.FrameV3(payload,prior,false,out digest);
                        output.Write(frame,0,frame.Length);prior=digest;}
                    return output.ToArray();
                }
            }
        }
        static JournalEvent Find(Experiment task,string kind)
        {foreach(var item in task.Events)if(item.Kind==kind)return item;throw new InvalidOperationException("Fixture event missing: "+kind);}
        static ViveRawSessionReadResult Project(Fixture fixture)
        {using(var input=new MemoryStream(fixture.Encode(),false))
            return ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,5000,CancellationToken.None);}
        static void CheckDeclaration(bool interrupt,bool respond)
        {
            var f=new Fixture();var trial=f.Task.CurrentTrial;string id=trial.TrialId;
            f.Task.MarkPresentedWithSoftwareProxy(id,12.75,42,8);
            var presented=Find(f.Task,"stimulus_presented");
            Check.True(trial.Onset.HasValue&&presented.Time>trial.Onset.Value);
            string onset=trial.Onset.Value.ToString("R",CultureInfo.InvariantCulture);
            Check.Equal(onset,presented.Fields["task_onset_seconds"]);
            Check.Equal(f.Clock.ClockId,presented.Fields["task_onset_clock_id"]);
            Check.Equal("task_clock_after_caller_declared_unity_proxy",presented.Fields["task_onset_basis"]);
            Check.Equal("12.75",presented.Fields["unity_render_proxy_seconds"]);
            Check.Equal("unity-realtime-since-startup",presented.Fields["unity_render_proxy_clock_id"]);
            Check.Equal("42",presented.Fields["unity_render_proxy_frame"]);
            Check.Equal("8",presented.Fields["unity_render_proxy_revision"]);
            Check.Equal("caller_declared_unity_render_proxy_not_photon_onset",presented.Fields["unity_render_proxy_basis"]);
            if(interrupt)f.Task.Pause(Actor.Participant);
            if(respond)Check.True(f.Task.ButtonChanged(trial.Stimulus.Ink,true));
            var result=Project(f);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
            Check.True(result.TrialProjection!=null&&!result.CompleteSessionProven&&!result.CurrentAccessAuthorized);
            var observed=result.TrialProjection.Trials[0];Check.Equal(id,observed.TrialId);
            Check.Equal(onset,observed.DeclaredPresentationTaskOnset);
            Check.Equal(f.Clock.ClockId,observed.DeclaredPresentationTaskClock);
            Check.Equal("task_clock_after_caller_declared_unity_proxy",observed.DeclaredPresentationTaskBasis);
            Check.Equal("12.75",observed.DeclaredUnityRenderSeconds);
            Check.Equal("unity-realtime-since-startup",observed.DeclaredUnityRenderClock);
            Check.Equal("42",observed.DeclaredUnityRenderFrame);
            Check.Equal("8",observed.DeclaredUnityRenderRevision);
            Check.Equal("caller_declared_unity_render_proxy_not_photon_onset",observed.DeclaredUnityRenderBasis);
            Check.True(observed.PresentationEventLocalSeconds.HasValue&&observed.PresentationEventLocalSeconds.Value>trial.Onset.Value);
            Check.Equal(interrupt?1:0,observed.InterruptionEvents);Check.Equal(respond?1:0,observed.ResponseEvents);
            Check.Equal(respond?onset:null,observed.DeclaredOnset);
            Check.True(!observed.EventConflict);Check.Equal("UNKNOWN",observed.Validity);Check.Equal("UNKNOWN",observed.RenderProxyEvidence);
            ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(result,true,out report));
            Check.True(!report.Saved&&!report.CompleteSessionProven&&!report.CurrentAccessAuthorized);
            var rows=report.TrialsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);
            // A response immediately prepares the next trial; interruption or
            // no response leaves just the current trial declaration.
            Check.Equal(respond?3:2,rows.Length);var header=rows[0].Split(',');var data=rows[1].Split(',');Check.Equal(47,header.Length);Check.Equal(47,data.Length);
            int taskOnsetColumn=Array.IndexOf(header,"declared_presentation_task_onset"),unityColumn=Array.IndexOf(header,"declared_unity_render_seconds");
            Check.True(taskOnsetColumn>=0&&unityColumn>=0);
            Check.Equal("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(onset)),data[taskOnsetColumn]);
            Check.Equal("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes("12.75")),data[unityColumn]);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VRAW3 preserves distinct task onset and Unity proxy before any response",()=>CheckDeclaration(false,false));
            suite.Run("VRAW3 preserves onset and proxy when the trial is interrupted",()=>CheckDeclaration(true,false));
            suite.Run("VRAW3 preserves both presentation and response onset declarations",()=>CheckDeclaration(false,true));
            suite.Run("VIVE named preview rejects invalid software proxy values before onset",()=>{
                foreach(double time in new[]{-1.0,Double.NaN,Double.PositiveInfinity})
                {var f=new Fixture();var trial=f.Task.CurrentTrial;Check.Throws<InvalidOperationException>(()=>f.Task.MarkPresentedWithSoftwareProxy(trial.TrialId,time,42,8));
                    Check.True(!trial.Onset.HasValue);}
            });
            suite.Run("VRAW3 rejects incomplete or invalid software-proxy declarations even with valid frame hashes",()=>{
                var f=new Fixture();f.Task.MarkPresentedWithSoftwareProxy(f.Task.CurrentTrial.TrialId,12.75,42,8);
                var corruptions=new Action<Dictionary<string,string>,JournalEvent>[] {
                    (fields,item)=>fields.Remove("task_onset_seconds"),
                    (fields,item)=>fields["task_onset_clock_id"]="different-task-clock",
                    (fields,item)=>fields["task_onset_seconds"]=(item.Time+1).ToString("R",CultureInfo.InvariantCulture),
                    (fields,item)=>fields["unity_render_proxy_seconds"]="NaN",
                    (fields,item)=>fields["unity_render_proxy_frame"]="-1",
                    (fields,item)=>fields["software_proxy_schema"]="2",
                    (fields,item)=>fields["unity_render_proxy_basis"]="photon-onset"
                };
                foreach(var corrupt in corruptions)using(var input=new MemoryStream(f.Encode(corrupt),false))
                {var result=ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,5000,CancellationToken.None);
                    Check.Equal(ViveRawSessionReadStatus.Invalid,result.Status);Check.Equal((ViveRawTrialProjection)null,result.TrialProjection);Check.Equal(0L,result.VerifiedBytes);}
            });
            suite.Run("VRAW3 flags contradictory response onset without replacing either declaration",()=>{
                var f=new Fixture();var trial=f.Task.CurrentTrial;f.Task.MarkPresentedWithSoftwareProxy(trial.TrialId,12.75,42,8);
                Check.True(f.Task.ButtonChanged(trial.Stimulus.Ink,true));
                foreach(string supplied in new[]{"999","not-a-number"})using(var input=new MemoryStream(f.Encode((fields,item)=>fields["onset"]=supplied,"first_response",false),false))
                {var result=ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,5000,CancellationToken.None);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);var observed=result.TrialProjection.Trials[0];
                    Check.True(observed.EventConflict);Check.Equal(supplied,observed.DeclaredOnset);
                    Check.Equal(trial.Onset.Value.ToString("R",CultureInfo.InvariantCulture),observed.DeclaredPresentationTaskOnset);}
                string equivalent=trial.Onset.Value.ToString("0.000000",CultureInfo.InvariantCulture);
                using(var input=new MemoryStream(f.Encode((fields,item)=>fields["onset"]=equivalent,"first_response",false),false))
                {var result=ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,5000,CancellationToken.None);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.True(!result.TrialProjection.Trials[0].EventConflict);}
            });
            suite.Run("VRAW3 flags only an explicit interruption contradiction",()=>{
                var f=new Fixture();var trial=f.Task.CurrentTrial;f.Task.MarkPresentedWithSoftwareProxy(trial.TrialId,12.75,42,8);f.Task.Pause(Actor.Participant);
                using(var input=new MemoryStream(f.Encode((fields,item)=>fields["onset_available"]="False","trial_interrupted",false),false))
                {var result=ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,5000,CancellationToken.None);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.True(result.TrialProjection.Trials[0].EventConflict);}
                foreach(bool remove in new[]{true,false})using(var input=new MemoryStream(f.Encode((fields,item)=>{if(remove)fields.Remove("onset_available");else fields["onset_available"]=null;},"trial_interrupted",false),false))
                {var result=ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,5000,CancellationToken.None);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.True(!result.TrialProjection.Trials[0].EventConflict);}
            });
            suite.Run("legacy Quest presentation does not acquire VIVE proxy fields",()=>{
                var clock=new StepClock();var config=new SessionConfiguration("quest-proxy-regression",123,TrialPlans.Allocate(1,123)[0],RunKind.Simulation,"en",1.25);
                var task=new Experiment(config,clock);task.BeginFamiliarization(Actor.Owner);task.BeginPractice(Actor.Owner);
                task.MarkPresented(task.CurrentTrial.TrialId);var item=Find(task,"stimulus_presented");
                Check.True(!item.Fields.ContainsKey("task_onset_seconds")&&!item.Fields.ContainsKey("task_onset_clock_id")&&
                    !item.Fields.ContainsKey("task_onset_basis")&&!item.Fields.ContainsKey("unity_render_proxy_seconds")&&
                    !item.Fields.ContainsKey("unity_render_proxy_clock_id")&&!item.Fields.ContainsKey("unity_render_proxy_frame")&&
                    !item.Fields.ContainsKey("unity_render_proxy_revision")&&!item.Fields.ContainsKey("unity_render_proxy_basis"));
            });
        }
    }
}

