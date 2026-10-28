using UnityEngine;

namespace AvatarStroop.Unity
{
    // OnWillRenderObject belongs on the actual stimulus renderer: a camera callback
    // alone would also fire when the stimulus was hidden, culled, or on another layer.
    public sealed class StimulusRenderWitness : MonoBehaviour
    {
        [SerializeField] WorldSpaceExperimentView owner;
        public void Bind(WorldSpaceExperimentView presentation) { owner = presentation; }
        void OnWillRenderObject()
        {
            if(owner != null) owner.ObserveStimulusRender(Camera.current);
        }
    }
}
