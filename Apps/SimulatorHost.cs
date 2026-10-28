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
                if(args.Length!=6)throw new ArgumentException("Usage: operator|participant SESSION LOCAL_PORT REMOTE_PORT OUTPUT_DIRECTORY fault-script|safety-stop|normal");
                string role=args[0],session=args[1],output=Path.GetFullPath(args[4]);
                int local=Int32.Parse(args[2],CultureInfo.InvariantCulture),remote=Int32.Parse(args[3],CultureInfo.InvariantCulture);
                Directory.CreateDirectory(output);
                Console.WriteLine("SIMULATION ONLY — independent "+role+" process; no real tracking or headset.");
                if(role=="operator")
                {
                    using(var app=new OperatorSimulationApplication(session,local,remote,args[5]=="fault-script",args[5]=="safety-stop"))
                    {
                        while(app.Clock.Now<8){app.Tick();Thread.Sleep(2);}
                        using(var writer=new StreamWriter(Path.Combine(output,"operator-report.tsv")))
                        {
                            Report(writer,"pid",Process.GetCurrentProcess().Id);Report(writer,"samples_sent",app.SamplesSent);
                            Report(writer,"clock_mapping_available",app.Synchronizer.Snapshot().Available);
                            Report(writer,"clock_unavailable_reason",app.Synchronizer.Snapshot().UnavailableReason);
                            Report(writer,"clock_last_rejection",app.Synchronizer.LastRejection);
                            Report(writer,"stop_acknowledged",app.StopDelivery.Acknowledged);Report(writer,"stop_pending",app.StopDelivery.Pending);
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
                        while(app.Clock.Now<7){app.Tick();app.RecordControllerEvaluation();app.DriveHeadlessSimulation();Thread.Sleep(2);}
                        WriteClockEvidence(app,output,"participant");
                        var exported=app.Export(Path.Combine(output,"audit"),new ExportProvenance("independent-process-headless-simulator",
                            "headless software acknowledgement; not a rendered display","synthetic timed first press edge",Environment.GetEnvironmentVariable("STROOP_BUILD_SOURCE_DIGEST")));
                        if(!exported.Success)throw new IOException(exported.Error);
                        using(var writer=new StreamWriter(Path.Combine(output,"participant-report.tsv")))
                        {
                            Report(writer,"pid",Process.GetCurrentProcess().Id);Report(writer,"phase",app.Experiment.Phase);
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
