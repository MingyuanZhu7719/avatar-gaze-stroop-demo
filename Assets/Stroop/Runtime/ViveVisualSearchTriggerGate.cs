namespace AvatarStroop.Runtime
{
    // An object selection may advance to correction or ratings while the
    // physical trigger is still held. Require release before any new confirm.
    public sealed class ViveVisualSearchTriggerGate
    {
        bool consumed;
        public bool AllowAuxiliary(bool held,bool searchPressConsumed)
        {
            if(!held)consumed=false;
            else if(searchPressConsumed)consumed=true;
            return !consumed;
        }
    }
}
