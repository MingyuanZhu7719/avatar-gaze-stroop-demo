using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // The VIVE development task has no authenticated WoZ transport or
    // phase-bound live presentation proof. Never treat a pose's flags, source
    // names, timestamps or caller-supplied booleans as such a proof. This
    // current boundary permits only its existing automatic output; it must be
    // replaced with a separately reviewed authenticated boundary before any
    // live output can be presented.
    public static class ViveWozPresentationPolicy
    {
        public static bool IsAutomaticOnly(AvatarOutput output)
        {
            if(output==null||output.SelectedSource!=ControlSource.Automatic||
                output.ActualSource!=ControlSource.Automatic||
                output.Status!=GazeOutputStatus.Automatic||output.InputSample!=null)return false;
            if(!output.AvatarVisible)return true;
            return output.ActualPose.HasValue&&output.HeadValid&&output.EyeValid&&
                output.ActualPose.Value.HeadRotation.IsUnit&&output.ActualPose.Value.EyeDirection.IsUnit;
        }

        // Null includes normal no-avatar and pre-block states. A blocked
        // external value is not proof of a connection or a disconnection.
        public static string BlockedFeedback(AvatarOutput output)
        {
            if(output==null||IsAutomaticOnly(output))return null;
            if(output.SelectedSource!=ControlSource.Automatic||
                (output.ActualSource.HasValue&&output.ActualSource.Value!=ControlSource.Automatic)||
                output.InputSample!=null)
                return "External avatar control is unavailable; preview avatar cleared.";
            return "Avatar pose is unavailable or invalid; preview avatar cleared.";
        }
    }
}
