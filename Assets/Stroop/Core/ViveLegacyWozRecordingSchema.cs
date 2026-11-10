using System;
using System.Collections.Generic;
using System.Globalization;

namespace AvatarStroop.Core
{
    // Bounded historical values, not a wire protocol or an admission token.
    // Only the process-owned recording join can issue actual evidence. Passing
    // this validator proves neither capture, hardware identity nor authority.
    internal static class ViveLegacyWozRecordingSchema
    {
        internal const string EventKind="woz_network_evidence";
        internal const string SchemaVersion="vive-woz-local-evidence-v1";
        internal const string NotObserved="NOT_OBSERVED";
        internal const string NotApplicable="NOT_APPLICABLE";
        internal const int EvidenceFieldCount=33;
        const double UnitTolerance=.00001;

        static readonly HashSet<string> EvidenceFields=new HashSet<string>(StringComparer.Ordinal)
        {
            "woz_schema","woz_event","woz_epoch","woz_source_binding","woz_mapping",
            "woz_participant_clock_id","woz_operator_clock_id","woz_task_revision","woz_task_packet_sequence",
            "woz_choice_generation","woz_source_generation","woz_remote_sequence","woz_receipt_origin",
            "woz_control_source","woz_authentication","woz_hardware_identity","woz_clock_relation","woz_synthetic",
            "woz_task_issued_participant_time","woz_challenge_issued_participant_time","woz_receipt_participant_time",
            "woz_send_operator_time","woz_head_operator_time","woz_eye_operator_time","woz_epoch_sample_sequence",
            "woz_head_qx","woz_head_qy","woz_head_qz","woz_head_qw","woz_eye_dx","woz_eye_dy","woz_eye_dz",
            "woz_failure_reason"
        };
        // Experiment.Log adds exactly these context keys. Their required
        // values and manifest binding remain the codec/reader's responsibility.
        // No arbitrary legacy_* key, payload, diagnostic text or packet is allowed.
        static readonly HashSet<string> LegacyContextFields=new HashSet<string>(StringComparer.Ordinal)
        {
            "legacy_schema","legacy_design_id","legacy_design_schema","legacy_control_mode","legacy_recording_intent_id",
            "legacy_task_recording_intent","legacy_eye_recording_intent","legacy_eye_sample_payload","legacy_phase",
            "legacy_allocation_id","legacy_b_initial_target","legacy_active_seconds","legacy_condition",
            "legacy_list_index","legacy_block_started"
        };
        static readonly string[] TimeFields={"woz_task_issued_participant_time","woz_challenge_issued_participant_time",
            "woz_receipt_participant_time","woz_send_operator_time","woz_head_operator_time","woz_eye_operator_time"};
        static readonly string[] PoseFields={"woz_head_qx","woz_head_qy","woz_head_qz","woz_head_qw",
            "woz_eye_dx","woz_eye_dy","woz_eye_dz"};

        // Uppercase 16-digit IEEE-754 bit encodings preserve the original
        // double, including signed zero. They are not rounded display times.
        internal static string EncodeNumber(double value)
        {
            if(!Finite(value))throw Invalid("Nonfinite numeric value.");
            return unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("X16",CultureInfo.InvariantCulture);
        }
        internal static string EncodeTime(double value)
        {
            if(!Finite(value)||value<0)throw Invalid("Invalid local time.");
            return EncodeNumber(value);
        }

