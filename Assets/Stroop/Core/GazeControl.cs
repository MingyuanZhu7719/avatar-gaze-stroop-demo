using System;

namespace AvatarStroop.Core
{
    public enum ControlSource { Automatic, LiveOperator, Simulator }
    public enum StreamRole { OperatorControl, ParticipantMeasurement }
    public enum ReferencePhase { ParticipantDwell, TaskDwell, TurnToParticipant, TurnToTask, Hidden }
    public enum GazeOutputStatus { Automatic, SampleAvailable, SampleMissing, MappingUnavailable }

    public struct Vec3
    {
        public double X { get; private set; }
        public double Y { get; private set; }
        public double Z { get; private set; }
        public Vec3(double x, double y, double z) { X=x; Y=y; Z=z; }
        public bool IsFinite { get { return GazeNumbers.Finite(X) && GazeNumbers.Finite(Y) && GazeNumbers.Finite(Z); } }
        public double LengthSquared { get { return X*X+Y*Y+Z*Z; } }
        public bool IsUnit { get { return IsFinite && Math.Abs(LengthSquared-1) <= GazeNumbers.UnitTolerance; } }
        internal Vec3 Unit()
        {
            double length=Math.Sqrt(LengthSquared);
            return new Vec3(X/length,Y/length,Z/length);
        }
        internal static double Dot(Vec3 a,Vec3 b) { return a.X*b.X+a.Y*b.Y+a.Z*b.Z; }
        internal static Vec3 Blend(Vec3 a,Vec3 b,double x,double y)
        { return new Vec3(a.X*x+b.X*y,a.Y*x+b.Y*y,a.Z*x+b.Z*y); }
        internal static Vec3 Slerp(Vec3 a,Vec3 b,double amount)
        {
            if(amount <= 0) return a;
            if(amount >= 1) return b;
            a=a.Unit(); b=b.Unit();
            double dot=GazeNumbers.Clamp(Dot(a,b),-1,1);
            if(dot > .999999) return Blend(a,b,1-amount,amount).Unit();
            // An exactly opposite pair has infinitely many arcs. Pick a reproducible
            // perpendicular axis in the simulation coordinate system, never a zero vector.
            Vec3 tangent=Blend(b,a,1,-dot);
            if(tangent.LengthSquared < 1e-24)
            {
                Vec3 axis=Math.Abs(a.X) <= Math.Abs(a.Y) && Math.Abs(a.X) <= Math.Abs(a.Z)
                    ? new Vec3(1,0,0) : Math.Abs(a.Y) <= Math.Abs(a.Z) ? new Vec3(0,1,0) : new Vec3(0,0,1);
                tangent=Blend(axis,a,1,-Dot(axis,a));
            }
            tangent=tangent.Unit();
            double angle=Math.Acos(dot)*amount;
            return Blend(a,tangent,Math.Cos(angle),Math.Sin(angle)).Unit();
        }
    }

    public struct Rotation
    {
        public double X { get; private set; }
        public double Y { get; private set; }
        public double Z { get; private set; }
        public double W { get; private set; }
        public Rotation(double x,double y,double z,double w) { X=x; Y=y; Z=z; W=w; }
        public static Rotation Identity { get { return new Rotation(0,0,0,1); } }
        public bool IsFinite { get { return GazeNumbers.Finite(X) && GazeNumbers.Finite(Y) && GazeNumbers.Finite(Z) && GazeNumbers.Finite(W); } }
        public double LengthSquared { get { return X*X+Y*Y+Z*Z+W*W; } }
        public bool IsUnit { get { return IsFinite && Math.Abs(LengthSquared-1) <= GazeNumbers.UnitTolerance; } }
        internal Rotation Unit()
        {
            double length=Math.Sqrt(LengthSquared);
            return new Rotation(X/length,Y/length,Z/length,W/length);
        }
        internal static Rotation Slerp(Rotation a,Rotation b,double amount)
        {
            if(amount <= 0) return a;
            if(amount >= 1) return b;
            a=a.Unit(); b=b.Unit();
            double dot=a.X*b.X+a.Y*b.Y+a.Z*b.Z+a.W*b.W;
            if(dot < 0) { b=new Rotation(-b.X,-b.Y,-b.Z,-b.W); dot=-dot; }
            dot=GazeNumbers.Clamp(dot,-1,1);
            double from=1-amount,to=amount;
            if(dot < .999999)
            {
                double angle=Math.Acos(dot),denominator=Math.Sin(angle);
                from=Math.Sin((1-amount)*angle)/denominator; to=Math.Sin(amount*angle)/denominator;
            }
            return new Rotation(a.X*from+b.X*to,a.Y*from+b.Y*to,a.Z*from+b.Z*to,a.W*from+b.W*to).Unit();
        }
    }

