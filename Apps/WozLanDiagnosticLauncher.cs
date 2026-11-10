using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AvatarStroop.Core;

namespace AvatarStroop.Apps
{
    public static class WozLanRuntimeCompatibility
    {
        public static bool IsSupported()
        {
            // The existing signed numeric codec is runtime-dependent. This
            // package supports Microsoft Windows CLR4 only, not Unity Mono or
            // modern .NET. Finite probes catch known R-format family mismatch.
            return Environment.OSVersion.Platform == PlatformID.Win32NT &&
                Environment.Version.Major == 4 && Type.GetType("Mono.Runtime") == null &&
                typeof(object).Assembly.GetName().Name == "mscorlib" &&
                (0.1).ToString("R", CultureInfo.InvariantCulture) == "0.1" &&
                (1E-07).ToString("R", CultureInfo.InvariantCulture) == "1E-07" &&
                Double.Epsilon.ToString("R", CultureInfo.InvariantCulture) == "4.94065645841247E-324";
        }
    }

    // Standalone, no Unity/HMD/SDK/player references or invocation. All UI and
    // state changes run on one WinForms thread, so queued ticks cannot revive
    // stopped state. No socket is constructed when this form is created.
    public sealed class WozLanDiagnosticLauncher : Form
    {
        readonly WozLanDiagnosticController model = new WozLanDiagnosticController();
        readonly ComboBox role = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310 };
        readonly TextBox localIp = Box(160), localPort = Box(80), peerIp = Box(160), peerPort = Box(80);
        readonly TextBox token = new TextBox { Width = 700, MaxLength = WozLanPairing.TokenLength, UseSystemPasswordChar = true, ShortcutsEnabled = true };
        readonly CheckBox reveal = new CheckBox { Text = "显示秘密配对码（避免旁观/截图）", AutoSize = true };
        readonly CheckBox consent = new CheckBox { Text = "仅同实验室LAN或127本机自测；合成数据不加密；不改防火墙/路由器。", AutoSize = true };
        readonly Button create = Button("任务端：生成全新配对码"), import = Button("姿态端：载入配对码");
        readonly Button start = Button("开始网络诊断"), stop = Button("停止并作废本次配对");
        readonly Button clock = Button("检查往返时延"), task = Button("发送当前合成任务"), next = Button("下一合成阶段");
        readonly Button pause = Button("暂停合成任务"), resume = Button("恢复合成任务"), endTask = Button("结束合成任务"), pose = Button("发送一帧合成姿态");
        readonly Label status = Label("尚未启动。没有监听网络。", 250), action = Label("", 55);
        readonly Timer timer = new Timer { Interval = 100 };
        bool changing, closed;
        static TextBox Box(int width) { return new TextBox { Width = width, MaxLength = 15 }; }
        static Button Button(string text) { return new Button { Text = text, AutoSize = true, Height = 34, Margin = new Padding(4) }; }
        static Label Label(string text, int height)
        { return new Label { Text = text, AutoSize = false, Height = height, Dock = DockStyle.Top, Padding = new Padding(4) }; }
        static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 2, 0, 2) };
            row.Controls.AddRange(controls); return row;
        }
        static Label Small(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(5, 8, 5, 0) }; }

        public WozLanDiagnosticLauncher()
        {
            Text = "WoZ 实验室局域网诊断 | SYNTHETIC ONLY";
            Name = "WozLanSyntheticDiagnostic";
            Font = new Font("Microsoft YaHei UI", 10);
            ClientSize = new Size(1000, 910); MinimumSize = new Size(850, 720);
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, AutoScroll = true };
            Controls.Add(layout);
            var title = Label("仅合成数据 · 无真实实验/录制/头显/眼动设备", 42);
            title.Font = new Font(Font.FontFamily, 17, FontStyle.Bold); title.ForeColor = Color.DarkRed; layout.Controls.Add(title);
            layout.Controls.Add(Label("HMAC 验证共享密钥，不验证物理设备。网络内容为明文，可被旁观。\n同一份程序、Microsoft Windows CLR4；不要使用 Mono/.NET 8。仅限已获允许的实验室 LAN。", 63));
            role.Items.AddRange(new object[] { "任务端（模拟 Participant，创建配对）", "姿态端（模拟 Operator，导入配对）" }); role.SelectedIndex = -1;
            layout.Controls.Add(Row(Small("1. 本机角色"), role));
            layout.Controls.Add(Row(Small("2. 本机 IPv4"), localIp, Small("端口"), localPort,
                Small("对端 IPv4"), peerIp, Small("端口"), peerPort));
            layout.Controls.Add(Label("两台电脑须用准确私网地址，相反填写本机/对端；端口1–65535，不扫描网络。\n本机自测可两处都填127.0.0.1并用不同端口；127不是第二台电脑，不能混填LAN地址。\n先填写角色和地址，再生成/导入配对码。修改设置会关闭会话并作废配对。", 79));
            layout.Controls.Add(Row(create, import));
            layout.Controls.Add(Row(token)); layout.Controls.Add(Row(reveal));
            layout.Controls.Add(Label("3. 任务端生成，双方通过可信的当面方式转交；姿态端粘贴并载入。此码是秘密。\n默认隐藏，不自动复制、不保存或记录。可手动选择复制；剪贴板由你管理。每次重启/停止需新码。\n托管内存和系统控件可能残留字符串，无法承诺完全擦除；不要截图或发到公共聊天。", 83));
            layout.Controls.Add(Row(consent)); layout.Controls.Add(Row(start, stop));
            layout.Controls.Add(Label("4. 先在任务端点“开始等待”，再在姿态端点“连接”。等待双方“新鲜挑战已验证=True”。\n只有通过各自随机挑战的往返验证后才开放合成发送；验证过期须手动重新检查往返。", 60));
            layout.Controls.Add(Row(clock, task, next, pause, resume, endTask, pose));
            status.BackColor = Color.FromArgb(236, 243, 249); layout.Controls.Add(status); layout.Controls.Add(action);
            layout.Controls.Add(Label("诊断任务/姿态不接入现有 VIVE 玩家，不改变录制门控。看到有效消息也不代表能开始真实实验。\n关闭窗口会终止监听，没有隐藏后台监听、自动启动或自动重连。若系统提示防火墙授权，请取消并停止。", 65));

            foreach (Control input in new Control[] { localIp, localPort, peerIp, peerPort }) input.TextChanged += ConfigurationChanged;
            role.SelectedIndexChanged += ConfigurationChanged;
            consent.CheckedChanged += (s, e) => { if (!consent.Checked) ConfigurationChanged(s, e); else RefreshState(); };
            token.TextChanged += TokenChanged;
            reveal.CheckedChanged += (s, e) => token.UseSystemPasswordChar = !reveal.Checked;
            create.Click += (s, e) => Guard(() => {
                if (role.SelectedIndex != 0) throw new InvalidOperationException();
                ValidateConfiguration(); ClearSecret(); char[] chars = model.CreatePairing();
                try { changing = true; token.Text = new string(chars); token.ReadOnly = true; }
                finally { Array.Clear(chars, 0, chars.Length); changing = false; }
            });
            import.Click += (s, e) => Guard(() => {
                if (role.SelectedIndex != 1) throw new InvalidOperationException();
                ValidateConfiguration(); char[] chars = token.Text.ToCharArray();
                try { model.ImportPairing(chars); } finally { Array.Clear(chars, 0, chars.Length); ClearSecret(); }
            });
            start.Click += (s, e) => Guard(() => {
                try
                {
                    if (!consent.Checked) throw new InvalidOperationException();
                    model.Start(ValidateConfiguration()); timer.Start();
                }
                catch { model.Invalidate(); throw; }
                finally { ClearSecret(); }
            });
            stop.Click += (s, e) => { timer.Stop(); model.Invalidate(); ClearSecret(); RefreshState(); };
            Wire(clock, WozLanUiAction.Clock); Wire(task, WozLanUiAction.SendTask); Wire(next, WozLanUiAction.NextTask);
            Wire(pause, WozLanUiAction.PauseTask); Wire(resume, WozLanUiAction.ResumeTask); Wire(endTask, WozLanUiAction.StopTask); Wire(pose, WozLanUiAction.SendPose);
            timer.Tick += (s, e) => { if (!closed) Guard(model.Tick); };
            FormClosing += (s, e) => CloseSession(); FormClosed += (s, e) => timer.Dispose();
            RefreshState();
        }
        WozLanUiConfiguration ValidateConfiguration()
        {
            if (role.SelectedIndex < 0) throw new ArgumentException("role");
            return new WozLanUiConfiguration(role.SelectedIndex == 0 ? PeerRole.Participant : PeerRole.Operator,
                localIp.Text, localPort.Text, peerIp.Text, peerPort.Text);
        }
        void ConfigurationChanged(object sender, EventArgs args)
        {
            if (changing || closed) return;
            timer.Stop(); model.Invalidate(); ClearSecret(); RefreshState();
        }
        void TokenChanged(object sender, EventArgs args)
        {
            if (changing || closed) return;
            timer.Stop(); model.Invalidate(); RefreshState();
        }
        void ClearSecret()
        {
            changing = true;
            try { token.Clear(); token.ClearUndo(); token.ReadOnly = false; reveal.Checked = false; token.UseSystemPasswordChar = true; }
            finally { changing = false; }
        }
        void Wire(Button button, WozLanUiAction command) { button.Click += (s, e) => Guard(() => model.Act(command)); }
        void Guard(Action work)
        {
            if (closed) return;
            try { work(); }
            catch (Exception e) when (!(e is OutOfMemoryException || e is StackOverflowException || e is AccessViolationException))
            {
                // Never display an exception message, stack, entered token or key.
                timer.Stop();
                if (model.State == WozLanUiState.Running || model.State == WozLanUiState.Ready) model.Invalidate();
                ClearSecret();
                action.Text = "操作未完成。请检查角色、私网地址、端口、配对码及确认框；失败后需双方重新配对。";
            }
            RefreshState();
        }
        void RefreshState()
        {
            bool running = model.State == WozLanUiState.Running;
            if (!running) timer.Stop();
            start.Enabled = model.State == WozLanUiState.Ready && consent.Checked;
            start.Text = role.SelectedIndex == 0 ? "任务端：开始等待" : role.SelectedIndex == 1 ? "姿态端：连接" : "开始网络诊断";
            create.Enabled = !running && role.SelectedIndex == 0; import.Enabled = !running && role.SelectedIndex == 1;
            token.Enabled = !running; reveal.Enabled = !running;
            clock.Enabled = running;
            foreach (var button in new[] { task, next, pause, resume, endTask }) button.Enabled = model.CanSendSynthetic && role.SelectedIndex == 0;
            pose.Enabled = model.CanSendSynthetic && role.SelectedIndex == 1;
            status.Text = model.Status;
            if (model.LastAction != null) action.Text = model.LastAction;
        }
        void CloseSession()
        {
            if (closed) return; closed = true; timer.Stop(); ClearSecret(); model.Dispose();
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { CloseSession(); timer.Dispose(); } base.Dispose(disposing); }
        [STAThread]
        public static int Main(string[] args)
        {
            // No command-line mode accepts endpoints, secrets or autostart.
            if (args.Length != 0) return 2;
            if (!WozLanRuntimeCompatibility.IsSupported())
            {
                MessageBox.Show("此程序仅支持 Microsoft Windows .NET Framework CLR4。请勿用 Mono、Unity 或 .NET 8 运行。两端必须使用同一份诊断包。", "不支持的运行时");
                return 3;
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new WozLanDiagnosticLauncher()) Application.Run(form);
            return 0;
        }
    }
}
