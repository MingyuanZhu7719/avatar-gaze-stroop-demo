using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AvatarStroop.Apps;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Fixtures
{
    // Offline presentation fixture only. No Show, Application.Run, DoEvents,
    // clicks, Poll, queued owner commands, sockets or participant processes are
    // used. Export checks use only synthetic snapshots under fixture output.
    public static class EnglishLauncherPresentation
    {
        static readonly BindingFlags PrivateInstance=BindingFlags.Instance|BindingFlags.NonPublic;
        static readonly List<string> failures=new List<string>();
        static readonly List<string> metrics=new List<string>();
        static int checks;
        static FieldInfo Field(string name){return typeof(DesktopPreviewLauncher).GetField(name,PrivateInstance);}
        static T Get<T>(DesktopPreviewLauncher form,string name){return (T)Field(name).GetValue(form);}
        static void Set(DesktopPreviewLauncher form,string name,object value){Field(name).SetValue(form,value);}
        static void Require(bool value,string message){checks++;if(!value)failures.Add(message);}
        static bool ContainsCjk(string text)
        {
            foreach(char c in text)if((c>=0x2e80&&c<=0x9fff)||(c>=0xf900&&c<=0xfaff)||c==0xfffd)return true;
            return false;
        }
        static IEnumerable<Control> Descendants(Control parent)
        {
            foreach(Control child in parent.Controls)
            {yield return child;foreach(var grandchild in Descendants(child))yield return grandchild;}
        }
        static void CheckNoSessionEffects(DesktopPreviewLauncher form)
        {
            foreach(string name in new[]{"participant","operatorPlayer","session","commands","statusPath","startup","sessionDirectory","pending","restartCommand","restart","restartSession","restartDirectory","verifiedExport"})
                Require(Field(name).GetValue(form)==null,"fixture acquired runtime/session state: "+name);
            Require(!Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"sessions")),"fixture created a sessions directory");
            Require(!form.Visible,"fixture made a real window visible");
        }
        static void CreateOffscreenControls(Control control)
        {
            var handle=control.Handle;Require(handle!=IntPtr.Zero,"offscreen control handle missing");
            foreach(Control child in control.Controls)CreateOffscreenControls(child);
            control.PerformLayout();
        }
        static void Measure(Form form,string scenario)
        {
            Require(!ContainsCjk(form.Text),scenario+": title contains localization");
            using(var graphics=form.CreateGraphics())
            {
                var title=TextRenderer.MeasureText(graphics,form.Text,SystemFonts.CaptionFont,Size.Empty,TextFormatFlags.SingleLine);
                Require(title.Width<form.Width-3*SystemInformation.CaptionButtonSize.Width-45,scenario+": title exceeds caption area");
                foreach(Control control in Descendants(form))
                {
                    if(control is Label)
                    {
                        if(String.IsNullOrEmpty(control.Text))continue;
                        var label=(Label)control;
                        int width=Math.Max(1,label.ClientSize.Width-label.Padding.Horizontal);
                        int height=label.ClientSize.Height-label.Padding.Vertical;
                        var needed=TextRenderer.MeasureText(graphics,label.Text,label.Font,new Size(width,Int32.MaxValue),
                            TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl|TextFormatFlags.NoPrefix);
                        string text=label.Text.Replace("\r","").Replace("\n"," / ");
                        metrics.Add(scenario+"\tLabel\t"+width+"x"+height+"\t"+needed.Width+"x"+needed.Height+"\t"+text);
                        Require(!ContainsCjk(label.Text),scenario+": label contains localization: "+text);
                        Require(needed.Height<=height+1&&needed.Width<=width+1,scenario+": label clipped (needs "+needed+", has "+width+"x"+height+"): "+text);
                    }
                    else if(control is Button)
                    {
                        var needed=control.GetPreferredSize(Size.Empty);
                        Require(!ContainsCjk(control.Text),scenario+": button contains localization");
                        Require(needed.Width<=control.Width+1&&needed.Height<=control.Height+1,scenario+": button clipped: "+control.Text);
                    }
                    else if(control is ComboBox)
                    {
                        var combo=(ComboBox)control;
                        int width=combo.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-8;
                        foreach(object item in combo.Items)
                        {
                            var text=item.ToString();var needed=TextRenderer.MeasureText(graphics,text,combo.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
                            metrics.Add(scenario+"\tComboBox\t"+width+"\t"+needed.Width+"\t"+text);
                            Require(!ContainsCjk(text),scenario+": source choice contains localization");
                            Require(needed.Width<=width,scenario+": source choice clipped: "+text);
                        }
                    }
                }
            }
        }
        static bool RenderedControl(Control control)
        {return control is Label||control is Button||control is ComboBox||control is TextBox;}
        static void Draw(Form form,string directory,string name,bool fullContent=false)
        {
            Require(!form.Visible,"offscreen form became visible before drawing");
            int width=form.Width,height=form.Height;
            if(fullContent)
            {
                // Native windows cannot exceed the desktop's maximum track size.
                // A synthetic content sheet prints all actual laid-out controls
                // without claiming a larger window or interactive scroll result.
                width=form.ClientSize.Width;height=form.ClientSize.Height;
                foreach(Control control in Descendants(form))if(RenderedControl(control))
                {
                    Point point=form.PointToClient(control.PointToScreen(Point.Empty));
                    height=Math.Max(height,point.Y+control.Height+24);
                }
            }
            using(var image=new Bitmap(width,height))
            {
                if(!fullContent)form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));
                // Windows omits invisible child HWNDs when printing the parent.
                // Print each real laid-out child directly and compose its bitmap;
                // no visibility flag, message pump or live desktop is involved.
                using(var graphics=Graphics.FromImage(image))
                {
                    Point client=Point.Empty;
                    if(fullContent)graphics.Clear(form.BackColor);
                    else{client=form.PointToScreen(Point.Empty);client.Offset(-form.Left,-form.Top);}
                    graphics.SetClip(fullContent?new Rectangle(Point.Empty,image.Size):new Rectangle(client,form.ClientSize));
                    foreach(Control control in Descendants(form))
                    {
                        if(!RenderedControl(control))continue;
                        if(control.Width<=0||control.Height<=0)continue;
                        Point point=form.PointToClient(control.PointToScreen(Point.Empty));point.Offset(client);
                        if(fullContent)Require(point.X>=0&&point.Y>=0&&point.X+control.Width<=image.Width&&point.Y+control.Height<=image.Height,
                            "full-content canvas does not contain complete control: "+control.Text);
                        using(var part=new Bitmap(control.Width,control.Height))
                        {control.DrawToBitmap(part,new Rectangle(Point.Empty,control.Size));graphics.DrawImageUnscaled(part,point);}
                    }
                }
                image.Save(Path.Combine(directory,name+".png"),ImageFormat.Png);
            }
            Require(!form.Visible,"offscreen form became visible after drawing");
        }
        static void ExerciseGuidance(DesktopPreviewLauncher form,string scenario,string output)
        {
            // Only this fixture's own process handles satisfy the pure Fresh
            // predicate. No process or lifecycle method is invoked. Session/path
            // fields remain null, and handles are cleared before disposal.
            using(var ownParticipant=Process.GetCurrentProcess())
            using(var ownOperator=Process.GetCurrentProcess())
            {
                Set(form,"participant",ownParticipant);Set(form,"operatorPlayer",ownOperator);Set(form,"released",true);
                try
                {
                    foreach(Phase phase in Enum.GetValues(typeof(Phase)))
                    {
                        bool block=phase>=Phase.BlockPreparation;
                        var status=new DesktopPreviewStatus("synthetic-ui-fixture",0,phase,block?0:-1,block?(Condition?)Condition.A:null,
                            block?(ControlSelection?)ControlSelection.Automatic:null,block,"Synthetic presentation only; no session was started.");
                        Set(form,"latest",status);Set(form,"lastStatusAt",Get<Stopwatch>(form,"clock").Elapsed.TotalSeconds);
                        string instruction=(string)typeof(DesktopPreviewLauncher).GetMethod("Instruction",PrivateInstance).Invoke(form,null);
                        Get<Label>(form,"guidance").Text=instruction;
                        typeof(DesktopPreviewLauncher).GetMethod("RefreshControls",PrivateInstance).Invoke(form,null);
                        if(phase==Phase.Ended)
                        {
                            Require(Get<Button>(form,"export").Enabled,"fresh Ended preview cannot export");
                            Require(!Get<Button>(form,"openExport").Enabled,"unverified Ended preview can open an export");
                        }
                        form.PerformLayout();Measure(form,scenario+"-"+phase);
                    }
                    foreach(Phase phase in new[]{Phase.BlockPreparation,Phase.BlockReady})
                    {
                        Set(form,"latest",new DesktopPreviewStatus("synthetic-ui-fixture",0,phase,3,Condition.D,ControlSelection.Automatic,true,"Synthetic D guidance only."));
                        Set(form,"lastStatusAt",Get<Stopwatch>(form,"clock").Elapsed.TotalSeconds);
                        string instruction=(string)typeof(DesktopPreviewLauncher).GetMethod("Instruction",PrivateInstance).Invoke(form,null);
                        Require(instruction.Contains("no avatar")&&instruction.Contains("either source"),"D guidance does not explain source-independent absence");
                        Get<Label>(form,"guidance").Text=instruction;form.PerformLayout();Measure(form,scenario+"-D-"+phase);
                    }
                    CheckSessionSourceContinuity(form,scenario,output);
                }
                finally
                {
                    Set(form,"participant",null);Set(form,"operatorPlayer",null);Set(form,"released",false);Set(form,"latest",null);
                    Get<Label>(form,"guidance").Text="";Get<ComboBox>(form,"source").SelectedIndex=-1;
                }
            }
        }
        static string PresentStatus(DesktopPreviewLauncher form,Phase phase,int block,ControlSelection? selected,bool ready)
        {
            var conditions=new[]{Condition.B,Condition.C,Condition.A,Condition.D};
            Set(form,"latest",new DesktopPreviewStatus("synthetic-ui-fixture",0,phase,block,conditions[block],selected,ready,"Synthetic presentation only; no session was started."));
            Set(form,"lastStatusAt",Get<Stopwatch>(form,"clock").Elapsed.TotalSeconds);
            typeof(DesktopPreviewLauncher).GetMethod("RefreshControls",PrivateInstance).Invoke(form,null);
            string instruction=(string)typeof(DesktopPreviewLauncher).GetMethod("Instruction",PrivateInstance).Invoke(form,null);
            Get<Label>(form,"guidance").Text=instruction;
            Get<Label>(form,"state").Text=(string)typeof(DesktopPreviewLauncher).GetMethod("StatusSummary",PrivateInstance).Invoke(form,null);
            return instruction;
        }
        static void CheckSessionSourceContinuity(DesktopPreviewLauncher form,string scenario,string output)
        {
            var selector=Get<ComboBox>(form,"source");var confirm=Get<Button>(form,"lockBlock");var begin=Get<Button>(form,"begin");
            PresentStatus(form,Phase.BlockPreparation,0,null,false);selector.SelectedIndex=-1;
            Require(selector.Enabled&&!confirm.Enabled&&!begin.Enabled,"initial source must require an explicit selection before confirmation");
            selector.SelectedIndex=1;
            Require(selector.Enabled&&confirm.Enabled&&!begin.Enabled,"initial source selection did not require confirmation before start");
            PresentStatus(form,Phase.BlockReady,0,ControlSelection.SimulatorRehearsal,false);
            Require(!selector.Enabled&&!confirm.Enabled&&!begin.Enabled,"initial block can start before confirmed source is ready");
            PresentStatus(form,Phase.BlockReady,0,ControlSelection.SimulatorRehearsal,true);
            Require(!selector.Enabled&&!confirm.Enabled&&begin.Enabled,"first block does not retain its explicit owner start");
            Require(selector.SelectedIndex==1,"first source acknowledgement did not display the confirmed simulated source");
            Require(begin.Text.Contains("first block")&&confirm.Text.Contains("session source"),"owner captions imply repeated block source confirmation");
            form.PerformLayout();Measure(form,scenario+"-first-block-ready");
            if(scenario.EndsWith("-normal",StringComparison.Ordinal))Draw(form,output,scenario+"-first-block-ready",true);
            string missingInitial=PresentStatus(form,Phase.BlockReady,0,null,false);
            Require(!selector.Enabled&&!confirm.Enabled&&!begin.Enabled&&missingInitial.Contains("source is missing"),"initial ready state without a source was treated as confirmed");
            foreach(ControlSelection selected in new[]{ControlSelection.Automatic,ControlSelection.SimulatorRehearsal})
                for(int block=1;block<4;block++)
                    foreach(Phase phase in new[]{Phase.BlockPreparation,Phase.BlockReady})
                        foreach(bool ready in new[]{false,true})
                        {
                            string instruction=PresentStatus(form,phase,block,selected,ready);
                            Require(!selector.Enabled&&!confirm.Enabled&&!begin.Enabled,"later block exposed owner selection/confirmation/start: "+block+"/"+phase+"/"+ready);
                            Require(selector.SelectedIndex==(selected==ControlSelection.Automatic?0:1),"later block lost or replaced its confirmed source");
                            Require(instruction.Contains("confirmed session source"),"later block did not explain source continuity");
                            if(phase==Phase.BlockReady)
                            {
                                Require(instruction.Contains("countdown starts automatically"),"later ready block did not explain automatic countdown");
                                Require(ready||instruction.Contains("when ready"),"unready source guidance lacks readiness wait");
                            }
                            else Require(instruction.Contains("stopped continuation"),"unexpected later preparation incorrectly promised automatic countdown");
                            form.PerformLayout();Measure(form,scenario+"-retained-source-"+selected+"-"+block+"-"+phase+"-"+ready);
                        }
            PresentStatus(form,Phase.BlockReady,3,ControlSelection.SimulatorRehearsal,false);
            if(scenario.EndsWith("-normal",StringComparison.Ordinal))Draw(form,output,scenario+"-later-block-waiting",true);
            foreach(Phase phase in new[]{Phase.BlockPreparation,Phase.BlockReady})
            {
                string instruction=PresentStatus(form,phase,2,null,false);
                Require(!selector.Enabled&&!confirm.Enabled&&!begin.Enabled,"unexpected later unlocked state enabled an owner action");
                Require(instruction.Contains("source is missing")&&instruction.Contains("stopped"),"missing retained source did not fail closed in guidance");
                Require(Get<Label>(form,"state").Text.Contains("Not confirmed"),"missing source was mislabeled as confirmed");
            }
            PresentStatus(form,Phase.BlockReady,1,ControlSelection.LiveWoz,false);
            Require(selector.SelectedIndex==-1&&!selector.Enabled&&!confirm.Enabled&&!begin.Enabled,"unsupported retained source was silently replaced by a preview source");
            Require(Get<Label>(form,"state").Text.Contains("Live WoZ"),"unsupported retained source was concealed in status");
            Require(Get<OwnerCommand>(form,"pending")==null,"presentation checks queued an owner command");
        }
        static void CheckConfirmation(string output,string flavour)
        {
            string preserved=Path.Combine(output,"synthetic-record-path-not-created");
            using(var confirmation=new PreviewRestartConfirmation(preserved))
            {
                Require(!confirmation.Visible&&confirmation.DialogResult==DialogResult.None,"confirmation was shown or accepted by construction");
                var cancel=(Button)confirmation.Controls.Find("CancelRestart",true)[0];
                var confirm=(Button)confirmation.Controls.Find("ConfirmRestart",true)[0];
                var path=(TextBox)confirmation.Controls.Find("PreservedRecordsPath",true)[0];
                var message=(Label)confirmation.Controls.Find("RestartExplanation",true)[0];
                Require(cancel.Text=="Cancel"&&cancel.DialogResult==DialogResult.Cancel,"confirmation cancellation caption/result changed");
                Require(confirm.Text=="End preview and start new session"&&confirm.DialogResult==DialogResult.OK,"confirmation explicit action caption/result changed");
                Require(confirmation.AcceptButton==cancel&&confirmation.CancelButton==cancel,"Enter/Escape must default to cancellation");
                Require(path.ReadOnly&&path.Text==preserved,"confirmation rewrote the preserved record path");
                Require(message.Text.Contains("will not resume")&&message.Text.Contains("NEW session")&&message.Text.Contains("both saved journals pass verification"),"confirmation omits progress or verification safeguards");
                CreateOffscreenControls(confirmation);confirmation.PerformLayout();Measure(confirmation,flavour+"-confirmation");
                Draw(confirmation,output,flavour+"-confirmation");
                Require(!Directory.Exists(preserved)&&!Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"sessions")),"confirmation created session data");
                Require(confirmation.DialogResult==DialogResult.None&&!confirmation.Visible,"drawing the confirmation accepted or displayed it");
            }
        }
        static void CheckExportPresentation(string output,string flavour)
        {
            string root=Path.Combine(output,"synthetic-export-evidence-"+flavour+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            const string sessionId="synthetic-ui-export";
            var allocation=new Allocation("synthetic-ui",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var experiment=new Experiment(new SessionConfiguration(sessionId,42,allocation,RunKind.Simulation,"en",3),new MonotonicClock("synthetic-ui-clock"));
            var audit=new AuditLog(sessionId,RunKind.Simulation);var provenance=new ExportProvenance("offline-ui-fixture","synthetic only","synthetic only");
            using(var form=new DesktopPreviewLauncher())
            {
                var timer=Get<System.Windows.Forms.Timer>(form,"timer");timer.Stop();
                try
                {
                    Set(form,"session",sessionId);Set(form,"sessionDirectory",root);
                    Get<Label>(form,"state").Text="Synthetic export-receipt presentation only. No preview process or participant session was started.";
                    Get<Label>(form,"folder").Text="Raw session records: "+root;
                    var observe=typeof(DesktopPreviewLauncher).GetMethod("ObserveExportAcknowledgement",PrivateInstance);
                    var refresh=typeof(DesktopPreviewLauncher).GetMethod("RefreshControls",PrivateInstance);
                    Action<OwnerCommand,bool> acknowledge=(command,accepted)=>{
                        Get<Label>(form,"receipt").Text=(accepted?"Acknowledged: ":"Rejected: ")+"Export - synthetic fixture";
                        observe.Invoke(form,new object[]{command,new OwnerAcknowledgement(command.RequestId,accepted,"synthetic fixture",OwnerCommandCodec.Hash(command))});refresh.Invoke(form,null);
                    };
                    var first=new OwnerCommand(Guid.NewGuid().ToString("N"),OwnerCommandKind.Export);
                    acknowledge(first,true);
                    Require(Get<PreviewExportReceipt>(form,"verifiedExport")==null&&!Get<Button>(form,"openExport").Enabled,"ACK without receipt exposed a folder");
                    var result=AuditExporter.Export(experiment,audit,Path.Combine(root,"exports"),provenance);
                    PreviewExportReceipt.WriteCompleted(root,sessionId,first.RequestId,result);acknowledge(first,true);
                    Require(Get<TextBox>(form,"exportedFolder").ReadOnly&&Get<TextBox>(form,"exportedFolder").Text==result.DirectoryPath,"verified CSV folder is not shown read-only");
                    Require(Get<Button>(form,"openExport").Enabled,"verified export has no explicit open action");
                    var failed=new OwnerCommand(Guid.NewGuid().ToString("N"),OwnerCommandKind.Export);acknowledge(failed,false);
                    Require(Get<TextBox>(form,"exportedFolder").Text==result.DirectoryPath&&Get<Label>(form,"exportState").Text.Contains("rejected"),"failed later export hid or relabeled prior success");
                    acknowledge(failed,true);
                    Require(Get<TextBox>(form,"exportedFolder").Text==result.DirectoryPath&&Get<Label>(form,"exportState").Text.Contains("could not be verified"),"missing later receipt replaced prior success");
                    CreateOffscreenControls(form);form.PerformLayout();Measure(form,flavour+"-export-unverified");
                    Draw(form,output,flavour+"-export-unverified",true);
                    var second=AuditExporter.Export(experiment,audit,Path.Combine(root,"exports"),provenance);
                    PreviewExportReceipt.WriteCompleted(root,sessionId,failed.RequestId,second);acknowledge(failed,true);
                    Require(result.DirectoryPath!=second.DirectoryPath&&Get<TextBox>(form,"exportedFolder").Text==second.DirectoryPath,"second export did not show its distinct folder");
                    Require(Directory.Exists(result.DirectoryPath),"previous CSV snapshot disappeared");
                    form.PerformLayout();Measure(form,flavour+"-export-verified");Draw(form,output,flavour+"-export-verified",true);
                    typeof(DesktopPreviewLauncher).GetMethod("ResetExportPresentation",PrivateInstance).Invoke(form,null);
                    Require(Get<PreviewExportReceipt>(form,"verifiedExport")==null&&Get<TextBox>(form,"exportedFolder").Text==""&&!Get<Button>(form,"openExport").Enabled,"new launch reset kept a stale export");
                    Set(form,"session",null);Set(form,"sessionDirectory",null);CheckNoSessionEffects(form);
                }
                finally{timer.Stop();timer.Dispose();}
            }
        }
        [STAThread]
        public static int Main(string[] args)
        {
            if(args.Length!=1||!Path.IsPathRooted(args[0]))throw new ArgumentException("An absolute synthetic evidence directory is required.");
            string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var originalUi=Thread.CurrentThread.CurrentUICulture;var originalDefaultUi=CultureInfo.DefaultThreadCurrentUICulture;
            var originalNumeric=Thread.CurrentThread.CurrentCulture;
            string flavour=
#if QUEST_PREVIEW_LAUNCHER
                "quest";
#else
                "desktop";
#endif
            try
            {
                foreach(string size in new[]{"normal","minimum"})
                {
                    Thread.CurrentThread.CurrentUICulture=CultureInfo.GetCultureInfo("zh-CN");
                    CultureInfo.DefaultThreadCurrentUICulture=CultureInfo.GetCultureInfo("zh-CN");
                    Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
                    using(var form=new DesktopPreviewLauncher())
                    {
                        var timer=Get<System.Windows.Forms.Timer>(form,"timer");timer.Stop();
                        try
                        {
                            CheckNoSessionEffects(form);
                            Require(!timer.Enabled,"launcher timer still enabled in offline fixture");
                            Require(Thread.CurrentThread.CurrentUICulture.Name=="en"&&CultureInfo.DefaultThreadCurrentUICulture.Name=="en","constructor did not select English UI resources");
                            Require(Thread.CurrentThread.CurrentCulture.Name=="fr-FR","UI language policy changed numeric culture");
                            if(size=="minimum")form.Size=form.MinimumSize;
                            // Creating an invisible native control handle supports
                            // DrawToBitmap; it is not a visible desktop launch.
                            var ignored=form.Handle;CreateOffscreenControls(form);form.PerformLayout();
                            string scenario=flavour+"-"+size;
                            Measure(form,scenario+"-initial");
                            Draw(form,output,scenario+"-initial");
                            if(size=="normal")Draw(form,output,flavour+"-full-content",true);
                            ExerciseGuidance(form,scenario,output);
                            CheckNoSessionEffects(form);
                        }
                        finally{timer.Stop();timer.Dispose();}
                    }
                }
                CheckConfirmation(output,flavour);
                CheckExportPresentation(output,flavour);
                File.WriteAllLines(Path.Combine(output,flavour+"-text-metrics.tsv"),metrics,new UTF8Encoding(false));
                File.WriteAllLines(Path.Combine(output,flavour+"-failures.txt"),failures,new UTF8Encoding(false));
                Console.WriteLine("OFFSCREEN_ENGLISH_LAUNCHER "+flavour+": "+checks+" checks, "+failures.Count+" failures; no visible form or session.");
                foreach(string failure in failures)Console.WriteLine("FAIL "+failure);
                return failures.Count==0?0:1;
            }
            finally
            {Thread.CurrentThread.CurrentUICulture=originalUi;CultureInfo.DefaultThreadCurrentUICulture=originalDefaultUi;Thread.CurrentThread.CurrentCulture=originalNumeric;}
        }
    }
}