        internal static void Validate(string kind,IReadOnlyDictionary<string,string> fields,
            string participantClockId,double appendTime,bool wozMode)
        {
            if(fields==null)throw Invalid("Missing event fields.");
            bool evidence=String.Equals(kind,EventKind,StringComparison.Ordinal);
            if(fields.Count>128||(evidence&&(fields.Count<EvidenceFieldCount||fields.Count>EvidenceFieldCount+LegacyContextFields.Count)))
                throw Invalid("Evidence field count.");
            foreach(var pair in fields)
            {
                if(pair.Key==null)throw Invalid("Missing field name.");
                if(pair.Key.StartsWith("woz_",StringComparison.OrdinalIgnoreCase)&&
                    (!evidence||!EvidenceFields.Contains(pair.Key)))throw Invalid("Reserved WoZ field.");
            }
            // Old VRAW2-4 events without reserved fields retain their layout
            // and validation. An empty evidence declaration is never ordinary.
            if(!evidence)return;
            if(!wozMode)throw Invalid("WoZ evidence requires the explicit V5 mode.");
            foreach(var pair in fields)
                if((!EvidenceFields.Contains(pair.Key)&&!LegacyContextFields.Contains(pair.Key))||pair.Value==null)
                    throw Invalid("Unexpected evidence field.");
            foreach(string key in EvidenceFields)if(!fields.ContainsKey(key))throw Invalid("Incomplete evidence fields.");
            Exact(fields,"woz_schema",SchemaVersion);
            Exact(fields,"woz_hardware_identity","NOT_VERIFIED");
            Exact(fields,"woz_clock_relation","INDEPENDENT_NOT_SYNCHRONIZED");
            Identity(participantClockId);
            Identity(fields["woz_epoch"]);Identity(fields["woz_source_binding"]);Identity(fields["woz_mapping"]);
            Identity(fields["woz_participant_clock_id"]);Identity(fields["woz_operator_clock_id"]);
            if(!String.Equals(fields["woz_participant_clock_id"],participantClockId,StringComparison.Ordinal))
                throw Invalid("Participant clock does not match the enclosing event.");
            if(!Finite(appendTime)||appendTime<0)throw Invalid("Invalid enclosing append time.");
            string eventName=fields["woz_event"];
            bool local=eventName=="LocalRevoke"||eventName=="LocalFailure";
            bool stop=eventName=="SafetyStopRequest";
            bool receipt=eventName=="TaskReceipt";
            bool preflight=eventName=="Preflight";
            // PoseUsed means an owned software presentation operation used
            // these values. It does not assert photon onset or user perception.
            bool pose=eventName=="Pose"||eventName=="PoseUsed";
            if(!local&&!stop&&!receipt&&!preflight&&!pose)throw Invalid("Unknown evidence event.");
            string source=fields["woz_control_source"];
            if(source!="LiveOperator"&&source!="Simulator")throw Invalid("Unknown control source.");
            if(local)
            {
                Exact(fields,"woz_receipt_origin","LOCAL_ONLY");Exact(fields,"woz_authentication","NONE");
                Exact(fields,"woz_synthetic",NotApplicable);
            }
            else
            {
                string origin=fields["woz_receipt_origin"];
                if(origin!="SOCKET"&&origin!="MEMORY")throw Invalid("Unknown network receipt origin.");
                if(origin=="MEMORY"&&source!="Simulator")throw Invalid("Memory evidence must remain synthetic.");
                Exact(fields,"woz_synthetic",source=="Simulator"?"TRUE":"FALSE");
                Exact(fields,"woz_authentication","HMAC_SHARED_KEY_POSSESSION");
            }
            Counter(fields,"woz_choice_generation",false);Counter(fields,"woz_source_generation",false);
            bool revision=Counter(fields,"woz_task_revision",local||stop);
            bool taskSequence=Counter(fields,"woz_task_packet_sequence",local||stop);
            if(revision!=taskSequence)throw Invalid("Partial task context.");
            if(local)Exact(fields,"woz_remote_sequence",NotObserved);else Counter(fields,"woz_remote_sequence",false);
            ValidateFailure(fields,eventName);
            if(local)
            {
                foreach(string key in TimeFields)Exact(fields,key,NotObserved);
            }
            else
            {
                double? issued=Time(fields,"woz_task_issued_participant_time",!stop);
                double? challenge=Time(fields,"woz_challenge_issued_participant_time",preflight||pose);
                double received=Time(fields,"woz_receipt_participant_time",true).Value;
                double sent=Time(fields,"woz_send_operator_time",true).Value;
                if(received>appendTime||(issued.HasValue&&issued.Value>received)||
                    (challenge.HasValue&&(challenge.Value>received||!issued.HasValue||challenge.Value<issued.Value)))
                    throw Invalid("Participant-local time order.");
                if(issued.HasValue&&!revision)throw Invalid("Issued task time lacks task context.");
                // TaskReceipt and SafetyStopRequest packets carry no owned
                // challenge provenance. A current local challenge must not
                // be attached to either packet after the fact.
                if(receipt||stop)Exact(fields,"woz_challenge_issued_participant_time",NotObserved);
                if(preflight||pose)
                {
                    double head=Time(fields,"woz_head_operator_time",true).Value;
                    double eye=Time(fields,"woz_eye_operator_time",true).Value;
                    if(head>sent||eye>sent)throw Invalid("Operator-local time order.");
                }
                else
                {Exact(fields,"woz_head_operator_time",NotObserved);Exact(fields,"woz_eye_operator_time",NotObserved);}
                // Intentionally no subtraction or ordering between operator
                // times and participant times, even when clock IDs spell alike.
            }
            if(pose)
            {
                Counter(fields,"woz_epoch_sample_sequence",false);
                double x=Number(fields["woz_head_qx"]),y=Number(fields["woz_head_qy"]),
                    z=Number(fields["woz_head_qz"]),w=Number(fields["woz_head_qw"]);
                double ex=Number(fields["woz_eye_dx"]),ey=Number(fields["woz_eye_dy"]),ez=Number(fields["woz_eye_dz"]);
                Unit(x*x+y*y+z*z+w*w);Unit(ex*ex+ey*ey+ez*ez);
            }
            else
            {
                Exact(fields,"woz_epoch_sample_sequence",NotObserved);
                foreach(string key in PoseFields)Exact(fields,key,NotObserved);
            }
        }

