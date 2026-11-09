using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Non-sensitive status only; no provider exception text, raw gaze or IDs.
    public enum ViveParticipantReadStatus { NotRead, Accepted, Duplicate, Missing, Stale, OutOfOrder, InvalidTimeOrSequence, ProviderFailure, ClockFailure, MappingFailure, RuntimeUnavailable, CalibrationUnavailable, DeviceUnavailable, NativeFailure, EyeDataUnavailable, HeadUnavailable, CombinedRayUnavailable, EyeDirectionInvalid, UnknownFailure, ReadAttemptGenerationExhausted }
    // Companion to the core sample: retains the unmodified per-eye SDK observations.
    // The native frame timestamp is opaque; the core sample time is the local eye
    // observation time, not a verified hardware capture time.
    public sealed class ViveParticipantEyeObservation
    {
        public ParticipantEyeSample Sample { get; internal set; }
        public ViveProEyePoll RawPoll { get; internal set; }
        public ViveFrameDisposition EyeDisposition { get; internal set; }
        public double PollStartedLocalSeconds { get; internal set; }
        public double PollCompletedLocalSeconds { get; internal set; }
        public ViveTimestampBasis TimestampBasis { get { return ViveTimestampBasis.LocalPollObservation; } }
        public bool HardwareCaptureTimeVerified { get { return false; } }
        public bool MappingValidatedForApparatus { get { return false; } }
        public string EyeFailureReason { get; internal set; }
        // Native direction report is independent of later runtime, calibration,
        // head, mapping and freshness gates applied by the adapter.
        public bool NativeDirectionReportedValid { get; internal set; }
        public ViveParticipantReadStatus UsabilityStatus {get;internal set;}
    }

    // In-memory development adapter. The caller must supply a separate participant
    // provider and explicit device-instance identities. The declarations cannot by
    // themselves prove physical separation. This does not start a device, perform
    // calibration, classify AOIs, or bypass the apparatus gate.
    public sealed class ViveParticipantEyeSource
    {
        readonly string sessionId, clockId, derivedCoordinateSpace,mappingVersion;
        readonly int blockIndex, segment;
        readonly ViveProEyeHeadEyeSource observationSource;
        readonly IClock sourceClock;
        readonly IViveProEyeCoordinateMapper sourceMapper;
        long sequence;
        long readAttemptGeneration;
        public string StreamId { get; private set; }
        public string DeviceId { get; private set; }
        public ViveDevelopmentOperatorStatus OperatorStatus {get;private set;}
        public string SessionId { get { return sessionId; } }
        public string ClockId { get { return clockId; } }
        public string MappingVersion { get { return mappingVersion; } }
        public int BlockIndex { get { return blockIndex; } }
        public int Segment { get { return segment; } }
        public bool UsesClock(IClock candidate) { return Object.ReferenceEquals(sourceClock,candidate); }
        public ViveParticipantReadStatus LastReadStatus {get;private set;}
        // This counter identifies attempted reads, not eye frames or a data-loss
        // duration. Callers must serialize TryRead and consumption on one owner
        // thread; atomicity of this diagnostic counter does not make polling safe
        // concurrently. MaxValue is a permanent fail-closed exhaustion sentinel.
        public long ReadAttemptGeneration {get {return System.Threading.Interlocked.Read(ref readAttemptGeneration);}}
        public bool ReadAttemptGenerationExhausted {get {return ReadAttemptGeneration==Int64.MaxValue;}}
        public ViveParticipantEyeObservation LastObservation { get; private set; }
        public bool MappingValidatedForApparatus { get { return false; } }

        public ViveParticipantEyeSource(string sessionId, int blockIndex, int segment,
            string participantStreamId, string participantDeviceInstanceId,
            string operatorStreamId, string operatorDeviceInstanceId,
            IClock clock, IViveProEyeProvider participantProvider, IViveProEyeCoordinateMapper mapper,
            double maximumObservationAgeSeconds=.1,string derivedDirectionCoordinateSpace=null)
            :this(sessionId,blockIndex,segment,participantStreamId,participantDeviceInstanceId,operatorStreamId,operatorDeviceInstanceId,
                clock,participantProvider,mapper,maximumObservationAgeSeconds,derivedDirectionCoordinateSpace,ViveDevelopmentOperatorStatus.Present)
        {}

        // An automatic participant session can explicitly have no operator.
        // This omits, rather than invents, the other person's stream/device.
        public static ViveParticipantEyeSource CreateForAutomatic(string sessionId,int blockIndex,int segment,
            string participantStreamId,string participantDeviceInstanceId,IClock clock,IViveProEyeProvider participantProvider,
            IViveProEyeCoordinateMapper mapper,double maximumObservationAgeSeconds=.1,string derivedDirectionCoordinateSpace=null)
        {return new ViveParticipantEyeSource(sessionId,blockIndex,segment,participantStreamId,participantDeviceInstanceId,null,null,
            clock,participantProvider,mapper,maximumObservationAgeSeconds,derivedDirectionCoordinateSpace,ViveDevelopmentOperatorStatus.NotApplicable);}

        ViveParticipantEyeSource(string sessionId,int blockIndex,int segment,string participantStreamId,string participantDeviceInstanceId,
            string operatorStreamId,string operatorDeviceInstanceId,IClock clock,IViveProEyeProvider participantProvider,
            IViveProEyeCoordinateMapper mapper,double maximumObservationAgeSeconds,string derivedDirectionCoordinateSpace,ViveDevelopmentOperatorStatus operatorStatus)
        {
            Identity(sessionId,"sessionId"); Identity(participantStreamId,"participantStreamId");
            Identity(participantDeviceInstanceId,"participantDeviceInstanceId");
            if(operatorStatus==ViveDevelopmentOperatorStatus.Present)
            {
                Identity(operatorStreamId,"operatorStreamId"); Identity(operatorDeviceInstanceId,"operatorDeviceInstanceId");
                if (participantStreamId==operatorStreamId || participantDeviceInstanceId==operatorDeviceInstanceId)
                    throw new ArgumentException("Participant and operator stream and device-instance identities must be distinct");
            }
            else if(operatorStatus!=ViveDevelopmentOperatorStatus.NotApplicable||operatorStreamId!=null||operatorDeviceInstanceId!=null)
                throw new ArgumentException("An automatic participant source cannot invent an operator stream or device");
            if (participantStreamId.StartsWith("simulator/",StringComparison.Ordinal) ||
                participantStreamId.StartsWith("vive-pro-eye/operator/",StringComparison.Ordinal))
                throw new ArgumentException("Participant stream must identify a separate device stream", "participantStreamId");
            if (clock==null) throw new ArgumentNullException("clock");
            Identity(clock.ClockId,"clock.ClockId");
            if (mapper==null) throw new ArgumentNullException("mapper");
            if (derivedDirectionCoordinateSpace==null && mapper is ViveProEyeRehearsalCoordinates)
                derivedDirectionCoordinateSpace="unity-tracking-local-rehearsal-unvalidated";
            Identity(derivedDirectionCoordinateSpace,"derivedDirectionCoordinateSpace");

            this.sessionId=sessionId; this.blockIndex=blockIndex; this.segment=segment; derivedCoordinateSpace=derivedDirectionCoordinateSpace;
            StreamId=participantStreamId; DeviceId=participantDeviceInstanceId; clockId=clock.ClockId;OperatorStatus=operatorStatus;
            sourceClock=clock;sourceMapper=mapper;mappingVersion=mapper.Version;Identity(mappingVersion,"mapper.Version");
            // The existing observer is private and solely consumes the supplied participant
            // provider. Its operator-labelled diagnostic sample is never returned here.
            observationSource=new ViveProEyeHeadEyeSource(sessionId,blockIndex,segment,
                "vive-pro-eye/operator/participant-observer/"+participantStreamId,clock,participantProvider,mapper,maximumObservationAgeSeconds);
        }

        public bool TryRead(out ViveParticipantEyeObservation participantObservation)
        {
            participantObservation=null; LastObservation=null;
            while(true)
            {
                long current=System.Threading.Interlocked.Read(ref readAttemptGeneration);
                if(current==Int64.MaxValue)
                {LastReadStatus=ViveParticipantReadStatus.ReadAttemptGenerationExhausted;return false;}
                long next=current==Int64.MaxValue-1?Int64.MaxValue:current+1;
                if(System.Threading.Interlocked.CompareExchange(ref readAttemptGeneration,next,current)!=current)continue;
                if(next==Int64.MaxValue)
                {LastReadStatus=ViveParticipantReadStatus.ReadAttemptGenerationExhausted;return false;}
                break;
            }
            try { if(sourceMapper.Version!=mappingVersion){LastReadStatus=ViveParticipantReadStatus.MappingFailure;return false;} }
            catch(Exception){LastReadStatus=ViveParticipantReadStatus.MappingFailure;return false;}
            HeadEyeSample ignored;
            bool read;
            try{read=observationSource.TryRead(out ignored);}
            catch(Exception){LastReadStatus=ViveParticipantReadStatus.UnknownFailure;return false;}
            var observation=observationSource.LastObservation;
            if(observation==null){LastReadStatus=ViveParticipantReadStatus.UnknownFailure;return false;}
            string failure=observation.FailureReason;
            if(failure!=null&&failure.StartsWith("Local observation clock failed",StringComparison.Ordinal))
            {LastReadStatus=ViveParticipantReadStatus.ClockFailure;return false;}
            if(failure!=null&&(failure.StartsWith("Provider failure:",StringComparison.Ordinal)||failure=="Provider returned no observation"))
            {LastReadStatus=ViveParticipantReadStatus.ProviderFailure;return false;}
            if(failure!=null&&failure.StartsWith("Coordinate mapper failure:",StringComparison.Ordinal))
            {LastReadStatus=ViveParticipantReadStatus.MappingFailure;return false;}
            var poll=observation.RawPoll;
            // A head-only update or rejected/stale/duplicate eye frame is not a new
            // participant eye observation and cannot become a new measurement.
            if (poll==null){LastReadStatus=ViveParticipantReadStatus.UnknownFailure;return false;}
            if(poll.Eye==null||observation.EyeDisposition!=ViveFrameDisposition.Accepted)
            {
                if(poll.Runtime!=ViveEyeRuntimeState.Working){LastReadStatus=ViveParticipantReadStatus.RuntimeUnavailable;return false;}
                if(poll.Calibration!=ViveEyeCalibrationState.ReportedReady){LastReadStatus=ViveParticipantReadStatus.CalibrationUnavailable;return false;}
                if(!poll.EyeDeviceConnected){LastReadStatus=ViveParticipantReadStatus.DeviceUnavailable;return false;}
                if(poll.NativeResultCode.HasValue&&poll.NativeResultCode.Value!=0){LastReadStatus=ViveParticipantReadStatus.NativeFailure;return false;}
                if(poll.Eye!=null&&poll.Eye.NoUser){LastReadStatus=ViveParticipantReadStatus.EyeDataUnavailable;return false;}
            }
            if (poll.Eye==null){LastReadStatus=ViveParticipantReadStatus.Missing;return false;}
            if (observation.EyeDisposition!=ViveFrameDisposition.Accepted)
            {LastReadStatus=Status(observation.EyeDisposition);return false;}
            if(!read||ignored==null){LastReadStatus=ViveParticipantReadStatus.UnknownFailure;return false;}
            var eye=poll.Eye;
            Vec3 raw=eye.Combined==null ? new Vec3(Double.NaN,Double.NaN,Double.NaN) : eye.Combined.Direction;
            // Keep native raw XYZ unchanged. A fully gated converted ray is stored
            // separately with its mapper and declared frame; legacy EyeValid still
            // describes the raw field and keeps existing exports unchanged.
            ParticipantEyeSample sample;
            if(observation.EyeUsable)
                sample=new ParticipantEyeSample(sessionId,StreamId,DeviceId,clockId,blockIndex,segment,
                    sequence,eye.ObservedLocalSeconds,ParticipantEyeSource.RealDevice,StreamRole.ParticipantMeasurement,raw,true,
                    ignored.RawEyeDirection,observation.MappingVersion,derivedCoordinateSpace);
            else
                sample=new ParticipantEyeSample(sessionId,StreamId,DeviceId,clockId,blockIndex,segment,
                    sequence,eye.ObservedLocalSeconds,ParticipantEyeSource.RealDevice,StreamRole.ParticipantMeasurement,raw,false);
            sequence=checked(sequence+1);
            participantObservation=new ViveParticipantEyeObservation {
                Sample=sample, RawPoll=poll, EyeDisposition=observation.EyeDisposition,
                PollStartedLocalSeconds=observation.PollStartedLocalSeconds, PollCompletedLocalSeconds=observation.PollCompletedLocalSeconds,
                EyeFailureReason=observation.EyeFailureReason,
                NativeDirectionReportedValid=eye.Combined!=null && (eye.Combined.ValidityMask&2UL)!=0 && eye.Combined.DirectionReportedValid,
                UsabilityStatus=Usability(poll,observation)
            };
            LastObservation=participantObservation;
            LastReadStatus=ViveParticipantReadStatus.Accepted;
            return true;
        }
        static ViveParticipantReadStatus Usability(ViveProEyePoll poll,ViveProEyeObservation observation)
        {
            if(poll.Runtime!=ViveEyeRuntimeState.Working)return ViveParticipantReadStatus.RuntimeUnavailable;
            if(poll.Calibration!=ViveEyeCalibrationState.ReportedReady)return ViveParticipantReadStatus.CalibrationUnavailable;
            if(!poll.EyeDeviceConnected)return ViveParticipantReadStatus.DeviceUnavailable;
            if(poll.NativeResultCode.HasValue&&poll.NativeResultCode.Value!=0)return ViveParticipantReadStatus.NativeFailure;
            if(poll.Eye.NoUser)return ViveParticipantReadStatus.EyeDataUnavailable;
            if(poll.Eye.Combined==null)return ViveParticipantReadStatus.CombinedRayUnavailable;
            Vec3 derived;
            if((poll.Eye.Combined.ValidityMask&2UL)==0||!poll.Eye.Combined.DirectionReportedValid||
                !ViveProEyeSdkDirection.TryGetUnitDirection(poll.Eye.Combined.Direction,out derived))return ViveParticipantReadStatus.EyeDirectionInvalid;
            if(!observation.HeadUsable)return ViveParticipantReadStatus.HeadUnavailable;
            return observation.EyeUsable?ViveParticipantReadStatus.Accepted:ViveParticipantReadStatus.MappingFailure;
        }
        static ViveParticipantReadStatus Status(ViveFrameDisposition disposition)
        {
            switch(disposition)
            {
                case ViveFrameDisposition.Duplicate:return ViveParticipantReadStatus.Duplicate;
                case ViveFrameDisposition.Missing:return ViveParticipantReadStatus.Missing;
                case ViveFrameDisposition.Stale:return ViveParticipantReadStatus.Stale;
                case ViveFrameDisposition.OutOfOrder:return ViveParticipantReadStatus.OutOfOrder;
                case ViveFrameDisposition.InvalidObservationTime:
                case ViveFrameDisposition.InvalidSequence:return ViveParticipantReadStatus.InvalidTimeOrSequence;
                default:return ViveParticipantReadStatus.UnknownFailure;
            }
        }
        static void Identity(string value,string name)
        { if (String.IsNullOrWhiteSpace(value)) throw new ArgumentException("Explicit identity required",name); }
    }
}
