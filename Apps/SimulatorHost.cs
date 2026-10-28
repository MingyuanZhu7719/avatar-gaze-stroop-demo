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
                if(args.Length!=6&&(args.Length!=8||args[6]!="--startup-barrier"))
                    throw new ArgumentException("Usage: operator|participant SESSION LOCAL_PORT REMOTE_PORT OUTPUT_DIRECTORY fault-script|safety-stop|normal [--startup-barrier DIRECTORY]");
                string role=args[0],session=args[1],output=Path.GetFullPath(args[4]);
                string barrier=args.Length==8?Path.GetFullPath(args[7]):null;
                if(barrier!=null&&!Directory.Exists(barrier))throw new ArgumentException("Startup barrier directory must already exist");
                int local=Int32.Parse(args[2],CultureInfo.InvariantCulture),remote=Int32.Parse(args[3],CultureInfo.InvariantCulture);
                Directory.CreateDirectory(output);
                Console.WriteLine("SIMULATION ONLY — independent "+role+" process; no real tracking or headset.");
                if(role=="operator")
                {
                    using(var app=new OperatorSimulationApplication(session,local,remote,args[5]=="fault-script",args[5]=="safety-stop"))
                    {
                        double activeStart=WaitForStartup(app,role,local,remote,barrier);
                        while(app.Clock.Now-activeStart<8){app.Tick();Thread.Sleep(2);}
                        double activeElapsed=app.Clock.Now-activeStart;
                        using(var writer=new StreamWriter(Path.Combine(output,"operator-report.tsv")))
                        {
                            Report(writer,"pid",Process.GetCurrentProcess().Id);Report(writer,"samples_sent",app.SamplesSent);
                            StartupReport(writer,barrier,activeStart,activeElapsed);
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
                        double activeStart=WaitForStartup(app,role,local,remote,barrier);
                        while(app.Clock.Now-activeStart<7){app.Tick();app.RecordControllerEvaluation();app.DriveHeadlessSimulation();Thread.Sleep(2);}
                        double activeElapsed=app.Clock.Now-activeStart;
                        WriteClockEvidence(app,output,"participant");
                        var exported=app.Export(Path.Combine(output,"audit"),new ExportProvenance("independent-process-headless-simulator",
                            "headless software acknowledgement; not a rendered display","synthetic timed first press edge",Environment.GetEnvironmentVariable("STROOP_BUILD_SOURCE_DIGEST")));
                        if(!exported.Success)throw new IOException(exported.Error);
                        using(var writer=new StreamWriter(Path.Combine(output,"participant-report.tsv")))
                        {
                            Report(writer,"pid",Process.GetCurrentProcess().Id);Report(writer,"phase",app.Experiment.Phase);
                            StartupReport(writer,barrier,activeStart,activeElapsed);
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
