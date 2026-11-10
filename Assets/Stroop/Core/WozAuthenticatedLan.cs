using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AvatarStroop.Core
{
    // Offline message authentication primitive, NOT a socket, pairing workflow,
    // admission gate, clock calibration, tracking validation or recording grant.
    // Every endpoint restart MUST receive a newly provisioned random 32-byte PSK
    // and fresh 16-byte epoch. Length checks cannot establish entropy/uniqueness.
    // Reusing both permits replay into a new instance whose high-water is empty.
    public sealed class WozLanContext
    {
        public PeerIdentity Participant { get; private set; }
        public PeerIdentity Operator { get; private set; }
        public string Epoch { get; private set; }
        public string Mapping { get; private set; }
        public string OperatorDevice { get; private set; }
        readonly byte[] digest;
        public WozLanContext(PeerIdentity participant, PeerIdentity op, byte[] freshEpoch,
            string mapping, string operatorDevice)
        {
            if (participant == null || op == null || participant.Role != PeerRole.Participant ||
                op.Role != PeerRole.Operator || participant.SessionId != op.SessionId ||
                participant.PeerId == op.PeerId || participant.ClockId == op.ClockId ||
                participant.Source != op.Source ||
                (op.Source != ControlSource.LiveOperator && op.Source != ControlSource.Simulator) ||
                (op.Source == ControlSource.Simulator && !op.SourceId.StartsWith("simulator/", StringComparison.Ordinal)) ||
                freshEpoch == null || freshEpoch.Length != 16)
                throw new ArgumentException("Explicit distinct peers and a newly provisioned 16-byte epoch required");
            WireCodec.RequireIdentity(mapping); WireCodec.RequireIdentity(operatorDevice);
            Participant = participant; Operator = op; Epoch = Convert.ToBase64String(freshEpoch);
            // Source retains the existing control-mode meaning on BOTH peers.
            // Roles identify task ownership. Simulator claims remain simulator;
            // a stream name or authenticated assertion is not hardware proof.
            Mapping = mapping; OperatorDevice = operatorDevice;
            string fields = "WOZLAN-CONTEXT-1|" + Epoch + "|" + PeerFields(participant) + "|" +
                PeerFields(op) + "|" + Id(mapping) + "|" + Id(operatorDevice);
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.ASCII.GetBytes(fields));
        }
        static string Id(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
        static string PeerFields(PeerIdentity p)
        {
            return String.Join("|", new[] { Id(p.SessionId), Id(p.PeerId), ((int)p.Role).ToString(CultureInfo.InvariantCulture),
                Id(p.SourceId), ((int)p.Source).ToString(CultureInfo.InvariantCulture), Id(p.ClockId) });
        }
        internal PeerIdentity Peer(PeerRole role) { return role == PeerRole.Participant ? Participant : Operator; }
        internal byte[] CopyDigest() { return (byte[])digest.Clone(); }
    }

    // Existing legacy A/B/C/D task schema only, NOT named-option provenance.
    // Declared task context. Callers still must bind it to their authoritative
    // task host; a valid MAC does not prove the host actually issued this task.
    public sealed class WozLanTaskContext
    {
        public TaskSnapshot Task { get; private set; }
        public long Revision { get; private set; }
        public long ParticipantSequence { get; private set; }
        public double ParticipantSentSeconds { get; private set; }
        public WozLanTaskContext(TaskSnapshot task, long revision, long participantSequence, double participantSentSeconds)
        {
            if (task == null || revision < 0 || participantSequence < 0 || !WozAuthenticatedLanEndpoint.Time(participantSentSeconds))
                throw new ArgumentException("Invalid declared task context");
            Task = task; Revision = revision; ParticipantSequence = participantSequence; ParticipantSentSeconds = participantSentSeconds;
        }
        internal byte[] Encode(PeerIdentity participant)
        {
            byte[] task = WireCodec.Encode(WirePacket.Task(participant, ParticipantSequence, ParticipantSentSeconds, Task));
            var result = new byte[8 + task.Length]; WozAuthenticatedLanEndpoint.WriteLong(result, 0, Revision);
            Buffer.BlockCopy(task, 0, result, 8, task.Length); return result;
        }
    }

    // Operator-local observations are assertions authenticated with the bytes,
    // not verified hardware capture times. All must follow task receipt.
    public sealed class WozLanPoseObservation
    {
        public double TaskReceivedOperatorSeconds { get; private set; }
        public double HeadObservedOperatorSeconds { get; private set; }
        public double EyeObservedOperatorSeconds { get; private set; }
        public WozLanPoseObservation(double taskReceived, double headObserved, double eyeObserved)
        {
            if (!WozAuthenticatedLanEndpoint.Time(taskReceived) || !WozAuthenticatedLanEndpoint.Time(headObserved) ||
                !WozAuthenticatedLanEndpoint.Time(eyeObserved) || headObserved <= taskReceived || eyeObserved <= taskReceived)
                throw new ArgumentException("Pose observations must follow task receipt on the operator clock");
            TaskReceivedOperatorSeconds = taskReceived; HeadObservedOperatorSeconds = headObserved; EyeObservedOperatorSeconds = eyeObserved;
        }
    }

    public sealed class WozAuthenticatedLanEndpoint : IDisposable
    {
        // Header: ASCII WOZLAN01, sender u8, receiver u8, context SHA-256 [32],
        // task length u16 LE, observation length u8 (0 or 24), payload length u16 LE.
        // Task: revision i64 LE + canonical existing typed Task WirePacket.
        // Observation: task-receipt, head-observed, eye-observed IEEE754 LE doubles.
        // Payload: canonical existing typed WirePacket. Tag: full HMAC-SHA256
        // over EVERY preceding byte, including lengths, context and direction.
        // WireCodec v2 uses Double.ToString("R"). Its spelling varies across
        // .NET/Mono generations. Both peers require the SAME serialization
        // runtime/build until cross-runtime vectors or a versioned portable
        // number codec are supplied. A mismatch fails closed, not interoperably.
        // A parser bound, NOT an MTU/UDP delivery guarantee. A future network
        // adapter needs a measured smaller datagram cap or explicit fragmentation.
        public const int MaximumEnvelopeBytes = 4352;
        const int Header = 47, Tag = 32;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("WOZLAN01");
        readonly object gate = new object();
        readonly object verificationToken = new object();
        readonly WozLanContext context;
        readonly PeerRole localRole, remoteRole;
        readonly byte[] key;
        readonly double maximumReceiptAge;
        long lastProtected = -1, lastReceived = -1;
        long lastSentTaskRevision = -1, lastReceivedTaskRevision = -1;
        long lastSentSample = -1, lastReceivedSample = -1;
        double lastSentHead = -1, lastSentEye = -1, lastReceivedHead = -1, lastReceivedEye = -1;
        byte[] lastSentTask, lastReceivedTask;
        double lastSent = -1, lastRemoteSent = -1, lastReceipt = -1, lastNow = -1;
        bool disposed;

        // The only construction site is TryReceive after byte verification.
        // This is a shared-key-possession result, not peer non-repudiation.
        public sealed class VerifiedMessage
        {
            public WirePacket Packet { get; private set; }
            public WozLanTaskContext TaskContext { get; private set; }
            public WozLanPoseObservation PoseObservation { get; private set; }
            public string ReceiptClockId { get; private set; }
            public double OriginalLocalReceiptSeconds { get; private set; }
            // No socket adapter exists in this increment. The receipt is NOT
            // authenticated by the remote MAC and its origin remains unverified.
            public bool ReceiptCapturedBySocket { get { return false; } }
            private VerifiedMessage(WirePacket packet, WozLanTaskContext task, WozLanPoseObservation pose, string clock, double receipt)
            { Packet = packet; TaskContext = task; PoseObservation = pose; ReceiptClockId = clock; OriginalLocalReceiptSeconds = receipt; }
            internal static VerifiedMessage Create(WozAuthenticatedLanEndpoint endpoint, object token,
                WirePacket packet, WozLanTaskContext task, WozLanPoseObservation pose, string clock, double receipt)
            {
                // The containing type cannot call a nested private constructor
                // in C#. The private per-endpoint token protects this assembly-
                // visible factory; it is never returned, serialized or logged.
                if (endpoint == null || !Object.ReferenceEquals(token, endpoint.verificationToken) || endpoint.disposed)
                    throw new InvalidOperationException("Byte verification capability required");
                return new VerifiedMessage(packet, task, pose, clock, receipt);
            }
        }

        public WozAuthenticatedLanEndpoint(WozLanContext context, PeerRole localRole, byte[] newlyProvisionedSessionKey, double maximumReceiptAge)
        {
            if (context == null || !Enum.IsDefined(typeof(PeerRole), localRole) || newlyProvisionedSessionKey == null ||
                newlyProvisionedSessionKey.Length != 32 || !Time(maximumReceiptAge) || maximumReceiptAge <= 0)
                throw new ArgumentException("Context, role, newly provisioned 32-byte key and positive receipt age required");
            this.context = context; this.localRole = localRole;
            remoteRole = localRole == PeerRole.Operator ? PeerRole.Participant : PeerRole.Operator;
            key = (byte[])newlyProvisionedSessionKey.Clone(); this.maximumReceiptAge = maximumReceiptAge;
        }
        internal static bool Time(double value)
        { return WireCodec.Finite(value) && value >= 0 && (value != 0 || BitConverter.DoubleToInt64Bits(value) == 0); }
        static bool SamePeer(PeerIdentity a, PeerIdentity b)
        { return a.SessionId == b.SessionId && a.PeerId == b.PeerId && a.Role == b.Role && a.SourceId == b.SourceId && a.Source == b.Source && a.ClockId == b.ClockId; }
        static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int different = 0; for (int i = 0; i < a.Length; i++) different |= a[i] ^ b[i]; return different == 0;
        }
        static bool Reject(string issue, out string error) { error = issue; return false; }
        static byte[] Slice(byte[] bytes, int start, int length)
        { var result = new byte[length]; Buffer.BlockCopy(bytes, start, result, 0, length); return result; }
        static void U16(byte[] bytes, int offset, int value) { bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); }
        static int U16(byte[] bytes, int offset) { return bytes[offset] | (bytes[offset + 1] << 8); }
        internal static void WriteLong(byte[] bytes, int offset, long value)
        { ulong bits = unchecked((ulong)value); for (int i = 0; i < 8; i++) { bytes[offset + i] = (byte)bits; bits >>= 8; } }
        static long ReadLong(byte[] bytes, int offset)
        { ulong value = 0; for (int i = 7; i >= 0; i--) value = (value << 8) | bytes[offset + i]; return unchecked((long)value); }
        static void WriteDouble(byte[] bytes, int offset, double value) { WriteLong(bytes, offset, BitConverter.DoubleToInt64Bits(value)); }
        static double ReadDouble(byte[] bytes, int offset) { return BitConverter.Int64BitsToDouble(ReadLong(bytes, offset)); }
        byte[] Mac(byte[] bytes, int count)
        { using (var hmac = new HMACSHA256(key)) return hmac.ComputeHash(bytes, 0, count); }
        byte[] TaskState(WozLanTaskContext task)
        { return WireCodec.Encode(WirePacket.Task(context.Participant, 0, 0, task.Task)); }

        bool Validate(WirePacket packet, PeerRole sender, WozLanTaskContext task, WozLanPoseObservation pose, out string error)
        {
            error = null;
            if (packet == null || !SamePeer(packet.Peer, context.Peer(sender))) return Reject("peer_context", out error);
            switch (packet.Kind)
            {
                case WireKind.Task:
                    if (sender != PeerRole.Participant || task == null || pose != null ||
                        !Equal(WireCodec.Encode(packet), WireCodec.Encode(WirePacket.Task(context.Participant,
                            task.ParticipantSequence, task.ParticipantSentSeconds, task.Task)))) return Reject("task_context", out error);
                    break;
                case WireKind.HeadEye:
                    if (sender != PeerRole.Operator || task == null || pose == null) return Reject("pose_context", out error);
                    var t = task.Task; var s = packet.Sample;
                    if (t.SessionId != context.Participant.SessionId || t.Phase != Phase.MainTask || t.BlockIndex < 0 ||
                        t.TrialId == null || !t.Word.HasValue || !t.Ink.HasValue || !t.Condition.HasValue || t.Condition == Condition.D ||
                        s.BlockIndex != t.BlockIndex || s.Segment != t.Segment || s.DeviceId != context.OperatorDevice ||
                        s.Role != StreamRole.OperatorControl || !s.HeadValid || !s.EyeValid ||
                        pose.HeadObservedOperatorSeconds > packet.SentLocalSeconds || pose.EyeObservedOperatorSeconds > packet.SentLocalSeconds ||
                        s.CaptureLocalSeconds <= pose.TaskReceivedOperatorSeconds || s.CaptureLocalSeconds > pose.HeadObservedOperatorSeconds ||
                        s.CaptureLocalSeconds > pose.EyeObservedOperatorSeconds) return Reject("pose_task_or_observation", out error);
                    break;
                case WireKind.TimeRequest:
                case WireKind.TimeReply:
                    if (task != null || pose != null || (packet.Kind == WireKind.TimeReply &&
                        packet.RequestClockId != context.Peer(sender == PeerRole.Operator ? PeerRole.Participant : PeerRole.Operator).ClockId))
                        return Reject("clock_context", out error);
                    break;
                case WireKind.SafetyStopRequest:
                case WireKind.SafetyStopAck:
                    if (task != null || pose != null || (packet.Kind == WireKind.SafetyStopRequest ? sender != PeerRole.Operator : sender != PeerRole.Participant))
                        return Reject("stop_direction", out error);
                    break;
                default: return Reject("kind_not_allowed", out error);
            }
            return true;
        }

        // Success means bytes protected in memory, NOT sent or delivered. A
        // retry must have a new sequence; no old-sequence delivery is inferred.
        public bool TryProtect(WirePacket packet, WozLanTaskContext task, WozLanPoseObservation pose, out byte[] bytes, out string error)
        {
            lock (gate)
            {
                bytes = null; error = null;
                if (disposed) return Reject("disposed", out error);
                if (lastProtected == Int64.MaxValue) return Reject("send_sequence_exhausted", out error);
                try
                {
                    if (!Validate(packet, localRole, task, pose, out error)) return false;
                    if (packet.Sequence <= lastProtected || packet.SentLocalSeconds < lastSent) return Reject("send_sequence_or_clock", out error);
                    if (pose != null && (packet.Sample.Sequence <= lastSentSample || pose.HeadObservedOperatorSeconds <= lastSentHead ||
                        pose.EyeObservedOperatorSeconds <= lastSentEye)) return Reject("repeated_pose_observation", out error);
                    byte[] taskState = packet.Kind == WireKind.Task ? TaskState(task) : null;
                    if (taskState != null && (task.Revision < lastSentTaskRevision ||
                        (task.Revision == lastSentTaskRevision && !Equal(taskState, lastSentTask)))) return Reject("task_revision", out error);
                    byte[] payload = WireCodec.Encode(packet), taskBytes = task == null ? new byte[0] : task.Encode(context.Participant);
                    WirePacket decoded; string decodeError;
                    if (!WireCodec.TryDecode(payload, out decoded, out decodeError) || !Equal(payload, WireCodec.Encode(decoded))) return Reject("payload_not_canonical", out error);
                    int observationLength = pose == null ? 0 : 24;
                    int contentLength = Header + taskBytes.Length + observationLength + payload.Length;
                    if (contentLength + Tag > MaximumEnvelopeBytes) return Reject("envelope_size", out error);
                    var result = new byte[contentLength + Tag]; Buffer.BlockCopy(Magic, 0, result, 0, Magic.Length);
                    result[8] = (byte)localRole; result[9] = (byte)remoteRole; Buffer.BlockCopy(context.CopyDigest(), 0, result, 10, 32);
                    U16(result, 42, taskBytes.Length); result[44] = (byte)observationLength; U16(result, 45, payload.Length);
                    Buffer.BlockCopy(taskBytes, 0, result, Header, taskBytes.Length);
                    int offset = Header + taskBytes.Length;
                    if (pose != null) { WriteDouble(result, offset, pose.TaskReceivedOperatorSeconds); WriteDouble(result, offset + 8, pose.HeadObservedOperatorSeconds); WriteDouble(result, offset + 16, pose.EyeObservedOperatorSeconds); }
                    Buffer.BlockCopy(payload, 0, result, offset + observationLength, payload.Length);
                    byte[] tag = Mac(result, contentLength); Buffer.BlockCopy(tag, 0, result, contentLength, Tag);
                    lastProtected = packet.Sequence; lastSent = packet.SentLocalSeconds;
                    if (taskState != null) { lastSentTaskRevision = task.Revision; lastSentTask = taskState; }
                    if (pose != null) { lastSentSample = packet.Sample.Sequence; lastSentHead = pose.HeadObservedOperatorSeconds; lastSentEye = pose.EyeObservedOperatorSeconds; }
                    bytes = result; return true;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException || ex is CryptographicException)
                { return Reject("protect_failed", out error); }
            }
        }

        // expectedPoseTask must come from the caller's authoritative host; this
        // primitive only compares it, and does not establish its authority.
        // originalLocalReceipt/nowLocal are declared readings, not socket proof.
        // Invalid caller clocks reject only that call, without advancing state.
        // A future actual-clock adapter MUST latch real clock/lifecycle failures
        // and require a fresh epoch. Local queue-age and monotonic assertions do
        // NOT prove remote capture freshness; authenticated time messages do not
        // correlate exchanges or establish a usable clock mapping by themselves.
        public bool TryReceive(byte[] input, double originalLocalReceipt, double nowLocal, WozLanTaskContext expectedPoseTask,
            out VerifiedMessage message, out string error)
        {
            lock (gate)
            {
                message = null; error = null;
                if (disposed) return Reject("disposed", out error);
                if (lastReceived == Int64.MaxValue) return Reject("receive_sequence_exhausted", out error);
                if (input == null || input.Length < Header + Tag + 1 || input.Length > MaximumEnvelopeBytes) return Reject("envelope_size", out error);
                // Ownership prevents caller buffer mutation between MAC and parse.
                byte[] bytes = (byte[])input.Clone(); int contentLength = bytes.Length - Tag;
                try
                {
                    byte[] expectedTag = Mac(bytes, contentLength);
                    int tagDifference = 0;
                    for (int i = 0; i < Tag; i++) tagDifference |= expectedTag[i] ^ bytes[contentLength + i];
                    if (tagDifference != 0) return Reject("mac_mismatch", out error);
                    if (!Equal(Slice(bytes, 0, 8), Magic) || bytes[8] != (byte)remoteRole || bytes[9] != (byte)localRole ||
                        !Equal(Slice(bytes, 10, 32), context.CopyDigest())) return Reject("envelope_context", out error);
                    int taskLength = U16(bytes, 42), observationLength = bytes[44], payloadLength = U16(bytes, 45);
                    if (taskLength > WireCodec.MaxPacketBytes + 8 || (taskLength != 0 && taskLength < 9) ||
                        (observationLength != 0 && observationLength != 24) || payloadLength < 1 || payloadLength > WireCodec.MaxPacketBytes ||
                        Header + taskLength + observationLength + payloadLength != contentLength) return Reject("envelope_lengths", out error);
                    if (!Time(originalLocalReceipt) || !Time(nowLocal) || originalLocalReceipt > nowLocal ||
                        originalLocalReceipt < lastReceipt || nowLocal < lastNow || nowLocal - originalLocalReceipt > maximumReceiptAge)
                        return Reject("declared_receipt_clock", out error);
                    WirePacket packet; string decodeError; int offset = Header + taskLength;
                    byte[] payload = Slice(bytes, offset + observationLength, payloadLength);
                    if (!WireCodec.TryDecode(payload, out packet, out decodeError) || !Equal(payload, WireCodec.Encode(packet))) return Reject("payload_not_canonical", out error);
                    WozLanTaskContext task = null;
                    if (taskLength != 0)
                    {
                        WirePacket taskPacket; byte[] taskPayload = Slice(bytes, Header + 8, taskLength - 8);
                        if (!WireCodec.TryDecode(taskPayload, out taskPacket, out decodeError) || taskPacket.Kind != WireKind.Task ||
                            !SamePeer(taskPacket.Peer, context.Participant) || !Equal(taskPayload, WireCodec.Encode(taskPacket))) return Reject("task_not_canonical", out error);
                        task = new WozLanTaskContext(taskPacket.Snapshot, ReadLong(bytes, Header), taskPacket.Sequence, taskPacket.SentLocalSeconds);
                    }
                    WozLanPoseObservation pose = observationLength == 0 ? null : new WozLanPoseObservation(ReadDouble(bytes, offset), ReadDouble(bytes, offset + 8), ReadDouble(bytes, offset + 16));
                    if (!Validate(packet, remoteRole, task, pose, out error)) return false;
                    if (packet.Kind == WireKind.HeadEye && (expectedPoseTask == null || !Equal(task.Encode(context.Participant), expectedPoseTask.Encode(context.Participant))))
                        return Reject("unexpected_pose_task", out error);
                    if (packet.Sequence <= lastReceived || packet.SentLocalSeconds < lastRemoteSent) return Reject("replay_or_remote_clock", out error);
                    if (pose != null && (packet.Sample.Sequence <= lastReceivedSample || pose.HeadObservedOperatorSeconds <= lastReceivedHead ||
                        pose.EyeObservedOperatorSeconds <= lastReceivedEye)) return Reject("repeated_pose_observation", out error);
                    byte[] taskState = packet.Kind == WireKind.Task ? TaskState(task) : null;
                    if (taskState != null && (task.Revision < lastReceivedTaskRevision ||
                        (task.Revision == lastReceivedTaskRevision && !Equal(taskState, lastReceivedTask)))) return Reject("task_revision", out error);
                    // No external calls after this commit. Dispose and all operations
                    // serialize on gate; no result can commit once disposed is true.
                    var verified = VerifiedMessage.Create(this, verificationToken, packet, task, pose, context.Peer(localRole).ClockId, originalLocalReceipt);
                    lastReceived = packet.Sequence; lastRemoteSent = packet.SentLocalSeconds; lastReceipt = originalLocalReceipt; lastNow = nowLocal;
                    if (taskState != null) { lastReceivedTaskRevision = task.Revision; lastReceivedTask = taskState; }
                    if (pose != null) { lastReceivedSample = packet.Sample.Sequence; lastReceivedHead = pose.HeadObservedOperatorSeconds; lastReceivedEye = pose.EyeObservedOperatorSeconds; }
                    message = verified; return true;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException || ex is CryptographicException)
                { return Reject("receive_failed", out error); }
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return; disposed = true; Array.Clear(key, 0, key.Length);
                // Only this owned array is cleared. Managed runtime/HMAC copies,
                // original caller arrays and process memory are not guaranteed erased.
            }
        }
    }
}
