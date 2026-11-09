using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Pure synthetic values and in-memory bytes; no publication or device.
    public static class ViveRawTaskFrameMeasurementTests
    {
        sealed class Clock:IClock {public double Now {get;set;}public string ClockId {get{return "clock";}}}
        sealed class Fixture
        {
            internal readonly ViveNamedSimulationPlan Plan;
            internal readonly ViveNamedRecordingIntent Intent;
            internal readonly ViveNamedSessionFrameAdmission.Manifest Manifest;
            internal Fixture()
            {
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"geometry");
                Plan=ViveNamedSimulationPlan.Select(Actor.Owner,"plan",option);
                Intent=ViveNamedRecordingIntent.Select(Actor.Owner,"intent",true,true);
                Manifest=new ViveNamedSessionFrameAdmission.Manifest("session","clock",Plan,Intent,true);Manifest.SealChoiceTime(0,-1);
            }
            internal Dictionary<string,string> Fields()
            {return new Dictionary<string,string>{{"named_schema",ViveNamedRecordingIntent.EventSchemaVersion},{"named_plan_id",Plan.PlanId},
                {"named_plan_schema",ViveNamedSimulationPlan.SchemaVersion},{"named_recording_intent_id",Intent.IntentId},
                {"named_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT"},{"named_task_recording_intent","REQUESTED"},{"named_eye_recording_intent","REQUESTED"}};}
            internal JournalEvent Event(IDictionary<string,string> fields,string trial=null,string kind="phase_changed",string session="session",string clock="clock",long sequence=0,double time=1,int block=-1)
            {return new JournalEvent(sequence,time,clock,session,block,0,trial,kind,fields,null,Plan,null,Intent);}
        }
        static void Text(BinaryWriter w,string value)
        {if(value==null){w.Write(-1);return;}var bytes=new UTF8Encoding(false,true).GetBytes(value);w.Write(bytes.Length);w.Write(bytes);}
        // Independent legacy-layout witness for valid records: catches any
        // byte change caused by the identity-check refactor.
        static byte[] Reference(long ordinal,JournalEvent e)
        {
            using(var m=new MemoryStream())using(var w=new BinaryWriter(m,new UTF8Encoding(false,true)))
            {
                w.Write(ordinal);w.Write((byte)1);w.Write(e.Sequence);w.Write(e.Time);Text(w,e.ClockId);Text(w,e.SessionId);w.Write(e.BlockIndex);w.Write(e.Segment);
                Text(w,e.TrialId);Text(w,e.Kind);w.Write(e.NamedBlock==null?-1:e.NamedBlock.Index);
                var keys=new List<string>(e.Fields.Keys);keys.Sort(StringComparer.Ordinal);w.Write(keys.Count);
                foreach(var key in keys){Text(w,key);Text(w,e.Fields[key]);}w.Flush();return m.ToArray();
            }
        }
        static void Matches(Fixture f,JournalEvent e,long ordinal=1)
        {
            byte[] payload=ViveRawSessionJournalCodec.RecordPayload(ordinal,e,f.Manifest);int measured;
            Check.True(payload.SequenceEqual(Reference(ordinal,e)),"VRAW task bytes changed");
            Check.True(ViveRawTaskFrameMeasurement.TryMeasure(ordinal,e,f.Manifest,Int64.MaxValue,out measured));
            Check.Equal(payload.Length+40,measured);byte[] digest;
            Check.Equal(measured,ViveRawSessionJournalCodec.FrameV3(payload,new byte[32],false,out digest).Length);
            int exact;Check.True(ViveRawTaskFrameMeasurement.TryMeasure(ordinal,e,f.Manifest,measured,out exact));Check.Equal(measured,exact);
            Check.True(!ViveRawTaskFrameMeasurement.TryMeasure(ordinal,e,f.Manifest,measured-1,out exact));Check.Equal(0,exact);
        }
        static void Rejected(Fixture f,JournalEvent e,long ordinal=1)
        {
            int measured;Check.True(!ViveRawTaskFrameMeasurement.TryMeasure(ordinal,e,f.Manifest,Int64.MaxValue,out measured));Check.Equal(0,measured);
            Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(ordinal,e,f.Manifest));
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VRAW queue budget accounts manifest terminal task eye and status against actual codec",()=>{
                var f=new Fixture();var fields=f.Fields();var item=f.Event(fields);int header=ViveRawSessionJournalCodec.ManifestPayloadV3(f.Manifest).Length+48;
                var status=new ViveProEyeWorker.ObservedEyeStatus(0,1,ViveProEyeWorker.EyeStatusCause.None,
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2,false,2UL,true);
                var ray=new ViveEyeRay(3,true,true,new Vec3(1,2,3),new Vec3(.1,.2,.3));
                var poll=new ViveProEyePoll(new ViveHeadFrame(1,1,true,true,Rotation.Identity),new ViveEyeFrame(1,8,1,false,ray,ray,ray),
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
                var stamp=new ViveDevelopmentParticipant.NamedRecordingTaskStamp(null,null,f.Plan,f.Intent,1,0,"session",Phase.Practice,0,0,new string('x',32768));
                var eye=new ViveNamedSessionFrameAdmission.Frame(poll,stamp,0,2);
                var budget=ViveRawByteBudget.SelectExplicit(1048576);var ledger=new ViveRawByteBudget.QueueLedger(budget,f.Manifest);int bound;
                Check.True(ledger.Uses(budget));Check.True(ledger.TryCharge(0,item));
                int taskBytes=ViveRawSessionJournalCodec.RecordPayload(1,item,f.Manifest).Length+40;
                Check.True(ledger.VerifyEncoded(0,item,header+taskBytes,header));
                Check.True(!ledger.VerifyEncoded(0,item,header+taskBytes+1,header));
                Check.True(!ledger.VerifyEncoded(1,status,100,0));
                Check.True(ledger.TryFrameBound(1,status,out bound));int statusBytes=ViveRawSessionJournalCodec.StatusPayloadV3(2,status).Length+40;
                Check.True(statusBytes<=bound);Check.True(ledger.TryCharge(1,status));Check.True(ledger.VerifyEncoded(1,status,statusBytes,0));
                Check.True(ledger.TryFrameBound(2,eye,out bound));int eyeBytes=ViveRawSessionJournalCodec.RecordPayload(3,eye,f.Manifest).Length+40;
                Check.True(eyeBytes<=bound);Check.True(ledger.TryCharge(2,eye));Check.True(ledger.VerifyEncoded(2,eye,eyeBytes,0));
                Check.True(!ledger.VerifyEncoded(2,eye,bound+1,0));Check.True(!ledger.TryCharge(2,eye));
            });
            suite.Run("VRAW queue budget fails before publication and never refunds a charged frame",()=>{
                var f=new Fixture();var item=f.Event(f.Fields());int bound;
                var measure=new ViveRawByteBudget.QueueLedger(ViveRawByteBudget.SelectExplicit(1048576),f.Manifest);
                Check.True(measure.TryFrameBound(0,item,out bound));int header=ViveRawSessionJournalCodec.ManifestPayloadV3(f.Manifest).Length+48;
                var exact=ViveRawByteBudget.SelectExplicit(header+bound+ViveRawByteBudget.TerminalFrameBytes);
                var ledger=new ViveRawByteBudget.QueueLedger(exact,f.Manifest);
                Check.True(ledger.TryCharge(0,item));Check.True(!ledger.TryCharge(1,item));
                Check.True(!ledger.TryCharge(0,item));Check.True(ledger.VerifyEncoded(0,item,header+bound,header));
                var tooSmall=new ViveRawByteBudget.QueueLedger(ViveRawByteBudget.SelectExplicit(header+bound+ViveRawByteBudget.TerminalFrameBytes-1),f.Manifest);
                Check.True(!tooSmall.TryCharge(0,item));Check.True(!tooSmall.VerifyEncoded(0,item,header+bound,header));
            });
            suite.Run("VRAW countdown worst-case reservation has an exact conservative boundary",()=>{
                var f=new Fixture();var item=f.Event(f.Fields());int firstBound;
                var probe=new ViveRawByteBudget.QueueLedger(ViveRawByteBudget.SelectExplicit(8*1024*1024),f.Manifest);
                Check.True(probe.TryFrameBound(0,item,out firstBound));
                int header=ViveRawSessionJournalCodec.ManifestPayloadV3(f.Manifest).Length+48;
                long maximum=(long)header+firstBound+1048616+ViveRawByteBudget.TerminalFrameBytes;
                var exact=new ViveRawByteBudget.QueueLedger(ViveRawByteBudget.SelectExplicit(maximum),f.Manifest);
                Check.True(exact.TryCharge(0,item));Check.True(exact.CanReserveUpper(1,1048616));
                Check.True(!exact.CanReserveUpper(2,1048616));Check.True(!exact.CanReserveUpper(1,1048617));
                var shortByOne=new ViveRawByteBudget.QueueLedger(ViveRawByteBudget.SelectExplicit(maximum-1),f.Manifest);
                Check.True(shortByOne.TryCharge(0,item));Check.True(!shortByOne.CanReserveUpper(1,1048616));
            });
            suite.Run("VRAW3 task frame size matches unchanged bytes for null empty and multibyte fields",()=>{
                var f=new Fixture();var fields=f.Fields();fields.Add("empty","");fields.Add("null",null);fields.Add("unicode-雪","😀,\n雪");
                Matches(f,f.Event(fields,null),1);Matches(f,f.Event(fields,""),Int64.MaxValue);Matches(f,f.Event(fields,"trial-雪"),2);
            });
            suite.Run("VRAW3 task frame size matches actual practice and main plan events",()=>{
                var f=new Fixture();var clock=new Clock();var e=new Experiment(SessionConfiguration.NamedRecordingDevelopment("session",7,f.Plan,f.Intent),clock);
                e.BeginFamiliarization(Actor.Owner);var practice=e.PrepareNamedRecordingBlockPlan(Actor.Owner,true);Check.Equal(31,practice.Fields.Count);Matches(f,practice);
                e.BeginPractice(Actor.Owner);
                for(int i=0;i<8;i++){e.MarkPresented(e.CurrentTrial.TrialId);clock.Now+=.25;var ink=e.CurrentTrial.Stimulus.Ink;Check.True(e.ButtonChanged(ink,true));e.ButtonChanged(ink,false);}
                e.ConfirmNamedPlan(Actor.Owner,"owner");var main=e.PrepareNamedRecordingBlockPlan(Actor.Owner,false);Check.Equal(71,main.Fields.Count);Matches(f,main,2);
            });
            suite.Run("VRAW3 task frame measurement enforces field count and UTF8 byte limits",()=>{
                var f=new Fixture();var fields=f.Fields();while(fields.Count<128)fields.Add("key"+fields.Count,null);
                Matches(f,f.Event(fields));fields.Add("extra",null);Rejected(f,f.Event(fields));
                fields=f.Fields();fields.Add("text",new string('雪',10922)+"aa");Matches(f,f.Event(fields));fields["text"]+="a";Rejected(f,f.Event(fields));
                fields=f.Fields();fields.Add(" ","value");Rejected(f,f.Event(fields));
            });
            suite.Run("VRAW3 task measurement bounds maximum payload without a partial result",()=>{
                var f=new Fixture();var fields=f.Fields();for(int i=0;i<31;i++)fields.Add("large"+i,new string('x',32768));fields.Add("padding","");
                var basePayload=ViveRawSessionJournalCodec.RecordPayload(1,f.Event(fields),f.Manifest);
                int remaining=1048576-basePayload.Length;Check.True(remaining>0&&remaining<32768);
                fields["padding"]=new string('p',remaining);Matches(f,f.Event(fields));int size;
                Check.True(ViveRawTaskFrameMeasurement.TryMeasure(1,f.Event(fields),f.Manifest,1048616,out size));Check.Equal(1048616,size);
                fields["padding"]+="p";Rejected(f,f.Event(fields));
            });
            suite.Run("VRAW3 task measurement rejects identity schema and malformed text like the codec",()=>{
                var f=new Fixture();var fields=f.Fields();Rejected(f,f.Event(fields),0);Rejected(f,f.Event(fields,session:"other"));
                Rejected(f,f.Event(fields,clock:"other"));Rejected(f,f.Event(fields,sequence:-1));Rejected(f,f.Event(fields,time:Double.NaN));
                Rejected(f,f.Event(fields,block:0));Rejected(f,f.Event(fields,kind:" "));
                var other=new Fixture();Rejected(f,other.Event(other.Fields()));
                fields=f.Fields();fields["named_schema"]="wrong";Rejected(f,f.Event(fields));
                fields=f.Fields();fields.Remove("named_plan_id");Rejected(f,f.Event(fields));
                fields=f.Fields();fields.Add("surrogate","\ud800");int ignored;
                Check.True(!ViveRawTaskFrameMeasurement.TryMeasure(1,f.Event(fields),f.Manifest,Int64.MaxValue,out ignored));
                Check.Throws<EncoderFallbackException>(()=>ViveRawSessionJournalCodec.RecordPayload(1,f.Event(fields),f.Manifest));
                Check.True(!ViveRawTaskFrameMeasurement.TryMeasure(1,null,f.Manifest,Int64.MaxValue,out ignored));
                Check.True(!ViveRawTaskFrameMeasurement.TryMeasure(1,f.Event(f.Fields()),new ViveNamedSessionFrameAdmission.Manifest("session","clock",null,f.Intent,true),Int64.MaxValue,out ignored));
                Check.True(!ViveRawTaskFrameMeasurement.TryMeasure(1,f.Event(f.Fields()),new ViveNamedSessionFrameAdmission.Manifest("session","clock",f.Plan,null,true),Int64.MaxValue,out ignored));
                fields=f.Fields();fields.Add("long",new string('x',100000));
                Check.True(!ViveRawTaskFrameMeasurement.TryMeasure(1,f.Event(fields),f.Manifest,Int64.MaxValue,out ignored));
                Check.True(!ViveRawTaskFrameMeasurement.TryMeasure(1,f.Event(f.Fields(),kind:new string(' ',100000)),f.Manifest,Int64.MaxValue,out ignored));
                Check.Equal(0,ignored);
            });
        }
    }
}
