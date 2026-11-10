using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AvatarStroop.Core
{
    public enum WozControlKind { SourceConfirm = 1, SourceRevoke = 2, SourceRevokeAck = 3, PreflightChallenge = 4, PreflightObservation = 5, TaskReceipt = 6 }

    // Logical ownership declarations, never physical device or person identity.
    public sealed class WozControlBinding
    {
        public WozLanTaskContext TaskContext { get; private set; }
        public string SourceOwner { get; private set; }
        public long SourceGeneration { get; private set; }
        public long ChoiceGeneration { get; private set; }
        public WozControlBinding(WozLanTaskContext taskContext, string sourceOwner, long sourceGeneration, long choiceGeneration)
        {
            if (taskContext == null || sourceGeneration < 0 || choiceGeneration < 0) throw new ArgumentException("Invalid control binding");
            WireCodec.RequireIdentity(sourceOwner);
            TaskContext = taskContext; SourceOwner = sourceOwner; SourceGeneration = sourceGeneration; ChoiceGeneration = choiceGeneration;
        }
    }

    // Plaintext shared-key-authenticated declarations only. Observation timestamps
    // do not prove capture. RevokeAck declares receiver cleanup, NOT participant
    // pause; the actual authoritative runtime must perform and verify that pause.
    public sealed class WozControlMessage
    {
        public WozControlKind Kind { get; private set; }
        public long Sequence { get; private set; }
        public double SentSeconds { get; private set; }
        public WozControlBinding Binding { get; private set; }
        public string Challenge { get; private set; }
        public double? ChallengeReceivedOperatorSeconds { get; private set; }
        public double? HeadObservedOperatorSeconds { get; private set; }
        public double? EyeObservedOperatorSeconds { get; private set; }
        public WozControlMessage(WozControlKind kind, long sequence, double sentSeconds, WozControlBinding binding, string challenge,
            double? challengeReceivedOperatorSeconds = null, double? headObservedOperatorSeconds = null, double? eyeObservedOperatorSeconds = null)
        {
            if (!Enum.IsDefined(typeof(WozControlKind), kind) || sequence < 0 || !WozAuthenticatedLanEndpoint.Time(sentSeconds) || binding == null)
                throw new ArgumentException("Invalid control declaration");
            WireCodec.RequireIdentity(challenge);
            if (kind == WozControlKind.PreflightObservation)
            {
                if (!challengeReceivedOperatorSeconds.HasValue || !headObservedOperatorSeconds.HasValue || !eyeObservedOperatorSeconds.HasValue ||
                    !WozAuthenticatedLanEndpoint.Time(challengeReceivedOperatorSeconds.Value) || !WozAuthenticatedLanEndpoint.Time(headObservedOperatorSeconds.Value) ||
                    !WozAuthenticatedLanEndpoint.Time(eyeObservedOperatorSeconds.Value) || headObservedOperatorSeconds.Value <= challengeReceivedOperatorSeconds.Value ||
                    eyeObservedOperatorSeconds.Value <= challengeReceivedOperatorSeconds.Value || headObservedOperatorSeconds.Value > sentSeconds || eyeObservedOperatorSeconds.Value > sentSeconds)
                    throw new ArgumentException("Observations must follow challenge receipt and precede send");
            }
            else if (challengeReceivedOperatorSeconds.HasValue || headObservedOperatorSeconds.HasValue || eyeObservedOperatorSeconds.HasValue)
                throw new ArgumentException("Unexpected observation fields");
            Kind = kind; Sequence = sequence; SentSeconds = sentSeconds; Binding = binding; Challenge = challenge;
            ChallengeReceivedOperatorSeconds = challengeReceivedOperatorSeconds; HeadObservedOperatorSeconds = headObservedOperatorSeconds; EyeObservedOperatorSeconds = eyeObservedOperatorSeconds;
        }
    }

    // Memory-only byte verification, never live admission or recording authority.
    // New endpoint lifetimes require fresh epoch/key provisioning, just as LAN01.
    public sealed class WozAuthenticatedControlEndpoint : IDisposable
    {
        public const int MaximumEnvelopeBytes = 4096;
        // All integers/doubles little endian; strings are u16-byte-count strict UTF8.
        // WOZCTL01, sender u8, receiver u8, context[32], kind u8, seq i64,
        // sent f64, binding (task u16+bytes, owner string, source/choice i64),
        // challenge string, observation count u8 (0/3), optional 3xf64, tag[32].
        // Task embeds the existing canonical codec; SAME serialization runtime/build
        // required, as for LAN01. Domain-derived key prevents cross-protocol tunneling.
        const string Domain = "WOZ-CONTROL-KEY-1";
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("WOZCTL01");
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        readonly object gate = new object(), token = new object();
        readonly WozLanContext context;
        readonly PeerRole localRole, remoteRole;
        readonly byte[] key;
        readonly double maximumReceiptAge;
        long sentSequence = -1, receivedSequence = -1;
        double sentTime = -1, remoteTime = -1, receiptTime = -1, nowTime = -1;
        bool disposed;

        public sealed class VerifiedControl
        {
            public WozControlMessage Message { get; private set; }
            public double OriginalLocalReceiptSeconds { get; private set; }
            public bool ReceiptCapturedBySocket { get { return false; } }
            private VerifiedControl(WozControlMessage message, double receipt) { Message = message; OriginalLocalReceiptSeconds = receipt; }
            internal static VerifiedControl Create(WozAuthenticatedControlEndpoint endpoint, object proof, WozControlMessage message, double receipt)
            {
                if (endpoint == null || endpoint.disposed || !Object.ReferenceEquals(endpoint.token, proof)) throw new InvalidOperationException("Byte verification required");
                return new VerifiedControl(message, receipt);
            }
        }

        public WozAuthenticatedControlEndpoint(WozLanContext context, PeerRole localRole, byte[] key, double maximumReceiptAge)
        {
            if (context == null || !Enum.IsDefined(typeof(PeerRole), localRole) || key == null || key.Length != 32 ||
                !WozAuthenticatedLanEndpoint.Time(maximumReceiptAge) || maximumReceiptAge <= 0) throw new ArgumentException("Invalid endpoint configuration");
            this.context = context; this.localRole = localRole; remoteRole = localRole == PeerRole.Participant ? PeerRole.Operator : PeerRole.Participant;
            using (var hmac = new HMACSHA256(key)) this.key = hmac.ComputeHash(Encoding.ASCII.GetBytes(Domain));
            this.maximumReceiptAge = maximumReceiptAge;
        }
        static bool Reject(string issue, out string error) { error = issue; return false; }
        static bool Equal(byte[] a, byte[] b)
        { if (a.Length != b.Length) return false; int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0; }
        static PeerRole Sender(WozControlKind kind)
        { return kind == WozControlKind.SourceRevokeAck || kind == WozControlKind.PreflightObservation || kind == WozControlKind.TaskReceipt ? PeerRole.Operator : PeerRole.Participant; }
        static byte[] ReadBytes(BinaryReader r, int n)
        { if (n < 0 || n > r.BaseStream.Length - r.BaseStream.Position) throw new FormatException("Truncated field"); return r.ReadBytes(n); }
        static void WriteIdentity(BinaryWriter w, string s)
        { WireCodec.RequireIdentity(s); byte[] b = Utf8.GetBytes(s); w.Write((ushort)b.Length); w.Write(b); }
        static string ReadIdentity(BinaryReader r)
        { int n = r.ReadUInt16(); if (n < 1 || n > 128) throw new FormatException("Identity length"); string s = Utf8.GetString(ReadBytes(r, n)); WireCodec.RequireIdentity(s); return s; }
        void WriteBinding(BinaryWriter w, WozControlBinding binding)
        {
            byte[] task = binding.TaskContext.Encode(context.Participant);
            if (task.Length > WireCodec.MaxPacketBytes + 8) throw new ArgumentException("Task size");
            byte[] payload = new byte[task.Length - 8]; Buffer.BlockCopy(task, 8, payload, 0, payload.Length);
            WirePacket decoded; string error;
            if (!WireCodec.TryDecode(payload, out decoded, out error) || decoded.Kind != WireKind.Task || !Equal(payload, WireCodec.Encode(decoded)))
                throw new ArgumentException("Noncanonical task declaration");
            w.Write((ushort)task.Length); w.Write(task); WriteIdentity(w, binding.SourceOwner); w.Write(binding.SourceGeneration); w.Write(binding.ChoiceGeneration);
        }
        byte[] BindingBytes(WozControlBinding binding)
        { using (var s = new MemoryStream()) { using (var w = new BinaryWriter(s)) { WriteBinding(w, binding); return s.ToArray(); } } }
        WozControlBinding ReadBinding(BinaryReader r)
        {
            int n = r.ReadUInt16(); if (n < 9 || n > WireCodec.MaxPacketBytes + 8) throw new FormatException("Task length");
            long revision = r.ReadInt64(); byte[] payload = ReadBytes(r, n - 8); WirePacket packet; string error;
            if (!WireCodec.TryDecode(payload, out packet, out error) || packet.Kind != WireKind.Task || !Equal(payload, WireCodec.Encode(packet))) throw new FormatException("Noncanonical task");
            var task = new WozLanTaskContext(packet.Snapshot, revision, packet.Sequence, packet.SentLocalSeconds);
            // Re-encoding with the configured participant enforces every peer field.
            byte[] canonical = task.Encode(context.Participant);
            var expected = new byte[n]; WozAuthenticatedLanEndpoint.WriteLong(expected, 0, revision); Buffer.BlockCopy(payload, 0, expected, 8, payload.Length);
            if (!Equal(canonical, expected)) throw new FormatException("Task peer context");
            return new WozControlBinding(task, ReadIdentity(r), r.ReadInt64(), r.ReadInt64());
        }
        byte[] Encode(WozControlMessage m, PeerRole sender, PeerRole receiver)
        {
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s))
            {
                w.Write(Magic); w.Write((byte)sender); w.Write((byte)receiver); w.Write(context.CopyDigest());
                w.Write((byte)m.Kind); w.Write(m.Sequence); w.Write(m.SentSeconds); WriteBinding(w, m.Binding); WriteIdentity(w, m.Challenge);
                w.Write((byte)(m.Kind == WozControlKind.PreflightObservation ? 3 : 0));
                if (m.Kind == WozControlKind.PreflightObservation) { w.Write(m.ChallengeReceivedOperatorSeconds.Value); w.Write(m.HeadObservedOperatorSeconds.Value); w.Write(m.EyeObservedOperatorSeconds.Value); }
                return s.ToArray();
            }
        }
        byte[] Mac(byte[] b, int count) { using (var h = new HMACSHA256(key)) return h.ComputeHash(b, 0, count); }
        public bool TryProtect(WozControlMessage declaration, out byte[] bytes, out string error)
        {
            lock (gate)
            {
                bytes = null; error = null; if (disposed) return Reject("disposed", out error);
                if (declaration == null || Sender(declaration.Kind) != localRole) return Reject("control_direction", out error);
                if (declaration.Sequence == Int64.MaxValue || declaration.Sequence <= sentSequence || declaration.SentSeconds < sentTime) return Reject("send_sequence_or_clock", out error);
                try
                {
                    byte[] content = Encode(declaration, localRole, remoteRole);
                    if (content.Length + 32 > MaximumEnvelopeBytes) return Reject("envelope_size", out error);
                    byte[] result = new byte[content.Length + 32]; Buffer.BlockCopy(content, 0, result, 0, content.Length); Buffer.BlockCopy(Mac(content, content.Length), 0, result, content.Length, 32);
                    sentSequence = declaration.Sequence; sentTime = declaration.SentSeconds; bytes = result; return true;
                }
                catch (Exception e) when (e is ArgumentException || e is FormatException || e is OverflowException || e is CryptographicException)
                { return Reject("protect_failed", out error); }
            }
        }
        // The expected binding is mandatory but caller-declared, not authoritative
        // provenance. Caller must separately correlate Challenge and enforce owned
        // lifecycle transitions. Receipt clocks are also declarations, not sockets.
        public bool TryReceive(byte[] input, double originalLocalReceipt, double nowLocal, WozControlBinding expectedBinding, out VerifiedControl message, out string error)
        {
            lock (gate)
            {
                message = null; error = null; if (disposed) return Reject("disposed", out error);
                if (expectedBinding == null) return Reject("expected_binding_required", out error);
                if (input == null || input.Length < 94 || input.Length > MaximumEnvelopeBytes) return Reject("envelope_size", out error);
                if (!WozAuthenticatedLanEndpoint.Time(originalLocalReceipt) || !WozAuthenticatedLanEndpoint.Time(nowLocal) || originalLocalReceipt > nowLocal ||
                    originalLocalReceipt < receiptTime || nowLocal < nowTime || nowLocal - originalLocalReceipt > maximumReceiptAge) return Reject("declared_receipt_clock", out error);
                byte[] b = (byte[])input.Clone(); int count = b.Length - 32;
                try
                {
                    byte[] actualTag = new byte[32]; Buffer.BlockCopy(b, count, actualTag, 0, 32);
                    if (!Equal(Mac(b, count), actualTag)) return Reject("mac_mismatch", out error);
                    using (var s = new MemoryStream(b, 0, count, false)) using (var r = new BinaryReader(s))
                    {
                        if (!Equal(ReadBytes(r, 8), Magic) || r.ReadByte() != (byte)remoteRole || r.ReadByte() != (byte)localRole || !Equal(ReadBytes(r, 32), context.CopyDigest())) return Reject("envelope_context", out error);
                        var kind = (WozControlKind)r.ReadByte(); long sequence = r.ReadInt64(); double sent = r.ReadDouble();
                        WozControlBinding binding = ReadBinding(r); string challenge = ReadIdentity(r); int observations = r.ReadByte();
                        if (observations != (kind == WozControlKind.PreflightObservation ? 3 : 0)) return Reject("observation_fields", out error);
                        double? receipt = null, head = null, eye = null;
                        if (observations == 3) { receipt = r.ReadDouble(); head = r.ReadDouble(); eye = r.ReadDouble(); }
                        var m = new WozControlMessage(kind, sequence, sent, binding, challenge, receipt, head, eye);
                        if (s.Position != count || !Equal(Encode(m, remoteRole, localRole), ReadContent(b, count))) return Reject("noncanonical_control", out error);
                        if (Sender(kind) != remoteRole) return Reject("control_direction", out error);
                        if (!Equal(BindingBytes(binding), BindingBytes(expectedBinding))) return Reject("unexpected_binding", out error);
                        if (sequence == Int64.MaxValue || sequence <= receivedSequence || sent < remoteTime) return Reject("replay_or_remote_clock", out error);
                        var verified = VerifiedControl.Create(this, token, m, originalLocalReceipt);
                        receivedSequence = sequence; remoteTime = sent; receiptTime = originalLocalReceipt; nowTime = nowLocal; message = verified; return true;
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is FormatException || e is OverflowException || e is CryptographicException || e is EndOfStreamException)
                { return Reject("receive_failed", out error); }
            }
        }
        static byte[] ReadContent(byte[] b, int count) { byte[] result = new byte[count]; Buffer.BlockCopy(b, 0, result, 0, count); return result; }
        // Clears the owned derived key only; managed crypto copies and caller key
        // memory are outside this object's erasure guarantees.
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; Array.Clear(key, 0, key.Length); } }
    }
}