    internal static class GazeNumbers
    {
        // Numerical representation tolerance, not a tracking/calibration quality threshold.
        internal const double UnitTolerance=0.000001;
        internal static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        internal static double Clamp(double value,double minimum,double maximum) { return Math.Max(minimum,Math.Min(maximum,value)); }
        internal static void Time(double value,string name)
        { if(!Finite(value) || value < 0) throw new ArgumentOutOfRangeException(name); }
        internal static void Identity(string value,string name)
        { if(String.IsNullOrWhiteSpace(value)) throw new ArgumentException("Explicit identity required",name); }
    }

    public struct AvatarPose
    {
        // Both channels use avatar-root-local coordinates in the simulation map.
        // EyeDirection is not head-local; an articulated renderer removes the head
        // rotation when converting this direction into each eye's local orientation.
        public Rotation HeadRotation { get; private set; }
        public Vec3 EyeDirection { get; private set; }
        public AvatarPose(Rotation headRotation,Vec3 eyeDirection) { HeadRotation=headRotation; EyeDirection=eyeDirection; }
    }

    // Raw readings and reported flags are retained exactly; invalid input is never repaired.
    // Capture and send are local monotonic readings on ClockId, not synchronized global time.
    public sealed class HeadEyeSample
    {
        public string SessionId { get; private set; }
        public string StreamId { get; private set; }
        public string DeviceId { get; private set; }
        public string ClockId { get; private set; }
        public int BlockIndex { get; private set; }
        public int Segment { get; private set; }
        public long Sequence { get; private set; }
        public double CaptureLocalSeconds { get; private set; }
        public double SendLocalSeconds { get; private set; }
        public ControlSource Source { get; private set; }
        public StreamRole Role { get; private set; }
        public Rotation RawHeadRotation { get; private set; }
        public Vec3 RawEyeDirection { get; private set; }
        public bool HeadReportedValid { get; private set; }
        public bool EyeReportedValid { get; private set; }
        public bool HeadValid { get { return HeadReportedValid && RawHeadRotation.IsUnit; } }
        public bool EyeValid { get { return EyeReportedValid && RawEyeDirection.IsUnit; } }
        public string SessionLabel { get { return Source == ControlSource.Simulator ? "SIMULATION:"+SessionId : SessionId; } }
        public HeadEyeSample(string sessionId,string streamId,string deviceId,string clockId,int blockIndex,int segment,
            long sequence,double captureLocalSeconds,double sendLocalSeconds,ControlSource source,StreamRole role,
            Rotation rawHeadRotation,Vec3 rawEyeDirection,bool headReportedValid,bool eyeReportedValid)
        {
            GazeNumbers.Identity(sessionId,"sessionId"); GazeNumbers.Identity(streamId,"streamId");
            GazeNumbers.Identity(deviceId,"deviceId"); GazeNumbers.Identity(clockId,"clockId");
            if(blockIndex < 0 || blockIndex > 3 || segment < 0 || sequence < 0) throw new ArgumentOutOfRangeException("sampleIdentity");
            if(!Enum.IsDefined(typeof(ControlSource),source) || !Enum.IsDefined(typeof(StreamRole),role)) throw new ArgumentException("Unknown sample provenance");
            GazeNumbers.Time(captureLocalSeconds,"captureLocalSeconds"); GazeNumbers.Time(sendLocalSeconds,"sendLocalSeconds");
            if(sendLocalSeconds < captureLocalSeconds) throw new ArgumentException("Send must follow capture on the same clock");
            if(source == ControlSource.Simulator && !streamId.StartsWith("simulator/",StringComparison.Ordinal))
                throw new ArgumentException("Simulator stream identity must start with simulator/");
            SessionId=sessionId; StreamId=streamId; DeviceId=deviceId; ClockId=clockId; BlockIndex=blockIndex; Segment=segment;
            Sequence=sequence; CaptureLocalSeconds=captureLocalSeconds; SendLocalSeconds=sendLocalSeconds;
            Source=source; Role=role; RawHeadRotation=rawHeadRotation; RawEyeDirection=rawEyeDirection;
            HeadReportedValid=headReportedValid; EyeReportedValid=eyeReportedValid;
        }
    }

