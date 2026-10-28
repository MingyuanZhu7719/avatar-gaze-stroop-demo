using System;
using System.Collections.Generic;

namespace AvatarStroop.Core
{
    public enum ParticipantEyeSource { RealDevice, Simulator }
    public enum ParticipantAoi { Task, Eyes, RemainingFace, Other, Invalid, NotApplicable }

    // A separate measurement type prevents operator control samples becoming outcomes.
    public sealed class ParticipantEyeSample
    {
        public string SessionId { get; private set; }
        public string StreamId { get; private set; }
        public string DeviceId { get; private set; }
        public string ClockId { get; private set; }
        public int BlockIndex { get; private set; }
        public int Segment { get; private set; }
        public long Sequence { get; private set; }
        public double CaptureLocalSeconds { get; private set; }
        public ParticipantEyeSource Source { get; private set; }
        public StreamRole Role { get { return StreamRole.ParticipantMeasurement; } }
        public Vec3 RawEyeDirection { get; private set; }
        public bool EyeReportedValid { get; private set; }
        public bool EyeValid { get { return EyeReportedValid && RawEyeDirection.IsUnit; } }
        public ParticipantEyeSample(string sessionId,string streamId,string deviceId,string clockId,int blockIndex,int segment,
            long sequence,double captureLocalSeconds,ParticipantEyeSource source,StreamRole role,Vec3 rawEyeDirection,bool eyeReportedValid)
        {
            GazeNumbers.Identity(sessionId,"sessionId"); GazeNumbers.Identity(streamId,"streamId");
            GazeNumbers.Identity(deviceId,"deviceId"); GazeNumbers.Identity(clockId,"clockId");
            if(role != StreamRole.ParticipantMeasurement) throw new ArgumentException("Participant measurements cannot use operator-control provenance");
            if(!Enum.IsDefined(typeof(ParticipantEyeSource),source)) throw new ArgumentException("Unknown participant source");
            if(blockIndex < 0 || blockIndex > 3 || segment < 0 || sequence < 0) throw new ArgumentOutOfRangeException("sampleIdentity");
            GazeNumbers.Time(captureLocalSeconds,"captureLocalSeconds");
            if(source == ParticipantEyeSource.Simulator && !streamId.StartsWith("simulator/",StringComparison.Ordinal))
                throw new ArgumentException("Simulator stream identity must start with simulator/");
            SessionId=sessionId; StreamId=streamId; DeviceId=deviceId; ClockId=clockId; BlockIndex=blockIndex; Segment=segment;
            Sequence=sequence; CaptureLocalSeconds=captureLocalSeconds; Source=source; RawEyeDirection=rawEyeDirection; EyeReportedValid=eyeReportedValid;
        }
    }

    // Caller supplies measured phase windows in one named reference clock. This class
    // performs no synchronization, calibration, gap filling, or AOI geometry inference.
    public sealed class GazeObservationWindow
    {
        public int BlockIndex { get; private set; }
        public int Segment { get; private set; }
        public Condition Condition { get; private set; }
        public Phase Phase { get; private set; }
        public double Start { get; private set; }
        public double End { get; private set; }
        public GazeObservationWindow(int blockIndex,int segment,Condition condition,Phase phase,double start,double end)
        {
            if(blockIndex < 0 || blockIndex > 3 || segment < 0 || !Enum.IsDefined(typeof(Condition),condition) || !Enum.IsDefined(typeof(Phase),phase))
                throw new ArgumentException("Invalid observation window context");
            GazeNumbers.Time(start,"start"); GazeNumbers.Time(end,"end");
            if(end <= start) throw new ArgumentException("Window must have positive duration");
            BlockIndex=blockIndex; Segment=segment; Condition=condition; Phase=phase; Start=start; End=end;
        }
    }

