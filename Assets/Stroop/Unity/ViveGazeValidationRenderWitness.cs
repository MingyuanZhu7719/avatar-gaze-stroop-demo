#if STROOP_XR_PREVIEW
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Attached to the actual target renderer; a camera pass alone is not evidence
    // that the target was submitted for that camera. This is not photon timing.
    public sealed class ViveGazeValidationRenderWitness : MonoBehaviour
    {
        [SerializeField] ViveGazeValidationView owner;
        public void Bind(ViveGazeValidationView value){owner=value;}
        void OnWillRenderObject(){if(owner!=null)owner.ObserveMarkerRender(Camera.current);}
    }
}
#endif
