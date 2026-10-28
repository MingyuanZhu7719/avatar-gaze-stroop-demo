using System;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class GazeControlTests
    {
        static readonly Vec3 Forward=new Vec3(0,0,1);
        static readonly Vec3 Right=new Vec3(1,0,0);
        static AutomaticGazeGeometry Geometry()
        { return new AutomaticGazeGeometry(Rotation.Identity,Forward,new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),Right,"test-explicit-geometry-v1"); }
        static AvatarController Controller(ControlSelection selection=ControlSelection.Automatic,Condition condition=Condition.B,
            RunKind kind=RunKind.Simulation,int segment=0)
        { return new AvatarController("session",kind,0,segment,selection,condition,GazeTarget.Participant,Geometry(),selection == ControlSelection.SimulatorRehearsal ? "simulator/operator" : "live/operator"); }
        static HeadEyeSample Sample(ControlSource source=ControlSource.LiveOperator,StreamRole role=StreamRole.OperatorControl,
            string session="session",int block=0,int segment=0,string stream=null,bool head=true,bool eye=true,
            Rotation? rotation=null,Vec3? direction=null)
        { return new HeadEyeSample(session,stream ?? (source == ControlSource.Simulator ? "simulator/operator" : "live/operator"),
            "device","operator-clock",block,segment,5,12.25,12.26,source,role,rotation ?? Rotation.Identity,direction ?? Forward,head,eye); }
        static void PoseNear(AvatarPose expected,AvatarPose actual,double tolerance=0.000001)
        {
            double dot=expected.HeadRotation.X*actual.HeadRotation.X+expected.HeadRotation.Y*actual.HeadRotation.Y+
                expected.HeadRotation.Z*actual.HeadRotation.Z+expected.HeadRotation.W*actual.HeadRotation.W;
            Check.Near(1,Math.Abs(dot),tolerance);
            Check.Near(expected.EyeDirection.X,actual.EyeDirection.X,tolerance);
            Check.Near(expected.EyeDirection.Y,actual.EyeDirection.Y,tolerance);
            Check.Near(expected.EyeDirection.Z,actual.EyeDirection.Z,tolerance);
        }
        static ReferenceGaze Ref(double seconds,GazeTarget initial=GazeTarget.Participant)
        { return AutomaticGaze.Evaluate(Condition.B,initial,seconds,Geometry()); }

        public static void Register(TestSuite t)
        {
            t.Run("AC-05 B exact 3/.5/3/.5 boundaries and seven-second return",()=>{
                double[] times={0,3,3.5,6.5,7,14,7000};
                ReferencePhase[] phases={ReferencePhase.ParticipantDwell,ReferencePhase.TurnToTask,ReferencePhase.TaskDwell,
                    ReferencePhase.TurnToParticipant,ReferencePhase.ParticipantDwell,ReferencePhase.ParticipantDwell,ReferencePhase.ParticipantDwell};
                bool[] taskPose={false,false,true,true,false,false,false};
                for(int i=0;i<times.Length;i++)
                {
                    var reference=Ref(times[i]);Check.Equal(phases[i],reference.Phase);
                    PoseNear(taskPose[i] ? Geometry().TaskPose : Geometry().ParticipantPose,reference.Pose.Value);
                    Check.Near(0,reference.TransitionFraction); Check.Near(times[i],reference.ActiveSeconds);
                }
                Check.Equal(ReferencePhase.ParticipantDwell,Ref(2.999999).Phase);
                Check.Equal(ReferencePhase.TurnToTask,Ref(3.499999).Phase);
                Check.Equal(ReferencePhase.TaskDwell,Ref(6.499999).Phase);
                Check.Equal(ReferencePhase.TurnToParticipant,Ref(6.999999).Phase);
            });
            t.Run("AC-05 B reverse target assignment preserves prescribed durations",()=>{
                Check.Equal(ReferencePhase.TaskDwell,Ref(0,GazeTarget.Task).Phase);
                Check.Equal(ReferencePhase.TurnToParticipant,Ref(3,GazeTarget.Task).Phase);
                Check.Equal(ReferencePhase.ParticipantDwell,Ref(3.5,GazeTarget.Task).Phase);
                Check.Equal(ReferencePhase.TurnToTask,Ref(6.5,GazeTarget.Task).Phase);
                PoseNear(Geometry().TaskPose,Ref(7,GazeTarget.Task).Pose.Value);
                PoseNear(Ref(3.25).Pose.Value,Ref(3.25,GazeTarget.Task).Pose.Value);
                Check.Equal((GazeTarget?)GazeTarget.Task,Ref(3.25,GazeTarget.Task).FromTarget);
                Check.Equal((GazeTarget?)GazeTarget.Participant,Ref(3.25,GazeTarget.Task).ToTarget);
            });
            t.Run("AC-05 automatic head and eyes share smooth timing without boundary snaps",()=>{
                var middle=Ref(3.25);Check.Near(.5,middle.TransitionFraction);
                Check.Near(Math.Sin(Math.PI/8),middle.Pose.Value.HeadRotation.Y);
                Check.Near(Math.Cos(Math.PI/8),middle.Pose.Value.HeadRotation.W);
                Check.Near(Math.Sqrt(.5),middle.Pose.Value.EyeDirection.X);Check.Near(Math.Sqrt(.5),middle.Pose.Value.EyeDirection.Z);
                foreach(double boundary in new[]{3.0,3.5,6.5,7.0})
                {
                    var at=Ref(boundary).Pose.Value;
                    PoseNear(at,Ref(boundary-.00001).Pose.Value,.00000001);
                    PoseNear(at,Ref(boundary+.00001).Pose.Value,.00000001);
                }
                for(int i=0;i<=1400;i++)
                {
                    var pose=Ref(i*.01).Pose.Value;
                    Check.True(pose.HeadRotation.IsUnit);Check.True(pose.EyeDirection.IsUnit);
                }
            });
            t.Run("AC-05 A C fixed explicit geometry and D has no reference pose",()=>{
                foreach(double time in new[]{0.0,3.25,1000.0})
                {
                    PoseNear(Geometry().ParticipantPose,AutomaticGaze.Evaluate(Condition.A,GazeTarget.Task,time,Geometry()).Pose.Value);
                    PoseNear(Geometry().TaskPose,AutomaticGaze.Evaluate(Condition.C,GazeTarget.Participant,time,Geometry()).Pose.Value);
                    var hidden=AutomaticGaze.Evaluate(Condition.D,GazeTarget.Participant,time,Geometry());
                    Check.Equal(ReferencePhase.Hidden,hidden.Phase);Check.True(!hidden.Pose.HasValue);Check.Equal(null,hidden.ToTarget);
                }
                foreach(Phase phase in Enum.GetValues(typeof(Phase)))
                {
                    Check.Equal(phase == Phase.MainTask,Controller().Render(phase,1).AvatarVisible);
                    Check.True(!Controller(condition:Condition.D).Render(phase,1).AvatarVisible);
                }
            });
            t.Run("AC-05 equivalent quaternions and opposite eye directions remain smooth and finite",()=>{
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,Forward,new Rotation(0,0,0,-1),new Vec3(0,0,-1),"opposite-test");
                var first=AutomaticGaze.Evaluate(Condition.B,GazeTarget.Participant,3.25,geometry).Pose.Value;
                var repeat=AutomaticGaze.Evaluate(Condition.B,GazeTarget.Participant,3.25,geometry).Pose.Value;
                Check.Near(1,Math.Abs(first.HeadRotation.W));Check.True(first.EyeDirection.IsUnit);PoseNear(first,repeat);
                Check.Near(0,first.EyeDirection.Z);Check.Near(1,Math.Abs(first.EyeDirection.X));
                var same=new AutomaticGazeGeometry(Rotation.Identity,Forward,Rotation.Identity,Forward,"identical-test");
                PoseNear(same.ParticipantPose,AutomaticGaze.Evaluate(Condition.B,GazeTarget.Task,6.75,same).Pose.Value);
                PoseNear(geometry.TaskPose,AutomaticGaze.Evaluate(Condition.B,GazeTarget.Participant,3.5,geometry).Pose.Value);
            });
            t.Run("AC-05 reference consumes only independent experiment active time across answers and pauses",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock,Condition.B);Fixtures.Practice(experiment,clock,8);Fixtures.Start(experiment,clock);
                experiment.MarkPresented(experiment.CurrentTrial.TrialId);clock.Advance(3.25);
                var controller=Controller();var before=controller.Render(experiment.Phase,experiment.ActiveSeconds);
                var answer=experiment.CurrentTrial.Stimulus.Ink;experiment.ButtonChanged(answer,true);experiment.ButtonChanged(answer,false);
                PoseNear(before.ActualPose.Value,controller.Render(experiment.Phase,experiment.ActiveSeconds).ActualPose.Value);
                experiment.MarkPresented(experiment.CurrentTrial.TrialId);experiment.Pause(Actor.Participant);clock.Advance(200);
                var paused=controller.Render(experiment.Phase,experiment.ActiveSeconds);Check.True(!paused.AvatarVisible);PoseNear(before.ActualPose.Value,paused.ActualPose.Value);
                experiment.Resume(true);clock.Advance(1);experiment.Tick();
                PoseNear(before.ActualPose.Value,Controller(segment:1).Render(experiment.Phase,experiment.ActiveSeconds).ActualPose.Value);
                experiment.MarkPresented(experiment.CurrentTrial.TrialId);clock.Advance(.25);Check.Near(3.5,experiment.ActiveSeconds);
            });
            t.Run("AC-05 live actual pose and acquisition timing are not blended or retimed",()=>{
                var head=new Rotation(0,Math.Sin(-.2),0,Math.Cos(-.2));var eye=new Vec3(0,.6,.8);
                var sample=Sample(rotation:head,direction:eye);var controller=Controller(ControlSelection.LiveWoz);
                foreach(double active in new[]{0.0,3.25,6.75,14.0})
                {
                    var output=controller.Render(Phase.MainTask,active,sample);
                    Check.Equal((ControlSource?)ControlSource.LiveOperator,output.ActualSource);Check.Equal(sample,output.InputSample);
                    Check.Equal(head,output.ActualPose.Value.HeadRotation);Check.Equal(eye,output.ActualPose.Value.EyeDirection);
                    Check.Near(12.25,output.InputSample.CaptureLocalSeconds);Check.Near(12.26,output.InputSample.SendLocalSeconds);
                    Check.True(output.HeadValid);Check.True(output.EyeValid);Check.Equal("simulation-identity-v1",output.MappingVersion);
                    Check.True(!output.MappingValidatedForApparatus);
                }
            });
            t.Run("AC-05 missing live sample never falls back or holds a previous sample as current",()=>{
                var controller=Controller(ControlSelection.LiveWoz);controller.Render(Phase.MainTask,0,Sample());
                var missing=controller.Render(Phase.MainTask,3.25);
                Check.Equal(ControlSource.LiveOperator,missing.SelectedSource);Check.Equal(null,missing.ActualSource);
                Check.True(!missing.HasActualPose);Check.True(!missing.HeadValid);Check.True(!missing.EyeValid);
                Check.Equal(GazeOutputStatus.SampleMissing,missing.Status);Check.True(missing.Reference.Pose.HasValue);
            });
            t.Run("AC-05 locked sources reject other sources and participant measurement role",()=>{
                Check.Throws<InvalidOperationException>(()=>Controller().Render(Phase.MainTask,0,Sample()));
                Check.Throws<InvalidOperationException>(()=>Controller(ControlSelection.LiveWoz).Render(Phase.MainTask,0,Sample(ControlSource.Simulator)));
                Check.Throws<InvalidOperationException>(()=>Controller(ControlSelection.SimulatorRehearsal).Render(Phase.MainTask,0,Sample()));
                Check.Throws<InvalidOperationException>(()=>Controller(ControlSelection.LiveWoz).Render(Phase.MainTask,0,Sample(role:StreamRole.ParticipantMeasurement)));
                var sim=Controller(ControlSelection.SimulatorRehearsal).Render(Phase.MainTask,3.25,Sample(ControlSource.Simulator));
                Check.Equal((ControlSource?)ControlSource.Simulator,sim.ActualSource);Check.Equal("SIMULATION:session",sim.InputSample.SessionLabel);
            });
            t.Run("AC-05 locked stream session block and segment identities are enforced while hidden",()=>{
                var controller=Controller(ControlSelection.LiveWoz,Condition.D);
                Check.Throws<InvalidOperationException>(()=>controller.Render(Phase.Paused,0,Sample(session:"wrong")));
                Check.Throws<InvalidOperationException>(()=>controller.Render(Phase.MainTask,0,Sample(stream:"live/another")));
                Check.Throws<InvalidOperationException>(()=>controller.Render(Phase.MainTask,0,Sample(block:1)));
                Check.Throws<InvalidOperationException>(()=>controller.Render(Phase.MainTask,0,Sample(segment:1)));
                var resumed=Controller(ControlSelection.LiveWoz,segment:1).Render(Phase.MainTask,3.25,Sample(segment:1));
                Check.True(resumed.HeadValid);
            });
            t.Run("AC-06 reported and numerical head eye validity stay separate without raw repair",()=>{
                var controller=Controller(ControlSelection.LiveWoz);
                var headOff=controller.Render(Phase.MainTask,0,Sample(head:false));Check.True(!headOff.HeadValid);Check.True(headOff.EyeValid);
                var eyeOff=controller.Render(Phase.MainTask,0,Sample(eye:false));Check.True(eyeOff.HeadValid);Check.True(!eyeOff.EyeValid);
                foreach(var badHead in new[]{new Rotation(),new Rotation(0,0,0,2),new Rotation(Double.NaN,0,0,1),new Rotation(0,Double.PositiveInfinity,0,1)})
                {
                    var sample=Sample(rotation:badHead);var output=controller.Render(Phase.MainTask,0,sample);
                    Check.True(sample.HeadReportedValid);Check.True(!sample.HeadValid);Check.True(!output.HeadValid);Check.True(output.EyeValid);
                    Check.Equal(badHead,sample.RawHeadRotation);Check.Equal(badHead,output.ActualPose.Value.HeadRotation);
                }
                foreach(var badEye in new[]{new Vec3(),new Vec3(0,0,2),new Vec3(Double.NaN,0,1),new Vec3(0,Double.NegativeInfinity,1)})
                {
                    var sample=Sample(direction:badEye);var output=controller.Render(Phase.MainTask,0,sample);
                    Check.True(sample.EyeReportedValid);Check.True(!sample.EyeValid);Check.True(output.HeadValid);Check.True(!output.EyeValid);
                    Check.Equal(badEye,sample.RawEyeDirection);Check.Equal(badEye,output.ActualPose.Value.EyeDirection);
                }
            });
            t.Run("AC-05 apparatus rejects simulator and unvalidated identity mapping",()=>{
                Check.Throws<InvalidOperationException>(()=>Controller(ControlSelection.SimulatorRehearsal,kind:RunKind.Apparatus));
                var output=Controller(ControlSelection.LiveWoz,kind:RunKind.Apparatus).Render(Phase.MainTask,3.25,Sample());
                Check.Equal(GazeOutputStatus.MappingUnavailable,output.Status);Check.Equal((ControlSource?)ControlSource.LiveOperator,output.ActualSource);
                Check.True(!output.HasActualPose);Check.True(!output.HeadValid);Check.True(!output.EyeValid);
                Check.True(output.InputSample.HeadValid);Check.True(output.Reference.Pose.HasValue);
            });
            t.Run("AC-06 transport usability masks axes without changing raw tracking provenance",()=>{
                var controller=Controller(ControlSelection.LiveWoz);var raw=Sample();
                var output=controller.RenderValidated(Phase.MainTask,1,raw,false,true);
                Check.True(!output.HeadValid);Check.True(output.EyeValid);Check.Equal(raw,output.InputSample);Check.True(raw.HeadReportedValid);
                var invalid=Sample(eye:false);var masked=controller.RenderValidated(Phase.MainTask,1,invalid,true,true);
                Check.True(masked.HeadValid);Check.True(!masked.EyeValid);Check.True(!invalid.EyeReportedValid);
                Check.True(!controller.RenderValidated(Phase.MainTask,1,null,false,false).HasActualPose);
                Check.Throws<ArgumentException>(()=>controller.RenderValidated(Phase.MainTask,1,null,true,false));
                Check.Throws<InvalidOperationException>(()=>Controller().RenderValidated(Phase.MainTask,1,null,false,false));
            });
            t.Run("AC-05 real adapter reports unavailable without synthesizing operator signals",()=>{
                IHeadEyeSource source=new UnavailableLiveOperatorSource("live/operator");HeadEyeSample sample;
                Check.Equal(ControlSource.LiveOperator,source.Source);Check.Equal(StreamRole.OperatorControl,source.Role);
                Check.True(!source.Available);Check.True(!String.IsNullOrWhiteSpace(source.UnavailableReason));
                Check.True(!source.TryRead(out sample));Check.Equal(null,sample);
            });
            t.Run("AC-05 deterministic simulator uses distinct stream and independent head eye motion",()=>{
                var clock=new ManualClock {Now=1};IHeadEyeSource source=new DeterministicSimulatorSource("session",RunKind.Simulation,0,0,"simulator/operator",clock);
                HeadEyeSample first,next,repeat;Check.True(source.TryRead(out first));clock.Advance(.01);Check.True(source.TryRead(out next));
                var again=new DeterministicSimulatorSource("session",RunKind.Simulation,0,0,"simulator/operator",clock);again.TryRead(out repeat);
                Check.Equal(ControlSource.Simulator,next.Source);Check.Equal("simulator-no-hardware",next.DeviceId);Check.Equal(0L,first.Sequence);Check.Equal(1L,next.Sequence);
                Check.Equal(next.RawHeadRotation,repeat.RawHeadRotation);Check.Equal(next.RawEyeDirection,repeat.RawEyeDirection);
                Check.True(!first.RawHeadRotation.Equals(next.RawHeadRotation));Check.True(!first.RawEyeDirection.Equals(next.RawEyeDirection));
                Check.True(next.HeadValid);Check.True(next.EyeValid);Check.Near(clock.Now,next.CaptureLocalSeconds);Check.Near(clock.Now,next.SendLocalSeconds);
                Check.True(Math.Abs(2*Math.Atan2(next.RawHeadRotation.Y,next.RawHeadRotation.W)-Math.Atan2(next.RawEyeDirection.X,next.RawEyeDirection.Z))>.001);
                Check.Throws<InvalidOperationException>(()=>new DeterministicSimulatorSource("session",RunKind.Apparatus,0,0,"simulator/operator",clock));
                clock.Now=.5;Check.Throws<InvalidOperationException>(()=>source.TryRead(out next));
            });
            t.Run("AC-05 invalid reference geometry time and sample envelopes are rejected",()=>{
                Check.Throws<ArgumentException>(()=>new AutomaticGazeGeometry(new Rotation(),Forward,Rotation.Identity,Forward,"bad"));
                Check.Throws<ArgumentException>(()=>new AutomaticGazeGeometry(Rotation.Identity,new Vec3(),Rotation.Identity,Forward,"bad"));
                foreach(double time in new[]{-1.0,Double.NaN,Double.PositiveInfinity}) Check.Throws<ArgumentOutOfRangeException>(()=>Ref(time));
                Check.Throws<ArgumentException>(()=>AutomaticGaze.Evaluate((Condition)99,GazeTarget.Participant,0,Geometry()));
                Check.Throws<ArgumentException>(()=>new AvatarController("session",RunKind.Simulation,0,0,ControlSelection.LiveWoz,Condition.A,GazeTarget.Participant,Geometry()));
                Check.Throws<ArgumentException>(()=>Sample(ControlSource.Simulator,stream:"live/operator"));
                Check.Throws<ArgumentOutOfRangeException>(()=>Sample(block:4));
                Check.Throws<ArgumentException>(()=>new HeadEyeSample("session","live/operator","device","clock",0,0,0,2,1,ControlSource.LiveOperator,StreamRole.OperatorControl,Rotation.Identity,Forward,true,true));
            });
        }
    }
}
