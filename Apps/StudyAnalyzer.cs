using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AvatarStroop.Runtime;

namespace AvatarStroop.Apps
{
    public static class StudyAnalyzer
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if(args.Length>0)
            {
                try
                {
                    var inputs=new System.Collections.Generic.List<string>();string output=null;
                    for(int i=0;i<args.Length;i++)
                    {
                        if(args[i]=="--input"&&i+1<args.Length)inputs.Add(args[++i]);
                        else if(args[i]=="--output"&&i+1<args.Length)output=args[++i];
                        else if(args[i]=="--help"){Console.WriteLine("AvatarStroopAnalyzer --input completed.vraw [--input another.csv] --output existing-or-new-parent-directory");return 0;}
                        else throw new ArgumentException("Unknown or incomplete argument: "+args[i]);
                    }
                    if(output==null)throw new ArgumentException("--output is required in command-line mode.");
                    string report=StudyAnalysisFiles.Run(inputs,output,Console.WriteLine,CancellationToken.None);Console.WriteLine(report);return 0;
                }
                catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
            }
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AnalyzerForm());return 0;
        }
    }

    public sealed class AnalyzerForm:Form
    {
        readonly ListBox inputs=new ListBox{Dock=DockStyle.Fill,HorizontalScrollbar=true,SelectionMode=SelectionMode.MultiExtended};
        readonly TextBox output=new TextBox{Dock=DockStyle.Fill};
        readonly Button add=new Button{Text="选择 VRAW / CSV",AutoSize=true};
        readonly TextBox pathEntry=new TextBox{Dock=DockStyle.Fill,MaxLength=32767,AccessibleName="完整记录路径"};
        readonly Button addPath=new Button{Text="添加路径",AutoSize=true};
        readonly Button clear=new Button{Text="移除选中",AutoSize=true};
        readonly Button browse=new Button{Text="选择输出位置",AutoSize=true};
        readonly Button analyze=new Button{Text="开始分析",AutoSize=true};
        readonly Button cancel=new Button{Text="取消",AutoSize=true,Enabled=false};
        readonly Button open=new Button{Text="打开报告",AutoSize=true,Enabled=false};
        readonly Label status=new Label{Text="选择已完成的实验记录。",AutoSize=true,Dock=DockStyle.Fill};
        readonly ProgressBar progress=new ProgressBar{Dock=DockStyle.Fill,Style=ProgressBarStyle.Marquee,Visible=false};
        CancellationTokenSource cancellation;string report;bool running;

        public AnalyzerForm()
        {
            Text="Avatar Stroop · 实验数据分析";ClientSize=new Size(960,640);MinimumSize=new Size(760,560);StartPosition=FormStartPosition.CenterScreen;
            Font=new Font("Microsoft YaHei UI",10F);BackColor=Color.FromArgb(244,247,250);
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(28),ColumnCount=1,RowCount=10};
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,24));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            layout.Controls.Add(new Label{Text="实验数据分析",Font=new Font(Font.FontFamily,22,FontStyle.Bold),AutoSize=true},0,0);
            layout.Controls.Add(new Label{Text="支持 Stroop 和 Visual search 的 VRAW / CSV，分析反应时、正确率、问卷和眼动质量；搜索任务按 12 / 24 个物品分别汇总。\n每个 session 只选一种格式；眼动精度验收暂缓，AOI 不作为已验证的注视指标。",Dock=DockStyle.Fill},0,1);
            var files=new FlowLayoutPanel{Dock=DockStyle.Fill};files.Controls.Add(add);files.Controls.Add(clear);files.Controls.Add(new Label{Text="长路径可在下方粘贴添加。",AutoSize=true,Padding=new Padding(12,6,0,0)});layout.Controls.Add(files,0,2);
            var pathRow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3};pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.Controls.Add(new Label{Text="完整路径",AutoSize=true,Padding=new Padding(0,5,8,0)},0,0);pathRow.Controls.Add(pathEntry,1,0);pathRow.Controls.Add(addPath,2,0);layout.Controls.Add(pathRow,0,3);layout.Controls.Add(inputs,0,4);
            layout.Controls.Add(new Label{Text="输出位置（每次分析自动创建新文件夹）",AutoSize=true},0,5);
            var destination=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));destination.Controls.Add(output,0,0);destination.Controls.Add(browse,1,0);layout.Controls.Add(destination,0,6);
            output.Text=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AnalysisResults");
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill};buttons.Controls.Add(analyze);buttons.Controls.Add(cancel);buttons.Controls.Add(open);layout.Controls.Add(buttons,0,7);layout.Controls.Add(progress,0,8);layout.Controls.Add(status,0,9);Controls.Add(layout);
            add.Click+=(s,e)=>Choose();clear.Click+=(s,e)=>{foreach(var item in inputs.SelectedItems.Cast<object>().ToArray())inputs.Items.Remove(item);};
            addPath.Click+=(s,e)=>AddManualPath();pathEntry.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;AddManualPath();}};
            browse.Click+=(s,e)=>{using(var picker=new FolderBrowserDialog{Description="选择分析结果的保存位置",SelectedPath=Directory.Exists(output.Text)?output.Text:""})if(picker.ShowDialog(this)==DialogResult.OK)output.Text=picker.SelectedPath;};
            analyze.Click+=async(s,e)=>await Analyze();cancel.Click+=(s,e)=>{if(cancellation!=null)cancellation.Cancel();};
            open.Click+=(s,e)=>OpenReport();FormClosing+=(s,e)=>{if(running){e.Cancel=true;cancellation.Cancel();status.Text="正在取消；读取停止后可以关闭。";}};
        }
        void Choose()
        {
            using(var picker=new OpenFileDialog{Title="选择已完成的实验记录",Filter="实验记录 (*.vraw;*.csv)|*.vraw;*.csv",Multiselect=true,CheckFileExists=true})
                if(picker.ShowDialog(this)==DialogResult.OK)foreach(string path in picker.FileNames)AddInputPath(path);
        }
        internal static string NormalizeInputPath(string supplied)
        {
            string value=(supplied??String.Empty).Trim();
            if(value.Length>=2&&value[0]=='"'&&value[value.Length-1]=='"')value=value.Substring(1,value.Length-2);
            if(String.IsNullOrWhiteSpace(value))throw new ArgumentException("请粘贴 VRAW 或 CSV 文件的完整路径。");
            string full=Path.GetFullPath(value),extension=Path.GetExtension(full);
            if(!String.Equals(extension,".vraw",StringComparison.OrdinalIgnoreCase)&&!String.Equals(extension,".csv",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择 VRAW 或 CSV 文件。");
            return full;
        }
        void AddInputPath(string value)
        {
            string path=NormalizeInputPath(value);
            if(!inputs.Items.Cast<string>().Any(item=>String.Equals(item,path,StringComparison.OrdinalIgnoreCase)))inputs.Items.Add(path);
        }
        void AddManualPath()
        {
            try
            {
                // No legacy file picker or File.Exists check here: the
                // read-only analyzer open validates existence and writer locks.
                AddInputPath(pathEntry.Text);pathEntry.Clear();status.Text="路径已添加。点击“开始分析”读取记录。";
            }
            catch(Exception error) when(error is ArgumentException||error is NotSupportedException||error is PathTooLongException)
            {status.Text=error.Message;pathEntry.Focus();}
        }
        async Task Analyze()
        {
            if(inputs.Items.Count==0){status.Text="请先选择实验记录文件。";return;}
            if(String.IsNullOrWhiteSpace(output.Text)){status.Text="请选择输出位置。";return;}
            string[] selected=inputs.Items.Cast<string>().ToArray();string destination=output.Text;
            running=true;add.Enabled=addPath.Enabled=pathEntry.Enabled=clear.Enabled=browse.Enabled=analyze.Enabled=output.Enabled=inputs.Enabled=open.Enabled=false;cancel.Enabled=true;progress.Visible=true;report=null;
            cancellation=new CancellationTokenSource();var token=cancellation.Token;
            var notify=new Progress<string>(text=>status.Text=text);
            try
            {
                report=await Task.Run(()=>StudyAnalysisFiles.Run(selected,destination,text=>((IProgress<string>)notify).Report(text),token));
                status.Text="分析完成。点击“打开报告”查看结果和下载分析表。";open.Enabled=true;
            }
            catch(OperationCanceledException){status.Text="分析已取消；未发布新的报告。";}
            catch(Exception error){status.Text="分析失败；原始记录未修改。";MessageBox.Show(this,error.Message,"无法完成分析",MessageBoxButtons.OK,MessageBoxIcon.Information);}
            finally{running=false;progress.Visible=false;cancel.Enabled=false;add.Enabled=addPath.Enabled=pathEntry.Enabled=clear.Enabled=browse.Enabled=analyze.Enabled=output.Enabled=inputs.Enabled=true;cancellation.Dispose();cancellation=null;}
        }
        void OpenReport()
        {
            if(report==null)return;
            try{Process.Start(new ProcessStartInfo(report){UseShellExecute=true});}
            catch(Exception error){MessageBox.Show(this,"无法打开浏览器。报告位于：\n"+report+"\n"+error.Message,"打开报告",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        }
    }
}
