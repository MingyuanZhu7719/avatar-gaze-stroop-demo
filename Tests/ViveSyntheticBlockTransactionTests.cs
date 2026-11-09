using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveSyntheticBlockTransactionTests
    {
        static ViveSyntheticBlockTransaction.Item[] Items(int count)
        {var result=new ViveSyntheticBlockTransaction.Item[count];for(int i=0;i<count;i++)result[i]=new ViveSyntheticBlockTransaction.Item(Colour.Red,Colour.Blue);return result;}
        static ViveSyntheticBlockTransaction CreateMain(int block=0,int capacity=4)
        {return new ViveSyntheticBlockTransaction("synthetic",block,0,Items(24),capacity,30);}
        static void Start(ViveSyntheticBlockTransaction model)
        {Check.Equal(ViveSyntheticBlockTransaction.StartResult.Started,model.TryStart(model.AckForTest,1,true,false));}
        static void Drain(ViveSyntheticBlockTransaction model)
        {while(model.PendingCount>0)Check.True(model.AcknowledgeNextForTest(model.ConfirmedThrough+1));}
        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic block start waits for release and consumes only its own plan",()=>{
                var a=CreateMain();var b=CreateMain(1);
                Check.Equal(ViveSyntheticBlockTransaction.StartResult.Waiting,a.TryStart(a.AckForTest,1,true,true));
                Check.Equal(0,a.AckForTest.Consumed);Start(a);Check.Equal(1,a.AckForTest.Consumed);
                Check.Equal(ViveSyntheticBlockTransaction.StartResult.Stopped,b.TryStart(a.AckForTest,1,true,false));
                Check.True(b.IsStopped&&!a.ProductAuthority&&!a.Durable);Check.Equal("UNKNOWN",a.ActualValidity);
            });
            suite.Run("synthetic block deadline and device loss reject late plan",()=>{
                var timeout=CreateMain();Check.Equal(ViveSyntheticBlockTransaction.StartResult.Waiting,timeout.TryStart(timeout.AckForTest,29,true,true));
                Check.Equal(ViveSyntheticBlockTransaction.StartResult.Stopped,timeout.TryStart(timeout.AckForTest,30,true,false));
                Check.Equal(ViveSyntheticBlockTransaction.StartResult.Stopped,timeout.TryStart(timeout.AckForTest,1,true,false));
                var loss=CreateMain();Check.Equal(ViveSyntheticBlockTransaction.StartResult.Stopped,loss.TryStart(loss.AckForTest,1,false,false));
                var clock=CreateMain();Check.Equal(ViveSyntheticBlockTransaction.StartResult.Stopped,clock.TryStart(clock.AckForTest,Double.NaN,true,false));
                var backwards=CreateMain();Check.Equal(ViveSyntheticBlockTransaction.StartResult.Waiting,backwards.TryStart(backwards.AckForTest,2,true,true));
                Check.Equal(ViveSyntheticBlockTransaction.StartResult.Stopped,backwards.TryStart(backwards.AckForTest,1,true,false));
                var replay=CreateMain();Start(replay);Check.Equal(ViveSyntheticBlockTransaction.StartResult.Stopped,replay.TryStart(replay.AckForTest,2,true,false));
            });
            suite.Run("synthetic four blocks queue asynchronously with unknown actual outcomes",()=>{
                for(int block=0;block<4;block++){
                    var model=CreateMain(block);Start(model);
                    for(int i=0;i<24;i++){
                        Check.True(model.RenderProxy(i,0,Colour.Red,Colour.Blue));Check.True(model.Respond(i,0));
                        Check.Equal(2,model.PendingCount);Check.True(!model.Durable&&model.ActualVisibility=="UNKNOWN");Drain(model);
                    }
                    Check.True(model.AllTrialEventsEnqueued);Check.Equal(47L,model.ConfirmedThrough);
                    Check.Equal("UNKNOWN",model.ActualValidity);Check.Equal("UNKNOWN",model.EyeCoverage);
                }
            });
            suite.Run("synthetic practice retry uses separate attempt and plan copy",()=>{
                var items=Items(8);var first=new ViveSyntheticBlockTransaction("synthetic",-1,1,items,4,30);
                var retry=new ViveSyntheticBlockTransaction("synthetic",-1,2,Items(8),4,30);items[0]=new ViveSyntheticBlockTransaction.Item(Colour.Green,Colour.Yellow);
                Check.True(first.Scope!=retry.Scope);Start(first);Check.True(first.RenderProxy(0,0,Colour.Red,Colour.Blue));
                Check.Equal(ViveSyntheticBlockTransaction.StartResult.Stopped,retry.TryStart(first.AckForTest,1,true,false));
            });
            suite.Run("synthetic interrupted trial advances segment without inventing response",()=>{
                var model=CreateMain();Start(model);Check.True(model.RenderProxy(0,0,Colour.Red,Colour.Blue));
                Check.True(model.Interrupt(0,0));Check.Equal(1,model.Index);Check.Equal(1,model.Segment);Drain(model);
                Check.True(!model.TryResumeForTest(true,false));Check.True(model.TryResumeForTest(true,true));
                Check.True(model.RenderProxy(1,1,Colour.Red,Colour.Blue));Check.True(model.Respond(1,1));
                Check.Equal("UNKNOWN",model.ActualValidity);
            });
            suite.Run("synthetic queue saturation and out of order acknowledgement stop",()=>{
                var full=CreateMain(0,1);Start(full);Check.True(full.RenderProxy(0,0,Colour.Red,Colour.Blue));
                Check.True(!full.Respond(0,0)&&full.IsStopped);Check.Equal(-1L,full.ConfirmedThrough);
                var order=CreateMain();Start(order);Check.True(order.RenderProxy(0,0,Colour.Red,Colour.Blue));
                Check.True(!order.AcknowledgeNextForTest(1)&&order.IsStopped);Check.Equal("UNKNOWN",order.ActualValidity);
            });
            suite.Run("synthetic revoke during enqueue and device loss fail closed",()=>{
                var revoked=CreateMain();Start(revoked);revoked.AfterEnqueueForTest=revoked.Withdraw;
                Check.True(!revoked.RenderProxy(0,0,Colour.Red,Colour.Blue)&&revoked.IsStopped);
                Check.True(!revoked.AcknowledgeNextForTest(0));Check.Equal("UNKNOWN",revoked.ActualValidity);
                var lost=CreateMain();Start(lost);lost.DeviceLost();Check.True(!lost.RenderProxy(0,0,Colour.Red,Colour.Blue));
                var fault=CreateMain();Start(fault);fault.AfterEnqueueForTest=()=>{throw new InvalidOperationException();};
                Check.True(!fault.RenderProxy(0,0,Colour.Red,Colour.Blue)&&fault.IsStopped);Check.Equal(-1L,fault.ConfirmedThrough);
            });
            suite.Run("synthetic stimulus mismatch and response without render stop",()=>{
                var mismatch=CreateMain();Start(mismatch);Check.True(!mismatch.RenderProxy(0,0,Colour.Green,Colour.Blue)&&mismatch.IsStopped);
                var response=CreateMain();Start(response);Check.True(!response.Respond(0,0)&&response.IsStopped);
                var index=CreateMain();Start(index);Check.True(!index.RenderProxy(1,0,Colour.Red,Colour.Blue)&&index.IsStopped);
                var segment=CreateMain();Start(segment);Check.True(!segment.RenderProxy(0,1,Colour.Red,Colour.Blue)&&segment.IsStopped);
            });
        }
    }
}
