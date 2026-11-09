namespace AvatarStroop.Runtime
{
    // One view-session startup gate, not study consent or eye calibration.
    // Unsupported presence and an initial/default true never prove headset wear.
    public sealed class ViveStartupAlignmentGate
    {
        bool sawVerifiedAbsence,manualConfirmed,neutral;
        int lastFrame=-1;
        public bool AutomaticWearVerified {get;private set;}
        public bool KnownAbsent {get;private set;}
        public bool ManualConfirmed {get{return manualConfirmed;}}
        public void Reset()
        {sawVerifiedAbsence=false;AutomaticWearVerified=false;KnownAbsent=false;ResetConfirmation();lastFrame=-1;}
        void ResetConfirmation(){manualConfirmed=false;neutral=false;}
        public bool Observe(int frame,bool sessionRunning,bool poseFocused,bool capabilityVerified,bool present,
            bool promptSeen,bool controllerReady,bool deviceChanged,bool triggerHeld,bool trackpadHeld,bool menuHeld)
        {
            if(frame<0||frame<=lastFrame)return false;
            lastFrame=frame;
            if(!sessionRunning){Reset();return false;}
            if(!capabilityVerified)sawVerifiedAbsence=false;
            if(capabilityVerified&&!present)sawVerifiedAbsence=true;
            KnownAbsent=capabilityVerified&&!present;
            AutomaticWearVerified=capabilityVerified&&present&&sawVerifiedAbsence;
            if(!poseFocused||KnownAbsent){ResetConfirmation();return false;}
            if(AutomaticWearVerified){ResetConfirmation();return true;}
            if(!controllerReady||deviceChanged||(!manualConfirmed&&!promptSeen)){ResetConfirmation();return false;}
            if(!triggerHeld&&!trackpadHeld&&!menuHeld)neutral=true;
            if(trackpadHeld||menuHeld){neutral=false;manualConfirmed=false;return false;}
            if(neutral&&triggerHeld){manualConfirmed=true;neutral=false;}
            return manualConfirmed;
        }
    }
}
