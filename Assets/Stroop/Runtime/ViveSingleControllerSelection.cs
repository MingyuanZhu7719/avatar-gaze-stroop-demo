namespace AvatarStroop.Runtime
{
    // Lifecycle-local identity guard. It never guesses between devices and never
    // transfers an established session to a different device or role.
    public sealed class ViveSingleControllerSelection
    {
        int selectedId,selectedRole;
        bool selected,identityChanged;
        public string Status { get; private set; } = "Waiting for one VIVE controller.";
        public bool Observe(int connectedCount,int deviceId,int role)
        {
            if(identityChanged){Status="Controller identity changed. Restart the view before continuing.";return false;}
            if(connectedCount!=1){Status=connectedCount==0?"Connect one VIVE controller.":"More than one VIVE controller connected. Keep only one connected.";return false;}
            if(deviceId<=0||(role!=1&&role!=2)){Status="Controller role unavailable. Check SteamVR controller assignment.";return false;}
            if(selected&&(selectedId!=deviceId||selectedRole!=role))
            {identityChanged=true;Status="Controller identity changed. Restart the view before continuing.";return false;}
            selected=true;selectedId=deviceId;selectedRole=role;
            Status="Controller connected ("+(role==1?"Left":"Right")+").";return true;
        }
    }
}
