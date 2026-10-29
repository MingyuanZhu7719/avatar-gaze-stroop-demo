using System;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class GeometryTests
    {
        static Vec3 Subtract(Vec3 a,Vec3 b) { return new Vec3(a.X-b.X,a.Y-b.Y,a.Z-b.Z); }
        static Vec3 Unit(Vec3 direction)
        {
            double length=Math.Sqrt(direction.X*direction.X+direction.Y*direction.Y+direction.Z*direction.Z);
            return new Vec3(direction.X/length,direction.Y/length,direction.Z/length);
        }
        static Vec3 Forward(Rotation q)
        { return new Vec3(2*(q.X*q.Z+q.W*q.Y),2*(q.Y*q.Z-q.W*q.X),1-2*(q.X*q.X+q.Y*q.Y)); }
        static Vec3 Yaw(Vec3 direction,double degrees)
        {
            double radians=degrees*Math.PI/180,cos=Math.Cos(radians),sin=Math.Sin(radians);
            return new Vec3(cos*direction.X+sin*direction.Z,direction.Y,-sin*direction.X+cos*direction.Z);
        }
        static void DirectionNear(Vec3 expected,Vec3 actual,double tolerance=0.000000000001)
        {
            Check.True(actual.IsUnit);Check.Near(expected.X,actual.X,tolerance);Check.Near(expected.Y,actual.Y,tolerance);Check.Near(expected.Z,actual.Z,tolerance);
            Check.Near(1,expected.X*actual.X+expected.Y*actual.Y+expected.Z*actual.Z,tolerance);
        }
        static void CoOriented(AvatarPose pose)
        { Check.True(pose.HeadRotation.IsUnit);DirectionNear(pose.EyeDirection,Forward(pose.HeadRotation)); }

        public static void Register(TestSuite t)
        {
            t.Run("AC-05 classroom scene A and C head eye rays point to actual world targets",()=>{
                var head=new Vec3(.65,1.6,1.9);var participant=new Vec3(0,1.6,0);var task=new Vec3(-.5,1.65,2);
                var participantWorld=Subtract(participant,head);var taskWorld=Subtract(task,head);
                // The fixed body faces yaw 225. Its inverse and forward rotations differ.
                var geometry=DevelopmentGeometry.FromLocalDirections(Yaw(participantWorld,-225),Yaw(taskWorld,-225),"classroom-test-v2");
                var a=AutomaticGaze.Evaluate(Condition.A,GazeTarget.Participant,0,geometry).Pose.Value;
                var c=AutomaticGaze.Evaluate(Condition.C,GazeTarget.Participant,0,geometry).Pose.Value;
                DirectionNear(Unit(participantWorld),Yaw(Forward(a.HeadRotation),225));
                DirectionNear(Unit(participantWorld),Yaw(a.EyeDirection,225));
                DirectionNear(Unit(taskWorld),Yaw(Forward(c.HeadRotation),225));
                DirectionNear(Unit(taskWorld),Yaw(c.EyeDirection,225));
                CoOriented(a);CoOriented(c);Check.Equal("classroom-test-v2",geometry.Version);
                Check.True(a.EyeDirection.X < 0,"Participant target lies on negative avatar-root-local X");
                Check.True(c.EyeDirection.X > 0,"Task target lies on positive avatar-root-local X");
            });
            t.Run("AC-05 development geometry normalizes all octants and coorients head eyes",()=>{
                for(int x=-2;x<=2;x++)for(int y=-2;y<=2;y++)for(int z=-2;z<=2;z++)
                {
                    if(x == 0 && y == 0 && z == 0) continue;
                    var direction=new Vec3(x,y,z);
                    var geometry=DevelopmentGeometry.FromLocalDirections(direction,new Vec3(-x,-y,-z),"grid-test");
                    DirectionNear(Unit(direction),geometry.ParticipantPose.EyeDirection);
                    CoOriented(geometry.ParticipantPose);CoOriented(geometry.TaskPose);
                }
            });
            t.Run("AC-05 backwards target has deterministic half turn and near backwards remains stable",()=>{
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-5),new Vec3(0,0,9),"axis-test");
                Check.Equal(new Rotation(0,1,0,0),geometry.ParticipantPose.HeadRotation);
                Check.Equal(Rotation.Identity,geometry.TaskPose.HeadRotation);
                CoOriented(geometry.ParticipantPose);CoOriented(geometry.TaskPose);
                foreach(double offset in new[]{.001,1e-8,1e-16,1e-200,Double.Epsilon})
                {
                    var near=DevelopmentGeometry.FromLocalDirections(new Vec3(offset,0,-1),new Vec3(0,-offset,-1),"near-backwards-test");
                    CoOriented(near.ParticipantPose);CoOriented(near.TaskPose);
                    Check.Near(-1,near.ParticipantPose.EyeDirection.Z,.000001);
                    Check.True(near.ParticipantPose.HeadRotation.Y > 0);
                    Check.True(near.TaskPose.HeadRotation.X > 0);
                }
            });
            t.Run("AC-05 finite huge and subnormal target magnitudes retain unit directions",()=>{
                var huge=DevelopmentGeometry.FromLocalDirections(new Vec3(Double.MaxValue,Double.MaxValue,-Double.MaxValue),new Vec3(Double.MaxValue,0,0),"huge-test");
                DirectionNear(Unit(new Vec3(1,1,-1)),huge.ParticipantPose.EyeDirection);
                CoOriented(huge.ParticipantPose);CoOriented(huge.TaskPose);
                var tiny=DevelopmentGeometry.FromLocalDirections(new Vec3(Double.Epsilon,Double.Epsilon,-Double.Epsilon),new Vec3(0,0,Double.Epsilon),"tiny-test");
                DirectionNear(huge.ParticipantPose.EyeDirection,tiny.ParticipantPose.EyeDirection);
                CoOriented(tiny.ParticipantPose);CoOriented(tiny.TaskPose);
                var ordinary=DevelopmentGeometry.FromLocalDirections(new Vec3(2,-3,4),new Vec3(-5,6,7),"scale-test");
                var scaled=DevelopmentGeometry.FromLocalDirections(new Vec3(20,-30,40),new Vec3(-.5,.6,.7),"scale-test");
                DirectionNear(ordinary.ParticipantPose.EyeDirection,scaled.ParticipantPose.EyeDirection);
                DirectionNear(ordinary.TaskPose.EyeDirection,scaled.TaskPose.EyeDirection);
            });
            t.Run("AC-05 development geometry rejects zero nonfinite directions and missing version",()=>{
                var forward=new Vec3(0,0,1);
                foreach(var invalid in new[]{new Vec3(),new Vec3(Double.NaN,0,1),new Vec3(0,Double.PositiveInfinity,1),new Vec3(0,0,Double.NegativeInfinity)})
                {
                    Check.Throws<ArgumentException>(()=>DevelopmentGeometry.FromLocalDirections(invalid,forward,"invalid-test"));
                    Check.Throws<ArgumentException>(()=>DevelopmentGeometry.FromLocalDirections(forward,invalid,"invalid-test"));
                }
                Check.Throws<ArgumentException>(()=>DevelopmentGeometry.FromLocalDirections(forward,forward,null));
                Check.Throws<ArgumentException>(()=>DevelopmentGeometry.FromLocalDirections(forward,forward," "));
            });
        }
    }
}
