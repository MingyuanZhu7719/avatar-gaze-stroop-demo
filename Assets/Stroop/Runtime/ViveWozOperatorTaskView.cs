using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Render-only adapter. Only pass the currently fresh task returned by the
    // transport; a null input blanks the task. This is not readiness evidence,
    // authentication, an eye sample, a recording, or a source-lock assertion.
    public static class ViveWozOperatorTaskView
    {
        public static TaskView FromFreshTask(TaskSnapshot task)
        {
            return new TaskView {
                Phase=task==null?Phase.Preparation:task.Phase,
                BlockIndex=task==null?-1:task.BlockIndex,
                Segment=task==null?0:task.Segment,
                TrialId=task==null?null:task.TrialId,
                Stimulus=task!=null&&task.Word.HasValue?new Stimulus(task.Word.Value,task.Ink.Value):null,
                ParticipantAvatarVisible=task!=null&&task.Phase==Phase.MainTask,
                OperatorViewProvisional=task==null||task.Phase!=Phase.MainTask||task.Condition==Condition.D,
                SourceLabel="DEVELOPMENT TASK DISPLAY - LIVE SOURCE NOT VERIFIED"
            };
        }
    }
}
