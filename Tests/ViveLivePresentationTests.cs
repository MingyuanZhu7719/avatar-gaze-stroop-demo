using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic only: no Unity renderer, hardware, transport, or study admission.
    public static class ViveLivePresentationTests
    {
        sealed class Clock:IClock
        {
            public double Time=10;
            public bool Fail;
            public string ClockId {get{return "live-test-clock";}}
            public double Now {get{if(Fail)throw new InvalidOperationException("synthetic clock failure");return Time;}}
        }
        sealed class Sink:IViveLivePresentationSink
        {
            public int Applied,Cleared;
            public AvatarOutput Current;
            public bool FailApply,FailClear;
            public Action OnApply,OnClear;
            public void Apply(AvatarOutput output)
            {
                if(FailApply)throw new InvalidOperationException("synthetic sink failure");
                var callback=OnApply;OnApply=null;if(callback!=null)callback();
                Applied++;Current=output;
            }
            public void Clear(){Cleared++;if(FailClear)throw new InvalidOperationException("synthetic clear failure");var callback=OnClear;OnClear=null;if(callback!=null)callback();Current=null;}
        }
        sealed class Fixture
        {
            public readonly Clock Clock=new Clock();
            public readonly Sink Sink=new Sink();
            public readonly ViveLivePresentationAdapter Adapter;
            public Fixture(){Adapter=new ViveLivePresentationAdapter(Clock,"session","mapping",.25,Sink,true);}
            public bool Present(TaskSnapshot task,ViveLivePresentationFrame frame,ControlSelection? selection=ControlSelection.LiveWoz)
            {return Adapter.Present(task,selection,frame);}
        }
        static TaskSnapshot Task(int block=0,int segment=0,string trial="trial1",Condition condition=Condition.A,Phase phase=Phase.MainTask,string session="session")
        {return new TaskSnapshot(session,block,segment,trial,Colour.Red,Colour.Blue,phase,condition);}
        static ViveLivePresentationFrame Frame(long sequence=1,int block=0,int segment=0,string trial="trial1",Condition condition=Condition.A,
            double received=9.9,string session="session",string mapping="mapping",bool headValid=true,bool eyeValid=true,
            Rotation? head=null,Vec3? eye=null)
        {return new ViveLivePresentationFrame(session,block,segment,trial,condition,sequence,received,mapping,
            head??Rotation.Identity,eye??new Vec3(0,0,1),headValid,eyeValid);}
        static void CheckNoPose(Fixture f)
        {Check.True(f.Sink.Current==null,"Invalid or unavailable live data must clear the live presentation");}
        public static void Register(TestSuite suite)
        {
            suite.Run("live presentation valid A B C frames render mapped pose with gates closed",()=>{
                foreach(var condition in new[]{Condition.A,Condition.B,Condition.C})
                {
                    var f=new Fixture();var task=Task(condition:condition);var frame=Frame(condition:condition);
                    Check.True(f.Present(task,frame));Check.True(f.Sink.Current!=null&&f.Sink.Current.ActualPose.HasValue);
                    Check.True(f.Sink.Current.AvatarVisible&&f.Sink.Current.HeadValid&&f.Sink.Current.EyeValid);
                    Check.Equal(ControlSource.LiveOperator,f.Sink.Current.ActualSource.Value);
                    Check.Near(1,f.Sink.Current.ActualPose.Value.HeadRotation.W);
                    Check.Near(1,f.Sink.Current.ActualPose.Value.EyeDirection.Z);
                    Check.True(!f.Adapter.CanEnterLiveCountdown&&!f.Adapter.CanRecordOrExport);
                }
            });
            suite.Run("live presentation disallowed phase source and D clear without fallback",()=>{
                foreach(int variant in new[]{0,1,2,3,4})
                {
                    var f=new Fixture();Check.True(f.Present(Task(),Frame()));
                    TaskSnapshot task=variant==0?Task(condition:Condition.D):variant==1?Task(phase:Phase.Rest):Task();
                    ControlSelection? source=variant==2?ControlSelection.Automatic:variant==3?(ControlSelection?)null:ControlSelection.LiveWoz;
                    var frame=variant==0?Frame(2,condition:Condition.D):variant==4?null:Frame(2);
                    Check.True(!f.Present(task,frame,source));CheckNoPose(f);Check.Equal(1,f.Sink.Applied);
                }
            });
            suite.Run("live presentation rejects mismatched session block segment trial condition mapping",()=>{
                for(int variant=0;variant<6;variant++)
                {
                    var f=new Fixture();Check.True(f.Present(Task(),Frame()));
                    var frame=Frame(2,block:variant==1?1:0,segment:variant==2?1:0,trial:variant==3?"other-trial":"trial1",
                        condition:variant==4?Condition.B:Condition.A,session:variant==0?"other-session":"session",mapping:variant==5?"other-mapping":"mapping");
                    Check.True(!f.Present(Task(),frame));CheckNoPose(f);Check.Equal(1,f.Sink.Applied);
                }
            });
            suite.Run("live presentation rejects partial invalid and nonunit channels",()=>{
                for(int variant=0;variant<4;variant++)
                {
                    var f=new Fixture();Check.True(f.Present(Task(),Frame()));
                    var frame=Frame(2,headValid:variant!=0,eyeValid:variant!=1,
                        head:variant==2?new Rotation(0,0,0,0):Rotation.Identity,
                        eye:variant==3?new Vec3(0,0,0):new Vec3(0,0,1));
                    Check.True(!f.Present(Task(),frame));CheckNoPose(f);Check.Equal(1,f.Sink.Applied);
                }
            });
            suite.Run("live presentation rejects old future and repeated pose",()=>{
                for(int variant=0;variant<4;variant++)
                {
                    var f=new Fixture();Check.True(f.Present(Task(),Frame()));
                    var frame=Frame(variant==0?1:variant==1?0:2,received:variant==2?9:variant==3?10.1:9.9);
                    Check.True(!f.Present(Task(),frame));CheckNoPose(f);Check.Equal(1,f.Sink.Applied);
                }
            });
            suite.Run("live presentation invalid sequence is consumed and cannot be repaired by replay",()=>{
                var f=new Fixture();Check.True(f.Present(Task(),Frame()));
                Check.True(!f.Present(Task(),Frame(2,eyeValid:false)));CheckNoPose(f);
                Check.True(!f.Present(Task(),Frame(2)));CheckNoPose(f);
                Check.True(f.Present(Task(),Frame(3)));Check.Equal(2,f.Sink.Applied);
            });
            suite.Run("live presentation clears across trial segment and block transitions before fresh frame",()=>{
                for(int variant=0;variant<3;variant++)
                {
                    var f=new Fixture();Check.True(f.Present(Task(),Frame()));
                    int block=variant==0?1:0,segment=variant==1?1:0;string trial=variant==2?"trial2":"trial1";
                    var changed=Task(block,segment,trial);Check.True(!f.Present(changed,Frame(2,block,segment,trial)));
                    CheckNoPose(f);Check.Equal(1,f.Sink.Applied);
                    Check.True(f.Present(changed,Frame(3,block,segment,trial)));Check.Equal(2,f.Sink.Applied);
                }
            });
            suite.Run("live presentation requires new frame after source phase or D even if transition frame missing",()=>{
                for(int variant=0;variant<3;variant++)
                {
                    var f=new Fixture();Check.True(f.Present(Task(),Frame()));
                    var during=variant==1?Task(phase:Phase.Rest):variant==2?Task(condition:Condition.D):Task();
                    var source=variant==0?ControlSelection.Automatic:ControlSelection.LiveWoz;
                    Check.True(!f.Present(during,null,source));CheckNoPose(f);
                    Check.True(!f.Present(Task(),Frame(2)));CheckNoPose(f);
                    Check.True(f.Present(Task(),Frame(3)));Check.Equal(2,f.Sink.Applied);
                }
            });
            suite.Run("live presentation clear and sink failure never hold old pose",()=>{
                var f=new Fixture();Check.True(f.Present(Task(),Frame()));f.Adapter.Clear();CheckNoPose(f);
                Check.True(!f.Present(Task(),Frame(2)));CheckNoPose(f);
                Check.True(f.Present(Task(),Frame(3)));f.Sink.FailApply=true;
                try{Check.True(!f.Present(Task(),Frame(4)));}catch(InvalidOperationException){}
                CheckNoPose(f);Check.Equal(2,f.Sink.Applied);
            });
            suite.Run("live presentation clear failure latches fault and prevents later apply",()=>{
                var f=new Fixture();Check.True(f.Present(Task(),Frame()));f.Sink.FailClear=true;
                try{f.Present(Task(),Frame(2,eyeValid:false));}catch(InvalidOperationException){}
                // A failed sink cleanup cannot prove that an old physical image disappeared.
                f.Sink.FailClear=false;
                try{Check.True(!f.Present(Task(),Frame(3)));}catch(InvalidOperationException){}
                Check.Equal(1,f.Sink.Applied);
            });
            suite.Run("live presentation sink reentry faults and clears without reapplying",()=>{
                foreach(bool reenterApply in new[]{false,true})
                {
                    var f=new Fixture();Check.True(f.Present(Task(),Frame()));
                    Action reenter=()=>{Check.True(!f.Present(Task(),Frame(3)));};
                    if(reenterApply)f.Sink.OnApply=reenter;else f.Sink.OnClear=reenter;
                    Check.True(!f.Present(Task(),Frame(2)));Check.True(f.Adapter.Faulted);CheckNoPose(f);
                    int count=f.Sink.Applied;Check.True(!f.Present(Task(),Frame(4)));Check.Equal(count,f.Sink.Applied);
                }
            });
            suite.Run("live presentation foreign thread faults and owner later clears",()=>{
                var f=new Fixture();Check.True(f.Present(Task(),Frame()));bool result=true;
                var thread=new Thread(()=>{result=f.Present(Task(),Frame(2));});thread.Start();thread.Join();
                Check.True(!result&&f.Adapter.Faulted);
                // The foreign call does not own the sink and cannot promise immediate hide.
                Check.True(!f.Present(Task(),Frame(3)));CheckNoPose(f);Check.Equal(1,f.Sink.Applied);
            });
            suite.Run("live presentation clock rollback or exception fails closed",()=>{
                foreach(bool throws in new[]{false,true})
                {
                    var f=new Fixture();Check.True(f.Present(Task(),Frame()));
                    if(throws)f.Clock.Fail=true;else f.Clock.Time=9.8;
                    Check.True(!f.Present(Task(),Frame(2,received:9.7)));CheckNoPose(f);Check.True(f.Adapter.Faulted);
                    f.Clock.Fail=false;f.Clock.Time=10.1;Check.True(!f.Present(Task(),Frame(3)));Check.Equal(1,f.Sink.Applied);
                }
            });
        }
    }
}