    // Explicit avatar-root-local targets (+Z forward, +Y up) are an engineering geometry fixture.
    // Neither these targets nor the identity map claim apparatus calibration/validation.
    public sealed class AutomaticGazeGeometry
    {
        public AvatarPose ParticipantPose { get; private set; }
        public AvatarPose TaskPose { get; private set; }
        public string Version { get; private set; }
        public AutomaticGazeGeometry(Rotation participantHead,Vec3 participantEye,Rotation taskHead,Vec3 taskEye,string geometryVersion)
        {
            GazeNumbers.Identity(geometryVersion,"geometryVersion");
            if(!participantHead.IsUnit || !taskHead.IsUnit || !participantEye.IsUnit || !taskEye.IsUnit)
                throw new ArgumentException("Automatic target geometry requires finite unit rotations/directions");
            ParticipantPose=new AvatarPose(participantHead,participantEye); TaskPose=new AvatarPose(taskHead,taskEye); Version=geometryVersion;
        }
        internal AvatarPose Pose(GazeTarget target) { return target == GazeTarget.Participant ? ParticipantPose : TaskPose; }
    }

    public sealed class ReferenceGaze
    {
        public AvatarPose? Pose { get; internal set; }
        public ReferencePhase Phase { get; internal set; }
        public GazeTarget? FromTarget { get; internal set; }
        public GazeTarget? ToTarget { get; internal set; }
        public double TransitionFraction { get; internal set; }
        public double ActiveSeconds { get; internal set; }
        public double CycleSeconds { get; internal set; }
        public string GeometryVersion { get; internal set; }
    }

    // No wall clock, response, trial index, or participant measurement enters this schedule.
    public static class AutomaticGaze
    {
        public const double DwellSeconds=3, TurnSeconds=.5, CycleSeconds=7;
        public static ReferenceGaze Evaluate(Condition condition,GazeTarget initialTarget,double activeSeconds,AutomaticGazeGeometry geometry)
        {
            if(geometry == null) throw new ArgumentNullException("geometry");
            if(!Enum.IsDefined(typeof(Condition),condition) || !Enum.IsDefined(typeof(GazeTarget),initialTarget)) throw new ArgumentException("Unknown reference condition/target");
            GazeNumbers.Time(activeSeconds,"activeSeconds");
            var result=new ReferenceGaze { ActiveSeconds=activeSeconds,CycleSeconds=activeSeconds%CycleSeconds,GeometryVersion=geometry.Version };
            if(condition == Condition.D) { result.Phase=ReferencePhase.Hidden; return result; }
            GazeTarget from=condition == Condition.C ? GazeTarget.Task : GazeTarget.Participant, to=from;
            double fraction=0;
            bool transition=false;
            if(condition == Condition.B)
            {
                var other=initialTarget == GazeTarget.Participant ? GazeTarget.Task : GazeTarget.Participant;
                double cycle=result.CycleSeconds;
                if(cycle < DwellSeconds) from=to=initialTarget;
                else if(cycle < DwellSeconds+TurnSeconds)
                { from=initialTarget; to=other; fraction=(cycle-DwellSeconds)/TurnSeconds; transition=true; }
                else if(cycle < 2*DwellSeconds+TurnSeconds) from=to=other;
                else { from=other; to=initialTarget; fraction=(cycle-(2*DwellSeconds+TurnSeconds))/TurnSeconds; transition=true; }
            }
            double smooth=fraction*fraction*(3-2*fraction);
            var a=geometry.Pose(from); var b=geometry.Pose(to);
            result.Pose=transition ? new AvatarPose(Rotation.Slerp(a.HeadRotation,b.HeadRotation,smooth),Vec3.Slerp(a.EyeDirection,b.EyeDirection,smooth)) : a;
            result.FromTarget=from; result.ToTarget=to; result.TransitionFraction=smooth;
            result.Phase=transition ? (to == GazeTarget.Participant ? ReferencePhase.TurnToParticipant : ReferencePhase.TurnToTask)
                : (to == GazeTarget.Participant ? ReferencePhase.ParticipantDwell : ReferencePhase.TaskDwell);
            return result;
        }
    }

