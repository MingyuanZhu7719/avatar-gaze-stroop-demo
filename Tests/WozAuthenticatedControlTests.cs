using System;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    // Memory-only fixtures. Fixed test keys/epochs are never session provisioning.
    public static class WozAuthenticatedControlTests
    {
        static byte[] Key() { var k = new byte[32]; for (int i = 0; i < k.Length; i++) k[i] = (byte)i; return k; }
        static WozLanContext Context(string session = "control-test", byte epoch = 1)
        {
            var e = new byte[16]; e[0] = epoch;
            return new WozLanContext(new PeerIdentity(session, "p", PeerRole.Participant, "task", ControlSource.LiveOperator, "pc"),
                new PeerIdentity(session, "o", PeerRole.Operator, "source", ControlSource.LiveOperator, "oc"), e, "map", "device");
        }
        static WozControlBinding Binding(long revision = 2, long sequence = 3, long source = 4, long choice = 5, string owner = "owner")
        { return new WozControlBinding(new WozLanTaskContext(new TaskSnapshot("control-test", 0, 0, "trial", Colour.Red, Colour.Blue, Phase.MainTask, Condition.B), revision, sequence, 1), owner, source, choice); }
        static WozControlMessage Message(WozControlKind kind = WozControlKind.SourceConfirm, long sequence = 1, double sent = 4)
        { return new WozControlMessage(kind, sequence, sent, Binding(), "challenge", kind == WozControlKind.PreflightObservation ? (double?)2 : null, kind == WozControlKind.PreflightObservation ? (double?)3 : null, kind == WozControlKind.PreflightObservation ? (double?)3.5 : null); }
        static WozAuthenticatedControlEndpoint End(PeerRole role = PeerRole.Operator, WozLanContext context = null, byte[] key = null)
        { return new WozAuthenticatedControlEndpoint(context ?? Context(), role, key ?? Key(), 1); }
        static void Assert(bool value, string error = "Assertion failed") { if (!value) throw new Exception(error); }
        static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected invalid declaration"); }
        static byte[] Protect(WozControlMessage m = null)
        {
            m = m ?? Message(); var role = m.Kind == WozControlKind.PreflightObservation || m.Kind == WozControlKind.SourceRevokeAck || m.Kind == WozControlKind.TaskReceipt ? PeerRole.Operator : PeerRole.Participant;
            using (var e = End(role)) { byte[] bytes; string error; Assert(e.TryProtect(m, out bytes, out error), error); return bytes; }
        }
        static WozAuthenticatedControlEndpoint.VerifiedControl Receive(WozAuthenticatedControlEndpoint e, byte[] b, double receipt = 10, double now = 10.1)
        { WozAuthenticatedControlEndpoint.VerifiedControl m; string error; Assert(e.TryReceive(b, receipt, now, Binding(), out m, out error), error); return m; }
        static void Reject(WozAuthenticatedControlEndpoint e, byte[] b, WozControlBinding binding = null, double receipt = 10, double now = 10.1)
        { WozAuthenticatedControlEndpoint.VerifiedControl m; string error; Assert(!e.TryReceive(b, receipt, now, binding ?? Binding(), out m, out error)); Assert(m == null && error != null); }
        static byte[] Resign(byte[] b)
        {
            byte[] key; using (var h = new HMACSHA256(Key())) key = h.ComputeHash(Encoding.ASCII.GetBytes("WOZ-CONTROL-KEY-1"));
            using (var h = new HMACSHA256(key)) { var tag = h.ComputeHash(b, 0, b.Length - 32); Buffer.BlockCopy(tag, 0, b, b.Length - 32, 32); }
            return b;
        }
        static byte[] Mutate(byte[] original, int offset, byte value) { var b = (byte[])original.Clone(); b[offset] = value; return Resign(b); }
        static int failed;
        static void Run(string name, Action test) { try { test(); Console.WriteLine("PASS control " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL control " + name + " " + e); } }
        public static int RunAll()
        {
            failed = 0;
            Run("SourceConfirm prior independent 244-byte known answer unchanged", () => {
                const string expected="574F5A43544C3031000197D24BE97DC2968ADA3EFC224FE85AACD98376599D7D7463BF86D10D315067740101000000000000000000000000001040740002000000000000004153577C327C5461736B7C5932397564484A76624331305A584E307C63413D3D7C5061727469636970616E747C6447467A61773D3D7C4C6976654F70657261746F727C63474D3D7C337C317C307C307C64484A705957773D7C5265647C426C75657C4D61696E5461736B7C4205006F776E65720400000000000000050000000000000009006368616C6C656E676500CD336F0E3E46356234E609578F644CFFACC6527542ECE811DB024406AF026AD6";
                byte[] bytes=Protect();Assert(bytes.Length==244,"prior length changed");Assert(BitConverter.ToString(bytes).Replace("-","")==expected,"prior canonical message or MAC changed");
            });
            Run("all kinds round trip and original receipt", () => {
                foreach (WozControlKind kind in Enum.GetValues(typeof(WozControlKind)))
                {
                    bool fromOperator = kind == WozControlKind.SourceRevokeAck || kind == WozControlKind.PreflightObservation || kind == WozControlKind.TaskReceipt;
                    byte[] b = Protect(Message(kind));
                    using (var e = End(fromOperator ? PeerRole.Participant : PeerRole.Operator))
                    {
                        var v = Receive(e, b); Assert(v.Message.Kind == kind && v.Message.Challenge == "challenge" && v.Message.Sequence == 1);
                        Assert(v.Message.Binding.TaskContext.Revision == 2 && v.Message.Binding.SourceGeneration == 4 && v.Message.Binding.ChoiceGeneration == 5);
                        Assert(v.OriginalLocalReceiptSeconds == 10 && !v.ReceiptCapturedBySocket);
                        Assert(Convert.ToBase64String(b) == Convert.ToBase64String(Protect(v.Message)));
                    }
                }
            });
            Run("directions and reflection", () => {
                foreach (WozControlKind kind in Enum.GetValues(typeof(WozControlKind)))
                {
                    bool fromOperator = kind == WozControlKind.SourceRevokeAck || kind == WozControlKind.PreflightObservation || kind == WozControlKind.TaskReceipt;
                    using (var e = End(fromOperator ? PeerRole.Participant : PeerRole.Operator))
                    { byte[] b; string error; Assert(!e.TryProtect(Message(kind), out b, out error)); }
                    using (var e = End(fromOperator ? PeerRole.Operator : PeerRole.Participant)) Reject(e, Protect(Message(kind)));
                }
                using (var e = End()) Reject(e, Mutate(Protect(), 42, (byte)WozControlKind.SourceRevokeAck));
            });
            Run("wrong key session epoch and exact binding", () => {
                byte[] b = Protect(), key = Key(); key[0] ^= 1;
                using (var e = End(key: key)) Reject(e, b);
                using (var e = End(context: Context("other"))) Reject(e, b);
                using (var e = End(context: Context(epoch: 2))) Reject(e, b);
                using (var e = End())
                {
                    Reject(e, b, Binding(revision: 3)); Reject(e, b, Binding(sequence: 4)); Reject(e, b, Binding(source: 5));
                    Reject(e, b, Binding(choice: 6)); Reject(e, b, Binding(owner: "other"));
                    WozAuthenticatedControlEndpoint.VerifiedControl m; string error; Assert(!e.TryReceive(b, 10, 10.1, null, out m, out error));
                    Receive(e, b);
                }
            });
            Run("every byte tamper and bounded lengths", () => {
                byte[] b = Protect(); using (var e = End())
                {
                    for (int i = 0; i < b.Length; i++) { var bad = (byte[])b.Clone(); bad[i] ^= 1; Reject(e, bad); }
                    Reject(e, new byte[4097]); Reject(e, new byte[0]); Reject(e, null);
                    Reject(e, Mutate(b, 59, 255)); Reject(e, Mutate(b, 60, 255));
                    Reject(e, Mutate(b, b.Length - 33, 1)); Reject(e, Mutate(b, 42, 0)); Receive(e, b);
                }
            });
            Run("noncanonical clocks and malformed declarations", () => {
                Throws(() => new WozControlMessage((WozControlKind)99, 0, 0, Binding(), "x"));
                Throws(() => new WozControlMessage(WozControlKind.SourceConfirm, 0, 0, Binding(), "x", 0));
                Throws(() => new WozControlMessage(WozControlKind.PreflightObservation, 0, 4, Binding(), "x", 2, 2, 3));
                Throws(() => new WozControlMessage(WozControlKind.PreflightObservation, 0, 4, Binding(), "x", 2, 3, 5));
                Throws(() => new WozControlMessage(WozControlKind.PreflightObservation, 0, 4, Binding(), "x", 2, Double.NaN, 3));
                Throws(() => new WozControlBinding(Binding().TaskContext, "owner", -1, 0));
                foreach (double bad in new[] { Double.NaN, Double.PositiveInfinity, -1, BitConverter.Int64BitsToDouble(Int64.MinValue) })
                {
                    Throws(() => new WozControlMessage(WozControlKind.SourceConfirm, 0, bad, Binding(), "x"));
                    byte[] b = Protect(); byte[] bits = BitConverter.GetBytes(bad); if (!BitConverter.IsLittleEndian) Array.Reverse(bits);
                    Buffer.BlockCopy(bits, 0, b, 51, 8); using (var e = End()) Reject(e, Resign(b));
                }
            });
            Run("sequence and send time ordering", () => {
                using (var e = End()) { Receive(e, Protect(Message(sequence: 2))); Reject(e, Protect(Message(sequence: 2))); Reject(e, Protect()); Reject(e, Protect(Message(sequence: 3, sent: 3))); Receive(e, Protect(Message(sequence: 3, sent: 4))); }
                using (var e = End(PeerRole.Participant))
                {
                    byte[] b; string error; Assert(e.TryProtect(Message(sequence: 2), out b, out error));
                    Assert(!e.TryProtect(Message(sequence: 2), out b, out error)); Assert(!e.TryProtect(Message(sequence: 3, sent: 3), out b, out error));
                    Assert(e.TryProtect(Message(sequence: 3, sent: 4), out b, out error));
                }
            });
            Run("receipt age and monotonic local clocks", () => {
                using (var e = End())
                {
                    Reject(e, Protect(), receipt: 8, now: 10); Reject(e, Protect(), receipt: 11, now: 10); Reject(e, Protect(), receipt: Double.NaN);
                    Receive(e, Protect()); Reject(e, Protect(Message(sequence: 2)), receipt: 9.9, now: 10.2);
                    Reject(e, Protect(Message(sequence: 2)), receipt: 10, now: 10.05); Receive(e, Protect(Message(sequence: 2)), 10.2, 10.3);
                }
            });
            Run("counter exhaustion and terminal disposal", () => {
                using (var e = End(PeerRole.Participant))
                {
                    byte[] b; string error; Assert(e.TryProtect(Message(sequence: Int64.MaxValue - 1), out b, out error));
                    Assert(!e.TryProtect(Message(sequence: Int64.MaxValue), out b, out error)); Assert(!e.TryProtect(Message(sequence: 0), out b, out error));
                    e.Dispose(); e.Dispose(); Assert(!e.TryProtect(Message(), out b, out error) && error == "disposed");
                }
                using (var e = End())
                {
                    byte[] b = Protect(); for (int i = 0; i < 8; i++) b[43 + i] = i == 7 ? (byte)127 : (byte)255;
                    Reject(e, Resign(b)); Receive(e, Protect(Message(sequence: Int64.MaxValue - 1))); Reject(e, Protect()); e.Dispose(); Reject(e, Protect());
                }
            });
            Run("old protocol never tunnels", () => {
                var c = Context(); byte[] b; string error;
                using (var old = new WozAuthenticatedLanEndpoint(c, PeerRole.Participant, Key(), 1))
                    Assert(old.TryProtect(WirePacket.TimeRequest(c.Participant, 1, 4, "x"), null, null, out b, out error));
                using (var e = End()) Reject(e, b);
                using (var old = new WozAuthenticatedLanEndpoint(c, PeerRole.Operator, Key(), 1))
                { WozAuthenticatedLanEndpoint.VerifiedMessage m; Assert(!old.TryReceive(Protect(), 10, 10.1, null, out m, out error)); }
            });
            return failed;
        }
    }
}
