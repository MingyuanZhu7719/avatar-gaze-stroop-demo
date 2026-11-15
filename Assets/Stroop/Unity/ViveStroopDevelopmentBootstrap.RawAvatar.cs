#if STROOP_XR_PREVIEW
using UnityEngine;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        bool OwnsRawAutomaticAvatar
        {
            get
            {
                return namedRawPump!=null&&participant!=null&&!participant.LegacyWozRecordingDeclared&&legacyWozScene==null;
            }
        }
        internal bool PrepareRawAutomaticAvatar()
        {
            // The live operator scene owns its existing verified pose pipeline.
            if(!OwnsRawAutomaticAvatar)return true;
            if(view==null||participant.CurrentTrial==null)return false;
            var expected=participant.ParticipantView;
            bool visible=expected.Phase==Phase.MainTask&&expected.OperatorAvatarVisible;
            AvatarOutput output=visible?participant.CurrentAvatar:null;
            if(visible&&(!ViveWozPresentationPolicy.IsAutomaticOnly(output)||!output.AvatarVisible))return false;
            return view.PrepareRawAutomaticAvatar(expected,output);
        }
        internal bool RawAutomaticAvatarCurrent(Camera camera=null)
        {
            return !OwnsRawAutomaticAvatar||view!=null&&participant.CurrentTrial!=null&&
                view.RawAutomaticAvatarCurrent(participant.ParticipantView,camera);
        }
        // Optional software evidence, never a prerequisite when a participant
        // looks away. It does not establish physical visibility.
        internal bool RawAutomaticAvatarRendered()
        {
            return OwnsRawAutomaticAvatar&&view!=null&&participant.CurrentTrial!=null&&
                view.RawAutomaticAvatarRendered(participant.ParticipantView);
        }
        internal void ClearRawAutomaticAvatar()
        {
            if(view!=null)view.ClearRawAutomaticAvatar();
        }
    }
}
#endif
