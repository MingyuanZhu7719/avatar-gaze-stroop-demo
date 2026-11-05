using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // One main-thread-owned aggregate per preview run. Observe each source observation
    // once; the source's disposition owns freshness/deduplication. Retain this object
    // after Stop, then Reset or replace it only when beginning a new run. No observation,
    // sample, vector, identifier, timestamp, reason string or provider is retained.
    public sealed class VivePreviewDiagnostics
    {
        // Latest worker totals within this run. A reaped worker returns zero; that
        // snapshot must not erase already observed totals. These are worker activity
        // counts, not tracking accuracy, hardware frame rate or wearer evidence.
        public long NativePolls {get;private set;}
        public long SuccessfulEyeReads {get;private set;}
        public long SequenceChanges {get;private set;}
        public long TimestampChanges {get;private set;}

        // Observation partition: Observations = NewEyeFrames + MissingViews +
        // DuplicateViews + StaleViews + OtherDispositionViews. Missing includes null
        // observations/polls/eye payloads. Repeated/stale views never count as new frames.
        public long Observations {get;private set;}
        public long NewEyeFrames {get;private set;}
        public long MissingViews {get;private set;}
        public long DuplicateViews {get;private set;}
        public long StaleViews {get;private set;}
        public long OtherDispositionViews {get;private set;}

        // Overlapping subsets, not additional rejection categories. HeadReady is the
        // caller's current preview head gate, never proof that the headset is worn.
        // Mask-valid counts the native combined direction bit, even if data is absent
        // or the vector is nonunit. UsableEyeFrames counts the source's EyeUsable flag.
        public long NewEyeFramesWithHeadReady {get;private set;}
        public long CombinedMaskValidFrames {get;private set;}
        public long UsableEyeFrames {get;private set;}
        // A subset of DuplicateViews with healthy current raw eye/head gates and
        // mapped head. This is diagnostic only: a duplicate supplies no usable eye
        // output and we neither remap nor extend its freshness.
        public long HealthyDuplicateViews {get;private set;}

        // Accepted-frame partition, in first-blocking-gate order:
        // NewEyeFrames = all twelve rejection counters below + UsableWithHeadFrames.
        // DirectionNonunit/Convertible/NormalizedFrames are overlapping diagnostics,
        // not members of that rejection partition.
        // Raw head validity and the caller's headReady precede the mapping gate.
        // These counters describe accepted frames only; missing payloads belong to
        // MissingViews, even if their poll reports a runtime/native error.
        public long RuntimeRejectedFrames {get;private set;}
        public long CalibrationRejectedFrames {get;private set;}
        public long DeviceRejectedFrames {get;private set;}
        public long NativeRejectedFrames {get;private set;}
        public long DataAbsentFrames {get;private set;}
        public long CombinedMissingFrames {get;private set;}
        public long CombinedInvalidFrames {get;private set;}
        public long DirectionNonfiniteFrames {get;private set;}
        public long DirectionDegenerateFrames {get;private set;}
        public long DirectionMagnitudeOverflowFrames {get;private set;}
        // Legacy raw !IsUnit count after preceding native gates. Previously all
        // such frames were rejected; now this is NOT a rejection count. It equals
        // the three direction failure bins plus DirectionNormalizedFrames.
        public long DirectionNonunitFrames {get;private set;}
        // Convertible includes raw unit and raw nonunit vectors. Normalized is
        // its raw-nonunit subset; neither implies a usable head, mapping or accuracy.
        public long DirectionConvertibleFrames {get;private set;}
        public long DirectionNormalizedFrames {get;private set;}
        public long HeadBlockedFrames {get;private set;}
        public long MappingBlockedFrames {get;private set;}
        public long UsableWithHeadFrames {get;private set;}
        // Subset of UsableWithHeadFrames; projected is ignored for every blocked view.
        public long UsableOffBoardFrames {get;private set;}

        public void ObserveNative(ViveWorkerCaptureDiagnostics counts)
        {
            if(counts==null||counts.NativePolls<NativePolls)return;
            NativePolls=counts.NativePolls;
            SuccessfulEyeReads=counts.SuccessfulEyeReads;
            SequenceChanges=counts.SequenceChanges;
            TimestampChanges=counts.TimestampChanges;
        }

        public void Observe(ViveProEyeObservation observation,bool headReady,bool projected)
        {
            Observations++;
            var poll=observation==null?null:observation.RawPoll;
            var eye=poll==null?null:poll.Eye;
            if(eye==null){MissingViews++;return;}
            switch(observation.EyeDisposition)
            {
                case ViveFrameDisposition.Missing: MissingViews++;return;
                case ViveFrameDisposition.Duplicate:
                    DuplicateViews++;
                    if(HealthyDuplicate(observation,headReady))HealthyDuplicateViews++;
                    return;
                case ViveFrameDisposition.Stale: StaleViews++;return;
                case ViveFrameDisposition.Accepted: break;
                default: OtherDispositionViews++;return;
            }

            NewEyeFrames++;
            if(headReady)NewEyeFramesWithHeadReady++;
            var combined=eye.Combined;
            if(combined!=null&&(combined.ValidityMask&2UL)!=0)CombinedMaskValidFrames++;
            if(observation.EyeUsable)UsableEyeFrames++;
            if(poll.Runtime!=ViveEyeRuntimeState.Working){RuntimeRejectedFrames++;return;}
            if(poll.Calibration!=ViveEyeCalibrationState.ReportedReady){CalibrationRejectedFrames++;return;}
            if(!poll.EyeDeviceConnected){DeviceRejectedFrames++;return;}
            if(poll.NativeResultCode.HasValue&&poll.NativeResultCode.Value!=0){NativeRejectedFrames++;return;}
            if(eye.NoUser){DataAbsentFrames++;return;}
            if(combined==null){CombinedMissingFrames++;return;}
            if((combined.ValidityMask&2UL)==0||!combined.DirectionReportedValid){CombinedInvalidFrames++;return;}
            bool rawUnit=combined.Direction.IsUnit;
            if(!rawUnit)DirectionNonunitFrames++;
            Vec3 sdkRayDirection;
            var directionStatus=ViveProEyeSdkDirection.ConvertForRay(combined.Direction,out sdkRayDirection);
            if(directionStatus==ViveSdkDirectionStatus.Nonfinite){DirectionNonfiniteFrames++;return;}
            if(directionStatus==ViveSdkDirectionStatus.Degenerate){DirectionDegenerateFrames++;return;}
            if(directionStatus==ViveSdkDirectionStatus.MagnitudeOverflow){DirectionMagnitudeOverflowFrames++;return;}
            DirectionConvertibleFrames++;
            if(!rawUnit)DirectionNormalizedFrames++;
            if(!headReady||!RawHeadUsable(observation)){HeadBlockedFrames++;return;}
            if(!observation.EyeUsable){MappingBlockedFrames++;return;}
            UsableWithHeadFrames++;
            if(!projected)UsableOffBoardFrames++;
        }

        static bool RawHeadUsable(ViveProEyeObservation observation)
        {
            var head=observation.RawPoll.Head;
            return observation.HeadDisposition==ViveFrameDisposition.Accepted&&head!=null&&
                head.DeviceConnected&&head.RotationReportedValid&&head.RawTrackingRotation.IsUnit;
        }

        static bool HealthyDuplicate(ViveProEyeObservation observation,bool headReady)
        {
            var poll=observation.RawPoll;
            var eye=poll.Eye;
            var combined=eye.Combined;
            Vec3 sdkRayDirection;
            return headReady&&observation.HeadUsable&&RawHeadUsable(observation)&&
                poll.Runtime==ViveEyeRuntimeState.Working&&poll.Calibration==ViveEyeCalibrationState.ReportedReady&&
                poll.EyeDeviceConnected&&(!poll.NativeResultCode.HasValue||poll.NativeResultCode.Value==0)&&!eye.NoUser&&
                combined!=null&&(combined.ValidityMask&2UL)!=0&&combined.DirectionReportedValid&&
                ViveProEyeSdkDirection.TryGetUnitDirection(combined.Direction,out sdkRayDirection);
        }

        public void Reset()
        {
            NativePolls=SuccessfulEyeReads=SequenceChanges=TimestampChanges=0;
            Observations=NewEyeFrames=MissingViews=DuplicateViews=StaleViews=OtherDispositionViews=0;
            NewEyeFramesWithHeadReady=CombinedMaskValidFrames=UsableEyeFrames=HealthyDuplicateViews=0;
            RuntimeRejectedFrames=CalibrationRejectedFrames=DeviceRejectedFrames=NativeRejectedFrames=DataAbsentFrames=0;
            CombinedMissingFrames=CombinedInvalidFrames=DirectionNonunitFrames=HeadBlockedFrames=MappingBlockedFrames=0;
            DirectionNonfiniteFrames=DirectionDegenerateFrames=DirectionMagnitudeOverflowFrames=DirectionConvertibleFrames=DirectionNormalizedFrames=0;
            UsableWithHeadFrames=UsableOffBoardFrames=0;
        }
    }
}
