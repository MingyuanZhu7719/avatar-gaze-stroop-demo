using System;
using AvatarStroop.Core;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class WorldSpaceExperimentView
    {
        bool wozParticipantAvatarView;
        bool wozParticipantPoseVisible;

        // Place the operator at the evaluator's eyes. Keep the outbound operator
        // tracking frame unchanged and transform the replica of the classroom.
        public void BeginWozParticipantAvatarView(Vector3 operatorTrackingPosition)
        {
            RequireLayout();
            if(role!=Actor.Operator || wozParticipantAvatarView)
                throw new InvalidOperationException("A new operator view is required.");
            Vector3 evaluatorEyes=contentRoot.InverseTransformPoint((leftEye.position+rightEye.position)*.5f);
            Quaternion roomRotation=Quaternion.Inverse(avatarRoot.localRotation);
            contentRoot.SetPositionAndRotation(operatorTrackingPosition-roomRotation*evaluatorEyes,roomRotation);
            avatarRoot.localRotation=Quaternion.identity;
            avatarRoot.localPosition=CenteredParticipantReference-avatarHead.localPosition-(leftEye.localPosition+rightEye.localPosition)*.5f;

            // Turn the operator's copy of the writing surface toward this seat.
            // Rotate around the stimulus so its target direction stays identical.
            var board=contentRoot.Find("ClassroomEnvironment/Whiteboard");
            Vector3 target=stimulusText.transform.position;
            Vector3 across=Vector3.ProjectOnPlane(target-operatorTrackingPosition,Vector3.up);
            Quaternion face=Quaternion.LookRotation(across.normalized,Vector3.up);
            Vector3 stimulusOnBoard=board.InverseTransformPoint(target);
            board.rotation=face;
            board.position+=target-board.TransformPoint(stimulusOnBoard);
            stimulusText.transform.rotation=face;
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
