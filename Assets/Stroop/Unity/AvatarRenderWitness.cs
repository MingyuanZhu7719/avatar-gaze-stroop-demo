using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed class AvatarRenderWitness : MonoBehaviour
    {
        [SerializeField] WorldSpaceExperimentView owner;
        public void Bind(WorldSpaceExperimentView presentation) { owner = presentation; }
        void OnWillRenderObject()
        {
            if(owner != null) owner.ObserveAvatarRender(Camera.current);
        }
    }
}
