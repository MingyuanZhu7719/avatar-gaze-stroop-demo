using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Only an explicitly built controlled development player exposes this
    // choice. Availability does not authorize a file, a worker or research
    // collection; the installed path still requires its live source and UI checks.
    public static class ViveNamedRecordingUiAdmission
    {
        public static bool RecordingAvailable {get{return ViveControlledRecordingBuildPolicy.Available;}}
        public static ViveNamedRecordingIntent Create(Actor actor,string identity,bool taskRequested,bool eyeRequested)
        {
            var intent=ViveNamedRecordingIntent.Select(actor,identity,taskRequested,eyeRequested);
            if((intent.TaskRequested||intent.EyeRequested)&&!RecordingAvailable)
                throw new InvalidOperationException("Session recording is not yet available; no recording session was created.");
            return intent;
        }
    }
}