    public sealed class AvatarOutput
    {
        // Protocol visibility request only. Tracking/network fault presentation policy is separate.
        public bool AvatarVisible { get; internal set; }
        public ControlSource SelectedSource { get; internal set; }
        public ControlSource? ActualSource { get; internal set; }
        public ReferenceGaze Reference { get; internal set; }
        public AvatarPose? ActualPose { get; internal set; }
        public bool HasActualPose { get { return ActualPose.HasValue; } }
        public bool HeadValid { get; internal set; }
        public bool EyeValid { get; internal set; }
        public HeadEyeSample InputSample { get; internal set; }
        public string MappingVersion { get; internal set; }
        public bool MappingValidatedForApparatus { get { return false; } }
        public GazeOutputStatus Status { get; internal set; }
    }

    // Construct once for each locked block/segment. The transport layer owns freshness,
    // ordering and clock reconciliation; callers must not supply cached samples as new input.
    public sealed class AvatarController
    {
        public const string IdentityMappingVersion="simulation-identity-v1";
        readonly string sessionId,expectedStreamId;
        readonly RunKind runKind;
        readonly int blockIndex,segment;
        readonly ControlSource source;
        readonly Condition condition;
        readonly GazeTarget initialTarget;
        readonly AutomaticGazeGeometry geometry;
        public AvatarController(string sessionId,RunKind runKind,int blockIndex,int segment,ControlSelection selection,
            Condition condition,GazeTarget bInitialTarget,AutomaticGazeGeometry geometry,string expectedStreamId=null)
        {
            GazeNumbers.Identity(sessionId,"sessionId");
            if(!Enum.IsDefined(typeof(RunKind),runKind) || !Enum.IsDefined(typeof(ControlSelection),selection) ||
                !Enum.IsDefined(typeof(Condition),condition) || !Enum.IsDefined(typeof(GazeTarget),bInitialTarget)) throw new ArgumentException("Invalid locked configuration");
            if(geometry == null) throw new ArgumentNullException("geometry");
            if(blockIndex < 0 || blockIndex > 3 || segment < 0) throw new ArgumentOutOfRangeException("blockIdentity");
            if(selection == ControlSelection.SimulatorRehearsal && runKind != RunKind.Simulation)
                throw new InvalidOperationException("Simulator source is restricted to simulation sessions");
            if(selection != ControlSelection.Automatic) GazeNumbers.Identity(expectedStreamId,"expectedStreamId");
            if(selection == ControlSelection.SimulatorRehearsal && !expectedStreamId.StartsWith("simulator/",StringComparison.Ordinal))
                throw new ArgumentException("Explicit simulator stream required");
            this.sessionId=sessionId; this.runKind=runKind; this.blockIndex=blockIndex; this.segment=segment;
            source=selection == ControlSelection.Automatic ? ControlSource.Automatic : selection == ControlSelection.LiveWoz ? ControlSource.LiveOperator : ControlSource.Simulator;
            this.condition=condition; initialTarget=bInitialTarget; this.geometry=geometry; this.expectedStreamId=expectedStreamId;
        }
        public AvatarOutput Render(Phase phase,double activeSeconds,HeadEyeSample sample=null)
        {
            if(!Enum.IsDefined(typeof(Phase),phase)) throw new ArgumentException("Unknown experiment phase");
            var reference=AutomaticGaze.Evaluate(condition,initialTarget,activeSeconds,geometry);
            var output=new AvatarOutput { AvatarVisible=phase == Phase.MainTask && condition != Condition.D,
                SelectedSource=source,Reference=reference,MappingVersion=IdentityMappingVersion };
            if(source == ControlSource.Automatic)
            {
                if(sample != null) throw new InvalidOperationException("Automatic lock rejects external control samples");
                output.ActualSource=ControlSource.Automatic; output.ActualPose=reference.Pose;
                output.HeadValid=output.EyeValid=reference.Pose.HasValue; output.Status=GazeOutputStatus.Automatic;
                return output;
            }
            if(sample == null) { output.Status=GazeOutputStatus.SampleMissing; return output; }
            if(sample.Source != source || sample.Role != StreamRole.OperatorControl || sample.SessionId != sessionId ||
                sample.StreamId != expectedStreamId || sample.BlockIndex != blockIndex || sample.Segment != segment)
                throw new InvalidOperationException("Sample does not match the locked control source and block/segment identity");
            output.InputSample=sample; output.ActualSource=sample.Source;
            if(runKind == RunKind.Apparatus)
            { output.Status=GazeOutputStatus.MappingUnavailable; return output; }
            // Simulation identity mapping preserves raw values, validity, and capture timing.
            output.ActualPose=new AvatarPose(sample.RawHeadRotation,sample.RawEyeDirection);
            output.HeadValid=sample.HeadValid; output.EyeValid=sample.EyeValid; output.Status=GazeOutputStatus.SampleAvailable;
            return output;
        }
        // Production adapters must combine raw tracking validity with receive freshness.
        // The original input flags remain intact for audit; unusable axes cannot become valid.
        public AvatarOutput RenderValidated(Phase phase,double activeSeconds,HeadEyeSample sample,bool headUsable,bool eyeUsable)
        {
            if(source == ControlSource.Automatic) throw new InvalidOperationException("Automatic control does not accept external validated samples");
            if(sample == null && (headUsable || eyeUsable)) throw new ArgumentException("Missing source cannot be usable");
            var output=Render(phase,activeSeconds,sample);
            output.HeadValid=output.HeadValid && headUsable;output.EyeValid=output.EyeValid && eyeUsable;
            return output;
        }
    }

