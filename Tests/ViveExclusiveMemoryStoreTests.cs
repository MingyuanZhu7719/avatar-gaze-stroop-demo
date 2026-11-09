using System;
using System.Security.Cryptography;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Fake opaque identities and private memory only: no OS path, real file,
    // native lock, symlink defense, durability or actual consent is exercised.
    public static class ViveExclusiveMemoryStoreTests
    {
        static ViveExclusiveMemoryTarget Create(ViveExclusiveMemoryStore store,string id,int max=64)
        {
            ViveExclusiveMemoryTarget target;
            Check.Equal(ViveFakeCreateResult.Created,store.TryCreateNew(id,max,out target));
            Check.True(target!=null);return target;
        }
        static ViveFakeWritePlan Full(byte[] bytes,Action hook=null){return new ViveFakeWritePlan(bytes.Length,false,hook);}
        static void Bytes(byte[] expected,byte[] actual)
        {
            Check.True(actual!=null&&expected.Length==actual.Length,"Unexpected byte count");
            for(int i=0;i<expected.Length;i++)Check.Equal(expected[i],actual[i],"byte "+i);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("exclusive memory CreateNew refuses existing id and preserves its bytes",()=>{
                var store=new ViveExclusiveMemoryStore(8);var target=Create(store,"one");var input=new byte[]{1,2,3};
                Check.Equal(ViveFakeWriteResult.Accepted,target.Append(input,Full(input)));ViveExclusiveMemoryTarget duplicate;
                Check.Equal(ViveFakeCreateResult.AlreadyExists,store.TryCreateNew("one",64,out duplicate));
                Bytes(new byte[]{1,2,3},store.CopyForTest("one"));
            });
            suite.Run("exclusive memory concurrent CreateNew has exactly one winner",()=>{
                var store=new ViveExclusiveMemoryStore(8);const int count=8;var start=new ManualResetEvent(false);
                var done=new CountdownEvent(count);var result=new ViveFakeCreateResult[count];var errors=new Exception[count];
                for(int i=0;i<count;i++)
                {
                    int index=i;var thread=new Thread(()=>{try{start.WaitOne();ViveExclusiveMemoryTarget target;
                        result[index]=store.TryCreateNew("same",64,out target);}catch(Exception e){errors[index]=e;}finally{done.Signal();}});
                    thread.IsBackground=true;thread.Start();
                }
                start.Set();Check.True(done.Wait(5000));int created=0,existing=0;
                for(int i=0;i<count;i++){Check.True(errors[i]==null);if(result[i]==ViveFakeCreateResult.Created)created++;
                    else if(result[i]==ViveFakeCreateResult.AlreadyExists)existing++;}
                Check.Equal(1,created);Check.Equal(count-1,existing);Bytes(new byte[0],store.CopyForTest("same"));
            });
            suite.Run("exclusive memory keeps ids isolated and returns defensive copies",()=>{
                var store=new ViveExclusiveMemoryStore(4);var first=Create(store,"first");var second=Create(store,"second");
                var input=new byte[]{4,5,6};Check.Equal(ViveFakeWriteResult.Accepted,first.Append(input,Full(input,()=>input[0]=99)));
                var other=new byte[]{8,9};Check.Equal(ViveFakeWriteResult.Accepted,second.Append(other,Full(other)));
                var copy=store.CopyForTest("first");Bytes(new byte[]{4,5,6},copy);copy[1]=77;
                Bytes(new byte[]{4,5,6},store.CopyForTest("first"));Bytes(new byte[]{8,9},store.CopyForTest("second"));
            });
            suite.Run("exclusive memory rejects invalid create and enforces entry limit",()=>{
                var store=new ViveExclusiveMemoryStore(1);ViveExclusiveMemoryTarget target;
                Check.Equal(ViveFakeCreateResult.Rejected,store.TryCreateNew(null,64,out target));
                Check.Equal(ViveFakeCreateResult.Rejected,store.TryCreateNew("",64,out target));
                Check.True(store.CopyForTest(null)==null&&store.CopyForTest("../bad")==null&&store.CopyForTest("missing")==null);
                Create(store,"one");Check.Equal(ViveFakeCreateResult.Rejected,store.TryCreateNew("two",64,out target));
            });
            suite.Run("exclusive memory cumulative budget faults before modifying preserved prefix",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"budget",4);
                var first=new byte[]{1,2,3};Check.Equal(ViveFakeWriteResult.Accepted,target.Append(first,Full(first)));
                var next=new byte[]{4,5};Check.Equal(ViveFakeWriteResult.Rejected,target.Append(next,Full(next)));
                Check.Equal(ViveFakeTargetState.Faulted,target.State);Bytes(first,store.CopyForTest("budget"));
                Check.Equal(ViveFakeWriteResult.Rejected,target.Append(new byte[]{4},new ViveFakeWritePlan(1,false,null)));
                Bytes(first,store.CopyForTest("budget"));
            });
            suite.Run("exclusive memory preserves partial prefix and refuses retry after failure",()=>{
                foreach(int prefix in new[]{0,2,4})
                {
                    var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"partial");
                    var payload=new byte[]{1,2,3,4};Check.Equal(ViveFakeWriteResult.Uncertain,target.Append(payload,new ViveFakeWritePlan(prefix,true,null)));
                    var expected=new byte[prefix];Array.Copy(payload,expected,prefix);Bytes(expected,store.CopyForTest("partial"));
                    Check.Equal(ViveFakeTargetState.Uncertain,target.State);
                    ViveExclusiveMemoryTarget duplicate;Check.Equal(ViveFakeCreateResult.AlreadyExists,store.TryCreateNew("partial",64,out duplicate));
                    Check.Equal(ViveFakeWriteResult.Rejected,target.Append(payload,Full(payload)));Bytes(expected,store.CopyForTest("partial"));
                }
            });
            suite.Run("exclusive memory rejects a short successful fake write as uncertain",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"short");
                var payload=new byte[]{5,6,7};Check.Equal(ViveFakeWriteResult.Uncertain,target.Append(payload,new ViveFakeWritePlan(1,false,null)));
                Bytes(new byte[]{5},store.CopyForTest("short"));Check.Equal(ViveFakeTargetState.Uncertain,target.State);
            });
            suite.Run("exclusive memory withdrawal before write prohibits all new fake writes",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"before");target.Withdraw();
                Check.Equal(ViveFakeTargetState.Withdrawn,target.State);
                Check.Equal(ViveFakeWriteResult.Rejected,target.Append(new byte[]{1},new ViveFakeWritePlan(1,false,null)));
                Bytes(new byte[0],store.CopyForTest("before"));Check.True(!target.Complete());
            });
            suite.Run("exclusive memory withdrawal inside write retains prefix and is uncertain",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"withdraw");var bytes=new byte[]{3,4,5};
                Check.Equal(ViveFakeWriteResult.Uncertain,target.Append(bytes,new ViveFakeWritePlan(2,false,target.Withdraw)));
                Check.Equal(ViveFakeTargetState.Uncertain,target.State);Bytes(new byte[]{3,4},store.CopyForTest("withdraw"));
                target.Withdraw();Check.Equal(ViveFakeWriteResult.Rejected,target.Append(bytes,Full(bytes)));
                ViveExclusiveMemoryTarget duplicate;Check.Equal(ViveFakeCreateResult.AlreadyExists,store.TryCreateNew("withdraw",64,out duplicate));
                Bytes(new byte[]{3,4},store.CopyForTest("withdraw"));
            });
            suite.Run("exclusive memory withdrawal from another thread never waits on fake callback",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"concurrent");bool finished=false;
                var data=new byte[]{7,8};
                Action callback=()=>{var t=new Thread(()=>{target.Withdraw();finished=true;});t.IsBackground=true;t.Start();Check.True(t.Join(5000));};
                Check.Equal(ViveFakeWriteResult.Uncertain,target.Append(data,Full(data,callback)));
                Check.True(finished);Check.Equal(ViveFakeTargetState.Uncertain,target.State);Bytes(data,store.CopyForTest("concurrent"));
            });
            suite.Run("exclusive memory reentrant append faults outer write and preserves prefix",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"reentry");var data=new byte[]{1,2};
                ViveFakeWriteResult nested=ViveFakeWriteResult.Accepted;
                var result=target.Append(data,Full(data,()=>nested=target.Append(new byte[]{9},new ViveFakeWritePlan(1,false,null))));
                Check.Equal(ViveFakeWriteResult.Uncertain,nested);Check.Equal(ViveFakeWriteResult.Uncertain,result);
                Check.Equal(ViveFakeTargetState.Uncertain,target.State);Bytes(data,store.CopyForTest("reentry"));
            });
            suite.Run("exclusive memory callback exception leaves previously written prefix uncertain",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"throws");var data=new byte[]{1,2,3};
                Check.Equal(ViveFakeWriteResult.Uncertain,target.Append(data,new ViveFakeWritePlan(2,false,()=>{throw new InvalidOperationException();})));
                Bytes(new byte[]{1,2},store.CopyForTest("throws"));Check.Equal(ViveFakeTargetState.Uncertain,target.State);
                Check.True(!target.Complete());Check.Equal(ViveFakeWriteResult.Rejected,target.Append(data,Full(data)));
            });
            suite.Run("exclusive memory cross-thread write during callback faults both writes",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"inflight-thread");var data=new byte[]{1,2};
                ViveFakeWriteResult nested=ViveFakeWriteResult.Accepted;
                Action callback=()=>{var t=new Thread(()=>nested=target.Append(new byte[]{8},new ViveFakeWritePlan(1,false,null)));t.IsBackground=true;
                    t.Start();Check.True(t.Join(5000));};
                Check.Equal(ViveFakeWriteResult.Uncertain,target.Append(data,Full(data,callback)));
                Check.Equal(ViveFakeWriteResult.Uncertain,nested);Bytes(data,store.CopyForTest("inflight-thread"));
            });
            suite.Run("exclusive memory completion inside callback cannot falsely close",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"inflight-complete");var data=new byte[]{1,2};bool closed=true;
                Check.Equal(ViveFakeWriteResult.Uncertain,target.Append(data,Full(data,()=>closed=target.Complete())));
                Check.True(!closed);Check.Equal(ViveFakeTargetState.Uncertain,target.State);Bytes(data,store.CopyForTest("inflight-complete"));
            });
            suite.Run("exclusive memory non-owner write fails closed with no replacement",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"thread");var data=new byte[]{4};
                Check.Equal(ViveFakeWriteResult.Accepted,target.Append(data,Full(data)));ViveFakeWriteResult result=ViveFakeWriteResult.Accepted;
                var thread=new Thread(()=>result=target.Append(new byte[]{9},new ViveFakeWritePlan(1,false,null)));thread.IsBackground=true;
                thread.Start();Check.True(thread.Join(5000));Check.Equal(ViveFakeWriteResult.Rejected,result);
                Check.Equal(ViveFakeTargetState.Faulted,target.State);Bytes(data,store.CopyForTest("thread"));
                Check.Equal(ViveFakeWriteResult.Rejected,target.Append(data,Full(data)));
            });
            suite.Run("exclusive memory complete is explicit and prevents future writes",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"complete");var data=new byte[]{2,5};
                Check.Equal(ViveFakeWriteResult.Accepted,target.Append(data,Full(data)));Check.True(target.Complete());
                Check.Equal(ViveFakeTargetState.Closed,target.State);Check.True(!target.Complete());
                Check.Equal(ViveFakeWriteResult.Rejected,target.Append(new byte[]{8},new ViveFakeWritePlan(1,false,null)));
                Bytes(data,store.CopyForTest("complete"));
            });
            suite.Run("exclusive memory receipt identifies exact payload and returns defensive digest copies",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"receipt");var data=new byte[]{1,2,3};var original=(byte[])data.Clone();
                ViveExclusiveMemoryTarget.Receipt first;Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(data,Full(data,()=>data[0]=99),out first));
                Check.True(first!=null&&target.IsCurrentReceipt(first));Check.Equal(0L,first.Ordinal);Check.Equal(3L,first.TotalLength);
                byte[] digest;using(var sha=SHA256.Create())digest=sha.ComputeHash(original);Bytes(digest,first.CopyPayloadDigest());
                Check.True(first.MatchesPayloadDigest(digest));Check.True(!first.MatchesPayloadDigest(null));
                Check.True(!first.MatchesPayloadDigest(new byte[31])&&!first.MatchesPayloadDigest(new byte[33]));
                var changed=first.CopyPayloadDigest();changed[0]^=0xff;Check.True(!first.MatchesPayloadDigest(changed));
                Bytes(digest,first.CopyPayloadDigest());Check.True(first.MatchesPayloadDigest(digest));
                var secondBytes=new byte[]{8,9};ViveExclusiveMemoryTarget.Receipt second;
                Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(secondBytes,Full(secondBytes),out second));
                Check.True(!target.IsCurrentReceipt(first)&&target.IsCurrentReceipt(second));Check.Equal(1L,second.Ordinal);Check.Equal(5L,second.TotalLength);
                using(var sha=SHA256.Create())Bytes(sha.ComputeHash(secondBytes),second.CopyPayloadDigest());
                Bytes(new byte[]{1,2,3,8,9},store.CopyForTest("receipt"));
            });
            suite.Run("exclusive memory receipt rejects same-fields counterfeit and other target",()=>{
                var store=new ViveExclusiveMemoryStore(3);var first=Create(store,"issuer");var second=Create(store,"other");var data=new byte[]{2};
                ViveExclusiveMemoryTarget.Receipt receipt;Check.Equal(ViveFakeWriteResult.Accepted,first.AppendWithReceipt(data,Full(data),out receipt));
                var forged=new ViveExclusiveMemoryTarget.Receipt(receipt.Ordinal,receipt.TotalLength,receipt.CopyPayloadDigest());
                Check.True(!first.IsCurrentReceipt(forged)&&!second.IsCurrentReceipt(receipt)&&first.IsCurrentReceipt(receipt));
                Check.True(!first.IsCurrentReceipt(null));
            });
            suite.Run("exclusive memory every later ordinary append invalidates receipt",()=>{
                foreach(bool valid in new[]{false,true})
                {
                    var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"invalidate");var data=new byte[]{1,2};
                    ViveExclusiveMemoryTarget.Receipt prior;Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(data,Full(data),out prior));
                    if(valid)Check.Equal(ViveFakeWriteResult.Accepted,target.Append(new byte[]{7},new ViveFakeWritePlan(1,false,null)));
                    else Check.Equal(ViveFakeWriteResult.Rejected,target.Append(null,new ViveFakeWritePlan(0,false,null)));
                    Check.True(!target.IsCurrentReceipt(prior));
                }
            });
            suite.Run("exclusive memory withdrawal uncertain and complete each invalidate receipt",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"end");var data=new byte[]{1};
                    ViveExclusiveMemoryTarget.Receipt receipt;Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(data,Full(data),out receipt));
                    if(mode==0)target.Withdraw();else if(mode==1)target.MarkOutcomeUncertain();else Check.True(target.Complete());
                    Check.True(!target.IsCurrentReceipt(receipt));Bytes(data,store.CopyForTest("end"));
                }
            });
            suite.Run("exclusive memory failed receipt append cannot issue or preserve a receipt",()=>{
                for(int mode=0;mode<4;mode++)
                {
                    var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"failed");var seed=new byte[]{7};
                    ViveExclusiveMemoryTarget.Receipt prior;Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(seed,Full(seed),out prior));
                    var data=new byte[]{1,2,3};ViveFakeWritePlan plan;
                    if(mode==0)plan=new ViveFakeWritePlan(1,true,null);
                    else if(mode==1)plan=new ViveFakeWritePlan(data.Length,false,()=>{throw new InvalidOperationException();});
                    else if(mode==2)plan=new ViveFakeWritePlan(2,false,target.Withdraw);
                    else plan=new ViveFakeWritePlan(1,false,null);
                    ViveExclusiveMemoryTarget.Receipt failed;Check.Equal(ViveFakeWriteResult.Uncertain,target.AppendWithReceipt(data,plan,out failed));
                    Check.True(failed==null&&!target.IsCurrentReceipt(prior));Check.Equal(ViveFakeTargetState.Uncertain,target.State);
                    Check.True(store.CopyForTest("failed").Length>=1);
                }
            });
            suite.Run("exclusive memory wrong-thread receipt access and append invalidate old receipt",()=>{
                for(int mode=0;mode<2;mode++)
                {
                    var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"thread-receipt");var data=new byte[]{4};
                    ViveExclusiveMemoryTarget.Receipt prior;Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(data,Full(data),out prior));
                    bool current=true;ViveExclusiveMemoryTarget.Receipt foreign=null;
                    var thread=new Thread(()=>{if(mode==0)current=target.IsCurrentReceipt(prior);else target.AppendWithReceipt(data,Full(data),out foreign);});
                    thread.IsBackground=true;thread.Start();Check.True(thread.Join(5000));
                    Check.True(!current||mode==1);Check.True(foreign==null&&!target.IsCurrentReceipt(prior));
                    Check.Equal(ViveFakeTargetState.Faulted,target.State);Bytes(data,store.CopyForTest("thread-receipt"));
                }
            });
            suite.Run("exclusive memory conditional append replaces only the exact current receipt",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"conditional");var a=new byte[]{1,2};
                ViveExclusiveMemoryTarget.Receipt first;Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(a,Full(a),out first));
                var b=new byte[]{3,4,5};ViveExclusiveMemoryTarget.Receipt next;
                Check.Equal(ViveFakeWriteResult.Accepted,target.AppendIfCurrent(first,b,Full(b),out next));
                Check.True(next!=null&&!target.IsCurrentReceipt(first)&&target.IsCurrentReceipt(next));
                Check.Equal(first.Ordinal+1,next.Ordinal);Check.Equal(5L,next.TotalLength);
                using(var sha=SHA256.Create())Bytes(sha.ComputeHash(b),next.CopyPayloadDigest());
                Bytes(new byte[]{1,2,3,4,5},store.CopyForTest("conditional"));
            });
            suite.Run("exclusive memory conditional append rejects null forged foreign and stale receipts",()=>{
                for(int mode=0;mode<4;mode++)
                {
                    var store=new ViveExclusiveMemoryStore(3);var target=Create(store,"target");var data=new byte[]{1};
                    ViveExclusiveMemoryTarget.Receipt first;Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(data,Full(data),out first));
                    var current=first;ViveExclusiveMemoryTarget.Receipt expected=null;
                    if(mode==1)expected=new ViveExclusiveMemoryTarget.Receipt(first.Ordinal,first.TotalLength,first.CopyPayloadDigest());
                    else if(mode==2)
                    {
                        var other=Create(store,"other");ViveExclusiveMemoryTarget.Receipt foreign;
                        Check.Equal(ViveFakeWriteResult.Accepted,other.AppendWithReceipt(data,Full(data),out foreign));expected=foreign;
                    }
                    else if(mode==3)
                    {
                        Check.Equal(ViveFakeWriteResult.Accepted,target.AppendIfCurrent(first,new byte[]{2},new ViveFakeWritePlan(1,false,null),out current));
                        expected=first;
                    }
                    var preserved=store.CopyForTest("target");ViveExclusiveMemoryTarget.Receipt next;
                    Check.Equal(ViveFakeWriteResult.Rejected,target.AppendIfCurrent(expected,new byte[]{9},new ViveFakeWritePlan(1,false,null),out next));
                    Check.True(next==null&&!target.IsCurrentReceipt(current));Check.Equal(ViveFakeTargetState.Faulted,target.State);
                    Bytes(preserved,store.CopyForTest("target"));
                    Check.Equal(ViveFakeWriteResult.Rejected,target.AppendIfCurrent(current,data,Full(data),out next));Check.True(next==null);
                    Bytes(preserved,store.CopyForTest("target"));
                }
            });
            suite.Run("exclusive memory conditional append consumes current receipt on invalid input",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"invalid",4);var seed=new byte[]{1,2};
                    ViveExclusiveMemoryTarget.Receipt prior;Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(seed,Full(seed),out prior));
                    byte[] data=mode==0?null:mode==1?new byte[]{3}:new byte[]{3,4,5};
                    var plan=mode==1?new ViveFakeWritePlan(2,false,null):new ViveFakeWritePlan(data==null?0:data.Length,false,null);
                    ViveExclusiveMemoryTarget.Receipt next;Check.Equal(ViveFakeWriteResult.Rejected,target.AppendIfCurrent(prior,data,plan,out next));
                    Check.True(next==null&&!target.IsCurrentReceipt(prior));Check.Equal(ViveFakeTargetState.Faulted,target.State);
                    Bytes(seed,store.CopyForTest("invalid"));
                }
            });
            suite.Run("exclusive memory conditional append preserves partial failure and issues no receipt",()=>{
                for(int mode=0;mode<4;mode++)
                {
                    var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"uncertain");var seed=new byte[]{1};
                    ViveExclusiveMemoryTarget.Receipt prior;Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(seed,Full(seed),out prior));
                    var data=new byte[]{2,3,4};ViveFakeWritePlan plan=mode==0?new ViveFakeWritePlan(1,true,null):
                        mode==1?new ViveFakeWritePlan(2,false,()=>{throw new InvalidOperationException();}):
                        mode==2?new ViveFakeWritePlan(2,false,target.Withdraw):new ViveFakeWritePlan(1,false,null);
                    ViveExclusiveMemoryTarget.Receipt next;Check.Equal(ViveFakeWriteResult.Uncertain,target.AppendIfCurrent(prior,data,plan,out next));
                    Check.True(next==null&&!target.IsCurrentReceipt(prior));Check.Equal(ViveFakeTargetState.Uncertain,target.State);
                    var preserved=store.CopyForTest("uncertain");Check.True(preserved.Length==2||preserved.Length==3);
                    Check.Equal((byte)1,preserved[0]);Check.Equal((byte)2,preserved[1]);
                    Check.Equal(ViveFakeWriteResult.Rejected,target.AppendIfCurrent(prior,data,Full(data),out next));
                    Check.True(next==null);Bytes(preserved,store.CopyForTest("uncertain"));
                }
            });
            suite.Run("exclusive memory conditional append rejects foreign thread and in-flight reentry",()=>{
                for(int mode=0;mode<2;mode++)
                {
                    var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"thread");var seed=new byte[]{1};
                    ViveExclusiveMemoryTarget.Receipt prior;Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(seed,Full(seed),out prior));
                    ViveExclusiveMemoryTarget.Receipt next=null;ViveFakeWriteResult nested=ViveFakeWriteResult.Accepted;
                    if(mode==0)
                    {
                        var thread=new Thread(()=>nested=target.AppendIfCurrent(prior,new byte[]{9},new ViveFakeWritePlan(1,false,null),out next));
                        thread.IsBackground=true;thread.Start();Check.True(thread.Join(5000));
                        Check.Equal(ViveFakeWriteResult.Rejected,nested);Check.Equal(ViveFakeTargetState.Faulted,target.State);
                        Bytes(seed,store.CopyForTest("thread"));
                    }
                    else
                    {
                        var data=new byte[]{2,3};ViveExclusiveMemoryTarget.Receipt outer;
                        Check.Equal(ViveFakeWriteResult.Uncertain,target.AppendIfCurrent(prior,data,Full(data,
                            ()=>nested=target.AppendIfCurrent(prior,new byte[]{9},new ViveFakeWritePlan(1,false,null),out next)),out outer));
                        Check.Equal(ViveFakeWriteResult.Uncertain,nested);Check.True(outer==null);Check.Equal(ViveFakeTargetState.Uncertain,target.State);
                        Bytes(new byte[]{1,2,3},store.CopyForTest("thread"));
                    }
                    Check.True(next==null&&!target.IsCurrentReceipt(prior));
                }
            });
            suite.Run("exclusive memory preempted check cannot append through a stale receipt",()=>{
                var store=new ViveExclusiveMemoryStore(2);var target=Create(store,"preempt");var a=new byte[]{1};
                ViveExclusiveMemoryTarget.Receipt prior;Check.Equal(ViveFakeWriteResult.Accepted,target.AppendWithReceipt(a,Full(a),out prior));
                Check.True(target.IsCurrentReceipt(prior));
                ViveExclusiveMemoryTarget.Receipt replacement;Check.Equal(ViveFakeWriteResult.Accepted,
                    target.AppendWithReceipt(new byte[]{2},new ViveFakeWritePlan(1,false,null),out replacement));
                ViveExclusiveMemoryTarget.Receipt next;Check.Equal(ViveFakeWriteResult.Rejected,
                    target.AppendIfCurrent(prior,new byte[]{9},new ViveFakeWritePlan(1,false,null),out next));
                Check.True(next==null&&!target.IsCurrentReceipt(prior)&&!target.IsCurrentReceipt(replacement));
                Bytes(new byte[]{1,2},store.CopyForTest("preempt"));
            });
        }
    }
}
