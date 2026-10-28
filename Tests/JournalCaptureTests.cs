using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class JournalCaptureTests
    {
        static ExportProvenance Provenance(){return new ExportProvenance("journal-capture-fixture","manual-clock acknowledgement; not rendered","manual-clock press edge","sha256:fixture");}
        static ContinuousJournal Journal(MemoryStream data,string role="participant")
        {return new ContinuousJournal(data,()=>data.Flush(),new JournalIdentity("test-session",role,"test-monotonic",Provenance()),true);}
        static List<JournalRecord> Records(MemoryStream data)
        {var rows=new List<JournalRecord>();using(var input=new MemoryStream(data.ToArray())){var result=JournalPrefixReader.Read(input,rows.Add);Check.True(result.Status==JournalReadStatus.CleanClose||result.Status==JournalReadStatus.UncleanEnd,result.Error);}return rows;}
        static HeadEyeSample Operator(long sequence=0,string session="test-session")
        {return new HeadEyeSample(session,"simulator/operator","operator-device","test-monotonic",0,0,sequence,1,1.1,ControlSource.Simulator,StreamRole.OperatorControl,
            new Rotation(Double.NaN,Double.PositiveInfinity,0,1),new Vec3(0,0,1),true,true);}

        public static void Register(TestSuite suite)
        {
            suite.Run("M4 journal captures mutable onset response interruption and technical flags",()=>{
                var clock=new ManualClock();var e=Fixtures.New(clock);var audit=new AuditLog("test-session",RunKind.Simulation);
                using(var data=new MemoryStream())using(var journal=Journal(data))
                {
                    var capture=new SimulationJournalCapture(journal,e,audit);Check.True(capture.Capture());e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);capture.Capture();
                    string id=e.CurrentTrial.TrialId;int count=e.Trials.Count;e.MarkPresented(id);capture.Capture();Check.Equal(count,e.Trials.Count);
                    clock.Advance(.2);e.FlagTechnicalIssue("fixture\nhead-invalid");capture.Capture();e.Pause(Actor.Participant);capture.Capture();Check.Equal(count,e.Trials.Count);
                    long written=journal.LastWrittenSequence;capture.Capture();Check.Equal(written,journal.LastWrittenSequence,"Unchanged capture should append nothing");
                    var observations=Records(data).Where(x=>x.Kind=="trial_observation"&&x.Fields["trial_id"]==id).ToArray();
                    Check.Equal(4,observations.Length);Check.Equal(null,observations[0].Fields["onset_seconds"]);Check.Equal("true",observations[1].Fields["stimulus_onset_recorded"]);
                    var last=observations.Last().Fields;Check.Equal("Interrupted",last["outcome"]);Check.Equal("true",last["technical_issue"]);Check.Equal(null,last["correct"]);Check.Equal(null,last["reaction_seconds"]);
                    Check.Equal("Paused",Records(data).Last(x=>x.Kind=="session_observation").Fields["phase"]);
                }
            });
            suite.Run("M4 journal captures complete session and keeps recorder closure separate",()=>{
                var clock=new ManualClock();var e=Fixtures.New(clock);using(var data=new MemoryStream())using(var journal=Journal(data))
                {
                    var capture=new SimulationJournalCapture(journal,e,new AuditLog("test-session",RunKind.Simulation));capture.Capture();Fixtures.Practice(e,clock,8);capture.Capture();
                    for(int block=0;block<4;block++)
                    {
                        Fixtures.Start(e,clock);capture.Capture();for(int trial=0;trial<24;trial++){Fixtures.Answer(e,clock);capture.Capture();}
                        Fixtures.Ratings(e,clock);capture.Capture();clock.Advance(30);Check.True(e.Ready());capture.Capture();
                    }
                    Check.True(journal.Close());var rows=Records(data);var final=rows.Last(x=>x.Kind=="session_observation");Check.Equal("true",final.Fields["session_completed"]);
                    Check.Equal("Completed",final.Fields["end_reason"]);Check.Equal(20,rows.Count(x=>x.Kind=="rating"));
                    Check.Equal(104,rows.Where(x=>x.Kind=="trial_observation").Select(x=>x.Fields["trial_id"]).Distinct().Count());
                    Check.True(rows.Where(x=>x.Kind=="trial_observation").GroupBy(x=>x.Fields["trial_id"]).All(x=>x.Last().Fields["outcome"]=="Responded"));
                    Check.Equal(0,rows.Last().Fields.Count,"A recorder close marker must not manufacture study completion");
                }
            });
            suite.Run("M4 journal separates raw acquisition receiver acceptance and invalid raw numbers",()=>{
                var original=CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture=new CultureInfo("fr-FR");var clock=new ManualClock();var e=Fixtures.New(clock);var audit=new AuditLog("test-session",RunKind.Simulation);var sample=Operator();
                    using(var sourceBytes=new MemoryStream())using(var receiverBytes=new MemoryStream())using(var sourceJournal=Journal(sourceBytes,"operator"))using(var receiverJournal=Journal(receiverBytes))
                    {
                        var source=new SimulationJournalCapture(sourceJournal);var receiver=new SimulationJournalCapture(receiverJournal,e,audit);
                        Check.True(source.RecordAcquisition(sample));Check.Throws<ArgumentException>(()=>source.RecordAcquisition(sample));Check.Throws<ArgumentException>(()=>source.RecordAcquisition(Operator(1,"other-session")));
                        receiver.Capture();Check.True(!Records(receiverBytes).Any(x=>x.Kind=="operator_received_sample"));
                        audit.RecordOperatorSample(sample);receiver.Capture();receiver.Capture();
                        var acquired=Records(sourceBytes).Single(x=>x.Kind=="operator_acquired_sample");var received=Records(receiverBytes).Single(x=>x.Kind=="operator_received_sample");
                        Check.Equal(acquired.Fields["sample_sequence"],received.Fields["sample_sequence"]);Check.Equal("NaN",acquired.Fields["head_x"]);Check.Equal("Infinity",acquired.Fields["head_y"]);
                        Check.Equal("false",acquired.Fields["head_valid"]);Check.Equal("true",acquired.Fields["head_reported_valid"]);Check.Equal("1.1",acquired.Fields["send_local_seconds"]);
                        var missing=Records(receiverBytes).Single(x=>x.Kind=="participant_measurement_configuration");Check.Equal("false",missing.Fields["available"]);Check.Equal("no_independent_participant_measurement_stream_attached",missing.Fields["reason"]);
                    }
                }finally{CultureInfo.CurrentCulture=original;}
            });
            suite.Run("M4 journal persists clock transport presentation and independent gaze evidence",()=>{
                var clock=new ManualClock();var e=Fixtures.New(clock);var gaze=new ParticipantGazeLog("test-session",RunKind.Simulation,"simulator/participant","participant-device",ParticipantEyeSource.Simulator,"test-monotonic");
                var audit=new AuditLog("test-session",RunKind.Simulation,gaze);var sample=new ParticipantEyeSample("test-session","simulator/participant","participant-device","participant-clock",0,0,0,1,
                    ParticipantEyeSource.Simulator,StreamRole.ParticipantMeasurement,new Vec3(0,0,1),true);gaze.AddSample(sample);
                var window=new GazeObservationWindow(0,0,Condition.D,Phase.MainTask,1,2);gaze.AddWindow(window);gaze.AddInterval(new ParticipantGazeInterval(sample,window,"trial",1,2,ParticipantAoi.Task,"unvalidated-fixture-map","unvalidated-fixture-calibration"));
                var context=new AuditContext(0,0);audit.RecordClock(new ClockAuditRecord(context,"remote-clock","test-monotonic","fixture-map",2,.2,null,.01,"fixture\nnot hardware timing"));
                audit.RecordTransport(new TransportAuditRecord(context,ControlSource.Simulator,"simulator/operator",9,"test-monotonic",2,"packet_rejected",2,null,null,null,"fixture reason",4));
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),Rotation.Identity,new Vec3(1,0,0),"fixture-geometry");
                var controller=new AvatarController("test-session",RunKind.Simulation,0,0,ControlSelection.Automatic,Condition.A,GazeTarget.Participant,geometry,null);
                audit.RecordPresentation(new AvatarPresentationRecord(context,"test-monotonic",2,"headless_controller_evaluation_not_rendered",Phase.MainTask,controller.Render(Phase.MainTask,1)));
                using(var data=new MemoryStream())using(var journal=Journal(data))
                {
                    var capture=new SimulationJournalCapture(journal,e,audit);capture.Capture();long count=journal.LastWrittenSequence;capture.Capture();Check.Equal(count,journal.LastWrittenSequence);
                    var rows=Records(data);Check.Equal("4",rows.Single(x=>x.Kind=="transport_observation").Fields["source_sample_sequence"]);Check.Equal("9",rows.Single(x=>x.Kind=="transport_observation").Fields["packet_sequence"]);
                    Check.Equal("fixture\nnot hardware timing",rows.Single(x=>x.Kind=="clock_observation").Fields["evidence"]);
                    var presentation=rows.Single(x=>x.Kind=="avatar_presentation");Check.Equal(null,presentation.Fields["renderer_observed"]);Check.Equal(null,presentation.Fields["applied_head_w"]);
                    Check.Equal("participant-device",rows.Single(x=>x.Kind=="participant_sample").Fields["device_id"]);Check.Equal("D",rows.Single(x=>x.Kind=="gaze_window").Fields["condition"]);
                    Check.Equal("Task",rows.Single(x=>x.Kind=="gaze_interval").Fields["aoi"]);
                }
            });
            suite.Run("M4 journal evidence export preserves incomplete prefix and original bytes",()=>{
                string directory=Path.Combine(Path.GetTempPath(),"stroop-journal-evidence-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
                try
                {
                    string source=Path.Combine(directory,"source.asj");byte[] data;
                    using(var memory=new MemoryStream())using(var journal=Journal(memory))
                    {journal.TryAppend("fixture",SimulationJournalCapture.Fields("text","quoted\"line\n,next","empty","","missing",null));journal.Flush();data=memory.ToArray();}
                    File.WriteAllBytes(source,data);var result=JournalEvidenceExporter.Export(source,directory);Check.True(result.Success,result.Error);Check.Equal(JournalReadStatus.UncleanEnd,result.ReadResult.Status);
                    Check.True(data.SequenceEqual(File.ReadAllBytes(source)));Check.True(data.SequenceEqual(File.ReadAllBytes(Path.Combine(result.DirectoryPath,"verified-prefix.asj"))));
                    Check.True(File.Exists(Path.Combine(result.DirectoryPath,"manifest.csv")));Check.True(File.ReadAllText(Path.Combine(result.DirectoryPath,"fields.csv")).Contains("true,"));
                    var again=JournalEvidenceExporter.Export(source,directory);Check.True(again.Success);Check.True(result.DirectoryPath!=again.DirectoryPath);
                    var bad=JournalEvidenceExporter.Export(source,source);Check.True(!bad.Success);Check.True(bad.Error!=null);
                }
                finally{Directory.Delete(directory,true);}
            });
        }
    }
}
