#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AvatarStroop.Runtime
{
    // Consecutive software observations, not physical sensor gap endpoints.
    // A returned observation row does not by itself establish usable gaze.
    public sealed class StudyEyeAvailability
    {
        public string State,Reason,Transition,TimeBasis;
        public long FirstRecordOrdinal,LastRecordOrdinal,ObservationRows;
        public long? FirstTaskCursor,LastTaskCursor;
        public double? FirstObservedLocalSeconds,LastObservedLocalSeconds,FollowingObservedLocalSeconds;
    }

    public sealed partial class StudyAnalysis
    {
        public readonly List<StudyEyeAvailability> EyeAvailability=new List<StudyEyeAvailability>();

        static long? LongField(IDictionary<string,string> fields,string name)
        {long value;return Int64.TryParse(Get(fields,name),NumberStyles.Integer,Inv,out value)?(long?)value:null;}

        void ObserveEyeAvailability(StudyDataRecord record)
        {
            if(record.Kind!="eye_observation"&&record.Kind!="observation")return;
            var f=record.Fields;string state,reason,timeBasis;double? time;
            if(record.Kind=="eye_observation")
            {
                int cause=Int(f,"reason",-1),disposition=Int(f,"eye_observation",-1);
                if(cause<0||cause>(int)ViveRawEyeObservation.Reason.ClientFaulted||
                    disposition!=(cause==0?0:1))throw new InvalidDataException("Invalid owner eye availability observation.");
                // A repeated frame is not evidence that the eye client failed.
                if(cause==0)return;
                state="MISSING";reason=ViveRawReadableAnalysisCsv.EyeReasonLabel(cause);
                time=Number(f,"owner_observed_local_seconds");timeBasis="OWNER_MISSING_OBSERVATION";
            }
            else
            {
                state="SAMPLE_RECORDED";reason="RECORDED_SAMPLE_NOT_VALIDITY_OR_COVERAGE";
                time=Number(f,"checked_at");timeBasis="OWNER_INGEST_CHECK";
            }
            long? cursor=LongField(f,"task_cursor");
            var previous=EyeAvailability.Count==0?null:EyeAvailability[EyeAvailability.Count-1];
            bool monotonic=previous==null||!time.HasValue||!previous.LastObservedLocalSeconds.HasValue||time.Value>=previous.LastObservedLocalSeconds.Value;
            if(!monotonic)Warn("Eye availability owner time moved backwards; observations remain separate and no gap duration is inferred.");
            if(previous!=null&&previous.State==state&&previous.Reason==reason&&monotonic)
            {
                previous.LastRecordOrdinal=record.Ordinal;previous.ObservationRows++;
                previous.LastObservedLocalSeconds=time;previous.LastTaskCursor=cursor;return;
            }
            if(EyeAvailability.Count>=100000)throw new InvalidDataException("Too many eye availability transitions.");
            if(previous!=null&&monotonic)previous.FollowingObservedLocalSeconds=time;
            EyeAvailability.Add(new StudyEyeAvailability {
                State=state,Reason=reason,Transition=previous==null?"INITIAL_OBSERVATION":!monotonic?"NONMONOTONIC_OWNER_TIME":
                    state=="SAMPLE_RECORDED"?"DATA_RESUMED":previous.State=="MISSING"?"MISSING_REASON_CHANGED":"MISSING_OBSERVED",
                TimeBasis=timeBasis,FirstRecordOrdinal=record.Ordinal,LastRecordOrdinal=record.Ordinal,ObservationRows=1,
                FirstTaskCursor=cursor,LastTaskCursor=cursor,FirstObservedLocalSeconds=time,LastObservedLocalSeconds=time});
        }
    }
}
#endif
