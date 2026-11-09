using System;

namespace AvatarStroop.Runtime
{
    // Transient, local development recording choice. It neither identifies a
    // participant nor represents research consent or permission to write.
    internal sealed class VivePreSessionRecordingChoice
    {
        internal enum Step { Task, Eye, Confirmed, Declined, Cancelled }
        readonly bool requestedTask,requestedEye;
        bool taskAccepted,eyeAccepted,armed;
        internal Step Current {get;private set;}
        internal int Revision {get;private set;}
        internal VivePreSessionRecordingChoice(bool task,bool eye)
        {
            if(!task&&!eye)throw new ArgumentException("At least one recording request is required.");
            requestedTask=task;requestedEye=eye;Current=task?Step.Task:Step.Eye;
        }
        internal bool TaskSelectedInRehearsal {get{return Current==Step.Confirmed&&requestedTask&&taskAccepted;}}
        internal bool EyeSelectedInRehearsal {get{return Current==Step.Confirmed&&requestedEye&&eyeAccepted;}}
        internal bool IsTerminal {get{return Current==Step.Confirmed||Current==Step.Declined||Current==Step.Cancelled;}}
        // A fresh neutral frame is needed for each prompt. Losing focus or
        // tracking invalidates *all* choices, including a prior acceptance.
        internal void Observe(bool physicallyReady,bool promptVisible,bool triggerHeld,bool menuHeld,bool trackpadHeld,bool changedDevice)
        {
            if(Current==Step.Confirmed&&(!physicallyReady||changedDevice)){Cancel();return;}
            if(IsTerminal)return;
            if(!physicallyReady||changedDevice){Reset();return;}
            if(!promptVisible){armed=false;return;}
            if(!armed)
            {if(!triggerHeld&&!menuHeld&&!trackpadHeld)armed=true;return;}
            if(menuHeld){Current=Step.Declined;armed=false;Revision++;return;}
            if(!triggerHeld)return;
            armed=false;
            if(Current==Step.Task)
            {taskAccepted=true;Current=requestedEye?Step.Eye:Step.Confirmed;}
            else if(Current==Step.Eye)
            {eyeAccepted=true;Current=Step.Confirmed;}
            Revision++;
        }
        internal void Reset()
        {
            if(IsTerminal)return;
            if(!taskAccepted&&!eyeAccepted&&!armed&&Current==(requestedTask?Step.Task:Step.Eye))return;
            taskAccepted=false;eyeAccepted=false;armed=false;
            Current=requestedTask?Step.Task:Step.Eye;Revision++;
        }
        internal void Cancel(){taskAccepted=false;eyeAccepted=false;armed=false;Current=Step.Cancelled;Revision++;}
    }
}