    public interface IHeadEyeSource
    {
        ControlSource Source { get; }
        StreamRole Role { get; }
        string StreamId { get; }
        bool Available { get; }
        string UnavailableReason { get; }
        bool TryRead(out HeadEyeSample sample);
    }

    // SDK binding, calibration, sampling and hardware validation are deliberately unavailable.
    public sealed class UnavailableLiveOperatorSource : IHeadEyeSource
    {
        public ControlSource Source { get { return ControlSource.LiveOperator; } }
        public StreamRole Role { get { return StreamRole.OperatorControl; } }
        public string StreamId { get; private set; }
        public bool Available { get { return false; } }
        public string UnavailableReason { get { return "Real VIVE Pro Eye operator acquisition and coordinate mapping are not implemented or validated"; } }
        public UnavailableLiveOperatorSource(string streamId) { GazeNumbers.Identity(streamId,"streamId"); StreamId=streamId; }
        public bool TryRead(out HeadEyeSample sample) { sample=null; return false; }
    }

    // An independent, deterministic oscillator for transport/presentation rehearsals. Its
    // head and eye rates differ and it does not follow or retime to the automatic reference.
    public sealed class DeterministicSimulatorSource : IHeadEyeSource
    {
        readonly IClock clock;
        readonly string sessionId;
        readonly int blockIndex,segment;
        long sequence;
        double lastTime;
        public ControlSource Source { get { return ControlSource.Simulator; } }
        public StreamRole Role { get { return StreamRole.OperatorControl; } }
        public string StreamId { get; private set; }
        public bool Available { get { return true; } }
        public string UnavailableReason { get { return null; } }
        public DeterministicSimulatorSource(string sessionId,RunKind runKind,int blockIndex,int segment,string streamId,IClock clock)
        {
            GazeNumbers.Identity(sessionId,"sessionId"); GazeNumbers.Identity(streamId,"streamId");
            if(runKind != RunKind.Simulation) throw new InvalidOperationException("Simulator requires a simulation session");
            if(!streamId.StartsWith("simulator/",StringComparison.Ordinal)) throw new ArgumentException("Explicit simulator stream required");
            if(blockIndex < 0 || blockIndex > 3 || segment < 0) throw new ArgumentOutOfRangeException("blockIdentity");
            if(clock == null) throw new ArgumentNullException("clock");
            GazeNumbers.Identity(clock.ClockId,"clockId"); lastTime=clock.Now; GazeNumbers.Time(lastTime,"clock.Now");
            this.sessionId=sessionId; this.blockIndex=blockIndex; this.segment=segment; StreamId=streamId; this.clock=clock;
        }
        public bool TryRead(out HeadEyeSample sample)
        {
            double now=clock.Now; GazeNumbers.Time(now,"clock.Now");
            if(now < lastTime) throw new InvalidOperationException("Simulator clock moved backwards");
            lastTime=now;
            double headYaw=.35*Math.Sin(now*.71),eyeYaw=.22*Math.Sin(now*1.13+.4),eyePitch=.08*Math.Sin(now*.53);
            var head=new Rotation(0,Math.Sin(headYaw/2),0,Math.Cos(headYaw/2));
            var eye=new Vec3(Math.Sin(eyeYaw)*Math.Cos(eyePitch),Math.Sin(eyePitch),Math.Cos(eyeYaw)*Math.Cos(eyePitch));
            sample=new HeadEyeSample(sessionId,StreamId,"simulator-no-hardware",clock.ClockId,blockIndex,segment,
                sequence,now,now,Source,Role,head,eye,true,true);
            sequence=checked(sequence+1); return true;
        }
    }
}
