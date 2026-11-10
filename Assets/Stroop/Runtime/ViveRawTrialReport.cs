using System;
using System.Globalization;
using System.Text;

namespace AvatarStroop.Runtime
{
    // Internal, bounded memory-only analysis candidate. It neither opens nor
    // writes a stream, file, clipboard or network and is not an export receipt.
    // No product call site exists. Before product access, a separate current
    // authorization/withdrawal fence and stale-result suppression are needed.
    internal sealed class ViveRawTrialReport
    {
        internal readonly string ManifestCsv,BlocksCsv,PreparedStimuliCsv,TrialsCsv,RatingsCsv,StatusesCsv,SafetyEventsCsv;
        internal bool Saved {get{return false;}}
        internal bool CompleteSessionProven {get{return false;}}
        internal bool CurrentAccessAuthorized {get{return false;}}
        ViveRawTrialReport(string manifest,string blocks,string prepared,string trials,string ratings,string statuses,string safety)
        {ManifestCsv=manifest;BlocksCsv=blocks;PreparedStimuliCsv=prepared;TrialsCsv=trials;RatingsCsv=ratings;StatusesCsv=statuses;SafetyEventsCsv=safety;}
        sealed class Limit:Exception {}
        sealed class Output
        {
            internal readonly StringBuilder Text=new StringBuilder();readonly Count shared;
            internal Output(Count count){shared=count;}
            // ASCII output is capped in characters; UTF-16 builders, joined
            // rows and final strings can occupy substantially more memory.
            internal void Line(params string[] values)
            {string line=String.Join(",",values)+"\n";if(line.Length>2097152-shared.Chars)throw new Limit();shared.Chars+=line.Length;Text.Append(line);}
        }
        sealed class Count {internal int Chars;}
        static string TextCell(string value)
        {return value==null?"state:missing":"b64:"+Convert.ToBase64String(new UTF8Encoding(false,true).GetBytes(value));}
        static string DeclaredTextCell(bool present,string value)
        {return !present?"state:missing":value==null?"state:null":TextCell(value);}
        static string I32(int value){return "i32:"+value.ToString(CultureInfo.InvariantCulture);}
        static string I64(long value){return "i64:"+value.ToString(CultureInfo.InvariantCulture);}
        static string OptI64(long? value){return value.HasValue?I64(value.Value):"state:missing";}
        static string U64(ulong value){return "u64:"+value.ToString(CultureInfo.InvariantCulture);}
        static string Bool(bool value){return value?"bool:true":"bool:false";}
        static string OptBool(bool? value){return value.HasValue?Bool(value.Value):"state:missing";}
        static string OptI32(int? value){return value.HasValue?I32(value.Value):"state:missing";}
        static string OptU64(ulong? value){return value.HasValue?U64(value.Value):"state:missing";}
        static string F64(double value){return "f64bits:"+BitConverter.DoubleToInt64Bits(value).ToString("X16",CultureInfo.InvariantCulture);}
        static string OptF64(double? value){return value.HasValue?F64(value.Value):"state:missing";}
        // This explicit flag requests an in-memory representation; it does not
        // authenticate the source, consent, or current authorization.
        internal static bool TryBuild(ViveRawSessionReadResult source,bool explicitAction,out ViveRawTrialReport report)
        {
            report=null;
            if(!explicitAction||source==null||source.Status!=ViveRawSessionReadStatus.VerifiedPrefix||source.TrialProjection==null)return false;
            try
            {
                var p=source.TrialProjection;var count=new Count();var manifest=new Output(count);var blocks=new Output(count);var prepared=new Output(count);
                var trials=new Output(count);var ratings=new Output(count);var statuses=new Output(count);var safety=new Output(count);
                manifest.Line("field","value");
                manifest.Line("scope","state:selected_verified_prefix_only");manifest.Line("session",TextCell(p.DeclaredSessionId));
                manifest.Line("clock",TextCell(p.DeclaredClockId));manifest.Line(p.DesignVariant=="LEGACY_ABCD"?"legacy_design":"plan",TextCell(p.DesignVariant=="LEGACY_ABCD"?p.DeclaredLegacyDesignId:p.DeclaredPlanId));
                manifest.Line("design_variant",TextCell(p.DesignVariant));
                if(p.DesignVariant!="LEGACY_ABCD")manifest.Line("legacy_second_choice_local_observed_semantics","state:recorder_admission_enabled_at_owner_start_not_participant_input");
                else manifest.Line("choice_times",p.OperatorWozRecordingDeclared?"state:separate_task_poll_eye_poll_and_owner_admission_in_VRAW5_manifest":"state:separate_task_poll_eye_poll_and_owner_admission_in_VRAW4_manifest");
                if(p.OperatorWozRecordingDeclared){manifest.Line("control_mode","state:operator_woz_participant_local_recording");
                    manifest.Line("network_times","state:independent_clocks_not_synchronized");manifest.Line("network_authentication","state:key_possession_not_hardware_identity");
                    manifest.Line("remote_operator_cleanup","state:not_proven_by_participant_capture_terminal");}
                manifest.Line("participant_choice_poll_times","state:see_participant_recording_software_choices_task_event_if_present_else_unknown");
                manifest.Line("capture_terminal_declared",Bool(source.CaptureTerminalObserved));manifest.Line("trailing_frame_truncated",Bool(source.TrailingFrameTruncated));
                manifest.Line("verified_bytes",I64(source.VerifiedBytes));manifest.Line("task_records",I64(source.VerifiedTaskRecords));
                manifest.Line("eye_observations",I64(source.VerifiedObservationRecords));manifest.Line("eye_statuses",I64(source.VerifiedStatusRecords));
                manifest.Line("pre_choice_coverage","state:unknown");manifest.Line("sampling_coverage","state:unknown");
                manifest.Line("complete_session_proven","bool:false");manifest.Line("source_authenticated","bool:false");
                manifest.Line("participant_consent_authenticated","bool:false");manifest.Line("current_access_authorized","state:unknown");
                manifest.Line("current_withdrawal_status","state:unknown");manifest.Line("samples_joined_to_trials","bool:false");
                manifest.Line("raw_eye_sample_rows_included","bool:false");manifest.Line("all_task_event_fields_included","bool:false");
                manifest.Line("trial_open_flush_evidence","state:unknown");manifest.Line("render_proxy_evidence","state:unknown");
                manifest.Line("response_commit_evidence","state:unknown");manifest.Line("safety_event_fields","state:selected_only");
                manifest.Line("phase_transitions","state:declared_events_in_selected_prefix_only");
                manifest.Line("duration_fields","state:declared_strings_not_measurement_or_coverage");
                manifest.Line("unassigned_trial_events",I32(p.UnassignedTrialEvents));
                manifest.Line("prepared_plan_events",I32(p.PreparedPlans.Count));manifest.Line("unsupported_plan_events",I32(p.UnsupportedPreparedPlanEvents));
                manifest.Line("writer_confirmation","state:unknown");manifest.Line("tail_coverage","state:unknown");
                blocks.Line("block",p.DesignVariant=="LEGACY_ABCD"?"legacy_condition":"option","list_index",p.DesignVariant=="LEGACY_ABCD"?"mapping":"schedule","geometry");
                foreach(var b in p.DeclaredPlanBlocks)blocks.Line(I32(b.Index),TextCell(b.Option),I32(b.ListIndex),TextCell(b.Schedule),TextCell(b.Geometry));
                prepared.Line("scope","block","declaration_segment","attempt","plan_event_sequence","plan_event_local_f64bits","list","item_index","planned_word","planned_ink","execution","prior_order_state","writer_confirmation");
                foreach(var plan in p.PreparedPlans)foreach(var item in plan.Items)
                    prepared.Line(TextCell(plan.Scope),I32(plan.Block),I32(plan.SegmentAtDeclaration),I32(plan.Attempt),I64(plan.EventSequence),F64(plan.EventLocalSeconds),TextCell(plan.List),I32(item.Index),TextCell(item.Word),TextCell(item.Ink),"state:unknown","state:"+plan.PriorOrderState.ToLowerInvariant(),"state:unknown");
                trials.Line("trial_id","block","segment","planned_event_count","presentation_event_count","response_event_count","interruption_event_count",
                    "planned_event_sequence","presentation_event_sequence","response_event_sequence","interruption_event_sequence",
                    "planned_event_local_f64bits","presentation_event_local_f64bits","response_event_local_f64bits","interruption_event_local_f64bits",
                    "planned_list","planned_index","planned_stimulus_schema","planned_word","planned_ink","planned_stimulus_state","presented_list","presented_index","presentation_event_word","presentation_event_ink","response","declared_correct","declared_onset","declared_rt_seconds",
                    "interruption_actor","interruption_reason","interruption_onset_available","event_conflict","incomplete_evidence",
                    "open_flush_evidence","render_proxy_evidence","response_commit_evidence","validity","gaze_alignment",
                    "declared_presentation_task_onset","declared_presentation_task_clock","declared_presentation_task_basis","declared_unity_render_seconds","declared_unity_render_clock","declared_unity_render_frame","declared_unity_render_revision","declared_unity_render_basis");
                foreach(var t in p.Trials)trials.Line(TextCell(t.TrialId),I32(t.Block),I32(t.Segment),I32(t.PlannedEvents),I32(t.PresentationEvents),I32(t.ResponseEvents),I32(t.InterruptionEvents),
                    OptI64(t.PlannedEventSequence),OptI64(t.PresentationEventSequence),OptI64(t.ResponseEventSequence),OptI64(t.InterruptionEventSequence),
                    OptF64(t.PlannedEventLocalSeconds),OptF64(t.PresentationEventLocalSeconds),OptF64(t.ResponseEventLocalSeconds),OptF64(t.InterruptionEventLocalSeconds),
                    TextCell(t.PlannedList),TextCell(t.PlannedIndex),TextCell(t.PlannedStimulusSchema),TextCell(t.PlannedWord),TextCell(t.PlannedInk),"state:"+t.PlannedStimulusState.ToLowerInvariant(),
                    TextCell(t.PresentedList),TextCell(t.PresentedIndex),TextCell(t.Word),TextCell(t.Ink),TextCell(t.Response),
                    TextCell(t.DeclaredCorrect),TextCell(t.DeclaredOnset),TextCell(t.DeclaredReactionSeconds),
                    DeclaredTextCell(t.InterruptionActorPresent,t.InterruptionActor),DeclaredTextCell(t.InterruptionReasonPresent,t.InterruptionReason),
                    DeclaredTextCell(t.InterruptionOnsetAvailablePresent,t.InterruptionOnsetAvailable),
                    Bool(t.EventConflict),Bool(t.IncompleteEvidence),"state:unknown","state:unknown","state:unknown","state:unknown","state:unknown",
                    TextCell(t.DeclaredPresentationTaskOnset),TextCell(t.DeclaredPresentationTaskClock),TextCell(t.DeclaredPresentationTaskBasis),
                    TextCell(t.DeclaredUnityRenderSeconds),TextCell(t.DeclaredUnityRenderClock),TextCell(t.DeclaredUnityRenderFrame),TextCell(t.DeclaredUnityRenderRevision),TextCell(t.DeclaredUnityRenderBasis));
                ratings.Line("block","segment","event_local_f64bits","item","declared_value","language","version","validity");
                foreach(var r in p.Ratings)ratings.Line(I32(r.Block),I32(r.Segment),F64(r.EventLocalSeconds),TextCell(r.Item),TextCell(r.DeclaredValue),TextCell(r.Language),TextCell(r.Version),"state:unknown");
                statuses.Line("sequence","poll_return_local_f64bits","cause_flags","runtime","calibration","eye_connected","native_result","runtime_status","no_user","combined_mask","combined_direction_valid");
                foreach(var s in p.ObservedStatuses)statuses.Line(I64(s.Sequence),F64(s.PollReturnLocalSeconds),U64((ulong)s.CauseFlags),
                    OptI32(s.Runtime.HasValue?(int?)s.Runtime.Value:null),OptI32(s.Calibration.HasValue?(int?)s.Calibration.Value:null),OptBool(s.EyeDeviceConnected),
                    OptI32(s.NativeResultCode),OptI32(s.NativeRuntimeStatus),OptBool(s.NoUser),OptU64(s.CombinedValidityMask),OptBool(s.CombinedDirectionReportedValid));
                safety.Line("sequence","block","segment","event_local_f64bits","kind","trial_id","actor","code","reason","detail","onset_available","paused_seconds","declared_active_seconds","declared_phase_from","declared_phase_to","declared_questionnaire_duration_seconds","declared_rest_seconds");
                foreach(var e in p.ObservedSafetyEvents)safety.Line(I64(e.Sequence),I32(e.Block),I32(e.Segment),F64(e.EventLocalSeconds),TextCell(e.Kind),TextCell(e.TrialId),
                    DeclaredTextCell(e.ActorPresent,e.Actor),DeclaredTextCell(e.CodePresent,e.Code),DeclaredTextCell(e.ReasonPresent,e.Reason),
                    DeclaredTextCell(e.DetailPresent,e.Detail),DeclaredTextCell(e.OnsetAvailablePresent,e.OnsetAvailable),DeclaredTextCell(e.PausedSecondsPresent,e.PausedSeconds),DeclaredTextCell(e.ActiveSecondsPresent,e.ActiveSeconds),
                    DeclaredTextCell(e.PhaseFromPresent,e.PhaseFrom),DeclaredTextCell(e.PhaseToPresent,e.PhaseTo),
                    DeclaredTextCell(e.DurationSecondsPresent,e.DurationSeconds),DeclaredTextCell(e.RestSecondsPresent,e.RestSeconds));
                report=new ViveRawTrialReport(manifest.Text.ToString(),blocks.Text.ToString(),prepared.Text.ToString(),trials.Text.ToString(),ratings.Text.ToString(),statuses.Text.ToString(),safety.Text.ToString());return true;
            }
            catch(Exception e) when(e is Limit||e is EncoderFallbackException||e is OverflowException)
            {report=null;return false;}
        }
    }
}
