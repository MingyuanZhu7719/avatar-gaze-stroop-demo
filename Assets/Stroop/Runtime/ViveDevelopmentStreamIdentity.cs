using System;

namespace AvatarStroop.Runtime
{
    public enum ViveDevelopmentOperatorStatus { Present=1, NotApplicable=2 }
    // Immutable declared stream identity only. No frame, observation time,
    // gaze vector, capture-time claim or proof of physical device separation.
    // Values can be selected before any source has been polled.
    public sealed class ViveDevelopmentStreamIdentity
    {
        public string SessionId {get;private set;}
        public string ParticipantId {get;private set;}
        public string OperatorId {get;private set;}
        public ViveDevelopmentOperatorStatus OperatorStatus {get;private set;}
        public ViveDevelopmentMeasurementRole MeasurementRole {get;private set;}
        public string StreamId {get;private set;}
        public string DeviceId {get;private set;}
        public string LocalObservationClockId {get;private set;}
        public string MappingVersion {get;private set;}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool MappingValidatedForApparatus {get{return false;}}
        public ViveDevelopmentStreamIdentity(string sessionId,string participantId,string operatorId,
            ViveDevelopmentMeasurementRole role,string streamId,string deviceId,string clockId,string mappingVersion,ViveDevelopmentOperatorStatus operatorStatus)
        {
            RequireId(sessionId,"sessionId");RequireId(participantId,"participantId");
            RequireId(streamId,"streamId");RequireId(deviceId,"deviceId");RequireId(clockId,"clockId");RequireId(mappingVersion,"mappingVersion");
            if(role!=ViveDevelopmentMeasurementRole.Participant&&role!=ViveDevelopmentMeasurementRole.Operator)throw new ArgumentOutOfRangeException("role");
            if(operatorStatus==ViveDevelopmentOperatorStatus.Present)
            {
                RequireId(operatorId,"operatorId");
                if(String.Equals(participantId,operatorId,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Participant and operator identities must be distinct");
            }
            else if(operatorStatus==ViveDevelopmentOperatorStatus.NotApplicable)
            {
                if(operatorId!=null||role!=ViveDevelopmentMeasurementRole.Participant)
                    throw new ArgumentException("An absent operator is only valid for a participant stream and requires a null operator ID");
            }
            else throw new ArgumentOutOfRangeException("operatorStatus");
            SessionId=sessionId;ParticipantId=participantId;OperatorId=operatorId;OperatorStatus=operatorStatus;MeasurementRole=role;
            StreamId=streamId;DeviceId=deviceId;LocalObservationClockId=clockId;MappingVersion=mappingVersion;
        }
        // Compatibility adapter copies metadata only. It never converts the
        // supplied observation's timestamps into identity or a new sample.
        internal static ViveDevelopmentStreamIdentity FromObservation(ViveDevelopmentProvenance observation)
        {
            if(observation==null)throw new ArgumentNullException("observation");
            return new ViveDevelopmentStreamIdentity(observation.SessionId,observation.ParticipantId,observation.OperatorId,
                observation.MeasurementRole,observation.StreamId,observation.DeviceId,observation.LocalObservationClockId,observation.MappingVersion,observation.OperatorStatus);
        }
        static void RequireId(string value,string name)
        {
            if(String.IsNullOrWhiteSpace(value)||value.Length>256||value!=value.Trim())throw new ArgumentException("Explicit canonical identity required",name);
            foreach(char c in value)if(Char.IsControl(c))throw new ArgumentException("Control characters are not allowed",name);
        }
    }
}
