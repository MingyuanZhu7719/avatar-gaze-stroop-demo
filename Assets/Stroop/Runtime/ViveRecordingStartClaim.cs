using System;
using System.Threading;
namespace AvatarStroop.Runtime
{
    // Same implementation for the scene owner's one-use start claim and
    // synthetic lifecycle tests. No path, writer or permission is granted here.
    internal sealed class ViveRecordingStartClaim
    {
        internal enum Result { Waiting, Invalid, Attempted }
        internal readonly ViveProEyeWorker Worker;
        internal readonly long Generation;
        readonly bool taskOnly;
        bool consumed;
        internal ViveRecordingStartClaim(ViveProEyeWorker worker):this(worker,false){}
        internal ViveRecordingStartClaim(ViveProEyeWorker worker,bool taskOnly)
        {if(worker==null)throw new ArgumentNullException("worker");Worker=worker;Generation=worker.RecordingStartGeneration;this.taskOnly=taskOnly;}
        internal Result TryExecute(Func<bool> exactContext,Func<bool> currentInputs,Action attempt)
        {
            if(exactContext==null||currentInputs==null||attempt==null)throw new ArgumentNullException();
            if(consumed)return Result.Invalid;
            if(!Monitor.TryEnter(Worker.RecordingTicketGate))return Result.Waiting;
            try
            {
                if(consumed||Generation!=Worker.RecordingStartGeneration||!exactContext())
                {consumed=true;return Result.Invalid;}
                if(!(taskOnly?Worker.IsTaskOnlyRecordingRun:Worker.ReadRecordingStartReadiness().Ready)||!currentInputs())return Result.Waiting;
                // Even a throwing/failed install cannot be replayed. This
                // reports only an attempt, never file creation or success.
                consumed=true;attempt();return Result.Attempted;
            }
            finally{Monitor.Exit(Worker.RecordingTicketGate);}
        }
    }
}
