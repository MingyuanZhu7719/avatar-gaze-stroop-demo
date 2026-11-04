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
        readonly Label state = MakeLabel("Not started. Open desktop preview launches the participant and operator windows.", 106);
        readonly Label guidance = MakeLabel("", 65);
        readonly Label receipt = MakeLabel("Command results appear here. Unacknowledged commands are never retried automatically.", 64);
        readonly Label folder = MakeLabel("", 64);
        readonly Label exportState = MakeLabel("CSV export: no verified export for this session.", 64);
        readonly TextBox exportedFolder = new TextBox { ReadOnly=true,Multiline=true,WordWrap=false,ScrollBars=ScrollBars.Horizontal,Dock=DockStyle.Top,Height=48 };
        readonly ComboBox source = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Width=330 };
        readonly Button launch = ActionButton("1  Open desktop preview");
        readonly Button practice = ActionButton("2  Start / retry practice");
        readonly Button lockBlock = ActionButton("3  Confirm session source");
        readonly Button begin = ActionButton("4  Start first block");
        readonly Button export = ActionButton("Export simulation records");
        readonly Button openExport = ActionButton("Open exported data");
        readonly Button closePlayers = ActionButton("Close both preview windows");
        readonly Button reconnect = ActionButton("End preview and reconnect / new session");
        readonly Timer timer = new Timer { Interval=250 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        Process participant, operatorPlayer;
        string session, commands, statusPath, startup, sessionDirectory;
        DesktopPreviewStatus latest;
        OwnerCommand pending;
        OwnerCommand restartCommand;
        PreviewRestartWorkflow restart;
        string restartSession,restartDirectory;
        PreviewExportReceipt verifiedExport;
        long lastSequence=-1;
        double lastStatusAt, sentAt, launchedAt;
        string portNote="";
        bool released,launchOutcomeUncertain;

        public DesktopPreviewLauncher()
        {
            AppLanguage.UseEnglishResources();
            Name="DesktopPreviewOwner";
            Text="Avatar Stroop - Desktop preview owner controls / SIMULATION";
#if QUEST_PREVIEW_LAUNCHER
            Text="Avatar Stroop - Quest Link preview owner controls / SIMULATED GAZE";
            launch.Text="1  Open Quest Link preview";
            state.Text="First connect Quest 3 through the existing Meta Quest Link app. Open preview only after Link is ready. The participant uses the headset; the operator stays on the desktop.";
#endif
            Font=new Font("Segoe UI",11);
            AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(920,750); MinimumSize=new Size(850,720);
            StartPosition=FormStartPosition.CenterScreen;
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(24), ColumnCount=1, AutoScroll=true };
            Controls.Add(layout);
            var title=MakeLabel("Avatar Stroop | Desktop preview",42); title.Font=new Font(Font.FontFamily,20,FontStyle.Bold); layout.Controls.Add(title);
#if QUEST_PREVIEW_LAUNCHER
            title.Text="Avatar Stroop | Quest Link preview";
            layout.Controls.Add(MakeLabel("HEAD VIEW ONLY | SIMULATED AVATAR GAZE | No eye tracking | Not a study run",32));
#else
            layout.Controls.Add(MakeLabel("DEVELOPMENT ONLY | Desktop simulation | No eye tracking | Not a study run",32));
#endif
            layout.Controls.Add(MakeLabel("Fixed preview fixture: BCAD order; B starts toward the participant. This is not a fresh participant allocation.\nEnglish questionnaire; 3-second countdown; 8 practice trials; 4 blocks of 24 main trials.\nRest after blocks 1-3 has no minimum; Continue starts the next countdown when the source is ready.",86));
            layout.Controls.Add(Row(launch,closePlayers));
            layout.Controls.Add(Row(reconnect));
            state.BackColor=Color.FromArgb(232,241,248); state.Padding=new Padding(10); layout.Controls.Add(state);
            layout.Controls.Add(guidance);
            layout.Controls.Add(Row(practice));
            source.Items.AddRange(new object[]{"Automatic reference control","Simulated head/eye input (not live WoZ)"});
            source.SelectedIndex=-1;
            layout.Controls.Add(MakeLabel("Confirm a gaze-control source once after practice; it stays selected for all 4 blocks. No default selection.\nThe source controls gaze input; the A-D avatar conditions still change in BCAD order. D has no avatar with either source. Live WoZ is not connected.",70));
            layout.Controls.Add(Row(source,lockBlock,begin));
            receipt.ForeColor=Color.FromArgb(33,75,96); layout.Controls.Add(receipt);
#if QUEST_PREVIEW_LAUNCHER
            layout.Controls.Add(MakeLabel("Touch: X red / Y green / A blue / B yellow. Release before the next answer.\nRatings: read the introduction, then A. Stick UP lowers / DOWN raises 1-5; centre, then A confirms.\nA continues after rest immediately; right trigger continues after correction. Left menu pauses.\nPaused: trigger resumes; B withdraws. PC P / Esc: safety controls.",100));
#else
            layout.Controls.Add(MakeLabel("Participant keys: 1 red / 2 green / 3 blue / 4 yellow. Answer the ink colour.\nSpace: Continue after correction, rating introduction or rest. Rest has no minimum.\nP: pause. R: resume. Esc: withdraw. Avatar hidden during practice, ratings, rest and D.\nOperator window: P requests a safety stop.",100));
#endif
            layout.Controls.Add(Row(export,openExport));layout.Controls.Add(exportState);
            layout.Controls.Add(MakeLabel("Last verified CSV folder:",28));layout.Controls.Add(exportedFolder);layout.Controls.Add(folder);
            layout.Controls.Add(MakeLabel("Startup may take several seconds. Success requires current status updates and an acknowledgement for each request.",44));
            launch.Click+=(s,e)=>Guard(Launch);
            closePlayers.Click+=(s,e)=>Guard(CloseOwnedPlayers);
            reconnect.Click+=(s,e)=>Guard(BeginRestart);
            practice.Click+=(s,e)=>Send(OwnerCommandKind.Practice);
            lockBlock.Click+=(s,e)=>Send(OwnerCommandKind.Lock);
            begin.Click+=(s,e)=>Send(OwnerCommandKind.Begin);
            export.Click+=(s,e)=>Send(OwnerCommandKind.Export);
            openExport.Click+=(s,e)=>Guard(OpenExport);
            source.SelectedIndexChanged+=(s,e)=>RefreshControls();
            timer.Tick+=(s,e)=>Poll(); timer.Start(); RefreshControls();
            FormClosing+=(s,e)=> {
                if(RestartBlocksActions&&restart.Stage!=PreviewRestartStage.Blocked)
                {e.Cancel=true;receipt.Text="Reconnect is pending. Wait for its result; closing this console cannot confirm the old session ended.";return;}
                if(Alive(participant)||Alive(operatorPlayer)) { e.Cancel=true; receipt.Text="Close both preview windows, using the button or their own close controls, before closing this console."; }
            };
        }
        static Label MakeLabel(string value,int height) { return new Label { Text=value,AutoSize=false,Height=height,Dock=DockStyle.Top,Margin=new Padding(0,3,0,5) }; }
        static Button ActionButton(string value) { return new Button { Text=value,AutoSize=true,Height=38,MinimumSize=new Size(155,38),Margin=new Padding(0,0,12,0) }; }
        static FlowLayoutPanel Row(params Control[] children) { var row=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Margin=new Padding(0,5,0,8) };row.Controls.AddRange(children);return row; }
        static bool Alive(Process process) {bool exited;return ObserveExit(process,out exited)&&!exited;}
        static bool ObserveExit(Process process,out bool exited)
        {
            exited=false;if(process==null)return false;
            try{exited=process.HasExited;return true;}
            catch(InvalidOperationException){return false;}
            catch(System.ComponentModel.Win32Exception){return false;}
        }
        static bool CanReplace(Process process){bool exited;return process==null||(ObserveExit(process,out exited)&&exited);}
        bool RestartBlocksActions {get{return restart!=null&&restart.BlocksActions;}}
        static string Quote(string value) { if(value.IndexOf('"')>=0||value.IndexOf('\r')>=0||value.IndexOf('\n')>=0)throw new ArgumentException("Invalid launch path");return "\""+value+"\""; }
        void Guard(Action action) {try{action();}catch(Exception error){receipt.Text="Not completed: "+error.Message;}RefreshControls();}
        void Launch()
        {
            if(RestartBlocksActions)throw new InvalidOperationException("Reconnect must finish or its old records must be reviewed; a new launch cannot bypass it.");
            LaunchCore();
        }
        void LaunchCore()
        {
            if(launchOutcomeUncertain)throw new InvalidOperationException("A prior launch returned no process handle. Review its outcome before using a new console; this console cannot retry it.");
            if(!CanReplace(participant)||!CanReplace(operatorPlayer))throw new InvalidOperationException("Existing preview process exit is not confirmed. Close the owned windows first.");
            string root=AppDomain.CurrentDomain.BaseDirectory;
            string p=Path.Combine(root,"Participant","AvatarStroopParticipant.exe"),o=Path.Combine(root,"Operator","AvatarStroopOperator.exe");
            if(!File.Exists(p)||!File.Exists(o))throw new FileNotFoundException("Player files are missing. Keep the complete Participant and Operator folders from the preview package.");
#if QUEST_PREVIEW_LAUNCHER
            session="quest-preview-"+Guid.NewGuid().ToString("N");
#else
            session="preview-"+Guid.NewGuid().ToString("N");
#endif
            sessionDirectory=Path.Combine(root,"sessions",session); Directory.CreateDirectory(sessionDirectory);
            commands=Path.Combine(sessionDirectory,"owner.commands");statusPath=Path.Combine(sessionDirectory,"participant.status");startup=Path.Combine(sessionDirectory,"start.signal");
            int pp,op;
            using(var a=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
            using(var b=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
            {pp=((IPEndPoint)a.Client.LocalEndPoint).Port;op=((IPEndPoint)b.Client.LocalEndPoint).Port;}
            string common=" -screen-fullscreen 0 -screen-width 1100 -screen-height 720 --stroop-session "+session+" --stroop-participant-port "+pp+" --stroop-operator-port "+op+" --stroop-startup "+Quote(startup);
            latest=null;pending=null;lastSequence=-1;released=false;source.SelectedIndex=-1;
            ResetExportPresentation();
            portNote="participant="+pp+", operator="+op;launchedAt=clock.Elapsed.TotalSeconds;
            operatorPlayer=Start(o,common+" -logFile "+Quote(Path.Combine(sessionDirectory,"operator.log"))+" --stroop-journal "+Quote(Path.Combine(sessionDirectory,"operator.journal")));
            participant=Start(p,common+" -logFile "+Quote(Path.Combine(sessionDirectory,"participant.log"))+" --stroop-journal "+Quote(Path.Combine(sessionDirectory,"participant.journal"))+" --stroop-owner-commands "+Quote(commands)+" --stroop-status "+Quote(statusPath));
            state.Text="Starting: waiting for both windows to join this separate preview session.";
            folder.Text="Raw session records: "+sessionDirectory;
            receipt.Text="Launch requested. Practice has not started and no control source is selected.";
        }
        Process Start(string path,string args)
        {
            var process=Process.Start(new ProcessStartInfo(path,args) {UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(path)});
            if(process==null)
            {
                launchOutcomeUncertain=true;
                throw new InvalidOperationException("Preview launch returned no process handle; its outcome cannot be confirmed and this console will not retry it.");
            }
            return process;
        }
        bool ParticipantFresh {get{return latest!=null&&released&&Alive(participant)&&clock.Elapsed.TotalSeconds-lastStatusAt<3;}}
        bool Fresh {get{return ParticipantFresh&&Alive(operatorPlayer);}}
        void Poll()
        {
            if(session==null)return;
            if(RestartBlocksActions){PollRestart();RefreshControls();return;}
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
                    if(next.SessionId!=session)throw new InvalidDataException("Status belongs to another session and was rejected.");
                    if(next.Sequence<lastSequence)throw new InvalidDataException("Status sequence moved backward and was rejected.");
                    if(next.Sequence>lastSequence){latest=next;lastSequence=next.Sequence;lastStatusAt=clock.Elapsed.TotalSeconds;}
                }
                if(pending!=null)
                {
                    var ack=OwnerCommandFile.FindAcknowledgement(commands,pending);
                    if(ack!=null)
                    {
                        var acknowledged=pending;pending=null;
                        receipt.Text=(ack.Accepted?"Acknowledged: ":"Rejected: ")+acknowledged.Kind+" - "+ack.Reason;
                        if(acknowledged.Kind==OwnerCommandKind.Export)ObserveExportAcknowledgement(acknowledged,ack);
                    }
                    else if(clock.Elapsed.TotalSeconds-sentAt>3)receipt.Text="Command queued; acknowledgement is still pending. No automatic retry. Waiting for status and receipt.";
                }
                if(!Alive(participant)||!Alive(operatorPlayer))state.Text="A preview window is not running or has exited. Review this session's logs. Close any remaining window before explicitly starting a new session.";
                else if(!released&&clock.Elapsed.TotalSeconds-launchedAt>15)state.Text="Both preview windows started but the startup handshake is incomplete ("+portNote+"). A selected port may be occupied, or a window may have failed to bind. Close both preview windows before retrying. Logs: "+sessionDirectory;
                else if(!Fresh)state.Text="Waiting for current preview status. Controls are unavailable; experiment startup has not been confirmed.";
                else state.Text=StatusSummary();
                guidance.Text=Instruction();
            }
            catch(IOException error){state.Text="Status unavailable: "+error.Message;latest=null;}
            catch(Exception error){state.Text="Controls stopped: "+error.Message;latest=null;}
            RefreshControls();
        }
        string StatusSummary()
        {
            string progress=latest.BlockIndex<0?"Before main blocks":"Block: "+(latest.BlockIndex+1)+" / 4";
            string selected=!latest.LockedSource.HasValue?"Not confirmed":latest.LockedSource==ControlSelection.Automatic?"Automatic reference":latest.LockedSource==ControlSelection.SimulatorRehearsal?"Simulated head/eye input":"Live WoZ";
            return "Phase: "+latest.Phase+"   |   "+progress+"   |   Condition: "+(latest.Condition.HasValue?latest.Condition.ToString():"-")+
                "\nSession source: "+selected+"   |   Source ready: "+(latest.Ready?"Yes":"No")+"\n"+latest.Message;
        }
        string Instruction()
        {
            if(Fresh&&(latest.Phase==Phase.BlockPreparation||latest.Phase==Phase.BlockReady))
            {
                string condition=latest.Condition==Condition.D?" Condition D has no avatar with either source.":" The source stays fixed while avatar conditions change.";
                if(latest.BlockIndex>0)
                {
                    if(!latest.LockedSource.HasValue)return "The session source is missing. This block is stopped; review the session before reconnecting."+condition;
                    if(latest.Phase==Phase.BlockPreparation)return "The confirmed session source is retained. Unexpected block preparation has stopped continuation; review this session."+condition;
                    return (latest.Ready?"Continuing with the confirmed session source. The 3-second countdown starts automatically.":"Waiting for the confirmed session source to be ready. The 3-second countdown starts automatically when ready.")+condition;
                }
                if(latest.Phase==Phase.BlockPreparation)return "Practice is complete. Choose and confirm the session source once, then start the first block."+condition;
                if(!latest.LockedSource.HasValue)return "The session source is missing. This block is stopped; review the session before reconnecting."+condition;
                return (latest.Ready?"Session source confirmed and ready. Select Start first block for the 3-second countdown.":"Session source confirmed but not ready. Keep both preview windows running; start the first block when ready.")+condition;
            }
#if QUEST_PREVIEW_LAUNCHER
            if(!Fresh)return "Waiting for both preview processes. Quest Link must already be connected; gaze remains simulated. No source or practice starts automatically.";
            switch(latest.Phase)
            {
                case Phase.Preparation: case Phase.Familiarization: case Phase.PracticeRetry:
                    return "When headset and both Touch controllers report ready, the owner may begin practice. Answer the ink colour using X / Y / A / B.";
                case Phase.Practice: case Phase.MainTask:return "Touch: X red / Y green / A blue / B yellow. Release before another answer. Left menu pauses.";
                case Phase.PracticeCorrection:return "Read the correction in the headset, then make a new right-trigger press to continue.";
                case Phase.Questionnaire:return "Read the introduction at your own pace, then press A. For ratings, stick UP lowers / DOWN raises 1-5; centre between steps. A confirms.";
                case Phase.Rest:return "Rest as long as needed; there is no minimum. A continues immediately. The next 3-second countdown starts when the confirmed source is ready.";
                case Phase.Paused:return "Paused. Right trigger actively resumes; B withdraws. The owner cannot remotely resume the participant.";
            }
#endif
            if(!Fresh)return "Desktop simulation: waiting for both windows to load. This is not a VIVE or Quest headset application.";
            switch(latest.Phase)
            {
                case Phase.Preparation: case Phase.Familiarization: case Phase.PracticeRetry:return "Owner: select Start / retry practice. Then use the participant window to answer the ink colour with the number keys.";
                case Phase.Countdown:return "The 3-second countdown is running. The main task begins automatically when it finishes.";
                case Phase.Practice: case Phase.MainTask:return "Answer the ink colour in the participant window. Release the key before answering the next trial.";
                case Phase.PracticeCorrection:return "Read the correction in the participant window, then press Space to continue.";
                case Phase.Questionnaire:return "Read the introduction at your own pace, then press Space. Use keys 1-5 to answer each of the five rating items.";
                case Phase.Rest:return "Rest as long as needed; there is no minimum. Space continues immediately. The next 3-second countdown starts when the confirmed source is ready.";
                case Phase.Paused:return "Paused. The participant must press R to resume. The owner console cannot resume remotely.";
                case Phase.Ended:return "This simulation has ended. Export the recorded data, then close the preview windows.";
                default:return "Follow the current instructions in the participant window. Responses and phase changes are not automatic.";
            }
        }
        void RefreshControls()
        {
            if(latest!=null&&latest.LockedSource.HasValue)
            {
                int confirmed=latest.LockedSource==ControlSelection.Automatic?0:latest.LockedSource==ControlSelection.SimulatorRehearsal?1:-1;
                if(source.SelectedIndex!=confirmed)source.SelectedIndex=confirmed;
            }
            bool free=!RestartBlocksActions&&Fresh&&pending==null;
            launch.Enabled=!launchOutcomeUncertain&&!RestartBlocksActions&&CanReplace(participant)&&CanReplace(operatorPlayer);
            closePlayers.Enabled=(!RestartBlocksActions||restart.Stage==PreviewRestartStage.Blocked)&&(Alive(participant)||Alive(operatorPlayer));
            reconnect.Enabled=!RestartBlocksActions&&ParticipantFresh&&pending==null&&latest.Phase!=Phase.Ended;
            practice.Enabled=free&&(latest.Phase==Phase.Preparation||latest.Phase==Phase.Familiarization||latest.Phase==Phase.PracticeRetry);
            source.Enabled=free&&latest.BlockIndex==0&&latest.Phase==Phase.BlockPreparation&&!latest.LockedSource.HasValue;
            lockBlock.Enabled=source.Enabled&&source.SelectedIndex>=0;
            begin.Enabled=free&&latest.BlockIndex==0&&latest.Phase==Phase.BlockReady&&latest.LockedSource.HasValue&&latest.Ready;
            export.Enabled=free;
            openExport.Enabled=!RestartBlocksActions&&verifiedExport!=null;
        }
        void Send(OwnerCommandKind kind)
        {
            Guard(()=>{
                if(RestartBlocksActions)throw new InvalidOperationException("Owner actions are blocked while reconnect is pending or requires review.");
                if(!Fresh||pending!=null)throw new InvalidOperationException("Current status is required, and the previous command must be acknowledged.");
                ControlSelection? chosen=null;
                if(kind==OwnerCommandKind.Lock)
                {
                    if(latest.BlockIndex!=0||latest.Phase!=Phase.BlockPreparation||latest.LockedSource.HasValue)
                        throw new InvalidOperationException("The session source is confirmed only once, after practice and before the first block.");
                    if(source.SelectedIndex<0||source.SelectedIndex>1)throw new InvalidOperationException("Explicitly select the session control source.");
                    chosen=source.SelectedIndex==0?ControlSelection.Automatic:ControlSelection.SimulatorRehearsal;
                }
                if(kind==OwnerCommandKind.Begin&&(latest.BlockIndex!=0||latest.Phase!=Phase.BlockReady||!latest.LockedSource.HasValue||!latest.Ready))
                    throw new InvalidOperationException("Owner start is available only for the first block with a confirmed ready source.");
                pending=new OwnerCommand(Guid.NewGuid().ToString("N"),kind,chosen);sentAt=clock.Elapsed.TotalSeconds;
                if(kind==OwnerCommandKind.Export)ExportOutcome("CSV export requested; waiting for acknowledgement and a verified folder.");
                // Preserve this ID even if append throws: the write outcome may be uncertain.
                OwnerCommandFile.Append(commands,pending);receipt.Text="Queued: "+kind+"; waiting for the participant application to acknowledge.";
            });
        }
        void ResetExportPresentation()
        {
            verifiedExport=null;exportedFolder.Text="";
            exportState.Text="CSV export: no verified export for this session.";openExport.Enabled=false;
        }
        void ExportOutcome(string message)
        {
            exportState.Text=message+(verifiedExport==null?" No verified CSV folder is available.":" The previous verified export remains available below.");
        }
        void ObserveExportAcknowledgement(OwnerCommand command,OwnerAcknowledgement acknowledgement)
        {
            if(command==null||acknowledgement==null||command.Kind!=OwnerCommandKind.Export||
                acknowledgement.RequestId!=command.RequestId||acknowledgement.CommandHash!=OwnerCommandCodec.Hash(command))
                throw new InvalidDataException("Export acknowledgement does not match the pending request");
            if(!acknowledgement.Accepted){ExportOutcome("CSV export rejected; no new export was verified.");return;}
            PreviewExportReceipt evidence;string reason;
            if(!PreviewExportReceipt.TryReadVerified(sessionDirectory,session,command.RequestId,out evidence,out reason))
            {
                ExportOutcome("Export was acknowledged, but its CSV folder could not be verified. No new folder was selected.");
                receipt.Text=reason;return;
            }
            verifiedExport=evidence;exportedFolder.Text=evidence.DirectoryPath;
            exportState.Text="CSV snapshot saved and verified. Use Open exported data to view its files. This does not certify study completion.";
        }
        void OpenExport()
        {
            if(RestartBlocksActions||verifiedExport==null)throw new InvalidOperationException("A verified export is required before opening a folder");
            PreviewExportReceipt current;string reason;
            if(!PreviewExportReceipt.TryReadVerified(sessionDirectory,session,verifiedExport.RequestId,out current,out reason)||
                current.DirectoryPath!=verifiedExport.DirectoryPath)
            {
                ResetExportPresentation();exportState.Text="The previously verified CSV folder is unavailable or changed. No folder was opened.";
                throw new InvalidDataException(reason??"Export directory changed");
            }
            // This is an explicit owner click, never part of acknowledgement,
            // export, startup or retry. Revalidate before passing a path to Shell.
            Process.Start(new ProcessStartInfo(current.DirectoryPath){UseShellExecute=true,Verb="open"});
        }
        void CloseOwnedPlayers()
        {
            if(RestartBlocksActions&&restart.Stage!=PreviewRestartStage.Blocked)
                throw new InvalidOperationException("The acknowledged reconnect flow owns window closure. Wait for its result.");
            // Explicit owner click only; never kill or enumerate unrelated applications.
            if(Alive(participant))participant.CloseMainWindow();
            if(Alive(operatorPlayer))operatorPlayer.CloseMainWindow();
            receipt.Text="Normal closure requested. Wait for both windows to exit. No process was forcibly terminated.";
        }
        void BeginRestart()
        {
            if(RestartBlocksActions||!ParticipantFresh||pending!=null||latest.Phase==Phase.Ended)
                throw new InvalidOperationException("Reconnect requires current participant status and no pending owner command.");
            using(var confirmation=new PreviewRestartConfirmation(sessionDirectory))
                if(confirmation.ShowDialog(this)!=DialogResult.OK)return;
            // The modal dialog pumps messages; recheck the live state after it.
            if(RestartBlocksActions||!ParticipantFresh||pending!=null||latest.Phase==Phase.Ended)
                throw new InvalidOperationException("Participant status changed while confirming; no reconnect command was sent.");
            var command=new OwnerCommand(Guid.NewGuid().ToString("N"),OwnerCommandKind.RestartPreview);
            var workflow=new PreviewRestartWorkflow();double now=clock.Elapsed.TotalSeconds;
            if(!workflow.TryBegin(command,ParticipantFresh,pending!=null,now))throw new InvalidOperationException("Reconnect could not begin.");
            restart=workflow;restartCommand=command;restartSession=session;restartDirectory=sessionDirectory;
            folder.Height=96;receipt.Height=90;
            pending=command;sentAt=now;
            try {OwnerCommandFile.Append(commands,command);}
            catch(Exception error)
            {
                restart.Stop("owner_command_append_uncertain:"+error.Message+";no_automatic_retry");
                ShowRestartState();throw;
            }
            ShowRestartState();
        }
        static string RequestCloseOnce(Process process,string role)
        {
            bool exited;
            if(!ObserveExit(process,out exited))return role+" process state is unknown";
            if(exited)return null;
            try
            {
                if(process.CloseMainWindow())return null;
                // A normal exit can race the close request; require evidence.
                if(ObserveExit(process,out exited)&&exited)return null;
                return role+" window did not accept the normal close request";
            }
            catch(Exception error){return role+" close outcome is uncertain: "+error.Message;}
        }
        void PollRestart()
        {
            try
            {
                restart.Poll(clock.Elapsed.TotalSeconds);
                if(restart.Stage==PreviewRestartStage.AwaitingAcknowledgement)
                {
                    var ack=OwnerCommandFile.FindAcknowledgement(commands,restartCommand);
                    if(restart.ObserveAcknowledgement(ack,clock.Elapsed.TotalSeconds))pending=null;
                }
                if(restart.TryRequestClose(clock.Elapsed.TotalSeconds))
                {
                    // Each request is consumed once before either process call.
                    string participantFailure=RequestCloseOnce(participant,"Participant");
                    string operatorFailure=RequestCloseOnce(operatorPlayer,"Operator");
                    if(participantFailure!=null||operatorFailure!=null)
                        restart.Stop("normal_close_failed:"+participantFailure+" "+operatorFailure);
                }
                if(restart.Stage==PreviewRestartStage.AwaitingExit)
                {
                    bool participantExited,operatorExited;
                    bool participantKnown=ObserveExit(participant,out participantExited),operatorKnown=ObserveExit(operatorPlayer,out operatorExited);
                    restart.ObserveExit(participantKnown,participantExited,operatorKnown,operatorExited,clock.Elapsed.TotalSeconds);
                }
                if(restart.Stage==PreviewRestartStage.Verifying)
                {
                    string reason;
                    bool verified=PreviewRecovery.VerifyClosedSession(restartDirectory,restartSession,restart.RequestId,out reason);
                    restart.ObserveVerification(verified,reason,clock.Elapsed.TotalSeconds);
                }
                if(restart.TryClaimLaunch(clock.Elapsed.TotalSeconds))
                {
                    LaunchCore();restart.CompleteLaunch();
                    receipt.Text="Old preview ended and its saved records passed verification. A NEW session is opening; practice has not started and no source is selected.";
                    folder.Text="New session: "+sessionDirectory+"\nPreserved interrupted session: "+restartDirectory;
                }
            }
            catch(Exception error){restart.Stop("reconnect_action_failed:"+error.Message+";outcome_may_be_uncertain_no_automatic_retry");}
            if(RestartBlocksActions)ShowRestartState();
        }
        void ShowRestartState()
        {
            folder.Text="Preserved session: "+restartDirectory;
            if(sessionDirectory!=restartDirectory)folder.Text+="\nNew launch attempt: "+sessionDirectory;
            if(restart.Stage==PreviewRestartStage.Blocked)
            {
                state.Text="Reconnect stopped. No further automatic close or launch will be attempted.";
                receipt.Text=restart.FailureReason+"\nRequest: "+restart.RequestId;
                guidance.Text="Review the preserved session records. You may close remaining owned windows manually; this console will not bypass the unresolved result to open another session.";
            }
            else
            {
                state.Text="Explicit reconnect: "+restart.Stage+". The old preview will not resume.";
                receipt.Text="Request: "+restart.RequestId+". Waiting for the acknowledged ending, normal process exits and complete saved records.";
                guidance.Text="The new session will begin at Preparation. Existing progress, interrupted trials and logs stay in the old session.";
            }
        }
        [STAThread] public static int Main()
        {
            AppLanguage.UseEnglishResources();
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DesktopPreviewLauncher());return 0;
        }
    }

    // Project-owned captions avoid native MessageBox button localization. This
    // dialog changes no session state; only the owner's explicit OK choice lets
    // BeginRestart recheck readiness and create its existing command/workflow.
    internal sealed class PreviewRestartConfirmation : Form
    {
        public PreviewRestartConfirmation(string recordDirectory)
        {
            if(String.IsNullOrWhiteSpace(recordDirectory))throw new ArgumentException("The preserved record directory is required.","recordDirectory");
            AppLanguage.UseEnglishResources();
            Name="PreviewRestartConfirmation";Text="End preview and reconnect";
            Font=new Font("Segoe UI",11);AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(680,380);FormBorderStyle=FormBorderStyle.FixedDialog;
            MinimizeBox=false;MaximizeBox=false;ShowInTaskbar=false;StartPosition=FormStartPosition.CenterParent;
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=4};
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50));
            var explanation=new Label{Name="RestartExplanation",Dock=DockStyle.Fill,Margin=new Padding(0),Text=
                "End this preview as a technical interruption and reconnect in a NEW session?\n\n"+
                "Current practice and block progress will not resume. The new session starts at Preparation with no control source selected.\n\n"+
                "A new preview opens only after the ending is acknowledged, both owned windows exit normally, and both saved journals pass verification."};
            layout.Controls.Add(explanation);
            layout.Controls.Add(new Label{Name="PreservedRecordsLabel",Dock=DockStyle.Fill,Margin=new Padding(0),Text="Existing records are preserved in this folder:"});
            layout.Controls.Add(new TextBox{Name="PreservedRecordsPath",Dock=DockStyle.Fill,Margin=new Padding(0,0,0,6),ReadOnly=true,Multiline=true,WordWrap=false,ScrollBars=ScrollBars.Horizontal,Text=recordDirectory});
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Margin=new Padding(0)};
            var cancel=new Button{Name="CancelRestart",Text="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true,MinimumSize=new Size(100,40),Margin=new Padding(12,4,0,0)};
            var confirm=new Button{Name="ConfirmRestart",Text="End preview and start new session",DialogResult=DialogResult.OK,AutoSize=true,MinimumSize=new Size(300,40),Margin=new Padding(0,4,0,0)};
            buttons.Controls.Add(cancel);buttons.Controls.Add(confirm);layout.Controls.Add(buttons);Controls.Add(layout);
            AcceptButton=cancel;CancelButton=cancel;ActiveControl=cancel;
        }
    }
}
