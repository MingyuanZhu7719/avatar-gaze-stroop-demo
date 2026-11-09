using System;
using System.Collections.Generic;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Read-only structural proof over one immutable, bounded participant trace.
    // It is not a consent, target, hardware, persistence, or file permit.
    internal static class VivePracticeRetryReleaseValidator
    {
        internal static bool IsReleaseOnly(IReadOnlyList<ViveSyntheticStepTrace> trace,long mutationStepsObserved,
            int fromCount,ViveSyntheticStepTrace fromStep,ExperimentIncrementalSnapshot fromSnapshot,
            int endExclusive,ExperimentIncrementalSnapshot endSnapshot)
        {
            if(trace==null||trace.Count<1||trace.Count>4096||mutationStepsObserved!=trace.Count||fromCount<1||
                fromCount>trace.Count||endExclusive<fromCount||endExclusive>trace.Count||fromStep==null||
                fromSnapshot==null||endSnapshot==null||fromSnapshot.Task==null||endSnapshot.Task==null||
                fromSnapshot.Task.Phase!=Phase.PracticeRetry||endSnapshot.Task.Phase!=Phase.PracticeRetry||
                !Object.ReferenceEquals(trace[fromCount-1],fromStep)||!Object.ReferenceEquals(fromStep.After,fromSnapshot))return false;
            if(endExclusive<trace.Count&&(trace[endExclusive]==null||trace[endExclusive-1]==null||
                trace[endExclusive].Revision<=trace[endExclusive-1].Revision||trace[endExclusive].Index!=0))return false;
            var previous=fromSnapshot;long revision=fromStep.Revision;int index=fromStep.Index;
            for(int i=fromCount;i<endExclusive;i++)
            {
                var step=trace[i];
                if(step==null||step.Before==null||step.After==null||step.Before.Task==null||step.After.Task==null||
                    !Object.ReferenceEquals(previous,step.Before)||step.Before.Task.Phase!=Phase.PracticeRetry||
                    step.After.Task.Phase!=Phase.PracticeRetry||
                    !((step.Revision==revision&&step.Index==index+1)||
                      (revision<Int64.MaxValue&&step.Revision==revision+1&&step.Index==0)))return false;
                var before=step.Before.Scalars;var after=step.After.Scalars;
                if(after.EventCount!=before.EventCount||(after.HeldMask&~before.HeldMask)!=0||
                    !Object.ReferenceEquals(step.Before.TrialVector,step.After.TrialVector)||
                    !Object.ReferenceEquals(step.Before.Practice,step.After.Practice)||
                    !Object.ReferenceEquals(step.Before.Ratings,step.After.Ratings))return false;
                after.HeldMask=before.HeldMask;
                if(!before.Equals(after))return false;
                previous=step.After;revision=step.Revision;index=step.Index;
            }
            return Object.ReferenceEquals(previous,endSnapshot);
        }
    }
}
