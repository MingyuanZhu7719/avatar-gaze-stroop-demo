using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // An issuer-owned, single-use IN-MEMORY development candidate. Only the
    // exact instance held privately by the issuing host may be consumed after
    // its participant, choice generation, current head and target are checked.
    // Its declared identities are not device authentication or user consent;
    // it is not an I/O permit and proves neither file ownership nor durability.
    internal sealed class VivePracticeRetryContinuation
    {
        // Internal Simulation + sealed in-memory rehearsal only. It does not
        // admit real eye capture, a file, hardware, or a research session.
        // Injected test callbacks and IClock are caller code, not a sandbox.
        internal static bool RetryHandoffEnabled {get{return true;}}
        internal readonly VivePracticeOwnedMemoryStart Issuer;
        internal readonly VivePracticeTaskEventCursor Cursor;
        internal readonly VivePracticeCursorAnchor Anchor;
        internal readonly ExperimentIncrementalSnapshot TerminalSnapshot;
        internal readonly ViveSyntheticStepTrace TerminalStep;
        internal readonly ViveDevelopmentParticipant.PracticeTaskDrainFence TerminalFence;
        internal readonly long TerminalRevision,TerminalSequence;
        internal readonly double TerminalTime;
        internal readonly ExperimentIncrementalSnapshot IssueSnapshot;
        internal readonly ViveSyntheticStepTrace IssueStep;
        internal readonly ViveDevelopmentParticipant.PracticeTaskDrainFence IssueFence;
        internal readonly int IssueTraceCount;
        internal readonly long IssueRevision;

        internal VivePracticeRetryContinuation(VivePracticeOwnedMemoryStart issuer,VivePracticeTaskEventCursor cursor,
            VivePracticeCursorAnchor anchor,ExperimentIncrementalSnapshot terminalSnapshot,
            ViveSyntheticStepTrace terminalStep,ViveDevelopmentParticipant.PracticeTaskDrainFence terminalFence,
            long terminalRevision,long terminalSequence,double terminalTime,
            ViveDevelopmentParticipant.PracticeTaskDrainFence issueFence)
        {
            Issuer=issuer;Cursor=cursor;Anchor=anchor;TerminalSnapshot=terminalSnapshot;
            TerminalStep=terminalStep;TerminalFence=terminalFence;TerminalRevision=terminalRevision;
            TerminalSequence=terminalSequence;TerminalTime=terminalTime;
            IssueFence=issueFence;IssueSnapshot=issueFence.Snapshot;IssueTraceCount=issueFence.StepTrace.Count;
            IssueStep=issueFence.StepTrace[IssueTraceCount-1];IssueRevision=IssueStep.Revision;
        }
        internal bool CanWrite {get{return false;}}
        internal bool DurabilityProven {get{return false;}}
    }
}
