using System;
using System.Collections.Generic;

namespace AvatarStroop.Core
{
    // Delivery state only; not an emergency-stop hardware guarantee or remote-resume channel.
    public sealed class SafetyStopRequestState
    {
        public string RequestId {get;private set;}
        public bool Pending {get;private set;}
        public bool Acknowledged {get;private set;}
        public string Request()
        {
            if(Pending)return RequestId;
            RequestId=Guid.NewGuid().ToString("N");Pending=true;Acknowledged=false;return RequestId;
        }
        public bool Acknowledge(string requestId)
        {
            if(!Pending||RequestId!=requestId)return false;
            Pending=false;Acknowledged=true;return true;
        }
    }
    public sealed class SafetyStopInbox
    {
        readonly HashSet<string> handled=new HashSet<string>();
        public int AppliedCount {get{return handled.Count;}}
        public bool ApplyOnce(string requestId,Action stop)
        {
            if(String.IsNullOrWhiteSpace(requestId)||stop==null)throw new ArgumentException("Stop identity and handler required");
            if(handled.Contains(requestId))return false;
            stop();handled.Add(requestId);return true;
        }
    }
}
