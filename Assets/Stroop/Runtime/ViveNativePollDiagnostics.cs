using System;

namespace AvatarStroop.Runtime
{
    // Brackets measured on the same process monotonic clock as task events.
    // These describe API calls, not sensor exposure, calibration accuracy or a
    // verified mapping from the opaque native timestamp to software time.
    public sealed class ViveNativePollDiagnostics
    {
        public long ProviderPollSequence {get;private set;}
        public double PollStartedLocalSeconds {get;private set;}
        public double PollReturnedLocalSeconds {get;private set;}
        public double? StatusReturnedLocalSeconds {get;private set;}
        public double? DeviceReturnedLocalSeconds {get;private set;}
        public double? CalibrationReturnedLocalSeconds {get;private set;}
        public double? ReadStartedLocalSeconds {get;private set;}
        public double? ReadReturnedLocalSeconds {get;private set;}
        public int? StatusResult {get;private set;}
        public int? CalibrationResult {get;private set;}
        public bool? CalibrationNeeded {get;private set;}
        public int? ReadResult {get;private set;}
        public ViveNativePollDiagnostics(long providerPollSequence,double pollStartedLocalSeconds,double pollReturnedLocalSeconds,
            double? statusReturnedLocalSeconds,double? deviceReturnedLocalSeconds,double? calibrationReturnedLocalSeconds,
            double? readStartedLocalSeconds,double? readReturnedLocalSeconds,int? statusResult,int? calibrationResult,bool? calibrationNeeded,int? readResult)
        {
            ProviderPollSequence=providerPollSequence;PollStartedLocalSeconds=pollStartedLocalSeconds;PollReturnedLocalSeconds=pollReturnedLocalSeconds;
            StatusReturnedLocalSeconds=statusReturnedLocalSeconds;DeviceReturnedLocalSeconds=deviceReturnedLocalSeconds;
            CalibrationReturnedLocalSeconds=calibrationReturnedLocalSeconds;ReadStartedLocalSeconds=readStartedLocalSeconds;
            ReadReturnedLocalSeconds=readReturnedLocalSeconds;StatusResult=statusResult;CalibrationResult=calibrationResult;
            CalibrationNeeded=calibrationNeeded;ReadResult=readResult;
        }
    }

    // An acquisition is independent of the Unity render/update loop. No head
    // observation, task cursor or AOI is invented for the native sample. Invalid
    // native samples are retained with their original masks/calibration state.
    public sealed class ViveRawEyeAcquisition
    {
        public long Sequence {get;private set;}
        public double WorkerPublishedLocalSeconds {get;private set;}
        public ViveProEyePoll Poll {get;private set;}
        public long EligiblePolls {get;private set;}
        public long DuplicatePolls {get;private set;}
        public long NativeSequenceGapCount {get;private set;}
        public long NativeSequenceDiscontinuities {get;private set;}
        public ViveRawEyeAcquisition(long sequence,double workerPublishedLocalSeconds,ViveProEyePoll poll,
            long eligiblePolls,long duplicatePolls,long nativeSequenceGapCount,long nativeSequenceDiscontinuities)
        {
            Sequence=sequence;WorkerPublishedLocalSeconds=workerPublishedLocalSeconds;Poll=poll;
            EligiblePolls=eligiblePolls;DuplicatePolls=duplicatePolls;NativeSequenceGapCount=nativeSequenceGapCount;
            NativeSequenceDiscontinuities=nativeSequenceDiscontinuities;
        }
    }
}
