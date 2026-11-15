using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic declarations and hashed memory streams only. These tests do
    // not certify the truth of a focus report, rendering or physical tracking.
    public static class ViveFocusProjectionTests
    {
        static string Cell(string text)
        {return "b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(text));}
        static Dictionary<string,string> Context(ViveNamedSimulationPlan plan,ViveNamedRecordingIntent intent,params string[] values)
        {
            var option=plan.Blocks[0].Selection;
            var fields=new Dictionary<string,string>{{"named_schema","vive-named-recording-intent-v3"},{"named_plan_id",plan.PlanId},
                {"named_plan_schema","vive-named-simulation-plan-v2"},{"named_recording_intent_id",intent.IntentId},
                {"named_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT"},{"named_task_recording_intent","REQUESTED"},{"named_eye_recording_intent","REQUESTED"},
                {"named_block_index","0"},{"named_option","Automatic"},{"named_list_index","1"},{"named_schedule",option.ScheduleVersion},{"named_geometry",option.GeometryVersion}};
            for(int i=0;i<values.Length;i+=2)fields.Add(values[i],values[i+1]);
            return fields;
        }
        static byte[] Prefix(bool v3,bool withResumedResponse=false)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"focus-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"focus-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"focus-intent",true,true);
            var manifest=new ViveNamedSessionFrameAdmission.Manifest("focus-session","focus-clock",plan,intent,true);manifest.SealChoiceTime(0,-1);
            var events=new List<JournalEvent>();
            Action<string,string,Dictionary<string,string>> add=(kind,trial,fields)=>events.Add(
                new JournalEvent(events.Count,events.Count+1,"focus-clock","focus-session",0,0,trial,kind,fields,null,plan,plan.Blocks[0],intent));
            add("focus_changed",null,Context(plan,intent,"actor","Owner","code","focus_state","reason","focus_baseline", "focus_schema","1",
                "desktop_focused","True","xr_focused","True","detail","desktop_focused=True;xr_focused=True;task_input=available;sampling=physical_validity_required",
                "source","owner_window_and_openxr_poll","measurement_quality","NOT_ESTABLISHED"));
            add("trial_planned","focus-trial",Context(plan,intent,"list","L1","planned_index","0","planned_stimulus_schema","1","planned_word","Red","planned_ink","Blue"));
            add("stimulus_presented","focus-trial",Context(plan,intent,"list","L1","planned_index","0","word","Red","ink","Blue"));
            add("focus_changed","focus-trial",Context(plan,intent,"actor","Owner","code","focus_state","reason","focus_lost","focus_schema","1",
                "desktop_focused","True","xr_focused","False","detail","desktop_focused=True;xr_focused=False;task_input=blocked;sampling=physical_validity_required",
                "source","owner_window_and_openxr_poll","measurement_quality","NOT_ESTABLISHED"));
            add("trial_interrupted","focus-trial",Context(plan,intent,"actor","Owner","reason","xr_focus_lost","onset_available","True"));
            add("focus_pause","focus-trial",Context(plan,intent,"actor","Owner","code","focus_hold","reason","xr_focus_lost",
                "detail","Raw capture continues; task input waits for focus and explicit resume."));
            add("focus_changed","focus-trial",Context(plan,intent,"actor","Owner","code","focus_state","reason","focus_restored","focus_schema","1",
                "desktop_focused","True","xr_focused","True","detail","desktop_focused=True;xr_focused=True;task_input=waiting_for_explicit_resume;sampling=physical_validity_required",
                "source","owner_window_and_openxr_poll","measurement_quality","NOT_ESTABLISHED"));
            if(withResumedResponse)
            {
                add("explicit_resume","focus-trial",Context(plan,intent,"paused_seconds","1.25"));
                events.Add(new JournalEvent(events.Count,9,"focus-clock","focus-session",0,1,"resumed-trial","trial_planned",
                    Context(plan,intent,"list","L1","planned_index","1","planned_stimulus_schema","1","planned_word","Green","planned_ink","Yellow"),null,plan,plan.Blocks[0],intent));
                events.Add(new JournalEvent(events.Count,10,"focus-clock","focus-session",0,1,"resumed-trial","stimulus_presented",
                    Context(plan,intent,"list","L1","planned_index","1","word","Green","ink","Yellow"),null,plan,plan.Blocks[0],intent));
                events.Add(new JournalEvent(events.Count,10.25,"focus-clock","focus-session",0,1,"resumed-trial","first_response",
                    Context(plan,intent,"response","Yellow","correct","True","onset","10","rt_seconds",".25"),null,plan,plan.Blocks[0],intent));
            }
            using(var output=new MemoryStream())
            {
                byte[] prior=new byte[32];
                Action<byte[],bool> append=(payload,first)=>{
                    byte[] digest;
                    var frame=v3?ViveRawSessionJournalCodec.FrameV3(payload,prior,first,out digest):ViveRawSessionJournalCodec.Frame(payload,prior,first,out digest);
                    output.Write(frame,0,frame.Length);prior=digest;
                };
                append(v3?ViveRawSessionJournalCodec.ManifestPayloadV3(manifest):ViveRawSessionJournalCodec.ManifestPayload(manifest),true);
                foreach(var item in events)append(ViveRawSessionJournalCodec.RecordPayload(item.Sequence+1,item,manifest),false);
                return output.ToArray();
            }
        }
        static ViveRawSessionReadResult Project(byte[] bytes)
        {using(var input=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,5000,CancellationToken.None);}
        public static void Register(TestSuite suite)
        {
            suite.Run("VRAW focus metadata uses existing hashed event layout and selected safety CSV fields",()=>{
                foreach(bool v3 in new[]{false,true})
                {
                    var source=Project(Prefix(v3));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,source.Status);
                    Check.Equal(7L,source.VerifiedTaskRecords);Check.Equal(0L,source.VerifiedObservationRecords);
                    var events=source.TrialProjection.ObservedSafetyEvents;Check.Equal(5,events.Count);
                    Check.Equal("focus_changed",events[0].Kind);Check.Equal("focus_baseline",events[0].Reason);
                    Check.Equal("focus_lost",events[1].Reason);
                    Check.Equal("desktop_focused=True;xr_focused=False;task_input=blocked;sampling=physical_validity_required",events[1].Detail);
                    Check.Equal("focus_pause",events[3].Kind);Check.Equal("focus_restored",events[4].Reason);
                    ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(source,true,out report));
                    foreach(var line in report.SafetyEventsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries))Check.Equal(17,line.Split(',').Length);
                    Check.True(report.SafetyEventsCsv.Contains(Cell("focus_lost"))&&report.SafetyEventsCsv.Contains(Cell(events[1].Detail)));
                    Check.True(report.ManifestCsv.Contains("focus_event_fields,state:actor_code_reason_detail_only_raw_event_keeps_other_fields\n"));
                    Check.True(report.ManifestCsv.Contains("focus_measurement_quality,state:focus_does_not_prove_pose_gaze_or_trial_validity\n"));
                }
            });
            suite.Run("VRAW focus restoration cannot convert an interrupted trial into a valid response",()=>{
                var source=Project(Prefix(true));var trial=source.TrialProjection.Trials[0];
                Check.Equal(1,source.TrialProjection.Trials.Count);Check.Equal(1,trial.InterruptionEvents);Check.Equal(0,trial.ResponseEvents);
                Check.Equal((string)null,trial.DeclaredReactionSeconds);Check.Equal((string)null,trial.DeclaredCorrect);Check.Equal("UNKNOWN",trial.Validity);
                Check.Equal("xr_focus_lost",trial.InterruptionReason);Check.True(!trial.EventConflict&&!trial.IncompleteEvidence);
                ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(source,true,out report));
                Check.True(report.ManifestCsv.Contains("focus_recovery,state:restored_focus_does_not_revalidate_interrupted_trials\n"));
                Check.True(!report.Saved&&!report.CompleteSessionProven&&!source.CompleteSessionProven);
            });
            suite.Run("VRAW focus safety projection preserves missing null and untrusted declarations",()=>{
                var acc=new ViveRawTrialAccumulator();acc.SetManifest("session","clock","plan",new[]{0},new[]{1},new[]{"schedule"},new[]{"geometry"});
                acc.Observe("focus_changed",null,0,0,0,1,new Dictionary<string,string>{{"reason",null},{"detail","=untrusted,\nfocus"}});
                acc.Observe("focus_pause",null,0,0,1,2,new Dictionary<string,string>());
                var source=new ViveRawSessionReadResult{Status=ViveRawSessionReadStatus.VerifiedPrefix,TrialProjection=acc.Finish()};
                Check.Equal(2,source.TrialProjection.ObservedSafetyEvents.Count);Check.True(source.TrialProjection.ObservedSafetyEvents[0].ReasonPresent);
                Check.True(!source.TrialProjection.ObservedSafetyEvents[1].ReasonPresent);
                ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(source,true,out report));
                var rows=report.SafetyEventsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);
                var first=rows[1].Split(',');var second=rows[2].Split(',');
                Check.Equal("state:null",first[8]);Check.Equal(Cell("=untrusted,\nfocus"),first[9]);Check.Equal("state:missing",second[8]);
                Check.True(!report.SafetyEventsCsv.Contains("=untrusted")&&source.TrialProjection.Trials.Count==0);
            });
            suite.Run("VRAW resumed focus prefix preserves later response without repairing interrupted RT or hash corruption",()=>{
                var bytes=Prefix(true,true);var source=Project(bytes);
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,source.Status);
                Check.Equal(11L,source.VerifiedTaskRecords);Check.Equal(12L,source.VerifiedRecordCount);
                Check.Equal((long)bytes.Length,source.VerifiedBytes);Check.True(!source.TrailingFrameTruncated);
                Check.True(!String.IsNullOrEmpty(source.VerifiedPrefixChainDigestHex)&&source.VerifiedPrefixChainDigestHex.Length==64);
                Check.True(!source.CaptureTerminalObserved&&!source.CompleteSessionProven);
                var trials=source.TrialProjection.Trials;Check.Equal(2,trials.Count);
                var interrupted=trials[0];var resumed=trials[1];
                Check.Equal("focus-trial",interrupted.TrialId);Check.Equal(1,interrupted.InterruptionEvents);Check.Equal(0,interrupted.ResponseEvents);
                Check.Equal((string)null,interrupted.DeclaredReactionSeconds);Check.Equal((string)null,interrupted.Response);
                Check.Equal("resumed-trial",resumed.TrialId);Check.Equal(1,resumed.Segment);Check.Equal(1,resumed.ResponseEvents);Check.Equal(0,resumed.InterruptionEvents);
                Check.Equal((long?)8,resumed.PlannedEventSequence);Check.Equal((long?)9,resumed.PresentationEventSequence);Check.Equal((long?)10,resumed.ResponseEventSequence);
                Check.Equal("Yellow",resumed.Response);Check.Equal("10",resumed.DeclaredOnset);Check.Equal(".25",resumed.DeclaredReactionSeconds);
                Check.Equal((double?)10.25,resumed.ResponseEventLocalSeconds);Check.True(!resumed.EventConflict&&!resumed.IncompleteEvidence);
                Check.Equal("UNKNOWN",interrupted.Validity);Check.Equal("UNKNOWN",resumed.Validity);
                ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(source,true,out report));
                string[] resumedCells=null;
                foreach(var row in report.TrialsCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries))
                {var cells=row.Split(',');if(cells[0]==Cell("resumed-trial"))resumedCells=cells;}
                Check.True(resumedCells!=null);Check.Equal(Cell("Yellow"),resumedCells[25]);Check.Equal(Cell(".25"),resumedCells[28]);
                Check.True(report.SafetyEventsCsv.Contains(Cell("explicit_resume"))&&!report.CompleteSessionProven&&!report.Saved);
                int offset=8,lastPayload=-1;
                while(offset<bytes.Length)
                {int length=BitConverter.ToInt32(bytes,offset);lastPayload=offset+8;offset=checked(offset+8+length+32);}
                Check.Equal(bytes.Length,offset);Check.True(lastPayload>=0);
                var tampered=(byte[])bytes.Clone();tampered[lastPayload+9]^=1;
                var rejected=Project(tampered);Check.Equal(ViveRawSessionReadStatus.Invalid,rejected.Status);
                Check.Equal((ViveRawTrialProjection)null,rejected.TrialProjection);
            });
        }
    }
}