    public sealed class ParticipantGazeInterval
    {
        public ParticipantEyeSample Sample { get; private set; }
        public GazeObservationWindow Window { get; private set; }
        public string TrialId { get; private set; }
        public double Start { get; private set; }
        public double End { get; private set; }
        public ParticipantAoi Aoi { get; private set; }
        public string MappingVersion { get; private set; }
        public string CalibrationVersion { get; private set; }
        public ParticipantGazeInterval(ParticipantEyeSample sample,GazeObservationWindow window,string trialId,double start,double end,
            ParticipantAoi aoi,string mappingVersion,string calibrationVersion)
        {
            if(sample == null || window == null) throw new ArgumentNullException("sample/window");
            GazeNumbers.Identity(mappingVersion,"mappingVersion"); GazeNumbers.Identity(calibrationVersion,"calibrationVersion");
            GazeNumbers.Time(start,"start"); GazeNumbers.Time(end,"end");
            if(end <= start || start < window.Start || end > window.End) throw new ArgumentException("Interval must be contained in its measured window");
            if(sample.BlockIndex != window.BlockIndex || sample.Segment != window.Segment) throw new ArgumentException("Sample/window context mismatch");
            if(!Enum.IsDefined(typeof(ParticipantAoi),aoi) || aoi == ParticipantAoi.NotApplicable) throw new ArgumentException("NotApplicable is a structural outcome, not a measured gaze interval");
            if(sample.EyeValid == (aoi == ParticipantAoi.Invalid)) throw new ArgumentException("Invalid tracking must be Invalid; valid tracking cannot be Invalid");
            if(window.Condition == Condition.D && (aoi == ParticipantAoi.Eyes || aoi == ParticipantAoi.RemainingFace))
                throw new ArgumentException("Condition D has no avatar eyes or face AOI");
            Sample=sample; Window=window; TrialId=trialId; Start=start; End=end; Aoi=aoi; MappingVersion=mappingVersion; CalibrationVersion=calibrationVersion;
        }
    }

    public sealed class ParticipantGazeSummary
    {
        public int BlockIndex { get; internal set; }
        public int Segment { get; internal set; }
        public Condition Condition { get; internal set; }
        public double ActiveSeconds { get; internal set; }
        public double ValidSeconds { get; internal set; }
        public double InvalidSeconds { get; internal set; }
        public double UnobservedSeconds { get { return Math.Max(0,ActiveSeconds-ValidSeconds-InvalidSeconds); } }
        public double TaskSeconds { get; internal set; }
        public double OtherSeconds { get; internal set; }
        public double? EyesSeconds { get; internal set; }
        public double? RemainingFaceSeconds { get; internal set; }
        public double? CombinedFaceSeconds { get { return EyesSeconds+RemainingFaceSeconds; } }
        public bool AvatarAoisApplicable { get { return Condition != Condition.D; } }
        public double? ValidCoverage { get { return Ratio(ValidSeconds,ActiveSeconds); } }
        public double? TaskProportion { get { return Ratio(TaskSeconds,ValidSeconds); } }
        public double? EyesProportion { get { return Ratio(EyesSeconds,ValidSeconds); } }
        public double? RemainingFaceProportion { get { return Ratio(RemainingFaceSeconds,ValidSeconds); } }
        public double? CombinedFaceProportion { get { return Ratio(CombinedFaceSeconds,ValidSeconds); } }
        static double? Ratio(double? numerator,double denominator) { return denominator > 0 ? numerator/denominator : null; }
    }

