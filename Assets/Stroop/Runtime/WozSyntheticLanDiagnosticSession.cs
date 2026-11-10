using System;
using System.Net;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A deliberately tiny, legacy-schema network fixture, NOT Experiment,
    // named-option provenance, an apparatus host, tracking or research admission.
    internal static class WozSyntheticLanScenario
    {
        static readonly Condition[] Order = { Condition.B, Condition.C, Condition.A, Condition.D };
        internal static TaskSnapshot Create(string session, int step, bool paused)
        {
            bool main = step >= 1 && step <= 4;
            return new TaskSnapshot(session, main ? step - 1 : -1, 0,
                main ? "synthetic-trial/" + step : null,
                main ? (Colour?)Colour.Red : null, main ? (Colour?)Colour.Green : null,
                paused ? Phase.Paused : main ? Phase.MainTask : step == 0 ? Phase.Preparation : step == 5 ? Phase.Questionnaire : Phase.Ended,
                main ? (Condition?)Order[step - 1] : null);
        }
        internal static bool IsScenarioTask(TaskSnapshot t)
        {
            if (t == null || t.Segment != 0) return false;
            if (t.Phase == Phase.MainTask || t.Phase == Phase.Paused)
                return t.BlockIndex >= 0 && t.BlockIndex <= 3 && t.Condition == Order[t.BlockIndex] &&
                    t.TrialId == "synthetic-trial/" + (t.BlockIndex + 1) && t.Word == Colour.Red && t.Ink == Colour.Green;
            return (t.Phase == Phase.Preparation || t.Phase == Phase.Questionnaire || t.Phase == Phase.Ended) &&
                t.BlockIndex == -1 && t.TrialId == null && t.Word == null && t.Ink == null && t.Condition == null;
        }
        internal static bool CanShowPose(TaskSnapshot t)
        { return IsScenarioTask(t) && t.Phase == Phase.MainTask && t.Condition != Condition.D; }
    }

    // Immutable historical status. PoseVisible is a local-age diagnostic only,
    // never physical freshness, peer liveness, clock calibration or readiness.
    public sealed class WozSyntheticLanDiagnosticSnapshot
    {
        public PeerRole Role { get; private set; }
        public WozDiagnosticState State { get; private set; }
        public WozDiagnosticOrigin Origin { get; private set; }
        public WozLanTaskContext TaskContext { get; private set; }
        public TaskSnapshot OwnedTask { get; private set; }
        public bool TaskFresh { get; private set; }
        public bool PoseVisible { get { return RemotePose != null; } }
        public HeadEyeSample RemotePose { get; private set; }
        public WozLanPoseObservation PoseObservation { get; private set; }
        public double? OriginalLocalPoseReceiptSeconds { get; private set; }
        public double? OriginalLocalTaskReceiptSeconds { get; private set; }
        public long AcceptedMessages { get; private set; }
        public long RejectedMessages { get; private set; }
        public long SentMessages { get; private set; }
        public string Detail { get; private set; }
        public WozDiagnosticFailure PrimaryFailure { get; private set; }
        public WozDiagnosticFailure CleanupFailure { get; private set; }
        public double? RoundTripProcessSeconds { get; private set; }
        public bool PeerAuthenticated { get; private set; }
        public string PairingStatus { get { return PeerAuthenticated ? "fresh_own_challenge_reply_not_experiment_readiness" : "own_fresh_clock_challenge_required"; } }
        internal WozSyntheticLanDiagnosticSnapshot(PeerRole role, WozAuthenticatedDiagnosticTransport transport,
            WozLanTaskContext task, TaskSnapshot owned, bool fresh, HeadEyeSample pose, WozLanPoseObservation observation,
            double? poseReceipt, double? taskReceipt, long accepted, long rejected, long sent, string detail, double? rtt, bool peerAuthenticated)
        {
            Role = role; State = transport.State; Origin = transport.Origin; TaskContext = task; OwnedTask = owned;
            TaskFresh = fresh; RemotePose = pose; PoseObservation = observation;
            OriginalLocalPoseReceiptSeconds = poseReceipt; OriginalLocalTaskReceiptSeconds = taskReceipt;
            AcceptedMessages = accepted; RejectedMessages = rejected; SentMessages = sent; Detail = detail;
            PrimaryFailure = transport.PrimaryFailure; CleanupFailure = transport.CleanupFailure; RoundTripProcessSeconds = rtt;
            PeerAuthenticated = peerAuthenticated;
        }
    }

    // Public surface accepts only fixed Simulator identities and explicit actions.
    // No source/provider/sample/time injection, fallback, recording or SDK access.
    // Construction opens no socket. A disposed/faulted session cannot restart;
    // new launches require a fresh pairing key and epoch provisioned separately.
    // Own fresh challenge replies are a narrow synthetic replay gate, not a
    // general-purpose mutual-key handshake or real experiment/source admission.
    public sealed class WozSyntheticLanDiagnosticSession : IDisposable
    {
        public const double PeerProofLifetimeSeconds = 10;
        readonly object gate = new object();
        readonly WozAuthenticatedDiagnosticTransport transport;
        readonly PeerRole role;
        readonly string session;
        readonly double freshness;
        int step;
        bool paused, started, startupChallengeRequested;
        long revision, accepted, rejected, sent;
        WozLanTaskContext task;
        HeadEyeSample remotePose;
        WozLanPoseObservation observation;
        double? taskReceipt, poseReceipt, rtt, peerReplyReceipt;
        string detail = "synthetic_legacy_diagnostic_dormant";

        public WozSyntheticLanDiagnosticSession(WozLanContext context, PeerRole role, byte[] key,
            IPEndPoint exactLocal, IPEndPoint exactRemote, double freshnessSeconds = 2)
            : this(context, role, key, exactLocal, exactRemote, freshnessSeconds, null) { }
        internal WozSyntheticLanDiagnosticSession(WozLanContext context, PeerRole role, byte[] key,
            IPEndPoint exactLocal, IPEndPoint exactRemote, double freshnessSeconds, IWozDiagnosticSocket socket)
        {
            if (context == null || context.Participant.Source != ControlSource.Simulator || context.Operator.Source != ControlSource.Simulator ||
                !context.Participant.SourceId.StartsWith("simulator/", StringComparison.Ordinal) ||
                !context.Operator.SourceId.StartsWith("simulator/", StringComparison.Ordinal))
                throw new ArgumentException("Synthetic Simulator peers are required; real sources are not admitted");
            this.role = role; session = context.Participant.SessionId; freshness = freshnessSeconds;
            transport = socket == null ? new WozAuthenticatedDiagnosticTransport(context, role, key, exactLocal, exactRemote, freshnessSeconds) :
                new WozAuthenticatedDiagnosticTransport(context, role, key, exactLocal, exactRemote, freshnessSeconds, socket);
        }
        public WozDiagnosticState StartDiagnostic()
        {
            lock (gate)
            {
                if (started) return transport.State;
                started = true;
                var state = transport.StartDiagnostic();
                detail = state == WozDiagnosticState.Running ? "synthetic_only_running_no_real_host_admission" : transport.TerminalReason ?? "terminal_session_requires_new_pairing";
                if (state == WozDiagnosticState.Running && role == PeerRole.Operator)
                { startupChallengeRequested = true; Track(transport.RequestClockDiagnostic()); }
                Refresh(); return transport.State;
            }
        }
        void ClearPose() { remotePose = null; observation = null; poseReceipt = null; }
        bool HasPeerProof(double now)
        { return peerReplyReceipt.HasValue && now >= peerReplyReceipt.Value && now - peerReplyReceipt.Value <= PeerProofLifetimeSeconds; }
        void InvalidateProof()
        { peerReplyReceipt = null; rtt = null; task = null; taskReceipt = null; ClearPose(); transport.InvalidateSyntheticBinding(); }
        bool CanUsePeer()
        {
            Refresh(); double now;
            return transport.TryReadSyntheticTime(out now) && HasPeerProof(now);
        }
        bool Refresh()
        {
            double now;
            if (!transport.TryReadSyntheticTime(out now))
            {
                InvalidateProof();
                if (transport.State == WozDiagnosticState.Faulted) detail = transport.TerminalReason;
                return false;
            }
            if (!HasPeerProof(now))
            {
                if (peerReplyReceipt.HasValue) { detail = "fresh_peer_reply_expired_explicit_clock_and_task_required"; InvalidateProof(); }
                ClearPose(); return false;
            }
            double since = role == PeerRole.Participant && task != null ? task.ParticipantSentSeconds : taskReceipt ?? -1;
            bool fresh = task != null && since >= 0 && now >= since && now - since <= freshness;
            if (!fresh || !WozSyntheticLanScenario.CanShowPose(task.Task) ||
                (poseReceipt.HasValue && now - poseReceipt.Value > freshness)) ClearPose();
            if (task != null && !fresh) detail = "synthetic_task_local_age_expired_pose_hidden";
            return fresh;
        }
        WozSyntheticLanDiagnosticSnapshot SnapshotCore()
        {
            bool fresh = Refresh();
            return new WozSyntheticLanDiagnosticSnapshot(role, transport, task,
                role == PeerRole.Participant ? WozSyntheticLanScenario.Create(session, step, paused) : null,
                fresh, remotePose, observation, poseReceipt, taskReceipt, accepted, rejected, sent, detail, rtt, peerReplyReceipt.HasValue);
        }
        public WozSyntheticLanDiagnosticSnapshot Snapshot { get { lock (gate) return SnapshotCore(); } }
        public WozSyntheticLanDiagnosticSnapshot Tick()
        {
            lock (gate)
            {
                Refresh();
                foreach (var item in transport.DrainSyntheticDiagnostic())
                {
                    detail = item.Detail;
                    if (item.Kind == WozDiagnosticEventKind.Rejected)
                    { rejected++; ClearPose(); if (role == PeerRole.Operator) { task = null; taskReceipt = null; } continue; }
                    if (item.Kind == WozDiagnosticEventKind.TerminalFault) { ClearPose(); continue; }
                    if (item.Kind == WozDiagnosticEventKind.ClockRequestExpired) { InvalidateProof(); continue; }
                    if (item.Kind == WozDiagnosticEventKind.CorrelatedClockReply)
                    {
                        // Clear only session cache here. Transport already cleared
                        // its cache at the exact reply boundary, before later frames.
                        task = null; taskReceipt = null; ClearPose();
                        rtt = item.RoundTripProcessSeconds; peerReplyReceipt = item.Message.OriginalLocalReceiptSeconds;
                    }
                    if (item.ReplySend != null && item.ReplySend.Status == WozDiagnosticSendStatus.AcceptedBySocketOnly) sent++;
                    if (item.Message == null) continue;
                    var message = item.Message; var packet = message.Packet;
                    accepted++;
                    if (role == PeerRole.Participant && packet.Kind == WireKind.TimeRequest && !startupChallengeRequested)
                    {
                        // Single ordered startup exchange, never a retry/reopen or
                        // automatic recovery after confirmation expiry or failure.
                        startupChallengeRequested = true;
                        Track(transport.RequestClockDiagnostic());
                    }
                    if (role == PeerRole.Operator && packet.Kind == WireKind.Task)
                    {
                        ClearPose();
                        if (!HasPeerProof(message.OriginalLocalReceiptSeconds))
                        { task = null; taskReceipt = null; detail = "own_fresh_clock_reply_required_before_task_binding"; continue; }
                        if (!WozSyntheticLanScenario.IsScenarioTask(packet.Snapshot))
                        { task = null; taskReceipt = null; rejected++; detail = "not_a_synthetic_scenario_task"; continue; }
                        task = message.TaskContext; taskReceipt = message.OriginalLocalReceiptSeconds;
                        detail = "authenticated_synthetic_task_received_not_real_host";
                    }
                    else if (role == PeerRole.Participant && packet.Kind == WireKind.HeadEye)
                    {
                        remotePose = packet.Sample; observation = message.PoseObservation; poseReceipt = message.OriginalLocalReceiptSeconds;
                        detail = "authenticated_synthetic_pose_local_receipt_not_sensor_capture";
                    }
                    else if (packet.Kind == WireKind.SafetyStopRequest || packet.Kind == WireKind.SafetyStopAck)
                    { ClearPose(); detail = "stop_observed_only_no_pause_applied_no_ack_sent"; }
                }
                return SnapshotCore();
            }
        }
        WozDiagnosticSendResult Reject(string reason)
        {
            detail = reason; Refresh();
            return new WozDiagnosticSendResult(transport.State == WozDiagnosticState.Faulted ? WozDiagnosticSendStatus.TerminalFault : WozDiagnosticSendStatus.Rejected, reason);
        }
        WozDiagnosticSendResult Track(WozDiagnosticSendResult result)
        {
            if (result.Status == WozDiagnosticSendStatus.AcceptedBySocketOnly) sent++;
            detail = result.Detail; Refresh(); return result;
        }
        bool CanOwnTask { get { return role == PeerRole.Participant && CanUsePeer(); } }
        public WozDiagnosticSendResult RequestClockDiagnostic()
        {
            lock (gate)
            {
                var result = transport.RequestClockDiagnostic();
                // Explicit renewal immediately suspends old usable state. A
                // rejected duplicate request does not erase a different proof.
                if (result.Status == WozDiagnosticSendStatus.AcceptedBySocketOnly) InvalidateProof();
                return Track(result);
            }
        }
        public WozDiagnosticSendResult SendSyntheticTask()
        {
            lock (gate)
            {
                if (!CanOwnTask) return Reject("participant_running_synthetic_session_required");
                ClearPose(); taskReceipt = null;
                return Track(transport.SendSyntheticTask(WozSyntheticLanScenario.Create(session, step, paused), revision, out task));
            }
        }
        public WozDiagnosticSendResult SendSyntheticPose()
        {
            lock (gate)
            {
                if (role != PeerRole.Operator || !Refresh()) return Reject("fresh_authenticated_synthetic_task_required");
                return Track(transport.SendSyntheticPose());
            }
        }
        bool AdvanceRevision()
        {
            ClearPose(); task = null; taskReceipt = null;
            if (revision == Int64.MaxValue) { transport.Dispose(); detail = "synthetic_revision_exhausted_new_pairing_required"; return false; }
            revision++; return true;
        }
        public WozDiagnosticSendResult NextSyntheticTask()
        {
            lock (gate)
            {
                if (!CanOwnTask || paused || step >= 6) return Reject("synthetic_next_not_available");
                if (!AdvanceRevision()) return Reject(detail);
                step++; return SendSyntheticTask();
            }
        }
        public WozDiagnosticSendResult PauseSyntheticTask()
        {
            lock (gate)
            {
                if (!CanOwnTask || paused || step < 1 || step > 4) return Reject("synthetic_pause_not_available");
                if (!AdvanceRevision()) return Reject(detail);
                paused = true; return SendSyntheticTask();
            }
        }
        public WozDiagnosticSendResult ResumeSyntheticTask()
        {
            lock (gate)
            {
                if (!CanOwnTask || !paused) return Reject("synthetic_resume_not_available");
                if (!AdvanceRevision()) return Reject(detail);
                paused = false; return SendSyntheticTask();
            }
        }
        public WozDiagnosticSendResult StopSyntheticTask()
        {
            lock (gate)
            {
                if (!CanOwnTask || step >= 6) return Reject("synthetic_stop_not_available");
                if (!AdvanceRevision()) return Reject(detail);
                step = 6; paused = false; return SendSyntheticTask();
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                transport.Dispose(); InvalidateProof();
                detail = "synthetic_session_closed_new_pairing_required";
            }
        }
    }
}
