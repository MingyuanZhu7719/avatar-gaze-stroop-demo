using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveWozPresentationPolicyTests
    {
        sealed class TestClock:IClock
        {
            public double Now {get;set;}
            public string ClockId {get{return "test-clock";}}
        }
        static AutomaticGazeGeometry Geometry()
        {
            return new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),
                new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"test-geometry");
        }
        static AvatarOutput Legacy(Condition condition)
        {
            var controller=new AvatarController("test-session",RunKind.Simulation,0,0,
                ControlSelection.Automatic,condition,GazeTarget.Participant,Geometry());
            return controller.Render(Phase.MainTask,0);
        }
        static void Input(ViveDevelopmentParticipant participant,bool pressed=false,Colour colour=Colour.Red)
        {
            double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0;
            double y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
            participant.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,pressed,x,y));
        }
        static ViveDevelopmentParticipant Named(ViveNamedOption option)
        {
            var selected=ViveNamedOptionProvenance.Select(Actor.Owner,option,1,
                option==ViveNamedOption.Automatic?"test-geometry":null);
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"test-plan",selected);
            var clock=new TestClock();
            var mapping=new Dictionary<ViveTrackpadDirection,Colour>{
                {ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var participant=new ViveDevelopmentParticipant(SessionConfiguration.NamedPlanDevelopment("test-session",37,plan),
                clock,Geometry(),mapping,.5,true);
            Input(participant);participant.OwnerBeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {
                Input(participant);participant.MarkPresented(participant.CurrentTrial.TrialId);
                Colour ink=participant.CurrentTrial.Stimulus.Ink;clock.Now+=.4;
                Input(participant,true,ink);Input(participant);
            }
            participant.OwnerConfirmNamedPlan(Actor.Owner,"test-owner");participant.OwnerBeginBlock(Actor.Owner);
            clock.Now+=3;participant.Tick();Input(participant);
            Check.Equal(Phase.MainTask,participant.Phase);
            return participant;
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE automatic-only policy preserves legacy A B C and D visibility",()=>{
                foreach(Condition c in new[]{Condition.A,Condition.B,Condition.C,Condition.D})
                {
                    AvatarOutput output=Legacy(c);
                    Check.True(ViveWozPresentationPolicy.IsAutomaticOnly(output));
                    Check.Equal(c!=Condition.D,output.AvatarVisible);
                    Check.Equal(ControlSource.Automatic,output.SelectedSource);
                    Check.Equal((ControlSource?)ControlSource.Automatic,output.ActualSource);
                }
            });
            suite.Run("VIVE automatic-only policy permits named Automatic and clears No Avatar",()=>{
                var automatic=Named(ViveNamedOption.Automatic).CurrentAvatar;
                Check.True(ViveWozPresentationPolicy.IsAutomaticOnly(automatic));
                var absent=Named(ViveNamedOption.NoAvatar);
                Check.True(absent.CurrentAvatar==null&&!absent.ParticipantView.OperatorAvatarVisible);
                Check.True(!ViveWozPresentationPolicy.IsAutomaticOnly(absent.CurrentAvatar));
            });
            suite.Run("VIVE automatic-only policy rejects all external and missing sources even if flags look valid",()=>{
                foreach(ControlSource source in new[]{ControlSource.LiveOperator,ControlSource.Simulator})
                {
                    var sample=new AvatarOutput{SelectedSource=source,ActualSource=source,
                        Status=GazeOutputStatus.SampleAvailable,AvatarVisible=true,HeadValid=true,EyeValid=true,
                        ActualPose=new AvatarPose(Rotation.Identity,new Vec3(0,0,1))};
                    Check.True(!ViveWozPresentationPolicy.IsAutomaticOnly(sample));
                    sample.ActualSource=null;sample.Status=GazeOutputStatus.SampleMissing;
                    Check.True(!ViveWozPresentationPolicy.IsAutomaticOnly(sample));
                }
            });
            suite.Run("VIVE automatic-only policy rejects mixed or nonautomatic status",()=>{
                var output=Legacy(Condition.A);
                output.ActualSource=ControlSource.LiveOperator;
                Check.True(!ViveWozPresentationPolicy.IsAutomaticOnly(output));
                output.ActualSource=ControlSource.Automatic;output.SelectedSource=ControlSource.LiveOperator;
                Check.True(!ViveWozPresentationPolicy.IsAutomaticOnly(output));
                output.SelectedSource=ControlSource.Automatic;output.Status=GazeOutputStatus.SampleAvailable;
                Check.True(!ViveWozPresentationPolicy.IsAutomaticOnly(output));
            });
            suite.Run("VIVE automatic-only policy rejects visible missing invalid or partial pose",()=>{
                var output=Legacy(Condition.A);
                output.ActualPose=null;Check.True(!ViveWozPresentationPolicy.IsAutomaticOnly(output));
                output=Legacy(Condition.A);output.HeadValid=false;Check.True(!ViveWozPresentationPolicy.IsAutomaticOnly(output));
                output=Legacy(Condition.A);output.EyeValid=false;Check.True(!ViveWozPresentationPolicy.IsAutomaticOnly(output));
                output=Legacy(Condition.A);output.ActualPose=new AvatarPose(new Rotation(0,0,0,0),new Vec3(0,0,1));
                Check.True(!ViveWozPresentationPolicy.IsAutomaticOnly(output));
                output=Legacy(Condition.A);output.ActualPose=new AvatarPose(Rotation.Identity,new Vec3(Double.NaN,0,1));
                Check.True(!ViveWozPresentationPolicy.IsAutomaticOnly(output));
            });
            suite.Run("VIVE blocked-avatar feedback does not label normal absence as disconnection",()=>{
                Check.True(ViveWozPresentationPolicy.BlockedFeedback(null)==null);
                foreach(Condition c in new[]{Condition.A,Condition.B,Condition.C,Condition.D})
                    Check.True(ViveWozPresentationPolicy.BlockedFeedback(Legacy(c))==null);
                Check.True(ViveWozPresentationPolicy.BlockedFeedback(Named(ViveNamedOption.NoAvatar).CurrentAvatar)==null);
                Check.True(ViveWozPresentationPolicy.BlockedFeedback(Named(ViveNamedOption.Automatic).CurrentAvatar)==null);
            });
            suite.Run("VIVE blocked-avatar feedback distinguishes external input from invalid automatic pose",()=>{
                var output=Legacy(Condition.A);
                output.SelectedSource=ControlSource.LiveOperator;
                Check.Equal("External avatar control is unavailable; preview avatar cleared.",ViveWozPresentationPolicy.BlockedFeedback(output));
                output.SelectedSource=ControlSource.Simulator;
                Check.Equal("External avatar control is unavailable; preview avatar cleared.",ViveWozPresentationPolicy.BlockedFeedback(output));
                output=Legacy(Condition.A);output.EyeValid=false;
                Check.Equal("Avatar pose is unavailable or invalid; preview avatar cleared.",ViveWozPresentationPolicy.BlockedFeedback(output));
            });
        }
    }
}
