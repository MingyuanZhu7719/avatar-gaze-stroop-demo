using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Classroom-space head orientation and fused head-local eye direction.
    public struct ParticipantAvatarPose
    {
        public readonly Rotation HeadRotation;
        public readonly Vec3 HeadLocalEyeDirection;
        public ParticipantAvatarPose(Rotation headRotation, Vec3 headLocalEyeDirection)
        {
            HeadRotation = headRotation;
            HeadLocalEyeDirection = headLocalEyeDirection;
        }
        internal bool Valid { get { return HeadRotation.IsUnit && HeadLocalEyeDirection.IsUnit; } }
    }

    // A lossy participant-to-operator display stream on the paired socket. Each
    // message authenticates an exact task declaration and a lane-local sequence.
    // It cannot grant tracking authority, change the task or acknowledge a stop.
    internal sealed class ParticipantAvatarDisplayLane : IDisposable
    {
        internal const double MaximumSampleAge = .25;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("WOZAVT01");
        const int DigestLength = 32;
        const int ClearBytes = 8 + DigestLength + 3 * sizeof(long) + 1 + DigestLength;
        const int PoseBytes = ClearBytes + 7 * sizeof(double);
        readonly HMACSHA256 mac;
        readonly WozLanContext context;
        readonly PeerRole role;
        WozLanTaskContext lastTask;
        byte[] taskDigest;
        long sent, received;

        internal ParticipantAvatarDisplayLane(WozLanContext context, PeerRole role, byte[] sessionKey)
        {
            this.context = context;
            this.role = role;
            using (var derive = new HMACSHA256(sessionKey))
            {
                string domain = "WOZAVT01/PARTICIPANT_TO_OPERATOR/" + Convert.ToBase64String(context.CopyDigest());
                byte[] key = derive.ComputeHash(Encoding.ASCII.GetBytes(domain));
                try { mac = new HMACSHA256(key); }
                finally { Array.Clear(key, 0, key.Length); }
            }
        }
        static bool Match(byte[] packet, int offset, byte[] expected)
        {
            if (offset < 0 || packet.Length - offset < expected.Length) return false;
            int mismatch = 0;
            for (int i = 0; i < expected.Length; i++) mismatch |= packet[offset + i] ^ expected[i];
            return mismatch == 0;
        }
        internal static bool IsDisplayMessage(byte[] packet) { return packet != null && Match(packet, 0, Magic); }

        byte[] TaskDigest(WozLanTaskContext task)
        {
            if (!Object.ReferenceEquals(lastTask, task))
            {
                using (var sha = SHA256.Create()) taskDigest = sha.ComputeHash(task.Encode(context.Participant));
                lastTask = task;
            }
            return taskDigest;
        }
        static bool CurrentSample(WozLanTaskContext task, double observed, double sentTime)
        {
            return task != null && task.Task.Phase == Phase.MainTask &&
                WozAuthenticatedLanEndpoint.Time(observed) && WozAuthenticatedLanEndpoint.Time(sentTime) &&
                observed >= task.ParticipantSentSeconds && observed <= sentTime && sentTime - observed <= MaximumSampleAge;
        }

        internal byte[] Protect(WozLanTaskContext task, ParticipantAvatarPose? pose, double observed, double now)
        {
            if (role != PeerRole.Participant || sent == Int64.MaxValue || !CurrentSample(task, observed, now) ||
                pose.HasValue && !pose.Value.Valid) return null;
            var bytes = new byte[pose.HasValue ? PoseBytes : ClearBytes];
            using (var stream = new MemoryStream(bytes))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(Magic); w.Write(TaskDigest(task)); w.Write(++sent);
                w.Write(observed); w.Write(now); w.Write(pose.HasValue);
                if (pose.HasValue)
                {
                    Rotation head = pose.Value.HeadRotation; Vec3 eye = pose.Value.HeadLocalEyeDirection;
                    w.Write(head.X); w.Write(head.Y); w.Write(head.Z); w.Write(head.W);
                    w.Write(eye.X); w.Write(eye.Y); w.Write(eye.Z);
                }
                int end = (int)stream.Position;
                w.Write(mac.ComputeHash(bytes, 0, end));
            }
            return bytes;
        }

        internal sealed class Update
        {
            internal readonly WozLanTaskContext Task;
            internal readonly ParticipantAvatarPose? Pose;
            internal readonly double Observed, Sent;
            internal Update(WozLanTaskContext task,ParticipantAvatarPose? pose, double observed, double sentTime)
            { Task=task;Pose = pose; Observed = observed; Sent = sentTime; }
        }
        internal Update Receive(byte[] bytes, WozLanTaskContext task)
        {
            if (role != PeerRole.Operator || task == null || task.Task.Phase != Phase.MainTask || bytes == null ||
                (bytes.Length != ClearBytes && bytes.Length != PoseBytes) || !IsDisplayMessage(bytes)) return null;
            int end = bytes.Length - DigestLength;
            if (!Match(bytes, end, mac.ComputeHash(bytes, 0, end)) || !Match(bytes, Magic.Length, TaskDigest(task))) return null;
            using (var stream = new MemoryStream(bytes, false))
            using (var r = new BinaryReader(stream))
            {
                stream.Position = Magic.Length + DigestLength;
                long sequence = r.ReadInt64(); double observed = r.ReadDouble(); double sentTime = r.ReadDouble();
                byte present = r.ReadByte();
                if (sequence <= received || !CurrentSample(task, observed, sentTime) ||
                    present != (bytes.Length == PoseBytes ? 1 : 0)) return null;
                ParticipantAvatarPose? pose = null;
                if (present == 1)
                {
                    var head = new Rotation(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
                    var eye = new Vec3(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
                    pose = new ParticipantAvatarPose(head, eye);
                    if (!pose.Value.Valid) return null;
                }
                received = sequence;
                return new Update(task,pose, observed, sentTime);
            }
        }
        public void Dispose() { mac.Dispose(); }
    }
}
