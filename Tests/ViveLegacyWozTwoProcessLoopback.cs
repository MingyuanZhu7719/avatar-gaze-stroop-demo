#if STROOP_TEST_BUILD
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Opt-in only. Ordinary memory Main never calls this harness.
    // Two owned child processes, inherited anonymous pipes, two exact sockets.
    // The coordinator's deadline bounds child pipe connection/read as well as
    // task execution. Only those exact child handles may be terminated.
    public static class ViveLegacyWozTwoProcessLoopback
    {
        const int ParticipantPort=45171,OperatorPort=45172;
        static IPEndPoint ParticipantEndpoint(){return new IPEndPoint(IPAddress.Loopback,ParticipantPort);}
        static IPEndPoint OperatorEndpoint(){return new IPEndPoint(IPAddress.Loopback,OperatorPort);}
        static void Require(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
        static Process StartChild(string role,AnonymousPipeServerStream input,AnonymousPipeServerStream output)
        {
            var info=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--role "+role+" "+input.GetClientHandleAsString()+" "+output.GetClientHandleAsString()){UseShellExecute=false,CreateNoWindow=true,
                WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
            var child=Process.Start(info);input.DisposeLocalCopyOfClientHandle();output.DisposeLocalCopyOfClientHandle();return child;
        }
        public static int Coordinate()
        {
            Process participant=null,op=null;var deadline=Stopwatch.StartNew();bool success=false,cleanupFailed=false;
            AnonymousPipeServerStream pi=null,po=null,oi=null,oo=null;Task relay=null;
            int result=1;
            try
            {
                pi=new AnonymousPipeServerStream(PipeDirection.Out,HandleInheritability.Inheritable);
                po=new AnonymousPipeServerStream(PipeDirection.In,HandleInheritability.Inheritable);
                participant=StartChild("participant",pi,po);
                oi=new AnonymousPipeServerStream(PipeDirection.Out,HandleInheritability.Inheritable);
                oo=new AnonymousPipeServerStream(PipeDirection.In,HandleInheritability.Inheritable);
                op=StartChild("operator",oi,oo);
                // Dedicated bounded relay, not stdout. The same 20-second
                // deadline and final pipe disposal cover blocked handshake I/O.
                relay=Task.Factory.StartNew(()=>Relay(pi,po,oi,oo));
                Require(relay.Wait(Math.Max(1,5000-(int)deadline.ElapsedMilliseconds)),"anonymous_handshake_timeout");
                while(deadline.Elapsed.TotalSeconds<20&&(!participant.HasExited||!op.HasExited))Thread.Sleep(10);
                Require(participant.HasExited&&op.HasExited,"owned_child_deadline");
                Require(participant.ExitCode==0&&op.ExitCode==0,"owned_child_failure");
                string p=participant.StandardOutput.ReadToEnd(),o=op.StandardOutput.ReadToEnd();
                Require(p.StartsWith("PASS socket participant ",StringComparison.Ordinal)&&o.StartsWith("PASS socket operator ",StringComparison.Ordinal),"missing_role_result");
                // Role children emit only fixed labels/numeric counts, no token,
                // exception messages, stacks, endpoint strings or pipe contents.
                Console.Write(p);Console.Write(o);success=true;
                Console.WriteLine("RESULT two_process_loopback passed; two local processes on one PC; synthetic observations only; not physical two-PC/HMD acceptance");
                result=0;
            }
            catch(Exception e){Console.WriteLine("FAIL two_process_loopback "+e.GetType().Name);}
            finally
            {
                foreach(var process in new[]{participant,op})if(process!=null)
                {
                    try
                    {if(!process.HasExited){process.Kill();process.WaitForExit(2000);}
                        if(!success)
                        {
                            Console.WriteLine("CHILD_RESULT exit="+(process.HasExited?process.ExitCode.ToString():"uncertain"));
                            if(process.HasExited)
                            {
                                string errors=process.StandardError.ReadToEnd();
                                foreach(string line in errors.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
                                    if(System.Text.RegularExpressions.Regex.IsMatch(line,@"^ROLE_FAILURE [A-Za-z0-9.]+ hresult=-?[0-9]+$"))Console.WriteLine(line);
                            }
                        }}
                    catch(Exception e){Console.WriteLine("CHILD_CLEANUP_UNCERTAIN "+e.GetType().Name);cleanupFailed=true;}
                    finally{try{process.Dispose();}catch(Exception e){Console.WriteLine("CHILD_HANDLE_CLOSE_UNCERTAIN "+e.GetType().Name);cleanupFailed=true;}}
                }
                foreach(var pipe in new[]{pi,po,oi,oo})if(pipe!=null)try{pipe.Dispose();}catch{cleanupFailed=true;}
                if(relay!=null&&!relay.IsCompleted)try{if(!relay.Wait(2000))cleanupFailed=true;}catch{cleanupFailed=true;}
            }
            return cleanupFailed?1:result;
        }
        static void Relay(Stream participantIn,Stream participantOut,Stream operatorIn,Stream operatorOut)
        {
            using(var pr=new BinaryReader(participantOut,Encoding.UTF8,true))using(var pw=new BinaryWriter(participantIn,Encoding.UTF8,true))
            using(var or=new BinaryReader(operatorOut,Encoding.UTF8,true))using(var ow=new BinaryWriter(operatorIn,Encoding.UTF8,true))
            {
                string clock=or.ReadString();Require(clock.Length<=128,"clock_length");pw.Write(clock);pw.Flush();
                int count=pr.ReadInt32();Require(count>0&&count<=2048,"token_length");char[] token=pr.ReadChars(count);
                try{Require(token.Length==count,"token_short");ow.Write(count);ow.Write(token);ow.Flush();}finally{Array.Clear(token,0,token.Length);}
                Require(or.ReadByte()==1,"operator_not_started");pw.Write((byte)1);pw.Flush();
            }
        }
        public static int Role(string role,string inputHandle,string outputHandle)
        {
            long handle;if(!Int64.TryParse(inputHandle,out handle)||handle<=0||!Int64.TryParse(outputHandle,out handle)||handle<=0)return 2;
            try
            {using(var input=new AnonymousPipeClientStream(PipeDirection.In,inputHandle))using(var output=new AnonymousPipeClientStream(PipeDirection.Out,outputHandle))
                return role=="participant"?Participant(input,output):role=="operator"?Operator(input,output):2;}
            catch(Exception e){Console.Error.WriteLine("ROLE_FAILURE "+e.GetType().Name+" hresult="+e.HResult);return 1;}
        }
        static void PumpUntil(ViveLegacyWozParticipantProcess process,Func<bool> condition,Stopwatch deadline)
        {
            while(deadline.Elapsed.TotalSeconds<12)
            {process.Tick();if(condition())return;Require(!process.Terminal,"participant_terminal");Thread.Sleep(10);}
            throw new TimeoutException();
        }
        static void SendPhase(ViveLegacyWozParticipantProcess process)
        {Require(process.PublishCurrentTask(),"publish");Require(process.ConfirmCurrentSource(),"confirm");Require(process.BeginPhasePreflight(),"challenge");}
        static int Participant(Stream input,Stream output)
        {
            var clock=new MonotonicClock("actual-participant-process-"+Guid.NewGuid().ToString("N"));
            var task=ViveLegacyWozProcessMemoryTests.Prepare(clock);var owner=task.CreateLegacyWozTaskOwner(clock);var deadline=Stopwatch.StartNew();
                using(var reader=new BinaryReader(input,Encoding.UTF8,true))using(var writer=new BinaryWriter(output,Encoding.UTF8,true))
                {
                    string operatorClock=reader.ReadString();
                    using(var offer=ViveLegacyWozProcessPairing.CreateSyntheticOffer(owner,"local-owner",operatorClock))
                    using(var process=ViveLegacyWozParticipantProcess.Create(offer,owner,ParticipantEndpoint(),OperatorEndpoint(),5))
                    {
                        char[] token=offer.RevealToken();try{writer.Write(token.Length);writer.Write(token);writer.Flush();}finally{Array.Clear(token,0,token.Length);}
                        Require(process.Start(),"participant_start");Require(reader.ReadByte()==1,"operator_not_started");
                        SendPhase(process);PumpUntil(process,()=>process.CurrentPreflight!=null,deadline);
                        Require(process.CurrentPreflight is ViveLegacyWozParticipantProcess.SocketEvidence&&process.CurrentPreflight.SyntheticObservationsOnly,"socket_origin");
                        Require(process.CurrentPreflight.Phase==Phase.BlockReady,"BlockReady");
                        Require(process.BeginCountdownForTest(task),"fixture_countdown");SendPhase(process);
                        PumpUntil(process,()=>process.CurrentPreflight!=null,deadline);Require(process.CurrentPreflight.Phase==Phase.Countdown,"Countdown");
                        Thread.Sleep(3010);task.Tick();Require(task.Phase==Phase.MainTask,"MainTask");SendPhase(process);
                        PumpUntil(process,()=>process.CurrentPose!=null,deadline);
                        Require(process.CurrentPose.ReceiptCapturedBySocket&&process.CurrentPose.SyntheticObservationsOnly&&!process.CurrentPose.IsExperimentAdmission,"pose_origin");
                        Require(process.RevokeSource(),"revoke");Require(task.Phase==Phase.Paused&&process.CurrentPose==null,"actual_pause");
                        PumpUntil(process,()=>process.SourceRevokeAcknowledged,deadline);
                        Console.WriteLine("PASS socket participant phases=3 pose=1 actual_pause=1 correlated_revoke_ack=1 socket_receipt=1 synthetic=1");return 0;
                    }
                }
        }
        static int Operator(Stream input,Stream output)
        {
            var clock=new MonotonicClock("actual-operator-process-"+Guid.NewGuid().ToString("N"));
            var observations=new ViveLegacyWozObservationOwner.SyntheticObservations(clock);var deadline=Stopwatch.StartNew();long sequence=0;int proofs=0,poses=0;
                using(var reader=new BinaryReader(input,Encoding.UTF8,true))using(var writer=new BinaryWriter(output,Encoding.UTF8,true))
                {
                    writer.Write(clock.ClockId);writer.Flush();int length=reader.ReadInt32();Require(length>0&&length<=2048,"token_length");
                    char[] token=reader.ReadChars(length);ViveLegacyWozProcessPairing pairing;
                    try{Require(token.Length==length,"token_short");pairing=ViveLegacyWozProcessPairing.Parse(token);}finally{Array.Clear(token,0,token.Length);}
                    using(pairing)using(var process=ViveLegacyWozOperatorProcess.Synthetic(pairing,clock,observations,OperatorEndpoint(),ParticipantEndpoint(),5,null))
                    {
                        Require(process.ConfirmLocalOperatorSource()&&process.Start(),"operator_start");writer.Write((byte)1);writer.Flush();
                        while(deadline.Elapsed.TotalSeconds<12)
                        {
                            process.Tick();if(process.Terminal)break;
                            sequence++;observations.Fresh(sequence,checked((int)sequence),checked((int)sequence));
                            if(process.PollPhasePreflight())proofs++;
                            // A distinct native fixture observation follows proof.
                            sequence++;observations.Fresh(sequence,checked((int)sequence),checked((int)sequence));
                            if(process.PollObservation())poses++;Thread.Sleep(10);
                        }
                        Require(process.Terminal&&process.SourceRevokeAcknowledged&&!process.CleanupUncertain&&proofs==3&&poses>=1,"operator_completion");
                        Console.WriteLine("PASS socket operator phases="+proofs+" poses="+poses+" source_closed_ack_sent=1 installed=0 synthetic=1");return 0;
                    }
                }
        }
    }
}
#endif
