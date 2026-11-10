using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    // Focused OFFLINE byte-array tests. Fixed keys/epochs and recreated receivers
    // exist only to test rejection. Never use this provisioning in a real session.
    // No sockets, SDK, Unity player, file recording, reflection or process probes.
    public static class WozAuthenticatedLanTests
    {
        const string Golden = "V09aTEFOMDEBAI9m+RwmQ92BzajzeitgEQ9TocW9HgAcuTrzDUtksSDnAAAAcwBBU1d8MnxUaW1lUmVxdWVzdHxiR0Z1TFhSbGMzUT18YjNCbGNtRjBiM0k9fE9wZXJhdG9yfGIzQmxjbUYwYjNJdGMzUnlaV0Z0fExpdmVPcGVyYXRvcnxieTFqYkc5amF3PT18N3wxMi41fGMzbHVZeTB4n9263YNN6HLIq7aXQUwab2iGKuTDl6UdJgoO3P8Mp+s=";
        static byte[] Key() { var b = new byte[32]; for (int i = 0; i < b.Length; i++) b[i] = (byte)i; return b; }
        static byte[] Epoch() { var b = new byte[16]; for (int i = 0; i < b.Length; i++) b[i] = (byte)(i + 1); return b; }
        static PeerIdentity P(string session = "lan-test", string peer = "participant", string source = "task-host", string clock = "p-clock", ControlSource kind = ControlSource.LiveOperator)
        { return new PeerIdentity(session, peer, PeerRole.Participant, source, kind, clock); }
        static PeerIdentity O(string session = "lan-test", string peer = "operator", string source = "operator-stream", string clock = "o-clock", ControlSource kind = ControlSource.LiveOperator)
        { return new PeerIdentity(session, peer, PeerRole.Operator, source, kind, clock); }
        static WozLanContext Context() { return new WozLanContext(P(), O(), Epoch(), "map-1", "operator-device"); }
        static WozAuthenticatedLanEndpoint End(PeerRole role, WozLanContext context = null, byte[] key = null)
        { return new WozAuthenticatedLanEndpoint(context ?? Context(), role, key ?? Key(), 1); }
        static void Assert(bool ok, string message = "Assertion failed") { if (!ok) throw new Exception(message); }
        static void Eq<T>(T a, T b) { Assert(Object.Equals(a, b), "Expected " + a + ", actual " + b); }
        static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        static WirePacket Sync(long sequence = 7, double sent = 12.5, string id = "sync-1") { return WirePacket.TimeRequest(O(), sequence, sent, id); }
        static WozLanTaskContext Task(long revision = 4, long sequence = 2, double sent = 5, string trial = "trial-1", int segment = 0, Condition condition = Condition.B, Phase phase = Phase.MainTask)
        { return new WozLanTaskContext(new TaskSnapshot("lan-test", 0, segment, trial, Colour.Red, Colour.Blue, phase, condition), revision, sequence, sent); }
        static WirePacket Pose(long sequence = 8, double sent = 13, int segment = 0, string device = "operator-device", double capture = 11.5)
        {
            return WirePacket.Tracking(O(), sequence, new HeadEyeSample("lan-test", "operator-stream", device, "o-clock", 0, segment,
                sequence, capture, sent, ControlSource.LiveOperator, StreamRole.OperatorControl, Rotation.Identity, new Vec3(0, 0, 1), true, true));
        }
        static WozLanPoseObservation Observation() { return new WozLanPoseObservation(11, 12, 12.1); }
        static byte[] Protect(WirePacket packet, WozLanTaskContext task = null, WozLanPoseObservation pose = null)
        { using (var endpoint = End(packet.Peer.Role)) { byte[] bytes; string error; Assert(endpoint.TryProtect(packet, task, pose, out bytes, out error), error); return bytes; } }
        static WozAuthenticatedLanEndpoint.VerifiedMessage Receive(WozAuthenticatedLanEndpoint receiver, byte[] bytes, WozLanTaskContext task = null, double receipt = 20, double now = 20.1)
        { WozAuthenticatedLanEndpoint.VerifiedMessage message; string error; Assert(receiver.TryReceive(bytes, receipt, now, task, out message, out error), error); Assert(message != null); return message; }
        static void Rejected(WozAuthenticatedLanEndpoint receiver, byte[] bytes, string expected = null, WozLanTaskContext task = null, double receipt = 20, double now = 20.1)
        { WozAuthenticatedLanEndpoint.VerifiedMessage message; string error; Assert(!receiver.TryReceive(bytes, receipt, now, task, out message, out error)); Assert(message == null); if (expected != null) Eq(expected, error); }
        static byte[] Copy(byte[] b) { return (byte[])b.Clone(); }
        static byte[] Resign(byte[] bytes)
        { using (var hmac = new HMACSHA256(Key())) { var tag = hmac.ComputeHash(bytes, 0, bytes.Length - 32); Buffer.BlockCopy(tag, 0, bytes, bytes.Length - 32, 32); } return bytes; }
        static byte[] ReplacePayload(byte[] bytes, string payload)
        {
            int offset = 47 + bytes[42] + 256 * bytes[43] + bytes[44]; byte[] text = Encoding.ASCII.GetBytes(payload);
            var result = new byte[offset + text.Length + 32]; Buffer.BlockCopy(bytes, 0, result, 0, offset);
            result[45] = (byte)text.Length; result[46] = (byte)(text.Length >> 8); Buffer.BlockCopy(text, 0, result, offset, text.Length); return Resign(result);
        }
        static string Payload(byte[] bytes)
        { int start = 47 + bytes[42] + 256 * bytes[43] + bytes[44]; return Encoding.ASCII.GetString(bytes, start, bytes[45] + 256 * bytes[46]); }
        static int passed, failed;
        static void Run(string name, Action action)
        { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + " " + e); } }

        public static int RunAll()
        {
            passed = 0; failed = 0;
            Run("independent full envelope known answer and immutable original receipt", () => {
                // Golden was serialized independently in PowerShell from the
                // documented spec, not obtained from this encoder. Full 32-byte
                // expected tag: 9fddbadd834de872c8abb697414c1a6f68862ae4c397a51d260a0edcff0ca7eb.
                byte[] bytes = Protect(Sync()); Eq(Golden, Convert.ToBase64String(bytes)); Eq(194, bytes.Length);
                using (var receiver = End(PeerRole.Participant)) {
                    var message = Receive(receiver, bytes); Eq(20.0, message.OriginalLocalReceiptSeconds); Eq("p-clock", message.ReceiptClockId);
                    Eq(WireKind.TimeRequest, message.Packet.Kind); Assert(!message.ReceiptCapturedBySocket);
                    bytes[0] ^= 1; Eq("sync-1", message.Packet.ExchangeId); receiver.Dispose(); Eq(20.0, message.OriginalLocalReceiptSeconds);
                }
            });
            Run("every envelope byte tamper rejected without advancing high water", () => {
                byte[] bytes = Protect(Sync()); using (var receiver = End(PeerRole.Participant)) {
                    for (int i = 0; i < bytes.Length; i++) { var bad = Copy(bytes); bad[i] ^= 1; Rejected(receiver, bad, "mac_mismatch"); }
                    Receive(receiver, bytes);
                }
            });
            Run("wrong key and owned key/epoch copies", () => {
                var key = Key(); var epoch = Epoch(); var context = new WozLanContext(P(), O(), epoch, "map-1", "operator-device");
                using (var sender = End(PeerRole.Operator, context, key)) {
                    Array.Clear(key, 0, key.Length); Array.Clear(epoch, 0, epoch.Length); byte[] bytes; string error;
                    Assert(sender.TryProtect(Sync(), null, null, out bytes, out error), error); Eq(Golden, Convert.ToBase64String(bytes));
                    key = Key(); key[1] ^= 3; using (var receiver = End(PeerRole.Participant, null, key)) Rejected(receiver, bytes, "mac_mismatch");
                }
            });
            Run("context digest binds both identities roles session epoch sources clocks mapping and device", () => {
                var epoch = Epoch(); epoch[0] ^= 1;
                var contexts = new[] {
                    new WozLanContext(P("other"), O("other"), Epoch(), "map-1", "operator-device"),
                    new WozLanContext(P(peer: "other-p"), O(), Epoch(), "map-1", "operator-device"),
                    new WozLanContext(P(), O(peer: "other-o"), Epoch(), "map-1", "operator-device"),
                    new WozLanContext(P(source: "other-p-source"), O(), Epoch(), "map-1", "operator-device"),
                    new WozLanContext(P(), O(source: "other-o-source"), Epoch(), "map-1", "operator-device"),
                    new WozLanContext(P(clock: "other-p-clock"), O(), Epoch(), "map-1", "operator-device"),
                    new WozLanContext(P(), O(clock: "other-o-clock"), Epoch(), "map-1", "operator-device"),
                    new WozLanContext(P(), O(), epoch, "map-1", "operator-device"),
                    new WozLanContext(P(), O(), Epoch(), "map-2", "operator-device"),
                    new WozLanContext(P(), O(), Epoch(), "map-1", "other-device"),
                    new WozLanContext(P(kind: ControlSource.Simulator), O(source: "simulator/fixture", kind: ControlSource.Simulator), Epoch(), "map-1", "operator-device") };
                var bytes = Protect(Sync()); foreach (var context in contexts) using (var receiver = End(PeerRole.Participant, context)) Rejected(receiver, bytes, "envelope_context");
                using (var reflected = End(PeerRole.Operator)) Rejected(reflected, bytes, "envelope_context");
            });
            Run("validly MACed invalid header direction lengths version and malformed payload", () => {
                var bytes = Protect(Sync()); using (var receiver = End(PeerRole.Participant)) {
                    foreach (int offset in new[] { 0, 8, 9, 10, 42, 43, 44, 45, 46 }) { var bad = Copy(bytes); bad[offset] ^= 128; Rejected(receiver, Resign(bad)); }
                    Rejected(receiver, ReplacePayload(bytes, Payload(bytes).Replace("ASW|2|", "ASW|3|")));
                    Rejected(receiver, ReplacePayload(bytes, Payload(bytes).Replace("TimeRequest", "NotAllowed")));
                    Rejected(receiver, ReplacePayload(bytes, Payload(bytes) + "|extra")); Receive(receiver, bytes);
                }
            });
            Run("noncanonical floating point integers identifiers and whitespace rejected after valid MAC", () => {
                var bytes = Protect(Sync()); var payload = Payload(bytes);
                using (var receiver = End(PeerRole.Participant)) {
                    foreach (string bad in new[] { payload.Replace("|12.5|", "|12.50|"), payload.Replace("|12.5|", "|1.25e1|"),
                        payload.Replace("|7|", "|07|"), payload.Replace("|7|", "|+7|"), payload.Replace("bGFuLXRlc3Q=", "bGFuLXRlc3Q"), payload + " ", payload.Replace("|12.5|", "|NaN|") })
                        Rejected(receiver, ReplacePayload(bytes, bad)); Receive(receiver, bytes);
                }
            });
            Run("nontrivial floating bit patterns roundtrip on tested runtime only", () => {
                foreach (long bits in new[] { 1L, 0x0010000000000000L, 0x3ff0000000000001L, 0x3fd3333333333333L, 0x44b52d02c7e14af6L, 0x4340000000000001L }) {
                    double value = BitConverter.Int64BitsToDouble(bits); var bytes = Protect(Sync(sent: value));
                    using (var receiver = End(PeerRole.Participant)) Eq(bits, BitConverter.DoubleToInt64Bits(Receive(receiver, bytes).Packet.SentLocalSeconds));
                    Console.WriteLine("NUMBER bits=" + bits.ToString("x16") + "; canonical=" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }
            });
            Run("null empty oversize truncated and appended buffers rejected", () => {
                var bytes = Protect(Sync()); using (var receiver = End(PeerRole.Participant)) {
                    Rejected(receiver, null); Rejected(receiver, new byte[0]); Rejected(receiver, new byte[WozAuthenticatedLanEndpoint.MaximumEnvelopeBytes + 1]);
                    for (int n = 1; n < bytes.Length; n++) { var shortened = new byte[n]; Buffer.BlockCopy(bytes, 0, shortened, 0, n); Rejected(receiver, shortened); }
                    var appended = new byte[bytes.Length + 1]; Buffer.BlockCopy(bytes, 0, appended, 0, bytes.Length); Rejected(receiver, appended); Receive(receiver, bytes);
                }
            });
            Run("strict high water rejects duplicate old reordered and same sequence changed payload", () => {
                var first = Protect(Sync(7)); var second = Protect(Sync(8, 13)); var third = Protect(Sync(9, 14));
                using (var receiver = End(PeerRole.Participant)) {
                    Receive(receiver, first); Rejected(receiver, first, "replay_or_remote_clock"); Receive(receiver, third);
                    Rejected(receiver, second, "replay_or_remote_clock"); Rejected(receiver, Protect(Sync(9, 14, "changed")), "replay_or_remote_clock");
                    Rejected(receiver, Protect(Sync(10, 13)), "replay_or_remote_clock"); Receive(receiver, Protect(Sync(10, 15)));
                }
            });
            Run("failed protect does not consume sequence and sender cannot wrap", () => {
                using (var sender = End(PeerRole.Operator)) {
                    byte[] bytes; string error;
                    Assert(!sender.TryProtect(WirePacket.Hello(O(), 7, 12.5, "2"), null, null, out bytes, out error)); Assert(bytes == null);
                    Assert(sender.TryProtect(Sync(), null, null, out bytes, out error), error);
                    Assert(!sender.TryProtect(Sync(), null, null, out bytes, out error)); Assert(bytes == null);
                    Assert(sender.TryProtect(Sync(Int64.MaxValue, 14), null, null, out bytes, out error));
                    Assert(!sender.TryProtect(Sync(0, 15), null, null, out bytes, out error)); Eq("send_sequence_exhausted", error); Assert(bytes == null);
                }
                using (var receiver = End(PeerRole.Participant)) {
                    Receive(receiver, Protect(Sync(Int64.MaxValue))); Rejected(receiver, Protect(Sync(0)), "receive_sequence_exhausted");
                }
            });
            Run("original receipt validation never authenticates or refreshes caller timestamp", () => {
                var bytes = Protect(Sync()); using (var receiver = End(PeerRole.Participant)) {
                    foreach (double value in new[] { -1.0, Double.NaN, Double.PositiveInfinity, 21.0, 18.0 }) Rejected(receiver, bytes, "declared_receipt_clock", receipt: value);
                    Rejected(receiver, bytes, "declared_receipt_clock", now: Double.NaN); Rejected(receiver, bytes, "declared_receipt_clock", now: 19);
                    var message = Receive(receiver, bytes); Rejected(receiver, Protect(Sync(8, 13)), "declared_receipt_clock", receipt: 19.9);
                    Rejected(receiver, Protect(Sync(8, 13)), "declared_receipt_clock", now: 20.05); Receive(receiver, Protect(Sync(8, 13)), receipt: 20.2, now: 20.3);
                    Eq(20.0, message.OriginalLocalReceiptSeconds);
                }
            });
            Run("typed task and exact pose context with separate observation ordering", () => {
                var task = Task(); var taskPacket = WirePacket.Task(P(), task.ParticipantSequence, task.ParticipantSentSeconds, task.Task);
                var taskBytes = Protect(taskPacket, task); var poseBytes = Protect(Pose(), task, Observation());
                using (var op = End(PeerRole.Operator)) { var receipt = Receive(op, taskBytes); Eq(4L, receipt.TaskContext.Revision); Eq("trial-1", receipt.TaskContext.Task.TrialId); }
                using (var participant = End(PeerRole.Participant)) {
                    Rejected(participant, poseBytes, "unexpected_pose_task");
                    foreach (var changed in new[] { Task(revision: 5), Task(sequence: 3), Task(sent: 5.1), Task(trial: "trial-2"), Task(segment: 1), Task(condition: Condition.C), Task(phase: Phase.Paused) })
                        Rejected(participant, poseBytes, "unexpected_pose_task", changed);
                    var receipt = Receive(participant, poseBytes, task); Eq(11.0, receipt.PoseObservation.TaskReceivedOperatorSeconds);
                    Eq(12.0, receipt.PoseObservation.HeadObservedOperatorSeconds); Eq(12.1, receipt.PoseObservation.EyeObservedOperatorSeconds);
                    Eq(13.0, receipt.Packet.SentLocalSeconds); Eq(ControlSource.LiveOperator, receipt.Packet.Sample.Source);
                }
                Console.WriteLine("SIZE short task=" + taskBytes.Length + "; full task-bound pose=" + poseBytes.Length + "; parser cap=" + WozAuthenticatedLanEndpoint.MaximumEnvelopeBytes);
            });
            Run("pose cannot use inactive trial wrong device segment stale capture or impossible observation times", () => {
                using (var sender = End(PeerRole.Operator)) {
                    byte[] bytes; string error;
                    foreach (var task in new[] { Task(condition: Condition.D), Task(phase: Phase.Paused), Task(segment: 1) }) Assert(!sender.TryProtect(Pose(), task, Observation(), out bytes, out error));
                    Assert(!sender.TryProtect(Pose(device: "wrong"), Task(), Observation(), out bytes, out error));
                    Assert(!sender.TryProtect(Pose(capture: 10), Task(), Observation(), out bytes, out error));
                    Assert(!sender.TryProtect(Pose(), Task(), new WozLanPoseObservation(11, 14, 12), out bytes, out error));
                    Assert(!sender.TryProtect(Pose(), Task(), null, out bytes, out error));
                    Assert(sender.TryProtect(Pose(), Task(), Observation(), out bytes, out error), error);
                }
                Throws<ArgumentException>(() => new WozLanPoseObservation(11, 11, 12));
                Throws<ArgumentException>(() => new WozLanPoseObservation(11, 12, Double.NaN));
                Throws<ArgumentException>(() => new WozLanPoseObservation(BitConverter.Int64BitsToDouble(Int64.MinValue), 1, 1));
            });
            Run("task revision rollback and changed same revision rejected on both directions", () => {
                var first = Task(); var changed = Task(sequence: 3, sent: 6, trial: "trial-2"); var rollback = Task(revision: 3, sequence: 3, sent: 6);
                var next = Task(revision: 5, sequence: 3, sent: 6, trial: "trial-2");
                using (var sender = End(PeerRole.Participant)) {
                    byte[] bytes; string error;
                    Assert(sender.TryProtect(WirePacket.Task(P(), 2, 5, first.Task), first, null, out bytes, out error));
                    Assert(!sender.TryProtect(WirePacket.Task(P(), 3, 6, changed.Task), changed, null, out bytes, out error)); Eq("task_revision", error);
                    Assert(!sender.TryProtect(WirePacket.Task(P(), 3, 6, rollback.Task), rollback, null, out bytes, out error)); Eq("task_revision", error);
                    Assert(sender.TryProtect(WirePacket.Task(P(), 3, 6, next.Task), next, null, out bytes, out error));
                }
                using (var receiver = End(PeerRole.Operator)) {
                    Receive(receiver, Protect(WirePacket.Task(P(), 2, 5, first.Task), first));
                    Rejected(receiver, Protect(WirePacket.Task(P(), 3, 6, changed.Task), changed), "task_revision");
                    Rejected(receiver, Protect(WirePacket.Task(P(), 3, 6, rollback.Task), rollback), "task_revision");
                    Receive(receiver, Protect(WirePacket.Task(P(), 3, 6, next.Task), next));
                }
            });
            Run("higher envelope sequence cannot repeat sample or head/eye observation", () => {
                var task = Task(); var first = Protect(Pose(), task, Observation());
                var repeatedSample = WirePacket.Tracking(O(), 9, Pose().Sample);
                using (var receiver = End(PeerRole.Participant)) {
                    Receive(receiver, first, task);
                    Rejected(receiver, Protect(repeatedSample, task, new WozLanPoseObservation(11, 12.2, 12.3)), "repeated_pose_observation", task);
                    Rejected(receiver, Protect(Pose(9, 14), task, Observation()), "repeated_pose_observation", task);
                    Rejected(receiver, Protect(Pose(9, 14), task, new WozLanPoseObservation(11, 12, 12.3)), "repeated_pose_observation", task);
                    Rejected(receiver, Protect(Pose(9, 14), task, new WozLanPoseObservation(11, 12.2, 12.1)), "repeated_pose_observation", task);
                    Receive(receiver, Protect(Pose(9, 14), task, new WozLanPoseObservation(11, 12.2, 12.3)), task);
                }
                using (var sender = End(PeerRole.Operator)) {
                    byte[] bytes; string error; Assert(sender.TryProtect(Pose(), task, Observation(), out bytes, out error));
                    Assert(!sender.TryProtect(Pose(9, 14), task, Observation(), out bytes, out error)); Eq("repeated_pose_observation", error);
                    Assert(sender.TryProtect(Pose(9, 14), task, new WozLanPoseObservation(11, 12.2, 12.3), out bytes, out error));
                }
            });
            Run("simulator remains explicitly simulator and mixed sources rejected", () => {
                var op = O(source: "simulator/fixture", kind: ControlSource.Simulator);
                var context = new WozLanContext(P(kind: ControlSource.Simulator), op, Epoch(), "map-1", "operator-device");
                var sample = new HeadEyeSample("lan-test", "simulator/fixture", "operator-device", "o-clock", 0, 0, 1, 11.5, 13,
                    ControlSource.Simulator, StreamRole.OperatorControl, Rotation.Identity, new Vec3(0, 0, 1), true, true);
                using (var sender = End(PeerRole.Operator, context)) using (var receiver = End(PeerRole.Participant, context)) {
                    byte[] bytes; string error; Assert(sender.TryProtect(WirePacket.Tracking(op, 0, sample), Task(), Observation(), out bytes, out error));
                    Eq(ControlSource.Simulator, Receive(receiver, bytes, Task()).Packet.Sample.Source);
                }
                Throws<ArgumentException>(() => new WozLanContext(P(kind: ControlSource.Automatic), O(), Epoch(), "map-1", "operator-device"));
                Throws<ArgumentException>(() => new WozLanContext(P(kind: ControlSource.Simulator), O(), Epoch(), "map-1", "operator-device"));
                Throws<ArgumentException>(() => new WozLanContext(P(), O(kind: ControlSource.Simulator), Epoch(), "map-1", "operator-device"));
            });
            Run("time reply authenticates matching request clock but not clock calibration", () => {
                using (var receiver = End(PeerRole.Operator)) {
                    var reply = WirePacket.TimeReply(P(), 4, 7, "exchange-1", "o-clock", 5, 6);
                    var result = Receive(receiver, Protect(reply)); Eq("o-clock", result.Packet.RequestClockId); Eq(6.0, result.Packet.RemoteReceivedSeconds);
                }
                using (var sender = End(PeerRole.Participant)) {
                    byte[] bytes; string error; Assert(!sender.TryProtect(WirePacket.TimeReply(P(), 4, 7, "exchange-1", "other-clock", 5, 6), null, null, out bytes, out error)); Eq("clock_context", error);
                }
            });
            Run("stop retries need new sequence and ack is only authenticated assertion", () => {
                using (var receiver = End(PeerRole.Participant)) {
                    var stop = Protect(WirePacket.SafetyStopRequest(O(), 7, 12, "stop-1")); Receive(receiver, stop); Rejected(receiver, stop, "replay_or_remote_clock");
                    Eq("stop-1", Receive(receiver, Protect(WirePacket.SafetyStopRequest(O(), 8, 13, "stop-1"))).Packet.StopRequestId);
                }
                using (var receiver = End(PeerRole.Operator)) Eq(WireKind.SafetyStopAck, Receive(receiver, Protect(WirePacket.SafetyStopAck(P(), 0, 14, "stop-1"))).Packet.Kind);
            });
            Run("Hello arbitrary payload and role substitution cannot enter allowlist", () => {
                var bytes = Protect(Sync()); using (var receiver = End(PeerRole.Participant)) {
                    Rejected(receiver, ReplacePayload(bytes, Encoding.ASCII.GetString(WireCodec.Encode(WirePacket.Hello(O(), 7, 12.5, "2")))), "kind_not_allowed");
                    Rejected(receiver, ReplacePayload(bytes, "participant response=correct RT=123 rating=4"));
                    Rejected(receiver, ReplacePayload(bytes, Encoding.ASCII.GetString(WireCodec.Encode(WirePacket.TimeRequest(P(), 7, 12.5, "sync-1")))), "peer_context");
                    Receive(receiver, bytes);
                }
            });
            Run("proof factory rejects fabricated capability and disposal is terminal", () => {
                using (var endpoint = End(PeerRole.Participant)) {
                    Throws<InvalidOperationException>(() => WozAuthenticatedLanEndpoint.VerifiedMessage.Create(endpoint, new object(), Sync(), null, null, "p-clock", 20));
                    endpoint.Dispose(); endpoint.Dispose(); Rejected(endpoint, Protect(Sync()), "disposed");
                    byte[] bytes; string error; Assert(!endpoint.TryProtect(WirePacket.TimeRequest(P(), 0, 1, "x"), null, null, out bytes, out error)); Eq("disposed", error); Assert(bytes == null);
                }
            });
            Run("concurrent duplicate receipt commits once and post-close never commits", () => {
                using (var endpoint = End(PeerRole.Participant)) using (var start = new ManualResetEvent(false)) {
                    var bytes = Protect(Sync()); int accepted = 0; Exception failure = null;
                    ThreadStart action = () => { try { start.WaitOne(); WozAuthenticatedLanEndpoint.VerifiedMessage message; string error; if (endpoint.TryReceive(bytes, 20, 20.1, null, out message, out error)) Interlocked.Increment(ref accepted); } catch (Exception e) { failure = e; } };
                    var a = new Thread(action); var b = new Thread(action); a.Start(); b.Start(); start.Set(); a.Join(); b.Join(); Assert(failure == null); Eq(1, accepted);
                    endpoint.Dispose(); Rejected(endpoint, Protect(Sync(8, 13)), "disposed");
                }
            });
            Run("bounded worst identifiers and no key entropy freshness or MTU claim", () => {
                string s = new string('s', 128), p = new string('p', 128), o = new string('o', 128), stream = new string('x', 128), pc = new string('c', 128), oc = new string('d', 128), device = new string('v', 128);
                var participant = P(s, p, stream, pc); var op = O(s, o, stream, oc); var context = new WozLanContext(participant, op, Epoch(), new string('m', 128), device);
                var task = new WozLanTaskContext(new TaskSnapshot(s, 3, Int32.MaxValue, new string('t', 128), Colour.Yellow, Colour.Green, Phase.MainTask, Condition.C), Int64.MaxValue, Int64.MaxValue, 5);
                var sample = new HeadEyeSample(s, stream, device, oc, 3, Int32.MaxValue, Int64.MaxValue, 11.5, 13, ControlSource.LiveOperator, StreamRole.OperatorControl, Rotation.Identity, new Vec3(0, 0, 1), true, true);
                using (var sender = End(PeerRole.Operator, context)) using (var receiver = End(PeerRole.Participant, context)) {
                    byte[] bytes; string error; Assert(sender.TryProtect(WirePacket.Tracking(op, Int64.MaxValue, sample), task, Observation(), out bytes, out error), error);
                    Assert(bytes.Length <= WozAuthenticatedLanEndpoint.MaximumEnvelopeBytes); Receive(receiver, bytes, task);
                    Console.WriteLine("SIZE maximum 128-byte identifiers, maximum counters, full task-bound pose=" + bytes.Length + "; no datagram MTU guarantee");
                }
                var largeSample = new HeadEyeSample(s, stream, device, oc, 3, Int32.MaxValue, Int64.MaxValue, 1.0000000000000002E+308, Double.MaxValue,
                    ControlSource.LiveOperator, StreamRole.OperatorControl, new Rotation(-Double.Epsilon, -Double.Epsilon, -Double.Epsilon, 1), new Vec3(-Double.Epsilon, -Double.Epsilon, 1), true, true);
                var largeTask = new WozLanTaskContext(task.Task, Int64.MaxValue, Int64.MaxValue, 1.0000000000000002E+308);
                using (var sender = End(PeerRole.Operator, context)) using (var receiver = End(PeerRole.Participant, context)) {
                    byte[] bytes; string error; Assert(sender.TryProtect(WirePacket.Tracking(op, Int64.MaxValue, largeSample), largeTask,
                        new WozLanPoseObservation(1E+290, 1.5E+308, 1.6E+308), out bytes, out error), error); Receive(receiver, bytes, largeTask);
                    Console.WriteLine("SIZE maximum identifiers/counters with long finite float spellings=" + bytes.Length + "; structural content ceiling=4207");
                }
                Throws<ArgumentException>(() => End(PeerRole.Operator, null, new byte[31]));
                Throws<ArgumentException>(() => new WozLanContext(P(), O(), new byte[15], "map", "device"));
                Throws<ArgumentException>(() => new WozLanContext(P(), O(clock: "p-clock"), Epoch(), "map", "device"));
                Throws<ArgumentException>(() => new WozLanContext(P(), O(), Epoch(), new string('m', 129), "device"));
            });
            Console.WriteLine("BOUNDARY: offline legacy-schema byte authentication only; no network, production admission, hardware validation, named-host integration, real receipt capture or recording.");
            Console.WriteLine("RESULT " + passed + " passed; " + failed + " failed"); return failed == 0 ? 0 : 1;
        }
    }
}
