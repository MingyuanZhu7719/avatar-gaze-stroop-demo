using System;
using AvatarStroop.Core;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class WorldSpaceExperimentView
    {
        bool wozParticipantAvatarView;
        bool wozParticipantPoseVisible;
        bool wozOperatorViewPlaced,wozOperatorReferenceCaptured;
        Vector3 wozEvaluatorEyeReference,wozBoardPositionReference;
        Quaternion wozRoomRotation,wozBoardRotationReference;
        public bool WozOperatorViewPlaced {get{return wozOperatorViewPlaced;}}

        // Place the local view before pairing, in the same evaluator station that
        // the participant sees to the board's right. The camera stays in the raw
        // tracking frame: translating the room handles both Device and Floor
        // origins without relabeling outbound head rotations or inventing height.
        public void PlaceWozOperatorView(Vector3 operatorTrackingPosition)
        {
            RequireLayout();
            if(role!=Actor.Operator)throw new InvalidOperationException("An operator view is required.");
            if(!FiniteWozPosition(operatorTrackingPosition))throw new ArgumentException("A finite tracked position is required.","operatorTrackingPosition");
            // The participant-facing room was open behind its camera. From the
            // evaluator station that opening is now behind the participant.
            var environment=contentRoot.Find("ClassroomEnvironment");
            if(environment.Find("OperatorFrontWall")==null)
                Primitive("OperatorFrontWall",PrimitiveType.Cube,environment,new Vector3(0,1.5f,-1.05f),
                    new Vector3(6,3,.10f),new Color(.70f,.72f,.70f));
            var board=contentRoot.Find("ClassroomEnvironment/Whiteboard");
            if(!wozOperatorReferenceCaptured)
            {
                // Capture before the same avatar geometry is moved to represent
                // the participant. A later explicit view restart must not use
                // the participant's eyes as the evaluator's reference.
                wozEvaluatorEyeReference=contentRoot.InverseTransformPoint((leftEye.position+rightEye.position)*.5f);
                wozRoomRotation=Quaternion.Inverse(avatarRoot.localRotation);
                wozBoardPositionReference=board.localPosition;
                wozBoardRotationReference=board.localRotation;
                avatarRoot.localRotation=Quaternion.identity;
                avatarRoot.localPosition=CenteredParticipantReference-avatarHead.localPosition-(leftEye.localPosition+rightEye.localPosition)*.5f;
                wozOperatorReferenceCaptured=true;
            }
            contentRoot.SetPositionAndRotation(operatorTrackingPosition-wozRoomRotation*wozEvaluatorEyeReference,wozRoomRotation);

            // Turn the operator's copy of the writing surface toward this seat.
            // Rotate around the stimulus so its target direction stays identical.
            board.localPosition=wozBoardPositionReference;
            board.localRotation=wozBoardRotationReference;
            Vector3 target=stimulusText.transform.position;
            Vector3 across=Vector3.ProjectOnPlane(target-operatorTrackingPosition,Vector3.up);
            Quaternion face=Quaternion.LookRotation(across.normalized,Vector3.up);
            Vector3 stimulusOnBoard=board.InverseTransformPoint(target);
            board.rotation=face;
            board.position+=target-board.TransformPoint(stimulusOnBoard);
            stimulusText.transform.rotation=face;
            wozOperatorViewPlaced=true;
        }

        static bool FiniteWozPosition(Vector3 value)
        {return !Single.IsNaN(value.x)&&!Single.IsInfinity(value.x)&&!Single.IsNaN(value.y)&&!Single.IsInfinity(value.y)&&!Single.IsNaN(value.z)&&!Single.IsInfinity(value.z);}

        // Pairing changes which participant pose may be displayed, never where
        // the operator stands. Retain the fallback for callers that create an
        // already-paired synthetic view without starting a local headset first.
        public void BeginWozParticipantAvatarView(Vector3 operatorTrackingPosition)
        {
            RequireLayout();
            if(role!=Actor.Operator || wozParticipantAvatarView)
                throw new InvalidOperationException("A new operator view is required.");
            if(!wozOperatorViewPlaced)PlaceWozOperatorView(operatorTrackingPosition);
            wozParticipantAvatarView=true;
            RefreshAvatarVisibility();
        }

        public void ShowWozParticipantPose(Rotation head,Vec3 headLocalEyes)
        {
            if(!wozParticipantAvatarView || currentView==null || !currentView.ParticipantAvatarVisible ||
                !head.IsUnit || !headLocalEyes.IsUnit)
            {
                ClearWozParticipantPose();
                return;
            }
            avatarHead.localRotation=new Quaternion((float)head.X,(float)head.Y,(float)head.Z,(float)head.W).normalized;
            var direction=new Vector3((float)headLocalEyes.X,(float)headLocalEyes.Y,(float)headLocalEyes.Z);
            var eyes=Quaternion.FromToRotation(Vector3.forward,direction);
            leftEye.localRotation=eyes;
            rightEye.localRotation=eyes;
            HeadHeld=EyesHeld=false;
            wozParticipantPoseVisible=true;
            RefreshAvatarVisibility();
        }

        public void ClearWozParticipantPose()
        {
            if(!wozParticipantPoseVisible)return;
            wozParticipantPoseVisible=false;
            HeadHeld=EyesHeld=true;
            RefreshAvatarVisibility();
        }
    }
}
