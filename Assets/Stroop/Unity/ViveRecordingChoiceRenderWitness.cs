#if STROOP_XR_PREVIEW
using UnityEngine;
namespace AvatarStroop.Unity
{
    // A Unity renderer proxy only; never evidence of wearer identity or reading.
    public sealed class ViveRecordingChoiceRenderWitness : MonoBehaviour
    {
        WorldSpaceExperimentView owner;int element;
        internal void Bind(WorldSpaceExperimentView view,int bit){owner=view;element=bit;}
        void OnWillRenderObject(){if(owner!=null)owner.ObserveViveRecordingChoiceElement(Camera.current,element);}
    }
}
#endif
