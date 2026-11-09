using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic Experiment events and in-memory streams only; no file export.
    public static class ViveRawTrialReportTests
    {
        static ViveRawSessionReadResult Project(byte[] data)
        {using(var stream=new MemoryStream(data,false))return ViveRawSessionJournalReader.ProjectTrials(stream,true,true,4194304,5000,CancellationToken.None);}
        static int Lines(string text){return text.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries).Length;}
        static void Width(string text,int expected)
        {foreach(string line in text.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries))Check.Equal(expected,line.Split(',').Length);}
        static byte[] NullFieldFixture(string kind,string key,bool legacy=false,string trial=null)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"geometry-fixture");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"plan-fixture",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"intent-fixture",true,true);
            var m=new ViveNamedSessionFrameAdmission.Manifest("session-fixture","clock-fixture",plan,intent,true);m.SealChoiceTime(0,-1);
            var fields=new Dictionary<string,string>{{"named_schema","vive-named-recording-intent-v3"},{"named_plan_id",plan.PlanId},{"named_plan_schema","vive-named-simulation-plan-v2"},
                {"named_recording_intent_id",intent.IntentId},{"named_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT"},{"named_task_recording_intent","REQUESTED"},{"named_eye_recording_intent","REQUESTED"},
                {"named_block_index","0"},{"named_option","Automatic"},{"named_list_index","1"},{"named_schedule",option.ScheduleVersion},{"named_geometry",option.GeometryVersion}};
            fields.Add(key,null);
            var phase=new JournalEvent(0,1,"clock-fixture","session-fixture",0,0,trial,kind,fields,null,plan,plan.Blocks[0],intent);
            using(var output=new MemoryStream())
            {
                byte[] prior=new byte[32],digest;var manifest=legacy?ViveRawSessionJournalCodec.Frame(ViveRawSessionJournalCodec.ManifestPayload(m),prior,true,out digest):ViveRawSessionJournalCodec.FrameV3(ViveRawSessionJournalCodec.ManifestPayloadV3(m),prior,true,out digest);
                output.Write(manifest,0,manifest.Length);prior=digest;
                var payload=ViveRawSessionJournalCodec.RecordPayload(1,phase,m);
                var frame=legacy?ViveRawSessionJournalCodec.Frame(payload,prior,false,out digest):ViveRawSessionJournalCodec.FrameV3(payload,prior,false,out digest);
                output.Write(frame,0,frame.Length);return output.ToArray();
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VRAW in-memory trial report shows four synthetic blocks and unknown validity",()=>{
                var source=Project(ViveRawTrialProjectionTests.FourBlocks());ViveRawTrialReport report;
                Check.True(ViveRawTrialReport.TryBuild(source,true,out report));Check.True(!report.Saved&&!report.CompleteSessionProven&&!report.CurrentAccessAuthorized);
                Check.Equal(5,Lines(report.BlocksCsv));Check.Equal(105,Lines(report.TrialsCsv));Check.Equal(21,Lines(report.RatingsCsv));Check.Equal(1,Lines(report.StatusesCsv));
                Width(report.BlocksCsv,5);Width(report.TrialsCsv,47);Width(report.RatingsCsv,8);Width(report.StatusesCsv,11);Width(report.SafetyEventsCsv,17);Width(report.PreparedStimuliCsv,13);
                Check.True(report.ManifestCsv.Contains("scope,state:selected_verified_prefix_only\n")&&report.ManifestCsv.Contains("sampling_coverage,state:unknown\n")&&
                    report.ManifestCsv.Contains("current_access_authorized,state:unknown\n")&&report.ManifestCsv.Contains("samples_joined_to_trials,bool:false\n")&&
                    report.ManifestCsv.Contains("raw_eye_sample_rows_included,bool:false\n")&&report.ManifestCsv.Contains("all_task_event_fields_included,bool:false\n")&&
                    report.ManifestCsv.Contains("trial_open_flush_evidence,state:unknown\n")&&report.ManifestCsv.Contains("render_proxy_evidence,state:unknown\n"));
                foreach(var line in report.TrialsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries))
                    if(!line.StartsWith("trial_id,",StringComparison.Ordinal)){var row=line.Split(',');Check.Equal("state:unknown",row[37]);Check.Equal("state:unknown",row[38]);}
                var first=source.TrialProjection.Trials[0];var cells=report.TrialsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries)[1].Split(',');
                Check.Equal("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(first.TrialId)),cells[0]);
                Check.Equal("i32:"+first.Block.ToString(CultureInfo.InvariantCulture),cells[1]);Check.Equal("state:declared_v1",cells[20]);
                Check.Equal("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(first.PlannedWord)),cells[18]);
                Check.Equal("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(first.Word)),cells[23]);
                Check.Equal("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(first.Response)),cells[25]);
                var rating=report.RatingsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries)[1].Split(',');Check.Equal("b64:MQ==",rating[4]);
                Check.True(report.SafetyEventsCsv.Contains("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes("trial_interrupted")))&&
                    report.SafetyEventsCsv.Contains("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes("session_ended"))));
                Check.True(report.ManifestCsv.Contains("phase_transitions,state:declared_events_in_selected_prefix_only\n"));
                var phases=source.TrialProjection.ObservedSafetyEvents;bool sawPhase=false;
                foreach(var phase in phases)if(phase.Kind=="phase_changed")
                {
                    sawPhase=true;string from=!phase.PhaseFromPresent?"state:missing":phase.PhaseFrom==null?"state:null":"b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(phase.PhaseFrom));
                    string to=!phase.PhaseToPresent?"state:missing":phase.PhaseTo==null?"state:null":"b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(phase.PhaseTo));
                    Check.True(report.SafetyEventsCsv.Contains(","+from+","+to+",state:missing,state:missing\n"));
                }
                Check.True(sawPhase,"Expected declared phase events in four-block fixture");
            });
            suite.Run("VRAW phase declarations preserve missing and untrusted fields without inferred transitions",()=>{
                var acc=new ViveRawTrialAccumulator();acc.SetManifest("session","clock","plan",new[]{0},new[]{1},new[]{"schedule"},new[]{"geometry"});
                acc.Observe("phase_changed",null,0,0,1,1,new Dictionary<string,string>{{"from","=untrusted,\nvalue"}});
                acc.Observe("explicit_pause",null,0,0,2,2,new Dictionary<string,string>{{"from","ignored"},{"to","ignored"}});
                var p=acc.Finish();Check.Equal(2,p.ObservedSafetyEvents.Count);
                Check.Equal("=untrusted,\nvalue",p.ObservedSafetyEvents[0].PhaseFrom);Check.Equal((string)null,p.ObservedSafetyEvents[0].PhaseTo);
                Check.Equal((string)null,p.ObservedSafetyEvents[1].PhaseFrom);Check.Equal((string)null,p.ObservedSafetyEvents[1].PhaseTo);
                var source=new ViveRawSessionReadResult{Status=ViveRawSessionReadStatus.VerifiedPrefix,TrialProjection=p};ViveRawTrialReport report;
                Check.True(ViveRawTrialReport.TryBuild(source,true,out report));Width(report.SafetyEventsCsv,17);
                Check.True(!report.SafetyEventsCsv.Contains("=untrusted")&&report.SafetyEventsCsv.Contains("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes("=untrusted,\nvalue"))+",state:missing,state:missing,state:missing\n"));
                Check.True(report.ManifestCsv.Contains("complete_session_proven,bool:false\n")&&report.ManifestCsv.Contains("current_withdrawal_status,state:unknown\n"));
            });
            suite.Run("VRAW hashed phase declaration distinguishes null from absent",()=>{
                var source=Project(NullFieldFixture("phase_changed","from"));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,source.Status);
                var phase=source.TrialProjection.ObservedSafetyEvents[0];Check.True(phase.PhaseFromPresent&&!phase.PhaseToPresent);
                Check.Equal((string)null,phase.PhaseFrom);Check.Equal((string)null,phase.PhaseTo);
                ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(source,true,out report));
                Check.True(report.SafetyEventsCsv.Contains(",state:null,state:missing,state:missing,state:missing\n"));
                Check.True(!source.CaptureTerminalObserved&&!report.CompleteSessionProven);
            });
            suite.Run("VRAW report retains task and questionnaire duration declarations in four-block prefix",()=>{
                var source=Project(ViveRawTrialProjectionTests.FourBlocks());ViveRawTrialReport report;
                Check.True(ViveRawTrialReport.TryBuild(source,true,out report));int blocks=0,questionnaires=0,rests=0;
                foreach(var item in source.TrialProjection.ObservedSafetyEvents)
                {
                    if(item.Kind=="block_task_ended"){blocks++;Check.True(item.ActiveSecondsPresent&&item.ActiveSeconds!=null);}
                    if(item.Kind=="questionnaire_completed"){questionnaires++;Check.True(item.DurationSecondsPresent&&item.DurationSeconds!=null);}
                    if(item.Kind=="rest_ready"){rests++;Check.True(item.RestSecondsPresent&&item.RestSeconds!=null);}
                }
                Check.Equal(4,blocks);Check.Equal(4,questionnaires);Check.Equal(3,rests);
                Check.True(report.ManifestCsv.Contains("duration_fields,state:declared_strings_not_measurement_or_coverage\n"));
                Check.True(report.SafetyEventsCsv.Contains("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes("questionnaire_completed"))));
                Check.True(report.ManifestCsv.Contains("sampling_coverage,state:unknown\n")&&!report.CompleteSessionProven);
            });
            suite.Run("VRAW duration declarations preserve null missing and untrusted bytes",()=>{
                var acc=new ViveRawTrialAccumulator();acc.SetManifest("session","clock","plan",new[]{0},new[]{1},new[]{"schedule"},new[]{"geometry"});
                acc.Observe("questionnaire_completed",null,0,0,1,1,new Dictionary<string,string>{{"duration_seconds",null}});
                acc.Observe("rest_ready",null,0,0,2,2,new Dictionary<string,string>{{"rest_seconds","=bad,\nvalue"}});
                acc.Observe("block_task_ended",null,0,0,3,3,new Dictionary<string,string>());
                var source=new ViveRawSessionReadResult{Status=ViveRawSessionReadStatus.VerifiedPrefix,TrialProjection=acc.Finish()};ViveRawTrialReport report;
                Check.True(ViveRawTrialReport.TryBuild(source,true,out report));var rows=report.SafetyEventsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);
                var q=rows[1].Split(',');var r=rows[2].Split(',');var b=rows[3].Split(',');
                Check.Equal("state:null",q[15]);Check.Equal("state:missing",q[16]);
                Check.Equal("state:missing",r[15]);Check.Equal("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes("=bad,\nvalue")),r[16]);
                Check.Equal("state:missing",b[12]);Check.True(!report.SafetyEventsCsv.Contains("=bad"));
            });
            suite.Run("VRAW hashed duration declaration distinguishes null from absent",()=>{
                var source=Project(NullFieldFixture("questionnaire_completed","duration_seconds"));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,source.Status);
                var item=source.TrialProjection.ObservedSafetyEvents[0];Check.True(item.DurationSecondsPresent&&!item.RestSecondsPresent);Check.Equal((string)null,item.DurationSeconds);
                ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(source,true,out report));
                Check.True(report.SafetyEventsCsv.Contains(",state:null,state:missing\n"));Check.True(!report.CompleteSessionProven);
            });
            suite.Run("VRAW2 and VRAW3 safety fields distinguish explicit null from missing",()=>{
                string[] keys={"actor","code","reason","detail","onset_available","paused_seconds"};
                string[] kinds={"headset_removed","technical_issue","trial_interrupted","technical_issue","trial_interrupted","explicit_resume"};
                foreach(bool legacy in new[]{true,false})for(int i=0;i<keys.Length;i++)
                {
                    var source=Project(NullFieldFixture(kinds[i],keys[i],legacy));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,source.Status);
                    ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(source,true,out report));
                    var rows=report.SafetyEventsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);Check.Equal(2,rows.Length);
                    var row=rows[1].Split(',');Check.Equal(17,row.Length);
                    for(int j=0;j<keys.Length;j++)Check.Equal(j==i?"state:null":"state:missing",row[6+j]);
                    Check.True(!report.Saved&&!report.CompleteSessionProven&&!report.CurrentAccessAuthorized);
                }
            });
            suite.Run("VRAW safety report encodes untrusted field values without spreadsheet formulas",()=>{
                var acc=new ViveRawTrialAccumulator();acc.SetManifest("session","clock","plan",new[]{0},new[]{1},new[]{"schedule"},new[]{"geometry"});
                string[] keys={"actor","code","reason","detail","onset_available","paused_seconds"};var fields=new Dictionary<string,string>();
                foreach(var key in keys)fields.Add(key,"=bad,\nvalue");acc.Observe("technical_issue",null,0,0,1,1,fields);
                var source=new ViveRawSessionReadResult{Status=ViveRawSessionReadStatus.VerifiedPrefix,TrialProjection=acc.Finish()};ViveRawTrialReport report;
                Check.True(ViveRawTrialReport.TryBuild(source,true,out report));var row=report.SafetyEventsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries)[1].Split(',');
                string expected="b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes("=bad,\nvalue"));for(int i=0;i<keys.Length;i++)Check.Equal(expected,row[6+i]);
                Check.True(!report.SafetyEventsCsv.Contains("=bad"));
            });
            suite.Run("VRAW interruption nulls match between safety and trial reports",()=>{
                string[] keys={"actor","reason","onset_available"};int[] safetyColumns={6,8,10};
                foreach(bool legacy in new[]{true,false})for(int i=0;i<keys.Length;i++)
                {
                    var source=Project(NullFieldFixture("trial_interrupted",keys[i],legacy,"trial"));
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,source.Status);ViveRawTrialReport report;
                    Check.True(ViveRawTrialReport.TryBuild(source,true,out report));
                    var safety=report.SafetyEventsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries)[1].Split(',');
                    var trial=report.TrialsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries)[1].Split(',');
                    for(int j=0;j<keys.Length;j++)
                    {string expected=i==j?"state:null":"state:missing";Check.Equal(expected,safety[safetyColumns[j]]);Check.Equal(expected,trial[29+j]);}
                    Check.Equal("state:unknown",trial[37]);Check.True(!report.Saved&&!report.CompleteSessionProven);
                }
            });
            suite.Run("VRAW phase declarations share the bounded event budget",()=>{
                var acc=new ViveRawTrialAccumulator();var fields=new Dictionary<string,string>{{"from","Practice"},{"to","Paused"}};
                for(int i=0;i<2048;i++)acc.Observe("phase_changed",null,0,0,i,i,fields);
                Check.Throws<ViveRawTrialAccumulator.ProjectionLimit>(()=>acc.Observe("phase_changed",null,0,0,2048,2048,fields));
            });
            suite.Run("VRAW in-memory report preserves local status and distinguishes missing values",()=>{
                var source=Project(ViveRawSessionTerminalTests.Fixture(withStatus:true));ViveRawTrialReport report;
                Check.True(ViveRawTrialReport.TryBuild(source,true,out report));Check.Equal(3,Lines(report.StatusesCsv));
                Check.True(report.StatusesCsv.Contains(",u64:1,state:missing,state:missing,state:missing,")&&report.ManifestCsv.Contains("eye_statuses,i64:2\n"));
            });
            suite.Run("VRAW report never backfills missing plan stimulus from later presentation",()=>{
                var source=Project(ViveRawTrialProjectionTests.FourBlocks(omitFirstPlanned:true));ViveRawTrialReport report;
                Check.True(ViveRawTrialReport.TryBuild(source,true,out report));var cells=report.TrialsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries)[1].Split(',');
                Check.Equal("state:missing",cells[17]);Check.Equal("state:missing",cells[18]);Check.Equal("state:missing",cells[19]);
                Check.Equal("state:unknown",cells[20]);Check.True(cells[23].StartsWith("b64:",StringComparison.Ordinal));
                Check.Equal("bool:true",cells[33]);Check.Equal("state:unknown",cells[34]);Check.Equal("state:unknown",cells[37]);
            });
            suite.Run("VRAW in-memory report requires action and withholds invalid or withdrawn sources",()=>{
                var valid=Project(ViveRawTrialProjectionTests.FourBlocks());ViveRawTrialReport report;
                Check.True(!ViveRawTrialReport.TryBuild(valid,false,out report)&&report==null);
                var withdrawn=Project(ViveRawSessionTerminalTests.Fixture(reason:"Withdrawn"));
                Check.True(!ViveRawTrialReport.TryBuild(withdrawn,true,out report)&&report==null);
                byte[] bytes=ViveRawTrialProjectionTests.FourBlocks();bytes[bytes.Length-1]^=1;
                Check.True(!ViveRawTrialReport.TryBuild(Project(bytes),true,out report)&&report==null);
            });
            suite.Run("VRAW report encodes attacker-controlled text instead of emitting CSV formulas or newlines",()=>{
                var acc=new ViveRawTrialAccumulator();acc.SetManifest("=unsafe,session\nnext","clock","plan",new[]{0},new[]{1},new[]{"schedule"},new[]{"geometry"});
                var fields=new Dictionary<string,string>{{"list","=danger,\nnext"},{"planned_index","0"}};
                acc.Observe("trial_planned","+trial,\nnext",0,0,0,1,fields);
                var source=new ViveRawSessionReadResult{Status=ViveRawSessionReadStatus.VerifiedPrefix,TrialProjection=acc.Finish()};ViveRawTrialReport report;
                Check.True(ViveRawTrialReport.TryBuild(source,true,out report));Check.True(!report.ManifestCsv.Contains("=unsafe")&&!report.TrialsCsv.Contains("=danger")&&!report.TrialsCsv.Contains("+trial"));
                Check.True(report.TrialsCsv.Contains("b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes("+trial,\nnext"))));Width(report.TrialsCsv,47);
            });
        }
    }
}