    public sealed class ParticipantGazeLog
    {
        readonly List<ParticipantEyeSample> samples=new List<ParticipantEyeSample>();
        readonly List<GazeObservationWindow> windows=new List<GazeObservationWindow>();
        readonly List<ParticipantGazeInterval> intervals=new List<ParticipantGazeInterval>();
        readonly HashSet<long> sequences=new HashSet<long>();
        string captureClockId;
        public string SessionId { get; private set; }
        public RunKind RunKind { get; private set; }
        public string StreamId { get; private set; }
        public string DeviceId { get; private set; }
        public ParticipantEyeSource Source { get; private set; }
        public string ReferenceClockId { get; private set; }
        public IReadOnlyList<ParticipantEyeSample> Samples { get { return samples.AsReadOnly(); } }
        public IReadOnlyList<GazeObservationWindow> Windows { get { return windows.AsReadOnly(); } }
        public IReadOnlyList<ParticipantGazeInterval> Intervals { get { return intervals.AsReadOnly(); } }
        public ParticipantGazeLog(string sessionId,RunKind runKind,string streamId,string deviceId,ParticipantEyeSource source,string referenceClockId)
        {
            GazeNumbers.Identity(sessionId,"sessionId"); GazeNumbers.Identity(streamId,"streamId"); GazeNumbers.Identity(deviceId,"deviceId");
            GazeNumbers.Identity(referenceClockId,"referenceClockId");
            if(!Enum.IsDefined(typeof(RunKind),runKind) || !Enum.IsDefined(typeof(ParticipantEyeSource),source)) throw new ArgumentException("Unknown provenance");
            if(source == ParticipantEyeSource.Simulator && runKind != RunKind.Simulation) throw new ArgumentException("Simulator cannot enter apparatus data");
            if(source == ParticipantEyeSource.Simulator && !streamId.StartsWith("simulator/",StringComparison.Ordinal)) throw new ArgumentException("Simulator stream prefix required");
            SessionId=sessionId; RunKind=runKind; StreamId=streamId; DeviceId=deviceId; Source=source; ReferenceClockId=referenceClockId;
        }
        public void AddSample(ParticipantEyeSample sample)
        {
            if(sample == null) throw new ArgumentNullException("sample");
            if(sample.SessionId != SessionId || sample.StreamId != StreamId || sample.DeviceId != DeviceId || sample.Source != Source)
                throw new ArgumentException("Participant sample does not match this separate measurement stream");
            if(captureClockId != null && sample.ClockId != captureClockId) throw new ArgumentException("A participant stream cannot silently change source clock");
            if(!sequences.Add(sample.Sequence)) throw new ArgumentException("Duplicate participant sample identity");
            captureClockId=sample.ClockId; samples.Add(sample);
        }
        public void AddWindow(GazeObservationWindow window)
        {
            if(window == null) throw new ArgumentNullException("window");
            foreach(var prior in windows)
            {
                if(window.Start < prior.End && prior.Start < window.End) throw new ArgumentException("Observation windows cannot overlap");
                if(window.BlockIndex == prior.BlockIndex && window.Condition != prior.Condition) throw new ArgumentException("A block cannot change condition");
            }
            windows.Add(window);
        }
        public void AddInterval(ParticipantGazeInterval interval)
        {
            if(interval == null) throw new ArgumentNullException("interval");
            if(!samples.Contains(interval.Sample) || !windows.Contains(interval.Window)) throw new ArgumentException("Register the exact sample and window before classifying an interval");
            foreach(var prior in intervals)
                if(interval.Start < prior.End && prior.Start < interval.End) throw new ArgumentException("Exclusive AOI intervals cannot overlap");
            intervals.Add(interval);
        }
        public IReadOnlyList<ParticipantGazeSummary> Summaries()
        {
            var result=new List<ParticipantGazeSummary>();
            foreach(var window in windows)
            {
                if(window.Phase != Phase.MainTask) continue;
                var summary=result.Find(x=>x.BlockIndex == window.BlockIndex && x.Segment == window.Segment);
                if(summary == null)
                {
                    summary=new ParticipantGazeSummary { BlockIndex=window.BlockIndex,Segment=window.Segment,Condition=window.Condition,
                        EyesSeconds=window.Condition == Condition.D ? (double?)null : 0,RemainingFaceSeconds=window.Condition == Condition.D ? (double?)null : 0 };
                    result.Add(summary);
                }
                summary.ActiveSeconds+=window.End-window.Start;
                foreach(var interval in intervals)
                {
                    if(interval.Window != window) continue;
                    double seconds=interval.End-interval.Start;
                    if(interval.Aoi == ParticipantAoi.Invalid) { summary.InvalidSeconds+=seconds; continue; }
                    summary.ValidSeconds+=seconds;
                    switch(interval.Aoi)
                    {
                        case ParticipantAoi.Task: summary.TaskSeconds+=seconds; break;
                        case ParticipantAoi.Eyes: summary.EyesSeconds+=seconds; break;
                        case ParticipantAoi.RemainingFace: summary.RemainingFaceSeconds+=seconds; break;
                        case ParticipantAoi.Other: summary.OtherSeconds+=seconds; break;
                    }
                }
            }
            return result.AsReadOnly();
        }
    }
}
