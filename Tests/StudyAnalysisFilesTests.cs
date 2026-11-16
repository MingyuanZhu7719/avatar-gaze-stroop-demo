#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class StudyAnalysisFilesTests
    {
        sealed class Files:IDisposable
        {
            internal readonly string Root,Raw,Csv,Output;
            readonly string parent;
            internal Files()
            {
                parent=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"build","study-analysis-file-tests"));
                Root=Path.Combine(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Root);
                Raw=Path.Combine(Root,"synthetic.vraw");Csv=Path.Combine(Root,"synthetic.csv");Output=Path.Combine(Root,"output");
                byte[] bytes=ViveRawTrialProjectionTests.FourBlocks();File.WriteAllBytes(Raw,bytes);
                using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                {
                    Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,
                        ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,CancellationToken.None));
                    File.WriteAllBytes(Csv,output.ToArray());
                }
            }
            public void Dispose()
            {
                string resolved=Path.GetFullPath(Root);
                if(!resolved.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Path.GetDirectoryName(resolved)!=parent)
                    throw new InvalidOperationException("Synthetic cleanup target escaped its test workspace.");
                if(Directory.Exists(resolved))Directory.Delete(resolved,true);
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("study file reports preserve sources and yield identical VRAW CSV analysis tables",()=>{
                using(var files=new Files())
                {
                    byte[] original=File.ReadAllBytes(files.Raw);long csvBytes=new FileInfo(files.Csv).Length;
                    string rawReport=StudyAnalysisFiles.Run(new[]{files.Raw},files.Output,null,CancellationToken.None);
                    string csvReport=StudyAnalysisFiles.Run(new[]{files.Csv},files.Output,null,CancellationToken.None);
                    Check.True(File.Exists(rawReport)&&File.Exists(csvReport)&&rawReport!=csvReport);
                    foreach(string table in new[]{"trials.csv","blocks.csv","contrasts.csv","ratings.csv","eye_quality.csv","aoi_observations.csv","event_counts.csv"})
                        Check.Equal(File.ReadAllText(Path.Combine(Path.GetDirectoryName(rawReport),table)),File.ReadAllText(Path.Combine(Path.GetDirectoryName(csvReport),table)),table);
                    Check.True(!File.ReadAllText(Path.Combine(Path.GetDirectoryName(rawReport),"sources.csv")).Equals(File.ReadAllText(Path.Combine(Path.GetDirectoryName(csvReport),"sources.csv"))));
                    Check.True(original.SequenceEqual(File.ReadAllBytes(files.Raw)));Check.Equal(csvBytes,new FileInfo(files.Csv).Length);
                    Check.Equal(0,Directory.GetDirectories(files.Output,"*.working").Length);
                }
            });
            suite.Run("study file batch rejects duplicate sessions without publishing output",()=>{
                using(var files=new Files())
                {
                    Check.Throws<InvalidDataException>(()=>StudyAnalysisFiles.Run(new[]{files.Raw,files.Csv},files.Output,null,CancellationToken.None));
                    Check.True(!Directory.Exists(files.Output));
                }
            });
            suite.Run("study file failures incomplete inputs and cancellation leave no published stage",()=>{
                using(var files=new Files())
                {
                    byte[] bad=File.ReadAllBytes(files.Raw);bad[bad.Length-1]^=1;string invalid=Path.Combine(files.Root,"invalid.vraw");File.WriteAllBytes(invalid,bad);
                    Check.Throws<InvalidDataException>(()=>StudyAnalysisFiles.Run(new[]{files.Raw,invalid},files.Output,null,CancellationToken.None));Check.True(!Directory.Exists(files.Output));
                    string partial=Path.Combine(files.Root,"partial.vraw");File.WriteAllBytes(partial,ViveRawTrialProjectionTests.FourBlocks(truncateTerminal:true));
                    Check.Throws<InvalidDataException>(()=>StudyAnalysisFiles.Run(new[]{partial},files.Output,null,CancellationToken.None));Check.True(!Directory.Exists(files.Output));
                    using(var cancel=new CancellationTokenSource())
                    {cancel.Cancel();Check.Throws<OperationCanceledException>(()=>StudyAnalysisFiles.Run(new[]{files.Raw},files.Output,null,cancel.Token));Check.True(!Directory.Exists(files.Output));}
                    // Fail after private staging has begun. A progress callback
                    // cancels before Write's first source, exercising cleanup.
                    using(var cancel=new CancellationTokenSource())
                    {
                        Check.Throws<OperationCanceledException>(()=>StudyAnalysisFiles.Run(new[]{files.Raw},files.Output,
                            text=>{if(text=="Writing report and analysis tables")cancel.Cancel();},cancel.Token));
                        Check.Equal(0,Directory.GetFileSystemEntries(files.Output).Length);
                    }
                }
            });
            suite.Run("study output escapes untrusted text in report HTML and spreadsheet cells",()=>{
                using(var files=new Files())
                {
                    Directory.CreateDirectory(files.Output);var analysis=new StudyAnalysis();
                    analysis.Accept(new StudyDataRecord{Kind="manifest",Fields=new Dictionary<string,string>{{"session_id","<script>alert('x')</script>"},{"source","=SUM(1,2)"}}});
                    var source=new StudyAnalysisSource{Path=files.Raw,Analysis=analysis,Sha256="synthetic",Read=new StudyDataReadSummary{Format="csv",Status="Complete"}};
                    StudyAnalysisFiles.Write(new[]{source},files.Output,CancellationToken.None);
                    string html=File.ReadAllText(Path.Combine(files.Output,"report.html"));
                    Check.True(!html.Contains("<script>")&&html.Contains("&lt;script&gt;"));
                    Check.True(File.ReadAllText(Path.Combine(files.Output,"sources.csv")).Contains("\"'=SUM(1,2)\""));
                }
            });
        }
    }
}
#endif
