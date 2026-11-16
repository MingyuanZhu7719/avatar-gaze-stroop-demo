#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AvatarStroop.Runtime
{
    public sealed class StudyAnalysisSource
    {
        public string Path, Sha256;
        public StudyAnalysis Analysis;
        public StudyDataReadSummary Read;
    }

    public static class StudyAnalysisFiles
    {
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        public static string Run(IEnumerable<string> paths,string outputParent,Action<string> progress,CancellationToken token)
        {
            if(paths==null)throw new ArgumentNullException("paths");
            var inputs=paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(inputs.Length==0||inputs.Length>1000)throw new ArgumentException("Select between 1 and 1000 participant VRAW or CSV files.");
            var sessions=new HashSet<string>(StringComparer.Ordinal);var sources=new List<StudyAnalysisSource>();
            foreach(string path in inputs)
            {
                token.ThrowIfCancellationRequested();if(progress!=null)progress("Reading "+System.IO.Path.GetFileName(path));
                var source=new StudyAnalysisSource{Path=path,Analysis=new StudyAnalysis()};
                // FileShare.Read prevents opening an actively written recording
                // and keeps the exact bytes stable through verification+hashing.
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan))
                {
                    source.Read=StudyDataReader.Read(stream,System.IO.Path.GetExtension(path),source.Analysis.Accept,token);
                    if(source.Read.Status=="Cancelled")throw new OperationCanceledException(token);
                    if(source.Read.Status!="Complete")throw new InvalidDataException(System.IO.Path.GetFileName(path)+": "+source.Read.Message+" No report has been published. Use a completed recording/export.");
                    stream.Position=0;using(var sha=SHA256.Create())
                    {
                        var buffer=new byte[65536];int read;
                        while((read=stream.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();sha.TransformBlock(buffer,0,read,buffer,0);}
                        token.ThrowIfCancellationRequested();sha.TransformFinalBlock(new byte[0],0,0);
                        source.Sha256=BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
                    }
                }
                token.ThrowIfCancellationRequested();source.Analysis.Finish();
                if(!sessions.Add(source.Analysis.SessionId))throw new InvalidDataException("The same session was selected more than once (for example its VRAW and CSV). Select one source per session: "+source.Analysis.SessionId);
                sources.Add(source);
            }
            token.ThrowIfCancellationRequested();string parent=System.IO.Path.GetFullPath(outputParent);Directory.CreateDirectory(parent);
            string name="StroopAnalysis-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ",Inv)+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
            string target=System.IO.Path.Combine(parent,name),stage=System.IO.Path.Combine(parent,"."+name+".working");
            Directory.CreateDirectory(stage);
            try
            {
                if(progress!=null)progress("Writing report and analysis tables");
                Write(sources,stage,token);token.ThrowIfCancellationRequested();Directory.Move(stage,target);
                return System.IO.Path.Combine(target,"report.html");
            }
            catch
            {
                // Only our newly-created, uniquely-named staging directory.
                if(Directory.Exists(stage))Directory.Delete(stage,true);throw;
            }
        }

        public static void Write(IList<StudyAnalysisSource> sources,string directory,CancellationToken token)
        {
            var trials=new Table("session_id","trial_id","scope","block_index","condition","segment","response_rule","rule_provenance","word","ink","expected_response","response","congruence","correct_recomputed","rt_seconds","onset_local_seconds","response_local_seconds","response_event_local_seconds","task_clock_id","onset_basis","unity_render_seconds","unity_render_basis","planned_events","presented_events","response_events","accuracy_included","correct_rt_included","flags","list_id","retry_of");
            var blocks=new Table("session_id","scope","block_index","condition","response_rule","congruence","trial_rows","planned_trials","presented_trials","responded_trials","interrupted_trials","accuracy_n","correct_n","accuracy_proportion","correct_rt_n","mean_rt_seconds","median_rt_seconds","sd_rt_seconds","p95_rt_seconds");
            var contrasts=new Table("session_id","block_index","condition","response_rule","congruent_correct_rt_n","incongruent_correct_rt_n","incongruent_minus_congruent_median_seconds","incongruent_minus_congruent_mean_seconds");
            var ratings=new Table("session_id","block_index","condition","response_rule","item","value","language","questionnaire_version","event_local_seconds","flags");
            var eyes=new Table("session_id","block_index","condition","segment","phase","acquisition_rows","native_identity_changes","adjacent_native_duplicates","nonincreasing_native_frame_transitions","callback_time_n","callback_span_seconds","callback_receipt_observed_hz","nonincreasing_callback_times","combined_valid_n","combined_invalid_n","combined_unknown_n","calibration_state_counts","runtime_state_counts","observation_rows","aoi_eligible_observations","aoi_ineligible_observations","owner_quality_marker_counts");
            var aoi=new Table("session_id","block_index","condition","segment","phase","category","observation_n","eligible_observation_n","observation_proportion","avatar_aoi_applicability","accuracy_status");
            var provenance=new Table("session_id","source_file","source_sha256","input_format","record_count","source_terminal","chain_verified_by_analyzer","source_chain_digest","run_kind","source","design","planned_blocks","blocks_ended","session_end_reason","analyzer_version");
            var events=new Table("session_id","event_kind","count");
            foreach(var s in sources)
            {
                token.ThrowIfCancellationRequested();var a=s.Analysis;a.Finish();
                provenance.Add(a.SessionId,System.IO.Path.GetFileName(s.Path),s.Sha256,s.Read.Format,s.Read.RecordCount,s.Read.SourceTerminalObserved,s.Read.ChainVerified,s.Read.VerifiedChainSha256,a.RunKind,a.Source,a.Design,a.PlannedBlocks,a.EndedBlocks,a.SessionEndReason,StudyAnalysis.Version);
                foreach(var t in a.Trials)trials.Add(t.Session,t.Id,t.Practice?"Practice":"Formal",t.Block,t.Condition,t.Segment,t.Rule,t.RuleProvenance,t.Word,t.Ink,t.Expected,t.Response,t.Congruence,t.Correct,t.ReactionSeconds,t.Onset,t.ResponseTime,t.ResponseEventTime,t.TaskClockId,t.OnsetBasis,t.UnityRenderSeconds,t.UnityRenderBasis,t.PlannedCount,t.PresentedCount,t.ResponseCount,t.AccuracyIncluded,t.RtIncluded,String.Join(";",t.Flags),t.List,t.RetryOf);
                var summary=a.Summaries().ToArray();
                foreach(var b in summary)blocks.Add(a.SessionId,b.Scope,b.Block,b.Condition,b.Rule,b.Congruence,b.Trials,b.Planned,b.Presented,b.Responded,b.Interrupted,b.AccuracyN,b.Correct,b.Accuracy,b.RtN,b.MeanRt,b.MedianRt,b.SdRt,b.P95Rt);
                foreach(var pair in summary.Where(x=>x.Scope=="Formal").GroupBy(x=>new{x.Block,x.Condition,x.Rule}))
                {
                    var c=pair.FirstOrDefault(x=>x.Congruence=="Congruent");var i=pair.FirstOrDefault(x=>x.Congruence=="Incongruent");
                    contrasts.Add(a.SessionId,pair.Key.Block,pair.Key.Condition,pair.Key.Rule,c==null?0:c.RtN,i==null?0:i.RtN,
                        c==null||i==null?null:i.MedianRt-c.MedianRt,c==null||i==null?null:i.MeanRt-c.MeanRt);
                }
                foreach(var r in a.Ratings)ratings.Add(r.Session,r.Block,r.Condition,r.Rule,r.Item,r.Value,r.Language,r.Version,r.LocalTime,r.Flags);
                foreach(var e in a.EyeGroups)
                {
                    string condition=e.Phase=="NativeAcquisitionUnaligned"||e.Phase=="OwnerObservationUnaligned"?"Unaligned":a.Condition(e.Block);bool absent=condition=="D"||condition=="NoAvatar";
                    eyes.Add(a.SessionId,e.Block,condition,e.Segment,e.Phase,e.AcquisitionRows,e.NativeChanges,e.AdjacentDuplicates,e.NonIncreasingNativeFrames,e.CallbackTimes,e.LastCallback-e.FirstCallback,e.ObservedHz,e.NonIncreasingCallbackTimes,e.CombinedValid,e.CombinedInvalid,e.CombinedUnknown,Counts(e.Calibration),Counts(e.Runtime),e.Observations,e.AoiEligible,e.AoiMissing,Counts(e.ObservationReasons));
                    foreach(string category in new[]{"Task","Eyes","RemainingFace","Other"})
                    {
                        long n;e.Aoi.TryGetValue(category,out n);bool na=absent&&(category=="Eyes"||category=="RemainingFace");
                        aoi.Add(a.SessionId,e.Block,condition,e.Segment,e.Phase,category,na?(object)null:n,e.AoiEligible,na||e.AoiEligible==0?(double?)null:(double)n/e.AoiEligible,absent?"NOT_APPLICABLE_NO_AVATAR":"Avatar present or unknown; see condition","DEFERRED_NOT_VALIDATED");
                    }
                }
                foreach(var e in a.EventCounts.OrderBy(x=>x.Key,StringComparer.Ordinal))events.Add(a.SessionId,e.Key,e.Value);
            }
            trials.Save(directory,"trials.csv");blocks.Save(directory,"blocks.csv");contrasts.Save(directory,"contrasts.csv");ratings.Save(directory,"ratings.csv");eyes.Save(directory,"eye_quality.csv");aoi.Save(directory,"aoi_observations.csv");provenance.Save(directory,"sources.csv");events.Save(directory,"event_counts.csv");
            string methods="Avatar Stroop Analyzer "+StudyAnalysis.Version+"\r\n"+
                "Inputs are read-only. VRAW chain verification checks integrity; CSV source-chain values are declarations only. Neither establishes research eligibility. Existing recordings identify Simulation/development scope; preserve that label.\r\n"+
                "One source per session. A session is not an authenticated participant. No cross-participant inferential statistics or p-values are computed.\r\n"+
                "Block indices are zero-based and conditions follow recorded allocation. Practice is separate. Each trial ID, including interrupted attempts/retries, is retained.\r\n"+
                "Accuracy: unambiguous planned/presented/responded, uninterrupted trials with known phase, rule and colours. No response is missing, not an incorrect response. Correctness is recomputed using ink or word meaning and checked against declarations.\r\n"+
                "RT: correct accuracy-eligible trials with finite positive declared RT and task onset, consistent onset declarations and no impossible event ordering. response_local_seconds is derived from the response-declared onset plus RT; response_event_local_seconds is the later event logging time, not the input capture time. onset_basis identifies presentation- or response-declared onset. No automatic outlier trimming. Times are seconds; software onset is not a measured photon/display timestamp.\r\n"+
                "RT SD is sample SD; quantiles use linear interpolation at (n-1)*p. Interference is incongruent minus congruent within the same session/block/rule. Missing cells remain empty. No pooling of InkColor and WordMeaning.\r\n"+
                "Ratings preserve five individual 1-5 responses and questionnaire versions. Duplicates/invalid values are flagged. No unvalidated perceived-evaluation composite or causal conclusion is produced.\r\n"+
                "Eye: native acquisitions and geometry observations are separate. Native acquisitions have no task/block alignment and are reported for the whole source as NativeAcquisitionUnaligned. Native identity changes are counted sequentially; they are not globally unique after counter resets. Observed Hz=(callback_time_n-1)/(last-first callback receipt time); gaps in recording reduce this wall-span rate. Nonmonotonic callback time disables Hz. This is not sensor exposure frequency. Calibration enum 0=Unknown,1=Required,2=ReportedReady; ReportedReady does not prove accuracy.\r\n"+
                "AOI denominator: valid active-task geometry observations, including repeated native frames. These proportions are NOT dwell durations or fixation counts. Ineligible/missing geometry is separate; no interpolation. No-avatar D eye/face measures are structurally not applicable. Fine eye/face spatial accuracy remains deferred.\r\n"+
                "Before research: finalize eligibility/consent externally; pre-register RT outlier and missingness rules, compare tracking quality and interruptions by condition, verify questionnaire design/versions, and complete deferred spatial accuracy validation for fine AOI claims.\r\n"+
                "CSV cells containing formula-like text receive a leading apostrophe for spreadsheet safety. Original files remain authoritative. sources.csv records SHA-256 and provenance.\r\n";
            File.WriteAllText(System.IO.Path.Combine(directory,"methods.txt"),methods,new UTF8Encoding(true));
            File.WriteAllText(System.IO.Path.Combine(directory,"report.html"),Html(sources,methods),new UTF8Encoding(false));
        }

        static string Html(IList<StudyAnalysisSource> sources,string methods)
        {
            var h=new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Stroop 数据分析报告</title><style>body{font:16px/1.6 system-ui,sans-serif;color:#152b3d;background:#f4f7fa;margin:0}main{max-width:1200px;margin:auto;padding:40px 28px}h1{font-size:32px}h2{margin-top:36px}table{border-collapse:collapse;width:100%;font-size:14px;background:white}td,th{padding:10px 12px;text-align:left;border-bottom:1px solid #dde5eb}th{background:#e8f0f6}.card{background:white;padding:20px 24px;border-radius:12px;margin:20px 0}.note{border-left:4px solid #d99c2b;padding:14px 20px;background:#fff5df}a{color:#08659c}.muted{color:#596b78}pre{white-space:pre-wrap;overflow-wrap:anywhere}small{overflow-wrap:anywhere}.scroll{overflow:auto}</style><main><h1>Stroop 实验数据分析</h1><p class='muted'>本地描述性分析 · v");
            h.Append(StudyAnalysis.Version).Append(" · ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'",Inv)).Append("</p><div class='note'>眼动精度验收已暂缓。以下眼动结果用于检查采样与记录质量；眼睛／脸部 AOI 尚不能作为精度已验收的研究指标。</div><div class='card'><b>下载分析表：</b> ");
            foreach(string name in new[]{"trials","blocks","contrasts","ratings","eye_quality","aoi_observations","sources","event_counts"})h.Append("<a href='").Append(name).Append(".csv'>").Append(name).Append(".csv</a>　");
            h.Append("<p>保留原始文件；每次分析创建新文件夹。可将 CSV 导入 R、Python、SPSS 或 Excel。单位为秒，block 序号从 0 开始。</p></div>");
            foreach(var s in sources)
            {
                var a=s.Analysis;h.Append("<section class='card'><h2>").Append(E(System.IO.Path.GetFileName(s.Path))).Append("</h2><small>Session: ").Append(E(a.SessionId)).Append("<br>Source SHA-256: ").Append(E(s.Sha256)).Append("</small>");
                h.Append("<p>").Append(a.Trials.Count(x=>!x.Practice)).Append(" 个正式试次；").Append(a.Trials.Count(x=>x.Practice)).Append(" 个练习试次；").Append(a.Ratings.Count).Append(" 条问卷回答；").Append(a.EndedBlocks).Append(" / ").Append(a.PlannedBlocks.HasValue?a.PlannedBlocks.Value.ToString(Inv):"未知").Append(" 个 block 有完成记录。</p>");
                h.Append("<p>来源：").Append(E(a.RunKind)).Append(" / ").Append(E(a.Source)).Append("。完整性：").Append(s.Read.ChainVerified?"VRAW 校验链已验证":"CSV 已解析；原始校验链未由分析器验证").Append("。文件完整不等于实验覆盖完整。</p>");
                foreach(string warning in a.Warnings)h.Append("<p class='note'>").Append(E(warning)).Append("</p>");
                h.Append("<h3>正式试次 · 正确率与反应时</h3><p>正确率按可判定的已作答试次计算；RT 仅包含正确、未中断且时间一致的试次，无自动极端值删除。</p><div class='scroll'><table><tr><th>Block / 条件</th><th>作答规则</th><th>一致性</th><th>可判定 / 试次</th><th>正确率</th><th>正确 RT n</th><th>RT 中位数 ms</th><th>RT 均值 ms</th></tr>");
                foreach(var b in a.Summaries().Where(x=>x.Scope=="Formal"))
                    h.Append("<tr><td>").Append(b.Block).Append(" / ").Append(E(b.Condition)).Append("</td><td>").Append(E(b.Rule)).Append("</td><td>").Append(E(b.Congruence)).Append("</td><td>").Append(b.AccuracyN).Append(" / ").Append(b.Trials).Append("</td><td>").Append(Percent(b.Accuracy)).Append("</td><td>").Append(b.RtN).Append("</td><td>").Append(Num(b.MedianRt*1000)).Append("</td><td>").Append(Num(b.MeanRt*1000)).Append("</td></tr>");
                h.Append("</table></div><h3>问卷原始条目</h3><p>observed、evaluated、negative_evaluation_worry 等条目分别报告，不自动合成 perceived evaluation 总分。</p><div class='scroll'><table><tr><th>Block / 条件</th><th>条目</th><th>分值</th><th>版本</th><th>标记</th></tr>");
                foreach(var r in a.Ratings)h.Append("<tr><td>").Append(r.Block).Append(" / ").Append(E(r.Condition)).Append("</td><td>").Append(E(r.Item)).Append("</td><td>").Append(E(r.Value)).Append("</td><td>").Append(E(r.Version)).Append("</td><td>").Append(E(r.Flags)).Append("</td></tr>");
                h.Append("</table></div><h3>眼动采样与正式任务 AOI</h3><p>原生样本没有可靠的 block 对齐，因此频率按整个记录报告。AOI 按已记录的任务上下文分组；占比含重复原生样本，不代表注视时长。</p>");
                foreach(var e in a.EyeGroups.Where(x=>x.AcquisitionRows>0))h.Append("<p>原生记录：").Append(e.AcquisitionRows).Append("；回调接收频率：").Append(Num(e.ObservedHz)).Append(" Hz；SDK 联合射线有效标记：").Append(e.CombinedValid).Append(" / ").Append(e.AcquisitionRows).Append("；SDK 校准状态：").Append(E(Counts(e.Calibration))).Append("。</p>");
                h.Append("<div class='scroll'><table><tr><th>Block / 条件 / 段</th><th>AOI 有效 / 观测</th><th>白板</th><th>眼睛</th><th>其余脸部</th></tr>");
                foreach(var e in a.EyeGroups.Where(x=>x.Phase=="MainTask"))
                {
                    bool absent=a.Condition(e.Block)=="D"||a.Condition(e.Block)=="NoAvatar";
                    h.Append("<tr><td>").Append(e.Block).Append(" / ").Append(E(a.Condition(e.Block))).Append(" / ").Append(e.Segment).Append("</td><td>").Append(e.AoiEligible).Append(" / ").Append(e.Observations).Append("</td><td>").Append(AoiPercent(e,"Task")).Append("</td><td>").Append(absent?"不适用":AoiPercent(e,"Eyes")).Append("</td><td>").Append(absent?"不适用":AoiPercent(e,"RemainingFace")).Append("</td></tr>");
                }
                h.Append("</table></div></section>");
            }
            h.Append("<section class='card'><h2>用于研究前</h2><p>核对实验条件和作答模式；将练习与正式任务分开；审查中断、缺失和极端反应时；按条件比较有效眼动比例；保留问卷版本。正式推断分析仍需根据预先确定的研究设计、参与者标识和清洗规则进行。</p><details><summary>完整分析规则 / methods.txt</summary><pre>").Append(E(methods)).Append("</pre></details></section></main></html>");return h.ToString();
        }
        static string AoiPercent(StudyEyeGroup e,string key){long n;e.Aoi.TryGetValue(key,out n);return Percent(e.AoiEligible==0?(double?)null:(double)n/e.AoiEligible);}
        static string Num(double? n){return n.HasValue?n.Value.ToString("0.0",Inv):"—";}
        static string Percent(double? n){return n.HasValue?(n.Value*100).ToString("0.0",Inv)+"%":"—";}
        static string E(string value){return (value??"").Replace("&","&amp;").Replace("<","&lt;").Replace(">","&gt;").Replace("\"","&quot;").Replace("'","&#39;");}
        static string Counts(Dictionary<string,long> counts){return String.Join(";",counts.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>x.Key+":"+x.Value.ToString(Inv)));}

        sealed class Table
        {
            readonly string[] headers;readonly List<object[]> rows=new List<object[]>();
            internal Table(params string[] values){headers=values;}
            internal void Add(params object[] values){if(values.Length!=headers.Length)throw new InvalidOperationException("Analysis table width mismatch.");rows.Add(values);}
            internal void Save(string directory,string name)
            {
                using(var writer=new StreamWriter(System.IO.Path.Combine(directory,name),false,new UTF8Encoding(true)))
                {writer.WriteLine(String.Join(",",headers));foreach(var row in rows)writer.WriteLine(String.Join(",",row.Select(Cell)));}
            }
            static string Cell(object value)
            {
                if(value==null)return "";string s;
                if(value is double)s=((double)value).ToString("R",Inv);
                else if(value is bool)s=(bool)value?"true":"false";
                else s=Convert.ToString(value,Inv);
                if(value is string&&s.Length>0&&(s[0]=='='||s[0]=='+'||s[0]=='-'||s[0]=='@'||s[0]=='\t'||s[0]=='\r'||s[0]=='\n'))s="'"+s;
                return "\""+s.Replace("\"","\"\"")+"\"";
            }
        }
    }
}
#endif
