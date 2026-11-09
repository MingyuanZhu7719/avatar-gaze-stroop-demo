using System;
using System.Security.Cryptography;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Only an explicitly instrumented isolated source copy calls this hook.
    internal static class ViveSyntheticWorkerTestHook
    {internal static Action<string> Callback;internal static void Invoke(string point){var action=Callback;if(action!=null)action(point);}}
    // Isolated fake memory only; no participant, files, devices or network.
    public static class ViveSyntheticCommitWorkerTests
    {
        static byte[] Frame(int ordinal,int payload)
        {var result=new byte[payload+(ordinal==0?177:169)];for(int i=0;i<result.Length;i++)result[i]=(byte)(i*13+ordinal);return result;}
        static void Submit(ViveSyntheticCommitWorker w,long n,object id,byte[] bytes,int payload,ViveSyntheticWorkerPlan plan)
        {
            for(int i=0;i<5000;i++)
            {var r=w.TrySubmit(n,id,bytes,payload,plan);if(r==ViveSyntheticWorkerAdmission.Queued)return;Check.Equal(ViveSyntheticWorkerAdmission.Busy,r);Thread.Sleep(1);}
            throw new Exception("bounded submit wait exceeded");
        }
        static ViveSyntheticWorkerAck Take(ViveSyntheticCommitWorker w)
        {
            for(int i=0;i<5000;i++)
            {ViveSyntheticWorkerAck ack;var r=w.TryTakeAck(out ack);if(r==ViveSyntheticWorkerAckRead.Available)return ack;
                Check.True(r==ViveSyntheticWorkerAckRead.Busy||r==ViveSyntheticWorkerAckRead.Empty);Thread.Sleep(1);}
            throw new Exception("bounded ack wait exceeded");
        }
        static byte[] Bytes(ViveSyntheticCommitWorker w,out bool fault,out bool revoked,out string reason)
        {
            for(int i=0;i<5000;i++){byte[] bytes;if(w.TryCopyFakeBytes(out bytes,out fault,out revoked,out reason))return bytes;Thread.Sleep(1);}
            throw new Exception("bounded fake snapshot wait exceeded");
        }
        static void EqualBytes(byte[] a,byte[] b)
        {Check.True(a!=null&&b!=null&&a.Length==b.Length);for(int i=0;i<a.Length;i++)Check.Equal(a[i],b[i]);}
        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic worker clones frames and returns unique FIFO identities and digest",()=>{
                using(var w=new ViveSyntheticCommitWorker(3,1024,3,4096))
                {
                    var a=Frame(0,12);var b=Frame(1,7);var original=(byte[])a.Clone();var idA=new object();var idB=new object();
                    Submit(w,0,idA,a,12,ViveSyntheticWorkerPlan.Complete);a[0]^=255;Submit(w,1,idB,b,7,ViveSyntheticWorkerPlan.Complete);
                    var first=Take(w);var second=Take(w);Check.Equal(0L,first.Sequence);Check.Equal(1L,second.Sequence);
                    Check.True(Object.ReferenceEquals(idA,first.Identity)&&Object.ReferenceEquals(idB,second.Identity));
                    Check.Equal(ViveSyntheticWorkerOutcome.Accepted,first.Outcome);Check.Equal(ViveSyntheticWorkerOutcome.Accepted,second.Outcome);
                    byte[] hash;using(var sha=SHA256.Create())hash=sha.ComputeHash(original);EqualBytes(hash,first.CopyDigest());
                    var returned=first.CopyDigest();returned[0]^=255;EqualBytes(hash,first.CopyDigest());
                    ViveSyntheticWorkerAck extra;Check.Equal(ViveSyntheticWorkerAckRead.Empty,w.TryTakeAck(out extra));Check.True(extra==null);
                    bool fault,revoked;string reason;var sink=Bytes(w,out fault,out revoked,out reason);
                    var expected=new byte[original.Length+b.Length];Buffer.BlockCopy(original,0,expected,0,original.Length);Buffer.BlockCopy(b,0,expected,original.Length,b.Length);
                    EqualBytes(expected,sink);Check.True(!fault&&!revoked&&reason==null&&!w.ResearchAdmission&&!w.RealStorage&&!w.ConsentAuthenticated);
                }
            });
            suite.Run("synthetic worker bounded outstanding acks apply nonblocking backpressure",()=>{
                using(var w=new ViveSyntheticCommitWorker(1,1024,2,4096))
                {
                    var first=new object();var second=new object();Submit(w,0,first,Frame(0,1),1,ViveSyntheticWorkerPlan.Complete);
                    Check.Equal(ViveSyntheticWorkerAdmission.Busy,w.TrySubmit(1,second,Frame(1,1),1,ViveSyntheticWorkerPlan.Complete));
                    Check.Equal(0L,Take(w).Sequence);Submit(w,1,second,Frame(1,1),1,ViveSyntheticWorkerPlan.Complete);Check.Equal(1L,Take(w).Sequence);
                }
            });
            suite.Run("synthetic worker rejects repeated identity and out of order ordinal",()=>{
                using(var w=new ViveSyntheticCommitWorker(1,1024,2,4096))
                {var id=new object();Submit(w,0,id,Frame(0,1),1,ViveSyntheticWorkerPlan.Complete);Take(w);
                    Check.Equal(ViveSyntheticWorkerAdmission.Rejected,w.TrySubmit(1,id,Frame(1,1),1,ViveSyntheticWorkerPlan.Complete));}
                using(var w=new ViveSyntheticCommitWorker(1,1024,2,4096))
                {Check.Equal(ViveSyntheticWorkerAdmission.Rejected,w.TrySubmit(1,new object(),Frame(1,1),1,ViveSyntheticWorkerPlan.Complete));
                    Check.Equal(ViveSyntheticWorkerAdmission.Rejected,w.TrySubmit(0,new object(),Frame(0,1),1,ViveSyntheticWorkerPlan.Complete));}
            });
            suite.Run("synthetic worker accepts exact frame and payload maximums",()=>{
                using(var w=new ViveSyntheticCommitWorker(2,262144,2,524288))
                {Submit(w,0,new object(),Frame(0,261967),261967,ViveSyntheticWorkerPlan.Complete);
                    Submit(w,1,new object(),Frame(1,261975),261975,ViveSyntheticWorkerPlan.Complete);
                    Check.Equal(ViveSyntheticWorkerOutcome.Accepted,Take(w).Outcome);Check.Equal(ViveSyntheticWorkerOutcome.Accepted,Take(w).Outcome);}
            });
            suite.Run("synthetic worker rejects oversize and wrong frame arithmetic",()=>{
                using(var w=new ViveSyntheticCommitWorker(1,262144,1,524288))
                {Check.Equal(ViveSyntheticWorkerAdmission.Rejected,w.TrySubmit(0,new object(),Frame(0,261968),261968,ViveSyntheticWorkerPlan.Complete));}
                using(var w=new ViveSyntheticCommitWorker(1,1024,1,4096))
                {Check.Equal(ViveSyntheticWorkerAdmission.Rejected,w.TrySubmit(0,new object(),Frame(0,4),5,ViveSyntheticWorkerPlan.Complete));}
            });
            suite.Run("synthetic worker marks partial and full fake failures uncertain without retry",()=>{
                foreach(var plan in new[]{ViveSyntheticWorkerPlan.PartialThenFail,ViveSyntheticWorkerPlan.CompleteThenFail})
                using(var w=new ViveSyntheticCommitWorker(1,1024,2,4096))
                {var frame=Frame(0,8);Submit(w,0,new object(),frame,8,plan);var ack=Take(w);Check.Equal(ViveSyntheticWorkerOutcome.Uncertain,ack.Outcome);
                    bool fault,revoked;string issue;var bytes=Bytes(w,out fault,out revoked,out issue);Check.True(fault&&!revoked&&issue=="fake_write_uncertain");
                    Check.Equal(plan==ViveSyntheticWorkerPlan.PartialThenFail?frame.Length/2:frame.Length,bytes.Length);
                    Check.Equal(ViveSyntheticWorkerAdmission.Rejected,w.TrySubmit(1,new object(),Frame(1,1),1,ViveSyntheticWorkerPlan.Complete));}
            });
            suite.Run("synthetic worker capacity rejection has no partial fake write",()=>{
                using(var w=new ViveSyntheticCommitWorker(1,1024,2,177))
                {Submit(w,0,new object(),Frame(0,1),1,ViveSyntheticWorkerPlan.Complete);var ack=Take(w);Check.Equal(ViveSyntheticWorkerOutcome.NotWritten,ack.Outcome);
                    bool fault,revoked;string issue;var bytes=Bytes(w,out fault,out revoked,out issue);Check.Equal(0,bytes.Length);Check.True(fault&&!revoked);}
            });
            suite.Run("synthetic worker revocation refuses future submissions",()=>{
                using(var w=new ViveSyntheticCommitWorker(1,1024,1,4096))
                {w.Revoke();Check.Equal(ViveSyntheticWorkerAdmission.Rejected,w.TrySubmit(0,new object(),Frame(0,1),1,ViveSyntheticWorkerPlan.Complete));
                    bool fault,revoked;string issue;Check.Equal(0,Bytes(w,out fault,out revoked,out issue).Length);Check.True(revoked);}
            });
            suite.Run("synthetic worker submit ack and revoke never wait for worker lock",()=>{
                using(var w=new ViveSyntheticCommitWorker(1,1024,1,4096))
                using(var held=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {
                    var field=typeof(ViveSyntheticCommitWorker).GetField("gate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);Check.True(field!=null);
                    object gate=field.GetValue(w);var holder=new Thread(()=>{lock(gate){held.Set();release.WaitOne(5000);}}){IsBackground=true};holder.Start();Check.True(held.WaitOne(5000));
                    try
                    {Check.Equal(ViveSyntheticWorkerAdmission.Busy,w.TrySubmit(0,new object(),Frame(0,1),1,ViveSyntheticWorkerPlan.Complete));
                        ViveSyntheticWorkerAck ack;Check.Equal(ViveSyntheticWorkerAckRead.Busy,w.TryTakeAck(out ack));w.Revoke();}
                    finally{release.Set();}
                    Check.True(holder.Join(5000));Check.Equal(ViveSyntheticWorkerAdmission.Rejected,w.TrySubmit(0,new object(),Frame(0,1),1,ViveSyntheticWorkerPlan.Complete));
                }
            });
        }
        // Do not register in a build containing the original worker: these
        // cases require isolated, test-only hooks around the reservation CAS.
        public static void RegisterInstrumented(TestSuite suite)
        {
            foreach(var point in new[]{"before_reserve","after_reserve","after_commit"})
            {
                string phase=point;
                suite.Run("INSTRUMENTED synthetic revoke at "+phase,()=>{
                    using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                    using(var w=new ViveSyntheticCommitWorker(1,1024,1,4096))
                    {
                        ViveSyntheticWorkerTestHook.Callback=actual=>{if(actual==phase){entered.Set();if(!release.WaitOne(5000))throw new InvalidOperationException("test barrier timed out");}};
                        try
                        {
                            var id=new object();var frame=Frame(0,3);Submit(w,0,id,frame,3,ViveSyntheticWorkerPlan.Complete);
                            Check.True(entered.WaitOne(5000),"the expected test-only CAS hook was not reached");
                            w.Revoke();release.Set();var ack=Take(w);Check.True(Object.ReferenceEquals(id,ack.Identity));
                            var expected=phase=="before_reserve"?ViveSyntheticWorkerOutcome.NotWritten:
                                phase=="after_reserve"?ViveSyntheticWorkerOutcome.Uncertain:ViveSyntheticWorkerOutcome.Accepted;
                            Check.Equal(expected,ack.Outcome);bool fault,revoked;string reason;var bytes=Bytes(w,out fault,out revoked,out reason);
                            Check.True(revoked);Check.Equal(phase=="before_reserve"?0:frame.Length,bytes.Length);
                            Check.Equal(ViveSyntheticWorkerAdmission.Rejected,w.TrySubmit(1,new object(),Frame(1,1),1,ViveSyntheticWorkerPlan.Complete));
                        }
                        finally{release.Set();ViveSyntheticWorkerTestHook.Callback=null;}
                    }
                });
            }
            foreach(var point in new[]{"before_admission_commit","after_admission_commit"})
            {
                string phase=point;
                suite.Run("INSTRUMENTED synthetic admission revoke at "+phase,()=>{
                    using(var w=new ViveSyntheticCommitWorker(1,1024,1,4096))
                    {
                        bool joined=false;
                        ViveSyntheticWorkerTestHook.Callback=actual=>{
                            if(actual!=phase)return;var thread=new Thread(w.Revoke){IsBackground=true};
                            thread.Start();joined=thread.Join(5000);
                        };
                        try
                        {
                            var status=w.TrySubmit(0,new object(),Frame(0,2),2,ViveSyntheticWorkerPlan.Complete);
                            Check.True(joined,"revocation must not wait for admission lock");
                            if(phase=="before_admission_commit")Check.Equal(ViveSyntheticWorkerAdmission.Rejected,status);
                            else{Check.Equal(ViveSyntheticWorkerAdmission.Queued,status);Check.Equal(ViveSyntheticWorkerOutcome.NotWritten,Take(w).Outcome);}
                            bool fault,revoked;string reason;Check.Equal(0,Bytes(w,out fault,out revoked,out reason).Length);Check.True(revoked);
                        }
                        finally{ViveSyntheticWorkerTestHook.Callback=null;}
                    }
                });
            }
        }
    }
}
