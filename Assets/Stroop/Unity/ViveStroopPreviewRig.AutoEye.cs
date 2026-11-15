#if STROOP_XR_PREVIEW
using UnityEngine;

namespace AvatarStroop.Unity
{
    // One automatic diagnostic attempt per explicit classroom lifecycle.
    // This state carries no participant selection, consent, or storage power.
    internal sealed class ViveAutoEyeViewStart
    {
        enum State { Idle, Pending, Consumed, Suppressed }
        State state;
        internal bool Pending {get{return state==State.Pending;}}
        internal bool BeginExplicitView()
        {
            if(state!=State.Idle)return false;
            state=State.Pending;return true;
        }
        internal bool TryClaim(bool loaderRunning,bool currentAlignedHead,bool focused,bool viewClockAvailable,bool serviceAllowed)
        {
            if(state!=State.Pending||!loaderRunning||!currentAlignedHead||!focused||!viewClockAvailable||!serviceAllowed)
                return false;
            // Commit before calling native initialization, even if it throws.
            state=State.Consumed;return true;
        }
        internal void Suppress(){if(state==State.Pending)state=State.Suppressed;}
        internal void EndView(){state=State.Idle;}
    }

    public sealed partial class ViveStroopPreviewRig
    {
        readonly ViveAutoEyeViewStart autoEyeForView=new ViveAutoEyeViewStart();
        internal bool AutoEyeStartPending {get{return autoEyeForView.Pending;}}

        bool EyeDiagnosticStartAllowed(bool allowChoicePrompts)
        {
            return running&&headReady&&clock!=null&&worker==null&&!stopping&&!restartBlocked&&!quitRequested&&!RawInstallFailure&&
                (development==null||(!development.RecordingBlocked&&(allowChoicePrompts||!development.PreSessionChoiceActive)));
        }

        bool TryStartAutomaticEyeDiagnostic()
        {
            if(!autoEyeForView.Pending)return false;
            if(!autoEyeForView.TryClaim(running&&display!=null&&display.running&&input!=null&&input.running,
                alignment!=null&&alignment.Applied&&!referenceChanged&&headReady&&headReadyFrame==Time.frameCount,
                XrFocused,clock!=null,EyeDiagnosticStartAllowed(true)))return false;
            // Uses the same view clock, provider, and diagnostic source as the
            // original manual service. No call here can admit recording.
            StartEyeDiagnostic();
            return true;
        }
    }
}
#endif
