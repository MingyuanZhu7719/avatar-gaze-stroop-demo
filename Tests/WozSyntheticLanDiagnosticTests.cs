using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Starts ONLY this sealed memory fixture. No OS socket, file/native fixture,
    // HMD, participant/operator input, active player or research recording.
    public static class WozSyntheticLanDiagnosticTests
    {
        sealed class MemorySocket : IWozDiagnosticSocket
        {
            internal MemorySocket Other;
            internal readonly Queue<byte[]> Queue = new Queue<byte[]>();
            internal readonly List<byte[]> Sent = new List<byte[]>();
            internal IPEndPoint Local;
            internal bool FailSend, FailPoll;
            internal int Closes;
            public void Start(IPEndPoint local) { Local = local; }
            public bool Poll() { if (FailPoll) throw new SocketException((int)SocketError.NetworkDown); return Queue.Count > 0; }
            public int Receive(byte[] buffer, out IPEndPoint sender, out bool truncated)
            { var b = Queue.Dequeue(); sender = Other.Local; truncated = b.Length > buffer.Length; Buffer.BlockCopy(b, 0, buffer, 0, Math.Min(b.Length, buffer.Length)); return b.Length; }
            public int Send(byte[] bytes, IPEndPoint remote)
            { if (FailSend) throw new SocketException((int)SocketError.NetworkDown); Sent.Add((byte[])bytes.Clone()); Other.Queue.Enqueue((byte[])bytes.Clone()); return bytes.Length; }
            public void Dispose() { Closes++; }
        }
        sealed class Pair : IDisposable
        {
            internal readonly MemorySocket A = new MemorySocket(), B = new MemorySocket();
            internal readonly WozSyntheticLanDiagnosticSession P, O;
            internal readonly WozLanContext Context;
            internal Pair(double age = 2, int epoch = 1)
            {
                Context = MakeContext(epoch); A.Other = B; B.Other = A;
                P = new WozSyntheticLanDiagnosticSession(Context, PeerRole.Participant, Key(), PAddress(), OAddress(), age, A);
                O = new WozSyntheticLanDiagnosticSession(Context, PeerRole.Operator, Key(), OAddress(), PAddress(), age, B);
                P.StartDiagnostic(); O.StartDiagnostic();
            }
            internal void Prove()
            {
                P.Tick(); O.Tick(); P.Tick(); O.Tick();
                Assert(P.Snapshot.PeerAuthenticated && O.Snapshot.PeerAuthenticated);
            }
            internal void Pose()
            {
                Sent(P.NextSyntheticTask()); O.Tick(); Thread.Sleep(1);
                Sent(O.SendSyntheticPose()); P.Tick(); Assert(P.Snapshot.PoseVisible);
            }
            public void Dispose() { P.Dispose(); O.Dispose(); }
        }
        static IPEndPoint PAddress() { return new IPEndPoint(IPAddress.Loopback, 43401); }
        static IPEndPoint OAddress() { return new IPEndPoint(IPAddress.Loopback, 43402); }
        static byte[] Key() { var b = new byte[32]; for (int i = 0; i < 32; i++) b[i] = (byte)(i + 1); return b; }
        static WozLanContext MakeContext(int epoch = 1, ControlSource source = ControlSource.Simulator)
        {
            var b = new byte[16]; b[0] = (byte)epoch; string id = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
            return new WozLanContext(new PeerIdentity("synthetic/" + id, "synthetic-task", PeerRole.Participant, "simulator/task", source, "task-clock/" + id),
                new PeerIdentity("synthetic/" + id, "synthetic-pose", PeerRole.Operator, "simulator/pose", source, "pose-clock/" + id), b, "synthetic-fixed-v1", "synthetic-device");
        }
        static object Get(object o, string name) { return o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o); }
        static void Set(object o, string name, object value) { o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(o, value); }
        static void Assert(bool yes, string detail = "assertion") { if (!yes) throw new Exception(detail); }
        static void Eq<T>(T a, T b) { Assert(Object.Equals(a, b), "Expected " + a + ", got " + b); }
        static void Sent(WozDiagnosticSendResult r) { Eq(WozDiagnosticSendStatus.AcceptedBySocketOnly, r.Status); }
        static void NotSent(WozDiagnosticSendResult r) { Assert(r.Status != WozDiagnosticSendStatus.AcceptedBySocketOnly, r.Detail); }
        static void Throws(Action a) { try { a(); } catch (ArgumentException) { return; } throw new Exception("Expected argument rejection"); }
        static byte[] Protect(WozLanContext c, WirePacket p, WozLanTaskContext t = null, WozLanPoseObservation o = null)
        {
            using (var e = new WozAuthenticatedLanEndpoint(c, p.Peer.Role, Key(), 2))
            { byte[] bytes; string error; Assert(e.TryProtect(p, t, o, out bytes, out error), error); return bytes; }
        }
        static WozAuthenticatedLanEndpoint.VerifiedMessage Decode(WozLanContext c, byte[] bytes, PeerRole receiver, WozLanTaskContext expected = null)
        {
            using (var e = new WozAuthenticatedLanEndpoint(c, receiver, Key(), 2))
            { WozAuthenticatedLanEndpoint.VerifiedMessage message; string error; Assert(e.TryReceive(bytes, 100, 100, expected, out message, out error), error); return message; }
        }
        static int passed, failed;
        static void Run(string name, Action action)
        { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + " " + e); } }
        public static int RunAll()
        {
            passed = failed = 0;
            Run("public synthetic construction dormant and no injection or real source", () => {
                using (var s = new WozSyntheticLanDiagnosticSession(MakeContext(), PeerRole.Operator, Key(), OAddress(), PAddress())) {
                    Eq(WozDiagnosticState.Dormant, s.Snapshot.State); Assert(!s.Snapshot.PeerAuthenticated);
                    NotSent(s.SendSyntheticPose()); s.Dispose(); Eq(WozDiagnosticState.Disposed, s.StartDiagnostic());
                }
                Throws(() => new WozSyntheticLanDiagnosticSession(MakeContext(1, ControlSource.LiveOperator), PeerRole.Operator, Key(), OAddress(), PAddress()));
                Eq(1, typeof(WozSyntheticLanDiagnosticSession).GetConstructors().Length);
                foreach (var m in typeof(WozSyntheticLanDiagnosticSession).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    Eq(0, m.GetParameters().Length);
            });
            Run("both directions require own fresh nonce reply not inbound request", () => {
                using (var p = new Pair()) {
                    NotSent(p.P.NextSyntheticTask()); NotSent(p.O.SendSyntheticPose());
                    p.P.Tick(); Assert(!p.P.Snapshot.PeerAuthenticated); Assert(!p.O.Snapshot.PeerAuthenticated);
                    p.O.Tick(); Assert(p.O.Snapshot.PeerAuthenticated); Assert(!p.P.Snapshot.PeerAuthenticated);
                    p.P.Tick(); Assert(p.P.Snapshot.PeerAuthenticated);
                }
            });
            Run("initial operator challenge send failure reports terminal start", () => {
                var a = new MemorySocket(); var b = new MemorySocket { FailSend = true }; a.Other = b; b.Other = a;
                using (var s = new WozSyntheticLanDiagnosticSession(MakeContext(), PeerRole.Operator, Key(), OAddress(), PAddress(), 2, b)) {
                    Eq(WozDiagnosticState.Faulted, s.StartDiagnostic()); Eq(WozDiagnosticState.Faulted, s.Snapshot.State);
                    Assert(!s.Snapshot.PeerAuthenticated && !s.Snapshot.PoseVisible); Eq(1, b.Closes);
                    Eq("send", s.Snapshot.PrimaryFailure.Operation); Eq(WozDiagnosticState.Faulted, s.StartDiagnostic()); Eq(1, b.Closes);
                }
            });
            Run("fresh random challenge differs across same key epoch process instances", () => {
                string first;
                using (var p = new Pair()) { Sent(p.P.RequestClockDiagnostic()); first = Decode(p.Context, p.A.Sent[0], PeerRole.Operator).Packet.ExchangeId; }
                using (var p = new Pair()) { Sent(p.P.RequestClockDiagnostic()); string second = Decode(p.Context, p.A.Sent[0], PeerRole.Operator).Packet.ExchangeId; Assert(first != second); Eq(16, Convert.FromBase64String(second.Substring("diagnostic/".Length)).Length); }
            });
            Run("recorded old reply does not authenticate restarted process", () => {
                byte[] old;
                using (var p = new Pair()) { p.P.Tick(); old = p.A.Sent[0]; }
                using (var p = new Pair()) { p.B.Queue.Enqueue(old); p.O.Tick(); Assert(!p.O.Snapshot.PeerAuthenticated); NotSent(p.O.SendSyntheticPose()); }
            });
            Run("task before own reply cannot become active after confirmation", () => {
                using (var p = new Pair()) {
                    var request = Decode(p.Context, p.B.Sent[0], PeerRole.Participant).Packet;
                    var task = WozSyntheticLanScenario.Create(p.Context.Participant.SessionId, 1, false);
                    var context = new WozLanTaskContext(task, 1, 10, 10);
                    var bytes = Protect(p.Context, WirePacket.Task(p.Context.Participant, 10, 10, task), context);
                    p.B.Queue.Enqueue(bytes);
                    p.B.Queue.Enqueue(Protect(p.Context, WirePacket.TimeReply(p.Context.Participant, 11, 11, request.ExchangeId, request.RequestClockId, request.RequestSentSeconds, 10)));
                    var state = p.O.Tick(); Assert(state.PeerAuthenticated); Assert(state.TaskContext == null); NotSent(p.O.SendSyntheticPose());
                    p.B.Queue.Enqueue(bytes); p.O.Tick(); Assert(p.O.Snapshot.TaskContext == null); NotSent(p.O.SendSyntheticPose());
                }
            });
            Run("task after own reply in same bounded drain establishes new binding", () => {
                using (var p = new Pair()) {
                    var request = Decode(p.Context, p.B.Sent[0], PeerRole.Participant).Packet;
                    p.B.Queue.Enqueue(Protect(p.Context, WirePacket.TimeReply(p.Context.Participant, 10, 10, request.ExchangeId, request.RequestClockId, request.RequestSentSeconds, 9)));
                    var task = WozSyntheticLanScenario.Create(p.Context.Participant.SessionId, 1, false);
                    var context = new WozLanTaskContext(task, 1, 11, 11);
                    p.B.Queue.Enqueue(Protect(p.Context, WirePacket.Task(p.Context.Participant, 11, 11, task), context));
                    var state = p.O.Tick(); Assert(state.PeerAuthenticated && state.TaskFresh); Eq(11L, state.TaskContext.ParticipantSequence);
                    Thread.Sleep(1); Sent(p.O.SendSyntheticPose());
                }
            });
            Run("own proof renewal clears earlier displayed pose and exact task", () => {
                using (var p = new Pair()) {
                    p.Prove(); p.Pose(); Sent(p.P.RequestClockDiagnostic());
                    var pending = p.P.Snapshot; Assert(!pending.PeerAuthenticated && !pending.PoseVisible && pending.TaskContext == null);
                    NotSent(p.P.SendSyntheticTask()); NotSent(p.P.RequestClockDiagnostic());
                    p.O.Tick(); var state = p.P.Tick();
                    Assert(state.PeerAuthenticated && !state.PoseVisible && state.TaskContext == null);
                    Sent(p.O.SendSyntheticPose()); Assert(!p.P.Tick().PoseVisible);
                    Sent(p.P.SendSyntheticTask()); p.O.Tick(); Sent(p.O.SendSyntheticPose()); Assert(p.P.Tick().PoseVisible);
                }
            });
            Run("actual received task and fresh generated pose echo every bound field", () => {
                using (var p = new Pair()) {
                    p.Prove(); p.Pose(); var a = p.P.Snapshot; var b = p.O.Snapshot;
                    Eq(Condition.B, a.TaskContext.Task.Condition.Value); Eq(ControlSource.Simulator, a.RemotePose.Source);
                    Eq(a.TaskContext.ParticipantSequence, b.TaskContext.ParticipantSequence); Eq(a.TaskContext.ParticipantSentSeconds, b.TaskContext.ParticipantSentSeconds);
                    Eq(b.OriginalLocalTaskReceiptSeconds.Value, a.PoseObservation.TaskReceivedOperatorSeconds);
                    Assert(a.RemotePose.CaptureLocalSeconds > a.PoseObservation.TaskReceivedOperatorSeconds);
                    Assert(a.PoseObservation.HeadObservedOperatorSeconds >= a.RemotePose.CaptureLocalSeconds);
                    Assert(a.RemotePose.SendLocalSeconds >= a.PoseObservation.EyeObservedOperatorSeconds);
                    Assert(a.OriginalLocalPoseReceiptSeconds.HasValue); Eq(WozDiagnosticOrigin.InMemorySynthetic, a.Origin);
                    int maxTask = 0, maxPose = 0;
                    foreach (var bytes in p.A.Sent) { Assert(bytes.Length <= 1200, "final identity task envelope too large"); maxTask = Math.Max(maxTask, bytes.Length); }
                    foreach (var bytes in p.B.Sent) { Assert(bytes.Length <= 1200, "final identity pose envelope too large"); maxPose = Math.Max(maxPose, bytes.Length); }
                    Console.WriteLine("SIZE final pairing identities participant max=" + maxTask + "; operator max=" + maxPose + "; UDP cap=1200");
                }
            });
            Run("participant transition clears old pose and old exact task rejected", () => {
                using (var p = new Pair()) {
                    p.Prove(); p.Pose(); Sent(p.O.SendSyntheticPose()); // queued against old task
                    Sent(p.P.NextSyntheticTask()); Assert(!p.P.Snapshot.PoseVisible);
                    var a = p.P.Tick(); Assert(!a.PoseVisible); Assert(a.RejectedMessages > 0);
                    p.O.Tick(); Sent(p.O.SendSyntheticPose()); Assert(p.P.Tick().PoseVisible); Eq(Condition.C, p.P.Snapshot.TaskContext.Task.Condition.Value);
                }
            });
            Run("same revision retransmit changes exact packet binding and clears pose", () => {
                using (var p = new Pair()) {
                    p.Prove(); p.Pose(); long rev = p.P.Snapshot.TaskContext.Revision;
                    Sent(p.O.SendSyntheticPose()); Sent(p.P.SendSyntheticTask()); Eq(rev, p.P.Snapshot.TaskContext.Revision);
                    Assert(!p.P.Tick().PoseVisible); p.O.Tick(); Sent(p.O.SendSyntheticPose()); Assert(p.P.Tick().PoseVisible);
                }
            });
            Run("pause resume is owned synthetic transition never stop acknowledgement", () => {
                using (var p = new Pair()) {
                    p.Prove(); p.Pose(); Sent(p.P.PauseSyntheticTask()); Assert(!p.P.Snapshot.PoseVisible);
                    p.O.Tick(); Eq(Phase.Paused, p.O.Snapshot.TaskContext.Task.Phase); NotSent(p.O.SendSyntheticPose());
                    Sent(p.P.ResumeSyntheticTask()); p.O.Tick(); Sent(p.O.SendSyntheticPose()); Assert(p.P.Tick().PoseVisible);
                    NotSent(p.O.PauseSyntheticTask());
                }
            });
            Run("BCAD scenario D non-task and ended never expose pose", () => {
                using (var p = new Pair()) {
                    p.Prove(); Sent(p.P.SendSyntheticTask()); p.O.Tick(); NotSent(p.O.SendSyntheticPose());
                    p.Pose(); Sent(p.P.NextSyntheticTask()); p.O.Tick(); Sent(p.P.NextSyntheticTask()); p.O.Tick(); Eq(Condition.A, p.O.Snapshot.TaskContext.Task.Condition.Value);
                    Sent(p.P.NextSyntheticTask()); p.O.Tick(); Eq(Condition.D, p.O.Snapshot.TaskContext.Task.Condition.Value); NotSent(p.O.SendSyntheticPose()); Assert(!p.P.Snapshot.PoseVisible);
                    Sent(p.P.NextSyntheticTask()); p.O.Tick(); Eq(Phase.Questionnaire, p.O.Snapshot.TaskContext.Task.Phase); NotSent(p.O.SendSyntheticPose());
                    Sent(p.P.StopSyntheticTask()); p.O.Tick(); Eq(Phase.Ended, p.O.Snapshot.TaskContext.Task.Phase); NotSent(p.O.SendSyntheticPose()); NotSent(p.P.NextSyntheticTask());
                }
            });
            Run("local task silence clears prior pose and forbids stale sends", () => {
                using (var p = new Pair(.05)) { p.Prove(); p.Pose(); Thread.Sleep(80); Assert(!p.P.Snapshot.PoseVisible); Assert(!p.P.Snapshot.TaskFresh); NotSent(p.O.SendSyntheticPose()); }
            });
            Run("expired peer proof clears bindings without automatic resume", () => {
                using (var p = new Pair()) {
                    p.Prove(); p.Pose(); Set(p.P, "peerReplyReceipt", (double?)(-20)); var a = p.P.Snapshot;
                    Assert(!a.PeerAuthenticated && !a.PoseVisible && a.TaskContext == null); NotSent(p.P.NextSyntheticTask());
                    p.P.Tick(); Assert(!p.P.Snapshot.PeerAuthenticated);
                    Sent(p.P.RequestClockDiagnostic()); p.O.Tick(); p.P.Tick(); Assert(p.P.Snapshot.PeerAuthenticated); Assert(p.P.Snapshot.TaskContext == null);
                }
            });
            Run("clock request timeout invalidates current proof and pose", () => {
                using (var p = new Pair(.05)) { p.Prove(); p.Pose(); Sent(p.P.RequestClockDiagnostic()); Thread.Sleep(80); var a = p.P.Tick(); Assert(!a.PeerAuthenticated && !a.PoseVisible && a.TaskContext == null); }
            });
            Run("replay MAC error wrong epoch and invalid task clear pose", () => {
                using (var p = new Pair()) {
                    p.Prove(); p.Pose(); var bytes = p.B.Sent[p.B.Sent.Count - 1]; p.A.Queue.Enqueue(bytes); Assert(!p.P.Tick().PoseVisible);
                    Sent(p.O.SendSyntheticPose()); p.P.Tick(); Assert(p.P.Snapshot.PoseVisible);
                    var bad = (byte[])p.B.Sent[p.B.Sent.Count - 1].Clone(); bad[bad.Length - 1] ^= 1; p.A.Queue.Enqueue(bad); Assert(!p.P.Tick().PoseVisible);
                    var other = MakeContext(2); p.A.Queue.Enqueue(Protect(other, WirePacket.TimeRequest(other.Operator, 100, 1, "wrong-context"))); Assert(!p.P.Tick().PoseVisible);
                    var t = new TaskSnapshot(p.Context.Participant.SessionId, 0, 0, "not-synthetic", Colour.Blue, Colour.Red, Phase.MainTask, Condition.A);
                    p.B.Queue.Enqueue(Protect(p.Context, WirePacket.Task(p.Context.Participant, 100, 100, t), new WozLanTaskContext(t, 100, 100, 100)));
                    p.O.Tick(); NotSent(p.O.SendSyntheticPose()); Assert(p.O.Snapshot.TaskContext == null);
                }
            });
            Run("stop request observed never acknowledged or real paused", () => {
                using (var p = new Pair()) {
                    p.Prove(); p.Pose(); int count = p.A.Sent.Count;
                    p.A.Queue.Enqueue(Protect(p.Context, WirePacket.SafetyStopRequest(p.Context.Operator, 100, 100, "synthetic-stop")));
                    var a = p.P.Tick(); Eq(count, p.A.Sent.Count); Eq(Phase.MainTask, a.OwnedTask.Phase); Assert(!a.PoseVisible); Assert(a.Detail.Contains("no_pause_applied"));
                }
            });
            Run("send or receive failure clears pose and closes once no restart", () => {
                using (var p = new Pair()) {
                    p.Prove(); p.Pose(); p.A.FailSend = true; NotSent(p.P.SendSyntheticTask());
                    Assert(!p.P.Snapshot.PoseVisible && !p.P.Snapshot.PeerAuthenticated); Eq(WozDiagnosticState.Faulted, p.P.StartDiagnostic()); Eq(1, p.A.Closes); p.P.Dispose(); Eq(1, p.A.Closes);
                }
                using (var p = new Pair()) { p.Prove(); p.Pose(); p.A.FailPoll = true; var a = p.P.Tick(); Eq(WozDiagnosticState.Faulted, a.State); Assert(!a.PoseVisible && !a.PeerAuthenticated); }
            });
            Run("immutable snapshots retain historical receipt but close removes current pose", () => {
                using (var p = new Pair()) { p.Prove(); p.Pose(); var old = p.P.Snapshot; double time = old.OriginalLocalPoseReceiptSeconds.Value; p.P.Dispose(); Assert(!p.P.Snapshot.PoseVisible); Assert(old.PoseVisible); Eq(time, old.OriginalLocalPoseReceiptSeconds.Value); }
            });
            Console.WriteLine("BOUNDARY: sealed memory synthetic legacy diagnostic only; no OS socket or real experiment admission.");
            Console.WriteLine("RESULT " + passed + " passed; " + failed + " failed"); return failed == 0 ? 0 : 1;
        }
    }
    public static class WozLanBindingMemoryRunner
    {
        public static int Main()
        { return WozAuthenticatedLanTests.RunAll() | WozAuthenticatedDiagnosticTransportTests.RunAll() | WozSyntheticLanDiagnosticTests.RunAll(); }
    }
}
