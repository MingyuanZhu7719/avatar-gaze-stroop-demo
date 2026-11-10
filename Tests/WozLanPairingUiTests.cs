using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Windows.Forms;
using AvatarStroop.Apps;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Headless: no Form is shown and no real socket/fresh session is started.
    // Blank-only offscreen rendering follows lifecycle tests. Ephemeral RNG
    // values are kept only in memory, never rendered, saved or printed.
    public static class WozLanPairingUiTests
    {
        static int passed, failed;
        static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
        static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + " / " + e.GetType().Name); }
        }
        static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected exception"); }
        static WozLanPairing Fixed(int marker = 1)
        { return WozLanPairing.Create(b => { for (int i = 0; i < b.Length; i++) b[i] = (byte)(i + marker); }); }
        static char[] Token(int marker = 1) { using (var p = Fixed(marker)) return p.RevealToken(); }
        static WozLanUiConfiguration Config(PeerRole role = PeerRole.Operator)
        { return new WozLanUiConfiguration(role, "192.168.20.2", "43102", "192.168.20.1", "43101"); }
        sealed class Fake : IWozLanUiSession
        {
            public int Starts, Closes, Ticks, Acts;
            public bool FailStart, FailTick, FaultOnTick, FailClose;
            public WozDiagnosticState State { get; private set; }
            public bool PeerAuthenticated { get { return State == WozDiagnosticState.Running; } }
            public string CleanupNote { get { return FailClose ? "cleanup_failed" : ""; } }
            public string PrimaryNote { get { return FailStart ? "synthetic_start_failure" : ""; } }
            public WozDiagnosticState Start() { Starts++; State = FailStart ? WozDiagnosticState.Faulted : WozDiagnosticState.Running; return State; }
            public string Tick() { Ticks++; if (FailTick) throw new InvalidOperationException(); if (FaultOnTick) State = WozDiagnosticState.Faulted; return "synthetic fake only"; }
            public string Act(WozLanUiAction action) { Acts++; return "fake only"; }
            public void Dispose() { Closes++; State = WozDiagnosticState.Disposed; }
        }
        static WozLanDiagnosticController Controller(Fake fake)
        { return new WozLanDiagnosticController((c, r, k, l, p) => fake); }
        static void Import(WozLanDiagnosticController c, int marker = 1)
        { var token = Token(marker); try { c.ImportPairing(token); } finally { Array.Clear(token, 0, token.Length); } }
        [STAThread]
        public static int Main()
        {
            Test("native Microsoft CLR4 serialization family", () => Assert(WozLanRuntimeCompatibility.IsSupported()));
            Test("fixed token exact roundtrip and Simulator identities", () => {
                var token = Token(); using (var p = WozLanPairing.Parse(token)) {
                    var again = p.RevealToken(); Assert(new string(token) == new string(again));
                    Assert(p.Context.Participant.Source == ControlSource.Simulator && p.Context.Operator.Source == ControlSource.Simulator);
                    Assert(p.Context.Participant.Role == PeerRole.Participant && p.Context.Operator.Role == PeerRole.Operator);
                    Assert(p.Context.Participant.SessionId == p.Context.Operator.SessionId && p.Context.Participant.ClockId != p.Context.Operator.ClockId);
                    Assert(p.Context.Participant.SourceId.StartsWith("simulator/") && p.Context.Operator.SourceId.StartsWith("simulator/"));
                    Array.Clear(again, 0, again.Length);
                } Array.Clear(token, 0, token.Length);
            });
            Test("entropy fill lengths and zeroization of temporary arrays", () => {
                var arrays = new List<byte[]>(); using (var p = WozLanPairing.Create(b => { arrays.Add(b); for (int i = 0; i < b.Length; i++) b[i] = 7; })) {
                    Assert(arrays.Count == 2 && arrays[0].Length == 32 && arrays[1].Length == 16);
                    foreach (var b in arrays) foreach (byte v in b) Assert(v == 0);
                }
            });
            Test("entropy failure clears arrays", () => {
                byte[] captured = null;
                Throws<InvalidOperationException>(() => WozLanPairing.Create(b => { captured = b; b[0] = 1; throw new InvalidOperationException(); }));
                foreach (byte b in captured) Assert(b == 0);
            });
            Test("pairing disposal clears owned arrays", () => {
                var p = Fixed(); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var key = (byte[])typeof(WozLanPairing).GetField("key", flags).GetValue(p);
                var epoch = (byte[])typeof(WozLanPairing).GetField("epoch", flags).GetValue(p);
                p.Dispose(); p.Dispose(); foreach (byte b in key) Assert(b == 0); foreach (byte b in epoch) Assert(b == 0);
                Assert(p.Context == null); Throws<ObjectDisposedException>(() => p.RevealToken());
            });
            Test("reject missing oversized version whitespace and malformed tokens", () => {
                Throws<ArgumentException>(() => WozLanPairing.Parse(null)); Throws<ArgumentException>(() => WozLanPairing.Parse(new char[10000]));
                foreach (int position in new[] { 0, 8, 71 }) { var t = Token(); t[position] = ' '; Throws<ArgumentException>(() => WozLanPairing.Parse(t)); Array.Clear(t, 0, t.Length); }
            });
            Test("epoch determines context with fixed synthetic protocol", () => {
                using (var a = Fixed(1)) using (var b = Fixed(2)) {
                    Assert(a.Context.Participant.SessionId != b.Context.Participant.SessionId);
                    Assert(a.Context.Mapping == b.Context.Mapping && a.Context.OperatorDevice == b.Context.OperatorDevice);
                }
            });
            Test("final pairing identities task and pose fit datagram bound", () => {
                using (var pair = Fixed()) {
                    var c = pair.Context; var k = pair.CopyKey();
                    var t = new TaskSnapshot(c.Participant.SessionId, 0, 0, "synthetic-trial/1", Colour.Red, Colour.Green, Phase.MainTask, Condition.B);
                    var task = new WozLanTaskContext(t, Int64.MaxValue - 1, Int64.MaxValue - 1, 1234.1234567890123);
                    byte[] bytes; string error; int taskSize, poseSize;
                    using (var endpoint = new WozAuthenticatedLanEndpoint(c, PeerRole.Participant, k, 2)) {
                        Assert(endpoint.TryProtect(WirePacket.Task(c.Participant, task.ParticipantSequence, task.ParticipantSentSeconds, t), task, null, out bytes, out error));
                        taskSize = bytes.Length; Assert(taskSize <= WozAuthenticatedDiagnosticTransport.MaximumDatagramBytes);
                    }
                    using (var endpoint = new WozAuthenticatedLanEndpoint(c, PeerRole.Operator, k, 2)) {
                        var sample = new HeadEyeSample(c.Operator.SessionId, c.Operator.SourceId, c.OperatorDevice, c.Operator.ClockId, 0, 0,
                            Int64.MaxValue - 1, 1235.1234567890123, 1236.1234567890123, ControlSource.Simulator, StreamRole.OperatorControl,
                            Rotation.Identity, new Vec3(0.6, 0, 0.8), true, true);
                        Assert(endpoint.TryProtect(WirePacket.Tracking(c.Operator, Int64.MaxValue - 1, sample), task,
                            new WozLanPoseObservation(1234.5234567890123, 1235.2234567890123, 1235.3234567890123), out bytes, out error));
                        poseSize = bytes.Length; Assert(poseSize <= WozAuthenticatedDiagnosticTransport.MaximumDatagramBytes);
                    }
                    Array.Clear(k, 0, k.Length);
                    Console.WriteLine("SIZE final pairing context task=" + taskSize + " pose=" + poseSize + " bytes; cap=1200; synthetic fixed fixture, not MTU proof");
                }
            });
            Test("all permitted private address ranges", () => {
                foreach (var ip in new[] { "10.0.0.1", "172.16.0.1", "172.31.255.1", "192.168.1.2" })
                    Assert(new WozLanUiConfiguration(PeerRole.Operator, ip, "1", "192.168.1.3", "65535").Local.Port == 1);
            });
            Test("reject public alternate-loopback wildcard broadcast DNS abbreviated IPv6", () => {
                foreach (var ip in new[] { "8.8.8.8", "127.0.0.2", "127.1.2.3", "0.0.0.0", "192.168.1.255", "192.168.1.0", "host", "127.1", "::1", "192.168.01.2", " 192.168.1.2", "172.32.1.2" })
                    Throws<ArgumentException>(() => new WozLanUiConfiguration(PeerRole.Operator, ip, "43101", "192.168.1.3", "43102"));
            });
            Test("exact two-endpoint loopback self-test requires different ports", () => {
                var c = new WozLanUiConfiguration(PeerRole.Operator, "127.0.0.1", "43101", "127.0.0.1", "43102"); Assert(c.IsLocalOnly);
                Assert(!Config().IsLocalOnly);
                Throws<ArgumentException>(() => new WozLanUiConfiguration(PeerRole.Operator, "127.0.0.1", "43101", "127.0.0.1", "43101"));
                Throws<ArgumentException>(() => new WozLanUiConfiguration(PeerRole.Operator, "127.0.0.1", "43101", "192.168.1.3", "43102"));
                Throws<ArgumentException>(() => new WozLanUiConfiguration(PeerRole.Operator, "192.168.1.3", "43101", "127.0.0.1", "43102"));
            });
            Test("normal controller accepts labeled loopback with fake lifecycle only", () => {
                var f = new Fake(); using (var c = Controller(f)) {
                    Import(c); c.Start(new WozLanUiConfiguration(PeerRole.Operator, "127.0.0.1", "43101", "127.0.0.1", "43102"));
                    Assert(c.Status.Contains("127.0.0.1")); c.Tick(); Assert(c.Status.Contains("127.0.0.1") && f.Starts == 1);
                }
            });
            Test("reject zero high and malformed ports and identical endpoint", () => {
                foreach (var port in new[] { "0", "65536", "-1", " 43101", "1e3", "" })
                    Throws<ArgumentException>(() => new WozLanUiConfiguration(PeerRole.Operator, "192.168.1.2", port, "192.168.1.3", "43102"));
                Throws<ArgumentException>(() => new WozLanUiConfiguration(PeerRole.Operator, "192.168.1.2", "43101", "192.168.1.2", "43101"));
            });
            Test("construction import and tick never start transport", () => {
                var f = new Fake(); using (var c = Controller(f)) { Import(c); c.Tick(); Assert(f.Starts == 0 && f.Ticks == 0 && c.State == WozLanUiState.Ready); }
            });
            Test("explicit start once and repeated click cannot reopen", () => {
                var f = new Fake(); using (var c = Controller(f)) { Import(c); c.Start(Config()); Throws<InvalidOperationException>(() => c.Start(Config())); Assert(f.Starts == 1); }
                Assert(f.Closes == 1);
            });
            Test("stop queued tick and repeated dispose do not revive", () => {
                var f = new Fake(); var c = Controller(f); Import(c); c.Start(Config()); c.Invalidate(); c.Tick(); c.Invalidate(); c.Dispose(); c.Dispose(); c.Tick();
                Assert(f.Closes == 1 && f.Ticks == 0 && c.State == WozLanUiState.Disposed);
            });
            Test("config invalidation rejects old token and requires fresh", () => {
                var f = new Fake(); using (var c = Controller(f)) { Import(c); c.Start(Config()); c.Invalidate(); Throws<InvalidOperationException>(() => Import(c)); Import(c, 2); Assert(c.State == WozLanUiState.Ready && f.Closes == 1); }
            });
            Test("constructor failure consumes token and clears key copy", () => {
                byte[] seen = null; using (var c = new WozLanDiagnosticController((a,b,k,d,e) => { seen = k; throw new InvalidOperationException(); })) {
                    Import(c); Throws<InvalidOperationException>(() => c.Start(Config())); foreach (byte b in seen) Assert(b == 0); Throws<InvalidOperationException>(() => Import(c));
                }
            });
            Test("start fault closes once and consumes token", () => {
                var f = new Fake { FailStart = true }; using (var c = Controller(f)) { Import(c); Throws<InvalidOperationException>(() => c.Start(Config())); c.Invalidate(); Assert(c.Status.Contains("synthetic_start_failure")); Throws<InvalidOperationException>(() => Import(c)); Assert(f.Closes == 1); }
            });
            Test("null config attempt consumes token without transport", () => {
                var f = new Fake(); using (var c = Controller(f)) { Import(c); Throws<ArgumentException>(() => c.Start(null)); Throws<InvalidOperationException>(() => Import(c)); Assert(f.Starts == 0); }
            });
            Test("imported token cannot start Participant creator", () => {
                var f = new Fake(); using (var c = Controller(f)) { Import(c); Throws<InvalidOperationException>(() => c.Start(Config(PeerRole.Participant))); Assert(f.Starts == 0); }
            });
            Test("fresh creator cannot start Operator joiner", () => {
                var f = new Fake(); using (var c = Controller(f)) { var chars = c.CreatePairing(); Array.Clear(chars, 0, chars.Length); Throws<InvalidOperationException>(() => c.Start(Config())); Assert(f.Starts == 0); }
            });
            Test("fresh creator starts Participant and no secret survives controller", () => {
                var f = new Fake(); using (var c = Controller(f)) { var chars = c.CreatePairing(); Array.Clear(chars, 0, chars.Length); c.Start(Config(PeerRole.Participant)); Assert(f.Starts == 1); }
            });
            Test("tick fault terminates and never reconnects", () => {
                var f = new Fake { FaultOnTick = true }; using (var c = Controller(f)) { Import(c); c.Start(Config()); c.Tick(); c.Tick(); Assert(f.Ticks == 1 && f.Closes == 1 && c.State == WozLanUiState.Ended); }
            });
            Test("tick exception terminates without showing exception data", () => {
                var f = new Fake { FailTick = true }; using (var c = Controller(f)) { Import(c); c.Start(Config()); Throws<InvalidOperationException>(() => c.Tick()); Assert(f.Closes == 1 && c.State == WozLanUiState.Ended); }
            });
            Test("malformed import invalidates previous ready state", () => {
                var f = new Fake(); using (var c = Controller(f)) { Import(c); Throws<ArgumentException>(() => c.ImportPairing(new char[1])); Throws<InvalidOperationException>(() => c.Start(Config())); Assert(f.Starts == 0); }
            });
            Test("config edit before Start consumes loaded token", () => {
                var f = new Fake(); using (var c = Controller(f)) { Import(c); c.Invalidate(); Throws<InvalidOperationException>(() => Import(c)); Assert(f.Starts == 0); }
            });
            Test("bounded attempted-token history fails closed at 64", () => {
                var f = new Fake(); using (var c = Controller(f)) {
                    for (int i = 1; i <= 64; i++) { Import(c, i); c.Start(Config()); c.Invalidate(); }
                    Throws<InvalidOperationException>(() => Import(c, 65)); Assert(f.Starts == 64 && f.Closes == 64);
                }
            });
            Test("sanitized cleanup uncertainty remains visible", () => {
                var f = new Fake { FailClose = true }; using (var c = Controller(f)) { Import(c); c.Start(Config()); c.Invalidate(); c.Invalidate(); Assert(c.Status.Contains("cleanup_failed")); Throws<InvalidOperationException>(() => Import(c, 2)); Assert(f.Starts == 1); }
            });
            Test("blank form is masked non-listening and closes idempotently", () => {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var form = new WozLanDiagnosticLauncher();
                var c = (WozLanDiagnosticController)typeof(WozLanDiagnosticLauncher).GetField("model", flags).GetValue(form);
                var token = (TextBox)typeof(WozLanDiagnosticLauncher).GetField("token", flags).GetValue(form);
                var timer = (Timer)typeof(WozLanDiagnosticLauncher).GetField("timer", flags).GetValue(form);
                Assert(!form.Visible && c.State == WozLanUiState.Empty && !timer.Enabled && token.Text == "" && token.UseSystemPasswordChar);
                form.Dispose(); form.Dispose(); Assert(c.State == WozLanUiState.Disposed);
            });
            Test("blank offscreen render never shows form or secret", () => {
                using (var form = new WozLanDiagnosticLauncher()) {
                    // A hidden Form suppresses child visibility in DrawToBitmap.
                    // Detach only its blank child layout; never Show the Form.
                    var panel = form.Controls[0]; form.Controls.Remove(panel); panel.Font = form.Font;
                    using (panel) using (var bitmap = new Bitmap(form.ClientSize.Width, 1400)) {
                        panel.Size = bitmap.Size; panel.CreateControl(); panel.PerformLayout();
                        panel.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        Assert(!form.Visible);
                        int changed = 0; Color background = bitmap.GetPixel(0, 0);
                        for (int y = 0; y < bitmap.Height; y += 5) for (int x = 0; x < bitmap.Width; x += 5)
                            if (bitmap.GetPixel(x, y) != background) changed++;
                        Assert(changed > 100);
                        bitmap.Save(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "launcher-blank.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
            });
            Console.WriteLine("RESULT " + passed + " passed; " + failed + " failed. Fake lifecycle and blank offscreen UI only; no visible GUI, sockets, SDK or recording.");
            return failed == 0 ? 0 : 1;
        }
    }
}
