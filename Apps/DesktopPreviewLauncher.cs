using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Apps
{
    // Separate owner-only desktop application. It never adds owner controls to a participant view.
    public sealed class DesktopPreviewLauncher : Form
    {
        readonly Label state = MakeLabel("\u5c1a\u672a\u542f\u52a8\u3002\u70b9\u51fb\u201c\u6253\u5f00\u684c\u9762\u9884\u89c8\u201d\u4f1a\u6253\u5f00\u53c2\u4e0e\u8005\u548c\u64cd\u4f5c\u8005\u4e24\u4e2a\u7a97\u53e3\u3002", 106);
        readonly Label guidance = MakeLabel("", 65);
        readonly Label receipt = MakeLabel("\u547d\u4ee4\u7ed3\u679c\u4f1a\u5728\u8fd9\u91cc\u663e\u793a\uff1b\u672a\u6536\u5230\u786e\u8ba4\u4e0d\u4f1a\u81ea\u52a8\u91cd\u8bd5\u3002", 64);
        readonly Label folder = MakeLabel("", 48);
        readonly ComboBox source = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Width=330 };
        readonly Button launch = ActionButton("1  \u6253\u5f00\u684c\u9762\u9884\u89c8");
        readonly Button practice = ActionButton("2  \u5f00\u59cb / \u91cd\u8bd5\u7ec3\u4e60");
        readonly Button lockBlock = ActionButton("3  \u9501\u5b9a\u672c\u7ec4\u6765\u6e90");
        readonly Button begin = ActionButton("4  \u786e\u8ba4\u5e76\u5f00\u59cb\u672c\u7ec4");
        readonly Button export = ActionButton("\u5bfc\u51fa\u6a21\u62df\u8bb0\u5f55");
        readonly Button closePlayers = ActionButton("\u5173\u95ed\u4e24\u4e2a\u9884\u89c8\u7a97\u53e3");
        readonly Timer timer = new Timer { Interval=250 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        Process participant, operatorPlayer;
        string session, commands, statusPath, startup, sessionDirectory;
        DesktopPreviewStatus latest;
        OwnerCommand pending;
        long lastSequence=-1;
        double lastStatusAt, sentAt;
        int lastBlock=-99;
        bool released;

        public DesktopPreviewLauncher()
        {
            Name="DesktopPreviewOwner";
            Text="Avatar Stroop \u2014 \u8d1f\u8d23\u4eba\u684c\u9762\u9884\u89c8\u63a7\u5236\u53f0 / SIMULATION";
            Font=new Font("Segoe UI",11);
            AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(920,750); MinimumSize=new Size(850,720);
            StartPosition=FormStartPosition.CenterScreen;
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(24), ColumnCount=1, AutoScroll=true };
            Controls.Add(layout);
            var title=MakeLabel("Avatar Stroop  |  \u684c\u9762\u6a21\u62df\u9884\u89c8",42); title.Font=new Font(Font.FontFamily,20,FontStyle.Bold); layout.Controls.Add(title);
            layout.Controls.Add(MakeLabel("\u4ec5\u4f9b\u5f00\u53d1\u6f14\u793a \u00b7 \u4e0d\u662f VR \u00b7 \u4e0d\u91c7\u96c6\u771f\u5b9e\u773c\u52a8 \u00b7 \u4e0d\u80fd\u7528\u4e8e\u6b63\u5f0f\u5b9e\u9a8c",32));
            layout.Controls.Add(MakeLabel("\u5f53\u524d\u6f14\u793a\u56fa\u5b9a\u5939\u5177\uff1aBCAD \u987a\u5e8f\uff0cB \u4ece\u53c2\u4e0e\u8005\u65b9\u5411\u5f00\u59cb\uff1b\u82f1\u6587\u95ee\u5377\uff0c1 \u79d2\u5012\u8ba1\u65f6\u3002\n\u7ec3\u4e60 8 \u9898\uff1b\u4e3b\u4efb\u52a1 4 \u00d7 24 \u9898\uff1b\u7ec4\u540e\u53e6\u6709\u81f3\u5c11 30 \u79d2\u4f11\u606f\u3002\u5b9e\u9a8c\u65b9\u6848\u53c2\u6570\u672a\u6539\u52a8\u3002",58));
            layout.Controls.Add(Row(launch,closePlayers));
            state.BackColor=Color.FromArgb(232,241,248); state.Padding=new Padding(10); layout.Controls.Add(state);
            layout.Controls.Add(guidance);
            layout.Controls.Add(Row(practice));
            source.Items.AddRange(new object[]{"\u81ea\u52a8\u53c2\u8003\u63a7\u5236\uff08Automatic\uff09","\u72ec\u7acb\u6a21\u62df\u5934\u773c\u4fe1\u53f7\uff08\u4e0d\u662f\u5b9e\u65f6 WoZ\uff09"});
            source.SelectedIndex=-1;
            layout.Controls.Add(MakeLabel("\u6bcf\u7ec4\u524d\u7531\u8d1f\u8d23\u4eba\u660e\u786e\u9009\u62e9\u6765\u6e90\uff1b\u6ca1\u6709\u9ed8\u8ba4\u6a21\u5f0f\u3002\u771f\u5b9e WoZ \u5c1a\u672a\u63a5\u5165\u3002",30));
            layout.Controls.Add(Row(source,lockBlock,begin));
            receipt.ForeColor=Color.FromArgb(33,75,96); layout.Controls.Add(receipt);
            layout.Controls.Add(MakeLabel("\u5728\u53c2\u4e0e\u8005\u7a97\u53e3\u4f5c\u7b54\uff1a1 \u7ea2 / 2 \u7eff / 3 \u84dd / 4 \u9ec4\uff08\u6309\u5b57\u4f53\u989c\u8272\uff09\u3002\n\u7a7a\u683c = \u7ea0\u9519\u540e\u7ee7\u7eed / \u4f11\u606f\u540e Ready\uff1bP = \u6682\u505c\uff0cR = \u6062\u590d\uff0cEsc = \u9000\u51fa\u3002\n\u7ec3\u4e60\u3001\u95ee\u5377\u3001\u4f11\u606f\u548c D \u6761\u4ef6\u9690\u85cf\u5934\u50cf\u662f\u9884\u671f\u884c\u4e3a\u3002\u64cd\u4f5c\u8005\u7a97\u53e3 P = \u5b89\u5168\u505c\u6b62\u3002",78));
            layout.Controls.Add(Row(export)); layout.Controls.Add(folder);
            layout.Controls.Add(MakeLabel("\u9996\u6b21\u542f\u52a8\u53ef\u80fd\u9700\u8981\u6570\u79d2\u3002\u4ec5\u5f53\u72b6\u6001\u6301\u7eed\u66f4\u65b0\u3001\u8bf7\u6c42\u5f97\u5230\u56de\u6267\u65f6\u624d\u786e\u8ba4\u6210\u529f\u3002",28));
            launch.Click+=(s,e)=>Guard(Launch);
            closePlayers.Click+=(s,e)=>Guard(CloseOwnedPlayers);
            practice.Click+=(s,e)=>Send(OwnerCommandKind.Practice);
            lockBlock.Click+=(s,e)=>Send(OwnerCommandKind.Lock);
            begin.Click+=(s,e)=>Send(OwnerCommandKind.Begin);
            export.Click+=(s,e)=>Send(OwnerCommandKind.Export);
            source.SelectedIndexChanged+=(s,e)=>RefreshControls();
            timer.Tick+=(s,e)=>Poll(); timer.Start(); RefreshControls();
            FormClosing+=(s,e)=> { if(Alive(participant)||Alive(operatorPlayer)) { e.Cancel=true; receipt.Text="\u8bf7\u5148\u4f7f\u7528\u201c\u5173\u95ed\u4e24\u4e2a\u9884\u89c8\u7a97\u53e3\u201d\uff0c\u6216\u624b\u52a8\u5173\u95ed\u5b83\u4eec\uff0c\u7136\u540e\u5173\u95ed\u63a7\u5236\u53f0\u3002"; } };
        }
        static Label MakeLabel(string value,int height) { return new Label { Text=value,AutoSize=false,Height=height,Dock=DockStyle.Top,Margin=new Padding(0,3,0,5) }; }
        static Button ActionButton(string value) { return new Button { Text=value,AutoSize=true,Height=38,MinimumSize=new Size(155,38),Margin=new Padding(0,0,12,0) }; }
        static FlowLayoutPanel Row(params Control[] children) { var row=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Margin=new Padding(0,5,0,8) };row.Controls.AddRange(children);return row; }
        static bool Alive(Process process) { try{return process!=null&&!process.HasExited;}catch(InvalidOperationException){return false;} }
        static string Quote(string value) { if(value.IndexOf('"')>=0||value.IndexOf('\r')>=0||value.IndexOf('\n')>=0)throw new ArgumentException("Invalid launch path");return "\""+value+"\""; }
        void Guard(Action action) {try{action();}catch(Exception error){receipt.Text="\u672a\u5b8c\u6210\uff1a"+error.Message;}RefreshControls();}
        void Launch()
        {
            if(Alive(participant)||Alive(operatorPlayer))throw new InvalidOperationException("\u8bf7\u5148\u5173\u95ed\u73b0\u6709\u9884\u89c8\u7a97\u53e3\u3002");
            string root=AppDomain.CurrentDomain.BaseDirectory;
            string p=Path.Combine(root,"Participant","AvatarStroopParticipant.exe"),o=Path.Combine(root,"Operator","AvatarStroopOperator.exe");
            if(!File.Exists(p)||!File.Exists(o))throw new FileNotFoundException("\u64ad\u653e\u5668\u6587\u4ef6\u7f3a\u5931\u3002\u8bf7\u4fdd\u7559\u5b8c\u6574\u5305\u4e2d\u7684 Participant \u548c Operator \u6587\u4ef6\u5939\u3002");
            session="preview-"+Guid.NewGuid().ToString("N"); sessionDirectory=Path.Combine(root,"sessions",session); Directory.CreateDirectory(sessionDirectory);
            commands=Path.Combine(sessionDirectory,"owner.commands");statusPath=Path.Combine(sessionDirectory,"participant.status");startup=Path.Combine(sessionDirectory,"start.signal");
            int pp,op;
            using(var a=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
            using(var b=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
            {pp=((IPEndPoint)a.Client.LocalEndPoint).Port;op=((IPEndPoint)b.Client.LocalEndPoint).Port;}
            string common=" -screen-fullscreen 0 -screen-width 1100 -screen-height 720 --stroop-session "+session+" --stroop-participant-port "+pp+" --stroop-operator-port "+op+" --stroop-startup "+Quote(startup);
            latest=null;pending=null;lastSequence=-1;lastBlock=-99;released=false;source.SelectedIndex=-1;
            operatorPlayer=Start(o,common+" -logFile "+Quote(Path.Combine(sessionDirectory,"operator.log"))+" --stroop-journal "+Quote(Path.Combine(sessionDirectory,"operator.journal")));
            participant=Start(p,common+" -logFile "+Quote(Path.Combine(sessionDirectory,"participant.log"))+" --stroop-journal "+Quote(Path.Combine(sessionDirectory,"participant.journal"))+" --stroop-owner-commands "+Quote(commands)+" --stroop-status "+Quote(statusPath));
            state.Text="\u542f\u52a8\u4e2d\uff1a\u7b49\u5f85\u4e24\u4e2a\u7a97\u53e3\u7ed1\u5b9a\u672c\u6b21\u72ec\u7acb\u9884\u89c8\u4f1a\u8bdd\u3002";
            folder.Text="\u672c\u6b21\u6a21\u62df\u65e5\u5fd7\uff1a"+sessionDirectory;
            receipt.Text="\u542f\u52a8\u8bf7\u6c42\u5df2\u53d1\u9001\uff1b\u7ec3\u4e60\u5c1a\u672a\u5f00\u59cb\uff0c\u63a7\u5236\u6765\u6e90\u5c1a\u672a\u9009\u62e9\u3002";
        }
        static Process Start(string path,string args) { return Process.Start(new ProcessStartInfo(path,args) {UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(path)}); }
        bool Fresh {get{return latest!=null&&released&&Alive(participant)&&Alive(operatorPlayer)&&clock.Elapsed.TotalSeconds-lastStatusAt<3;}}
        void Poll()
        {
            if(session==null)return;
            try
            {
                if(!released&&Alive(participant)&&Alive(operatorPlayer))
                {
                    string p=startup+".Participant.ready",o=startup+".Operator.ready";
                    if(File.Exists(p)&&File.Exists(o)&&File.ReadAllText(p)=="ASPREVIEWREADY|1|"+session+"|Participant"&&File.ReadAllText(o)=="ASPREVIEWREADY|1|"+session+"|Operator")
                    {
                        DesktopPreviewStartup.WriteRelease(startup,session);released=true;
                    }
                }
                var next=DesktopPreviewStatus.Read(statusPath);
                if(next!=null)
                {
                    if(next.SessionId!=session)throw new InvalidDataException("\u72b6\u6001\u5c5e\u4e8e\u5176\u4ed6\u4f1a\u8bdd\uff0c\u62d2\u7edd\u4f7f\u7528\u3002");
                    if(next.Sequence<lastSequence)throw new InvalidDataException("\u72b6\u6001\u5e8f\u5217\u56de\u9000\uff0c\u62d2\u7edd\u4f7f\u7528\u3002");
                    if(next.Sequence>lastSequence){latest=next;lastSequence=next.Sequence;lastStatusAt=clock.Elapsed.TotalSeconds;}
                    if(next.BlockIndex!=lastBlock){lastBlock=next.BlockIndex;source.SelectedIndex=-1;}
                }
                if(pending!=null)
                {
                    var ack=OwnerCommandFile.FindAcknowledgement(commands,pending);
                    if(ack!=null){receipt.Text=(ack.Accepted?"\u5df2\u786e\u8ba4\uff1a":"\u5df2\u62d2\u7edd\uff1a")+pending.Kind+" \u2014 "+ack.Reason;pending=null;}
                    else if(clock.Elapsed.TotalSeconds-sentAt>3)receipt.Text="\u547d\u4ee4\u5df2\u6392\u961f\uff0c\u4ecd\u672a\u786e\u8ba4\u3002\u4e0d\u4f1a\u81ea\u52a8\u91cd\u8bd5\uff1b\u7b49\u5f85\u72b6\u6001\u4e0e\u56de\u6267\u3002";
                }
                if(!Alive(participant)||!Alive(operatorPlayer))state.Text="\u9884\u89c8\u7a97\u53e3\u672a\u8fd0\u884c\u6216\u5df2\u7ecf\u9000\u51fa\u3002\u8bf7\u67e5\u770b\u672c\u6b21\u65e5\u5fd7\uff1b\u5173\u95ed\u5269\u4f59\u7a97\u53e3\u540e\u53ef\u660e\u786e\u542f\u52a8\u65b0\u4f1a\u8bdd\u3002";
                else if(!Fresh)state.Text="\u7b49\u5f85\u9884\u89c8\u72b6\u6001\u66f4\u65b0\u3002\u63a7\u5236\u6682\u4e0d\u53ef\u7528\uff1b\u6ca1\u6709\u786e\u8ba4\u5b9e\u9a8c\u5df2\u5f00\u59cb\u3002";
                else state.Text="\u9636\u6bb5\uff1a"+latest.Phase+"   |   \u7ec4\uff1a"+(latest.BlockIndex+1)+" / 4   |   \u6761\u4ef6\uff1a"+(latest.Condition.HasValue?latest.Condition.ToString():"\u2014")+"\n\u5df2\u9501\u5b9a\u6765\u6e90\uff1a"+(latest.LockedSource.HasValue?latest.LockedSource.ToString():"\u672a\u9009\u62e9")+"   |   \u6765\u6e90\u5c31\u7eea\uff1a"+(latest.Ready?"\u662f":"\u5426")+"\n"+latest.Message;
                guidance.Text=Instruction();
            }
            catch(IOException error){state.Text="\u72b6\u6001\u6682\u4e0d\u53ef\u8bfb\uff1a"+error.Message;latest=null;}
            catch(Exception error){state.Text="\u63a7\u5236\u5df2\u505c\u6b62\uff1a"+error.Message;latest=null;}
            RefreshControls();
        }
        string Instruction()
        {
            if(!Fresh)return "\u8fd9\u662f\u684c\u9762\u6a21\u62df\u3002\u7b49\u5f85\u4e24\u4e2a\u7a97\u53e3\u52a0\u8f7d\u5b8c\u6210\uff1b\u4e0d\u662f VIVE / Quest \u5934\u663e\u7a0b\u5e8f\u3002";
            switch(latest.Phase)
            {
                case Phase.Preparation: case Phase.Familiarization: case Phase.PracticeRetry:return "\u8d1f\u8d23\u4eba\uff1a\u70b9\u51fb\u201c\u5f00\u59cb / \u91cd\u8bd5\u7ec3\u4e60\u201d\u3002\u7136\u540e\u5207\u6362\u5230\u53c2\u4e0e\u8005\u7a97\u53e3\uff0c\u7528\u6570\u5b57\u952e\u6309\u5b57\u4f53\u989c\u8272\u4f5c\u7b54\u3002";
                case Phase.BlockPreparation:return "\u7ec3\u4e60 / \u4e0a\u7ec4\u5df2\u5b8c\u6210\u3002\u6838\u5bf9\u5f53\u524d\u6761\u4ef6\uff0c\u7136\u540e\u660e\u786e\u9009\u62e9\u5e76\u9501\u5b9a\u672c\u7ec4\u63a7\u5236\u6765\u6e90\u3002";
                case Phase.BlockReady:return latest.Ready?"\u6765\u6e90\u5df2\u5c31\u7eea\u3002\u8d1f\u8d23\u4eba\u53ef\u4ee5\u786e\u8ba4\u5f00\u59cb\u672c\u7ec4\u3002":"\u6765\u6e90\u5c1a\u672a\u5c31\u7eea\u3002\u4fdd\u6301\u4e24\u4e2a\u7a97\u53e3\u8fd0\u884c\uff1b\u4e0d\u4f1a\u81ea\u52a8\u6539\u7528\u5176\u4ed6\u6765\u6e90\u3002";
                case Phase.Practice: case Phase.MainTask:return "\u5728\u53c2\u4e0e\u8005\u7a97\u53e3\u6309\u5b57\u4f53\u989c\u8272\u4f5c\u7b54\u3002\u9700\u677e\u5f00\u6309\u952e\u518d\u4f5c\u7b54\u4e0b\u4e00\u9898\u3002";
                case Phase.PracticeCorrection:return "\u5728\u53c2\u4e0e\u8005\u7a97\u53e3\u9605\u8bfb\u7ea0\u9519\u63d0\u793a\uff0c\u518d\u6309\u7a7a\u683c\u7ee7\u7eed\u3002";
                case Phase.Questionnaire:return "\u5728\u53c2\u4e0e\u8005\u7a97\u53e3\u7528 1\u20135 \u5206\u522b\u5b8c\u6210\u4e94\u4e2a\u8bc4\u5206\u9879\u76ee\u3002";
                case Phase.Rest:return "\u53c2\u4e0e\u8005\u4f11\u606f\u81f3\u5c11 30 \u79d2\uff1bReady \u53ef\u7528\u540e\u6309\u7a7a\u683c\u7ee7\u7eed\uff0c\u4e5f\u53ef\u5ef6\u957f\u4f11\u606f\u3002";
                case Phase.Paused:return "\u5df2\u6682\u505c\u3002\u4ec5\u5728\u53c2\u4e0e\u8005\u7a97\u53e3\u4e3b\u52a8\u6309 R \u6062\u590d\uff1b\u8d1f\u8d23\u4eba\u7a97\u53e3\u4e0d\u63d0\u4f9b\u8fdc\u7a0b\u6062\u590d\u3002";
                case Phase.Ended:return "\u672c\u6b21\u6a21\u62df\u5df2\u7ed3\u675f\u3002\u53ef\u5bfc\u51fa\u5b9e\u9645\u8bb0\u5f55\uff0c\u518d\u5173\u95ed\u9884\u89c8\u7a97\u53e3\u3002";
                default:return "\u6309\u53c2\u4e0e\u8005\u7a97\u53e3\u7684\u5f53\u524d\u9636\u6bb5\u8bf4\u660e\u7ee7\u7eed\uff1b\u4e0d\u4f1a\u81ea\u52a8\u4f5c\u7b54\u6216\u8df3\u8fc7\u6d41\u7a0b\u3002";
            }
        }
        void RefreshControls()
        {
            bool free=Fresh&&pending==null;
            launch.Enabled=!Alive(participant)&&!Alive(operatorPlayer);
            closePlayers.Enabled=Alive(participant)||Alive(operatorPlayer);
            practice.Enabled=free&&(latest.Phase==Phase.Preparation||latest.Phase==Phase.Familiarization||latest.Phase==Phase.PracticeRetry);
            source.Enabled=free&&latest.Phase==Phase.BlockPreparation;
            lockBlock.Enabled=source.Enabled&&source.SelectedIndex>=0;
            begin.Enabled=free&&latest.Phase==Phase.BlockReady&&latest.Ready;
            export.Enabled=free;
        }
        void Send(OwnerCommandKind kind)
        {
            Guard(()=>{
                if(!Fresh||pending!=null)throw new InvalidOperationException("\u9700\u8981\u6700\u65b0\u72b6\u6001\uff0c\u4e14\u4e0a\u4e00\u6761\u547d\u4ee4\u5fc5\u987b\u5df2\u6709\u786e\u8ba4\u3002");
                ControlSelection? chosen=null;
                if(kind==OwnerCommandKind.Lock){if(source.SelectedIndex<0)throw new InvalidOperationException("\u8bf7\u660e\u786e\u9009\u62e9\u672c\u7ec4\u6765\u6e90\u3002");chosen=source.SelectedIndex==0?ControlSelection.Automatic:ControlSelection.SimulatorRehearsal;}
                pending=new OwnerCommand(Guid.NewGuid().ToString("N"),kind,chosen);sentAt=clock.Elapsed.TotalSeconds;
                // Preserve this ID even if append throws: the write outcome may be uncertain.
                OwnerCommandFile.Append(commands,pending);receipt.Text="\u5df2\u6392\u961f\uff1a"+kind+"\uff1b\u6b63\u5728\u7b49\u5f85\u53c2\u4e0e\u8005\u5e94\u7528\u786e\u8ba4\u3002";
            });
        }
        void CloseOwnedPlayers()
        {
            // Explicit owner click only; never kill or enumerate unrelated applications.
            if(Alive(participant))participant.CloseMainWindow();
            if(Alive(operatorPlayer))operatorPlayer.CloseMainWindow();
            receipt.Text="\u5df2\u8bf7\u6c42\u6b63\u5e38\u5173\u95ed\u3002\u8bf7\u7b49\u5f85\u7a97\u53e3\u9000\u51fa\uff1b\u6ca1\u6709\u5f3a\u5236\u7ec8\u6b62\u8fdb\u7a0b\u3002";
        }
        [STAThread] public static int Main()
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DesktopPreviewLauncher());return 0;
        }
    }
}
