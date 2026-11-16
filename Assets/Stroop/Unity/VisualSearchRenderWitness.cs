using UnityEngine;

namespace AvatarStroop.Unity
{
    // The search array is acknowledged only after the actual cue, item-colour
    // and active pattern renderers are culled in by the participant camera.
    [ExecuteAlways]
    public sealed class VisualSearchRenderWitness : MonoBehaviour
    {
        [SerializeField] WorldSpaceExperimentView owner;
        [SerializeField] int elementIndex=-1;
        public void Bind(WorldSpaceExperimentView presentation,int index){owner=presentation;elementIndex=index;}
        void OnWillRenderObject(){if(owner!=null)owner.ObserveVisualSearchRender(Camera.current,elementIndex);}
    }
}
