#if STROOP_XR_PREVIEW
using System;
using System.Globalization;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        ViveWozPhotonSession easyWozLobby;
        ViveDevelopmentParticipant easyWozParticipant;
        object easyWozSession;
        string easyWozRoomCode="",easyWozServiceFailure="";
        string easyWozTiming="";
        double nextEasyWozTiming;
        double easyWozDrainUntil=-1;
        bool manualWozPairing,easyWozRecorded,easyWozAvatarShared;
        bool easyWozOfferQueued,easyWozOfferPublished,easyWozStartNotified,easyWozAbortPending;

        // Service the room even when guarded rig Poll returns for tracking or
        // focus. Actual source mutations remain in PollEasyWozPairing below.
        void Update()
        {
            var room=easyWozLobby;if(room==null)return;
            if(legacyWozProcess!=null&&legacyWozProcess.Terminal)DrainStoppedEasyWozPairing();
            try
            {
                room.Service();
                CaptureEasyWozTiming();
                if(easyWozDrainUntil>=0&&(room.Terminal||Time.realtimeSinceStartupAsDouble>=easyWozDrainUntil))CloseEasyWozPairing();
            }
            catch(Exception e){easyWozServiceFailure=e.Message;}
        }

        bool CanWaitForEasyWoz()
        {
            return isActiveAndEnabled&&configured&&!legacyWozClosed&&!manualWozPairing&&
                easyWozLobby==null&&legacyWozProcess==null&&pendingLegacyWozOffer==null&&!easyWozAbortPending&&
                participant!=null&&!participant.HasNamedOption&&participant.Phase==Phase.BlockPreparation&&sharedClock!=null&&
                avatarChoice.Answered(AvatarChoiceSession)&&participant.InputReady&&rig!=null&&rig.HeadReady&&rig.XrFocused&&
                !PresentationBlocked&&recording==null&&(participant.RawRecordingRequested?RecordedLegacyWozReady:namedRawPump==null)&&
                !String.IsNullOrWhiteSpace(easyWozRoomCode);
        }
        bool EasyWozSessionCurrent()
        {
            return isActiveAndEnabled&&configured&&!legacyWozClosed&&!manualWozPairing&&legacyWozScope&&legacyWozPlaintext&&
                object.ReferenceEquals(easyWozParticipant,participant)&&object.ReferenceEquals(easyWozSession,AvatarChoiceSession)&&
                avatarChoice.Answered(easyWozSession)&&avatarChoice.Granted(easyWozSession)==easyWozAvatarShared&&
                RecordedLegacyWoz==easyWozRecorded&&participant!=null&&
                (legacyWozStarted||participant.Phase==Phase.BlockPreparation);
        }
        void BeginEasyWozPairing()
        {
            if(!CanWaitForEasyWoz())return;
            try
            {
                string appId,region,issue;
                if(!ViveWozPhotonConfiguration.TryLoad(out appId,out region,out issue))throw new InvalidOperationException(issue);
                // This explicit button accepts the adjacent scope/cloud notice.
                // Wearer avatar and recording choices remain independently owned.
                legacyWozScope=true;legacyWozPlaintext=true;
                easyWozParticipant=participant;easyWozSession=AvatarChoiceSession;
                easyWozRecorded=RecordedLegacyWoz;easyWozAvatarShared=avatarChoice.Granted(easyWozSession);
                easyWozServiceFailure="";
                easyWozTiming="";nextEasyWozTiming=0;
                easyWozLobby=ViveWozPhotonSession.JoinParticipant(appId,region,sharedClock,easyWozRoomCode.Trim().ToUpperInvariant());
                legacyWozStatus="Joining the experimenter's Photon room. No task source is active yet.";
            }
            catch(Exception e)
            {
                CloseEasyWozPairing();legacyWozScope=false;legacyWozPlaintext=false;
                legacyWozStatus="Could not join the room: "+e.Message;
            }
        }
        void CloseEasyWozPairing()
        {
            CaptureEasyWozTiming(true);
            if(easyWozOfferQueued)pendingLegacyWozOffer=null;
            var lobby=easyWozLobby;easyWozLobby=null;
            easyWozParticipant=null;easyWozSession=null;easyWozServiceFailure="";easyWozDrainUntil=-1;
            easyWozOfferQueued=false;easyWozOfferPublished=false;easyWozStartNotified=false;easyWozAbortPending=false;
            if(lobby!=null)lobby.Dispose();
        }
        void CaptureEasyWozTiming(bool force=false)
        {
            var room=easyWozLobby;if(room==null)return;
            double now=Time.realtimeSinceStartupAsDouble;
            if(!force&&now<nextEasyWozTiming)return;
            nextEasyWozTiming=now+1.0;
            long rtt=room.RoundTripMilliseconds;
            easyWozTiming="Photon region: "+room.Region+" | PC-to-Photon server RTT: "+
                (rtt>0?rtt.ToString(CultureInfo.InvariantCulture)+" ms":"pending")+
                "\nTiming profile: "+room.TimingProfile+" | Task/reply window: "+
                (room.NetworkVisualBudgetSeconds*1000).ToString("0",CultureInfo.InvariantCulture)+" ms"+
                "\nControl proof window: "+(room.ControlProofBudgetSeconds*1000).ToString("0",CultureInfo.InvariantCulture)+" ms"+
                "\nCapture age limit: "+(room.LocalCaptureBudgetSeconds*1000).ToString("0",CultureInfo.InvariantCulture)+
                " ms | Received pose age limit: "+(room.LocalReceiptBudgetSeconds*1000).ToString("0",CultureInfo.InvariantCulture)+
                " ms. RTT is not headset display latency.";
        }
        void DrainStoppedEasyWozPairing()
        {
            // Only the stopped channel's final acknowledgment may drain. No
            // offer, owner or source is created again during this grace period.
            if(easyWozLobby!=null&&easyWozDrainUntil<0)
                easyWozDrainUntil=Time.realtimeSinceStartupAsDouble+1.0;
        }
        void CancelEasyWozPairing()
        {
            bool sourceCreated=legacyWozClosed||legacyWozProcess!=null||legacyWozTaskOwner!=null;
            CloseEasyWozPairing();legacyWozScope=false;legacyWozPlaintext=false;
            // Leave immediately. If the real owner already exists,
            // close it at the next guarded owner frame instead of reusing it.
            easyWozAbortPending=sourceCreated;
            legacyWozStatus=sourceCreated?"Pairing canceled. Closing this source; start a new session to pair again.":
                "Pairing canceled. No WoZ source was created.";
        }
        void FailEasyWozPairing(string detail)
        {
            bool sourceCreated=legacyWozClosed||legacyWozProcess!=null||legacyWozTaskOwner!=null;
            if(sourceCreated){CloseLegacyWozTaskBinding();legacyWozClosed=true;}
            else CloseEasyWozPairing();
            legacyWozScope=false;legacyWozPlaintext=false;
            legacyWozStatus="Connection stopped: "+detail+(sourceCreated?". Start a new session to pair again.":". Check the room code and try again.");
        }
        // Called only from the same guarded Poll stage as the old offer factory.
        // Room service only publishes state; it never mutates the task owner.
        bool PollEasyWozPairing()
        {
            if(easyWozAbortPending)
            {
                easyWozAbortPending=false;CloseLegacyWozTaskBinding();legacyWozClosed=true;
                legacyWozStatus="Pairing canceled. Start a new session to pair again.";return true;
            }
            var lobby=easyWozLobby;
            if(lobby==null)return false;
            if(easyWozDrainUntil>=0)return false;
            try
            {
                if(!EasyWozSessionCurrent())
                {FailEasyWozPairing("the participant session or local choices changed");return true;}
                if(!String.IsNullOrEmpty(easyWozServiceFailure)||!String.IsNullOrEmpty(lobby.FailureReason)||lobby.Terminal)
                {FailEasyWozPairing(!String.IsNullOrEmpty(easyWozServiceFailure)?easyWozServiceFailure:lobby.FailureReason??"the room closed");return true;}
                if(lobby.ReadyForOffer&&legacyWozProcess==null&&!easyWozOfferQueued)
                {
                    if(!lobby.PeerConnected||!lobby.PeerConfirmed)return false;
                    string clock=lobby.OperatorClockId;
                    easyWozOfferQueued=true;
                    pendingLegacyWozOffer=()=>
                    {
                        easyWozOfferQueued=false;
                        if(!object.ReferenceEquals(easyWozLobby,lobby)||!EasyWozSessionCurrent()||!lobby.ReadyForOffer)
                        {FailEasyWozPairing("the approved connection expired before the source could be created");return;}
                        try
                        {
                            legacyWozOperatorClock=clock;legacyWozMaximumAge="2";
                            CreateLegacyWozOffer();
                            // The secret offer goes only to the accepted peer,
                            // never into room properties or this room-code UI.
                            lobby.PublishOffer(legacyWozOffer);legacyWozOffer="";easyWozOfferPublished=true;
                            legacyWozStatus="Room paired. Waiting for the experimenter's forwarding permission.";
                        }
                        catch(Exception e){FailEasyWozPairing(e.Message);}
                    };
                }
                if(easyWozOfferPublished&&lobby.OperatorStarted&&!legacyWozStarted)
                {
                    StartLegacyWoz();
                    if(!legacyWozStarted)return true;
                    lobby.MarkParticipantStarted();easyWozStartNotified=true;
                }
                // The room carries the live protocol; setup completion must not close it.
                if(easyWozStartNotified&&lobby.Complete)legacyWozStatus="Connected through Photon. Waiting for current tracking and block readiness.";
            }
            catch(Exception e){FailEasyWozPairing(e.Message);return true;}
            return false;
        }
        void DrawEasyWozPairingControls()
        {
            if(legacyWozClosed||legacyWozStarted||participant==null||participant.Phase!=Phase.BlockPreparation)return;
            if(legacyWozProcess==null&&pendingLegacyWozOffer==null&&easyWozLobby==null&&!easyWozAbortPending)
            {
                bool entered=GUILayout.Toggle(manualWozPairing,"Advanced: manual LAN connection");
                if(entered!=manualWozPairing)
                {manualWozPairing=entered;legacyWozScope=false;legacyWozPlaintext=false;}
            }
            if(manualWozPairing)return;
            var lobby=easyWozLobby;
            if(lobby==null)
            {
                GUILayout.Label(RecordedLegacyWoz?
                    "Join the experimenter's room to use LiveWoZ for all four recorded blocks. This computer also saves mapped operator poses and source times.":
                    "Join the experimenter's room to use LiveWoZ for all four blocks. This preview does not record task or eye files.",recordingWrap);
                GUILayout.Label("Photon relays task state and permitted live head/gaze over an encrypted internet connection. Answers, ratings and raw eye files stay on this computer. Joining does not change your headset sharing or recording choices.",recordingWrap);
                easyWozRoomCode=WozInput("Room code from experimenter",easyWozRoomCode);
                bool old=GUI.enabled;GUI.enabled=old&&CanWaitForEasyWoz();
                if(GUILayout.Button("Join room"))BeginEasyWozPairing();
                GUI.enabled=old;return;
            }
            if(lobby.RoomReady)GUILayout.Label("Room: "+lobby.RoomCode,recordingWrap);
            GUILayout.Label(lobby.Status,recordingWrap);
            if(lobby.RoomReady&&!lobby.PeerConfirmed)GUILayout.Label("Tell the experimenter this room code. They will confirm the participant before connection details are exchanged.",recordingWrap);
            if(GUILayout.Button("Leave room"))CancelEasyWozPairing();
        }
    }
}
#endif
