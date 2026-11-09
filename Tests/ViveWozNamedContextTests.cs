using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveWozNamedContextTests
    {
        static AutomaticGazeGeometry Geometry()
        {return new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"named-host-geometry");}
        static Dictionary<ViveTrackpadDirection,Colour> Mapping()
        {return new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};}
        static ViveNamedSimulationPlan Plan()
        {return ViveNamedSimulationPlan.Select(Actor.Owner,"named-host-plan",ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"named-host-geometry"));}
        static ViveDevelopmentParticipant Participant(ManualClock clock,bool withIntent)
        {
            var plan=Plan();var config=withIntent?SessionConfiguration.NamedRecordingDevelopment("named-host-session",37,plan,ViveNamedRecordingIntent.Disabled(Actor.Owner,"no-recording")):
                SessionConfiguration.NamedPlanDevelopment("named-host-session",37,plan);
            return new ViveDevelopmentParticipant(config,clock,Geometry(),Mapping(),.5,true);
        }
        static void Input(ViveDevelopmentParticipant p,bool pressed=false,Colour colour=Colour.Red)
        {double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0;double y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
            p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,pressed,x,y));}
        public static void Register(TestSuite suite)
        {
            suite.Run("named host reads only bound simulation declarations",()=>{
                var clock=new ManualClock();var task=Participant(clock,true);var host=new ViveWozParticipantHost(task,clock,true);
                ViveWozParticipantHost.NamedContext context;Check.True(host.TryReadNamedContext(out context));
                Check.Equal(task.SessionId,context.Session);Check.Equal(task.Phase,context.Phase);Check.Equal(task.BlockIndex,context.Block);
                Check.Equal("named-host-plan",context.PlanId);Check.True(host.IsNamedContextCurrent(context));
                var forged=new ViveWozParticipantHost.NamedContext(task,context.Stamp,"other-session",context.Phase,context.Block,context.Segment,
                    context.PlanId,context.PlanBlock,context.DeclaredOption,context.ListIndex,context.PlanConfirmed);
                Check.True(!host.IsNamedContextCurrent(forged));
                var forgedOption=new ViveWozParticipantHost.NamedContext(task,context.Stamp,context.Session,context.Phase,context.Block,context.Segment,
                    context.PlanId,context.PlanBlock,ViveNamedOption.LiveWoz,context.ListIndex,context.PlanConfirmed);
                Check.True(!host.IsNamedContextCurrent(forgedOption));
                Check.True(!host.LiveSourceSelected&&!host.CanEnterLiveCountdown&&!host.CanRecordOrExport);
                Check.Throws<InvalidOperationException>(()=>host.ReadAuthoritativeTask());host.ClearLivePose();
                Check.Throws<InvalidOperationException>(()=>host.PauseForTransportLoss());Check.Equal(Phase.Preparation,task.Phase);
            });
            suite.Run("named host contexts expire across real practice and block changes",()=>{
                var clock=new ManualClock();var task=Participant(clock,true);var host=new ViveWozParticipantHost(task,clock,true);
                ViveWozParticipantHost.NamedContext first;Check.True(host.TryReadNamedContext(out first));
                Input(task);Check.True(!host.IsNamedContextCurrent(first));
                task.PrepareNamedRecordingBlockPlan(Actor.Owner,true);task.OwnerBeginPractice(Actor.Owner);
                ViveWozParticipantHost.NamedContext practice;Check.True(host.TryReadNamedContext(out practice));Check.Equal(Phase.Practice,practice.Phase);
                for(int i=0;i<8;i++){Input(task);task.MarkPresented(task.CurrentTrial.TrialId);var ink=task.CurrentTrial.Stimulus.Ink;clock.Advance(.4);Input(task,true,ink);Input(task);}
                Check.True(!host.IsNamedContextCurrent(practice));task.OwnerConfirmNamedPlan(Actor.Owner,"named-host-owner");
                ViveWozParticipantHost.NamedContext block;Check.True(host.TryReadNamedContext(out block));
                Check.Equal(Phase.BlockReady,block.Phase);Check.Equal((ViveNamedOption?)ViveNamedOption.Automatic,block.DeclaredOption);
                Check.Equal((int?)1,block.ListIndex);Check.Equal((int?)0,block.PlanBlock);Check.True(block.PlanConfirmed);
                Check.True(!host.LiveSourceSelected&&!host.CanEnterLiveCountdown);
            });
            suite.Run("named host rejects withdrawal foreign thread and unrelated participant",()=>{
                var clock=new ManualClock();var task=Participant(clock,true);var host=new ViveWozParticipantHost(task,clock,true);
                ViveWozParticipantHost.NamedContext old;Check.True(host.TryReadNamedContext(out old));
                var second=Participant(clock,true);var other=new ViveWozParticipantHost(second,clock,true);Check.True(!other.IsNamedContextCurrent(old));
                Exception foreign=null;var thread=new Thread(()=>{try{ViveWozParticipantHost.NamedContext ignored;host.TryReadNamedContext(out ignored);}catch(Exception e){foreign=e;}});
                thread.Start();Check.True(thread.Join(3000));Check.True(foreign is InvalidOperationException);
                task.ParticipantWithdraw();Check.True(!host.IsNamedContextCurrent(old));ViveWozParticipantHost.NamedContext after;Check.True(!host.TryReadNamedContext(out after));
            });
            suite.Run("named host rejects missing intent and wrong clock",()=>{
                var clock=new ManualClock();var noIntent=Participant(clock,false);
                Check.Throws<InvalidOperationException>(()=>new ViveWozParticipantHost(noIntent,clock,true));
                var task=Participant(clock,true);Check.Throws<InvalidOperationException>(()=>new ViveWozParticipantHost(task,new ManualClock(),true));
                Check.Throws<ArgumentException>(()=>new ViveWozParticipantHost(task,clock,false));
            });
        }
    }
}
