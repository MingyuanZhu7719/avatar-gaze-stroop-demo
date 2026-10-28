using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class DataTests
    {
        static ExportProvenance Provenance() { return new ExportProvenance("regression-fixture","manual-clock presentation acknowledgement; not display-photon onset","manual-clock first button edge"); }
        static ParticipantGazeLog Gaze()
        { return new ParticipantGazeLog("test-session",RunKind.Simulation,"simulator/participant","participant-device",ParticipantEyeSource.Simulator,"test-monotonic"); }
        static ParticipantEyeSample Sample(long sequence,bool valid=true,int block=0,int segment=0,string session="test-session")
        { return new ParticipantEyeSample(session,"simulator/participant","participant-device","participant-clock",block,segment,sequence,sequence,ParticipantEyeSource.Simulator,StreamRole.ParticipantMeasurement,new Vec3(0,0,1),valid); }
        static HeadEyeSample Operator(long sequence=0,string session="test-session",StreamRole role=StreamRole.OperatorControl,string device="operator-device")
        { return new HeadEyeSample(session,"simulator/operator",device,"operator-clock",0,0,sequence,1,1.1,ControlSource.Simulator,role,Rotation.Identity,new Vec3(0,0,1),true,true); }
        static void Interval(ParticipantGazeLog log,GazeObservationWindow window,long sequence,double start,double end,ParticipantAoi aoi)
        {
            var sample=Sample(sequence,aoi != ParticipantAoi.Invalid,window.BlockIndex,window.Segment); log.AddSample(sample);
            log.AddInterval(new ParticipantGazeInterval(sample,window,"trial",start,end,aoi,"caller-map-v1","calibration-unvalidated-fixture"));
        }
        static string Temp() { string path=Path.Combine(Path.GetTempPath(),"stroop-data-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        static List<Dictionary<string,string>> Read(string directory,string file)
        {
            var rows=Parse(File.ReadAllText(Path.Combine(directory,file))); var output=new List<Dictionary<string,string>>();
            foreach(var row in rows.Skip(1))
            {
                Check.Equal(rows[0].Count,row.Count,"CSV column count in "+file);
                var map=new Dictionary<string,string>(); for(int i=0;i<row.Count;i++) map.Add(rows[0][i],row[i]); output.Add(map);
            }
            return output;
        }
        // Test reader supports embedded comma, quote, CR and LF; no Split(',') shortcut.
        static List<List<string>> Parse(string csv)
        {
            var rows=new List<List<string>>(); var row=new List<string>(); var cell=new StringBuilder(); bool quoted=false;
            for(int i=0;i<csv.Length;i++)
            {
                char ch=csv[i];
                if(ch == '"') { if(quoted && i+1<csv.Length && csv[i+1]=='"') { cell.Append('"'); i++; } else quoted=!quoted; }
                else if(!quoted && ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if(!quoted && (ch == '\r' || ch == '\n'))
                { if(ch=='\r' && i+1<csv.Length && csv[i+1]=='\n') i++; row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row=new List<string>(); }
                else cell.Append(ch);
            }
            Check.True(!quoted,"Unterminated CSV quoted field");
            if(cell.Length>0 || row.Count>0) {row.Add(cell.ToString()); rows.Add(row);}
            return rows;
        }
        public static void Register(TestSuite t)
        {
            t.Run("M3 clock audit exports revisions beyond retained window and same-count invalidation",()=>{
                string root=Temp();try {
                    var map=new ClockSynchronizer("local-clock","remote-clock",new DevelopmentClockConfiguration(3,.2,.2,2,200));
                    var peer=new PeerIdentity("test-session","operator",PeerRole.Operator,"simulator/operator",ControlSource.Simulator,"remote-clock");
                    var audit=new AuditLog("test-session",RunKind.Simulation);var recorder=new ClockAuditRecorder();
                    for(int i=0;i<80;i++) {
                        double start=i*.1;string id="exchange-"+i;
                        Check.True(map.RegisterRequest(id,start));
                        Check.True(map.ObserveReply(WirePacket.TimeReply(peer,i,start+.002,id,"local-clock",start,start+.001),start+.003));
                        Check.True(recorder.Record(map,audit,new AuditContext(0,0,null),start+.003));
                        Check.True(!recorder.Record(map,audit,new AuditContext(0,0,null),start+.004));
                    }
                    Check.Equal(64,map.Snapshot().SampleCount);Check.Equal(80L,map.Revision);Check.Equal(80,audit.Clocks.Count);
                    Check.Near(map.Snapshot().LocalReferenceSeconds,audit.Clocks.Last().ObservedReferenceSeconds);
                    Check.True(!map.RegisterRequest("rollback",0));Check.Equal(81L,map.Revision);Check.True(!map.Snapshot().Available);
                    Check.True(recorder.Record(map,audit,new AuditContext(0,0,null),8));
                    var result=AuditExporter.Export(Fixtures.New(new ManualClock()),audit,root,Provenance());Check.True(result.Success,result.Error);
                    var rows=Read(result.DirectoryPath,"clock_audit.csv");Check.Equal(81,rows.Count);
                    Check.True(rows.Last()["evidence"].Contains("revision=81;available=False;retained_samples=64"));
                    Check.True(rows[79]["evidence"].Contains("revision=80;available=True"));
                    Check.Equal("",rows.Last()["offset_seconds"]);
                }finally{Directory.Delete(root,true);}
            });
            t.Run("AC-07 CSV roundtrips quotes commas newlines and invariant numbers",()=>{
                string root=Temp(); var culture=CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture=new CultureInfo("fr-FR"); var clock=new ManualClock(); string id="session,\"alpha\"\r\nsecond line";
                    var e=new Experiment(new SessionConfiguration(id,123,TrialPlans.Allocate(1,123)[0],RunKind.Simulation,"en",1.25),clock);
                    e.RecordPreparation(Actor.Owner,"screening,check","reported \"pass\"\nnot verified"); e.BeginFamiliarization(Actor.Owner); e.BeginPractice(Actor.Owner);
                    e.MarkPresented(e.CurrentTrial.TrialId); clock.Advance(.125); var ink=e.CurrentTrial.Stimulus.Ink; e.ButtonChanged(ink,true);
                    var result=AuditExporter.Export(e,new AuditLog(id,RunKind.Simulation),root,Provenance()); Check.True(result.Success,result.Error);
                    var session=Read(result.DirectoryPath,"session.csv").Single(); Check.Equal(id,session["session_id"]); Check.Equal("1.25",session["countdown_seconds"]);
                    var field=Read(result.DirectoryPath,"event_fields.csv").Single(x=>x["field_name"]=="status"); Check.Equal("reported \"pass\"\nnot verified",field["field_value"]);
                    var trial=Read(result.DirectoryPath,"trials.csv").Single(x=>x["outcome"]=="Responded"); Check.Equal("0.125",trial["reaction_seconds"]); Check.Equal("true",trial["correct"]);
                }
                finally { CultureInfo.CurrentCulture=culture; Directory.Delete(root,true); }
            });
            t.Run("AC-07 partial export preserves pending interruption and actual list seed",()=>{
                string root=Temp(); try {
                    var clock=new ManualClock(); var e=Fixtures.New(clock,Condition.B); Fixtures.Practice(e,clock,8); Fixtures.Start(e,clock);
                    var trial=e.CurrentTrial; e.MarkPresented(trial.TrialId); clock.Advance(1.5); e.Pause(Actor.Operator);
                    var result=AuditExporter.Export(e,new AuditLog("test-session",RunKind.Simulation),root,Provenance()); Check.True(result.Success,result.Error); Check.True(!result.SessionCompleted);
                    var session=Read(result.DirectoryPath,"session.csv").Single(); Check.Equal("Paused",session["phase"]); Check.Equal("",session["end_reason"]); Check.Equal("false",session["session_completed"]);
                    var row=Read(result.DirectoryPath,"trials.csv").Single(x=>x["is_practice"]=="false"); Check.Equal(trial.ListId,row["list_id"]); Check.Equal("42",row["seed"]);
                    Check.Equal("Interrupted",row["outcome"]); Check.Equal("",row["correct"]); Check.Equal("",row["reaction_seconds"]); Check.Equal("",row["response"]); Check.Equal("true",row["stimulus_onset_recorded"]);
                    Check.Equal(0,Read(result.DirectoryPath,"ratings.csv").Count); Check.Equal(9,Read(result.DirectoryPath,"trials.csv").Count);
                    var e2=Fixtures.New(new ManualClock()); e2.BeginFamiliarization(Actor.Owner); e2.BeginPractice(Actor.Owner);
                    var pending=AuditExporter.Export(e2,new AuditLog("test-session",RunKind.Simulation),root,Provenance());
                    var p=Read(pending.DirectoryPath,"trials.csv").Single(); Check.Equal("Pending",p["outcome"]); Check.Equal("false",p["stimulus_onset_recorded"]); Check.Equal("",p["onset_seconds"]);
                } finally {Directory.Delete(root,true);}
            });
            t.Run("AC-07 export unique no overwrite final manifest hashes every table",()=>{
                string root=Temp(); try {
                    var e=Fixtures.New(new ManualClock()); var audit=new AuditLog("test-session",RunKind.Simulation);
                    var first=AuditExporter.Export(e,audit,root,Provenance()); Check.True(first.Success,first.Error);
                    byte[] original=File.ReadAllBytes(Path.Combine(first.DirectoryPath,"session.csv"));
                    var second=AuditExporter.Export(e,audit,root,Provenance()); Check.True(second.Success,second.Error); Check.True(first.DirectoryPath != second.DirectoryPath);
                    Check.True(original.SequenceEqual(File.ReadAllBytes(Path.Combine(first.DirectoryPath,"session.csv"))));
                    var manifest=Read(first.DirectoryPath,"manifest.csv"); Check.Equal(Directory.GetFiles(first.DirectoryPath,"*.csv").Length-1,manifest.Count);
                    Check.Equal(0,Directory.GetFiles(first.DirectoryPath,"*.partial").Length);
                    foreach(var file in manifest) {
                        byte[] bytes=File.ReadAllBytes(Path.Combine(first.DirectoryPath,file["file_name"])); string hash;
                        using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
                        Check.Equal(hash,file["sha256"]); Check.Equal("true",file["complete_bundle"]); Check.Equal("false",file["session_completed"]); Check.Equal("SIMULATION",file["data_label"]);
                        Check.Equal(Read(first.DirectoryPath,file["file_name"]).Count.ToString(CultureInfo.InvariantCulture),file["row_count"]);
                    }
                } finally {Directory.Delete(root,true);}
            });
            t.Run("AC-07 file system failure returns explicit failure without completion",()=>{
                string root=Temp(); try {
                    string blocked=Path.Combine(root,"parent-is-file"); File.WriteAllText(blocked,"retain me");
                    var result=AuditExporter.Export(Fixtures.New(new ManualClock()),new AuditLog("test-session",RunKind.Simulation),blocked,Provenance());
                    Check.True(!result.Success); Check.True(!String.IsNullOrWhiteSpace(result.Error)); Check.Equal("retain me",File.ReadAllText(blocked)); Check.Equal(0,Directory.GetDirectories(root).Length);
                } finally {Directory.Delete(root,true);}
            });
            t.Run("AC-07 termination is not successful study completion",()=>{
                string root=Temp(); try {
                    var e=Fixtures.New(new ManualClock()); e.Withdraw();
                    var result=AuditExporter.Export(e,new AuditLog("test-session",RunKind.Simulation),root,Provenance());
                    Check.True(result.Success); Check.True(!result.SessionCompleted); var row=Read(result.DirectoryPath,"session.csv").Single();
                    Check.Equal("Withdrawn",row["end_reason"]); Check.Equal("false",row["session_completed"]);
                } finally {Directory.Delete(root,true);}
            });
            t.Run("AC-07 complete study has exact completion and separate rating rows",()=>{
                string root=Temp(); try {
                    var clock=new ManualClock();var e=Fixtures.New(clock);Fixtures.Practice(e,clock,8);
                    for(int b=0;b<4;b++){Fixtures.Start(e,clock);for(int i=0;i<24;i++)Fixtures.Answer(e,clock);Fixtures.Ratings(e,clock);clock.Advance(30);Check.True(e.Ready());}
                    var result=AuditExporter.Export(e,new AuditLog("test-session",RunKind.Simulation),root,Provenance()); Check.True(result.Success);Check.True(result.SessionCompleted);
                    Check.Equal(104,Read(result.DirectoryPath,"trials.csv").Count);Check.Equal(20,Read(result.DirectoryPath,"ratings.csv").Count);
                    Check.Equal("Completed",Read(result.DirectoryPath,"session.csv").Single()["end_reason"]);
                } finally {Directory.Delete(root,true);}
            });
            t.Run("AC-07 audit rejects mixed sessions duplicate sample IDs and shared device",()=>{
                var log=new AuditLog("test-session",RunKind.Simulation,Gaze()); log.RecordOperatorSample(Operator());
                Check.Throws<ArgumentException>(()=>log.RecordOperatorSample(Operator()));
                Check.Throws<ArgumentException>(()=>log.RecordOperatorSample(Operator(1,"other-session")));
                Check.Throws<ArgumentException>(()=>log.RecordOperatorSample(Operator(1,"test-session",StreamRole.ParticipantMeasurement)));
                Check.Throws<ArgumentException>(()=>log.RecordOperatorSample(Operator(1,"test-session",StreamRole.OperatorControl,"participant-device")));
                Check.Throws<ArgumentException>(()=>log.RecordOperatorSample(Operator(1,"test-session",StreamRole.OperatorControl,"operator-device-2")));
                Check.Throws<ArgumentException>(()=>new AuditLog("other-session",RunKind.Simulation,Gaze()));
                var real=new AuditLog("apparatus",RunKind.Apparatus); Check.Throws<ArgumentException>(()=>real.RecordOperatorSample(Operator(1,"apparatus")));
                Check.Throws<ArgumentException>(()=>new ParticipantGazeLog("x",RunKind.Apparatus,"simulator/participant","p",ParticipantEyeSource.Simulator,"clock"));
            });
            t.Run("AC-07 participant role source and sample identities are independent",()=>{
                Check.Throws<ArgumentException>(()=>new ParticipantEyeSample("s","simulator/p","p","clock",0,0,0,0,ParticipantEyeSource.Simulator,StreamRole.OperatorControl,new Vec3(0,0,1),true));
                var gaze=Gaze();var sample=Sample(0);gaze.AddSample(sample);
                Check.Throws<ArgumentException>(()=>gaze.AddSample(Sample(0)));Check.Throws<ArgumentException>(()=>gaze.AddSample(Sample(1,true,0,0,"other")));
                Check.Throws<ArgumentException>(()=>gaze.AddSample(new ParticipantEyeSample("test-session","simulator/participant","participant-device","changed-clock",0,0,1,1,ParticipantEyeSource.Simulator,StreamRole.ParticipantMeasurement,new Vec3(0,0,1),true)));
                var actual=new ParticipantEyeSample("s","participant/device","p","clock",0,0,0,0,ParticipantEyeSource.RealDevice,StreamRole.ParticipantMeasurement,new Vec3(0,0,1),true);
                Check.Equal(ParticipantEyeSource.RealDevice,actual.Source);Check.Equal(StreamRole.ParticipantMeasurement,actual.Role);
            });
            t.Run("AC-07 valid coverage and exclusive AOI denominators exclude inactive phases",()=>{
                var gaze=Gaze();var active=new GazeObservationWindow(0,0,Condition.A,Phase.MainTask,0,10);gaze.AddWindow(active);
                Interval(gaze,active,0,0,3,ParticipantAoi.Task);Interval(gaze,active,1,3,5,ParticipantAoi.Eyes);Interval(gaze,active,2,5,6,ParticipantAoi.RemainingFace);
                Interval(gaze,active,3,6,7,ParticipantAoi.Other);Interval(gaze,active,4,7,9,ParticipantAoi.Invalid);
                var pause=new GazeObservationWindow(0,0,Condition.A,Phase.Paused,10,30);gaze.AddWindow(pause);Interval(gaze,pause,5,10,30,ParticipantAoi.Other);
                var summary=gaze.Summaries().Single();Check.Near(10,summary.ActiveSeconds);Check.Near(7,summary.ValidSeconds);Check.Near(2,summary.InvalidSeconds);Check.Near(1,summary.UnobservedSeconds);
                Check.Near(.7,summary.ValidCoverage.Value);Check.Near(3.0/7,summary.TaskProportion.Value);Check.Near(1,summary.OtherSeconds);Check.Near(3,summary.CombinedFaceSeconds.Value);
                Check.Near(summary.ValidSeconds,summary.TaskSeconds+summary.OtherSeconds+summary.EyesSeconds.Value+summary.RemainingFaceSeconds.Value);
            });
            t.Run("AC-07 invalid is not Other and D eye face outcomes are structurally NA",()=>{
                var gaze=Gaze();var d=new GazeObservationWindow(0,0,Condition.D,Phase.MainTask,0,4);gaze.AddWindow(d);
                var invalid=new ParticipantEyeSample("test-session","simulator/participant","participant-device","participant-clock",0,0,0,0,ParticipantEyeSource.Simulator,StreamRole.ParticipantMeasurement,new Vec3(0,0,0),true);
                Check.True(!invalid.EyeValid);gaze.AddSample(invalid);
                Check.Throws<ArgumentException>(()=>new ParticipantGazeInterval(invalid,d,null,0,1,ParticipantAoi.Other,"map","cal"));
                gaze.AddInterval(new ParticipantGazeInterval(invalid,d,null,0,1,ParticipantAoi.Invalid,"map","cal"));
                var valid=Sample(1);gaze.AddSample(valid);
                Check.Throws<ArgumentException>(()=>new ParticipantGazeInterval(valid,d,null,1,2,ParticipantAoi.Eyes,"map","cal"));
                Check.Throws<ArgumentException>(()=>new ParticipantGazeInterval(valid,d,null,1,2,ParticipantAoi.RemainingFace,"map","cal"));
                Check.Throws<ArgumentException>(()=>new ParticipantGazeInterval(valid,d,null,1,2,ParticipantAoi.NotApplicable,"map","cal"));
                gaze.AddInterval(new ParticipantGazeInterval(valid,d,null,1,3,ParticipantAoi.Task,"map","cal"));
                var s=gaze.Summaries().Single();Check.Equal(null,s.EyesSeconds);Check.Equal(null,s.RemainingFaceSeconds);Check.Equal(null,s.CombinedFaceProportion);Check.True(!s.AvatarAoisApplicable);
                Check.Near(1,s.TaskProportion.Value);Check.Near(.5,s.ValidCoverage.Value);Check.Near(1,s.InvalidSeconds);
            });
            t.Run("AC-07 interval window overlap and cross-context classification rejected",()=>{
                var gaze=Gaze();var w=new GazeObservationWindow(0,0,Condition.A,Phase.MainTask,0,10);gaze.AddWindow(w);Interval(gaze,w,0,0,2,ParticipantAoi.Task);
                Check.Throws<ArgumentException>(()=>gaze.AddWindow(new GazeObservationWindow(0,0,Condition.A,Phase.Paused,9,12)));
                var sample=Sample(1);gaze.AddSample(sample);Check.Throws<ArgumentException>(()=>gaze.AddInterval(new ParticipantGazeInterval(sample,w,null,1,3,ParticipantAoi.Other,"map","cal")));
                Check.Throws<ArgumentException>(()=>new ParticipantGazeInterval(sample,w,null,9,11,ParticipantAoi.Task,"map","cal"));
                Check.Throws<ArgumentException>(()=>new ParticipantGazeInterval(Sample(2,true,1),w,null,2,3,ParticipantAoi.Task,"map","cal"));
                Check.Throws<ArgumentException>(()=>gaze.AddInterval(new ParticipantGazeInterval(Sample(3),w,null,2,3,ParticipantAoi.Task,"map","cal")));
                var empty=Gaze();empty.AddWindow(new GazeObservationWindow(0,0,Condition.A,Phase.MainTask,0,2));var s=empty.Summaries().Single();Check.Equal(null,s.TaskProportion);Check.Near(0,s.ValidCoverage.Value);Check.Near(2,s.UnobservedSeconds);
            });
            t.Run("AC-07 raw streams presentations clock transport and gaze export stay partitioned",()=>{
                string root=Temp();try {
                    var gaze=Gaze();var window=new GazeObservationWindow(0,0,Condition.D,Phase.MainTask,0,2);gaze.AddWindow(window);Interval(gaze,window,0,0,1,ParticipantAoi.Task);
                    var audit=new AuditLog("test-session",RunKind.Simulation,gaze);audit.RecordOperatorSample(Operator());
                    var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),Rotation.Identity,new Vec3(1,0,0),"fixture-geometry");
                    var controller=new AvatarController("test-session",RunKind.Simulation,0,0,ControlSelection.Automatic,Condition.D,GazeTarget.Participant,geometry);
                    var context=new AuditContext(0,0);audit.RecordPresentation(new AvatarPresentationRecord(context,"render-clock",2,"software proxy",Phase.MainTask,controller.Render(Phase.MainTask,1)));
                    audit.RecordClock(new ClockAuditRecord(context,"operator-clock","test-monotonic","clock-fixture-v1",2,null,null,null,"no estimate available"));
                    audit.RecordTransport(new TransportAuditRecord(context,ControlSource.Simulator,"simulator/operator",0,"receiver-clock",1.5,"out_of_order",null,null,null,null,"arrival fixture",42));
                    var result=AuditExporter.Export(Fixtures.New(new ManualClock(),Condition.D),audit,root,Provenance());Check.True(result.Success,result.Error);
                    Check.Equal("OperatorControl",Read(result.DirectoryPath,"operator_samples.csv").Single()["role"]);Check.Equal("ParticipantMeasurement",Read(result.DirectoryPath,"participant_samples.csv").Single()["role"]);
                    var summary=Read(result.DirectoryPath,"gaze_summary.csv").Single();Check.Equal("",summary["eyes_seconds"]);Check.Equal("NotApplicable",summary["avatar_aoi_applicability"]);Check.Equal("valid_active_task_seconds",summary["proportion_denominator"]);
                    var p=Read(result.DirectoryPath,"avatar_presentations.csv").Single();Check.Equal("false",p["avatar_visible"]);Check.Equal("",p["mapped_eye_x"]);Check.Equal("",p["renderer_observed"]);Check.Equal("software proxy",p["timestamp_semantics"]);
                    Check.Equal("",Read(result.DirectoryPath,"clock_audit.csv").Single()["offset_seconds"]);var transport=Read(result.DirectoryPath,"transport_audit.csv").Single();Check.Equal("",transport["latency_seconds"]);Check.Equal("0",transport["packet_sequence"]);Check.Equal("42",transport["source_sample_sequence"]);
                    foreach(var file in Directory.GetFiles(result.DirectoryPath,"*.csv"))foreach(var row in Read(result.DirectoryPath,Path.GetFileName(file))) {Check.Equal("Simulation",row["run_kind"]);Check.Equal("SIMULATION",row["data_label"]);}
                } finally {Directory.Delete(root,true);}
            });
            t.Run("AC-07 sample epoch and actual held renderer pose remain distinct",()=>{
                string root=Temp();try {
                    var audit=new AuditLog("test-session",RunKind.Simulation);var first=Operator();audit.RecordOperatorSample(first);
                    var second=new HeadEyeSample(first.SessionId,first.StreamId,first.DeviceId,first.ClockId,1,0,0,2,2.1,first.Source,first.Role,first.RawHeadRotation,first.RawEyeDirection,true,true);
                    audit.RecordOperatorSample(second);Check.Throws<ArgumentException>(()=>audit.RecordOperatorSample(second));
                    var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),Rotation.Identity,new Vec3(1,0,0),"fixture");
                    var controller=new AvatarController("test-session",RunKind.Simulation,0,0,ControlSelection.SimulatorRehearsal,Condition.A,GazeTarget.Participant,geometry,"simulator/operator");
                    var output=controller.RenderValidated(Phase.MainTask,1,first,false,true);var applied=new AvatarPose(new Rotation(0,1,0,0),new Vec3(0,0,1));
                    audit.RecordPresentation(new AvatarPresentationRecord(new AuditContext(0,0),"render",3,"camera proxy",Phase.MainTask,output,"held_head_development_policy",applied,true,true,false));
                    var export=AuditExporter.Export(Fixtures.New(new ManualClock()),audit,root,Provenance());Check.True(export.Success,export.Error);
                    var row=Read(export.DirectoryPath,"avatar_presentations.csv").Single();Check.Equal("0",row["mapped_head_y"]);Check.Equal("1",row["applied_head_y"]);
                    Check.Equal("false",row["head_valid"]);Check.Equal("true",row["renderer_head_held"]);Check.Equal("true",row["renderer_observed"]);
                }finally{Directory.Delete(root,true);}
            });
        }
    }
}
