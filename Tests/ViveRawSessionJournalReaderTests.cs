using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // All fixtures are synthetic memory bytes; no application files or device.
    public static class ViveRawSessionJournalReaderTests
    {
        static string B64(string s){return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));}
        static string Bits(double d){return BitConverter.DoubleToInt64Bits(d).ToString("X16",CultureInfo.InvariantCulture);}
        static ViveNamedSessionFrameAdmission.Manifest Manifest()
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"geometry-fixture");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"plan-fixture",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"intent-fixture",true,true);
            var value=new ViveNamedSessionFrameAdmission.Manifest("session-fixture","clock-fixture",plan,intent,true);value.SealChoiceTime(0.5,11);return value;
        }
        internal static byte[] Fixture(out long manifestEnd,out long taskEnd)
        {
            var m=Manifest();byte[] prior=new byte[32];var output=new MemoryStream();
            Action<byte[],bool> frame=(payload,magic)=>{byte[] next,encoded=ViveRawSessionJournalCodec.Frame(payload,prior,magic,out next);output.Write(encoded,0,encoded.Length);prior=next;};
            frame(ViveRawSessionJournalCodec.ManifestPayload(m),true);manifestEnd=output.Length;
            var fields=new Dictionary<string,string>{{"empty",""},{"null",null},{"text","unicode-眼"},
                {"named_schema","vive-named-recording-intent-v3"},{"named_plan_id",m.Plan.PlanId},{"named_plan_schema","vive-named-simulation-plan-v2"},
                {"named_recording_intent_id",m.Intent.IntentId},{"named_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT"},{"named_task_recording_intent","REQUESTED"},
                {"named_eye_recording_intent","REQUESTED"},{"named_block_index","0"},{"named_option","Automatic"},{"named_list_index","1"},
                {"named_schedule",m.Plan.Blocks[0].Selection.ScheduleVersion},{"named_geometry",m.Plan.Blocks[0].Selection.GeometryVersion}};
            var task=new JournalEvent(12,1.25,"clock-fixture","session-fixture",0,2,"trial-fixture","pressed",fields,null,m.Plan,m.Plan.Blocks[0],m.Intent);
            frame(ViveRawSessionJournalCodec.RecordPayload(1,task,m),false);taskEnd=output.Length;
            var l=new ViveEyeRay(3,true,true,new Vec3(1,2,3),new Vec3(.1,.2,.3));
            var c=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(Double.NaN,0,1));
            var poll=new ViveProEyePoll(new ViveHeadFrame(7,2.5,true,true,new Rotation(.1,.2,.3,.4)),
                new ViveEyeFrame(9,123,2.25,false,l,null,c),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
            var stamp=new ViveDevelopmentParticipant.NamedRecordingTaskStamp(null,null,m.Plan,m.Intent,1,12,m.Session,Phase.MainTask,0,2,"trial-fixture");
            var eye=new ViveNamedSessionFrameAdmission.Frame(poll,stamp,3,2.75);
            frame(ViveRawSessionJournalCodec.RecordPayload(2,eye,m),false);return output.ToArray();
        }
        static ViveRawSessionReadResult Read(byte[] bytes,long budget=4194304,int records=5000,bool recovery=true)
        {using(var s=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.Read(s,true,recovery,budget,records);}
        static byte[] Rewrite(byte[] input,int index,Action<byte[]> change)
        {
            using(var source=new MemoryStream(input,false))using(var output=new MemoryStream())
            {
                source.Position=8;byte[] previous=new byte[32];int i=0;
                while(source.Position<source.Length)
                {
                    byte[] prefix=new byte[8];Check.Equal(8,source.Read(prefix,0,8));int n=ContinuousJournal.Int(prefix);
                    byte[] payload=new byte[n];Check.Equal(n,source.Read(payload,0,n));source.Position+=32;
                    if(i==index)change(payload);byte[] digest,frame=ViveRawSessionJournalCodec.Frame(payload,previous,i==0,out digest);
                    output.Write(frame,0,frame.Length);previous=digest;i++;
                }
                return output.ToArray();
            }
        }
        static void ReplaceAscii(byte[] data,string value)
        {
            byte[] needle=Encoding.ASCII.GetBytes(value);int found=-1;
            for(int i=0;i<=data.Length-needle.Length;i++)
            {bool match=true;for(int j=0;j<needle.Length;j++)if(data[i+j]!=needle[j]){match=false;break;}if(match){found=i;break;}}
            Check.True(found>=0);data[found]^=1;
        }
        sealed class FailedInput:Stream
        {
            readonly byte[] bytes;readonly int failAt;int position;
            internal FailedInput(byte[] value,int failAt){bytes=value;this.failAt=failAt;}
            public override bool CanRead {get{return true;}}public override bool CanWrite {get{return false;}}public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] buffer,int offset,int count){if(position>=failAt)throw new IOException("private");int n=Math.Min(count,Math.Min(failAt-position,bytes.Length-position));Buffer.BlockCopy(bytes,position,buffer,offset,n);position+=n;return n;}
            public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}public override void Flush(){throw new NotSupportedException();}
            public override long Seek(long o,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}
        }
        static void Row(string data,int ordinal,string kind,string name,string type,string value)
        {Check.True(data.Contains(ordinal.ToString(CultureInfo.InvariantCulture)+","+kind+","+name+","+type+","+value+"\n"));}
        static void ExpectFull(string output)
        {
            var expected=new Dictionary<string,string>(StringComparer.Ordinal);
            Action<int,string,string,string> put=(n,k,t,v)=>expected.Add(n+":"+k,t+":"+v);
            Action<int,string,string> txt=(n,k,v)=>put(n,k,v==null?"null":"utf8_b64",v==null?"":B64(v));
            Action<int,string,long> num=(n,k,v)=>put(n,k,"int",v.ToString(CultureInfo.InvariantCulture));
            Action<int,string,bool> flag=(n,k,v)=>put(n,k,"bool",v?"1":"0");
            Action<int,string,double> dbl=(n,k,v)=>put(n,k,"f64_bits_hex",Bits(v));
            string[] keys={"schema","source","run_kind","native_time","eye_coverage","ingest_context","sdk_version","eye_space","mapping","eye_origin_unit","head_space","device_identity","participant_identity","session_id","clock_id","plan_schema","plan_id","intent_schema","intent_id"};
            string[] values={"VRAW0002","SYNTHETIC_ONLY","SIMULATION_DEVELOPMENT_NOT_RESEARCH","NATIVE_TIME_OPAQUE","EYE_COVERAGE_UNKNOWN","INGEST_CONTEXT_NOT_SAMPLE_ALIGNMENT","SRANIPAL_SDK_VERSION_UNKNOWN","RAW_EYE_SPACE_UNVERIFIED","MAPPING_NOT_APPLIED;REHEARSAL=vive-sranipal-ray-normalized-x-reflection-tracking-to-avatar-rehearsal-v3-unvalidated","EYE_ORIGIN_MILLIMETERS","HEAD_TRACKING_LOCAL_ROTATION","DEVICE_IDENTITY_UNKNOWN","PARTICIPANT_IDENTITY_NOT_AUTHENTICATED","session-fixture","clock-fixture","vive-named-simulation-plan-v2","plan-fixture","vive-named-recording-intent-v3","intent-fixture"};
            for(int i=0;i<keys.Length;i++)txt(0,keys[i],values[i]);
            flag(0,"task_requested",true);flag(0,"eye_requested",true);flag(0,"pre_choice_prefix_unknown",true);dbl(0,"second_choice_local_observed",.5);num(0,"task_baseline",11);
            txt(0,"second_choice_local_observed_semantics","recorder_admission_enabled_at_owner_start_not_participant_input");
            num(0,"block_count",1);num(0,"block_0_index",0);num(0,"block_0_option",0);num(0,"block_0_list_index",1);txt(0,"block_0_schedule_version",ViveNamedOptionProvenance.AutomaticScheduleVersion);txt(0,"block_0_geometry_version","geometry-fixture");
            num(1,"source_sequence",12);dbl(1,"event_local_time",1.25);txt(1,"clock_id","clock-fixture");txt(1,"session_id","session-fixture");num(1,"block",0);num(1,"segment",2);txt(1,"trial_id","trial-fixture");txt(1,"event_kind","pressed");num(1,"named_block_index",0);num(1,"field_count",15);
            string[] fieldKeys={"empty","named_block_index","named_eye_recording_intent","named_eye_sample_payload","named_geometry","named_list_index","named_option","named_plan_id","named_plan_schema","named_recording_intent_id","named_schedule","named_schema","named_task_recording_intent","null","text"};
            string[] fieldValues={"","0","REQUESTED","NOT_INCLUDED_IN_TASK_EVENT","geometry-fixture","1","Automatic","plan-fixture","vive-named-simulation-plan-v2","intent-fixture",ViveNamedOptionProvenance.AutomaticScheduleVersion,"vive-named-recording-intent-v3","REQUESTED",null,"unicode-眼"};
            for(int i=0;i<fieldKeys.Length;i++){txt(1,"field_"+i+"_key",fieldKeys[i]);txt(1,"field_"+i+"_value",fieldValues[i]);}
            num(2,"native_frame",9);num(2,"native_timestamp",123);dbl(2,"eye_observed",2.25);num(2,"head_sequence",7);dbl(2,"head_observed",2.5);dbl(2,"checked_at",2.75);num(2,"loss_generation",3);num(2,"runtime",1);num(2,"calibration",2);
            flag(2,"eye_connected",true);flag(2,"native_result_present",true);num(2,"native_result",0);flag(2,"runtime_status_present",true);num(2,"runtime_status",2);flag(2,"no_user",false);
            flag(2,"left_present",true);put(2,"left_mask","uint","3");flag(2,"left_origin_valid",true);flag(2,"left_direction_valid",true);
            string[] axes={"origin_x","origin_y","origin_z","direction_x","direction_y","direction_z"};double[] left={1,2,3,.1,.2,.3},combined={0,0,0,Double.NaN,0,1};
            for(int i=0;i<axes.Length;i++)dbl(2,"left_"+axes[i],left[i]);flag(2,"right_present",false);flag(2,"combined_present",true);put(2,"combined_mask","uint","2");flag(2,"combined_origin_valid",false);flag(2,"combined_direction_valid",true);
            for(int i=0;i<axes.Length;i++)dbl(2,"combined_"+axes[i],combined[i]);flag(2,"head_connected",true);flag(2,"head_valid",true);
            string[] rotation={"x","y","z","w"};double[] q={.1,.2,.3,.4};for(int i=0;i<rotation.Length;i++)dbl(2,"head_rotation_"+rotation[i],q[i]);
            num(2,"ingest_phase",8);num(2,"ingest_block",0);num(2,"ingest_segment",2);txt(2,"ingest_trial_id","trial-fixture");num(2,"task_cursor",12);
            string[] lines=output.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);Check.Equal("ordinal,record_kind,field,type,value",lines[0]);bool run=false;
            for(int i=1;i<lines.Length;i++)
            {string[] part=lines[i].Split(',');Check.Equal(5,part.Length);string key=part[0]+":"+part[2];if(key=="0:run_software_id"){Check.True(!run&&part[3]=="bytes_b64"&&Convert.FromBase64String(part[4]).Length==16);run=true;continue;}
                string want;Check.True(expected.TryGetValue(key,out want),"Unexpected/duplicate "+key);Check.Equal(want,part[3]+":"+part[4]);expected.Remove(key);}
            Check.True(run);Check.Equal(0,expected.Count);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VRAW keeps participant software choice polls separate from admission time",()=>{
                var clock=new MonotonicClock("choice-roundtrip-clock");
                double task=clock.Now,eye=clock.Now;
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"choice-geometry");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"choice-plan",option);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"choice-intent",true,true);
                var experiment=new Experiment(SessionConfiguration.NamedRecordingDevelopment("choice-session",37,plan,intent),clock);
                double admission=clock.Now;
                experiment.RecordNamedSoftwareChoices(clock.ClockId,task,eye);
                var choice=experiment.Events[experiment.Events.Count-1];
                Check.Equal("participant_recording_software_choices",choice.Kind);
                var manifest=new ViveNamedSessionFrameAdmission.Manifest("choice-session",clock.ClockId,plan,intent,true);
                manifest.SealChoiceTime(admission,choice.Sequence-1);
                byte[] previous=new byte[32],next;var bytes=new MemoryStream();
                byte[] first=ViveRawSessionJournalCodec.FrameV3(ViveRawSessionJournalCodec.ManifestPayloadV3(manifest),previous,true,out next);
                bytes.Write(first,0,first.Length);previous=next;
                byte[] second=ViveRawSessionJournalCodec.FrameV3(ViveRawSessionJournalCodec.RecordPayload(1,choice,manifest),previous,false,out next);
                bytes.Write(second,0,second.Length);var result=Read(bytes.ToArray());
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                Check.True(result.DisplayText.Contains(B64("participant_recording_software_choices")));
                Check.True(result.DisplayText.Contains(B64(task.ToString("R",CultureInfo.InvariantCulture))));
                Check.True(result.DisplayText.Contains(B64(eye.ToString("R",CultureInfo.InvariantCulture))));
                Check.True(result.DisplayText.Contains(B64("recorder_admission_enabled_at_owner_start_not_participant_input")));
                Check.True(result.DisplayText.Contains(B64("controller_poll_not_hardware_event")));
                Check.Throws<InvalidOperationException>(()=>experiment.RecordNamedSoftwareChoices(clock.ClockId,task,eye));
                var bad=new Dictionary<string,string>{{"task_choice_poll_seconds","2"},{"eye_choice_poll_seconds","1"},
                    {"choice_clock_id",clock.ClockId},{"choice_time_basis","controller_poll_not_hardware_event"},
                    {"identity_or_study_consent","NOT_AUTHENTICATED"}};
                Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.ValidateSoftwareChoices(
                    "participant_recording_software_choices",bad,clock.ClockId,3));
            });
            suite.Run("VRAW selected prefix requires separate explicit consent before stream inspection",()=>{
                long a,b;byte[] bytes=Fixture(out a,out b);using(var s=new MemoryStream(bytes,false))
                {var result=ViveRawSessionJournalReader.Read(s,true,false,4194304,5000);Check.Equal(ViveRawSessionReadStatus.RecoveryRequired,result.Status);Check.Equal(0L,s.Position);Check.Equal((string)null,result.DisplayText);}
                using(var s=new MemoryStream(bytes,false))Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalReader.Read(s,false,true,4194304,5000));
            });
            suite.Run("VRAW codec synthetic manifest task and eyes cross-read with raw values and no completion claim",()=>{
                long a,b;var result=Read(Fixture(out a,out b));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                Check.True(!result.SourceAuthenticated&&!result.ConsentAuthenticated&&!result.CompleteSessionProven&&!result.DurableStorageProven&&!result.TrailingFrameTruncated);
                string x=result.DisplayText;
                ExpectFull(x);
                Row(x,0,"manifest","schema","utf8_b64",B64("VRAW0002"));Row(x,0,"manifest","session_id","utf8_b64",B64("session-fixture"));
                Row(x,0,"manifest","clock_id","utf8_b64",B64("clock-fixture"));Row(x,0,"manifest","block_0_option","int","0");Row(x,0,"manifest","block_0_list_index","int","1");
                Row(x,0,"manifest","second_choice_local_observed","f64_bits_hex",Bits(.5));
                Row(x,1,"task","source_sequence","int","12");Row(x,1,"task","trial_id","utf8_b64",B64("trial-fixture"));Row(x,1,"task","event_local_time","f64_bits_hex",Bits(1.25));
                Row(x,1,"task","field_0_key","utf8_b64",B64("empty"));Row(x,1,"task","field_0_value","utf8_b64","");
                Check.True(x.Contains(",null,\n"));Check.True(x.Contains(",utf8_b64,"+B64("unicode-眼")+"\n"));
                Row(x,2,"observation","native_frame","int","9");Row(x,2,"observation","native_timestamp","int","123");
                Row(x,2,"observation","eye_observed","f64_bits_hex",Bits(2.25));Row(x,2,"observation","head_observed","f64_bits_hex",Bits(2.5));
                Row(x,2,"observation","right_present","bool","0");Row(x,2,"observation","left_mask","uint","3");
                Row(x,2,"observation","left_origin_y","f64_bits_hex",Bits(2));Row(x,2,"observation","combined_direction_x","f64_bits_hex",Bits(Double.NaN));
                Row(x,2,"observation","ingest_trial_id","utf8_b64",B64("trial-fixture"));Row(x,2,"observation","task_cursor","int","12");
            });
            suite.Run("VRAW all incomplete tails expose only validated prefix after manifest",()=>{
                long manifest,task;byte[] bytes=Fixture(out manifest,out task);
                for(int i=0;i<bytes.Length;i++)
                {var part=new byte[i];Buffer.BlockCopy(bytes,0,part,0,i);var r=Read(part);
                    if(i<manifest){Check.Equal(ViveRawSessionReadStatus.Invalid,r.Status);Check.Equal((string)null,r.DisplayText);}
                    else{Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,r.Status);Check.True(r.TrailingFrameTruncated==(i!=manifest&&i!=task));if(i<task)Check.True(!r.DisplayText.Contains(",task,"));}}
            });
            suite.Run("VRAW tamper budget malformed source and record limits never expose staged content",()=>{
                long manifest,task;byte[] bytes=Fixture(out manifest,out task),bad=(byte[])bytes.Clone();bad[bad.Length-1]^=1;
                var altered=Read(bad);Check.Equal(ViveRawSessionReadStatus.Invalid,altered.Status);Check.Equal((string)null,altered.DisplayText);
                var limited=Read(bytes,manifest+1);Check.Equal(ViveRawSessionReadStatus.LimitExceeded,limited.Status);Check.Equal((string)null,limited.DisplayText);
                var count=Read(bytes,4194304,1);Check.Equal(ViveRawSessionReadStatus.LimitExceeded,count.Status);Check.Equal((string)null,count.DisplayText);
                using(var writable=new MemoryStream())Check.Equal(ViveRawSessionReadStatus.ReadFailure,ViveRawSessionJournalReader.Read(writable,true,true,100,1).Status);
            });
            suite.Run("VRAW recomputed hash cannot conceal mismatched identity sequence or observed time",()=>{
                long manifest,task;byte[] bytes=Fixture(out manifest,out task);
                var cases=new[]{
                    Rewrite(bytes,1,p=>ReplaceAscii(p,"session-fixture")),
                    Rewrite(bytes,1,p=>ReplaceAscii(p,"plan-fixture")),
                    Rewrite(bytes,1,p=>Buffer.BlockCopy(BitConverter.GetBytes(13L),0,p,9,8)),
                    Rewrite(bytes,2,p=>Buffer.BlockCopy(BitConverter.GetBytes(Double.NaN),0,p,17,8)),
                    Rewrite(bytes,2,p=>Buffer.BlockCopy(BitConverter.GetBytes(13L),0,p,p.Length-8,8)),
                    Rewrite(bytes,0,p=>ReplaceAscii(p,ViveNamedOptionProvenance.AutomaticScheduleVersion)),
                    Rewrite(bytes,0,p=>ReplaceAscii(p,"EYE_COVERAGE_UNKNOWN"))
                };
                foreach(var value in cases){var r=Read(value);Check.Equal(ViveRawSessionReadStatus.Invalid,r.Status);Check.Equal((string)null,r.DisplayText);}
            });
            suite.Run("VRAW raw IEEE 754 payload bits survive independently of floating-point conversions",()=>{
                long manifest,task;byte[] bytes=Fixture(out manifest,out task);long original=BitConverter.DoubleToInt64Bits(Double.NaN);
                long[] patterns={0L,unchecked((long)0x8000000000000000UL),0x7ff0000000000000L,unchecked((long)0xfff0000000000000UL),0x7ff8000000000042L,0x7ff0000000000001L};
                foreach(long bits in patterns)
                {
                    var changed=Rewrite(bytes,2,p=>{byte[] needle=BitConverter.GetBytes(original);int found=-1;for(int i=0;i<=p.Length-8;i++){bool same=true;for(int j=0;j<8;j++)if(p[i+j]!=needle[j]){same=false;break;}if(same){found=i;break;}}Check.True(found>=0);Buffer.BlockCopy(BitConverter.GetBytes(bits),0,p,found,8);});
                    var result=Read(changed);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Row(result.DisplayText,2,"observation","combined_direction_x","f64_bits_hex",bits.ToString("X16",CultureInfo.InvariantCulture));
                }
            });
            suite.Run("VRAW input errors and invalid lengths discard every staged record",()=>{
                long manifest,task;byte[] bytes=Fixture(out manifest,out task);
                using(var source=new FailedInput(bytes,(int)manifest))
                {var r=ViveRawSessionJournalReader.Read(source,true,true,4194304,5000);Check.Equal(ViveRawSessionReadStatus.ReadFailure,r.Status);Check.Equal((string)null,r.DisplayText);}
                byte[] broken=(byte[])bytes.Clone();broken[manifest+4]^=1;
                var invalid=Read(broken);Check.Equal(ViveRawSessionReadStatus.Invalid,invalid.Status);Check.Equal((string)null,invalid.DisplayText);
            });
        }
    }
}
