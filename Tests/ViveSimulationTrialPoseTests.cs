using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveSimulationTrialPoseTests
    {
        sealed class Clock : IClock
        {
            readonly string id;double now;public Action OnNow,OnId;public bool Swallow;
            public double Now {get{Invoke(ref OnNow);return now;}set{now=value;}}
            public string ClockId {get{Invoke(ref OnId);return id;}}
            public Clock(string id){this.id=id;}
            void Invoke(ref Action action)
            {var callback=action;action=null;if(callback==null)return;if(!Swallow){callback();return;}try{callback();}catch(InvalidOperationException){}}
        }
        sealed class Fixture
        {
            public readonly Clock Participant=new Clock("participant-clock"),Operator=new Clock("operator-clock");
            public readonly ViveSimulationTrialPoseSender Sender;
            public readonly ViveSimulationTrialPoseReceiver Receiver;
            public TaskSnapshot Task;
            public Fixture()
            {
                var map=new ClockSynchronizer(Participant.ClockId,Operator.ClockId,new DevelopmentClockConfiguration(3,5,1,100,50));
                var peer=new PeerIdentity("sim-session","operator",PeerRole.Operator,"sim-source",ControlSource.LiveOperator,Operator.ClockId);
                for(int i=0;i<3;i++)
                {
                    double t=i*10;string id="sync-"+i;Check.True(map.RegisterRequest(id,t));
                    Check.True(map.ObserveReply(WirePacket.TimeReply(peer,i,t+.012,id,Participant.ClockId,t,t+.01),t+.022),map.LastRejection);
                }
                Participant.Now=21;Operator.Now=21;
                Sender=new ViveSimulationTrialPoseSender(Operator,"sim-session","epoch","mapping",Participant.ClockId,.4,true);
                Receiver=new ViveSimulationTrialPoseReceiver(Participant,map,"sim-session","epoch","mapping",Operator.ClockId,.4,4,true);
                Task=Main("trial-1",Condition.A);
            }
            public static TaskSnapshot Main(string trial,Condition condition)
            {return new TaskSnapshot("sim-session",0,0,trial,Colour.Red,Colour.Blue,Phase.MainTask,condition);}
            public ViveSimulationTrialTask Sent(TaskSnapshot task,long revision,long sequence)
            {var t=new ViveSimulationTrialTask(task,"epoch","mapping",Participant.ClockId,revision,sequence,Participant.Now);Receiver.RecordTaskSocketAccepted(t,true);return t;}
            public byte[] Pose(ViveSimulationTrialTask task)
            {
                Operator.Now+=.05;Check.True(Sender.ObserveTask(ViveSimulationTrialPoseCodec.EncodeTask(task),true,Operator.Now),Sender.LastIssue);
                double observation=Operator.Now+.02;Operator.Now+=.04;byte[] pose;
                Check.True(Sender.TryCreatePose(observation,observation,Rotation.Identity,new Vec3(0,0,1),out pose),Sender.LastIssue);return pose;
            }
            public bool Receive(byte[] pose,long revision)
            {Participant.Now=Operator.Now+.05;return Receiver.Observe(pose,true,Participant.Now,Task,revision);}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE simulation trial pose follows sent task and retains original receipt",()=>{
                var f=new Fixture();var sent=f.Sent(f.Task,7,1);var bytes=f.Pose(sent);Check.True(f.Receive(bytes,7),f.Receiver.LastIssue);
                var pose=f.Receiver.Current(f.Task,7);Check.True(pose!=null);Check.Equal("trial-1",pose.Task.Task.TrialId);
                Check.Equal(1L,pose.Task.PacketSequence);Check.Near(21.05,pose.TaskReceivedOperatorSeconds);
                Check.True(!pose.HardwareCaptureTimeVerified&&!pose.CryptographicallyAuthenticated&&!f.Receiver.CanEnterCountdown&&!f.Receiver.CanRecordOrExport);
                ViveSimulationTrialPoseFrame again;double receipt;Check.True(f.Receiver.TryGetCurrent(f.Task,7,out again,out receipt));double original=receipt;
                f.Participant.Now+=.1;Check.True(f.Receiver.TryGetCurrent(f.Task,7,out again,out receipt));Check.Near(original,receipt);
                f.Participant.Now+=.4;Check.True(f.Receiver.Current(f.Task,7)==null);
            });
            suite.Run("VIVE simulation trial pose rejects unsent task, changed trial, and delayed earlier trial",()=>{
                var f=new Fixture();var sent=f.Sent(f.Task,7,1);var bytes=f.Pose(sent);
                f.Task=Fixture.Main("trial-2",Condition.A);f.Participant.Now=f.Operator.Now+.05;
                Check.True(!f.Receiver.Observe(bytes,true,f.Participant.Now,f.Task,8));
                var second=f.Sent(f.Task,8,2);f.Operator.Now=f.Participant.Now+.02;var bytes2=f.Pose(second);Check.True(f.Receive(bytes2,8),f.Receiver.LastIssue);
                f.Participant.Now+=.01;Check.True(!f.Receiver.Observe(bytes,true,f.Participant.Now,f.Task,8));Check.True(f.Receiver.Current(f.Task,8)==null);
                var other=new Fixture();var unregistered=new ViveSimulationTrialTask(other.Task,"epoch","mapping",other.Participant.ClockId,7,3,other.Participant.Now);
                var forged=other.Pose(unregistered);Check.True(!other.Receive(forged,7));
            });
            suite.Run("VIVE simulation trial pose rejects duplicate, endpoint, D and pause",()=>{
                var f=new Fixture();var sent=f.Sent(f.Task,7,1);var bytes=f.Pose(sent);Check.True(f.Receive(bytes,7));
                f.Participant.Now+=.01;Check.True(!f.Receiver.Observe(bytes,true,f.Participant.Now,f.Task,7));
                var paused=new TaskSnapshot("sim-session",0,0,null,null,null,Phase.Paused,Condition.A);
                Check.True(f.Receiver.Current(paused,8)==null);
                f.Operator.Now+=.02;var d=Fixture.Main("trial-2",Condition.D);
                var dTask=new ViveSimulationTrialTask(d,"epoch","mapping",f.Participant.ClockId,8,2,f.Participant.Now);
                Check.True(!f.Sender.ObserveTask(ViveSimulationTrialPoseCodec.EncodeTask(dTask),true,f.Operator.Now));byte[] output;
                f.Operator.Now+=.02;Check.True(!f.Sender.TryCreatePose(f.Operator.Now,f.Operator.Now,Rotation.Identity,new Vec3(0,0,1),out output));
                var other=new Fixture();var s=other.Sent(other.Task,7,1);var p=other.Pose(s);other.Participant.Now=other.Operator.Now+.05;
                Check.True(!other.Receiver.Observe(p,false,other.Participant.Now,other.Task,7));
            });
            suite.Run("VIVE simulation trial pose rejects corrupted codec, stale observations and clock rollback",()=>{
                var f=new Fixture();var sent=f.Sent(f.Task,7,1);var bytes=f.Pose(sent);
                ViveSimulationTrialPoseFrame decoded;Check.True(ViveSimulationTrialPoseCodec.TryDecodePose(bytes,out decoded));
                var broken=(byte[])bytes.Clone();broken[0]=(byte)'X';Check.True(!ViveSimulationTrialPoseCodec.TryDecodePose(broken,out decoded));
                f.Participant.Now=f.Operator.Now+.6;Check.True(!f.Receiver.Observe(bytes,true,f.Participant.Now-.6,f.Task,7));
                f.Participant.Now-=1;Check.Throws<InvalidOperationException>(()=>f.Receiver.Current(f.Task,7));Check.True(f.Receiver.Faulted);
            });
            suite.Run("VIVE simulation task replay cannot refresh sender, and invalid observations stay hidden",()=>{
                var f=new Fixture();var sent=f.Sent(f.Task,7,1);f.Operator.Now+=.05;
                var taskBytes=ViveSimulationTrialPoseCodec.EncodeTask(sent);Check.True(f.Sender.ObserveTask(taskBytes,true,f.Operator.Now));
                f.Operator.Now+=.35;Check.True(!f.Sender.ObserveTask(taskBytes,true,f.Operator.Now));
                f.Operator.Now+=.06;byte[] output;
                Check.True(!f.Sender.TryCreatePose(f.Operator.Now,f.Operator.Now,Rotation.Identity,new Vec3(0,0,1),out output));
                var other=new Fixture();var s=other.Sent(other.Task,7,1);other.Operator.Now+=.05;Check.True(other.Sender.ObserveTask(ViveSimulationTrialPoseCodec.EncodeTask(s),true,other.Operator.Now));
                other.Operator.Now+=.02;Check.True(!other.Sender.TryCreatePose(other.Operator.Now,other.Operator.Now,Rotation.Identity,new Vec3(0,0,0),out output));
                Check.True(!other.Sender.TryCreatePose(other.Operator.Now+1,other.Operator.Now+1,Rotation.Identity,new Vec3(0,0,1),out output));
                var queued=new Fixture();var q=queued.Sent(queued.Task,7,1);queued.Operator.Now+=.7;
                Check.True(!queued.Sender.ObserveTask(ViveSimulationTrialPoseCodec.EncodeTask(q),true,queued.Operator.Now-.5));
                Check.Equal("stale_task_receipt",queued.Sender.LastIssue);
            });
            suite.Run("VIVE simulation authority revision cannot name two task states",()=>{
                var f=new Fixture();f.Sent(f.Task,7,1);var changed=Fixture.Main("trial-2",Condition.A);
                var inconsistent=new ViveSimulationTrialTask(changed,"epoch","mapping",f.Participant.ClockId,7,2,f.Participant.Now);
                Check.Throws<InvalidOperationException>(()=>f.Receiver.RecordTaskSocketAccepted(inconsistent,true));Check.True(f.Receiver.Faulted);
            });
            suite.Run("VIVE simulation repeated head or eye observation cannot become a new pose",()=>{
                var f=new Fixture();var sent=f.Sent(f.Task,7,1);var bytes=f.Pose(sent);Check.True(f.Receive(bytes,7));
                ViveSimulationTrialPoseFrame first;Check.True(ViveSimulationTrialPoseCodec.TryDecodePose(bytes,out first));
                f.Operator.Now+=.02;byte[] replay;
                Check.True(!f.Sender.TryCreatePose(first.HeadObservedOperatorSeconds,first.EyeObservedOperatorSeconds,Rotation.Identity,new Vec3(0,0,1),out replay));
                Check.True(!f.Sender.TryCreatePose(f.Operator.Now,first.EyeObservedOperatorSeconds,Rotation.Identity,new Vec3(0,0,1),out replay));
                var forged=new ViveSimulationTrialPoseFrame(first.Task,first.OperatorClock,first.Sequence+1,first.TaskReceivedOperatorSeconds,
                    first.HeadObservedOperatorSeconds,first.EyeObservedOperatorSeconds,first.SentOperatorSeconds,first.Head,first.Eye);
                f.Participant.Now+=.01;Check.True(!f.Receiver.Observe(ViveSimulationTrialPoseCodec.EncodePose(forged),true,f.Participant.Now,f.Task,7));
                Check.True(f.Receiver.Current(f.Task,7)==null);
            });
            suite.Run("VIVE simulation cross-thread access latches and clears pose",()=>{
                var f=new Fixture();var sent=f.Sent(f.Task,7,1);var bytes=f.Pose(sent);Check.True(f.Receive(bytes,7));
                Exception receiverError=null;var receiverThread=new Thread(()=>{try{f.Receiver.Current(f.Task,7);}catch(Exception e){receiverError=e;}});
                receiverThread.Start();Check.True(receiverThread.Join(3000));Check.True(receiverError is InvalidOperationException);Check.True(f.Receiver.Faulted);
                Check.Throws<InvalidOperationException>(()=>f.Receiver.Current(f.Task,7));
                Exception senderError=null;var senderThread=new Thread(()=>{try{byte[] b;f.Sender.TryCreatePose(99,99,Rotation.Identity,new Vec3(0,0,1),out b);}catch(Exception e){senderError=e;}});
                senderThread.Start();Check.True(senderThread.Join(3000));Check.True(senderError is InvalidOperationException);Check.True(f.Sender.Faulted);
            });
            suite.Run("VIVE simulation failed task evidence registration latches and clears prior pose",()=>{
                for(int scenario=0;scenario<3;scenario++)
                {
                    var f=new Fixture();var sent=f.Sent(f.Task,7,1);var bytes=f.Pose(sent);Check.True(f.Receive(bytes,7));
                    var next=new ViveSimulationTrialTask(f.Task,"epoch","mapping",f.Participant.ClockId,7,2,f.Participant.Now);
                    if(scenario==0)Check.Throws<InvalidOperationException>(()=>f.Receiver.RecordTaskSocketAccepted(next,false));
                    else if(scenario==1)Check.Throws<InvalidOperationException>(()=>f.Receiver.RecordTaskSocketAccepted(null,true));
                    else Check.Throws<InvalidOperationException>(()=>f.Receiver.RecordTaskSocketAccepted(sent,true));
                    Check.True(f.Receiver.Faulted);Check.Throws<InvalidOperationException>(()=>f.Receiver.Current(f.Task,7));
                }
            });
            suite.Run("VIVE simulation swallowed clock reentrancy cannot restore cleared pose",()=>{
                for(int scenario=0;scenario<2;scenario++)
                {
                    var f=new Fixture();var sent=f.Sent(f.Task,7,1);var bytes=f.Pose(sent);Check.True(f.Receive(bytes,7));
                    double receipt=f.Participant.Now;f.Participant.Swallow=true;
                    if(scenario==0)f.Participant.OnNow=()=>f.Receiver.Clear();else f.Participant.OnId=()=>f.Receiver.Observe(bytes,true,f.Participant.Now,f.Task,7);
                    Check.Throws<InvalidOperationException>(()=>f.Receiver.Observe(bytes,true,receipt,f.Task,7));
                    Check.True(f.Receiver.Faulted);Check.Throws<InvalidOperationException>(()=>f.Receiver.Current(f.Task,7));
                }
                var sender=new Fixture();var task=sender.Sent(sender.Task,7,1);var encoded=ViveSimulationTrialPoseCodec.EncodeTask(task);
                sender.Operator.Now+=.05;Check.True(sender.Sender.ObserveTask(encoded,true,sender.Operator.Now));
                double senderReceipt=sender.Operator.Now;sender.Operator.Now+=.05;sender.Operator.Swallow=true;
                sender.Operator.OnNow=()=>sender.Sender.ObserveTask(encoded,true,senderReceipt);
                Check.Throws<InvalidOperationException>(()=>{byte[] ignored;sender.Sender.TryCreatePose(senderReceipt+.01,senderReceipt+.01,Rotation.Identity,new Vec3(0,0,1),out ignored);});
                Check.True(sender.Sender.Faulted);
            });
            suite.Run("VIVE simulation concurrent foreign access is serialized and terminal",()=>{
                var f=new Fixture();var sent=f.Sent(f.Task,7,1);var bytes=f.Pose(sent);Check.True(f.Receive(bytes,7));
                Exception foreign=null;Thread thread=null;using(var started=new ManualResetEvent(false))
                {
                    f.Participant.OnNow=()=>{thread=new Thread(()=>{started.Set();try{f.Receiver.Clear();}catch(Exception e){foreign=e;}});thread.Start();Check.True(started.WaitOne(3000));};
                    f.Receiver.Current(f.Task,7);Check.True(thread.Join(3000));
                }
                Check.True(foreign is InvalidOperationException);Check.True(f.Receiver.Faulted);
                Check.Throws<InvalidOperationException>(()=>f.Receiver.Current(f.Task,7));
            });
        }
    }
}
