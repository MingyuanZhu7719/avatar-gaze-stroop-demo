using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Apps
{
    public enum WozLanUiState { Empty, Ready, Running, Ended, Disposed }
    public enum WozLanUiAction { Clock, SendTask, NextTask, PauseTask, ResumeTask, StopTask, SendPose }

    public sealed class WozLanUiConfiguration
    {
        public PeerRole Role { get; private set; }
        public IPEndPoint Local { get; private set; }
        public IPEndPoint Remote { get; private set; }
        public bool IsLocalOnly { get; private set; }
        public WozLanUiConfiguration(PeerRole role, string localIp, string localPort, string peerIp, string peerPort)
        {
            if (!Enum.IsDefined(typeof(PeerRole), role)) throw new ArgumentException("role");
            Role = role; Local = Parse(localIp, localPort); Remote = Parse(peerIp, peerPort);
            if (Local.Equals(Remote)) throw new ArgumentException("Distinct endpoints required");
            bool localLoopback = Local.Address.Equals(IPAddress.Loopback), remoteLoopback = Remote.Address.Equals(IPAddress.Loopback);
            if (localLoopback != remoteLoopback) throw new ArgumentException("Loopback self-test requires both addresses exactly 127.0.0.1");
            IsLocalOnly = localLoopback;
        }
        static IPEndPoint Parse(string address, string portText)
        {
            // No DNS, interface enumeration, abbreviated IP, global addresses,
            // wildcard, multicast or port-zero assignment in this UI. The sole
            // loopback exception is exact127.0.0.1 at BOTH ends, checked above.
            if (address == null || portText == null) throw new ArgumentException("Exact endpoints required");
            string[] pieces = address.Split('.'); var bytes = new byte[4];
            if (pieces.Length != 4) throw new ArgumentException("Private IPv4 required");
            for (int i = 0; i < 4; i++)
                if (!Byte.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i]) ||
                    pieces[i] != bytes[i].ToString(CultureInfo.InvariantCulture)) throw new ArgumentException("Canonical IPv4 required");
            bool privateIp = bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168);
            bool localTest = address == "127.0.0.1";
            int port;
            if ((!privateIp && !localTest) || bytes[3] == 0 || bytes[3] == 255 ||
                !Int32.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw new ArgumentException("Explicit private IPv4 host and port 1-65535 required");
            return new IPEndPoint(new IPAddress(bytes), port);
        }
    }

    internal interface IWozLanUiSession : IDisposable
    {
        WozDiagnosticState Start();
        WozDiagnosticState State { get; }
        string Tick();
        string Act(WozLanUiAction action);
        string CleanupNote { get; }
        string PrimaryNote { get; }
        bool PeerAuthenticated { get; }
    }

    // Owned by the UI thread. No timer, socket or background task is created
    // here until explicit Start. Each start attempt consumes that pairing.
    public sealed class WozLanDiagnosticController : IDisposable
    {
        const int MaximumAttempts = 64;
        readonly HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        readonly Func<WozLanContext, PeerRole, byte[], IPEndPoint, IPEndPoint, IWozLanUiSession> factory;
        WozLanPairing pairing;
        IWozLanUiSession session;
        bool createdHere;
        bool cleanupUncertain;
        bool localOnly;
        string cleanupMessage = "";
        public WozLanUiState State { get; private set; }
        public string Status { get; private set; }
        public string LastAction { get; private set; }
        public bool CanSendSynthetic { get { return State == WozLanUiState.Running && session != null && session.PeerAuthenticated; } }
        public WozLanDiagnosticController() : this((context, role, key, local, remote) =>
            new WozLanUiSessionAdapter(context, role, key, local, remote)) { }
        internal WozLanDiagnosticController(Func<WozLanContext, PeerRole, byte[], IPEndPoint, IPEndPoint, IWozLanUiSession> factory)
        { this.factory = factory; State = WozLanUiState.Empty; Status = "尚未启动。没有监听网络。"; }
        void Check()
        {
            if (State == WozLanUiState.Disposed) throw new ObjectDisposedException("WozLanDiagnosticController");
            if (cleanupUncertain) throw new InvalidOperationException("Uncertain closure; exit the application");
        }
        public char[] CreatePairing()
        {
            Check(); Invalidate(); SetPairing(WozLanPairing.CreateFresh()); createdHere = true; return pairing.RevealToken();
        }
        public void ImportPairing(char[] token)
        {
            Check(); Invalidate();
            try { SetPairing(WozLanPairing.Parse(token)); }
            catch { Status = "配对码无效或已经使用。请双方重新生成、导入。"; throw; }
        }
        void SetPairing(WozLanPairing value)
        {
            Check();
            if (pairing != null) { pairing.Dispose(); pairing = null; }
            if (value == null) throw new ArgumentNullException("value");
            if (used.Count >= MaximumAttempts || used.Contains(value.Fingerprint()))
            { value.Dispose(); throw new InvalidOperationException("Fresh pairing required; at most 64 attempts per process"); }
            pairing = value; State = WozLanUiState.Ready;
            Status = "配对码已载入，尚未联网。请确认两端地址、互补角色及同一份程序。";
        }
        public void Start(WozLanUiConfiguration config)
        {
            Check();
            if (State != WozLanUiState.Ready || pairing == null)
                throw new InvalidOperationException("Fresh pairing required");
            byte[] key = null;
            try
            {
                used.Add(pairing.Fingerprint());
                if (config == null) throw new ArgumentException("Configuration required");
                if (createdHere != (config.Role == PeerRole.Participant))
                    throw new InvalidOperationException("Participant must create fresh; Operator must import");
                localOnly = config.IsLocalOnly;
                key = pairing.CopyKey();
                session = factory(pairing.Context, config.Role, key, config.Local, config.Remote);
                if (session.Start() != WozDiagnosticState.Running) throw new InvalidOperationException("Start failed");
                State = WozLanUiState.Running; Status = (localOnly ? "本机自测127.0.0.1，不是两台电脑LAN验证。\r\n" : "实验室LAN诊断。\r\n") + "本地端口已打开；尚不表示对端在线或设备已验证。";
            }
            catch
            {
                string primary = session == null ? "" : session.PrimaryNote;
                Invalidate(); Status = "启动失败，会话已作废。核对地址/端口；不要修改防火墙或安全设置。" + primary + Status;
                throw;
            }
            finally
            {
                if (key != null) Array.Clear(key, 0, key.Length);
                if (pairing != null) { pairing.Dispose(); pairing = null; }
            }
        }
        public void Tick()
        {
            if (State != WozLanUiState.Running || session == null) return;
            try
            {
                string status = session.Tick();
                if (session.State != WozDiagnosticState.Running)
                { Invalidate(); Status = "诊断已终止，必须重新配对。\r\n" + status + "\r\n" + Status; }
                else Status = (localOnly ? "本机自测127.0.0.1，不是两台电脑LAN验证。\r\n" : "实验室LAN诊断。\r\n") + status;
            }
            catch { Invalidate(); Status = "诊断异常，会话已作废。" + Status; throw; }
        }
        public void Act(WozLanUiAction action)
        {
            Check();
            if (State != WozLanUiState.Running || session == null) throw new InvalidOperationException("Not running");
            try { LastAction = session.Act(action); Tick(); }
            catch { Invalidate(); Status = "操作异常，会话已作废。" + Status; throw; }
        }
        public void Invalidate()
        {
            if (State == WozLanUiState.Disposed) return;
            if (State == WozLanUiState.Ended && pairing == null && session == null) return;
            string cleanup = "";
            if (session != null)
            {
                try { session.Dispose(); cleanup = session.CleanupNote; }
                catch { cleanup = "关闭遇到异常；不能确认端口已释放，请退出此程序。"; }
                finally { session = null; }
            }
            if (pairing != null) { used.Add(pairing.Fingerprint()); pairing.Dispose(); pairing = null; }
            createdHere = false;
            if (!String.IsNullOrEmpty(cleanup)) { cleanupUncertain = true; cleanupMessage = cleanup; }
            State = WozLanUiState.Ended; LastAction = null;
            Status = "本次会话已作废，无自动重连。重新开始需双方使用全新配对码。" + cleanupMessage;
        }
        public void Dispose() { if (State == WozLanUiState.Disposed) return; Invalidate(); State = WozLanUiState.Disposed; }
    }

    internal sealed class WozLanUiSessionAdapter : IWozLanUiSession
    {
        readonly WozSyntheticLanDiagnosticSession value;
        public WozLanUiSessionAdapter(WozLanContext context, PeerRole role, byte[] key, IPEndPoint local, IPEndPoint remote)
        { value = new WozSyntheticLanDiagnosticSession(context, role, key, local, remote); }
        public WozDiagnosticState Start() { return value.StartDiagnostic(); }
        public WozDiagnosticState State { get { return value.Snapshot.State; } }
        public bool PeerAuthenticated { get { return value.Snapshot.PeerAuthenticated; } }
        static string Failure(WozDiagnosticFailure failure)
        { return failure == null ? "" : "\r\n错误: " + failure.Operation + " / " + failure.ExceptionType + " / " + failure.SocketErrorCode; }
        public string CleanupNote { get { return value.Snapshot.CleanupFailure == null ? "" : "\r\n不能确认端口已释放，请退出程序。" + Failure(value.Snapshot.CleanupFailure); } }
        public string PrimaryNote { get { return "\r\n" + value.Snapshot.Detail + Failure(value.Snapshot.PrimaryFailure); } }
        public string Tick()
        {
            var s = value.Tick(); var t = s.TaskContext == null ? null : s.TaskContext.Task;
            return "SIMULATOR ONLY / 仅合成诊断，无真实数据、录制或硬件\r\n" +
                "状态: " + s.State + " | 来源: " + s.Origin + "\r\n" +
                "新鲜挑战已验证: " + s.PeerAuthenticated + " | " + s.PairingStatus + "\r\n" +
                "已验证接收: " + s.AcceptedMessages + " | 拒绝: " + s.RejectedMessages + " | 发送: " + s.SentMessages + "\r\n" +
                "进程往返: " + (s.RoundTripProcessSeconds.HasValue ? (s.RoundTripProcessSeconds.Value * 1000).ToString("F2", CultureInfo.InvariantCulture) + " ms" : "尚无") + "（非单向时延/就绪证明）\r\n" +
                "任务: " + (t == null ? "尚无" : t.Phase + " / " + t.Condition + " / trial=" + t.TrialId + " / rev=" + s.TaskContext.Revision) + "\r\n" +
                "任务新鲜: " + s.TaskFresh + " | 合成姿态可见: " + s.PoseVisible + "\r\n" +
                "姿态: " + (s.RemotePose == null ? "已隐藏/尚无" : "seq=" + s.RemotePose.Sequence +
                    " | head=(" + Number(s.RemotePose.RawHeadRotation.X) + "," + Number(s.RemotePose.RawHeadRotation.Y) + "," + Number(s.RemotePose.RawHeadRotation.Z) + "," + Number(s.RemotePose.RawHeadRotation.W) + ")" +
                    " | eye=(" + Number(s.RemotePose.RawEyeDirection.X) + "," + Number(s.RemotePose.RawEyeDirection.Y) + "," + Number(s.RemotePose.RawEyeDirection.Z) + ")") + "\r\n" +
                "详情: " + s.Detail + Failure(s.PrimaryFailure) + Failure(s.CleanupFailure);
        }
        static string Number(double number) { return number.ToString("F2", CultureInfo.InvariantCulture); }
        public string Act(WozLanUiAction action)
        {
            WozDiagnosticSendResult result;
            switch (action)
            {
                case WozLanUiAction.Clock: result = value.RequestClockDiagnostic(); break;
                case WozLanUiAction.SendTask: result = value.SendSyntheticTask(); break;
                case WozLanUiAction.NextTask: result = value.NextSyntheticTask(); break;
                case WozLanUiAction.PauseTask: result = value.PauseSyntheticTask(); break;
                case WozLanUiAction.ResumeTask: result = value.ResumeSyntheticTask(); break;
                case WozLanUiAction.StopTask: result = value.StopSyntheticTask(); break;
                case WozLanUiAction.SendPose: result = value.SendSyntheticPose(); break;
                default: throw new ArgumentOutOfRangeException("action");
            }
            return result.Status + " / " + result.Detail + "（发送成功仅指本机套接字接受）";
        }
        public void Dispose() { value.Dispose(); }
    }
}
