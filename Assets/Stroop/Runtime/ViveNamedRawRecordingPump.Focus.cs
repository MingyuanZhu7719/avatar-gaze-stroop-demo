using System.Threading;

namespace AvatarStroop.Runtime
{
    internal sealed partial class ViveNamedRawRecordingPump
    {
        bool focusStateKnown,lastDesktopFocused,lastXrFocused;

        // Called at a known focus transition, before the next status/frame
        // preparation. It abandons no completed task mutation or published
        // event, and cannot turn a genuine failed reservation into success.
        internal bool TryAbandonPresentationForFocus(ViveNamedSessionFrameAdmission.PresentationReservation token)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||Volatile.Read(ref terminal)!=0||
                Volatile.Read(ref finishRequested)!=0||!admission.TryAbandonPresentationForFocus(token))
            {Fail();return false;}
            stagedEpoch=null;
            return true;
        }

        // Requires the same genuine, healthy and fresh eye/head frame as any
        // ordinary raw task mutation. Desktop focus is metadata; XR focus loss
        // holds the task and interrupts its trial without stopping the writer.
        // Regaining focus never resumes, confirms choices or starts capture.
        internal bool ProcessFocusState(bool desktopFocused,bool xrFocused)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||statusRun==null||stagedEpoch==null||
                Volatile.Read(ref terminal)!=0||Volatile.Read(ref finishRequested)!=0||!VerifyStagedEpoch())
            {Fail();return false;}
            if(focusStateKnown&&lastDesktopFocused==desktopFocused&&lastXrFocused==xrFocused)return true;
            bool baseline=!focusStateKnown;
            if(!TryRunReservedOrdinaryMutation(()=>participant.RecordFocusState(desktopFocused,xrFocused,baseline)))return false;
            focusStateKnown=true;lastDesktopFocused=desktopFocused;lastXrFocused=xrFocused;
            return true;
        }
    }
}
