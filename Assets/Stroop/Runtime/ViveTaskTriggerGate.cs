namespace AvatarStroop.Runtime
{
    // A physical press belongs to one context, even if selection or recovery
    // changes the task phase before this owner update has finished.
    public sealed class ViveTaskTriggerGate
    {
        bool consumed;
        public bool TakeConfirmationPress(bool inputReady,bool held,bool down,bool usedByAnotherAction)
        {
            if(!held){consumed=false;return false;}
            if(consumed)return false;
            consumed=true;
            return inputReady&&down&&!usedByAnotherAction;
        }
    }
}