        static void ValidateFailure(IReadOnlyDictionary<string,string> fields,string eventName)
        {
            if(eventName=="SafetyStopRequest")Exact(fields,"woz_failure_reason","OPERATOR_STOP");
            else if(eventName=="LocalRevoke")Exact(fields,"woz_failure_reason","LOCAL_REVOKE");
            else if(eventName=="LocalFailure")
            {
                string reason=fields["woz_failure_reason"];
                if(reason!="SOURCE_OR_TRANSPORT_LOST"&&reason!="RECORDING_FAILURE"&&reason!="DISPOSED"&&reason!="AGE_EXPIRED")
                    throw Invalid("Unknown local failure reason.");
            }
            else Exact(fields,"woz_failure_reason",NotApplicable);
        }
        static bool Counter(IReadOnlyDictionary<string,string> fields,string key,bool optional)
        {
            string value=fields[key];if(optional&&value==NotObserved)return false;
            long result;
            if(value.Length>19||!Int64.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out result)||result<0||
                result.ToString(CultureInfo.InvariantCulture)!=value)throw Invalid("Invalid bounded counter.");
            return true;
        }
        static double? Time(IReadOnlyDictionary<string,string> fields,string key,bool required)
        {
            string value=fields[key];if(!required&&value==NotObserved)return null;
            double number=Number(value);if(number<0)throw Invalid("Negative local time.");return number;
        }
        static double Number(string text)
        {
            ulong bits;
            if(text==null||text.Length!=16||!UInt64.TryParse(text,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out bits)||
                bits.ToString("X16",CultureInfo.InvariantCulture)!=text)throw Invalid("Noncanonical numeric bits.");
            double value=BitConverter.Int64BitsToDouble(unchecked((long)bits));
            if(!Finite(value))throw Invalid("Nonfinite numeric bits.");return value;
        }
        static void Unit(double squaredLength)
        {if(!Finite(squaredLength)||Math.Abs(squaredLength-1)>UnitTolerance)throw Invalid("Mapped pose is not a unit value.");}
        static void Identity(string value)
        {
            if(value==NotObserved||value==NotApplicable)throw Invalid("Missing identity.");
            try{WireCodec.RequireIdentity(value);}catch(ArgumentException){throw Invalid("Invalid bounded identity.");}
        }
        static void Exact(IReadOnlyDictionary<string,string> fields,string key,string expected)
        {if(!String.Equals(fields[key],expected,StringComparison.Ordinal))throw Invalid("Unexpected evidence value.");}
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        static InvalidOperationException Invalid(string message){return new InvalidOperationException("WoZ recording schema: "+message);}
    }
}
