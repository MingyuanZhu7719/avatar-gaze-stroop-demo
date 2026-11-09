using UnityEngine;

namespace AvatarStroop.Unity
{
    // Render callback on an actual panel element. This is a software witness,
    // never proof that either headset eye displayed or the wearer saw it.
    public sealed class ViveRawPanelRenderWitness : MonoBehaviour
    {
        WorldSpaceExperimentView owner;
        int element;
        internal void Bind(WorldSpaceExperimentView view,int bit){owner=view;element=bit;}
        void OnWillRenderObject(){if(owner!=null)owner.ObserveViveRawPanelElement(Camera.current,element);}
    }
}
