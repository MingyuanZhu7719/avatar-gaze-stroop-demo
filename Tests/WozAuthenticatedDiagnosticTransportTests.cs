using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Only sealed memory sockets are started. Synthetic fixed keys are never
    // provisioned for an actual session. Reflection mutates ONLY these newly
    // constructed local test objects to simulate otherwise inaccessible faults.
    public static class WozAuthenticatedDiagnosticTransportTests
    {
        sealed class FakeSocket : IWozDiagnosticSocket
        {
            internal readonly Queue<Tuple<byte[], IPEndPoint, bool>> Incoming = new Queue<Tuple<byte[], IPEndPoint, bool>>();
            internal readonly List<byte[]> Sent = new List<byte[]>();
            internal IPEndPoint Local = null, Destination = null;
            internal FakeSocket Other = null;
            internal MonotonicClock ObserveClock = null;
            internal double BeforeReturn = -1;
            internal bool Closed = false, FailStart = false, FailSend = false, Partial = false, FailPoll = false, FailReceive = false, MessageSize = false, WouldBlock = false, FailDispose = false;
            internal int Starts = 0, Receives = 0, Polls = 0, DisposeCalls = 0;
            internal SocketError StartError = SocketError.AddressAlreadyInUse, SendError = SocketError.AccessDenied;
            internal bool StopClockAtReturn = false;
            public void Start(IPEndPoint local) { Starts++; if (FailStart) throw new SocketException((int)StartError); Local = local; }
            public bool Poll() { Polls++; if (Closed) throw new ObjectDisposedException("fake"); if (FailPoll) throw new SocketException((int)SocketError.NetworkDown); return Incoming.Count > 0; }
            public int Receive(byte[] buffer, out IPEndPoint sender, out bool truncated)
            {
                Receives++; if (FailReceive) throw new SocketException((int)SocketError.ConnectionReset);
                if (WouldBlock) throw new SocketException((int)SocketError.WouldBlock);
                var item = Incoming.Dequeue();
                if (MessageSize) throw new SocketException((int)SocketError.MessageSize);
                sender = item.Item2; truncated = item.Item3;
                Buffer.BlockCopy(item.Item1, 0, buffer, 0, Math.Min(item.Item1.Length, buffer.Length));
                if (ObserveClock != null) BeforeReturn = ObserveClock.Now;
                if (StopClockAtReturn) ((Stopwatch)Field(ObserveClock, "timer")).Stop();
                return item.Item1.Length;
            }
            public int Send(byte[] bytes, IPEndPoint remote)
            {
                if (Closed) throw new ObjectDisposedException("fake"); if (FailSend) throw new SocketException((int)SendError);
                Sent.Add((byte[])bytes.Clone()); Destination = remote;
                if (Partial) return bytes.Length - 1;
                if (Other != null) Other.Queue(bytes, Local);
                return bytes.Length;
            }
            internal void Queue(byte[] bytes, IPEndPoint sender = null, bool truncated = false)
            { Incoming.Enqueue(Tuple.Create((byte[])bytes.Clone(), sender ?? PAddress(), truncated)); }
            public void Dispose() { DisposeCalls++; if (FailDispose) throw new SocketException((int)SocketError.NotSocket); Closed = true; }
        }
        static byte[] Key() { var b = new byte[32]; for (int i = 0; i < b.Length; i++) b[i] = (byte)i; return b; }
        static WozLanContext Context(bool other = false)
        {
            var epoch = new byte[16]; epoch[0] = (byte)(other ? 2 : 1);
            return new WozLanContext(P(), O(), epoch, "synthetic-map", "synthetic-device");
        }
        static PeerIdentity P() { return new PeerIdentity("synthetic-session", "p", PeerRole.Participant, "task", ControlSource.LiveOperator, "p-clock"); }
        static PeerIdentity O() { return new PeerIdentity("synthetic-session", "o", PeerRole.Operator, "op", ControlSource.LiveOperator, "o-clock"); }
        static IPEndPoint PAddress() { return new IPEndPoint(IPAddress.Loopback, 43101); }
        static IPEndPoint OAddress() { return new IPEndPoint(IPAddress.Loopback, 43102); }
        static WozAuthenticatedDiagnosticTransport Make(FakeSocket socket, PeerRole role = PeerRole.Operator, double timeout = 1)
        { return new WozAuthenticatedDiagnosticTransport(Context(), role, Key(), role == PeerRole.Operator ? OAddress() : PAddress(), role == PeerRole.Operator ? PAddress() : OAddress(), timeout, socket); }
        static object Field(object target, string name) { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        static void SetField(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        static void Assert(bool ok, string message = "Assertion failed") { if (!ok) throw new Exception(message); }
        static void Eq<T>(T expected, T actual) { Assert(Object.Equals(expected, actual), "Expected " + expected + ", got " + actual); }
        static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        static byte[] Protect(WirePacket packet, WozLanTaskContext task = null, WozLanPoseObservation pose = null, WozLanContext context = null)
        {
            using (var endpoint = new WozAuthenticatedLanEndpoint(context ?? Context(), packet.Peer.Role, Key(), 1))
            { byte[] bytes; string error; Assert(endpoint.TryProtect(packet, task, pose, out bytes, out error), error); return bytes; }
        }
        static WirePacket Decode(byte[] bytes, PeerRole receiver = PeerRole.Participant)
        {
            using (var endpoint = new WozAuthenticatedLanEndpoint(Context(), receiver, Key(), 1))
            { WozAuthenticatedLanEndpoint.VerifiedMessage message; string error; Assert(endpoint.TryReceive(bytes, 1, 1, null, out message, out error), error); return message.Packet; }
        }
        static void Rejected(WozAuthenticatedDiagnosticTransport transport, FakeSocket socket, byte[] bytes, string reason = null, IPEndPoint sender = null, bool truncated = false)
        {
            socket.Queue(bytes, sender, truncated); var events = transport.DrainDiagnostic(); Eq(1, events.Count);
            Eq(WozDiagnosticEventKind.Rejected, events[0].Kind); Assert(events[0].Message == null);
            if (reason != null) Eq(reason, events[0].Detail); Eq(WozDiagnosticState.Running, transport.State);
        }
        static byte[] Reply(WirePacket request, long sequence = 0, string id = null, string clock = null, double? sent = null)
        { return Protect(WirePacket.TimeReply(P(), sequence, 1 + sequence, id ?? request.ExchangeId, clock ?? request.RequestClockId, sent ?? request.RequestSentSeconds, 0.5 + sequence)); }
        static byte[] WrongClock(byte[] bytes)
        {
            var old = Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes("o-clock")));
            var replacement = Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes("x-clock")));
            bool changed = false;
            for (int i = 47; i <= bytes.Length - 32 - old.Length; i++) {
                bool equal = true; for (int j = 0; j < old.Length; j++) equal &= bytes[i + j] == old[j];
                if (equal) { Buffer.BlockCopy(replacement, 0, bytes, i, replacement.Length); changed = true; break; }
            }
            Assert(changed);
            using (var mac = new HMACSHA256(Key())) { var tag = mac.ComputeHash(bytes, 0, bytes.Length - 32); Buffer.BlockCopy(tag, 0, bytes, bytes.Length - 32, 32); }
            return bytes;
        }
        static int passed, failed;
        static void Run(string name, Action action)
        { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + " " + e); } }
        public static int RunAll()
        {
            passed = 0; failed = 0;
            Run("public construction dormant no OS socket and no public injection", () => {
                using (var t = new WozAuthenticatedDiagnosticTransport(Context(), PeerRole.Operator, Key(), OAddress(), PAddress())) {
                    Eq(WozDiagnosticState.Dormant, t.State); Assert(Field(Field(t, "socket"), "value") == null);
                    Eq(WozDiagnosticSendStatus.Rejected, t.RequestClockDiagnostic().Status); Eq(0, t.DrainDiagnostic().Count);
                    t.Dispose(); Eq(WozDiagnosticState.Disposed, t.StartDiagnostic()); Assert(Field(Field(t, "socket"), "value") == null);
                }
                Eq(1, typeof(WozAuthenticatedDiagnosticTransport).GetConstructors().Length);
                foreach (var method in typeof(WozAuthenticatedDiagnosticTransport).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    foreach (var parameter in method.GetParameters()) Assert(parameter.ParameterType != typeof(bool) && parameter.ParameterType != typeof(IClock));
                Assert(typeof(WozAuthenticatedDiagnosticTransport).GetInterfaces().Length == 1);
            });
            Run("exact LAN endpoints only cloned no wildcard broadcast multicast or equal", () => {
                foreach (string bad in new[] { "0.0.0.0", "255.255.255.255", "224.0.0.1", "8.8.8.8", "192.168.1.255", "192.168.1.0", "::1" })
                    Throws<ArgumentException>(() => new WozAuthenticatedDiagnosticTransport(Context(), PeerRole.Operator, Key(), new IPEndPoint(IPAddress.Parse(bad), 43102), PAddress()));
                Throws<ArgumentException>(() => new WozAuthenticatedDiagnosticTransport(Context(), PeerRole.Operator, Key(), OAddress(), OAddress()));
                Throws<ArgumentException>(() => new WozAuthenticatedDiagnosticTransport(Context(), PeerRole.Operator, Key(), new IPEndPoint(IPAddress.Loopback, 0), PAddress()));
            });
            Run("owned receive boundary timestamps preserved synthetic origin and actual correlation", () => {
                var a = new FakeSocket(); var b = new FakeSocket(); a.Other = b; b.Other = a;
                using (var op = Make(a)) using (var p = Make(b, PeerRole.Participant)) {
                    Eq(WozDiagnosticState.Running, op.StartDiagnostic()); Eq(WozDiagnosticState.Running, p.StartDiagnostic());
                    b.ObserveClock = (MonotonicClock)Field(p, "clock"); a.ObserveClock = (MonotonicClock)Field(op, "clock");
                    Eq(WozDiagnosticSendStatus.AcceptedBySocketOnly, op.RequestClockDiagnostic().Status);
                    Eq(WozDiagnosticSendStatus.Rejected, op.RequestClockDiagnostic().Status);
                    var received = p.DrainDiagnostic(); Eq(1, received.Count); var request = received[0];
                    Eq(WozDiagnosticOrigin.InMemorySynthetic, request.Origin); Assert(!request.Message.ReceiptCapturedBySocket);
                    Assert(request.Message.OriginalLocalReceiptSeconds >= b.BeforeReturn);
                    Eq(WozDiagnosticSendStatus.AcceptedBySocketOnly, request.ReplySend.Status);
                    var reply = op.DrainDiagnostic()[0]; Eq(WozDiagnosticEventKind.CorrelatedClockReply, reply.Kind);
                    Assert(reply.Message.OriginalLocalReceiptSeconds >= a.BeforeReturn); Assert(reply.RoundTripProcessSeconds >= 0);
                    double receipt = reply.Message.OriginalLocalReceiptSeconds; Thread.Sleep(2); Eq(receipt, reply.Message.OriginalLocalReceiptSeconds);
                    Eq(PAddress(), a.Destination); Eq(OAddress(), b.Destination);
                    Rejected(op, a, b.Sent[0], "replay_or_remote_clock");
                }
            });
            Run("wrong endpoint MAC context replay never creates receipt evidence", () => {
                var s = new FakeSocket(); using (var t = Make(s)) {
                    t.StartDiagnostic(); var valid = Protect(WirePacket.TimeRequest(P(), 0, 1, "incoming"));
                    Rejected(t, s, valid, "unexpected_endpoint", new IPEndPoint(IPAddress.Loopback, 43103));
                    var bad = (byte[])valid.Clone(); bad[bad.Length - 1] ^= 1; Rejected(t, s, bad, "mac_mismatch");
                    Rejected(t, s, Protect(WirePacket.TimeRequest(P(), 0, 1, "incoming"), context: Context(true)), "envelope_context");
                    s.Queue(valid); var e = t.DrainDiagnostic()[0]; Assert(e.Message != null); var receipt = e.Message.OriginalLocalReceiptSeconds;
                    Rejected(t, s, valid, "replay_or_remote_clock"); Eq(receipt, e.Message.OriginalLocalReceiptSeconds);
                }
            });
            Run("exact pending id clock and original send timestamp required before diagnostics", () => {
                var s = new FakeSocket(); using (var t = Make(s)) {
                    t.StartDiagnostic(); t.RequestClockDiagnostic(); var request = Decode(s.Sent[0]);
                    Rejected(t, s, Reply(request, 0, id: "other"), "unmatched_or_late_clock_reply");
                    Rejected(t, s, WrongClock(Reply(request, 1)));
                    Rejected(t, s, Reply(request, 2, sent: request.RequestSentSeconds + .001), "unmatched_or_late_clock_reply");
                    s.Queue(Reply(request, 3)); Eq(WozDiagnosticEventKind.CorrelatedClockReply, t.DrainDiagnostic()[0].Kind);
                    Rejected(t, s, Reply(request, 4), "unmatched_or_late_clock_reply");
                }
            });
            Run("missing reply expires separately late reply rejected no fault and new request allowed", () => {
                var s = new FakeSocket(); using (var t = Make(s, timeout: .01)) {
                    t.StartDiagnostic(); t.RequestClockDiagnostic(); var request = Decode(s.Sent[0]); Thread.Sleep(25);
                    var events = t.DrainDiagnostic(); Eq(1, events.Count); Eq(WozDiagnosticEventKind.ClockRequestExpired, events[0].Kind);
                    Eq(WozDiagnosticState.Running, t.State); Rejected(t, s, Reply(request), "unmatched_or_late_clock_reply");
                    Eq(WozDiagnosticSendStatus.AcceptedBySocketOnly, t.RequestClockDiagnostic().Status);
                    Assert(Decode(s.Sent[1]).ExchangeId != request.ExchangeId);
                }
            });
            Run("oversize truncation empty and socket MessageSize rejected without peer evidence", () => {
                var s = new FakeSocket(); using (var t = Make(s)) {
                    t.StartDiagnostic(); Rejected(t, s, new byte[1201], "datagram_size_or_truncation"); Rejected(t, s, new byte[0], "datagram_size_or_truncation");
                    var bytes = Protect(WirePacket.TimeRequest(P(), 0, 1, "in"));
                    Rejected(t, s, bytes, "datagram_size_or_truncation", truncated: true);
                    var shortBytes = new byte[bytes.Length - 1]; Array.Copy(bytes, shortBytes, shortBytes.Length); Rejected(t, s, shortBytes);
                    s.MessageSize = true; Rejected(t, s, bytes, "datagram_size_or_truncation"); s.MessageSize = false;
                    s.Queue(bytes); Assert(t.DrainDiagnostic()[0].Message != null);
                }
            });
            Run("receive work hard bounded even invalid flood and poll readiness race", () => {
                var s = new FakeSocket(); using (var t = Make(s)) {
                    t.StartDiagnostic(); for (int i = 0; i < 100; i++) s.Queue(new byte[1]);
                    Eq(16, t.DrainDiagnostic().Count); Eq(16, s.Receives); Eq(84, s.Incoming.Count);
                    s.WouldBlock = true; Eq(0, t.DrainDiagnostic().Count); Eq(WozDiagnosticState.Running, t.State);
                }
            });
            Run("send failure and partial write terminal no delivery claim no reopen", () => {
                foreach (bool partial in new[] { false, true }) {
                    var s = new FakeSocket { Partial = partial, FailSend = !partial }; using (var t = Make(s)) {
                        t.StartDiagnostic(); Eq(WozDiagnosticSendStatus.TerminalFault, t.RequestClockDiagnostic().Status);
                        Eq(WozDiagnosticState.Faulted, t.State); Assert(s.Closed); Eq(WozDiagnosticState.Faulted, t.StartDiagnostic()); Eq(1, s.Starts);
                    }
                }
            });
            Run("start poll receive errors and disposal close terminally without hidden reopen", () => {
                for (int mode = 0; mode < 3; mode++) {
                    var s = new FakeSocket { FailStart = mode == 0, FailPoll = mode == 1, FailReceive = mode == 2 };
                    using (var t = Make(s)) { t.StartDiagnostic(); s.Queue(new byte[1]); t.DrainDiagnostic(); Eq(WozDiagnosticState.Faulted, t.State); Assert(s.Closed); t.StartDiagnostic(); Eq(1, s.Starts); }
                }
                var d = new FakeSocket { FailDispose = true }; var disposed = Make(d); disposed.StartDiagnostic(); disposed.Dispose(); disposed.Dispose();
                Eq(WozDiagnosticState.Disposed, disposed.State); Eq(WozDiagnosticState.Disposed, disposed.StartDiagnostic()); Eq(1, d.Starts); Eq(0, disposed.DrainDiagnostic().Count);
            });
            Run("owned clock rollback identity change and stopped stopwatch latch terminal", () => {
                for (int mode = 0; mode < 3; mode++) {
                    var s = new FakeSocket(); using (var t = Make(s)) {
                        t.StartDiagnostic(); var clock = (MonotonicClock)Field(t, "clock");
                        if (mode == 0) SetField(t, "lastClock", Double.MaxValue);
                        if (mode == 1) SetField(clock, "<ClockId>k__BackingField", "changed");
                        if (mode == 2) ((Stopwatch)Field(clock, "timer")).Stop();
                        Eq(WozDiagnosticEventKind.TerminalFault, t.DrainDiagnostic()[0].Kind);
                        Eq(WozDiagnosticState.Faulted, t.State); Assert(s.Closed); Eq(WozDiagnosticState.Faulted, t.StartDiagnostic());
                    }
                }
            });
            Run("sanitized operation error codes and separate cleanup failure preserved", () => {
                foreach (var error in new[] { SocketError.AddressAlreadyInUse, SocketError.AccessDenied }) {
                    var s = new FakeSocket { FailStart = true, StartError = error }; using (var t = Make(s)) {
                        t.StartDiagnostic(); Eq("start", t.PrimaryFailure.Operation); Eq(error, t.PrimaryFailure.SocketErrorCode.Value);
                        Eq(typeof(SocketException).FullName, t.PrimaryFailure.ExceptionType); Assert(t.PrimaryFailure.HResult != 0);
                        Assert(t.PrimaryFailure.NativeErrorCode.HasValue); Assert(t.CleanupFailure == null);
                    }
                }
                var broken = new FakeSocket { FailSend = true, FailDispose = true }; var transport = Make(broken);
                transport.StartDiagnostic(); transport.RequestClockDiagnostic(); var primary = transport.PrimaryFailure;
                Eq("send", primary.Operation); Eq(SocketError.AccessDenied, primary.SocketErrorCode.Value);
                Eq("close", transport.CleanupFailure.Operation); Eq(SocketError.NotSocket, transport.CleanupFailure.SocketErrorCode.Value);
                Eq("socket_send_failed", transport.TerminalReason); Assert(!broken.Closed); Eq(1, broken.DisposeCalls);
                transport.Dispose(); Eq(primary, transport.PrimaryFailure); Eq("socket_send_failed", transport.TerminalReason); Eq(1, broken.DisposeCalls);
                foreach (var property in typeof(WozDiagnosticFailure).GetProperties()) Assert(property.Name != "Message" && property.Name != "StackTrace");
                var poll = new FakeSocket { FailPoll = true }; using (var t = Make(poll)) { t.StartDiagnostic(); t.DrainDiagnostic(); Eq("poll", t.PrimaryFailure.Operation); }
                var receive = new FakeSocket { FailReceive = true }; using (var t = Make(receive)) { t.StartDiagnostic(); receive.Queue(new byte[1]); t.DrainDiagnostic(); Eq("receive", t.PrimaryFailure.Operation); }
            });
            Run("receive-boundary clock failure emits no message and result capability cannot be forged", () => {
                var s = new FakeSocket(); using (var t = Make(s)) {
                    t.StartDiagnostic(); s.ObserveClock = (MonotonicClock)Field(t, "clock"); s.StopClockAtReturn = true;
                    s.Queue(Protect(WirePacket.TimeRequest(P(), 0, 1, "incoming")));
                    var result = t.DrainDiagnostic(); Eq(WozDiagnosticEventKind.TerminalFault, result[0].Kind);
                    Assert(result[0].Message == null); Eq(0, s.Sent.Count); Assert(s.Closed);
                    Throws<InvalidOperationException>(() => new WozDiagnosticEvent(t, new object(), WozDiagnosticEventKind.AuthenticatedObservationOnly, "forged"));
                }
            });
            Run("send WouldBlock is deliberately terminal without a peer disconnect claim", () => {
                var s = new FakeSocket { FailSend = true, SendError = SocketError.WouldBlock }; using (var t = Make(s)) {
                    t.StartDiagnostic(); Eq(WozDiagnosticSendStatus.TerminalFault, t.RequestClockDiagnostic().Status);
                    Eq(SocketError.WouldBlock, t.PrimaryFailure.SocketErrorCode.Value); Eq("socket_send_failed", t.TerminalReason); Assert(s.Closed);
                }
            });
            Run("send and receive sequence exhaustion terminate epoch rather than stay reusable", () => {
                var s = new FakeSocket(); using (var t = Make(s)) {
                    t.StartDiagnostic(); SetField(t, "sequence", Int64.MaxValue);
                    Eq(WozDiagnosticSendStatus.TerminalFault, t.RequestClockDiagnostic().Status); Eq("send_sequence_exhausted", t.TerminalReason); Eq(0, s.Sent.Count);
                }
                var remote = new FakeSocket(); using (var t = Make(remote)) {
                    t.StartDiagnostic(); remote.Queue(Protect(WirePacket.TimeRequest(P(), Int64.MaxValue, 1, "last")));
                    Eq(WozDiagnosticEventKind.TerminalFault, t.DrainDiagnostic()[0].Kind); Eq("receive_sequence_exhausted", t.TerminalReason);
                    Eq(WozDiagnosticState.Faulted, t.State); Assert(remote.Closed); Eq(0, remote.Sent.Count);
                }
            });
            Run("task and stop observation never apply pause or acknowledge and pose has no host context", () => {
                var task = new WozLanTaskContext(new TaskSnapshot("synthetic-session", 0, 0, "trial", Colour.Red, Colour.Blue, Phase.MainTask, Condition.B), 1, 0, 1);
                var s = new FakeSocket(); using (var t = Make(s)) {
                    t.StartDiagnostic(); s.Queue(Protect(WirePacket.Task(P(), 0, 1, task.Task), task));
                    Eq(WozDiagnosticEventKind.AuthenticatedObservationOnly, t.DrainDiagnostic()[0].Kind); Eq(0, s.Sent.Count);
                    s.Queue(Protect(WirePacket.SafetyStopAck(P(), 1, 2, "unknown-stop")));
                    Eq("no_task_authority_no_pause_applied_no_ack_sent", t.DrainDiagnostic()[0].Detail); Eq(0, s.Sent.Count);
                }
                var p = new FakeSocket(); using (var t = Make(p, PeerRole.Participant)) {
                    t.StartDiagnostic(); p.Queue(Protect(WirePacket.SafetyStopRequest(O(), 0, 1, "stop")), OAddress());
                    Eq("no_task_authority_no_pause_applied_no_ack_sent", t.DrainDiagnostic()[0].Detail); Eq(0, p.Sent.Count);
                    var pose = new HeadEyeSample("synthetic-session", "op", "synthetic-device", "o-clock", 0, 0, 1, 2.5, 4,
                        ControlSource.LiveOperator, StreamRole.OperatorControl, Rotation.Identity, new Vec3(0, 0, 1), true, true);
                    Rejected(t, p, Protect(WirePacket.Tracking(O(), 1, pose), task, new WozLanPoseObservation(2, 3, 3)), "unexpected_pose_task", OAddress());
                }
            });
            Console.WriteLine("RESULT " + passed + " passed; " + failed + " failed; in-memory only, no OS sockets opened");
            return failed;
        }
    }
}



