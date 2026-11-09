namespace AvatarStroop.Runtime
{
    // Dedicated safety-only menu gate. A held menu on entry, focus regain or
    // a replacement device is never interpreted as a new pause.
    public sealed class ViveStroopSafetyMenuGate
    {
        bool armed,previous;
        public void Reset(){armed=false;previous=false;}
        public bool Observe(bool ready,bool deviceChanged,bool held)
        {
            if(!ready){Reset();return false;}
            if(deviceChanged)Reset();
            if(!armed){if(!held)armed=true;previous=held;return false;}
            bool down=held&&!previous;previous=held;return down;
        }
    }
}
