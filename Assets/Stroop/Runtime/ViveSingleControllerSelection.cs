namespace AvatarStroop.Runtime
{
    // Lifecycle-local identity guard. It never guesses between devices and never
    // transfers an established session to a different device or role.
    public sealed class ViveSingleControllerSelection
    {
        int selectedId,selectedRole;
        bool selected,identityChanged;
        public bool IsCommitted {get{return selected;}}
        public int SelectedDeviceId {get{return selected?selectedId:0;}}
        public int SelectedRole {get{return selected?selectedRole:0;}}
        public string Status { get; private set; } = "Waiting for one VIVE controller.";
        // Candidate inspection cannot establish first ownership. In particular,
        // an untracked logical node exposed during XR startup is not a source.
        // Once established, any observed replacement still ends this lifecycle.
        public bool Inspect(int connectedCount,int deviceId,int role)
        {
            if(identityChanged){Status="Controller identity changed. Restart the view before continuing.";return false;}
            if(connectedCount!=1){Status=connectedCount==0?"Connect one VIVE controller.":"More than one VIVE controller connected. Keep only one connected.";return false;}
            if(deviceId<=0||(role!=1&&role!=2)){Status="Controller role unavailable. Check SteamVR controller assignment.";return false;}
            if(selected&&(selectedId!=deviceId||selectedRole!=role))
            {identityChanged=true;Status="Controller identity changed. Restart the view before continuing.";return false;}
            Status=selected?"Controller connected ("+(role==1?"Left":"Right")+").":
                "Controller candidate ("+(role==1?"Left":"Right")+"); waiting for focused, valid tracking.";
            return true;
        }
        // The physical adapter calls this only after the same candidate's
        // bound controls, focused headset and current pose have all passed.
        public bool Commit(int connectedCount,int deviceId,int role)
        {
            if(!Inspect(connectedCount,deviceId,role))return false;
            selected=true;selectedId=deviceId;selectedRole=role;
            Status="Controller connected ("+(role==1?"Left":"Right")+").";return true;
        }
        // Compatibility for existing pure policy callers that already supply
        // a validated identity. This does not itself verify physical tracking.
        public bool Observe(int connectedCount,int deviceId,int role)
        {return Commit(connectedCount,deviceId,role);}
    }
}
