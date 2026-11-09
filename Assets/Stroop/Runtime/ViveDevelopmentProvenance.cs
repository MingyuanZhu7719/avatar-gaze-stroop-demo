using System;

namespace AvatarStroop.Runtime
{
    // An in-memory development contract only. This does not acquire, persist,
    // export or validate measurements for apparatus use.
    public enum ViveDevelopmentMeasurementRole { Participant = 1, Operator = 2 }

    public sealed class ViveDevelopmentProvenance
    {
        public string SessionId { get; private set; }
        public string ParticipantId { get; private set; }
        public string OperatorId { get; private set; }
        public ViveDevelopmentOperatorStatus OperatorStatus {get;private set;}
        // Prevents a v2 observation from silently entering a v1 writer.
        public bool IsVersionTwoIdentity {get;private set;}
        public ViveDevelopmentMeasurementRole MeasurementRole { get; private set; }
        public string MeasurementPersonId { get { return MeasurementRole == ViveDevelopmentMeasurementRole.Participant ? ParticipantId : OperatorId; } }
        public string StreamId { get; private set; }
        public string DeviceId { get; private set; }
        public string LocalObservationClockId { get; private set; }
        public string MappingVersion { get; private set; }
        // Signed native value is retained exactly, with no unit, epoch or clock conversion implied.
        public int? NativeEyeTimestampOpaque { get; private set; }
        public double PollStartedLocalSeconds { get; private set; }
        public double PollCompletedLocalSeconds { get; private set; }
        public double? HeadObservedLocalSeconds { get; private set; }
        public double? EyeObservedLocalSeconds { get; private set; }
        public bool HardwareCaptureTimeUnverified { get { return true; } }
        public bool MappingValidatedForApparatus { get { return false; } }

        public ViveDevelopmentProvenance(string sessionId, string participantId, string operatorId,
            ViveDevelopmentMeasurementRole measurementRole, string streamId, string deviceId,
            string localObservationClockId, string mappingVersion, int? nativeEyeTimestampOpaque,
            double pollStartedLocalSeconds, double pollCompletedLocalSeconds,
            double? headObservedLocalSeconds, double? eyeObservedLocalSeconds,
            bool hardwareCaptureTimeUnverified)
            :this(new ViveDevelopmentStreamIdentity(sessionId,participantId,operatorId,measurementRole,streamId,deviceId,
                localObservationClockId,mappingVersion,ViveDevelopmentOperatorStatus.Present),nativeEyeTimestampOpaque,
                pollStartedLocalSeconds,pollCompletedLocalSeconds,headObservedLocalSeconds,eyeObservedLocalSeconds,hardwareCaptureTimeUnverified,false)
        {}

        // New versioned paths start with identity only but must supply actual
        // channel observation times before an observation record is created.
        public static ViveDevelopmentProvenance FromObserved(ViveDevelopmentStreamIdentity identity,int? nativeEyeTimestampOpaque,
            double pollStartedLocalSeconds,double pollCompletedLocalSeconds,double? headObservedLocalSeconds,double? eyeObservedLocalSeconds,
            bool hardwareCaptureTimeUnverified)
        {return new ViveDevelopmentProvenance(identity,nativeEyeTimestampOpaque,pollStartedLocalSeconds,pollCompletedLocalSeconds,
            headObservedLocalSeconds,eyeObservedLocalSeconds,hardwareCaptureTimeUnverified,true);}

        ViveDevelopmentProvenance(ViveDevelopmentStreamIdentity identity,int? nativeEyeTimestampOpaque,
            double pollStartedLocalSeconds,double pollCompletedLocalSeconds,double? headObservedLocalSeconds,double? eyeObservedLocalSeconds,
            bool hardwareCaptureTimeUnverified,bool versionTwoIdentity)
        {
            if(identity==null)throw new ArgumentNullException("identity");
            if (!hardwareCaptureTimeUnverified) throw new ArgumentException("Hardware capture time must be explicitly unverified", "hardwareCaptureTimeUnverified");
            RequireTime(pollStartedLocalSeconds, "pollStartedLocalSeconds");
            RequireTime(pollCompletedLocalSeconds, "pollCompletedLocalSeconds");
            if (pollStartedLocalSeconds > pollCompletedLocalSeconds) throw new ArgumentException("Poll completion precedes poll start");
            if (!headObservedLocalSeconds.HasValue && !eyeObservedLocalSeconds.HasValue)
                throw new ArgumentException("At least one channel observation is required");
            if (eyeObservedLocalSeconds.HasValue != nativeEyeTimestampOpaque.HasValue)
                throw new ArgumentException("Eye observation and opaque native timestamp must be supplied together");
            if (headObservedLocalSeconds.HasValue) RequireObservation(headObservedLocalSeconds.Value, pollCompletedLocalSeconds, "headObservedLocalSeconds");
            if (eyeObservedLocalSeconds.HasValue) RequireObservation(eyeObservedLocalSeconds.Value, pollCompletedLocalSeconds, "eyeObservedLocalSeconds");
            SessionId=identity.SessionId; ParticipantId=identity.ParticipantId; OperatorId=identity.OperatorId;OperatorStatus=identity.OperatorStatus;
            IsVersionTwoIdentity=versionTwoIdentity;
            MeasurementRole=identity.MeasurementRole; StreamId=identity.StreamId; DeviceId=identity.DeviceId;
            LocalObservationClockId=identity.LocalObservationClockId; MappingVersion=identity.MappingVersion;
            NativeEyeTimestampOpaque=nativeEyeTimestampOpaque; PollStartedLocalSeconds=pollStartedLocalSeconds;
            PollCompletedLocalSeconds=pollCompletedLocalSeconds; HeadObservedLocalSeconds=headObservedLocalSeconds; EyeObservedLocalSeconds=eyeObservedLocalSeconds;
        }

        static void RequireTime(double value, string name)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0) throw new ArgumentOutOfRangeException(name);
        }
        static void RequireObservation(double value, double completed, string name)
        {
            RequireTime(value, name);
            // Queued observations may precede the poll; they cannot postdate its completion.
            if (value > completed) throw new ArgumentOutOfRangeException(name);
        }
    }
}
