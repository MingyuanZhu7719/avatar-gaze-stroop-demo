#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
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
                if(Directory.Exists(resolved))Directory.Delete(Path.DirectorySeparatorChar=='\\'?@"\\?\"+resolved:resolved,true);
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("study reports separate tutorial exam and formal data and preserve exam result provenance",()=>{
                using(var files=new Files())
                {
                    Directory.CreateDirectory(files.Output);var analysis=StudyAnalysisTests.Analysis();
                    StudyAnalysisTrainingTests.Trial(analysis,"tutorial","tutorial",0,1,false);
                    for(int i=0;i<10;i++)StudyAnalysisTrainingTests.Trial(analysis,"exam"+i,"practice_exam",1,1,i<8);
                    StudyAnalysisTrainingTests.Completion(analysis,1,8);
                    StudyAnalysisTrainingTests.Trial(analysis,"formal","formal",1,1);
                    var source=new StudyAnalysisSource{Path=files.Raw,Analysis=analysis,Sha256="synthetic",Read=new StudyDataReadSummary{Format="synthetic",Status="Complete"}};
                    StudyAnalysisFiles.Write(new[]{source},files.Output,CancellationToken.None);
                    string trials=File.ReadAllText(Path.Combine(files.Output,"trials.csv"));
                    Check.True(trials.Contains("training_protocol,learning_stage,practice_attempt,tutorial_round")&&trials.Contains("\"Tutorial\"")&&trials.Contains("\"PracticeExam\""));
                    string training=File.ReadAllText(Path.Combine(files.Output,"training_attempts.csv"));
                    Check.Equal(2,File.ReadAllLines(Path.Combine(files.Output,"training_attempts.csv")).Length);
                    Check.True(training.Contains("reconstructed_correct")&&training.Contains("\"tutorial-exam-v1\"")&&training.Contains("\"explicit\",\"true\""));
                    Check.Equal(2,File.ReadAllLines(Path.Combine(files.Output,"contrasts.csv")).Length);
                    string html=File.ReadAllText(Path.Combine(files.Output,"report.html"));
                    Check.True(html.Contains("training_attempts.csv")&&html.Contains("1 个正式试次；1 个教学试次；10 个考试／旧版练习试次"));
                }
            });
            suite.Run("full visual search VRAW4 and CSV preserve 104 trials layouts timeouts and identical analysis tables",()=>{
                using(var files=new Files())
                {
                    byte[] bytes=StudyVisualSearchRecordingFixture.FourBlocks();File.WriteAllBytes(files.Raw,bytes);
                    using(var source=new MemoryStream(bytes,false))
                    {
                        var projected=ViveRawSessionJournalReader.ProjectTrials(source,true,true,4194304,5000,CancellationToken.None);
                        Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,projected.Status);Check.True(projected.CaptureTerminalObserved);
                        Check.Equal(104,projected.TrialProjection.Trials.Count);Check.Equal(5,projected.TrialProjection.PreparedPlans.Count);
                        Check.True(projected.TrialProjection.Trials.All(x=>!x.EventConflict&&!x.IncompleteEvidence&&x.PlannedStimulusState=="DECLARED_VISUAL_SEARCH_V1"));
                        Check.Equal(1,projected.TrialProjection.Trials.Sum(x=>x.TimeoutEvents));
                    }
                    using(var source=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                    {
                        Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(source,output,4194304,5000,33554432,CancellationToken.None));
                        File.WriteAllBytes(files.Csv,output.ToArray());
                    }
                    var analysis=new StudyAnalysis();using(var source=new MemoryStream(bytes,false))Check.Equal("Complete",StudyDataReader.Read(source,"vraw",analysis.Accept,CancellationToken.None).Status);
                    analysis.Finish();Check.Equal(104,analysis.Trials.Count);Check.Equal(20,analysis.Ratings.Count);Check.Equal(1872,analysis.SearchItems.Count);
                    Check.Equal(208,analysis.SearchEvents.Count);Check.Equal(104,analysis.SearchEvents.Count(x=>x.Action=="hover"));
                    var cues=analysis.SearchEvents.Where(x=>x.Action=="cue").ToArray();Check.Equal(104,cues.Length);Check.Equal(104,cues.Select(x=>x.Trial).Distinct().Count());
                    Check.True(cues.All(x=>x.Item==null&&x.CueFirstRenderUnitySeconds.HasValue&&x.CueRenderedElapsedSeconds==VisualSearchTrial.TargetPreviewSeconds&&x.CueBasis=="software_camera_callbacks_not_photon"&&x.Flags==""));
                    Check.True(analysis.Trials.All(x=>x.AccuracyIncluded));Check.Equal(102,analysis.Trials.Count(x=>x.RtIncluded));
                    Check.Equal(1,analysis.Trials.Count(x=>x.TimedOut));Check.Equal(1,analysis.Trials.Count(x=>!x.TimedOut&&x.Correct==false));Check.Equal(8,analysis.Summaries().Count(x=>x.Scope=="Formal"));
                    string raw=Path.GetDirectoryName(StudyAnalysisFiles.Run(new[]{files.Raw},files.Output,null,CancellationToken.None));
                    string csv=Path.GetDirectoryName(StudyAnalysisFiles.Run(new[]{files.Csv},files.Output,null,CancellationToken.None));
                    foreach(string table in new[]{"trials.csv","blocks.csv","contrasts.csv","visual_search_items.csv","visual_search_events.csv","ratings.csv","eye_quality.csv","aoi_observations.csv","event_counts.csv"})
                        Check.Equal(File.ReadAllText(Path.Combine(raw,table)),File.ReadAllText(Path.Combine(csv,table)),table);
                    Check.Equal(1,File.ReadAllLines(Path.Combine(raw,"contrasts.csv")).Length);
                    using(var source=new MemoryStream(StudyVisualSearchRecordingFixture.FourBlocks(true),false))
                        Check.Equal(ViveRawSessionReadStatus.Invalid,ViveRawSessionJournalReader.ProjectTrials(source,true,true,4194304,5000,CancellationToken.None).Status);
                }
            });
            suite.Run("study visual search output preserves layouts and pointer events without Stroop contrasts",()=>{
                using(var files=new Files())
                {
                    Directory.CreateDirectory(files.Output);var analysis=StudyAnalysisTests.Analysis();
                    StudyAnalysisTests.SearchTrial(analysis,"twelve",12);StudyAnalysisTests.SearchTrial(analysis,"twentyfour",24);
                    StudyAnalysisTests.SearchCue(analysis,"twelve");
                    var source=new StudyAnalysisSource{Path=files.Raw,Analysis=analysis,Sha256="synthetic",Read=new StudyDataReadSummary{Format="synthetic",Status="Complete"}};
                    StudyAnalysisFiles.Write(new[]{source},files.Output,CancellationToken.None);
                    Check.Equal(1,File.ReadAllLines(Path.Combine(files.Output,"contrasts.csv")).Length);
                    Check.Equal(37,File.ReadAllLines(Path.Combine(files.Output,"visual_search_items.csv")).Length);
                    string searchEvents=File.ReadAllText(Path.Combine(files.Output,"visual_search_events.csv"));
                    Check.Equal(4,File.ReadAllLines(Path.Combine(files.Output,"visual_search_events.csv")).Length);
                    Check.True(searchEvents.Contains("cue_first_render_unity_seconds")&&searchEvents.Contains("cue_rendered_elapsed_seconds")&&searchEvents.Contains("\"cue\"")&&searchEvents.Contains("software_camera_callbacks_not_photon"));
                    string trials=File.ReadAllText(Path.Combine(files.Output,"trials.csv"));
                    Check.True(trials.Contains("search_selection_seconds")&&trials.Contains("\"VisualSearch\"")&&trials.Contains("\"SelectTarget\""));
                    Check.True(File.ReadAllText(Path.Combine(files.Output,"methods.txt")).Contains("not pure visual-search latency"));
                }
            });
            suite.Run("study analysis opens long input paths without copying or changing source identity",()=>{
                using(var files=new Files())
                {
                    string directory=files.Root;
                    while(directory.Length<280)directory=Path.Combine(directory,"long-input-"+new string('x',48));
                    string path=Path.Combine(directory,"original-participant-recording.csv");
                    Func<string,string> native=value=>Path.DirectorySeparatorChar=='\\'?@"\\?\"+value:value;
                    Directory.CreateDirectory(native(directory));byte[] original=File.ReadAllBytes(files.Csv);
                    File.WriteAllBytes(native(path),original);Check.True(path.Length>260);
                    string report=StudyAnalysisFiles.Run(new[]{path},files.Output,null,CancellationToken.None);
                    string provenance=File.ReadAllText(Path.Combine(Path.GetDirectoryName(report),"sources.csv"));
                    Check.True(provenance.Contains("original-participant-recording.csv"));
                    Check.True(original.SequenceEqual(File.ReadAllBytes(native(path))));
                    string baseline=StudyAnalysisFiles.Run(new[]{files.Csv},files.Output,null,CancellationToken.None);
                    foreach(string table in new[]{"trials.csv","blocks.csv","contrasts.csv","ratings.csv","eye_quality.csv","aoi_observations.csv","event_counts.csv"})
                        Check.Equal(File.ReadAllText(Path.Combine(Path.GetDirectoryName(baseline),table)),File.ReadAllText(Path.Combine(Path.GetDirectoryName(report),table)),table);
                    if(Path.DirectorySeparatorChar=='\\')
                    {
                        int before=Directory.GetDirectories(files.Output).Length;
                        using(var writer=new FileStream(native(path),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                            Check.Throws<IOException>(()=>StudyAnalysisFiles.Run(new[]{path},files.Output,null,CancellationToken.None));
                        Check.Equal(before,Directory.GetDirectories(files.Output).Length);
                    }
                }
            });
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
