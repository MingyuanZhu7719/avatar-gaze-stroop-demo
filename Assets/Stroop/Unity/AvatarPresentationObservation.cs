using AvatarStroop.Core;

namespace AvatarStroop.Unity
{
    // Snapshot of applied renderer state at the start of a camera render, emitted
    // only if that state remained unchanged through camera completion. A renderer
    // witness establishes inclusion in this camera's render, never photon visibility.
    public sealed class AvatarPresentationObservation
    {
        public Actor Role { get; private set; }
        public string TrialId { get; private set; }
        public Phase Phase {get;private set;}
        public int BlockIndex {get;private set;}
        public int Segment {get;private set;}
        public AvatarOutput CapturedOutput {get;private set;}
        public string PresentationKind { get { return Role == Actor.Operator ? "static-participant-placeholder-no-reverse-tracking" : "development-shared-avatar-renderer"; } }
        public bool VisibilityRequested { get; private set; }
        public bool AnyAvatarRendererObserved { get; private set; }
        public AvatarPose AppliedPose { get; private set; }
        public bool HeadHeld { get; private set; }
        public bool EyesHeld { get; private set; }
        public bool HeadInputValid { get; private set; }
        public bool EyeInputValid { get; private set; }
        public ControlSource? ActualInputSource { get; private set; }
        public string MappingVersion { get; private set; }
        public string InputStreamId { get; private set; }
        public string InputClockId { get; private set; }
        public long? InputSequence { get; private set; }
        public double? InputCaptureLocalSeconds { get; private set; }
        public int Frame { get; private set; }
        public double RenderProxySeconds { get; private set; }
        public string ClockId { get { return WorldSpaceExperimentView.RenderClockId; } }
        public string TimestampMeaning { get { return WorldSpaceExperimentView.RenderTimestampMeaning; } }

        internal AvatarPresentationObservation(Actor role, TaskView view, bool visible, AvatarPose applied,
            bool headHeld, bool eyesHeld, AvatarOutput input)
        {
            Role = role; TrialId = view==null?null:view.TrialId;Phase=view==null?Phase.Preparation:view.Phase;
            BlockIndex=view==null?-1:view.BlockIndex;Segment=view==null?0:view.Segment;CapturedOutput=input;
            VisibilityRequested = visible; AppliedPose = applied;
            HeadHeld = headHeld; EyesHeld = eyesHeld;
            if(input == null || role == Actor.Operator) return;
            HeadInputValid = input.HeadValid; EyeInputValid = input.EyeValid;
            ActualInputSource = input.ActualSource; MappingVersion = input.MappingVersion;
            if(input.InputSample == null) return;
            InputStreamId = input.InputSample.StreamId; InputClockId = input.InputSample.ClockId;
            InputSequence = input.InputSample.Sequence; InputCaptureLocalSeconds = input.InputSample.CaptureLocalSeconds;
        }
        internal void Complete(int frame, double time, bool observed)
        {
            Frame = frame; RenderProxySeconds = time; AnyAvatarRendererObserved = observed;
        }
    }
}
