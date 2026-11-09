using System;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentProvenanceTests
    {
        static ViveDevelopmentProvenance Make(string participant="participant-1", string op="operator-1",
            ViveDevelopmentMeasurementRole role=ViveDevelopmentMeasurementRole.Participant, string mapping="mapping-v1",
            int? native=Int32.MinValue, double start=10, double end=11, double? head=9.5, double? eye=10.5,
            bool unverified=true, string session="session-1", string stream="stream-1", string device="device-1", string clock="clock-1")
        {return new ViveDevelopmentProvenance(session,participant,op,role,stream,device,clock,mapping,native,start,end,head,eye,unverified);}

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE development provenance retains explicit identities and opaque timing",()=>{
                var p=Make();
                Check.Equal("participant-1",p.MeasurementPersonId); Check.Equal("operator-1",p.OperatorId);
                Check.Equal("mapping-v1",p.MappingVersion); Check.Equal("clock-1",p.LocalObservationClockId);
                Check.Equal((int?)Int32.MinValue,p.NativeEyeTimestampOpaque);
                Check.Near(9.5,p.HeadObservedLocalSeconds.Value); Check.Near(10.5,p.EyeObservedLocalSeconds.Value);
                Check.Near(10,p.PollStartedLocalSeconds); Check.Near(11,p.PollCompletedLocalSeconds);
                Check.True(p.HardwareCaptureTimeUnverified && !p.MappingValidatedForApparatus);
                var o=Make(role:ViveDevelopmentMeasurementRole.Operator,native:Int32.MaxValue);
                Check.Equal("operator-1",o.MeasurementPersonId); Check.Equal((int?)Int32.MaxValue,o.NativeEyeTimestampOpaque);
            });
            suite.Run("VIVE development provenance permits missing channels only with matching eye timestamp",()=>{
                Check.Equal(null,Make(native:null,eye:null).NativeEyeTimestampOpaque);
                Check.Equal(null,Make(head:null).HeadObservedLocalSeconds);
                Check.Throws<ArgumentException>(()=>Make(head:null,eye:null,native:null));
                Check.Throws<ArgumentException>(()=>Make(native:null));
                Check.Throws<ArgumentException>(()=>Make(eye:null));
            });
            suite.Run("VIVE development provenance rejects ambiguous and malformed identities",()=>{
                foreach(var bad in new[]{null,""," "," participant-1","participant-1 ","a\nb",new string('a',257)})
                {
                    Check.Throws<ArgumentException>(()=>Make(participant:bad));
                    Check.Throws<ArgumentException>(()=>Make(op:bad));
                    Check.Throws<ArgumentException>(()=>Make(session:bad));
                    Check.Throws<ArgumentException>(()=>Make(stream:bad));
                    Check.Throws<ArgumentException>(()=>Make(device:bad));
                    Check.Throws<ArgumentException>(()=>Make(clock:bad));
                    Check.Throws<ArgumentException>(()=>Make(mapping:bad));
                }
                Check.Throws<ArgumentException>(()=>Make(participant:"Same",op:"same"));
                Check.Throws<ArgumentOutOfRangeException>(()=>Make(role:(ViveDevelopmentMeasurementRole)0));
                Check.Throws<ArgumentOutOfRangeException>(()=>Make(role:(ViveDevelopmentMeasurementRole)99));
                Check.Throws<ArgumentException>(()=>Make(unverified:false));
            });
            suite.Run("VIVE development provenance rejects invalid local timing without interpreting native time",()=>{
                foreach(double bad in new[]{-1,Double.NaN,Double.NegativeInfinity,Double.PositiveInfinity})
                {
                    Check.Throws<ArgumentOutOfRangeException>(()=>Make(start:bad));
                    Check.Throws<ArgumentOutOfRangeException>(()=>Make(end:bad));
                    Check.Throws<ArgumentOutOfRangeException>(()=>Make(head:bad));
                    Check.Throws<ArgumentOutOfRangeException>(()=>Make(eye:bad));
                }
                Check.Throws<ArgumentException>(()=>Make(start:12));
                Check.Throws<ArgumentOutOfRangeException>(()=>Make(head:11.01));
                Check.Throws<ArgumentOutOfRangeException>(()=>Make(eye:11.01));
                Check.Equal((int?)0,Make(native:0).NativeEyeTimestampOpaque);
            });
        }
    }
}
