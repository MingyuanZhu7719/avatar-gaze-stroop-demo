#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity.Editor
{
    // Synthetic scene geometry only; no XR, eye client, network or task starts.
    public static class ViveWozOperatorViewFixture
    {
        static int checks;
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("WoZ operator view fixture: "+message);checks++;}
        static bool Near(Vector3 a,Vector3 b){return Vector3.Distance(a,b)<.0001f;}
        static Vector3 Eyes(WorldSpaceExperimentView view)
        {return (view.AvatarHead.Find("LeftEye").position+view.AvatarHead.Find("RightEye").position)*.5f;}

        static void CheckOrigin(Vector3 rawPosition)
        {
            var camera=new GameObject("SyntheticOperatorCamera").AddComponent<Camera>();
            var view=new GameObject("SyntheticOperatorView").AddComponent<WorldSpaceExperimentView>();
            try
            {
                view.Initialize(camera,Actor.Operator);
                var room=view.AvatarRoot.parent;
                var canonicalEvaluatorEyes=room.InverseTransformPoint(Eyes(view));
                var canonicalEvaluatorYaw=view.AvatarRoot.localRotation;
                camera.transform.SetPositionAndRotation(rawPosition,Quaternion.Euler(7,32,-3));
                var rawRotation=camera.transform.rotation;
                view.PlaceWozOperatorView(rawPosition);
                Require(view.WozOperatorViewPlaced,"unpaired view did not establish its station");
                Require(Near(room.InverseTransformPoint(rawPosition),canonicalEvaluatorEyes),"Device/Floor height did not land at the evaluator's eyes");
                Require(canonicalEvaluatorEyes.x>WorldSpaceExperimentView.BoardPosition.x+.75f,"station must be to the board's right from the participant");
                Require(Quaternion.Angle(room.rotation*canonicalEvaluatorYaw,Quaternion.identity)<.01f,"outgoing tracking-to-avatar orientation changed");
                Require(Near(camera.transform.position,rawPosition)&&Quaternion.Angle(camera.transform.rotation,rawRotation)<.01f,"room placement rewrote raw camera pose");
                Require(Near(room.InverseTransformPoint(Eyes(view)),WorldSpaceExperimentView.CenteredParticipantReference),"participant placeholder is not at the participant station");
                Require(Near(room.InverseTransformPoint(view.TaskWorldPosition),WorldSpaceExperimentView.TaskDisplayPosition),"board target moved in the shared classroom coordinates");

                var floor=room.Find("ClassroomEnvironment/Floor");
                Require(Mathf.Abs(rawPosition.y-(floor.position.y+.04f)-1.655f)<.001f,"virtual floor-to-eye height is not constant across raw origins");
                var roomPosition=room.position;
                var boardPosition=view.TaskWorldPosition;
                var motion=new Vector3(.12f,.06f,-.09f);
                camera.transform.position+=motion;
                Require(Near(room.position,roomPosition)&&Near(view.TaskWorldPosition,boardPosition),"local head movement moved the classroom");
                var task=new TaskSnapshot("operator-view-fixture",0,1,"trial-1",Colour.Red,Colour.Blue,Phase.MainTask,Condition.A);
                view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(task));
                view.BeginWozParticipantAvatarView(camera.transform.position);
                Require(Near(room.position,roomPosition)&&Near(view.TaskWorldPosition,boardPosition),"pairing jumped or recentered the established view");
                Require(!view.AvatarRoot.gameObject.activeSelf,"pairing displayed an unreceived participant pose");

                var left=view.AvatarHead.Find("LeftEye");
                var right=view.AvatarHead.Find("RightEye");
                var participantEyes=Eyes(view);
                var boardDirection=(view.TaskWorldPosition-participantEyes).normalized;
                var evaluatorDirection=(rawPosition-participantEyes).normalized;
                Require(Vector3.Angle(boardDirection,evaluatorDirection)>20,"participant's board and evaluator targets overlap");
                var head=Quaternion.Euler(-2,14,3);
                foreach(var target in new[]{boardDirection,evaluatorDirection})
                {
                    var eyes=Quaternion.Inverse(head)*room.InverseTransformDirection(target);
                    view.ShowWozParticipantPose(new Rotation(head.x,head.y,head.z,head.w),new Vec3(eyes.x,eyes.y,eyes.z));
                    Require(view.AvatarRoot.gameObject.activeSelf&&Vector3.Angle(left.forward,target)<.1f&&Vector3.Angle(right.forward,target)<.1f,"received head/eye pose no longer points at its corresponding target");
                }
                view.ClearWozParticipantPose();
                Require(!view.AvatarRoot.gameObject.activeSelf,"stale participant pose remained visible");

                // A later deliberate source restart uses the original station,
                // not the participant's eyes or the previous transformed room.
                var newPosition=rawPosition+new Vector3(.8f,-.4f,.3f);
                view.PlaceWozOperatorView(newPosition);
                Require(Near(room.InverseTransformPoint(newPosition),canonicalEvaluatorEyes),"explicit re-placement compounded a previous transform");
                Require(Near(room.position-roomPosition,newPosition-rawPosition),"re-placement changed scale/orientation or used the participant anchor");
                var placedBoard=room.Find("ClassroomEnvironment/Whiteboard");
                var repeatedBoardPosition=placedBoard.position;
                var repeatedBoardRotation=placedBoard.rotation;
                view.PlaceWozOperatorView(newPosition);
                Require(Near(placedBoard.position,repeatedBoardPosition)&&Quaternion.Angle(placedBoard.rotation,repeatedBoardRotation)<.01f,"repeated placement accumulated a board transform");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(view.gameObject);
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }
        }

        public static void Validate()
        {
            checks=0;
            foreach(var position in new[]{Vector3.zero,new Vector3(.32f,1.61f,-.13f),new Vector3(-1.2f,.08f,.7f),new Vector3(1.3f,1.82f,-.6f)})CheckOrigin(position);
            Debug.Log("VIVE_WOZ_OPERATOR_VIEW_FIXTURE_PASSED: "+checks+" geometry checks; unpaired evaluator station, Device/Floor origins, raw pose preserved, no pairing jump, participant gaze correspondence. No headset acceptance.");
        }
    }
}
#endif
