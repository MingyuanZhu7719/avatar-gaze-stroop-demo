using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum WozDiagnosticState { Dormant, Running, Faulted, Disposed }
    public enum WozDiagnosticOrigin { InMemorySynthetic, ProcessSocketReceiveBoundary }
    public enum WozDiagnosticEventKind { AuthenticatedObservationOnly, CorrelatedClockReply, Rejected, ClockRequestExpired, TerminalFault }
    public enum WozDiagnosticSendStatus { AcceptedBySocketOnly, Rejected, TerminalFault }

    // Sanitized diagnostics only: never an exception message, stack or key.
    public sealed class WozDiagnosticFailure
    {
        public string Operation { get; private set; }
        public string ExceptionType { get; private set; }
        public int HResult { get; private set; }
        public SocketError? SocketErrorCode { get; private set; }
        public int? NativeErrorCode { get; private set; }
        internal WozDiagnosticFailure(string operation, Exception exception)
        {
            Operation = operation; ExceptionType = exception.GetType().FullName; HResult = exception.HResult;
            var socket = exception as SocketException;
            if (socket != null) { SocketErrorCode = socket.SocketErrorCode; NativeErrorCode = socket.NativeErrorCode; }
        }
    }

    public sealed class WozDiagnosticSendResult
    {
        public WozDiagnosticSendStatus Status { get; private set; }
        public string Detail { get; private set; }
        public string ExchangeId { get; private set; }
        internal WozDiagnosticSendResult(WozDiagnosticSendStatus status, string detail, string exchange = null)
        { Status = status; Detail = detail; ExchangeId = exchange; }
    }

    // Historical process-boundary observation, never live readiness, physical
    // capture time, kernel arrival, one-way latency, task authority or pause proof.
    // The nested Core result remains explicitly caller-declared at its boundary.
    public sealed class WozDiagnosticEvent
    {
        public WozDiagnosticEventKind Kind { get; private set; }
        public WozDiagnosticOrigin Origin { get; private set; }
        public string Detail { get; private set; }
        public WozAuthenticatedLanEndpoint.VerifiedMessage Message { get; private set; }
        public double? RoundTripProcessSeconds { get; private set; }
        public WozDiagnosticSendResult ReplySend { get; private set; }
        internal WozDiagnosticEvent(WozAuthenticatedDiagnosticTransport owner, object token, WozDiagnosticEventKind kind, string detail,
            WozAuthenticatedLanEndpoint.VerifiedMessage message = null, double? roundTrip = null, WozDiagnosticSendResult reply = null)
                {
            if (owner == null || !owner.OwnsResultToken(token)) throw new InvalidOperationException("Owned diagnostic result capability required");
            Kind = kind; Origin = owner.Origin; Detail = detail; Message = message; RoundTripProcessSeconds = roundTrip; ReplySend = reply;
        }
    }

    // Internal transport seam. Public construction cannot accept a fake socket,
    // clock, trust flag, receive timestamp, source or pose-admission callback.
    internal interface IWozDiagnosticSocket : IDisposable
    {
        void Start(IPEndPoint local);
        bool Poll();
        int Receive(byte[] buffer, out IPEndPoint sender, out bool truncated);
        int Send(byte[] bytes, IPEndPoint remote);
    }

    // Public clock-only surface; the sealed synthetic driver uses the narrow
    // internal bridge below. No Unity lifecycle, host/source/renderer/recording
    // interface, legacy UDP fallback or real operator sample sender is provided.
    // Key/epoch provisioning and same-runtime pairing must be reviewed separately.
    // Every terminal exit requires a fresh key and epoch, never hidden reopen.
    // This component cannot establish their entropy or uniqueness.
    public sealed class WozAuthenticatedDiagnosticTransport : IDisposable
    {
        public const int MaximumDatagramBytes = 1200;
        public const int MaximumDrainDatagrams = 16;
        public const int MaximumDrainEvents = MaximumDrainDatagrams + 2;
        readonly object gate = new object();
        readonly object resultToken = new object();
        readonly IWozDiagnosticSocket socket;
        readonly MonotonicClock clock;
        readonly PeerIdentity own;
        readonly IPEndPoint local, remote;
        readonly WozAuthenticatedLanEndpoint endpoint;
        readonly WozLanContext context;
        readonly double requestTimeout;
        readonly WozDiagnosticOrigin origin;
        readonly byte[] receiveBuffer = new byte[MaximumDatagramBytes + 1];
        long sequence;
        double lastClock = -1, pendingSent;
        string pendingId;
        WozDiagnosticState state;
        string terminalReason;
        bool closeAttempted;
        WozDiagnosticFailure primaryFailure, cleanupFailure;
        WozLanTaskContext syntheticExpectedTask;
        WozAuthenticatedLanEndpoint.VerifiedMessage syntheticReceivedTask;
        double lastSyntheticObservation = -1;
        public WozDiagnosticFailure PrimaryFailure { get { lock (gate) return primaryFailure; } }
        public WozDiagnosticFailure CleanupFailure { get { lock (gate) return cleanupFailure; } }
        internal bool OwnsResultToken(object token) { return Object.ReferenceEquals(token, resultToken); }
        public WozDiagnosticState State { get { lock (gate) return state; } }
        public string TerminalReason { get { lock (gate) return terminalReason; } }
        public WozDiagnosticOrigin Origin { get { return origin; } }

        // Construction owns only managed state. Even the System socket below
        // creates no OS socket before the explicit StartDiagnostic call.
        public WozAuthenticatedDiagnosticTransport(WozLanContext context, PeerRole role, byte[] newlyProvisionedSessionKey,
            IPEndPoint exactLocal, IPEndPoint exactRemote, double requestTimeoutSeconds = 1)
            : this(context, role, newlyProvisionedSessionKey, exactLocal, exactRemote, requestTimeoutSeconds, new SystemDiagnosticSocket()) { }

        internal WozAuthenticatedDiagnosticTransport(WozLanContext context, PeerRole role, byte[] key,
            IPEndPoint exactLocal, IPEndPoint exactRemote, double timeout, IWozDiagnosticSocket socket)
        {
            if (context == null || !Enum.IsDefined(typeof(PeerRole), role) || socket == null ||
                !WireCodec.Finite(timeout) || timeout <= 0 || timeout > 10) throw new ArgumentException("Explicit context, role and bounded timeout required");
            local = ExactUnicast(exactLocal); remote = ExactUnicast(exactRemote);
            if (local.Equals(remote)) throw new ArgumentException("Distinct exact local and remote endpoints required");
            own = role == PeerRole.Participant ? context.Participant : context.Operator;
            this.context = context;
            clock = new MonotonicClock(own.ClockId);
            endpoint = new WozAuthenticatedLanEndpoint(context, role, key, timeout);
            this.socket = socket; requestTimeout = timeout;
            // Derived from the private sealed implementation, never caller truth.
            origin = socket is SystemDiagnosticSocket ? WozDiagnosticOrigin.ProcessSocketReceiveBoundary : WozDiagnosticOrigin.InMemorySynthetic;
        }
        static IPEndPoint ExactUnicast(IPEndPoint address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork || address.Port < 1 || address.Port > 65535)
                throw new ArgumentException("Explicit IPv4 unicast address and nonzero port required");
            byte[] b = address.Address.GetAddressBytes();
            // Conservative LAN-only boundary; no wildcard, multicast, global,
            // network/broadcast-looking host, DNS, discovery or fallback endpoint.
            // This is only an address allowlist, not a subnet-mask, interface,
            // route, LAN-isolation or peer-identity check. Caller must choose
            // actual unicast host endpoints for the separately reviewed LAN.
            bool lan = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127;
            if (!lan || b[3] == 0 || b[3] == 255) throw new ArgumentException("Explicit private-LAN or loopback unicast host required");
            return new IPEndPoint(new IPAddress(b), address.Port);
        }
        static bool Recoverable(Exception e)
        { return !(e is OutOfMemoryException || e is StackOverflowException || e is AccessViolationException); }
        void Fault(string reason, string operation = null, Exception exception = null)
        {
            if (state == WozDiagnosticState.Disposed || state == WozDiagnosticState.Faulted) return;
            state = WozDiagnosticState.Faulted; terminalReason = reason; pendingId = null;
            syntheticExpectedTask = null; syntheticReceivedTask = null;
            if (exception != null) primaryFailure = new WozDiagnosticFailure(operation, exception);
            endpoint.Dispose(); CloseSocketOnce();
        }
        void CloseSocketOnce()
        {
            if (closeAttempted) return;
            closeAttempted = true;
            try { socket.Dispose(); }
            catch (Exception e) when (Recoverable(e)) { cleanupFailure = new WozDiagnosticFailure("close", e); }
            // Terminal application state is not proof of successful OS closure.
        }
        bool ReadClock(out double now)
        {
            now = 0;
            // Existing sealed stopwatch health read. This does not use or grant
            // recording admission; it reads only the privately owned stopwatch.
            if (clock.ClockId != own.ClockId || !clock.TryReadRecordingAdmissionTime(out now) || now < lastClock)
            { Fault("local_clock_failed_or_changed"); return false; }
            lastClock = now; return true;
        }
        public WozDiagnosticState StartDiagnostic()
        {
            lock (gate)
            {
                if (state != WozDiagnosticState.Dormant) return state;
                double now; if (!ReadClock(out now)) return state;
                try { socket.Start(local); state = WozDiagnosticState.Running; }
                catch (Exception e) when (Recoverable(e)) { Fault("socket_start_failed", "start", e); }
                return state;
            }
        }
        WozDiagnosticSendResult RejectedSend(string detail)
        { return new WozDiagnosticSendResult(state == WozDiagnosticState.Faulted ? WozDiagnosticSendStatus.TerminalFault : WozDiagnosticSendStatus.Rejected, detail); }
        bool NextSequence(out long next)
        {
            next = sequence;
            if (sequence == Int64.MaxValue) { Fault("send_sequence_exhausted"); return false; }
            sequence++; return true;
        }
        WozDiagnosticSendResult Send(WirePacket packet, WozLanTaskContext task = null, WozLanPoseObservation pose = null)
        {
            byte[] bytes; string error;
            if (!endpoint.TryProtect(packet, task, pose, out bytes, out error))
            { Fault("authentication_send_failed"); return RejectedSend(terminalReason); }
            if (bytes.Length > MaximumDatagramBytes) return RejectedSend("datagram_too_large_no_fragmentation");
            try
            {
                if (socket.Send(bytes, remote) != bytes.Length) { Fault("partial_datagram_send"); return RejectedSend(terminalReason); }
                return new WozDiagnosticSendResult(WozDiagnosticSendStatus.AcceptedBySocketOnly, "socket_acceptance_is_not_delivery", packet.ExchangeId);
            }
            // Send exceptions, including WouldBlock, terminate this diagnostic session.
            // This conservative policy is not a claim that the peer disconnected.
            catch (Exception e) when (Recoverable(e)) { Fault("socket_send_failed", "send", e); return RejectedSend(terminalReason); }
        }
        // At most one outstanding request. Expiration is reported by Drain;
        // callers cannot overwrite a pending request or reuse an exchange ID.
        public WozDiagnosticSendResult RequestClockDiagnostic()
        {
            lock (gate)
            {
                if (state != WozDiagnosticState.Running) return RejectedSend("not_running");
                double now; if (!ReadClock(out now)) return RejectedSend(terminalReason);
                if (pendingId != null) return RejectedSend("clock_request_pending_drain_to_expire");
                long next; if (!NextSequence(out next)) return RejectedSend(terminalReason);
                string id;
                try
                {
                    var nonce = new byte[16];
                    using (var random = RandomNumberGenerator.Create()) random.GetBytes(nonce);
                    id = "diagnostic/" + Convert.ToBase64String(nonce);
                }
                catch (Exception e) when (Recoverable(e))
                { Fault("challenge_generation_failed", "challenge", e); return RejectedSend(terminalReason); }
                var result = Send(WirePacket.TimeRequest(own, next, now, id));
                if (result.Status == WozDiagnosticSendStatus.AcceptedBySocketOnly) { pendingId = id; pendingSent = now; }
                return result;
            }
        }
        WozDiagnosticEvent Event(WozDiagnosticEventKind kind, string detail,
            WozAuthenticatedLanEndpoint.VerifiedMessage message = null, double? rtt = null, WozDiagnosticSendResult reply = null)
        { return new WozDiagnosticEvent(this, resultToken, kind, detail, message, rtt, reply); }
        void Expire(double now, List<WozDiagnosticEvent> events)
        {
            if (pendingId != null && now - pendingSent > requestTimeout)
            { events.Add(Event(WozDiagnosticEventKind.ClockRequestExpired, "no_correlated_reply_before_deadline")); pendingId = null; }
        }
        public ReadOnlyCollection<WozDiagnosticEvent> DrainDiagnostic()
        { return Drain(false); }
        internal ReadOnlyCollection<WozDiagnosticEvent> DrainSyntheticDiagnostic()
        { return Drain(true); }
        ReadOnlyCollection<WozDiagnosticEvent> Drain(bool synthetic)
        {
            lock (gate)
            {
                var events = new List<WozDiagnosticEvent>(MaximumDrainEvents);
                if (state != WozDiagnosticState.Running) return events.AsReadOnly();
                double now;
                if (!ReadClock(out now)) { events.Add(Event(WozDiagnosticEventKind.TerminalFault, terminalReason)); return events.AsReadOnly(); }
                Expire(now, events);
                for (int i = 0; i < MaximumDrainDatagrams && state == WozDiagnosticState.Running; i++)
                {
                    string operation = "poll";
                    try
                    {
                        if (!socket.Poll()) break;
                        IPEndPoint sender; bool truncated;
                        operation = "receive";
                        int count = socket.Receive(receiveBuffer, out sender, out truncated);
                        // FIRST operation after Receive: read the privately owned
                        // sealed process clock, before filtering/copying/parsing.
                        double receipt;
                        if (!ReadClock(out receipt)) { events.Add(Event(WozDiagnosticEventKind.TerminalFault, terminalReason)); break; }
                        operation = "process";
                        Expire(receipt, events);
                        if (truncated || count < 1 || count > MaximumDatagramBytes)
                        { events.Add(Event(WozDiagnosticEventKind.Rejected, "datagram_size_or_truncation")); continue; }
                        if (!remote.Equals(sender)) { events.Add(Event(WozDiagnosticEventKind.Rejected, "unexpected_endpoint")); continue; }
                        var bytes = new byte[count]; Buffer.BlockCopy(receiveBuffer, 0, bytes, 0, count);
                        if (!ReadClock(out now)) { events.Add(Event(WozDiagnosticEventKind.TerminalFault, terminalReason)); break; }
                        WozAuthenticatedLanEndpoint.VerifiedMessage verified; string error;
                        // Public clock-only path still has no pose authority. The
                        // synthetic path expects only its exact latest owned task,
                        // bounded by this participant process's original send time.
                        var expected = synthetic && syntheticExpectedTask != null &&
                            now - syntheticExpectedTask.ParticipantSentSeconds <= requestTimeout ? syntheticExpectedTask : null;
                        if (!endpoint.TryReceive(bytes, receipt, now, expected, out verified, out error))
                                                {
                            if (error == "receive_sequence_exhausted") {
                                Fault(error); events.Add(Event(WozDiagnosticEventKind.TerminalFault, terminalReason)); break;
                            }
                            events.Add(Event(WozDiagnosticEventKind.Rejected, error)); continue;
                        }
                        var packet = verified.Packet;
                        if (synthetic && packet.Kind == WireKind.Task)
                            syntheticReceivedTask = WozSyntheticLanScenario.IsScenarioTask(packet.Snapshot) ? verified : null;
                        if (packet.Sequence == Int64.MaxValue) { Fault("receive_sequence_exhausted"); events.Add(Event(WozDiagnosticEventKind.TerminalFault, terminalReason)); break; }
                        if (packet.Kind == WireKind.TimeReply)
                        {
                            if (pendingId == null || packet.ExchangeId != pendingId || packet.RequestClockId != own.ClockId ||
                                BitConverter.DoubleToInt64Bits(packet.RequestSentSeconds) != BitConverter.DoubleToInt64Bits(pendingSent))
                            { events.Add(Event(WozDiagnosticEventKind.Rejected, "unmatched_or_late_clock_reply")); continue; }
                            double rtt = receipt - pendingSent; pendingId = null;
                            // A newly confirmed own challenge never activates a
                            // task cached before that confirmation. Later datagrams
                            // in this same drain must establish a fresh binding.
                            if (synthetic) { syntheticExpectedTask = null; syntheticReceivedTask = null; }
                            events.Add(Event(WozDiagnosticEventKind.CorrelatedClockReply, "process_round_trip_only_not_one_way_or_readiness", verified, rtt));
                        }
                        else if (packet.Kind == WireKind.TimeRequest)
                        {
                            if (!ReadClock(out now)) { events.Add(Event(WozDiagnosticEventKind.TerminalFault, terminalReason)); break; }
                            long next; if (!NextSequence(out next)) { events.Add(Event(WozDiagnosticEventKind.TerminalFault, terminalReason)); break; }
                            var sent = Send(WirePacket.TimeReply(own, next, now, packet.ExchangeId, packet.RequestClockId, packet.RequestSentSeconds, receipt));
                            events.Add(Event(WozDiagnosticEventKind.AuthenticatedObservationOnly, "time_request_observed_reply_not_delivery", verified, null, sent));
                            if (state == WozDiagnosticState.Faulted) events.Add(Event(WozDiagnosticEventKind.TerminalFault, terminalReason));
                        }
                        else events.Add(Event(WozDiagnosticEventKind.AuthenticatedObservationOnly, "no_task_authority_no_pause_applied_no_ack_sent", verified));
                    }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
                    { break; } // A poll/receive readiness race, not a disconnect.
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize)
                    { events.Add(Event(WozDiagnosticEventKind.Rejected, "datagram_size_or_truncation")); }
                    catch (Exception e) when (Recoverable(e))
                    { Fault("socket_receive_or_processing_failed", operation, e); events.Add(Event(WozDiagnosticEventKind.TerminalFault, terminalReason)); }
                }
                return events.AsReadOnly();
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (state == WozDiagnosticState.Disposed) return;
                state = WozDiagnosticState.Disposed; pendingId = null; endpoint.Dispose();
                syntheticExpectedTask = null; syntheticReceivedTask = null;
                CloseSocketOnce();
            }
        }

        // Assembly-only bridge for the sealed synthetic driver. No public real
        // sample, provider, caller clock, receipt or authenticated flag is accepted.
        internal bool TryReadSyntheticTime(out double now)
        { lock (gate) { now = 0; return state == WozDiagnosticState.Running && ReadClock(out now); } }
        internal void InvalidateSyntheticBinding()
        { lock (gate) { syntheticExpectedTask = null; syntheticReceivedTask = null; } }
        bool IsSyntheticContext
        {
            get { return context.Participant.Source == ControlSource.Simulator && context.Operator.Source == ControlSource.Simulator &&
                context.Participant.SourceId.StartsWith("simulator/", StringComparison.Ordinal) &&
                context.Operator.SourceId.StartsWith("simulator/", StringComparison.Ordinal); }
        }
        internal WozDiagnosticSendResult SendSyntheticTask(TaskSnapshot task, long revision, out WozLanTaskContext sentTask)
        {
            lock (gate)
            {
                sentTask = null; syntheticExpectedTask = null;
                if (!IsSyntheticContext || own.Role != PeerRole.Participant || state != WozDiagnosticState.Running ||
                    task == null || task.SessionId != own.SessionId || !WozSyntheticLanScenario.IsScenarioTask(task))
                    return RejectedSend("synthetic_task_not_available");
                double now; long next;
                if (!ReadClock(out now) || !NextSequence(out next)) return RejectedSend(terminalReason);
                var value = new WozLanTaskContext(task, revision, next, now);
                var sent = Send(WirePacket.Task(own, next, now, task), value);
                if (sent.Status == WozDiagnosticSendStatus.AcceptedBySocketOnly) syntheticExpectedTask = sentTask = value;
                return sent;
            }
        }
        internal WozDiagnosticSendResult SendSyntheticPose()
        {
            lock (gate)
            {
                if (!IsSyntheticContext || own.Role != PeerRole.Operator || state != WozDiagnosticState.Running || syntheticReceivedTask == null)
                    return RejectedSend("no_authenticated_synthetic_task");
                var received = syntheticReceivedTask;
                double capture;
                if (!ReadClock(out capture)) return RejectedSend(terminalReason);
                if (capture - received.OriginalLocalReceiptSeconds > requestTimeout)
                { syntheticReceivedTask = null; return RejectedSend("synthetic_task_stale"); }
                if (!WozSyntheticLanScenario.CanShowPose(received.TaskContext.Task)) return RejectedSend("synthetic_phase_or_D_hidden");
                if (capture <= received.OriginalLocalReceiptSeconds) return RejectedSend("await_fresh_synthetic_observation");
                // Deterministic geometry generated here, never relabelled SDK or
                // caller data. Times are process observations, not sensor capture.
                var task = received.TaskContext.Task;
                double yaw = ((int)task.Condition.Value - 1) * 0.2;
                var head = new Rotation(0, Math.Sin(yaw / 2), 0, Math.Cos(yaw / 2));
                var eye = new Vec3(Math.Sin(yaw), 0, Math.Cos(yaw));
                double observed, sentAt; long next;
                if (!ReadClock(out observed)) return RejectedSend(terminalReason);
                if (observed <= lastSyntheticObservation) return RejectedSend("await_fresh_synthetic_observation");
                if (!ReadClock(out sentAt) || !NextSequence(out next)) return RejectedSend(terminalReason);
                var sample = new HeadEyeSample(own.SessionId, own.SourceId, context.OperatorDevice, own.ClockId,
                    task.BlockIndex, task.Segment, next, capture, sentAt, ControlSource.Simulator, StreamRole.OperatorControl, head, eye, true, true);
                var observation = new WozLanPoseObservation(received.OriginalLocalReceiptSeconds, observed, observed);
                var result = Send(WirePacket.Tracking(own, next, sample), received.TaskContext, observation);
                if (result.Status == WozDiagnosticSendStatus.AcceptedBySocketOnly) lastSyntheticObservation = observed;
                return result;
            }
        }

        // Only explicit Start creates the socket. Focused memory tests never
        // start this implementation; real loopback evidence is reviewed separately.
        // Fixed IPv4 unicast bind, nonblocking bounded receive, DF, no discovery.
        sealed class SystemDiagnosticSocket : IWozDiagnosticSocket
        {
            Socket value;
            public void Start(IPEndPoint address)
            {
                if (value != null) throw new InvalidOperationException("Cannot reopen diagnostic socket");
                value = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                value.ExclusiveAddressUse = true; value.Blocking = false;
                value.EnableBroadcast = false; value.DontFragment = true;
                value.ReceiveBufferSize = 16384; value.Bind(address);
            }
            public bool Poll() { return value.Poll(0, SelectMode.SelectRead); }
            public int Receive(byte[] buffer, out IPEndPoint sender, out bool truncated)
            {
                EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                int count = value.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref source);
                sender = (IPEndPoint)source; truncated = count > MaximumDatagramBytes; return count;
            }
            public int Send(byte[] bytes, IPEndPoint address) { return value.SendTo(bytes, address); }
            public void Dispose() { if (value != null) { value.Dispose(); value = null; } }
        }
    }
}



