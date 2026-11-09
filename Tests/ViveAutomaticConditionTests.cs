using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveAutomaticConditionTests
    {
        static AutomaticGazeGeometry Geometry()
        {
            return new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),
                new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"synthetic-explicit-geometry");
        }
        static ViveAutomaticReference At(double time){return ViveAutomaticCondition.Evaluate(time,Geometry());}
        static void PoseNear(AvatarPose expected,AvatarPose actual,double tolerance=.000001)
        {
            var a=expected.HeadRotation;var b=actual.HeadRotation;
            Check.Near(1,Math.Abs(a.X*b.X+a.Y*b.Y+a.Z*b.Z+a.W*b.W),tolerance);
            Check.Near(expected.EyeDirection.X,actual.EyeDirection.X,tolerance);
            Check.Near(expected.EyeDirection.Y,actual.EyeDirection.Y,tolerance);
            Check.Near(expected.EyeDirection.Z,actual.EyeDirection.Z,tolerance);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE new Automatic starts at board and follows exact 3/.5/3/.5 active-second cycle",()=>{
                double[] times={0,3,3.5,6.5,7,14};
                var phases=new[]{ReferencePhase.TaskDwell,ReferencePhase.TurnToParticipant,ReferencePhase.ParticipantDwell,
                    ReferencePhase.TurnToTask,ReferencePhase.TaskDwell,ReferencePhase.TaskDwell};
                for(int i=0;i<times.Length;i++)
                {
                    var result=At(times[i]);Check.Equal(phases[i],result.Phase);
                    PoseNear(i==2||i==3?Geometry().ParticipantPose:Geometry().TaskPose,result.Pose);
                    Check.Near(times[i],result.ActiveSeconds);Check.Near(0,result.TransitionFraction);
                }
                Check.Equal(ReferencePhase.TaskDwell,At(2.999999).Phase);
                Check.Equal(ReferencePhase.TurnToParticipant,At(3.499999).Phase);
                Check.Equal(ReferencePhase.ParticipantDwell,At(6.499999).Phase);
                Check.Equal(ReferencePhase.TurnToTask,At(6.999999).Phase);
            });
            suite.Run("VIVE new Automatic uses one smooth phase for head and eyes in both turns",()=>{
                foreach(double time in new[]{3.25,6.75})
                {
                    var result=At(time);Check.Near(.5,result.TransitionFraction);
                    Check.Near(Math.Sin(Math.PI/8),Math.Abs(result.Pose.HeadRotation.Y));
                    Check.Near(Math.Cos(Math.PI/8),Math.Abs(result.Pose.HeadRotation.W));
                    Check.Near(Math.Sqrt(.5),result.Pose.EyeDirection.X);
                    Check.Near(Math.Sqrt(.5),result.Pose.EyeDirection.Z);
                }
                foreach(double boundary in new[]{3.0,3.5,6.5,7.0})
                {
                    var actual=At(boundary).Pose;
                    PoseNear(actual,At(boundary-.00001).Pose,.00000001);
                    PoseNear(actual,At(boundary+.00001).Pose,.00000001);
                }
            });
            suite.Run("VIVE new Automatic is independent of participant answers and follows paused active time",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock,Condition.B);
                Fixtures.Practice(experiment,clock,8);Fixtures.Start(experiment,clock);
                experiment.MarkPresented(experiment.CurrentTrial.TrialId);clock.Advance(3.25);
                var before=At(experiment.ActiveSeconds);
                var answer=experiment.CurrentTrial.Stimulus.Ink;
                experiment.ButtonChanged(answer,true);experiment.ButtonChanged(answer,false);
                PoseNear(before.Pose,At(experiment.ActiveSeconds).Pose);
                experiment.MarkPresented(experiment.CurrentTrial.TrialId);experiment.Pause(Actor.Participant);clock.Advance(200);
                PoseNear(before.Pose,At(experiment.ActiveSeconds).Pose);
                experiment.Resume(true);clock.Advance(1);experiment.Tick();
                PoseNear(before.Pose,At(experiment.ActiveSeconds).Pose);
                experiment.MarkPresented(experiment.CurrentTrial.TrialId);clock.Advance(.25);
                Check.Equal(ReferencePhase.ParticipantDwell,At(experiment.ActiveSeconds).Phase);
            });
            suite.Run("VIVE new Automatic requires explicit geometry and finite nonnegative active time",()=>{
                Check.Throws<ArgumentNullException>(()=>ViveAutomaticCondition.Evaluate(0,null));
                foreach(double time in new[]{-1.0,Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity})
                    Check.Throws<ArgumentException>(()=>At(time));
                Check.Equal("vive-automatic-task-first-3-0.5-v1",ViveAutomaticCondition.ScheduleVersion);
            });
            suite.Run("VIVE new Automatic does not change legacy participant-first B or no-avatar D",()=>{
                var geometry=Geometry();
                var newOption=ViveAutomaticCondition.Evaluate(0,geometry);
                var oldB=AutomaticGaze.Evaluate(Condition.B,GazeTarget.Participant,0,geometry);
                Check.Equal(ReferencePhase.TaskDwell,newOption.Phase);
                Check.Equal(ReferencePhase.ParticipantDwell,oldB.Phase);
                PoseNear(geometry.TaskPose,newOption.Pose);
                PoseNear(geometry.ParticipantPose,oldB.Pose.Value);
                var noAvatar=AutomaticGaze.Evaluate(Condition.D,GazeTarget.Task,3.25,geometry);
                Check.Equal(ReferencePhase.Hidden,noAvatar.Phase);Check.True(!noAvatar.Pose.HasValue);
            });
            suite.Run("VIVE new Automatic rejects mismatched head and eye directions at either endpoint",()=>{
                var forward=new Vec3(0,0,1);var right=new Vec3(1,0,0);
                var badParticipant=new AutomaticGazeGeometry(Rotation.Identity,right,Rotation.Identity,forward,"bad-participant");
                var badTask=new AutomaticGazeGeometry(Rotation.Identity,forward,Rotation.Identity,right,"bad-task");
                Check.Throws<ArgumentException>(()=>ViveAutomaticCondition.Evaluate(0,badParticipant));
                Check.Throws<ArgumentException>(()=>ViveAutomaticCondition.Evaluate(0,badTask));
                // A small representation error may pass, but is not evidence
                // of tracking accuracy. A larger angular discrepancy is refused.
                double a=.00005,b=.0002;
                var near=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(Math.Sin(a),0,Math.Cos(a)),Rotation.Identity,forward,"near");
                var far=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(Math.Sin(b),0,Math.Cos(b)),Rotation.Identity,forward,"far");
                Check.Equal(ReferencePhase.TaskDwell,ViveAutomaticCondition.Evaluate(0,near).Phase);
                Check.Throws<ArgumentException>(()=>ViveAutomaticCondition.Evaluate(0,far));
                Check.Near(3,ViveAutomaticCondition.DwellSeconds);Check.Near(.5,ViveAutomaticCondition.TurnSeconds);
                Check.Near(7,ViveAutomaticCondition.CycleSeconds);
            });
            suite.Run("VIVE new Automatic eyes follow interpolated head forward even with endpoint roll",()=>{
                // Participant yaw +90 with roll: the endpoint forward is +X,
                // but independent head/vector slerps disagree mid-transition.
                var geometry=new AutomaticGazeGeometry(new Rotation(.5,.5,.5,.5),new Vec3(1,0,0),
                    Rotation.Identity,new Vec3(0,0,1),"yaw-plus-roll");
                foreach(double time in new[]{3.125,3.25,3.375,6.625,6.75,6.875})
                {
                    var pose=ViveAutomaticCondition.Evaluate(time,geometry).Pose;
                    var q=pose.HeadRotation;double n=Math.Sqrt(q.X*q.X+q.Y*q.Y+q.Z*q.Z+q.W*q.W);
                    double x=q.X/n,y=q.Y/n,z=q.Z/n,w=q.W/n;
                    Check.Near(2*(x*z+w*y),pose.EyeDirection.X,1e-12);
                    Check.Near(2*(y*z-w*x),pose.EyeDirection.Y,1e-12);
                    Check.Near(1-2*(x*x+y*y),pose.EyeDirection.Z,1e-12);
                    Check.True(pose.EyeDirection.IsUnit);
                }
                var halfway=ViveAutomaticCondition.Evaluate(3.25,geometry).Pose.EyeDirection;
                Check.Near(2.0/3,halfway.X);Check.Near(-1.0/3,halfway.Y);Check.Near(2.0/3,halfway.Z);
            });
        }
    }
}
