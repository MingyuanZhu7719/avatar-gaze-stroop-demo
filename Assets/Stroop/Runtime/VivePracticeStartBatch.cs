using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A historical copy from a single observed Simulation transaction. This
    // is not a revocable permit, user consent, writer admission, proof of
    // persistence or evidence of a real device or a research session.
    public sealed class VivePracticeStartBatch
    {
        public string SessionId {get;private set;}
        public string ClockId {get;private set;}
        public int Attempt {get;private set;}
        public long Revision {get;private set;}
        public IReadOnlyList<JournalEvent> Events {get;private set;}
        public bool CanWrite {get{return false;}}
        public bool ConsentProven {get{return false;}}
        public bool Durable {get{return false;}}
        internal VivePracticeStartBatch(string session,string clockId,int attempt,long revision,IReadOnlyList<JournalEvent> events)
        {SessionId=session;ClockId=clockId;Attempt=attempt;Revision=revision;Events=new ReadOnlyCollection<JournalEvent>(new List<JournalEvent>(events));}
    }

    // Read only: uses the participant's own current owner-thread fence and its
    // sole immutable pump trace. EventCount identifies event positions within
    // steps, never a revision or evidence of transaction identity. It accepts
    // only an immediately-current, complete BeginPractice revision.
    public static class VivePracticeStartBatchExtractor
    {
        static bool Finite(double x){return !Double.IsNaN(x)&&!Double.IsInfinity(x)&&x>=0;}
        public static bool TryExtract(ViveDevelopmentParticipant task,IClock clock,int expectedAttempt,int maximumTraceSteps,out VivePracticeStartBatch batch)
        {
            batch=null;
            if(task==null||clock==null||expectedAttempt<1||maximumTraceSteps<1||maximumTraceSteps>4096||
                task.IsResearchSession||!task.UsesEventClock(clock))return false;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                if(!task.TryAcquirePracticeObservationFence(clock,out fence)||fence==null)return false;
                var trace=fence.StepTrace;var current=fence.Snapshot;
                if(trace==null||trace.Count==0||trace.Count>maximumTraceSteps||fence.MutationStepsObserved!=trace.Count||
                    current==null||current.Task==null||current.Task.Phase!=Phase.Practice||current.Task.BlockIndex!=-1||
                    current.Task.Condition.HasValue||current.Selection.HasValue||current.Scalars.PracticeAttempt!=expectedAttempt||
                    !current.CurrentTrial.HasValue)return false;
                var planned=current.CurrentTrial.Value;
                if(!planned.IsPractice||planned.PracticeAttempt!=expectedAttempt||planned.BlockIndex!=-1||planned.Condition.HasValue||
                    planned.SessionId!=task.SessionId||planned.PlannedIndex!=0)return false;
                // Require the complete trace from revision 1, including every
                // step and reference link, and identify its last transaction.
                ViveSyntheticStepTrace previous=null;
                foreach(var step in trace)
                {
                    if(step==null||step.Before==null||step.After==null||step.Revision<1||step.Index<0)return false;
                    if(previous==null){if(step.Revision!=1||step.Index!=0)return false;}
                    else if(!Object.ReferenceEquals(previous.After,step.Before)||
                        !((step.Revision==previous.Revision&&step.Index==previous.Index+1)||
                          (previous.Revision<Int64.MaxValue&&step.Revision==previous.Revision+1&&step.Index==0)))return false;
                    previous=step;
                }
                if(!Object.ReferenceEquals(previous.After,current))return false;
                long revision=previous.Revision;int first=trace.Count-1;
                while(first>0&&trace[first-1].Revision==revision)first--;
                if(trace[first].Index!=0)return false;
                var initial=trace[first].Before;
                if(initial.Task==null||(initial.Task.Phase!=Phase.Familiarization&&initial.Task.Phase!=Phase.PracticeRetry)||
                    initial.Task.SessionId!=task.SessionId||initial.Task.BlockIndex!=-1||initial.Selection.HasValue)return false;
                int eventStart=initial.Scalars.EventCount,eventEnd=current.Scalars.EventCount;
                if(eventStart<0||eventStart>Int32.MaxValue-3||eventEnd!=eventStart+3)return false;
                var eventSteps=new List<ViveSyntheticStepTrace>(3);int count=eventStart;
                for(int i=first;i<trace.Count;i++)
                {
                    var step=trace[i];int before=step.Before.Scalars.EventCount,after=step.After.Scalars.EventCount;
                    if(before!=count||after<before||after-before>1)return false;
                    if(after==before+1){eventSteps.Add(step);count=after;}
                }
                if(count!=eventEnd||eventSteps.Count!=3)return false;
                var events=task.ReadEventsAfter((long)eventStart-1,4);
                if(events==null||events.Count!=3)return false;
                string session=task.SessionId,clockId=task.EventClockId;
                if(String.IsNullOrWhiteSpace(session)||String.IsNullOrWhiteSpace(clockId)||clock.ClockId!=clockId)return false;
                string[] kinds={"practice_started","phase_changed","trial_planned"};double last=-1;
                for(int i=0;i<3;i++)
                {
                    var item=events[i];var step=eventSteps[i];
                    if(item==null||item.Sequence!=(long)eventStart+i||item.Kind!=kinds[i]||item.SessionId!=session||
                        item.ClockId!=clockId||item.BlockIndex!=-1||item.Segment!=current.Task.Segment||
                        !Finite(item.Time)||item.Time<last||step.Before.Scalars.EventCount!=eventStart+i||
                        step.After.Scalars.EventCount!=eventStart+i+1)return false;
                    last=item.Time;
                }
                string attempt,from,to,list,index;
                if(events[0].Fields==null||!events[0].Fields.TryGetValue("attempt",out attempt)||attempt!=expectedAttempt.ToString(CultureInfo.InvariantCulture)||
                    eventSteps[0].After.Scalars.PracticeAttempt!=expectedAttempt||eventSteps[0].After.Task.Phase!=initial.Task.Phase||
                    events[1].Fields==null||!events[1].Fields.TryGetValue("from",out from)||from!=initial.Task.Phase.ToString()||
                    !events[1].Fields.TryGetValue("to",out to)||to!=Phase.Practice.ToString()||eventSteps[1].After.Task.Phase!=Phase.Practice||
                    events[2].Fields==null||!events[2].Fields.TryGetValue("list",out list)||list!=planned.ListId||
                    !events[2].Fields.TryGetValue("planned_index",out index)||index!="0"||events[2].TrialId!=planned.TrialId||
                    !eventSteps[2].After.CurrentTrial.HasValue||!eventSteps[2].After.CurrentTrial.Value.Equals(planned))return false;
                // ClockId can itself invoke caller code; consume the same fence
                // afterwards so any task action, even ABA, invalidates the copy.
                if(clock.ClockId!=clockId||!task.TryConsumePracticeObservationFence(fence))return false;
                fence=null;batch=new VivePracticeStartBatch(session,clockId,expectedAttempt,revision,events);return true;
            }
            catch(Exception){return false;}
            finally{if(fence!=null)task.AbandonPracticeObservationFence(fence);}
        }
    }
}
