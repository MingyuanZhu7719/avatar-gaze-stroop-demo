#if STROOP_XR_PREVIEW
using System;
using System.Globalization;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveWozOperatorDevelopmentBootstrap
    {
        ViveWozPhotonSession easyPairing;
        bool showManualPairing,easyOfferReceived,easyPeerConfirmed;
        string easyPairingIssue="";
        string easyPairingTiming="";
        double nextEasyPairingTiming;
        double easyPairingDrainUntil=-1;

        void StopEasyPairing()
        {
            CaptureEasyPairingTiming(true);
            var previous=easyPairing;easyPairing=null;
            easyPeerConfirmed=false;easyPairingDrainUntil=-1;
            if(previous!=null)previous.Dispose();
        }
        void CaptureEasyPairingTiming(bool force=false)
        {
            var room=easyPairing;if(room==null)return;
            double now=Time.realtimeSinceStartupAsDouble;
            if(!force&&now<nextEasyPairingTiming)return;
            nextEasyPairingTiming=now+1.0;
            long rtt=room.RoundTripMilliseconds;
            easyPairingTiming="Photon region: "+room.Region+" | PC-to-Photon server RTT: "+
                (rtt>0?rtt.ToString(CultureInfo.InvariantCulture)+" ms":"pending")+
                "\nTiming profile: "+room.TimingProfile+" | Task/reply window: "+
                (room.NetworkVisualBudgetSeconds*1000).ToString("0",CultureInfo.InvariantCulture)+" ms"+
                "\nControl proof window: "+(room.ControlProofBudgetSeconds*1000).ToString("0",CultureInfo.InvariantCulture)+" ms"+
                "\nCapture age limit: "+(room.LocalCaptureBudgetSeconds*1000).ToString("0",CultureInfo.InvariantCulture)+
                " ms | Received pose age limit: "+(room.LocalReceiptBudgetSeconds*1000).ToString("0",CultureInfo.InvariantCulture)+
                " ms. RTT is not headset display latency.";
        }
        void DrainStoppedEasyPairing()
        {
            // The process/channel is already terminal: service only the final
            // queued negative acknowledgment, with a bounded room lifetime.
            if(easyPairing!=null&&easyPairingDrainUntil<0)
                easyPairingDrainUntil=Time.realtimeSinceStartupAsDouble+1.0;
        }
        void StopUnstartedEasyPairing()
        {
            if(process!=null||easyPairing==null)return;
            bool selected=easyOfferReceived;
            StopEasyPairing();
            // A taken offer selected the participant's actual source. Clearing
            // its review cannot turn it back into a reusable setup invitation.
            if(selected){closed=true;status="Setup stopped. Reopen both apps for a new session.";}
            else status="Pairing stopped. Create a new room when ready.";
        }
        void CancelEasyPairing()
        {
            // An accepted offer has already selected an actual participant
            // source. It cannot become a different session by editing fields.
            bool accepted=easyOfferReceived;
            if(accepted){Close();status="Room closed. Reopen both apps for a new session.";}
            else{StopEasyPairing();token="";ClearOfferReview();easyPairingIssue="Room closed. You can create another room.";}
        }
        void PairingFailed(Exception error)
        {
            easyPairingIssue="Could not pair: "+error.Message;
            if(diagnostics!=null)diagnostics.Fail("PAIRING_FAILED",easyPairingIssue,error);
            bool accepted=easyOfferReceived;
            if(accepted||process!=null)Close();
            else{StopEasyPairing();token="";ClearOfferReview();}
        }
        void PollEasyPairing()
        {
            if(easyPairing==null)return;
            try
            {
                // Service before the bootstrap's process/closed early return.
                easyPairing.Service();
                CaptureEasyPairingTiming();
                if(easyPairingDrainUntil>=0)
                {
                    if(easyPairing.Terminal||Time.realtimeSinceStartupAsDouble>=easyPairingDrainUntil)StopEasyPairing();
                    return;
                }
                if(!String.IsNullOrEmpty(easyPairing.FailureReason)||easyPairing.Terminal)
                    throw new InvalidOperationException(easyPairing.FailureReason??"The Photon room closed.");
                // The room remains alive after setup to carry the live protocol.
                if(!easyPairing.OfferAvailable)return;
                string offer=easyPairing.TakeOffer();
                if(String.IsNullOrEmpty(offer))return;
                if(!easyPairing.PeerConfirmed||!easyPeerConfirmed||easyOfferReceived)
                    throw new InvalidOperationException("The selected connection is no longer current.");
                maximumAge="2";
                token=offer;ReviewOfferMode();
                if(!offerReviewed)throw new InvalidOperationException("The participant sent an invalid pairing offer.");
                easyOfferReceived=true;
                status="Participant connected to the room. Review the session permissions below.";
                if(diagnostics!=null)diagnostics.Record("PAIRING_OFFER_READY","Session offer received from the accepted Photon peer.");
            }
            catch(Exception error){PairingFailed(error);}
        }
        void DrawEasyPairingForm()
        {
            GUILayout.Label("Create a room, then enter its code on the participant computer.",textStyle);
            GUILayout.Label("Photon relays task state and permitted live head/gaze over an encrypted internet connection. Creating a room does not start forwarding.",mutedStyle);
            if(!String.IsNullOrEmpty(easyPairingIssue))GUILayout.Label(easyPairingIssue,mutedStyle);
            if(easyPairing==null)
            {
                if(OwnerButton("Create room",rig!=null&&rig.SharedClock is MonotonicClock&&!closed&&process==null))
                {
                    try
                    {
                        string appId,region,issue;
                        if(!ViveWozPhotonConfiguration.TryLoad(out appId,out region,out issue))throw new InvalidOperationException(issue);
                        easyPairingIssue="";easyOfferReceived=false;easyPeerConfirmed=false;
                        easyPairingTiming="";nextEasyPairingTiming=0;
                        easyPairing=ViveWozPhotonSession.CreateOperator(appId,region,(MonotonicClock)rig.SharedClock);
                        if(diagnostics!=null)diagnostics.Record("PAIRING_ROOM_START","Creating a Photon room; forwarding is off.");
                    }
                    catch(Exception error){PairingFailed(error);}
                }
                return;
            }
            if(easyPairing.RoomReady)GUILayout.Label("Room code: "+easyPairing.RoomCode,sectionStyle);
            GUILayout.Label(easyPairing.Status,mutedStyle);
            if(easyPairing.PeerConnected&&!easyPeerConfirmed)
            {
                GUILayout.Label("A participant joined. Check that the intended participant computer shows this room code, then accept. A room code alone does not identify a person.",textStyle);
                if(OwnerButton("Accept this participant",true))
                    try{easyPairing.ConfirmPeer();easyPeerConfirmed=true;}
                    catch(Exception error){PairingFailed(error);}
            }
            else if(easyPeerConfirmed&&!offerReviewed)GUILayout.Label("Participant accepted. Waiting for their current session and headset choices.",textStyle);
            if(easyPairing!=null&&OwnerButton("Close room",process==null))CancelEasyPairing();
        }
    }
}
#endif
