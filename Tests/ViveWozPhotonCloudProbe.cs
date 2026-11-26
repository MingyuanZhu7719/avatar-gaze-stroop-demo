#if STROOP_TEST_BUILD && UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor;

namespace AvatarStroop.Tests
{
    // Explicit opt-in cloud fixture. Every pose and answer is generated here;
    // no headset, recording, participant ID, filesystem data or user tracking.
    public static class ViveWozPhotonCloudProbe
    {
        static void Require(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
        static ViveDevelopmentParticipant Prepare(MonotonicClock clock)
        {
            var allocation=new Allocation("synthetic-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var config=new SessionConfiguration("synthetic-photon-"+Guid.NewGuid().ToString("N"),42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var task=new ViveDevelopmentParticipant(config,clock,DevelopmentFixtures.Geometry(),map,.5,true);
            Action neutral=()=>task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
            neutral();task.OwnerBeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {
                neutral();var trial=task.CurrentTrial;task.MarkPresented(trial.TrialId);var c=trial.Stimulus.Ink;
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,c==Colour.Green?1:c==Colour.Yellow?-1:0,c==Colour.Red?1:c==Colour.Blue?-1:0));neutral();
            }
            Require(task.Phase==Phase.BlockPreparation,"synthetic_training");return task;
        }
        static void Until(Func<bool> condition,Action service,int timeoutSeconds,string stage)
        {
            var timer=Stopwatch.StartNew();
            while(!condition())
            {if(timer.Elapsed.TotalSeconds>timeoutSeconds)throw new TimeoutException(stage);service();Thread.Sleep(5);}
        }
        public static void Run()
        {
            // SDK helpers use runtime GameObjects; use play mode so their
            // normal cleanup and service lifecycle match a player.
            EditorSettings.enterPlayModeOptionsEnabled=true;
            EditorSettings.enterPlayModeOptions=EnterPlayModeOptions.DisableDomainReload;
            EditorApplication.playModeStateChanged+=OnPlayMode;
            EditorApplication.isPlaying=true;
        }
        static void OnPlayMode(PlayModeStateChange state)
        {
            if(state!=PlayModeStateChange.EnteredPlayMode)return;
            EditorApplication.playModeStateChanged-=OnPlayMode;
            RunProbe();
        }
        static void RunProbe()
        {
            int code=1;
            try{Execute();code=0;}
            catch(Exception e){Console.WriteLine("FAIL photon_cloud "+e.GetType().Name+" "+e.Message);}
            EditorApplication.Exit(code);
        }
        static void Execute()
        {
            string config=File.ReadAllText(Environment.GetEnvironmentVariable("STROOP_PHOTON_CONFIG"));
            string appId=Regex.Match(config,"\"appId\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            string region=Regex.Match(config,"\"region\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            var pc=new MonotonicClock("synthetic-p-"+Guid.NewGuid().ToString("N"));
            var oc=new MonotonicClock("synthetic-o-"+Guid.NewGuid().ToString("N"));
            using(var opRoom=ViveWozPhotonSession.CreateOperator(appId,region,oc))
            {
                Action opService=()=>{opRoom.Service();Require(!opRoom.Terminal,"operator_setup:"+opRoom.FailureReason);};
                Until(()=>opRoom.RoomReady,opService,20,"create_room");
                using(var pRoom=ViveWozPhotonSession.JoinParticipant(appId,region,pc,opRoom.RoomCode))
                {
                    Action service=()=>{opRoom.Service();pRoom.Service();Require(!opRoom.Terminal,"operator_room:"+opRoom.FailureReason);Require(!pRoom.Terminal,"participant_room:"+pRoom.FailureReason);};
                    Until(()=>opRoom.PeerConnected&&pRoom.PeerConnected,service,20,"join_room");
                    Require(!pRoom.ReadyForOffer&&!opRoom.PeerConfirmed,"no_implicit_accept");
                    opRoom.ConfirmPeer();Until(()=>pRoom.ReadyForOffer&&opRoom.PeerConfirmed,service,10,"peer_confirm");
                    var task=Prepare(pc);var owner=task.CreateLegacyWozTaskOwner(pc);
                    var observations=new ViveLegacyWozObservationOwner.SyntheticObservations(oc);
                    using(var offer=ViveLegacyWozProcessPairing.CreateSyntheticOffer(owner,"synthetic-owner",oc.ClockId,true))
                    using(var p=ViveLegacyWozParticipantProcess.CreatePhoton(offer,owner,pRoom,5))
                    {
                        char[] token=offer.RevealToken();try{pRoom.PublishOffer(new string(token));}finally{Array.Clear(token,0,token.Length);}
                        Until(()=>opRoom.OfferAvailable,service,10,"offer");
                        char[] imported=opRoom.TakeOffer().ToCharArray();ViveLegacyWozProcessPairing parsed;
                        try{parsed=ViveLegacyWozProcessPairing.Parse(imported);}finally{Array.Clear(imported,0,imported.Length);}
                        using(parsed)
                        using(var op=ViveLegacyWozOperatorProcess.SyntheticPhoton(parsed,oc,observations,opRoom,5))
                        {
                            Require(!op.SourceConfirmed,"no_implicit_forwarding");
                            Require(op.ConfirmLocalOperatorSource()&&op.Start(),"operator_start");opRoom.MarkOperatorStarted();
                            Until(()=>pRoom.OperatorStarted,service,10,"operator_start_notice");
                            Require(p.Start(),"participant_start");pRoom.MarkParticipantStarted();
                            Until(()=>opRoom.Complete&&pRoom.Complete,service,10,"complete_pairing");
                            long seq=0;int proofs=0,poses=0;double maxCycle=0;bool avatarSeen=false;
                            Action pump=()=>{
                                service();op.Tick();p.Tick();Require(!op.Terminal,"operator_process:"+op.LastIssue);Require(!p.Terminal,"participant_process:"+p.LastIssue);
                                seq++;observations.Fresh(seq,(int)seq,(int)seq);if(op.PollPhasePreflight())proofs++;
                                seq++;observations.Fresh(seq,(int)seq,(int)seq);if(op.PollObservation())poses++;
                                Rotation head;Vec3 eye;if(op.TryGetParticipantAvatarPose(out head,out eye)){avatarSeen=true;Require(head.Y==.6&&eye.X==-.6,"avatar_payload");}
                            };
                            bool admissionRequired=false;int heldProofs=0;
                            Action phase=()=>{
                              for(int attempt=0;attempt<10;attempt++){
                                Require(p.PublishCurrentTask()&&p.ConfirmCurrentSource()&&p.BeginPhasePreflight(),"publish_phase");
                                Until(()=>p.CurrentPreflight!=null,pump,10,"preflight");
                                var proof=p.CurrentPreflight;Require(proof is ViveLegacyWozParticipantProcess.PhotonEvidence&&proof.SyntheticObservationsOnly&&!proof.ReceiptCapturedBySocket&&!proof.IsExperimentAdmission,"photon_origin");
                                maxCycle=Math.Max(maxCycle,proof.OriginalReceiptParticipantSeconds-proof.OriginalTaskIssuedParticipantSeconds);
                                Console.WriteLine("photon_phase_cycle_ms="+(int)((proof.OriginalReceiptParticipantSeconds-proof.OriginalTaskIssuedParticipantSeconds)*1000)+" rtt_p_ms="+pRoom.RoundTripMilliseconds+" rtt_o_ms="+opRoom.RoundTripMilliseconds);
                                if(!admissionRequired||p.ControlledEvidenceFresh(proof))return;
                                heldProofs++;
                              }
                              throw new InvalidOperationException("production_phase_freshness_budget");
                            };
                            // Initial JIT/crypto/network setup may delay a first
                            // proof. Warm up while still BlockReady: failed
                            // proofs grant no admission and no stimuli appear.
                            for(int warmup=0;warmup<5;warmup++)phase();
                            Require(p.CurrentPreflight.Phase==Phase.BlockReady,"ready_proof");
                            Require(p.ControlledEvidenceFresh(p.CurrentPreflight),"production_readiness_budget");admissionRequired=true;
                            var sink=new ViveLegacyWozControlledScene.MemorySink();
                            var scene=ViveLegacyWozControlledScene.CreatePhotonFixture(p,sink);
                            var readiness=Stopwatch.StartNew();int readyFrames=0,readyTotal=0;long readyCycle=-1;bool lastReady=true;
                            while(readiness.Elapsed.TotalSeconds<3)
                            {
                                scene.PumpBeforeTask();pump();Require(!scene.Closed,"readiness_scene_closed");
                                var currentReady=p.CurrentPreflight;
                                if(currentReady!=null&&currentReady.TaskPacketSequence!=readyCycle){readyCycle=currentReady.TaskPacketSequence;double elapsed=currentReady.OriginalReceiptParticipantSeconds-currentReady.OriginalTaskIssuedParticipantSeconds;maxCycle=Math.Max(maxCycle,elapsed);Console.WriteLine("photon_ready_cycle_ms="+(int)(1000*elapsed));}
                                if(readiness.Elapsed.TotalSeconds>=1){readyTotal++;bool enabled=scene.CanBeginCountdown;if(enabled)readyFrames++;
                                    if(!enabled&&lastReady){var retained=(ViveLegacyWozParticipantProcess.Evidence)typeof(ViveLegacyWozParticipantProcess).GetField("retainedPreflight",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(p);
                                        Console.WriteLine("photon_ready_gap ack="+p.DisplayTaskAcknowledged+" proof="+p.ControlledEvidenceFresh(p.DisplayPreflight)+" retained_age_ms="+(retained==null?-1:(int)(1000*(pc.Now-retained.OriginalTaskIssuedParticipantSeconds)))+" current_age_ms="+(currentReady==null?-1:(int)(1000*(pc.Now-currentReady.OriginalTaskIssuedParticipantSeconds))));}
                                    lastReady=enabled;}
                                Thread.Sleep(5);
                            }
                            Console.WriteLine("photon_ready_enabled_frames="+readyFrames+" total_frames="+readyTotal);
                            // Internet stalls are measured, not converted into
                            // authorization. Start only with actual fresh proof.
                            Require(readyTotal>0&&readyFrames>0,"countdown_never_ready");
                            Until(()=>scene.CanBeginCountdown,()=>{scene.PumpBeforeTask();pump();Require(!scene.Closed,"readiness_scene_closed");},10,"wait_current_countdown_proof");
                            Require(scene.BeginCountdown(),"actual_scene_countdown");
                            Console.WriteLine("photon_stage=countdown_started");
                            long countdownCycle=-1;
                            Until(()=>task.Phase==Phase.MainTask,()=>{
                                var before=task.Phase;var beforeProof=p.DisplayPreflight;double proofAge=beforeProof==null?-1:pc.Now-beforeProof.OriginalTaskIssuedParticipantSeconds;
                                double retainedAge=pc.Now-(double)typeof(ViveLegacyWozParticipantProcess).GetField("retainedTaskIssued",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(p);
                                bool ready=scene.PumpBeforeTask();Require(!scene.Closed,"countdown_scene_closed before="+before+" after="+task.Phase+" proof_age_ms="+(int)(proofAge*1000)+" retained_task_ms="+(int)(retainedAge*1000));pump();
                                var proof=p.CurrentPreflight;if(proof!=null&&proof.TaskPacketSequence!=countdownCycle){countdownCycle=proof.TaskPacketSequence;Console.WriteLine("photon_countdown_cycle_ms="+(int)(1000*(proof.OriginalReceiptParticipantSeconds-proof.OriginalTaskIssuedParticipantSeconds)));}
                                if(ready){task.Tick();scene.AfterTaskMutation();}
                            },8,"countdown_wait");
                            Require(task.Phase==Phase.MainTask,"main_task");
                            Console.WriteLine("photon_stage=main_acquisition");
                            Until(()=>scene.MayShowTask,()=>{scene.PumpBeforeTask();Require(!scene.Closed,"main_acquisition_closed");pump();},10,"main_display");
                            Require(p.TrySendParticipantAvatarPose(new Rotation(0,.6,0,.8),new Vec3(-.6,0,.8),pc.Now),"avatar_send");
                            Until(()=>p.CurrentPose!=null&&avatarSeen,pump,10,"pose_and_avatar");
                            Require(p.CurrentPose is ViveLegacyWozParticipantProcess.PhotonEvidence&&p.CurrentPose.SyntheticObservationsOnly,"pose_evidence");
                            scene.Present();Require(scene.MayShowTask,"actual_scene_display");
                            task.MarkPresented(task.CurrentTrial.TrialId);scene.NotePresented(task.CurrentTrial.TrialId);scene.AfterTaskMutation();
                            var sustained=Stopwatch.StartNew();int freshFrames=0,totalFrames=0,proofFrames=0,poseFrames=0,reverseFrames=0;double lastGap=-1,lastReverseSend=pc.Now,reverseGapStarted=0,maxReverseGap=0;long lastCycle=-1;bool previousReverse=true;
                            while(sustained.Elapsed.TotalSeconds<12)
                            {
                                scene.PumpBeforeTask();pump();Require(!scene.Closed,"maintained_scene_closed");scene.Present();scene.RecheckPresentation();
                                var cycle=p.CurrentPreflight;
                                if(cycle!=null&&cycle.TaskPacketSequence!=lastCycle){lastCycle=cycle.TaskPacketSequence;double elapsed=cycle.OriginalReceiptParticipantSeconds-cycle.OriginalTaskIssuedParticipantSeconds;maxCycle=Math.Max(maxCycle,elapsed);Console.WriteLine("photon_renewal_cycle_ms="+(int)(1000*elapsed));}
                                // Every measured frame follows actual onset.
                                // A stale formal frame must stop the scene.
                                if(sustained.Elapsed.TotalSeconds>=0)
                                {totalFrames++;bool proofFresh=p.ControlledEvidenceFresh(p.DisplayPreflight),poseFresh=p.ControlledEvidenceFresh(p.DisplayPose);
                                    if(proofFresh)proofFrames++;if(poseFresh)poseFrames++;if(proofFresh&&poseFresh)freshFrames++;
                                    Rotation reverseHead;Vec3 reverseEye;bool reverse=op.TryGetParticipantAvatarPose(out reverseHead,out reverseEye);if(reverse)reverseFrames++;
                                    if(!reverse){var flags=BindingFlags.NonPublic|BindingFlags.Instance;var type=typeof(ViveLegacyWozOperatorProcess);
                                        double remaining=(double)type.GetField("participantAvatarExpires",flags).GetValue(op)-oc.Now,sendAge=pc.Now-lastReverseSend;
                                        Require(remaining<=0,"reverse_avatar_hidden_before_expiry");
                                        Require(sendAge<.1,"reverse_sender_stopped_during_renewal");
                                        if(previousReverse){reverseGapStarted=sustained.Elapsed.TotalSeconds;double expires=(double)type.GetField("participantAvatarExpires",flags).GetValue(op);
                                            double currentTaskReceived=(double)type.GetField("taskReceived",flags).GetValue(op),priorTaskReceived=(double)type.GetField("previousTaskReceived",flags).GetValue(op);
                                            bool taskExpiry=Math.Abs(expires-currentTaskReceived-op.NetworkVisualBudgetSeconds)<.000001||Math.Abs(expires-priorTaskReceived-op.NetworkVisualBudgetSeconds)<.000001;
                                            Console.WriteLine("photon_reverse_gap bound="+(taskExpiry?"original_task":"received_pose")+" has_pose="+(type.GetField("participantAvatar",flags).GetValue(op)!=null)+" expiry_remaining_ms="+(int)(1000*remaining)+" latest_send_age_ms="+(int)(1000*sendAge));}}
                                    else if(!previousReverse)maxReverseGap=Math.Max(maxReverseGap,sustained.Elapsed.TotalSeconds-reverseGapStarted);
                                    previousReverse=reverse;
                                    if((!proofFresh||!poseFresh)&&sustained.Elapsed.TotalSeconds-lastGap>.25){lastGap=sustained.Elapsed.TotalSeconds;
                                        var flags=BindingFlags.NonPublic|BindingFlags.Instance;var type=typeof(ViveLegacyWozParticipantProcess);
                                        var rp=(ViveLegacyWozParticipantProcess.Evidence)type.GetField("retainedPreflight",flags).GetValue(p);
                                        var pose=(ViveLegacyWozParticipantProcess.Evidence)type.GetField("retainedPose",flags).GetValue(p);
                                        Console.WriteLine("photon_gap proof="+proofFresh+" pose="+poseFresh+" retained_task_ms="+(int)(1000*(pc.Now-(double)type.GetField("retainedTaskIssued",flags).GetValue(p)))+" retained_proof_task_ms="+(rp==null?-1:(int)(1000*(pc.Now-rp.OriginalTaskIssuedParticipantSeconds)))+" retained_pose_receipt_ms="+(pose==null?-1:(int)(1000*(pc.Now-pose.OriginalReceiptParticipantSeconds))));
                                    }}
                                if(p.TrySendParticipantAvatarPose(new Rotation(0,.6,0,.8),new Vec3(-.6,0,.8),pc.Now))lastReverseSend=pc.Now;
                                Thread.Sleep(5);
                            }
                            Console.WriteLine("photon_maintained_fresh_frames="+freshFrames+" total_frames="+totalFrames+" proof_frames="+proofFrames+" pose_frames="+poseFrames+" reverse_avatar_frames="+reverseFrames+" reverse_expiry_masked_frames="+(totalFrames-reverseFrames)+" max_reverse_gap_ms="+(int)(1000*maxReverseGap));
                            Require(totalFrames>0&&freshFrames==totalFrames,"maintained_formal_display_gap");
                            Require(reverseFrames>0&&previousReverse,"reverse_avatar_did_not_recover");
                            Require(p.RevokeSource()&&task.Phase==Phase.Paused,"local_revoke");
                            Until(()=>p.SourceRevokeAcknowledged,()=>{service();op.Tick();p.Tick();},10,"revoke_ack");
                            Require(op.SourceRevokeAcknowledged&&op.Terminal,"operator_revoke");
                            Console.WriteLine("PASS photon_cloud handshake=1 phases="+proofs+" poses="+poses+" avatar=1 revoke_ack=1 synthetic_only=1 held_stale_proofs="+heldProofs+" rtt_p_ms="+pRoom.RoundTripMilliseconds+" rtt_o_ms="+opRoom.RoundTripMilliseconds+" max_phase_cycle_ms="+(int)(maxCycle*1000));
                        }
                    }
                }
            }
            // A missing room returns a bounded, visible terminal error; joining
            // never silently creates an operator room or reconnects old state.
            using(var missing=ViveWozPhotonSession.JoinParticipant(appId,region,new MonotonicClock("missing-room-"+Guid.NewGuid().ToString("N")),"ZZZZZZZZ"))
            {Until(()=>missing.Terminal,missing.Service,20,"missing_room_error");Require(missing.FailureReason.StartsWith("join_room_",StringComparison.Ordinal),"missing_room_cause");Console.WriteLine("PASS photon_cloud wrong_room=1");}
        }
    }
}
#endif
