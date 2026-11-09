using System;
using System.Collections.Generic;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Structural precheck only. Inputs are the owning cursor's private committed
    // checkpoint and its participant's outstanding drain fence. A true result
    // is not consent, a commit receipt, current authority, or permission to write.
    // The caller must revalidate/consume the fence and use the issuer's existing
    // atomic receipt chain. This class never changes the writer or experiment.
    internal static class VivePracticeTerminalValidator
    {
        internal static bool TryValidate(VivePracticeCursorAnchor anchor,
            ViveDevelopmentParticipant.PracticeTaskDrainFence fence,
            ExperimentIncrementalSnapshot checkpoint, ViveSyntheticStepTrace lastStep,
            int traceCount, long lastRevision, long lastSequence, double lastEventTime, double now,
            out IReadOnlyList<JournalEvent> events)
        {
            events=null;
            try
            {
                if(anchor==null||anchor.Participant==null||anchor.Context==null||anchor.StartSnapshot==null||
                    anchor.StartSnapshot.Task==null||fence==null||
                    !fence.BelongsTo(anchor.Participant,anchor)||checkpoint==null||checkpoint.Task==null||lastStep==null||
                    traceCount<1||lastRevision<1||lastRevision==Int64.MaxValue||lastSequence<0||lastSequence>Int32.MaxValue-5L||
                    Double.IsNaN(lastEventTime)||Double.IsInfinity(lastEventTime)||lastEventTime<0||
                    Double.IsNaN(now)||Double.IsInfinity(now)||now<lastEventTime||
                    !((anchor.Context.PracticeAttempt==1&&anchor.Context.EpochReason==VivePracticeEpochReasonV3.Initial&&anchor.Context.PreviousEpochId==null)||
                      (anchor.Context.PracticeAttempt==2&&anchor.Context.EpochReason==VivePracticeEpochReasonV3.PracticeRetry&&anchor.Context.PreviousEpochId!=null))||
                    anchor.Context.RecoveryOrdinal!=0||
                    lastStep.Revision!=lastRevision||!Object.ReferenceEquals(lastStep.After,checkpoint)||
                    checkpoint.Scalars.EventCount!=lastSequence+1||checkpoint.Task.SessionId!=anchor.Context.SessionId||
                    checkpoint.Task.BlockIndex!=-1||checkpoint.Task.Condition.HasValue||checkpoint.Selection.HasValue||
                    checkpoint.Task.Segment!=anchor.StartSnapshot.Task.Segment||checkpoint.Scalars.PracticeAttempt!=anchor.Context.PracticeAttempt||
                    checkpoint.Scalars.TrialIndex<0||checkpoint.Scalars.TrialIndex>7||!checkpoint.CurrentTrial.HasValue)return false;
                var trial=checkpoint.CurrentTrial.Value;
                if(!trial.IsPractice||trial.SessionId!=anchor.Context.SessionId||trial.BlockIndex!=-1||trial.Condition.HasValue||
                    trial.PracticeAttempt!=anchor.Context.PracticeAttempt||trial.PlannedIndex!=checkpoint.Scalars.TrialIndex||trial.Segment!=checkpoint.Task.Segment)return false;

                var trace=fence.StepTrace;var head=fence.Snapshot;
                if(trace==null||traceCount>=trace.Count||trace.Count>4096||fence.MutationStepsObserved!=trace.Count||
                    !Object.ReferenceEquals(trace[traceCount-1],lastStep)||head==null||head.Task==null||
                    !Object.ReferenceEquals(trace[trace.Count-1].After,head)||fence.Revision<=lastRevision||
                    trace[trace.Count-1].Revision!=fence.Revision)return false;
                var previous=checkpoint;int count=0;bool enteredBoundary=false,enteredBlock=false,clearedTrial=false;
                int trialIndex=checkpoint.Scalars.TrialIndex,correctCount=checkpoint.Scalars.PracticeCorrect;
                bool pauseBoundary=head.Task.Phase==Phase.Paused;
                bool mayIncreaseCorrect=!pauseBoundary&&checkpoint.Task.Phase==Phase.Practice;
                long revision=lastRevision;int stepIndex=lastStep.Index;
                for(int i=traceCount;i<trace.Count;i++)
                {
                    var step=trace[i];
                    if(step==null||step.Before==null||step.After==null||
                        !(i==traceCount?(step.Revision==lastRevision+1&&step.Index==0):
                          ((step.Revision==revision&&step.Index==stepIndex+1)||
                           (revision<Int64.MaxValue&&step.Revision==revision+1&&step.Index==0)))||
                        !Object.ReferenceEquals(previous,step.Before)||step.After.Task==null||
                        step.After.Task.SessionId!=anchor.Context.SessionId||step.After.Task.Segment!=checkpoint.Task.Segment||
                        step.After.Scalars.PracticeAttempt!=anchor.Context.PracticeAttempt||step.After.Selection.HasValue||
                        step.After.SourceOwner!=checkpoint.SourceOwner||step.After.SourceConfirmationSequence!=checkpoint.SourceConfirmationSequence||
                        step.Before.Scalars.EventCount<0||step.After.Scalars.EventCount<step.Before.Scalars.EventCount||
                        step.After.Scalars.EventCount-step.Before.Scalars.EventCount>1)return false;
                    if(step.Revision!=fence.Revision)
                    {
                        var before=step.Before.Scalars;var after=step.After.Scalars;
                        if(step.After.Scalars.EventCount!=step.Before.Scalars.EventCount||
                            (after.HeldMask&~before.HeldMask)!=0||
                            !Object.ReferenceEquals(step.Before.TrialVector,step.After.TrialVector)||
                            !Object.ReferenceEquals(step.Before.Practice,step.After.Practice)||
                            !Object.ReferenceEquals(step.Before.Ratings,step.After.Ratings))return false;
                        after.HeldMask=before.HeldMask;
                        if(!before.Equals(after))return false;
                    }
                    var s=step.After;
                    if(s.Scalars.TrialIndex<trialIndex||s.Scalars.TrialIndex>checkpoint.Scalars.TrialIndex+(pauseBoundary?0:1)||
                        s.Scalars.PracticeCorrect<correctCount||s.Scalars.PracticeCorrect>checkpoint.Scalars.PracticeCorrect+(mayIncreaseCorrect?1:0))return false;
                    if(s.Scalars.TrialIndex>checkpoint.Scalars.TrialIndex&&mayIncreaseCorrect&&
                        s.Scalars.PracticeCorrect!=checkpoint.Scalars.PracticeCorrect+1)return false;
                    trialIndex=s.Scalars.TrialIndex;correctCount=s.Scalars.PracticeCorrect;
                    if(s.Task.Phase!=checkpoint.Task.Phase)
                    {
                        if(s.Task.Phase!=head.Task.Phase) return false;
                        enteredBoundary=true;
                    }
                    else if(enteredBoundary)return false;
                    if(s.Task.BlockIndex==0)enteredBlock=true;
                    else if(s.Task.BlockIndex!=-1||enteredBlock)return false;
                    if(head.Task.Phase!=Phase.BlockPreparation&&enteredBlock)return false;
                    if(!s.CurrentTrial.HasValue)clearedTrial=true;
                    else if(clearedTrial||s.CurrentTrial.Value.TrialId!=trial.TrialId||!s.CurrentTrial.Value.IsPractice||
                        s.CurrentTrial.Value.SessionId!=anchor.Context.SessionId||s.CurrentTrial.Value.PracticeAttempt!=anchor.Context.PracticeAttempt||
                        s.CurrentTrial.Value.PlannedIndex!=trial.PlannedIndex||s.CurrentTrial.Value.BlockIndex!=-1||
                        s.CurrentTrial.Value.Segment!=checkpoint.Task.Segment||s.CurrentTrial.Value.Condition.HasValue||
                        s.CurrentTrial.Value.ListId!=trial.ListId||s.CurrentTrial.Value.Word!=trial.Word||s.CurrentTrial.Value.Ink!=trial.Ink||
                        s.CurrentTrial.Value.Onset!=trial.Onset||s.CurrentTrial.Value.TechnicalIssue!=trial.TechnicalIssue)return false;
                    count+=step.After.Scalars.EventCount-step.Before.Scalars.EventCount;
                    if(count>4)return false;
                    previous=step.After;revision=step.Revision;stepIndex=step.Index;
                }
                if(!Object.ReferenceEquals(previous,head)||count<2||head.Scalars.EventCount!=lastSequence+1+count||
                    head.Scalars.PracticeAttempt!=anchor.Context.PracticeAttempt||head.Selection.HasValue)return false;
                var copied=anchor.Participant.ReadEventsAfter(lastSequence,count);
                if(copied==null||copied.Count!=count)return false;
                double prior=lastEventTime;
                for(int i=0;i<count;i++)
                {
                    var e=copied[i];
                    if(e==null||e.Sequence!=lastSequence+1+i||e.SessionId!=anchor.Context.SessionId||e.ClockId!=anchor.Context.ClockId||
                        e.Segment!=checkpoint.Task.Segment||Double.IsNaN(e.Time)||Double.IsInfinity(e.Time)||e.Time<prior||e.Time>now)return false;
                    prior=e.Time;
                }
                int eventIndex=0;
                for(int i=traceCount;i<trace.Count;i++)
                {
                    var step=trace[i];
                    if(step.After.Scalars.EventCount==step.Before.Scalars.EventCount)continue;
                    var original=copied[eventIndex++];
                    string expectedTrial=step.Before.CurrentTrial.HasValue?step.Before.CurrentTrial.Value.TrialId:null;
                    if(original.Sequence!=step.Before.Scalars.EventCount||original.BlockIndex!=step.Before.Task.BlockIndex||
                        original.TrialId!=expectedTrial)return false;
                }
                // A pause is a boundary, never an implicit resume or consent to
                // resume recording. It is checked separately from completion.
                if(head.Task.Phase==Phase.Paused)
                {
                    if((checkpoint.Task.Phase!=Phase.Practice&&checkpoint.Task.Phase!=Phase.PracticeCorrection)||
                        head.Task.BlockIndex!=-1||head.PausedFrom!=checkpoint.Task.Phase||
                        head.Scalars.TrialIndex!=checkpoint.Scalars.TrialIndex||head.Scalars.PracticeCorrect!=checkpoint.Scalars.PracticeCorrect||
                        !head.CurrentTrial.HasValue||head.CurrentTrial.Value.TrialId!=trial.TrialId)return false;
                    bool interrupted=checkpoint.Task.Phase==Phase.Practice&&trial.Outcome==TrialOutcome.Pending;
                    if(checkpoint.Task.Phase==Phase.Practice&&!interrupted||
                        checkpoint.Task.Phase==Phase.PracticeCorrection&&(trial.Outcome!=TrialOutcome.Responded||trial.Correct!=false)||
                        count!=(interrupted?3:2))return false;
                    int n=0;string actor,fromPause,toPause;
                    if(interrupted)
                    {
                        string onset;
                        if(copied[n].Kind!="trial_interrupted"||copied[n].TrialId!=trial.TrialId||
                            !copied[n].Fields.TryGetValue("onset_available",out onset)||onset!=trial.Onset.HasValue.ToString()||
                            head.CurrentTrial.Value.Outcome!=TrialOutcome.Interrupted)return false;
                        n++;
                    }
                    else if(head.CurrentTrial.Value.Outcome!=trial.Outcome)return false;
                    Actor parsed;
                    if(copied[n].Kind!="explicit_pause"||copied[n].BlockIndex!=-1||copied[n].TrialId!=trial.TrialId||
                        !copied[n].Fields.TryGetValue("actor",out actor)||!Enum.TryParse(actor,false,out parsed)||
                        !Enum.IsDefined(typeof(Actor),parsed)||parsed.ToString()!=actor)return false;
                    if(interrupted)
                    {string interruptionActor;if(!copied[0].Fields.TryGetValue("actor",out interruptionActor)||interruptionActor!=actor)return false;}
                    n++;
                    if(copied[n].Kind!="phase_changed"||copied[n].BlockIndex!=-1||copied[n].TrialId!=trial.TrialId||
                        !copied[n].Fields.TryGetValue("from",out fromPause)||fromPause!=checkpoint.Task.Phase.ToString()||
                        !copied[n].Fields.TryGetValue("to",out toPause)||toPause!="Paused")return false;
                    events=copied;return true;
                }
                if(checkpoint.Scalars.TrialIndex!=7||head.Scalars.TrialIndex!=8||head.CurrentTrial.HasValue||count<3)return false;
                bool correct=checkpoint.Task.Phase==Phase.Practice&&trial.Outcome==TrialOutcome.Pending&&trial.Onset.HasValue;
                bool correction=checkpoint.Task.Phase==Phase.PracticeCorrection&&trial.Outcome==TrialOutcome.Responded&&trial.Correct==false;
                int expected=checkpoint.Scalars.PracticeCorrect;
                if(expected<0||expected>7)return false;
                bool endsFailed=anchor.Context.PracticeAttempt==2&&expected+(correct?1:0)<6;
                int requiredCount=endsFailed?4:3;
                if(correct)
                {
                    string answer,isCorrect;
                    if(count!=requiredCount||copied[0].Kind!="first_response"||copied[0].TrialId!=trial.TrialId||
                        !copied[0].Fields.TryGetValue("correct",out isCorrect)||isCorrect!="True"||
                        !copied[0].Fields.TryGetValue("response",out answer)||answer!=trial.Ink.ToString())return false;
                    expected++;
                }
                else if(correction)
                {if(count!=requiredCount||copied[0].Kind!="practice_correction_continue"||copied[0].TrialId!=trial.TrialId)return false;}
                else return false;
                var completion=copied[1];var transition=copied[count-1];string attempt,planned,correctFirst,from,to;
                bool pass=expected>=6;
                if(completion.Kind!="practice_completed"||completion.TrialId!=trial.TrialId||completion.BlockIndex!=-1||
                    !completion.Fields.TryGetValue("attempt",out attempt)||attempt!=anchor.Context.PracticeAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture)||
                    !completion.Fields.TryGetValue("planned",out planned)||planned!="8"||
                    !completion.Fields.TryGetValue("correct_first",out correctFirst)||correctFirst!=expected.ToString(System.Globalization.CultureInfo.InvariantCulture)||
                    transition.Kind!="phase_changed"||transition.TrialId!=null||
                    !transition.Fields.TryGetValue("from",out from)||from!=checkpoint.Task.Phase.ToString()||
                    !transition.Fields.TryGetValue("to",out to)||to!=(pass?"BlockPreparation":(endsFailed?"Ended":"PracticeRetry"))||
                    transition.BlockIndex!=(pass?0:-1)||head.Task.Phase!=(pass?Phase.BlockPreparation:(endsFailed?Phase.Ended:Phase.PracticeRetry))||
                    head.Task.BlockIndex!=(pass?0:-1)||head.Scalars.PracticeCorrect!=expected)return false;
                if(endsFailed)
                {
                    string reason,detail;var ended=copied[2];
                    if(head.Scalars.EndReason!=EndReason.PracticeFailed||ended.Kind!="session_ended"||ended.BlockIndex!=-1||ended.TrialId!=null||
                        !ended.Fields.TryGetValue("reason",out reason)||reason!="PracticeFailed"||
                        !ended.Fields.TryGetValue("detail",out detail)||detail!="two_practice_attempts_below_6_of_8")return false;
                }
                events=copied;return true;
            }
            catch(Exception error) when(!VivePracticeJournalV3Codec.Fatal(error)){return false;}
        }
    }
}
