using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Apps
{
    public static class SimulatorHost
    {
        public static int Main(string[] args)
        {
            try
            {
                if(args.Length==3&&args[0]=="journal-extract")
                {
                    var evidence=JournalEvidenceExporter.Export(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]));
                    if(!evidence.Success)throw new IOException(evidence.Error);
                    Report(Console.Out,"extraction_directory",evidence.DirectoryPath);Report(Console.Out,"journal_status",evidence.ReadResult.Status);
                    Report(Console.Out,"verified_bytes",evidence.ReadResult.VerifiedBytes);Report(Console.Out,"record_count",evidence.ReadResult.RecordCount);
                    return 0;
                }
                if(args.Length<6)throw new ArgumentException("Usage: operator|participant SESSION LOCAL_PORT REMOTE_PORT OUTPUT_DIRECTORY fault-script|safety-stop|normal [--journal] [--startup-barrier DIRECTORY] [--scenario-completion|--journal-kill-fixture]; or journal-extract JOURNAL_PATH OUTPUT_PARENT");
                string role=args[0],session=args[1],output=Path.GetFullPath(args[4]);
                bool recording=false,completion=false,killFixture=false;string barrier=null;long lastAcknowledged=-1;
                for(int i=6;i<args.Length;i++)
                {
                    if(args[i]=="--journal"&&!recording)recording=true;
                    else if(args[i]=="--scenario-completion"&&!completion)completion=true;
                    else if(args[i]=="--journal-kill-fixture"&&!killFixture)killFixture=true;
                    else if(args[i]=="--startup-barrier"&&barrier==null&&i+1<args.Length)barrier=Path.GetFullPath(args[++i]);
                    else throw new ArgumentException("Unknown, duplicate or incomplete simulation option: "+args[i]);
                }
                if(barrier!=null&&!Directory.Exists(barrier))throw new ArgumentException("Startup barrier directory must already exist");
                if(completion&&(barrier==null||(args[5]!="fault-script"&&args[5]!="safety-stop")))throw new ArgumentException("Scenario completion requires its startup barrier and a recognized integration fixture");
                if(killFixture&&(!recording||barrier==null||completion||args[5]!="normal"))throw new ArgumentException("Journal kill fixture requires --journal, its startup barrier, normal scenario and no completion claim");
                var provenance=new ExportProvenance("independent-process-headless-simulator","headless software acknowledgement; not a rendered display",
                    "synthetic timed first press edge",Environment.GetEnvironmentVariable("STROOP_BUILD_SOURCE_DIGEST"));
                int local=Int32.Parse(args[2],CultureInfo.InvariantCulture),remote=Int32.Parse(args[3],CultureInfo.InvariantCulture);
                Directory.CreateDirectory(output);
                Console.WriteLine("SIMULATION ONLY — independent "+role+" process; no real tracking or headset.");
                if(role=="operator")
                {
                    using(var app=new OperatorSimulationApplication(session,local,remote,args[5]=="fault-script",args[5]=="safety-stop"))
                    {
                        if(recording)app.EnableRecording(Path.Combine(output,"operator-journal.asj"),provenance,.1);
                        double activeStart=WaitForStartup(app,role,local,remote,barrier);
                        Action tick=()=>{app.Tick();CheckRecording(app);AcknowledgeRecording(app,role,app.CurrentTask==null?"None":app.CurrentTask.Phase.ToString(),app.AcquiredSamples.Count,ref lastAcknowledged);};
                        int postExitTicks=0;
                        if(killFixture)RunJournalKillFixture(app,activeStart,tick);
                        else if(completion)RunOperatorScenario(app,args[5],barrier,output,activeStart,tick,out postExitTicks);
                        else while(app.Clock.Now-activeStart<8){tick();Thread.Sleep(2);}
                        double activeElapsed=app.Clock.Now-activeStart;
                        app.CloseRecording();CheckRecording(app);
                        using(var writer=new StreamWriter(Path.Combine(output,"operator-report.tsv")))
                        {
                            Report(writer,"pid",Process.GetCurrentProcess().Id);Report(writer,"samples_sent",app.SamplesSent);
                            Report(writer,"acquired_samples",app.AcquiredSamples.Count);RecordingReport(writer,app);
                            StartupReport(writer,barrier,activeStart,activeElapsed);
                            CompletionReport(writer,completion,postExitTicks);
                            Report(writer,"samples_send_failed",app.SamplesSendFailed);Report(writer,"samples_suppressed",app.SamplesSuppressed);
                            Report(writer,"clock_mapping_available",app.Synchronizer.Snapshot().Available);
                            Report(writer,"clock_unavailable_reason",app.Synchronizer.Snapshot().UnavailableReason);
                            Report(writer,"clock_last_rejection",app.Synchronizer.LastRejection);
                            Report(writer,"stop_acknowledged",app.StopDelivery.Acknowledged);Report(writer,"stop_pending",app.StopDelivery.Pending);
                            foreach(var item in app.Monitor.Events)writer.WriteLine("fault\t"+item.Kind+"\t"+item.Detail);
                            foreach(var pair in app.ObservedTrials.OrderBy(x=>x.Key))writer.WriteLine("trial\t"+pair.Key+"\t"+pair.Value);
                        }
                        WriteClockEvidence(app,output,"operator");
                        using(var writer=new StreamWriter(Path.Combine(output,"operator-acquisition.tsv")))
                        {
                            writer.WriteLine("data_label\tsession_id\tstream_id\tdevice_id\tclock_id\tblock_index\tsegment\tsample_sequence\tcapture_seconds\tsend_seconds\thead_x\thead_y\thead_z\thead_w\teye_x\teye_y\teye_z\thead_reported_valid\teye_reported_valid");
                            foreach(var s in app.AcquiredSamples)writer.WriteLine(String.Join("\t",new object[]{"SIMULATION",s.SessionId,s.StreamId,s.DeviceId,s.ClockId,s.BlockIndex,s.Segment,s.Sequence,s.CaptureLocalSeconds,s.SendLocalSeconds,s.RawHeadRotation.X,s.RawHeadRotation.Y,s.RawHeadRotation.Z,s.RawHeadRotation.W,s.RawEyeDirection.X,s.RawEyeDirection.Y,s.RawEyeDirection.Z,s.HeadReportedValid,s.EyeReportedValid}.Select(x=>Convert.ToString(x,CultureInfo.InvariantCulture))));
                        }
                    }
                }
                else if(role=="participant")
                {
                    using(var app=new ParticipantSimulationApplication(session,local,remote))
                    {
                        if(recording)app.EnableRecording(Path.Combine(output,"participant-journal.asj"),provenance,.1);
                        double activeStart=WaitForStartup(app,role,local,remote,barrier);
                        Action tick=()=>{app.Tick();app.RecordControllerEvaluation();app.DriveHeadlessSimulation();CheckRecording(app);AcknowledgeRecording(app,role,app.Experiment.Phase.ToString(),0,ref lastAcknowledged);};
                        if(killFixture)RunJournalKillFixture(app,activeStart,tick);
                        else if(completion)RunParticipantScenario(app,args[5],barrier,output,activeStart,tick);
                        else while(app.Clock.Now-activeStart<7){tick();Thread.Sleep(2);}
                        double activeElapsed=app.Clock.Now-activeStart;
                        WriteClockEvidence(app,output,"participant");
                        var exported=app.Export(Path.Combine(output,"audit"),provenance);
                        if(!exported.Success)throw new IOException(exported.Error);
                        app.CloseRecording();CheckRecording(app);
                        using(var writer=new StreamWriter(Path.Combine(output,"participant-report.tsv")))
                        {
                            Report(writer,"pid",Process.GetCurrentProcess().Id);Report(writer,"phase",app.Experiment.Phase);
                            RecordingReport(writer,app);
                            StartupReport(writer,barrier,activeStart,activeElapsed);
                            CompletionReport(writer,completion,0);
                            Report(writer,"main_trials",app.Experiment.Trials.Count(x=>!x.IsPractice));Report(writer,"ratings",app.Experiment.Ratings.Count);
                            Report(writer,"interrupted_main_trials",app.Experiment.Trials.Count(x=>!x.IsPractice&&x.Outcome==TrialOutcome.Interrupted));
                            Report(writer,"applied_remote_stops",app.AppliedRemoteStops);Report(writer,"export_directory",exported.DirectoryPath);
                            Report(writer,"received_samples",app.Packets.Count(x=>x.Accepted&&x.Packet!=null&&x.Packet.Kind==WireKind.HeadEye));
                            Report(writer,"valid_presentations",app.Outputs.Count(x=>x.HeadValid&&x.EyeValid&&x.ActualSource==ControlSource.Simulator));
                            Report(writer,"automatic_fallbacks",app.Outputs.Count(x=>x.ActualSource==ControlSource.Automatic));
                            var map=app.Synchronizer.Snapshot();Report(writer,"clock_mapping_available",map.Available);Report(writer,"clock_drift_available",map.DriftAvailable);
                            Report(writer,"clock_unavailable_reason",map.UnavailableReason);Report(writer,"clock_last_rejection",app.Synchronizer.LastRejection);
                            Report(writer,"clock_uncertainty_seconds",map.UncertaintySeconds);Report(writer,"clock_offset_seconds",map.RemoteMinusLocalSeconds);
                            foreach(var item in app.Monitor.Events)writer.WriteLine("fault\t"+item.Kind+"\t"+item.Detail);
                            foreach(var trial in app.Experiment.Trials.Where(x=>!x.IsPractice))writer.WriteLine("trial\t"+trial.TrialId+"\t"+trial.Stimulus.Key);
                        }
                    }
                }
                else throw new ArgumentException("Role must be operator or participant");
                return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
        const double ScenarioFailSafeSeconds=65; // Engineering test budget, never a participant response deadline.
        static void RunJournalKillFixture(SimulationPeer app,double start,Action tick)
        {
            // Explicit test lifetime: the launcher must kill this process after
            // observing progress. A slow runner cannot produce a normal report.
            while(app.Clock.Now-start<ScenarioFailSafeSeconds){tick();Thread.Sleep(2);}
            throw new TimeoutException("Journal kill fixture was not terminated by its launcher before its engineering timeout");
        }
        static string Signal(string kind,SimulationPeer app,string scenario)
        {return kind+"\t1\t"+app.Identity.SessionId+"\t"+scenario+"\n";}
        static bool HasSignal(string directory,string name,string expected)
        {
            string path=Path.Combine(directory,name);if(!File.Exists(path))return false;
            if(File.ReadAllText(path)!=expected)throw new InvalidDataException("Scenario lifecycle signal has the wrong identity: "+name);
            return true;
        }
        static bool ParticipantGoalReached(ParticipantSimulationApplication app,string scenario)
        {
            var e=app.Experiment;
            if(scenario=="safety-stop")
            {if(e.Phase!=Phase.Paused||app.AppliedRemoteStops!=1||e.Trials.Count(x=>!x.IsPractice&&x.Outcome==TrialOutcome.Interrupted)!=1||e.Trials.Count(x=>!x.IsPractice)>=24)return false;}
            else if(e.Phase!=Phase.Rest||e.Trials.Count(x=>!x.IsPractice)!=24||e.Ratings.Count!=5)return false;
            if(!app.Synchronizer.Snapshot().Available||app.ClockExchanges.Count<3||
                app.Packets.Count(x=>x.Accepted&&x.Packet!=null&&x.Packet.Kind==WireKind.HeadEye)<=20||
                !app.Outputs.Any(x=>x.HeadValid&&x.EyeValid&&x.ActualSource==ControlSource.Simulator))return false;
            if(scenario=="safety-stop")return e.Phase==Phase.Paused&&app.AppliedRemoteStops==1&&
                e.Trials.Count(x=>!x.IsPractice&&x.Outcome==TrialOutcome.Interrupted)==1&&e.Trials.Count(x=>!x.IsPractice)<24;
            return e.Phase==Phase.Rest&&e.Trials.Count(x=>!x.IsPractice)==24&&e.Ratings.Count==5&&
                new[]{"HeadInvalid","EyeInvalid","ConnectionStale","RecoveryDecisionRequired"}.All(kind=>app.Monitor.Events.Any(x=>x.Kind=="fault_started"&&x.Detail==kind));
        }
        static void RunParticipantScenario(ParticipantSimulationApplication app,string scenario,string directory,string output,double start,Action tick)
        {
            bool goalWritten=false;
            while(true)
            {
                CheckScenarioDeadline(app,scenario,directory,output,start);
                tick();
                if(!goalWritten&&ParticipantGoalReached(app,scenario))
                {
                    string goal=Signal("PARTICIPANT_GOAL",app,scenario).TrimEnd('\n');
                    foreach(var trial in app.Experiment.Trials.Where(x=>!x.IsPractice))goal+="\ntrial\t"+trial.TrialId+"\t"+trial.Stimulus.Key;
                    WriteStartupRecord(Path.Combine(directory,"participant-goal.tsv"),goal);goalWritten=true;
                }
                if(goalWritten&&HasSignal(directory,"operator-complete.tsv",Signal("OPERATOR_COMPLETE",app,scenario)))return;
                Thread.Sleep(2);
            }
        }
        static bool OperatorGoalReached(OperatorSimulationApplication app,string scenario,string directory)
        {
            string path=Path.Combine(directory,"participant-goal.tsv");if(!File.Exists(path))return false;
            var lines=File.ReadAllLines(path);
            if(lines.Length==0||lines[0]!=Signal("PARTICIPANT_GOAL",app,scenario).TrimEnd('\n'))throw new InvalidDataException("Participant goal has the wrong scenario identity");
            var expected=new System.Collections.Generic.Dictionary<string,string>();
            foreach(string line in lines.Skip(1))
            {
                var fields=line.Split('\t');
                if(fields.Length!=3||fields[0]!="trial"||expected.ContainsKey(fields[1]))throw new InvalidDataException("Invalid participant goal trial correspondence");
                expected.Add(fields[1],fields[2]);
            }
            if(scenario=="fault-script"?expected.Count!=24:expected.Count==0||expected.Count>=24)throw new InvalidDataException("Participant goal has the wrong main-trial count");
            if(!app.Synchronizer.Snapshot().Available||app.ClockExchanges.Count<3||app.CurrentTask==null||
                app.CurrentTask.Phase!=(scenario=="fault-script"?Phase.Rest:Phase.Paused)||app.ObservedTrials.Count!=expected.Count)return false;
            foreach(var trial in expected){string observed;if(!app.ObservedTrials.TryGetValue(trial.Key,out observed)||observed!=trial.Value)return false;}
            return scenario=="fault-script"||(app.StopDelivery.Acknowledged&&!app.StopDelivery.Pending);
        }
        static void RunOperatorScenario(OperatorSimulationApplication app,string scenario,string directory,string output,double start,Action tick,out int postExitTicks)
        {
            bool completeWritten=false;postExitTicks=0;
            while(true)
            {
                CheckScenarioDeadline(app,scenario,directory,output,start);
                tick();
                if(!completeWritten&&OperatorGoalReached(app,scenario,directory))
                {WriteStartupRecord(Path.Combine(directory,"operator-complete.tsv"),Signal("OPERATOR_COMPLETE",app,scenario).TrimEnd('\n'));completeWritten=true;}
                if(HasSignal(directory,"participant-exited.tsv",Signal("PARTICIPANT_EXITED",app,scenario)))
                {
                    if(!completeWritten)throw new InvalidDataException("Participant exit arrived before acknowledged scenario completion");
                    // The launcher has reaped the participant. Exercise the existing
                    // transport lifecycle once after peer exit; do not reopen/retry it.
                    tick();postExitTicks=1;
                    WriteStartupRecord(Path.Combine(directory,"operator-post-exit.tsv"),Signal("OPERATOR_POST_EXIT_TICK",app,scenario).TrimEnd('\n'));return;
                }
                Thread.Sleep(2);
            }
        }
        static void CheckScenarioDeadline(SimulationPeer app,string scenario,string directory,string output,double start)
        {
            if(!HasSignal(directory,"timeout.tsv",Signal("SCENARIO_TIMEOUT",app,scenario))&&app.Clock.Now-start<ScenarioFailSafeSeconds)return;
            string role=app.Identity.Role==PeerRole.Participant?"participant":"operator";
            WriteClockEvidence(app,output,role);
            using(var writer=new StreamWriter(Path.Combine(output,role+"-timeout.tsv")))
            {
                Report(writer,"reason","integration_scenario_timeout");Report(writer,"active_elapsed_seconds",app.Clock.Now-start);
                var participant=app as ParticipantSimulationApplication;var op=app as OperatorSimulationApplication;
                if(participant!=null)
                {
                    Report(writer,"phase",participant.Experiment.Phase);Report(writer,"main_trials",participant.Experiment.Trials.Count(x=>!x.IsPractice));
                    Report(writer,"ratings",participant.Experiment.Ratings.Count);Report(writer,"selected_source",participant.Experiment.LockedControl);
                    Report(writer,"selected_source_ready",participant.Experiment.LockedControl.HasValue&&participant.SourceReady(participant.Experiment.LockedControl.Value));
                }
                if(op!=null){Report(writer,"phase",op.CurrentTask==null?"Unavailable":op.CurrentTask.Phase.ToString());Report(writer,"observed_trials",op.ObservedTrials.Count);Report(writer,"stop_acknowledged",op.StopDelivery.Acknowledged);}
                var faults=app.Monitor.Snapshot(app.Clock.Now,app.Synchronizer);var map=app.Synchronizer.Snapshot();
                Report(writer,"recovery_required",faults.RecoveryDecisionRequired);Report(writer,"head_usable",faults.HeadUsable);Report(writer,"eye_usable",faults.EyeUsable);
                Report(writer,"clock_available",map.Available);Report(writer,"clock_unavailable_reason",map.UnavailableReason);Report(writer,"accepted_packets",app.Packets.Count(x=>x.Accepted));
                foreach(var warning in faults.Warnings)writer.WriteLine("warning\t"+warning.Kind+"\t"+warning.Message);
                foreach(var fault in app.Monitor.Events.Skip(Math.Max(0,app.Monitor.Events.Count-64)))writer.WriteLine("fault\t"+fault.Kind+"\t"+fault.Detail);
            }
            throw new TimeoutException("Integration scenario did not reach its required evidence before the test deadline: "+role+" "+scenario);
        }
        static void CompletionReport(TextWriter writer,bool completion,int postExitTicks)
        {
            Report(writer,"scenario_completion_enabled",completion);Report(writer,"completion_reason",completion?"scenario_goal":"fixed_duration");
            Report(writer,"post_participant_exit_ticks",postExitTicks);
        }
        // Opt-in local process-test rendezvous. This is not a transport handshake,
        // connection retry or apparatus recovery procedure. The app is already bound.
        static double WaitForStartup(SimulationPeer app,string role,int local,int remote,string directory)
        {
            if(directory==null)return 0;
            string session=app.Identity.SessionId;
            string identity=String.Join("\t",new[]{"1",session,role,local.ToString(CultureInfo.InvariantCulture),remote.ToString(CultureInfo.InvariantCulture),Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)});
            WriteStartupRecord(Path.Combine(directory,role+"-ready.tsv"),"READY\t"+identity+"\t"+app.Clock.Now.ToString("R",CultureInfo.InvariantCulture));
            string release=Path.Combine(directory,"start.tsv");var waiting=Stopwatch.StartNew();
            while(!File.Exists(release))
            {
                if(waiting.Elapsed.TotalSeconds>=20)throw new TimeoutException("Startup barrier timed out waiting for launcher release: "+role+" session "+session);
                Thread.Sleep(2);
            }
            if(File.ReadAllText(release)!="START\t1\t"+session+"\n")throw new InvalidDataException("Startup barrier release has the wrong version or session");
            string other=role=="operator"?"participant":"operator";
            var peer=File.ReadAllText(Path.Combine(directory,other+"-ready.tsv")).TrimEnd('\n').Split('\t');
            int peerPid;double peerReady;
            if(peer.Length!=8||peer[0]!="READY"||peer[1]!="1"||peer[2]!=session||peer[3]!=other||
                peer[4]!=remote.ToString(CultureInfo.InvariantCulture)||peer[5]!=local.ToString(CultureInfo.InvariantCulture)||
                !Int32.TryParse(peer[6],NumberStyles.None,CultureInfo.InvariantCulture,out peerPid)||peerPid<=0||
                !Double.TryParse(peer[7],NumberStyles.Float,CultureInfo.InvariantCulture,out peerReady)||!WireCodec.Finite(peerReady)||peerReady<0)
                throw new InvalidDataException("Startup barrier peer readiness does not match this session and endpoint pair");
            double activeStart=app.Clock.Now;
            WriteStartupRecord(Path.Combine(directory,role+"-first-tick.tsv"),"FIRST_TICK\t"+identity+"\t"+activeStart.ToString("R",CultureInfo.InvariantCulture));
            return activeStart;
        }
        static void WriteStartupRecord(string path,string value)
        {
            string pending=path+".pending";
            using(var writer=new StreamWriter(new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None)))writer.Write(value+"\n");
            File.Move(pending,path);
        }
        static void StartupReport(TextWriter writer,string barrier,double activeStart,double activeElapsed)
        {
            Report(writer,"startup_barrier_enabled",barrier!=null);
            Report(writer,"active_started_local_seconds",activeStart.ToString("R",CultureInfo.InvariantCulture));
            Report(writer,"active_elapsed_seconds",activeElapsed.ToString("R",CultureInfo.InvariantCulture));
        }
        static void CheckRecording(SimulationPeer app)
        {if(app.RecordingWarning!=null)throw new IOException(app.RecordingWarning);}
        static void AcknowledgeRecording(SimulationPeer app,string role,string phase,int acquired,ref long previous)
        {
            var journal=app.Recording;if(journal==null||journal.IsFaulted||journal.LastFlushedSequence==previous)return;
            // Tick may have flushed before input changed the phase. Capture and
            // flush again so the phase/count in this acknowledgment is covered.
            app.CaptureRecording();
            if(!journal.TryAppend("runtime_ack_context",SimulationJournalCapture.Fields("phase",phase,"acquired_samples",acquired))||!app.CaptureRecording(true))
                throw new IOException(app.RecordingWarning??"Recording acknowledgment failed");
            previous=journal.LastFlushedSequence;
            Console.WriteLine(String.Join("\t",new[]{"JOURNAL_ACK",role,previous.ToString(CultureInfo.InvariantCulture),journal.LastFlushedOffset.ToString(CultureInfo.InvariantCulture),phase,acquired.ToString(CultureInfo.InvariantCulture)}));Console.Out.Flush();
        }
        static void RecordingReport(TextWriter writer,SimulationPeer app)
        {
            var journal=app.Recording;Report(writer,"journal_enabled",journal!=null);if(journal==null)return;
            Report(writer,"journal_session_id",journal.Identity.SessionId);Report(writer,"journal_build_source_digest",journal.Identity.Provenance.BuildSourceDigest);
            Report(writer,"journal_path",journal.Path);Report(writer,"journal_flushed_sequence",journal.LastFlushedSequence);Report(writer,"journal_flushed_offset",journal.LastFlushedOffset);
            Report(writer,"journal_error",journal.Error);
        }
        static void WriteClockEvidence(SimulationPeer app,string output,string role)
        {
            using(var writer=new StreamWriter(Path.Combine(output,role+"-clock-exchanges.tsv")))
            {
                writer.WriteLine("exchange_id\tlocal_clock\tremote_clock\tt0_local_sent\tt1_remote_received\tt2_remote_sent\tt3_local_received\taccepted\trejection\tmapping_available\tunavailable_reason\trevision\tsample_count\tminimum_rate\tmaximum_rate\tpoint_estimate_method");
                foreach(var x in app.ClockExchanges)
                {
                    var p=x.Reply;var m=x.Mapping;
                    writer.WriteLine(String.Join("\t",new object[]{p.ExchangeId,p.RequestClockId,p.Peer.ClockId,p.RequestSentSeconds.ToString("R",CultureInfo.InvariantCulture),p.RemoteReceivedSeconds.ToString("R",CultureInfo.InvariantCulture),p.RemoteSentSeconds.ToString("R",CultureInfo.InvariantCulture),x.ReceivedSeconds.ToString("R",CultureInfo.InvariantCulture),x.Accepted,x.Rejection,m.Available,m.UnavailableReason,m.Revision,m.SampleCount,m.MinimumLocalSecondsPerRemoteSecond,m.MaximumLocalSecondsPerRemoteSecond,m.PointEstimateMethod}.Select(v=>Convert.ToString(v,CultureInfo.InvariantCulture))));
                }
            }
        }
        static void Report(TextWriter writer,string name,object value)
        {writer.WriteLine(name+"\t"+Convert.ToString(value,CultureInfo.InvariantCulture));}
    }
}
