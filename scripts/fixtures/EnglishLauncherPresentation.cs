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
    // clicks, Poll, owner commands, sockets or participant processes are used.
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
            foreach(string name in new[]{"participant","operatorPlayer","session","commands","statusPath","startup","sessionDirectory","pending","restartCommand","restart","restartSession","restartDirectory"})
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
        static void ExerciseGuidance(DesktopPreviewLauncher form,string scenario)
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
                        form.PerformLayout();Measure(form,scenario+"-"+phase);
                    }
                }
                finally
                {
                    Set(form,"participant",null);Set(form,"operatorPlayer",null);Set(form,"released",false);Set(form,"latest",null);
                    Get<Label>(form,"guidance").Text="";
                }
            }
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
                            ExerciseGuidance(form,scenario);
                            CheckNoSessionEffects(form);
                        }
                        finally{timer.Stop();timer.Dispose();}
                    }
                }
                CheckConfirmation(output,flavour);
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
