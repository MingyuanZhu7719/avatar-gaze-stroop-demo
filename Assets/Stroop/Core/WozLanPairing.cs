using System;
using System.Security.Cryptography;

namespace AvatarStroop.Core
{
    // One deliberately shared, memory-only laboratory capability. This is not
    // encryption, physical-device identity, a pairing handshake, or freshness
    // proof. Imported tokens cannot be recognized as old after process restart.
    public sealed class WozLanPairing : IDisposable
    {
        public const string TokenPrefix = "WOZLAB1.";
        public const int TokenLength = 72;
        byte[] key, epoch;
        bool disposed;
        public WozLanContext Context { get; private set; }

        WozLanPairing(byte[] key, byte[] epoch)
        {
            this.key = (byte[])key.Clone(); this.epoch = (byte[])epoch.Clone();
            string id = BitConverter.ToString(epoch).Replace("-", "").ToLowerInvariant();
            Context = new WozLanContext(
                new PeerIdentity("synthetic/" + id, "synthetic-task", PeerRole.Participant,
                    "simulator/task", ControlSource.Simulator, "task-clock/" + id),
                new PeerIdentity("synthetic/" + id, "synthetic-pose", PeerRole.Operator,
                    "simulator/pose", ControlSource.Simulator, "pose-clock/" + id),
                epoch, "synthetic-msclr4-r-v1", "synthetic-device");
        }

        public static WozLanPairing CreateFresh()
        {
            using (var random = new RNGCryptoServiceProvider()) return Create(random.GetBytes);
        }
        // Fixed synthetic entropy is used ONLY by the explicitly selected tests.
        internal static WozLanPairing Create(Action<byte[]> fill)
        {
            if (fill == null) throw new ArgumentNullException("fill");
            var key = new byte[32]; var epoch = new byte[16];
            try { fill(key); fill(epoch); return new WozLanPairing(key, epoch); }
            finally { Array.Clear(key, 0, key.Length); Array.Clear(epoch, 0, epoch.Length); }
        }
        public static WozLanPairing Parse(char[] token)
        {
            if (token == null || token.Length != TokenLength) throw new ArgumentException("Invalid pairing token");
            for (int i = 0; i < TokenPrefix.Length; i++)
                if (token[i] != TokenPrefix[i]) throw new ArgumentException("Invalid pairing version");
            for (int i = TokenPrefix.Length; i < token.Length; i++)
            {
                char c = token[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '+' || c == '/'))
                    throw new ArgumentException("Invalid pairing encoding");
            }
            byte[] bytes = null, key = new byte[32], epoch = new byte[16];
            try
            {
                bytes = Convert.FromBase64CharArray(token, TokenPrefix.Length, 64);
                if (bytes.Length != 48) throw new ArgumentException("Invalid pairing payload");
                Buffer.BlockCopy(bytes, 0, key, 0, 32); Buffer.BlockCopy(bytes, 32, epoch, 0, 16);
                return new WozLanPairing(key, epoch);
            }
            finally
            {
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                Array.Clear(key, 0, key.Length); Array.Clear(epoch, 0, epoch.Length);
            }
        }
        public char[] RevealToken()
        {
            Check(); var bytes = new byte[48]; var result = new char[TokenLength];
            try
            {
                Buffer.BlockCopy(key, 0, bytes, 0, 32); Buffer.BlockCopy(epoch, 0, bytes, 32, 16);
                TokenPrefix.CopyTo(0, result, 0, TokenPrefix.Length);
                Convert.ToBase64CharArray(bytes, 0, bytes.Length, result, TokenPrefix.Length);
                return result;
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        internal byte[] CopyKey() { Check(); return (byte[])key.Clone(); }
        internal string Fingerprint()
        {
            Check(); var bytes = new byte[48];
            try
            {
                Buffer.BlockCopy(key, 0, bytes, 0, 32); Buffer.BlockCopy(epoch, 0, bytes, 32, 16);
                using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes));
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        void Check() { if (disposed) throw new ObjectDisposedException("WozLanPairing"); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            Array.Clear(key, 0, key.Length); Array.Clear(epoch, 0, epoch.Length);
            Context = null;
        }
    }
}
