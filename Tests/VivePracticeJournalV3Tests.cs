using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // All streams and observations are synthetic and in memory. No device,
    // network, participant record, real file, or File.Move operation is used.
    public static class VivePracticeJournalV3Tests
    {
        sealed class Clock:IClock {public double Value=10;public bool Broken;public Action OnNow;public string ClockId {get{return "fixture-clock";}}public double Now {get{if(Broken)throw new InvalidOperationException();if(OnNow!=null)OnNow();return Value;}}}
        static VivePracticeContextV3 Context(int attempt=1,string epoch="epoch-1",int recovery=0,string previous=null,VivePracticeEpochReasonV3 reason=VivePracticeEpochReasonV3.Initial)
        {return new VivePracticeContextV3("fixture-session","fixture-participant","fixture-stream","fixture-device","fixture-clock","fixture-map",attempt,epoch,recovery,previous,reason,9);}
        static ViveProEyePoll Poll(int sequence,double time,bool connected=true,bool noUser=false,ulong mask=2)
        {
            var ray=new ViveEyeRay(mask,false,true,new Vec3(0,0,0),new Vec3(0,0,1));
            return new ViveProEyePoll(new ViveHeadFrame(sequence,time,true,true,Rotation.Identity),new ViveEyeFrame(sequence,sequence*8,time,noUser,null,null,ray),
                ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,connected,0);
        }
        static VivePracticeJournalV3Writer Writer(Stream s,Clock c,VivePracticeContextV3 context=null,int records=100,long bytes=100000)
        {var selected=context??Context();var preparation=Prepare(c,selected);return new VivePracticeJournalV3Writer(s,c,selected,Start(selected),ViveDevelopmentDataSource.SyntheticFixture,true,true,records,bytes,preparation);}
        static VivePracticeRecordingPreparationV3 Prepare(Clock c,VivePracticeContextV3 context)
        {double after=c.Value;c.Value=8;var p=VivePracticeRecordingPreparationV3.Create(c,context.SessionId,context.PracticeAttempt,context.RecordingEpochId,true,true);c.Value=after;return p;}
        static JournalEvent Start(VivePracticeContextV3 c)
        {return new JournalEvent(0,c.ObservedPracticeStartLocalSeconds.Value,"fixture-clock","fixture-session",-1,0,null,"practice_started",new Dictionary<string,string>{{"attempt",c.PracticeAttempt.ToString()}});}
        static JournalEvent Task(long seq,double time,string kind,int attempt=1)
        {return new JournalEvent(seq,time,"fixture-clock","fixture-session",-1,0,null,kind,new Dictionary<string,string>{{"attempt",attempt.ToString()}});}
        static VivePracticeJournalV3ReadResult Read(byte[] bytes,bool recover=false)
        {using(var input=new MemoryStream(bytes,false))return VivePracticeJournalV3Reader.Read(input,true,recover,1000,1000000);}
        static byte[] Complete()
        {
            var c=new Clock();var memory=new MemoryStream();var w=Writer(memory,c);c.Value=10.05;w.WriteTaskEvent(Task(5,10.05,"stimulus_presented"));
            c.Value=10.1;Check.Equal(VivePracticeV3EyeWrite.Written,w.WriteEye(Poll(1,10.1)));c.Value=10.2;w.MarkUnavailable(ViveDevelopmentGapReason.DeviceUnavailable);
            c.Value=10.3;Check.Equal(VivePracticeV3EyeWrite.Written,w.WriteEye(Poll(2,10.3)));c.Value=10.4;Check.True(w.Close());return memory.ToArray();
        }
        static byte[] RewriteTail(VivePracticeJournalV3ReadResult result,string end)
        {
            var memory=new MemoryStream();memory.Write(VivePracticeJournalV3Codec.Magic,0,VivePracticeJournalV3Codec.Magic.Length);
            var token=new byte[16];var prior=new byte[32];
            foreach(var record in result.Records)
            {
                var fields=new Dictionary<string,string>();foreach(var pair in record.Fields)fields.Add(pair.Key,pair.Value);
                if(record.Kind=="tail_unknown")fields["end_local_seconds"]=end;
                var payload=ContinuousJournal.Payload(record.Sequence,token,record.Kind,fields);var digest=ContinuousJournal.Digest(prior,payload);
                var prefix=new byte[8];ContinuousJournal.PutInt(prefix,payload.Length);ContinuousJournal.PutInt(prefix,~payload.Length,4);
                memory.Write(prefix,0,prefix.Length);memory.Write(payload,0,payload.Length);memory.Write(digest,0,digest.Length);prior=digest;
            }
            return memory.ToArray();
        }
        static byte[] WithoutBoundary(VivePracticeJournalV3ReadResult result)
        {
            var memory=new MemoryStream();memory.Write(VivePracticeJournalV3Codec.Magic,0,VivePracticeJournalV3Codec.Magic.Length);
            var token=new byte[16];var prior=new byte[32];long seq=0;
            foreach(var record in result.Records){if(record.Kind=="practice_start_boundary")continue;
                var fields=new Dictionary<string,string>();foreach(var pair in record.Fields)fields.Add(pair.Key,pair.Value);
                var payload=ContinuousJournal.Payload(seq++,token,record.Kind,fields);var digest=ContinuousJournal.Digest(prior,payload);
                var prefix=new byte[8];ContinuousJournal.PutInt(prefix,payload.Length);ContinuousJournal.PutInt(prefix,~payload.Length,4);
                memory.Write(prefix,0,prefix.Length);memory.Write(payload,0,payload.Length);memory.Write(digest,0,digest.Length);prior=digest;}
            return memory.ToArray();
        }
        static byte[] RewriteEye(VivePracticeJournalV3ReadResult result,Action<Dictionary<string,string>> mutate)
        {
            var memory=new MemoryStream();memory.Write(VivePracticeJournalV3Codec.Magic,0,VivePracticeJournalV3Codec.Magic.Length);
            var token=new byte[16];var prior=new byte[32];
            foreach(var record in result.Records){var fields=new Dictionary<string,string>();foreach(var pair in record.Fields)fields.Add(pair.Key,pair.Value);
                if(record.Kind=="eye_observation")mutate(fields);
                var payload=ContinuousJournal.Payload(record.Sequence,token,record.Kind,fields);var digest=ContinuousJournal.Digest(prior,payload);
                var prefix=new byte[8];ContinuousJournal.PutInt(prefix,payload.Length);ContinuousJournal.PutInt(prefix,~payload.Length,4);
                memory.Write(prefix,0,prefix.Length);memory.Write(payload,0,payload.Length);memory.Write(digest,0,digest.Length);prior=digest;}
            return memory.ToArray();
        }
        sealed class Probe:Stream
        {
            public int Calls;public override bool CanRead {get{Calls++;throw new Exception("private");}}public override bool CanWrite {get{Calls++;throw new Exception("private");}}
            public override bool CanSeek {get{Calls++;throw new Exception("private");}}public override long Length {get{Calls++;throw new Exception("private");}}
            public override long Position {get{Calls++;throw new Exception("private");}set{Calls++;throw new Exception("private");}}
            public override int Read(byte[] b,int o,int n){Calls++;throw new Exception("private");}public override void Write(byte[] b,int o,int n){Calls++;throw new Exception("private");}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin x){throw new NotSupportedException();}public override void SetLength(long x){throw new NotSupportedException();}
        }
        sealed class ThrowRead:Stream
        {
            readonly MemoryStream source;readonly int threshold;int read;
            public ThrowRead(byte[] bytes,int threshold){source=new MemoryStream(bytes,false);this.threshold=threshold;}
            public override bool CanRead {get{return true;}}public override bool CanWrite {get{return false;}}public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n){if(read>=threshold)throw new IOException("private-path-marker");int got=source.Read(b,o,Math.Min(n,threshold-read));read+=got;return got;}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin x){throw new NotSupportedException();}public override void SetLength(long x){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        sealed class FaultyStream:Stream
        {
            readonly MemoryStream store=new MemoryStream();public int Writes;public int FailAfter=int.MaxValue;public Action DuringWrite,DuringFlush;
            public byte[] Bytes(){return store.ToArray();}
            public override bool CanRead {get{return true;}}public override bool CanWrite {get{return true;}}public override bool CanSeek {get{return true;}}
            public override long Length {get{return store.Length;}}public override long Position {get{return store.Position;}set{store.Position=value;}}
            public override void Write(byte[] b,int o,int n){Writes++;if(DuringWrite!=null)DuringWrite();if(Writes>FailAfter)throw new IOException("private-path-marker");store.Write(b,o,n);}
            public override int Read(byte[] b,int o,int n){return store.Read(b,o,n);}public override void Flush(){if(DuringFlush!=null)DuringFlush();}
            public override long Seek(long o,SeekOrigin x){return store.Seek(o,x);}public override void SetLength(long x){store.SetLength(x);}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("V3 practice context is explicit and distinguishes retry/recovery",()=>{
                var initial=Context();Check.Equal(-1,initial.BlockIndex);Check.Equal((Condition?)null,initial.Condition);Check.Equal("UNSELECTED",initial.MainSourceStatus);
                var retry=Context(2,"epoch-2",0,"epoch-1",VivePracticeEpochReasonV3.PracticeRetry);Check.Equal(2,retry.PracticeAttempt);
                var resumed=Context(2,"epoch-3",1,"epoch-2",VivePracticeEpochReasonV3.ResumedAfterInterruption);Check.Equal(1,resumed.RecoveryOrdinal);
                Check.Throws<ArgumentException>(()=>Context(1,"same",1,"same",VivePracticeEpochReasonV3.ResumedAfterInterruption));
            });
            suite.Run("V3 practice requires both recording choices before inspecting stream",()=>{
                var p=new Probe();var c=new Clock();var ctx=Context();var preparation=Prepare(c,ctx);Check.Throws<InvalidOperationException>(()=>new VivePracticeJournalV3Writer(p,c,ctx,Start(ctx),ViveDevelopmentDataSource.SyntheticFixture,false,true,100,100000,preparation));
                Check.Equal(0,p.Calls);Check.Throws<InvalidOperationException>(()=>VivePracticeJournalV3Reader.Read(p,false,false,100,100000));Check.Equal(0,p.Calls);
            });
            suite.Run("V3 practice requires a matching declared start boundary before writing",()=>{
                var ctx=Context();var c=new Clock();
                Action<JournalEvent> reject=start=>{var stream=new MemoryStream();
                    var preparation=Prepare(c,ctx);Check.Throws<ArgumentException>(()=>new VivePracticeJournalV3Writer(stream,c,ctx,start,ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,preparation));
                    Check.Equal(0L,stream.Length);};
                reject(null);reject(Task(0,9,"practice_started",2));reject(Task(0,9.5,"practice_started"));
                reject(Task(0,9,"stimulus_presented"));
                reject(new JournalEvent(0,9,"fixture-clock","fixture-session",0,0,null,"practice_started",new Dictionary<string,string>{{"attempt","1"}}));
            });
            suite.Run("V3 preparation stores a complete caller-declared start after both choices",()=>{
                var c=new Clock();var ctx=Context();var p=Prepare(c,ctx);var stream=new MemoryStream();
                var fields=new Dictionary<string,string>{{"attempt","1"},{"extra","original"}};
                var start=new JournalEvent(17,9,"fixture-clock","fixture-session",-1,3,"trial-original","practice_started",fields);
                var w=new VivePracticeJournalV3Writer(stream,c,ctx,start,ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,p);
                c.Value=10.1;Check.True(w.Close());var result=Read(stream.ToArray());Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,result.Status);
                Check.True(result.HasDeclaredStartBoundary);
                var record=result.Records[1];Check.Equal("practice_start_boundary",record.Kind);Check.Equal("17",record.Fields["task_sequence"]);
                Check.Equal("9",record.Fields["observed_local_seconds"]);Check.Equal("3",record.Fields["task_segment"]);Check.Equal("trial-original",record.Fields["trial_id"]);
                ViveAuthoritativeTaskPayload payload;Check.True(ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(record.Fields["detail"],out payload));
                Check.Equal("practice_started",payload.Kind);Check.Equal("1",payload.Fields["attempt"]);Check.Equal("original",payload.Fields["extra"]);
                Check.True(!result.IdentityAuthenticated&&!result.ConsentAuthenticated&&!result.SourceClockBindingAuthenticated&&!result.ContinuousCoverageProven);
            });
            suite.Run("V3 legacy header-only start is distinguished from a recorded start",()=>{
                var result=Read(Complete());Check.True(result.HasDeclaredStartBoundary);
                var legacy=Read(WithoutBoundary(result));Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,legacy.Status);Check.True(!legacy.HasDeclaredStartBoundary);
                var bytes=Complete();bytes[bytes.Length-1]^=1;Check.True(!Read(bytes).HasDeclaredStartBoundary);
            });
            suite.Run("V3 preparation is one-shot and rejects same-tick, wrong clock and revocation",()=>{
                var c=new Clock();var ctx=Context();var p=Prepare(c,ctx);var s=new MemoryStream();
                var w=new VivePracticeJournalV3Writer(s,c,ctx,Start(ctx),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,p);
                Check.Throws<InvalidOperationException>(()=>new VivePracticeJournalV3Writer(new MemoryStream(),c,ctx,Start(ctx),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,p));
                Check.Throws<InvalidOperationException>(()=>p.Revoke());
                c.Value=10.1;Check.True(w.Close());
                c.Value=9;p=VivePracticeRecordingPreparationV3.Create(c,ctx.SessionId,1,ctx.RecordingEpochId,true,true);c.Value=10;s=new MemoryStream();
                Check.Throws<ArgumentException>(()=>new VivePracticeJournalV3Writer(s,c,ctx,Start(ctx),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,p));Check.Equal(0L,s.Length);
                Check.Throws<InvalidOperationException>(()=>new VivePracticeJournalV3Writer(new MemoryStream(),c,ctx,Start(ctx),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,p));
                p=Prepare(c,ctx);var other=new Clock();Check.Throws<InvalidOperationException>(()=>new VivePracticeJournalV3Writer(new MemoryStream(),other,ctx,Start(ctx),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,p));
                p=Prepare(c,ctx);p.Revoke();Check.Throws<InvalidOperationException>(()=>new VivePracticeJournalV3Writer(new MemoryStream(),c,ctx,Start(ctx),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,p));
                Check.Throws<InvalidOperationException>(()=>VivePracticeRecordingPreparationV3.Create(c,ctx.SessionId,1,ctx.RecordingEpochId,true,false));
            });
            suite.Run("V3 preparation rejects foreign thread, reentrant clock and uncertain writes",()=>{
                var c=new Clock();var ctx=Context();var p=Prepare(c,ctx);Exception seen=null;
                var thread=new Thread(()=>{try{new VivePracticeJournalV3Writer(new MemoryStream(),c,ctx,Start(ctx),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,p);}catch(Exception e){seen=e;}});
                thread.Start();Check.True(thread.Join(3000));Check.True(seen is InvalidOperationException);
                Check.Throws<InvalidOperationException>(()=>new VivePracticeJournalV3Writer(new MemoryStream(),c,ctx,Start(ctx),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,p));
                c.OnNow=()=>VivePracticeRecordingPreparationV3.Create(c,ctx.SessionId,1,ctx.RecordingEpochId,true,true);
                Check.Throws<InvalidOperationException>(()=>VivePracticeRecordingPreparationV3.Create(c,ctx.SessionId,1,ctx.RecordingEpochId,true,true));c.OnNow=null;
                p=Prepare(c,ctx);var stream=new FaultyStream();stream.FailAfter=2;
                Check.Throws<IOException>(()=>new VivePracticeJournalV3Writer(stream,c,ctx,Start(ctx),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,p));
                Check.Throws<InvalidOperationException>(()=>new VivePracticeJournalV3Writer(new MemoryStream(),c,ctx,Start(ctx),ViveDevelopmentDataSource.SyntheticFixture,true,true,100,100000,p));
                Check.True(Read(stream.Bytes(),true).Status!=ViveDevelopmentJournalReadStatus.CleanClose);
            });
            suite.Run("V3 practice round trips unknown windows and declared synthetic context",()=>{
                var result=Read(Complete());Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,result.Status);
                Check.True(!result.IdentityAuthenticated&&!result.SourceClockBindingAuthenticated&&!result.ConsentAuthenticated&&!result.HardwareCaptureTimeVerified&&
                    !result.ContinuousCoverageProven&&!result.CompleteSessionProven&&!result.DurableStorageProven&&!result.ResearchAcceptanceEstablished);
                Check.Equal("header",result.Records[0].Kind);var header=result.Records[0].Fields;
                Check.Equal("-1",header["block_index"]);Check.Equal(null,header["condition"]);Check.Equal("UNKNOWN",header["pre_consent_coverage"]);
                Check.Equal("UNSELECTED",header["main_source_status"]);Check.Equal("SyntheticFixture",header["data_source"]);
                Check.Equal("practice_start_boundary",result.Records[1].Kind);Check.Equal("CALLER_DECLARED",result.Records[1].Fields["binding"]);
                Check.Equal("8",result.Records[1].Fields["choices_observed_local_seconds"]);Check.Equal("9",result.Records[1].Fields["observed_local_seconds"]);
                int initial=0,recovery=0,tail=0;foreach(var r in result.Records){if(r.Kind=="initial_unknown")initial++;if(r.Kind=="recovery_unknown")recovery++;if(r.Kind=="tail_unknown")tail++;}
                Check.Equal(1,initial);Check.Equal(1,recovery);Check.Equal(1,tail);
            });
            suite.Run("V3 practice reader and legacy reader reject one another's magic",()=>{
                var bytes=Complete();using(var s=new MemoryStream(bytes,false))Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,ViveDevelopmentJournalReader.Read(s).Status);
                var old=Encoding.ASCII.GetBytes("VDEVJ001");Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(old,true).Status);
            });
            suite.Run("V3 eye extension preserves legacy unknown and rejects partial or conflicting flags",()=>{
                var result=Read(Complete());foreach(var row in result.Records)if(row.Kind=="eye_observation"){
                    Check.Equal("NOT_EVALUATED",row.HeadUsability);Check.Equal("NOT_COMPUTED",row.DerivedDirectionStatus);}
                string[] extra={"eye_extension_version","head_usability","head_observed_local_seconds","head_sequence","head_connected","head_rotation_reported_valid",
                    "head_qx_bits","head_qy_bits","head_qz_bits","head_qw_bits","candidate_checked_local_seconds","candidate_max_age_seconds","derived_status","derived_frame","derived_x_bits","derived_y_bits","derived_z_bits"};
                var legacy=Read(RewriteEye(result,fields=>{foreach(string key in extra)fields.Remove(key);}));
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,legacy.Status);foreach(var row in legacy.Records)if(row.Kind=="eye_observation"){
                    Check.Equal("UNKNOWN",row.HeadUsability);Check.Equal("UNKNOWN",row.DerivedDirectionStatus);}
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(RewriteEye(result,fields=>fields.Remove("head_qx_bits")),true).Status);
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(RewriteEye(result,fields=>fields["derived_status"]="SOURCE_REPORTED_AVAILABLE"),true).Status);
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(RewriteEye(result,fields=>{
                    fields["head_usability"]="LOCAL_ASSESSMENT_UNAVAILABLE";fields["derived_status"]="LOCAL_ASSESSMENT_UNAVAILABLE";
                    fields["candidate_checked_local_seconds"]="11";fields["candidate_max_age_seconds"]="0.2";}),true).Status);
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,Read(RewriteEye(result,fields=>{
                    fields["head_usability"]="LOCAL_ASSESSMENT_USABLE";fields["derived_status"]="LOCAL_ASSESSMENT_UNAVAILABLE";
                    fields["candidate_checked_local_seconds"]="10.3";fields["candidate_max_age_seconds"]="1";
                    fields["head_observed_local_seconds"]="10.05";fields["head_sequence"]="1";
                    fields["head_connected"]="true";fields["head_rotation_reported_valid"]="true";
                    fields["head_qx_bits"]=VivePracticeJournalV3Codec.Bits(0);fields["head_qy_bits"]=VivePracticeJournalV3Codec.Bits(0);
                    fields["head_qz_bits"]=VivePracticeJournalV3Codec.Bits(0);fields["head_qw_bits"]=VivePracticeJournalV3Codec.Bits(1);}),true).Status);
            });
            suite.Run("V3 candidate is rejected before writes while identity is unbound",()=>{
                foreach(bool declareContext in new[]{false,true}){
                    var c=new Clock();var stream=new MemoryStream();var ctx=Context();var w=Writer(stream,c,ctx);long before=stream.Length;c.Value=10.15;
                    var candidate=new VivePracticeEyeCandidate(Poll(1,10.1),new Vec3(0,0,1),true,"fixture-map",10.02,10.1,1,0,10.01,c,0.2,declareContext?ctx:null);
                    Check.Equal(VivePracticeV3EyeWrite.Rejected,w.WriteCandidate(candidate));
                    Check.Equal(VivePracticeV3WriterState.Faulted,w.State);Check.Equal(before,stream.Length);
                    Check.True(Read(stream.ToArray(),true).Status!=ViveDevelopmentJournalReadStatus.CleanClose);
                }
            });
            suite.Run("V3 raw valid eye survives absent head without derived claims",()=>{
                var c=new Clock();var stream=new MemoryStream();var w=Writer(stream,c);Check.True(!w.ProductionGateOpen);var original=Poll(1,10.1);
                var raw=new ViveProEyePoll(null,original.Eye,original.Runtime,original.Calibration,true,0);
                c.Value=10.1;Check.Equal(VivePracticeV3EyeWrite.Written,w.WriteEye(raw));c.Value=10.2;Check.True(w.Close());
                var r=Read(stream.ToArray());Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,r.Status);
                foreach(var row in r.Records)if(row.Kind=="eye_observation"){
                    Check.Equal("NOT_EVALUATED",row.HeadUsability);Check.Equal("NOT_COMPUTED",row.DerivedDirectionStatus);
                    Check.Equal(VivePracticeJournalV3Codec.Bits(1),row.Fields["raw_z_bits"]);Check.Equal(null,row.Fields["derived_x_bits"]);
                }
            });
            suite.Run("V3 practice all truncated prefixes are never clean",()=>{
                var bytes=Complete();for(int n=0;n<bytes.Length;n++){var prefix=new byte[n];Buffer.BlockCopy(bytes,0,prefix,0,n);
                    var strict=Read(prefix);Check.True(strict.Status!=ViveDevelopmentJournalReadStatus.CleanClose);Check.Equal(0,strict.Records.Count);}
            });
            suite.Run("V3 practice corrupt data is rejected or separately recovered",()=>{
                var bytes=Complete();bytes[bytes.Length-1]^=1;var strict=Read(bytes);Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,strict.Status);Check.Equal(0,strict.Records.Count);
                var recovered=Read(bytes,true);Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,recovered.Status);Check.True(recovered.Records.Count>0&&!recovered.CompleteSessionProven);
                var bad=Complete();bad[0]^=1;Check.Equal(0,Read(bad,true).Records.Count);
            });
            suite.Run("V3 practice incomplete and read failure stay distinct",()=>{
                var memory=new MemoryStream();var c=new Clock();var writer=Writer(memory,c);writer.Abort();var bytes=memory.ToArray();
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(bytes).Status);Check.Equal(0,Read(bytes).Records.Count);Check.True(Read(bytes,true).Records.Count>0);
                var complete=Complete();using(var s=new ThrowRead(complete,complete.Length-5)){
                    var r=VivePracticeJournalV3Reader.Read(s,true,true,100,100000);Check.Equal(ViveDevelopmentJournalReadStatus.ReadFailure,r.Status);Check.Equal(0,r.Records.Count);
                    Check.True(r.Issue.IndexOf("private",StringComparison.Ordinal)<0);}
            });
            suite.Run("V3 practice record budget failure never returns a prefix",()=>{
                var bytes=Complete();int count=Read(bytes).Records.Count;
                using(var s=new MemoryStream(bytes,false)){
                    var exact=VivePracticeJournalV3Reader.Read(s,true,false,count,100000);
                    Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,exact.Status);Check.Equal(count,exact.Records.Count);}
                foreach(bool recover in new[]{false,true})using(var s=new MemoryStream(bytes,false)){
                    var limited=VivePracticeJournalV3Reader.Read(s,true,recover,count-1,100000);
                    Check.Equal(ViveDevelopmentJournalReadStatus.ReadFailure,limited.Status);Check.Equal(0,limited.Records.Count);
                    Check.Equal("read_or_limit_failed",limited.Issue);}
            });
            suite.Run("V3 practice rejects preconsent and stale recovery eye frames",()=>{
                var c=new Clock();var memory=new MemoryStream();var w=Writer(memory,c);Check.Equal(VivePracticeV3EyeWrite.Rejected,w.WriteEye(Poll(1,10)));Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
                c=new Clock();memory=new MemoryStream();w=Writer(memory,c);c.Value=10.1;Check.Equal(VivePracticeV3EyeWrite.Written,w.WriteEye(Poll(1,10.1)));
                c.Value=10.2;w.MarkUnavailable(ViveDevelopmentGapReason.DeviceUnavailable);c.Value=10.3;
                Check.Equal(VivePracticeV3EyeWrite.WaitingForFreshRecovery,w.WriteEye(Poll(2,10.15)));Check.Equal(VivePracticeV3EyeWrite.Written,w.WriteEye(Poll(3,10.3)));
                c.Value=10.4;Check.True(w.Close());Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(memory.ToArray()).Status);
            });
            suite.Run("V3 duplicate eye invalidity is explicitly unavailable, not ignored",()=>{
                var c=new Clock();var memory=new MemoryStream();var w=Writer(memory,c);c.Value=10.1;Check.Equal(VivePracticeV3EyeWrite.Written,w.WriteEye(Poll(1,10.1)));
                c.Value=10.2;Check.Equal(VivePracticeV3EyeWrite.Unavailable,w.WriteEye(Poll(1,10.1,connected:false)));
                c.Value=10.3;Check.Equal(VivePracticeV3EyeWrite.Written,w.WriteEye(Poll(2,10.3)));c.Value=10.4;Check.True(w.Close());
                var r=Read(memory.ToArray());Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,r.Status);int unavailable=0;foreach(var row in r.Records)if(row.Kind=="unavailable_detected")unavailable++;Check.Equal(1,unavailable);
            });
            suite.Run("V3 practice zero frame close keeps unknown tail",()=>{
                var c=new Clock();var memory=new MemoryStream();var w=Writer(memory,c);c.Value=10.1;Check.True(w.Close());var r=Read(memory.ToArray());
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,r.Status);Check.Equal("no_observation_yet",r.Records[r.Records.Count-2].Fields["reason"]);Check.True(!r.ContinuousCoverageProven);
            });
            suite.Run("V3 practice task attempt and clock rollback fail closed",()=>{
                var c=new Clock();var memory=new MemoryStream();var w=Writer(memory,c);c.Value=10.1;Check.Throws<ArgumentException>(()=>w.WriteTaskEvent(Task(1,10.1,"practice_completed",2)));Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
                c=new Clock();memory=new MemoryStream();w=Writer(memory,c);c.Value=9.9;Check.Throws<InvalidOperationException>(()=>w.Close());Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
                c=new Clock();memory=new MemoryStream();w=Writer(memory,c);c.Broken=true;Check.Throws<InvalidOperationException>(()=>w.Close());Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
            });
            suite.Run("V3 practice task timestamps must be strictly after enablement",()=>{
                var c=new Clock();var memory=new MemoryStream();var w=Writer(memory,c);
                Check.Throws<ArgumentException>(()=>w.WriteTaskEvent(Task(1,10,"stimulus_presented")));Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
                c=new Clock();memory=new MemoryStream();w=Writer(memory,c);c.Value=10.1;
                Check.Throws<ArgumentException>(()=>w.WriteTaskEvent(Task(1,10.1,"practice_started")));Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
            });
            suite.Run("V3 practice preserves original task segment",()=>{
                var c=new Clock();var memory=new MemoryStream();var w=Writer(memory,c);c.Value=10.1;
                var item=new JournalEvent(1,10.1,"fixture-clock","fixture-session",-1,3,null,"practice_completed",new Dictionary<string,string>{{"attempt","1"}});
                w.WriteTaskEvent(item);c.Value=10.2;Check.True(w.Close());var r=Read(memory.ToArray());
                foreach(var record in r.Records)if(record.Kind=="task_event")Check.Equal("3",record.Fields["task_segment"]);
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,r.Status);
            });
            suite.Run("V3 practice rejects empty task kinds and latches fault",()=>{
                foreach(var kind in new[]{"","   "}){
                    var c=new Clock();var memory=new MemoryStream();var w=Writer(memory,c);c.Value=10.1;
                    Check.Throws<ArgumentException>(()=>w.WriteTaskEvent(Task(1,10.1,kind)));
                    Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
                }
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(Complete()).Status);
            });
            suite.Run("V3 practice reader rejects a rehashed impossible unknown tail",()=>{
                var c=new Clock();var memory=new MemoryStream();var w=Writer(memory,c);c.Value=10.1;w.WriteEye(Poll(1,10.1));
                c.Value=10.2;w.MarkUnavailable(ViveDevelopmentGapReason.DeviceUnavailable);c.Value=10.3;Check.True(w.Close());
                var rewritten=RewriteTail(Read(memory.ToArray()),"10.15");var result=Read(rewritten,true);
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,result.Status);Check.True(!result.CompleteSessionProven);
            });
            suite.Run("V3 practice write and flush failure never report clean writer state",()=>{
                var c=new Clock();var f=new FaultyStream();var w=Writer(f,c);f.FailAfter=f.Writes;c.Value=10.1;
                Check.Throws<IOException>(()=>w.WriteEye(Poll(1,10.1)));Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
                f=new FaultyStream();c=new Clock();w=Writer(f,c);f.DuringFlush=()=>{throw new IOException("private");};c.Value=10.1;
                Check.True(!w.Close());Check.Equal(VivePracticeV3WriterState.CloseOutcomeUncertain,w.State);
                f=new FaultyStream();c=new Clock();w=Writer(f,c);f.DuringWrite=()=>w.Abort();c.Value=10.1;
                Check.Throws<IOException>(()=>w.WriteTaskEvent(Task(1,10.1,"stimulus_presented")));Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
            });
            suite.Run("V3 practice cross-thread fault cannot be overwritten by close",()=>{
                var c=new Clock();var f=new FaultyStream();var w=Writer(f,c);Exception seen=null;
                f.DuringFlush=()=>{var t=new Thread(()=>{try{w.Abort();}catch(Exception e){seen=e;}});t.Start();Check.True(t.Join(3000));};
                c.Value=10.1;Check.True(!w.Close());Check.True(seen is InvalidOperationException);Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
            });
            suite.Run("V3 practice reentrant clock cannot write after a latched fault",()=>{
                var c=new Clock();var memory=new MemoryStream();var w=Writer(memory,c);long before=memory.Length;c.Value=10.1;
                c.OnNow=()=>{try{w.Abort();}catch(InvalidOperationException){}};
                Check.Throws<InvalidOperationException>(()=>w.WriteTaskEvent(Task(1,10.1,"stimulus_presented")));
                Check.Equal(before,memory.Length);Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
            });
            suite.Run("V3 practice limits bound records and bytes without File.Move",()=>{
                var c=new Clock();var memory=new MemoryStream();var w=Writer(memory,c,records:5);c.Value=10.01;w.WriteTaskEvent(Task(1,10.01,"stimulus_presented"));c.Value=10.02;w.WriteTaskEvent(Task(2,10.02,"practice_completed"));c.Value=10.1;
                Check.Throws<IOException>(()=>w.WriteEye(Poll(1,10.1)));Check.Equal(VivePracticeV3WriterState.Faulted,w.State);
                using(var s=new MemoryStream(Complete(),false)){var r=VivePracticeJournalV3Reader.Read(s,true,true,100,64);Check.Equal(ViveDevelopmentJournalReadStatus.ReadFailure,r.Status);Check.Equal(0,r.Records.Count);}
            });
        }
    }
}
