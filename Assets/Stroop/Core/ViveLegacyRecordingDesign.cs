using System;

namespace AvatarStroop.Core
{
    // Explicit software declarations. Neither type authenticates a participant,
    // authorizes storage, opens a source, or constitutes research consent.
    public sealed class ViveLegacyRecordingDesign
    {
        public const string SchemaVersion="vive-legacy-abcd-design-v1";
        public string DesignId {get;private set;}
        public Allocation Allocation {get;private set;}
        public string GeometryVersion {get;private set;}
        public string MappingVersion {get;private set;}
        ViveLegacyRecordingDesign(string id,Allocation allocation,string geometry,string mapping)
        {DesignId=id;Allocation=allocation;GeometryVersion=geometry;MappingVersion=mapping;}
        public static ViveLegacyRecordingDesign Select(Actor actor,string id,Allocation allocation,string geometryVersion,string mappingVersion)
        {
            if(actor!=Actor.Owner)throw new UnauthorizedAccessException("Owner selection is required.");
            if(String.IsNullOrWhiteSpace(id)||allocation==null||String.IsNullOrWhiteSpace(geometryVersion)||String.IsNullOrWhiteSpace(mappingVersion))
                throw new ArgumentException("Explicit legacy design, allocation, geometry and mapping are required.");
            var order=allocation.Order;var lists=allocation.ListByBlock;
            if(order[0]!=Condition.B||order[1]!=Condition.C||order[2]!=Condition.A||order[3]!=Condition.D||
                lists[0]!=0||lists[1]!=1||lists[2]!=2||lists[3]!=3||allocation.BInitialTarget!=GazeTarget.Participant)
                throw new ArgumentException("This recording version requires explicit BCAD, lists 0,1,2,3 and B initially Participant.");
            return new ViveLegacyRecordingDesign(id,allocation,geometryVersion,mappingVersion);
        }
    }
    public sealed class ViveLegacyRecordingIntent
    {
        public const string EventSchemaVersion="vive-legacy-recording-intent-v1";
        public string IntentId {get;private set;}
        public bool TaskRequested {get;private set;}
        public bool EyeRequested {get;private set;}
        ViveLegacyRecordingIntent(string id,bool task,bool eye){IntentId=id;TaskRequested=task;EyeRequested=eye;}
        public static ViveLegacyRecordingIntent Select(Actor actor,string id,bool taskRequested,bool eyeRequested)
        {
            if(actor!=Actor.Owner)throw new UnauthorizedAccessException("Owner recording selection is required.");
            if(String.IsNullOrWhiteSpace(id))throw new ArgumentException("Explicit legacy recording intent identity is required.");
            return new ViveLegacyRecordingIntent(id,taskRequested,eyeRequested);
        }
    }
}
