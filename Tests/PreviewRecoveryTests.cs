using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic peers and temporary recordings only. These checks do not open
    // players, use a headset, or change a running participant session.
    public static class PreviewRecoveryTests
    {
        const string Request="0123456789abcdef0123456789abcdef";
        sealed class FakeSocket : IUdpSocket
        {
            readonly int port;
            public FakeSocket(int port){this.port=port;}
            public IPEndPoint LocalEndpoint {get{return new IPEndPoint(IPAddress.Loopback,port);}}
            public bool Poll(){return false;}
            public byte[] Receive(ref IPEndPoint sender){throw new InvalidOperationException("No synthetic incoming packet");}
            public void Send(byte[] bytes,IPEndPoint remote){}
            public void Dispose(){}
            public UdpPeer Peer(){return new UdpPeer(LocalEndpoint,new IPEndPoint(IPAddress.Loopback,port==41001?41002:41001),false,this);}
        }
        sealed class FailingWriteStream : MemoryStream
        {
            public bool Fail;
            public override void Write(byte[] bytes,int offset,int count)
            {if(Fail)throw new IOException("synthetic intent write failure");base.Write(bytes,offset,count);}
        }
        sealed class FailingDisposeStream : FileStream
        {
            public FailingDisposeStream(string path):base(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read){}
            protected override void Dispose(bool disposing)
            {base.Dispose(disposing);if(disposing)throw new IOException("synthetic file close failure");}
        }
        static ParticipantSimulationApplication Participant(string session)
        {return new ParticipantSimulationApplication(session,41001,41002,null,new FakeSocket(41001).Peer());}
        static OperatorSimulationApplication Operator(string session)
        {return new OperatorSimulationApplication(session,41002,41001,false,false,new FakeSocket(41002).Peer());}
        static ExportProvenance Provenance()
        {return new ExportProvenance("preview-recovery-regression","synthetic render witness","synthetic input");}
        static string NewSession(){return "quest-preview-"+Guid.NewGuid().ToString("N");}
        static void Temporary(Action<string> action)
        {
            string tempRoot=Path.GetFullPath(Path.GetTempPath());
            string directory=Path.Combine(tempRoot,"stroop-preview-recovery-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try{action(directory);}
            finally
            {
                string resolved=Path.GetFullPath(directory);
                Check.True(resolved.StartsWith(tempRoot.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&
                    Path.GetFileName(resolved).StartsWith("stroop-preview-recovery-",StringComparison.Ordinal),"Refuse cleanup outside this test's temporary directory");
                if(Directory.Exists(resolved))Directory.Delete(resolved,true);
            }
        }
        static void ExpectFailure(Action action)
        {bool failed=false;try{action();}catch(Exception){failed=true;}Check.True(failed,"Expected recovery to fail closed");}
        static void Answer(Experiment e,bool correct)
        {
            var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);
            var response=correct?trial.Stimulus.Ink:(Colour)(((int)trial.Stimulus.Ink+1)%4);
            Check.True(e.ButtonChanged(response,true));e.ButtonChanged(response,false);
            if(e.Phase==Phase.PracticeCorrection)e.ContinuePractice();
        }
        static void Reach(ParticipantSimulationApplication app,Phase target)
        {
            var e=app.Experiment;
            if(target==Phase.Preparation)return;
            e.BeginFamiliarization(Actor.Owner);if(target==Phase.Familiarization)return;
            app.OwnerBeginPractice();if(target==Phase.Practice)return;
            if(target==Phase.PracticeCorrection)
            {
                var wrong=(Colour)(((int)e.CurrentTrial.Stimulus.Ink+1)%4);
                e.MarkPresented(e.CurrentTrial.TrialId);e.ButtonChanged(wrong,true);e.ButtonChanged(wrong,false);return;
            }
            for(int i=0;i<8;i++)Answer(e,target!=Phase.PracticeRetry);
            if(target==Phase.PracticeRetry||target==Phase.BlockPreparation)return;
            app.OwnerLockNextBlock(ControlSelection.Automatic);if(target==Phase.BlockReady)return;
            app.OwnerBeginBlock();if(target==Phase.Countdown)return;
            // Only the existing one-second development countdown needs time.
            // No network or display timing claim follows from this wait.
            Thread.Sleep(1050);e.Tick();Check.Equal(Phase.MainTask,e.Phase);
            if(target==Phase.MainTask)return;
            if(target==Phase.Paused){e.Pause(Actor.Participant);return;}
            for(int i=0;i<24;i++)Answer(e,true);
            if(target==Phase.Questionnaire)return;
            for(int i=0;i<5;i++)e.Rate(3);
            Check.Equal(Phase.Rest,target);
        }
        static List<JournalRecord> Read(string path,out JournalReadResult result)
        {
            var records=new List<JournalRecord>();
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))result=JournalPrefixReader.Read(stream,records.Add);
            return records;
        }
        static void ClosedPair(string directory,string session,string request=Request)
        {
            using(var participant=Participant(session))using(var operatorApp=Operator(session))
            {
                participant.EnableRecording(Path.Combine(directory,"participant.journal"),Provenance(),10);
                operatorApp.EnableRecording(Path.Combine(directory,"operator.journal"),Provenance(),10);
                participant.OwnerEndForRestart(request);
            }
        }
        static Dictionary<string,byte[]> Snapshot(string directory)
        {return Directory.GetFiles(directory,"*",SearchOption.AllDirectories).ToDictionary(x=>x,File.ReadAllBytes,StringComparer.Ordinal);}
        static void RejectedWithoutMutation(string directory,string session,string request)
        {
            var before=Snapshot(directory);string reason;
            Check.True(!PreviewRecovery.VerifyClosedSession(directory,session,request,out reason));
            Check.True(!String.IsNullOrWhiteSpace(reason),"Failed verification needs an actionable reason");
            var after=Snapshot(directory);Check.Equal(before.Count,after.Count);
            foreach(var item in before)Check.True(after.ContainsKey(item.Key)&&item.Value.SequenceEqual(after[item.Key]),"Read-only verification changed "+item.Key);
        }
        static PeerIdentity Peer(string session,PeerRole role)
        {
            string name=role==PeerRole.Participant?"participant":"operator";
            return new PeerIdentity(session,name,role,"simulator/"+name,ControlSource.Simulator,name+"-monotonic");
        }
        static ReceiveMonitor Monitor(string session,PeerRole remoteRole=PeerRole.Operator)
        {return new ReceiveMonitor(Peer(session,remoteRole),DevelopmentFixtures.Faults(),"simulator-no-hardware",remoteRole==PeerRole.Operator?"participant-monotonic":"operator-monotonic");}
        static HeadEyeSample Sample(string session,long sequence,double time)
        {return new HeadEyeSample(session,"simulator/operator","simulator-no-hardware","operator-monotonic",0,0,sequence,time,time,ControlSource.Simulator,StreamRole.OperatorControl,Rotation.Identity,new Vec3(0,0,1),true,true);}
        static IEnumerable<WirePacket> OldPackets(string session,PeerRole role)
        {
            var peer=Peer(session,role);const long sequence=Int64.MaxValue-1;
            yield return WirePacket.Hello(peer,sequence,10,SessionConfiguration.ProtocolVersion);
            yield return WirePacket.TimeRequest(peer,sequence,10,"old-exchange");
            yield return WirePacket.TimeReply(peer,sequence,10,"old-exchange",role==PeerRole.Operator?"participant-monotonic":"operator-monotonic",9,9.5);
            if(role==PeerRole.Operator)
            {
                yield return WirePacket.Tracking(peer,sequence,Sample(session,sequence,10));
                yield return WirePacket.SafetyStopRequest(peer,sequence,10,"old-stop");
            }
            else
            {
                yield return WirePacket.Task(peer,sequence,10,new TaskSnapshot(session,0,0,"old-trial",Colour.Red,Colour.Blue,Phase.MainTask,Condition.B));
                yield return WirePacket.SafetyStopAck(peer,sequence,10,"old-stop");
            }
        }
        static ClockSynchronizer FreshMap(string session)
        {
            var map=new ClockSynchronizer("participant-monotonic","operator-monotonic",DevelopmentFixtures.Clocks());
            for(int i=0;i<3;i++)
            {
                double sent=i*.11;string exchange="fresh-exchange-"+i;
                Check.True(map.RegisterRequest(exchange,sent));
                Check.True(map.ObserveReply(WirePacket.TimeReply(Peer(session,PeerRole.Operator),i,sent+.002,exchange,"participant-monotonic",sent,sent+.001),sent+.003),map.LastRejection);
            }
            Check.True(map.Snapshot().Available);return map;
        }
        public static void Register(TestSuite tests)
        {
            tests.Run("Preview restart reason accepts only the canonical explicit request identity",()=>{
                Check.Equal("owner_requested_preview_restart:"+Request,PreviewRecovery.EndReason(Request));
                foreach(var invalid in new[]{null,""," ",Request.ToUpperInvariant(),Request+"0",Request.Substring(1),Request.Substring(0,31)+"g",Request+"\n"})
                    Check.Throws<ArgumentException>(()=>PreviewRecovery.EndReason(invalid));
            });
            tests.Run("Preview explicit restart ends every reachable live phase without resuming or changing source",()=>{
                foreach(var phase in new[]{Phase.Preparation,Phase.Familiarization,Phase.Practice,Phase.PracticeCorrection,Phase.PracticeRetry,
                    Phase.BlockPreparation,Phase.BlockReady,Phase.Countdown,Phase.MainTask,Phase.Questionnaire,Phase.Rest,Phase.Paused})
                Temporary(directory=>{
                    var app=Participant(NewSession());string journal=Path.Combine(directory,"participant.journal");
                    try
                    {
                        app.EnableRecording(journal,Provenance(),10);Reach(app,phase);Check.Equal(phase,app.Experiment.Phase);
                        var pending=app.Experiment.CurrentTrial;bool wasPending=pending!=null&&pending.Outcome==TrialOutcome.Pending;
                        var source=app.Experiment.LockedControl;int count=app.Experiment.Trials.Count,ratings=app.Experiment.Ratings.Count;
                        app.OwnerEndForRestart(Request);
                        Check.Equal(Phase.Ended,app.Experiment.Phase);Check.Equal(EndReason.TechnicalEnd,app.Experiment.EndReason);
                        Check.Equal(source,app.Experiment.LockedControl);Check.Equal(count,app.Experiment.Trials.Count);Check.Equal(ratings,app.Experiment.Ratings.Count);
                        Check.True(app.Experiment.Events.Any(x=>x.Kind=="technical_issue"));
                        var ended=app.Experiment.Events.Single(x=>x.Kind=="session_ended");
                        Check.Equal("TechnicalEnd",ended.Fields["reason"]);Check.Equal(PreviewRecovery.EndReason(Request),ended.Fields["detail"]);
                        if(wasPending){Check.Equal(TrialOutcome.Interrupted,pending.Outcome);Check.True(pending.TechnicalIssue);Check.Equal<bool?>(null,pending.Correct);Check.Equal<double?>(null,pending.ReactionSeconds);}
                        Check.Equal(app.Recording.LastWrittenSequence,app.Recording.LastFlushedSequence);
                        int events=app.Experiment.Events.Count;ExpectFailure(()=>app.OwnerEndForRestart(Request));Check.Equal(events,app.Experiment.Events.Count);
                    }
                    finally{app.Dispose();}
                    JournalReadResult read;var rows=Read(journal,out read);Check.Equal(JournalReadStatus.CleanClose,read.Status);
                    var final=rows.Last(x=>x.Kind=="session_observation");Check.Equal("false",final.Fields["session_completed"]);Check.Equal("TechnicalEnd",final.Fields["end_reason"]);
                });
            });
            tests.Run("Preview restart refuses missing or failed recording and preserves previous end reason",()=>{
                using(var app=Participant(NewSession()))
                {
                    int events=app.Experiment.Events.Count;ExpectFailure(()=>app.OwnerEndForRestart(Request));
                    Check.Equal(Phase.Preparation,app.Experiment.Phase);Check.Equal(events,app.Experiment.Events.Count);
                }
                Temporary(directory=>{
                    using(var app=Participant(NewSession()))
                    {
                        app.EnableRecording(Path.Combine(directory,"absent","participant.journal"),Provenance(),10);
                        Check.True(app.Recording.IsFaulted);int events=app.Experiment.Events.Count;ExpectFailure(()=>app.OwnerEndForRestart(Request));
                        Check.Equal(Phase.Preparation,app.Experiment.Phase);Check.Equal(events,app.Experiment.Events.Count);
                    }
                    using(var app=Participant(NewSession()))
                    {
                        app.EnableRecording(Path.Combine(directory,"participant.journal"),Provenance(),10);app.Experiment.Withdraw();
                        int events=app.Experiment.Events.Count;ExpectFailure(()=>app.OwnerEndForRestart(Request));
                        Check.Equal(EndReason.Withdrawn,app.Experiment.EndReason);Check.Equal(events,app.Experiment.Events.Count);
                    }
                });
            });
            tests.Run("Preview restart records its intent before changing phase and refuses an already closed journal",()=>{
                foreach(bool alreadyClosed in new[]{true,false})
                using(var bytes=new FailingWriteStream())using(var app=Participant(NewSession()))
                {
                    var journal=new ContinuousJournal(bytes,()=>{},new JournalIdentity(app.Identity.SessionId,"participant",app.Clock.ClockId,Provenance()),true);
                    app.EnableRecording(journal,10);int events=app.Experiment.Events.Count;
                    if(alreadyClosed)Check.True(journal.Close());else bytes.Fail=true;
                    ExpectFailure(()=>app.OwnerEndForRestart(Request));
                    Check.Equal(Phase.Preparation,app.Experiment.Phase);Check.Equal(events,app.Experiment.Events.Count);
                    Check.Equal<EndReason?>(null,app.Experiment.EndReason);
                }
            });
            tests.Run("Preview restart verifies two closed recordings and repeated disposal preserves exact bytes",()=>Temporary(directory=>{
                string session=NewSession();var participant=Participant(session);var operatorApp=Operator(session);
                try
                {
                    participant.EnableRecording(Path.Combine(directory,"participant.journal"),Provenance(),10);
                    operatorApp.EnableRecording(Path.Combine(directory,"operator.journal"),Provenance(),10);
                    participant.OwnerEndForRestart(Request);participant.Dispose();operatorApp.Dispose();
                    Check.Equal<string>(null,participant.RecordingWarning);Check.Equal<string>(null,operatorApp.RecordingWarning);
                    var before=Snapshot(directory);Check.True(before.ContainsKey(Path.Combine(directory,"participant.journal.closed"))&&before.ContainsKey(Path.Combine(directory,"operator.journal.closed")));
                    participant.Dispose();operatorApp.Dispose();string reason;
                    Check.True(PreviewRecovery.VerifyClosedSession(directory,session,Request,out reason),reason);Check.Equal<string>(null,reason);
                    var after=Snapshot(directory);Check.Equal(before.Count,after.Count);
                    foreach(var item in before)Check.True(item.Value.SequenceEqual(after[item.Key]));
                    using(var fresh=Participant(NewSession()))
                    {
                        Check.True(fresh.Identity.SessionId!=session);Check.Equal(Phase.Preparation,fresh.Experiment.Phase);
                        Check.Equal<ControlSelection?>(null,fresh.Experiment.LockedControl);Check.Equal(0,fresh.Experiment.Trials.Count);
                        Check.Equal(0,fresh.Synchronizer.Snapshot().SampleCount);Check.Equal<ContinuousJournal>(null,fresh.Recording);
                    }
                }
                finally{participant.Dispose();operatorApp.Dispose();}
            }));
            tests.Run("Preview restart rejects incomplete mismatched and corrupt evidence without changing original bytes",()=>{
                foreach(string corruption in new[]{"missing_participant_receipt","missing_operator_receipt","bad_receipt","truncated_receipt","wrong_role_receipt",
                    "journal_append","journal_truncate","journal_corrupt","different_closed_journal","wrong_session","wrong_request"})
                Temporary(directory=>{
                    string session=NewSession();ClosedPair(directory,session);string journal=Path.Combine(directory,"participant.journal"),receipt=journal+".closed";
                    if(corruption=="missing_participant_receipt")File.Delete(receipt);
                    else if(corruption=="missing_operator_receipt")File.Delete(Path.Combine(directory,"operator.journal.closed"));
                    else if(corruption=="bad_receipt")File.WriteAllText(receipt,"not a close receipt\n");
                    else if(corruption=="truncated_receipt"){var bytes=File.ReadAllBytes(receipt);File.WriteAllBytes(receipt,bytes.Take(bytes.Length/2).ToArray());}
                    else if(corruption=="wrong_role_receipt")File.WriteAllBytes(receipt,File.ReadAllBytes(Path.Combine(directory,"operator.journal.closed")));
                    else if(corruption=="journal_append")using(var stream=new FileStream(journal,FileMode.Append,FileAccess.Write)){stream.WriteByte(1);}
                    else if(corruption=="journal_truncate"){var bytes=File.ReadAllBytes(journal);File.WriteAllBytes(journal,bytes.Take(bytes.Length-1).ToArray());}
                    else if(corruption=="journal_corrupt"){var bytes=File.ReadAllBytes(journal);bytes[bytes.Length/2]^=1;File.WriteAllBytes(journal,bytes);}
                    else if(corruption=="different_closed_journal")
                    {
                        string replacement=Path.Combine(directory,"replacement");Directory.CreateDirectory(replacement);ClosedPair(replacement,session);
                        File.WriteAllBytes(journal,File.ReadAllBytes(Path.Combine(replacement,"participant.journal")));
                    }
                    RejectedWithoutMutation(directory,corruption=="wrong_session"?NewSession():session,corruption=="wrong_request"?new string('a',32):Request);
                });
            });
            tests.Run("Preview valid close receipts do not manufacture the requested technical end",()=>Temporary(directory=>{
                string session=NewSession();
                using(var participant=Participant(session))using(var operatorApp=Operator(session))
                {
                    participant.EnableRecording(Path.Combine(directory,"participant.journal"),Provenance(),10);
                    operatorApp.EnableRecording(Path.Combine(directory,"operator.journal"),Provenance(),10);
                    participant.Experiment.Withdraw();
                }
                Check.True(File.Exists(Path.Combine(directory,"participant.journal.closed"))&&File.Exists(Path.Combine(directory,"operator.journal.closed")));
                RejectedWithoutMutation(directory,session,Request);
            }));
            tests.Run("Preview close receipt never overwrites an existing file and failure stays visible",()=>Temporary(directory=>{
                string path=Path.Combine(directory,"participant.journal"),receipt=path+".closed";byte[] sentinel={1,2,3,4};
                var app=Participant(NewSession());app.EnableRecording(path,Provenance(),10);app.OwnerEndForRestart(Request);
                File.WriteAllBytes(receipt,sentinel);app.Dispose();
                Check.True(!String.IsNullOrWhiteSpace(app.RecordingWarning));Check.True(sentinel.SequenceEqual(File.ReadAllBytes(receipt)));
                app.Dispose();Check.True(sentinel.SequenceEqual(File.ReadAllBytes(receipt)));
            }));
            tests.Run("Preview disposal refuses a receipt when events arrived after explicit recording close",()=>Temporary(directory=>{
                string path=Path.Combine(directory,"participant.journal");var app=Participant(NewSession());
                try
                {
                    app.EnableRecording(path,Provenance(),10);app.OwnerEndForRestart(Request);Check.True(app.CloseRecording());
                    byte[] closed;
                    // CloseRecording has flushed the journal but deliberately
                    // leaves its writer handle open until Dispose. Read a
                    // snapshot with sharing compatible with that handle.
                    using(var read=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
                    using(var snapshot=new MemoryStream()){read.CopyTo(snapshot);closed=snapshot.ToArray();}
                    app.Experiment.FlagTechnicalIssue("synthetic_event_after_explicit_recording_close");
                    app.Dispose();
                    Check.True(!File.Exists(path+".closed"),"A previous close must not stand in for the failed final capture");
                    Check.True(!String.IsNullOrWhiteSpace(app.RecordingWarning));
                    Check.True(closed.SequenceEqual(File.ReadAllBytes(path)),"Closing again changed the original journal bytes");
                }
                finally{app.Dispose();}
            }));
            tests.Run("Preview failed restart or close flush cannot produce a successful close receipt",()=>{
                foreach(bool failAtEnd in new[]{true,false})Temporary(directory=>{
                    string path=Path.Combine(directory,"participant.journal");bool fail=false;
                    var app=Participant(NewSession());var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
                    var journal=new ContinuousJournal(file,()=>{if(fail)throw new IOException("synthetic durable flush failure");file.Flush(true);},
                        new JournalIdentity(app.Identity.SessionId,"participant",app.Clock.ClockId,Provenance()),false,path);
                    app.EnableRecording(journal,10);
                    try
                    {
                        if(failAtEnd)
                        {
                            long acknowledged=journal.LastFlushedSequence;fail=true;ExpectFailure(()=>app.OwnerEndForRestart(Request));
                            Check.Equal(acknowledged,journal.LastFlushedSequence);Check.Equal(Phase.Ended,app.Experiment.Phase);
                        }
                        else {app.OwnerEndForRestart(Request);fail=true;}
                    }
                    finally{app.Dispose();}
                    Check.True(journal.IsFaulted);Check.True(!String.IsNullOrWhiteSpace(app.RecordingWarning));Check.True(!File.Exists(path+".closed"));
                    string reason;Check.True(!PreviewRecovery.VerifyClosedSession(directory,app.Identity.SessionId,Request,out reason));
                });
            });
            tests.Run("Preview disposal failure cannot publish a successful close receipt",()=>Temporary(directory=>{
                string path=Path.Combine(directory,"participant.journal");var app=Participant(NewSession());var file=new FailingDisposeStream(path);
                var journal=new ContinuousJournal(file,()=>file.Flush(true),new JournalIdentity(app.Identity.SessionId,"participant",app.Clock.ClockId,Provenance()),false,path);
                app.EnableRecording(journal,10);app.OwnerEndForRestart(Request);app.Dispose();
                Check.True(journal.IsFaulted);Check.True(!String.IsNullOrWhiteSpace(app.RecordingWarning));Check.True(!File.Exists(path+".closed"));
                app.Dispose();Check.True(!File.Exists(path+".closed"));
            }));
            tests.Run("Preview recovery preserves the 250 ms latch after a 263 ms gap and prolonged timeout",()=>{
                foreach(double gap in new[]{.2631215,2.0})
                {
                    string session=NewSession();var monitor=Monitor(session);var peer=Peer(session,PeerRole.Operator);const double first=4.7385923;
                    Check.True(monitor.Observe(WirePacket.Hello(peer,0,first,SessionConfiguration.ProtocolVersion),first).Accepted);
                    Check.True(monitor.Observe(WirePacket.Hello(peer,1,first+gap,SessionConfiguration.ProtocolVersion),first+gap).Accepted);
                    var state=monitor.Snapshot(first+gap);Check.True(state.RecoveryDecisionRequired&&!state.HeadUsable&&!state.EyeUsable);
                    Check.True(monitor.Events.Any(x=>x.Kind=="fault_started"&&x.Detail=="RecoveryDecisionRequired"));
                    Check.True(monitor.Observe(WirePacket.Hello(peer,2,first+gap+.01,SessionConfiguration.ProtocolVersion),first+gap+.01).Accepted);
                    Check.True(monitor.Snapshot(first+gap+.01).RecoveryDecisionRequired,"Ordinary traffic must not reset an interrupted receiver epoch");
                }
            });
            tests.Run("Preview new session rejects every old packet kind before high sequences can poison acceptance",()=>{
                var kinds=new HashSet<WireKind>();
                foreach(var role in new[]{PeerRole.Operator,PeerRole.Participant})
                {
                    string oldSession=NewSession(),freshSession=NewSession();var monitor=Monitor(freshSession,role);double now=0;
                    foreach(var packet in OldPackets(oldSession,role))
                    {kinds.Add(packet.Kind);Check.Equal("wrong_session",monitor.Observe(packet,now+=.001).Reason);}
                    var peer=Peer(freshSession,role);Check.True(monitor.Observe(WirePacket.Hello(peer,0,now,SessionConfiguration.ProtocolVersion),now).Accepted);
                    Check.True(monitor.Observe(WirePacket.Hello(peer,3,now+.01,SessionConfiguration.ProtocolVersion),now+.01).Accepted);
                    Check.Equal("out_of_order",monitor.Observe(WirePacket.Hello(peer,2,now+.02,SessionConfiguration.ProtocolVersion),now+.02).Reason);
                    Check.Equal("duplicate",monitor.Observe(WirePacket.Hello(peer,3,now+.03,SessionConfiguration.ProtocolVersion),now+.03).Reason);
                    var state=monitor.Snapshot(now+.03);Check.Equal(2L,state.MissingSequencePositions);Check.Equal(1L,state.OutOfOrderPackets);Check.Equal(1L,state.DuplicatePackets);
                    Check.True(!state.RecoveryDecisionRequired);
                }
                Check.Equal(Enum.GetValues(typeof(WireKind)).Length,kinds.Count);
            });
            tests.Run("Preview new handshake needs fresh synchronization and matching samples before simulator readiness",()=>{
                string session=NewSession();var monitor=Monitor(session);var emptyMap=new ClockSynchronizer("participant-monotonic","operator-monotonic",DevelopmentFixtures.Clocks());
                var before=monitor.Snapshot(0,emptyMap);Check.True(!before.HandshakeAccepted&&!before.CaptureAgeVerified&&!before.HeadUsable&&!before.EyeUsable);
                Check.Equal(0,emptyMap.Snapshot().SampleCount);Check.True(!emptyMap.Snapshot().Available);
                Check.True(monitor.Observe(WirePacket.Hello(Peer(session,PeerRole.Operator),0,0,SessionConfiguration.ProtocolVersion),0,emptyMap).Accepted);
                Check.True(!SelectedSimulationSource.Ready(monitor.CurrentSample(0,emptyMap),monitor.Snapshot(0,emptyMap),session,0,0));
                var sample=Sample(session,0,.23);Check.True(monitor.Observe(WirePacket.Tracking(Peer(session,PeerRole.Operator),1,sample),.234,emptyMap).Accepted);
                Check.True(!SelectedSimulationSource.Ready(sample,monitor.Snapshot(.234,emptyMap),session,0,0));
                var map=FreshMap(session);Check.True(SelectedSimulationSource.Ready(monitor.CurrentSample(.234,map),monitor.Snapshot(.234,map),session,0,0));
                Check.True(!SelectedSimulationSource.Ready(sample,monitor.Snapshot(.234,map),NewSession(),0,0));
                Check.True(!SelectedSimulationSource.Ready(sample,monitor.Snapshot(.234,map),session,0,1));
                var staleReply=WirePacket.TimeReply(Peer(NewSession(),PeerRole.Operator),100,.235,"fresh-exchange-0","participant-monotonic",0,.001);
                long revision=map.Revision;var decision=monitor.Observe(staleReply,.236,map);
                Check.Equal("wrong_session",decision.Reason);if(decision.Accepted)map.ObserveReply(staleReply,.236);Check.Equal(revision,map.Revision);
            });
        }
    }
}
