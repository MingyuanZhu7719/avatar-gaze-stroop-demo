using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic bytes only. A terminal is a declaration in a byte stream,
    // not evidence of actual hardware, file close, full data, or consent.
    public static class ViveRawSessionTerminalTests
    {
        internal static byte[] Fixture(string reason="Completed",bool appendTail=false,bool wrongCursor=false,bool omitPhase=false,bool truncateTerminal=false,
            bool withStatus=false,bool wrongStatusSequence=false,bool earlyStatus=false,bool invalidStatus=false,bool lateEye=false,bool lateStatus=false,bool omitTerminal=false,
            bool wrongCount=false,bool earlyTerminal=false)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"geometry-fixture");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"plan-fixture",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"intent-fixture",true,true);
            var m=new ViveNamedSessionFrameAdmission.Manifest("session-fixture","clock-fixture",plan,intent,true);m.SealChoiceTime(.5,11);
            var fields=new Dictionary<string,string>{{"named_schema","vive-named-recording-intent-v3"},{"named_plan_id",plan.PlanId},{"named_plan_schema","vive-named-simulation-plan-v2"},
                {"named_recording_intent_id",intent.IntentId},{"named_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT"},{"named_task_recording_intent","REQUESTED"},{"named_eye_recording_intent","REQUESTED"},
                {"named_block_index","0"},{"named_option","Automatic"},{"named_list_index","1"},{"named_schedule",option.ScheduleVersion},{"named_geometry",option.GeometryVersion},
                {"reason",reason},{"detail","synthetic-only"}};
            using(var output=new MemoryStream())
            {
                byte[] prior=new byte[32];
                Action<byte[],bool> append=(payload,magic)=>{byte[] digest,frame=ViveRawSessionJournalCodec.FrameV3(payload,prior,magic,out digest);output.Write(frame,0,frame.Length);prior=digest;};
                append(ViveRawSessionJournalCodec.ManifestPayloadV3(m),true);
                long statuses=0;
                if(withStatus)
                {
                    var missing=new ViveProEyeWorker.ObservedEyeStatus(wrongStatusSequence?1:0,earlyStatus ? 0.4 : 1,ViveProEyeWorker.EyeStatusCause.PollMissing,
                        null,null,null,null,null,null,null,null);
                    byte[] payload=ViveRawSessionJournalCodec.StatusPayloadV3(1,missing);
                    // Recalculate the chain after deliberately corrupting the
                    // schema, to test semantic validation independent of hash.
                    if(invalidStatus)Buffer.BlockCopy(BitConverter.GetBytes(0UL),0,payload,25,8);
                    append(payload,false);statuses++;
                    var recovered=new ViveProEyeWorker.ObservedEyeStatus(1,1.1,ViveProEyeWorker.EyeStatusCause.None,
                        ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2,false,2UL,true);
                    append(ViveRawSessionJournalCodec.StatusPayloadV3(2,recovered),false);statuses++;
                }
                var end=new JournalEvent(12,1.25,"clock-fixture","session-fixture",0,0,null,"session_ended",fields,null,plan,plan.Blocks[0],intent);
                append(ViveRawSessionJournalCodec.RecordPayload(statuses+1,end,m),false);
                if(!omitPhase)
                {
                    fields.Remove("reason");fields.Remove("detail");fields.Add("from","Questionnaire");fields.Add("to","Ended");
                    var phase=new JournalEvent(13,1.5,"clock-fixture","session-fixture",0,0,null,"phase_changed",fields,null,plan,plan.Blocks[0],intent);
                    append(ViveRawSessionJournalCodec.RecordPayload(statuses+2,phase,m),false);
                }
                long tasks=omitPhase?1:2;long cursor=omitPhase?12:13;
                if(lateStatus)
                {
                    var delayed=new ViveProEyeWorker.ObservedEyeStatus(statuses,1.8,ViveProEyeWorker.EyeStatusCause.PollMissing,
                        null,null,null,null,null,null,null,null);
                    append(ViveRawSessionJournalCodec.StatusPayloadV3(tasks+statuses+1,delayed),false);statuses++;
                }
                long eyes=0;
                if(lateEye)
                {
                    var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(0,0,1));
                    var poll=new ViveProEyePoll(new ViveHeadFrame(1,1.8,true,true,new Rotation(0,0,0,1)),new ViveEyeFrame(1,1,1.7,false,null,null,ray),
                        ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
                    var stamp=new ViveDevelopmentParticipant.NamedRecordingTaskStamp(null,null,plan,intent,1,cursor,m.Session,Phase.Ended,0,0,null);
                    append(ViveRawSessionJournalCodec.RecordPayload(tasks+statuses+1,new ViveNamedSessionFrameAdmission.Frame(poll,stamp,0,1.9),m),false);eyes++;
                }
                byte[] terminal=ViveRawSessionJournalCodec.TerminalPayloadV3(tasks+statuses+eyes+1,wrongCursor?cursor+1:cursor,tasks,eyes,statuses,tasks+statuses+eyes,earlyTerminal ? 0.75 : 2);
                if(wrongCount)
                {int tasksOffset=9+4+Encoding.UTF8.GetByteCount("NORMAL_TASK_COMPLETION_DRAIN_DECLARED")+8;Buffer.BlockCopy(BitConverter.GetBytes(tasks+1),0,terminal,tasksOffset,8);}
                if(!omitTerminal)append(terminal,false);
                if(appendTail)output.WriteByte(42);
                byte[] result=output.ToArray();if(truncateTerminal)Array.Resize(ref result,result.Length-1);return result;
            }
        }
        static ViveRawSessionReadResult Scan(byte[] bytes)
        {using(var source=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.Scan(source,true,true,4194304,5000,CancellationToken.None);}
        public static void Register(TestSuite suite)
        {
            suite.Run("VRAW3 capture terminal validates sealed waterline and prior completed task without claiming durability",()=>{
                byte[] bytes=Fixture();var result=Scan(bytes);
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.True(result.CaptureTerminalObserved&&!result.TrailingFrameTruncated);
                Check.Equal(2L,result.VerifiedTaskRecords);Check.Equal(0L,result.VerifiedObservationRecords);Check.Equal(0L,result.VerifiedStatusRecords);
                Check.True(!result.SourceAuthenticated&&!result.ConsentAuthenticated&&!result.CompleteSessionProven&&!result.DurableStorageProven);
                using(var stream=new MemoryStream(bytes,false))
                {var r=ViveRawSessionJournalReader.Read(stream,true,true,4194304,5000);Check.True(r.CaptureTerminalObserved&&r.DisplayText.Contains(",capture_terminal,"));}
            });
            suite.Run("VRAW3 mismatched or interrupted terminal cannot claim clean capture",()=>{
                foreach(var bytes in new[]{Fixture(reason:"TechnicalEnd"),Fixture(reason:"Withdrawn"),Fixture(wrongCursor:true),Fixture(omitPhase:true),Fixture(appendTail:true),Fixture(lateEye:true),Fixture(wrongCount:true),Fixture(earlyTerminal:true)})
                {var r=Scan(bytes);Check.Equal(ViveRawSessionReadStatus.Invalid,r.Status);Check.True(!r.CaptureTerminalObserved);Check.Equal(0L,r.VerifiedBytes);}
                var partial=Scan(Fixture(truncateTerminal:true));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,partial.Status);Check.True(partial.TrailingFrameTruncated&&!partial.CaptureTerminalObserved);
            });
            suite.Run("VRAW3 every truncated terminal returns only the earlier prefix",()=>{
                byte[] bytes=Fixture(withStatus:true);int position=8,terminal=-1;
                while(position<bytes.Length)
                {int n=ContinuousJournal.Int(bytes,position);if(bytes[position+16]==4){terminal=position;break;}position+=8+n+32;}
                Check.True(terminal>0);
                for(int length=terminal;length<bytes.Length;length++)
                {var truncated=new byte[length];Buffer.BlockCopy(bytes,0,truncated,0,length);var r=Scan(truncated);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,r.Status);Check.True(!r.CaptureTerminalObserved);Check.True(r.TrailingFrameTruncated==(length!=terminal));}
            });
            suite.Run("VRAW3 terminal encoding refuses false counters and invalid timestamps",()=>{
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.TerminalPayloadV3(4,13,2,0,0,2,2));
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.TerminalPayloadV3(3,13,2,0,0,2,Double.NaN));
            });
            suite.Run("VRAW3 records local missing and recovery statuses with null fidelity",()=>{
                var r=Scan(Fixture(withStatus:true));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,r.Status);
                Check.True(r.CaptureTerminalObserved);Check.Equal(2L,r.VerifiedStatusRecords);Check.Equal(2L,r.VerifiedTaskRecords);
                using(var stream=new MemoryStream(Fixture(withStatus:true),false))
                {var text=ViveRawSessionJournalReader.Read(stream,true,true,4194304,5000).DisplayText;
                    Check.True(text.Contains(",eye_status,cause_flags,uint,1\n")&&text.Contains(",eye_status,cause_flags,uint,0\n")&&text.Contains(",eye_status,no_user_present,bool,0\n"));}
            });
            suite.Run("VRAW3 allows a delayed queued status after Ended without treating it as a gaze sample",()=>{
                var r=Scan(Fixture(lateStatus:true));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,r.Status);
                Check.True(r.CaptureTerminalObserved);Check.Equal(1L,r.VerifiedStatusRecords);Check.Equal(0L,r.VerifiedObservationRecords);
            });
            suite.Run("VRAW3 rejects reordered pre-choice or contradictory local status evidence",()=>{
                foreach(var bytes in new[]{Fixture(withStatus:true,wrongStatusSequence:true),Fixture(withStatus:true,earlyStatus:true),Fixture(withStatus:true,invalidStatus:true)})
                {var r=Scan(bytes);Check.Equal(ViveRawSessionReadStatus.Invalid,r.Status);Check.True(!r.CaptureTerminalObserved);Check.Equal(0L,r.VerifiedBytes);}
            });
            suite.Run("VRAW3 status encoding preserves multicausal unknowns and rejects contradictory fields",()=>{
                var failures=ViveProEyeWorker.EyeStatusCause.RuntimeUnavailable|ViveProEyeWorker.EyeStatusCause.CalibrationNotReady|
                    ViveProEyeWorker.EyeStatusCause.DeviceNotReportedConnected|ViveProEyeWorker.EyeStatusCause.NativeResultMissing|ViveProEyeWorker.EyeStatusCause.EyeMissing;
                var unavailable=new ViveProEyeWorker.ObservedEyeStatus(0,1,failures,ViveEyeRuntimeState.Unavailable,ViveEyeCalibrationState.Required,false,null,null,null,null,null);
                Check.True(ViveRawSessionJournalCodec.ValidStatus(unavailable));Check.True(ViveRawSessionJournalCodec.StatusPayloadV3(1,unavailable).Length>0);
                var malformed=new ViveProEyeWorker.ObservedEyeStatus(1,2,ViveProEyeWorker.EyeStatusCause.DirectionMalformed,
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,null,false,2UL,true);
                Check.True(ViveRawSessionJournalCodec.ValidStatus(malformed)); // raw direction itself is absent from this status.
                var contradiction=new ViveProEyeWorker.ObservedEyeStatus(2,3,ViveProEyeWorker.EyeStatusCause.None,
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,null,true,2UL,true);
                Check.True(!ViveRawSessionJournalCodec.ValidStatus(contradiction));
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.StatusPayloadV3(3,contradiction));
            });
        }
    }
}
