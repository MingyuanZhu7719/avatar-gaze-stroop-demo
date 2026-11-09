using System;
using System.Collections.Generic;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Projects actual, committed owner-thread Experiment transactions into the
    // existing development cursor. It never asserts that a Live choice remains
    // active: task state alone cannot prove operator consent or readiness.
    // Neither this object nor the source journal can admit or resume a task.
    public sealed class ViveExperimentMutationCursor
    {
        readonly Experiment experiment;readonly ViveMutationCursorCandidate cursor;bool validated;
        public bool RealExperimentSourceBound {get{return true;}}
        public bool CanEnterCountdown {get{return false;}} public bool CanResume {get{return false;}}
        public bool Terminal {get{return cursor.Terminal||experiment.MutationObservationFaulted;}}
        public bool CaughtUp {get{return validated&&!Terminal&&cursor.CaughtUp&&experiment.IsMutationRevisionCurrent(cursor.Revision);}}
        public long Revision {get{return cursor.Revision;}}
        public string LastIssue {get{return experiment.MutationObservationFaulted?experiment.MutationObservationIssue:cursor.LastIssue;}}
        public ViveMutationStateCandidate Current {get{return CaughtUp?cursor.Current:null;}}
        public ViveExperimentMutationCursor(Experiment source)
        {
            if(source==null||!source.MutationObservationEnabled||source.MutationObservationFaulted||source.InitialMutationSnapshot==null)
                throw new ArgumentException("An actual development Experiment with observation enabled is required");
            experiment=source;cursor=new ViveMutationCursorCandidate(source.Configuration.SessionId,Project(source.InitialMutationSnapshot));
        }
        static ViveMutationStateCandidate Project(ExperimentMutationSnapshot snapshot)
        {
            if(snapshot==null)throw new InvalidOperationException("Mutation snapshot unavailable");
            return new ViveMutationStateCandidate(snapshot.Task,snapshot.Selection,snapshot.SourceOwner,false,snapshot.PausedFrom);
        }
        public bool Advance()
        {
            if(Terminal)return false;
            bool caughtUp=cursor.Advance(after=>{
                ExperimentMutationCommit commit;ExperimentMutationSnapshot current;long head;
                if(!experiment.TryReadMutationAfter(after,out commit,out head,out current))throw new InvalidOperationException("Actual mutation feed unavailable");
                ViveMutationCommitCandidate projected=null;
                if(commit!=null)
                {
                    var steps=new List<ViveMutationStepCandidate>();
                    foreach(var step in commit.Steps)steps.Add(new ViveMutationStepCandidate(step.Index,Project(step.Before),Project(step.After)));
                    projected=new ViveMutationCommitCandidate(commit.Revision,true,steps,Project(commit.FinalState));
                }
                return new ViveMutationReadCandidate(head,projected,Project(current));
            });
            validated=caughtUp;return caughtUp;
        }
    }
}
