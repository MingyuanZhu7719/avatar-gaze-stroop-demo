#if STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarStroop.Unity
{
    // Independent known-target presentation. The actual classroom primitives are
    // reused, with a static participant-facing head. No task state or condition
    // is manufactured. A completed camera pass is a software render observation,
    // not a hardware eye-exposure or display-photon timestamp.
    public sealed class ViveGazeValidationView : MonoBehaviour
    {
        public const string SceneGeometryVersion="classroom-fixed-participant-facing-validation-v2";
        public const double MaximumSceneAgeSeconds=.1;
        public const float TargetDiameterMetres=.012f;
        [SerializeField] WorldSpaceExperimentView classroom;
        [SerializeField] Camera presentationCamera;
        [SerializeField] Transform trackingOrigin,cameraOffset,targetMarker;
        [SerializeField] TextMesh instructionText;
        static readonly string[] Ids={"board_center","board_left","board_right","left_eye","right_eye","left_cheek","right_cheek","background"};
        static readonly string[] Labels={"Board centre","Board left","Board right","Avatar left eye","Avatar right eye","Avatar left cheek","Avatar right cheek","Background"};
        static readonly string[] Categories={"task","task","task","eyes","eyes","remaining_face","remaining_face","other"};
        static readonly string[] GeometryNames={"task","head","left_eye","right_eye","left_pupil","right_pupil"};
        readonly List<RenderedScene> history=new List<RenderedScene>();
        readonly Vector3[] positions=new Vector3[8];
        Transform[] geometry;
        IClock clock;
        ViveGazeValidationTarget currentTarget;
        int targetIndex=-1,revision;
        double? onset;
        RenderedScene pending;
        bool markerWitnessed;

        sealed class RenderedScene
        {
            internal ViveGazeValidationTarget Target;
            internal int Revision,Frame;
            internal double Started,Completed;
            internal Vector3 TargetPosition;
            internal bool GeometryValid;
            internal ViveEyeAoiGeometry[] Geometry;
            internal Matrix4x4[] Inverses;
        }

        public Camera PresentationCamera {get{return presentationCamera;}}
        public Transform TrackingOrigin {get{return trackingOrigin;}}
        public Transform CameraOffset {get{return cameraOffset;}}
        public Transform TargetMarker {get{return targetMarker;}}
        public TextMesh InstructionText {get{return instructionText;}}
        public WorldSpaceExperimentView Classroom {get{return classroom;}}
        public int TargetCount {get{return Ids.Length;}}
        public int ActiveTargetIndex {get{return targetIndex;}}
        public double? TargetOnsetLocalSeconds {get{return onset;}}
        public event Action<ViveGazeValidationTarget,double,int> TargetFirstRendered;

        public void Configure(WorldSpaceExperimentView source,Camera camera,Transform origin,Transform offset,Transform marker,TextMesh instructions)
        {
            if(Application.isPlaying)throw new InvalidOperationException("Author the validation scene outside Play Mode.");
            classroom=source;presentationCamera=camera;trackingOrigin=origin;cameraOffset=offset;targetMarker=marker;instructionText=instructions;
            PreparePresentation();ClearTarget();
        }
        public void Initialize(IClock localClock)
        {
            if(localClock==null)throw new ArgumentNullException("localClock");
            clock=localClock;history.Clear();PreparePresentation();ClearTarget();
        }
        void OnEnable(){UnityEngine.Camera.onPreCull+=BeforeCameraRender;UnityEngine.Camera.onPostRender+=AfterCameraRender;}
        void OnDisable(){UnityEngine.Camera.onPreCull-=BeforeCameraRender;UnityEngine.Camera.onPostRender-=AfterCameraRender;pending=null;}

        void PreparePresentation()
        {
            if(classroom==null||presentationCamera==null||trackingOrigin==null||cameraOffset==null||targetMarker==null||instructionText==null)
                throw new InvalidOperationException("The gaze-validation scene is incomplete.");
            // WorldSpaceExperimentView's normal HUD, task and automatic-avatar
            // updates must never run in this independent validation scene.
            classroom.enabled=false;
            var content=classroom.transform.Find("WorldSpaceContent");
            if(content==null)throw new InvalidOperationException("The classroom geometry is missing.");
            content.gameObject.SetActive(true);
            foreach(Transform child in content)
                child.gameObject.SetActive(child.name=="ClassroomEnvironment"||child==classroom.AvatarRoot);
            var warning=presentationCamera.transform.Find("HeadsetVisibleDevelopmentWarning");
            if(warning!=null)warning.gameObject.SetActive(false);
            var head=classroom.AvatarHead;
            if(head==null||classroom.AvatarRoot==null)throw new InvalidOperationException("The avatar geometry is missing.");
            classroom.AvatarRoot.gameObject.SetActive(true);
            head.rotation=Quaternion.LookRotation(WorldSpaceExperimentView.CenteredParticipantReference-head.position,Vector3.up);
            var left=head.Find("LeftEye");var right=head.Find("RightEye");
            if(left==null||right==null)throw new InvalidOperationException("The avatar eye anchors are missing.");
            left.localRotation=right.localRotation=Quaternion.identity;
            geometry=new[]{content.Find("ClassroomEnvironment/Whiteboard/WritingArea"),head.Find("HeadShape"),
                left.Find("White"),right.Find("White"),left.Find("Pupil"),right.Find("Pupil")};
            for(int i=0;i<geometry.Length;i++)
            {
                if(geometry[i]==null||!FiniteTransform(geometry[i]))throw new InvalidOperationException("Invalid validation geometry: "+GeometryNames[i]);
                var renderer=geometry[i].GetComponent<Renderer>();
                if(renderer==null)throw new InvalidOperationException("Missing validation renderer: "+GeometryNames[i]);
                geometry[i].gameObject.SetActive(true);renderer.enabled=true;
            }
            // Marker centres sit just in front of the actual visible surfaces.
            // The gaze ray is classified against the original primitives only.
            positions[0]=InFront(geometry[0].TransformPoint(new Vector3(0,0,-.5f)));
            positions[1]=InFront(geometry[0].TransformPoint(new Vector3(-.30f,0,-.5f)));
            positions[2]=InFront(geometry[0].TransformPoint(new Vector3(.30f,0,-.5f)));
            positions[3]=InFront(geometry[4].TransformPoint(new Vector3(0,0,.5f)));
            positions[4]=InFront(geometry[5].TransformPoint(new Vector3(0,0,.5f)));
            float cheekZ=Mathf.Sqrt(.25f-.24f*.24f-.20f*.20f);
            positions[5]=InFront(geometry[1].TransformPoint(new Vector3(-.24f,-.20f,cheekZ)));
            positions[6]=InFront(geometry[1].TransformPoint(new Vector3(.24f,-.20f,cheekZ)));
            // The background target stays outside the original task/face AOIs,
            // above the board in the central room view. This avoids the v1 jump
            // from the right cheek to the far-left peripheral background. Target
            // placement and measurement timing never depend on measured gaze.
            positions[7]=new Vector3(-.5f,2.30f,1.90f);
            targetMarker.localScale=Vector3.one*TargetDiameterMetres;
            var witness=targetMarker.GetComponent<ViveGazeValidationRenderWitness>();
            if(witness==null)throw new InvalidOperationException("The target render witness is missing.");
            witness.Bind(this);
        }
        static Vector3 InFront(Vector3 surface)
        {return surface+(WorldSpaceExperimentView.CenteredParticipantReference-surface).normalized*(TargetDiameterMetres*.6f);}
        public ViveGazeValidationTarget GetTarget(int index,string pass)
        {
            CheckIndex(index);return new ViveGazeValidationTarget(Ids[index],Categories[index],pass);
        }
        public string GetTargetLabel(int index){CheckIndex(index);return Labels[index];}
        public Vector3 GetTargetPositionWorld(int index){CheckIndex(index);return positions[index];}
        static void CheckIndex(int index){if(index<0||index>=Ids.Length)throw new ArgumentOutOfRangeException("index");}
        public void ShowTarget(int index,string pass)
        {
            if(clock==null)throw new InvalidOperationException("Initialize the validation clock before displaying a target.");
            currentTarget=GetTarget(index,pass);targetIndex=index;revision++;onset=null;pending=null;
            // Instructions and their backing are absent during measurement, so
            // they cannot occlude an AOI or become a competing gaze target.
            instructionText.gameObject.SetActive(false);
            targetMarker.position=positions[index];targetMarker.gameObject.SetActive(true);
        }
        public void ClearTarget()
        {
            currentTarget=null;targetIndex=-1;revision++;onset=null;pending=null;
            if(targetMarker!=null)targetMarker.gameObject.SetActive(false);
        }
        public void SetInstruction(string value)
        {
            if(instructionText==null)return;
            instructionText.text=value??"";instructionText.gameObject.SetActive(currentTarget==null);
            ViveProEyePreview.FitText(instructionText,1.7f,.22f,.024f);
        }
        bool CameraUsable(Camera camera)
        {
            return camera!=null&&camera==presentationCamera&&camera.isActiveAndEnabled&&camera.targetTexture==null&&camera.cullingMask!=0&&
                camera.rect.width>0&&camera.rect.height>0&&GraphicsSettings.currentRenderPipeline==null&&isActiveAndEnabled;
        }
        bool RendererVisible(Transform value)
        {
            if(value==null||!value.gameObject.activeInHierarchy)return false;
            var renderer=value.GetComponent<Renderer>();
            return renderer!=null&&renderer.enabled&&(presentationCamera.cullingMask&(1<<value.gameObject.layer))!=0;
        }
        internal void BeforeCameraRender(Camera camera)
        {
            if(camera!=presentationCamera)return;
            pending=null;markerWitnessed=false;
            if(clock==null||currentTarget==null||!CameraUsable(camera)||!RendererVisible(targetMarker))return;
            var scene=new RenderedScene{Target=currentTarget,Revision=revision,Frame=Time.frameCount,Started=clock.Now,
                TargetPosition=targetMarker.position,GeometryValid=true,Geometry=new ViveEyeAoiGeometry[6],Inverses=new Matrix4x4[6]};
            for(int i=0;i<geometry.Length;i++)
            {
                var item=geometry[i];bool available=RendererVisible(item)&&FiniteTransform(item);
                if(!available)scene.GeometryValid=false;
                scene.Geometry[i]=new ViveEyeAoiGeometry(GeometryNames[i],available,ToVec(item.position),ToRotation(item.rotation),ToVec(item.lossyScale));
                scene.Inverses[i]=item.worldToLocalMatrix;
            }
            pending=scene;
        }
        internal void ObserveMarkerRender(Camera camera)
        {
            if(camera==presentationCamera&&pending!=null&&pending.Frame==Time.frameCount&&pending.Revision==revision)
                markerWitnessed=true;
        }
        internal void AfterCameraRender(Camera camera)
        {
            if(camera!=presentationCamera)return;
            var scene=pending;pending=null;
            if(scene==null||!markerWitnessed||!CameraUsable(camera)||!RendererVisible(targetMarker)||scene.Frame!=Time.frameCount||
                scene.Revision!=revision||currentTarget!=scene.Target||targetMarker.position!=scene.TargetPosition)return;
            for(int i=0;i<geometry.Length;i++)
                if(!RendererVisible(geometry[i])||geometry[i].worldToLocalMatrix!=scene.Inverses[i])return;
            scene.Completed=clock.Now;
            if(!Finite(scene.Started)||!Finite(scene.Completed)||scene.Completed<scene.Started)return;
            history.Add(scene);while(history.Count>32)history.RemoveAt(0);
            if(!onset.HasValue)
            {
                onset=scene.Completed;
                var callback=TargetFirstRendered;
                if(callback!=null)callback(new ViveGazeValidationTarget(scene.Target.Id,scene.Target.ExpectedAoi,scene.Target.Pass,ToVec(scene.TargetPosition)),scene.Completed,scene.Frame);
            }
        }
        public ViveGazeValidationSnapshot Capture(ViveEyeFrame eye,bool headReady)
        {
            if(eye==null||clock==null||presentationCamera==null)return null;
            RenderedScene scene=null;
            // Select actual prior render evidence at callback arrival time, not
            // whichever marker is now on screen when Unity drains its FIFO.
            for(int i=history.Count-1;i>=0;i--)
                if(history[i].Completed<=eye.ObservedLocalSeconds){scene=history[i];break;}
            if(scene==null)return null;
            double poseTime=clock.Now;
            var cameraTransform=presentationCamera.transform;
            bool poseValid=headReady&&CameraUsable(presentationCamera)&&Finite(cameraTransform.position)&&ToRotation(cameraTransform.rotation).IsUnit;
            Vec3 invalid=new Vec3(Double.NaN,Double.NaN,Double.NaN),origin=invalid,direction=invalid,sdkUnit;
            bool rayValid=!eye.NoUser&&eye.Combined!=null&&eye.Combined.OriginReportedValid&&eye.Combined.DirectionReportedValid&&
                eye.Combined.OriginMillimeters.IsFinite&&ViveProEyeSdkDirection.TryGetUnitDirection(eye.Combined.Direction,out sdkUnit);
            Vector3 worldOrigin=Vector3.zero,worldDirection=Vector3.zero;
            if(rayValid)
            {
                var raw=eye.Combined.OriginMillimeters;
                worldOrigin=cameraTransform.TransformPoint(new Vector3((float)(-raw.X*.001),(float)(raw.Y*.001),(float)(raw.Z*.001)));
                ViveProEyeSdkDirection.TryGetUnitDirection(eye.Combined.Direction,out sdkUnit);
                worldDirection=cameraTransform.rotation*new Vector3((float)-sdkUnit.X,(float)sdkUnit.Y,(float)sdkUnit.Z);
                rayValid=Finite(worldOrigin)&&Finite(worldDirection)&&worldDirection.sqrMagnitude>0;
                if(rayValid){worldDirection.Normalize();origin=ToVec(worldOrigin);direction=ToVec(worldDirection);}
            }
            string category="invalid",reason;
            if(!poseValid)reason="head_pose_unavailable";
            else if(!IsEligibleHistoricalScene(scene.Frame,Time.frameCount,scene.Completed,eye.ObservedLocalSeconds,poseTime))reason="stale_render_scene";
            else if(!scene.GeometryValid)reason="geometry_unavailable";
            else if(!rayValid)reason="invalid_combined_ray";
            else {category=Classify(scene.Inverses,scene.Geometry,worldOrigin,worldDirection);reason="valid_surface_proxy";}
            return new ViveGazeValidationSnapshot(scene.Target.Id,scene.Target.Pass,scene.Started,scene.Completed,scene.Frame,poseTime,
                ToVec(scene.TargetPosition),ToVec(cameraTransform.position),ToRotation(cameraTransform.rotation),origin,direction,category,reason,poseValid,scene.Geometry);
        }
        internal static bool IsEligibleHistoricalScene(int sceneFrame,int poseFrame,double completed,double callback,double pose)
        {
            // Unlike a changing study trial, this scene uses static targets and
            // geometry. A callback drained in a later Unity frame may use its
            // genuine preceding render within this explicit age bound. The core
            // separately enforces the tighter callback-to-pose skew limit.
            return sceneFrame>=0&&poseFrame>=sceneFrame&&Finite(completed)&&Finite(callback)&&Finite(pose)&&
                completed>=0&&completed<=callback&&callback<=pose&&pose-completed<=MaximumSceneAgeSeconds;
        }
        internal static string Classify(Matrix4x4[] inverses,ViveEyeAoiGeometry[] surfaces,Vector3 origin,Vector3 unitDirection)
        {
            string category="other";float nearest=Single.PositiveInfinity;
            for(int i=0;i<6;i++)
            {
                if(!surfaces[i].Enabled)continue;
                float distance;Vector3 local;
                if(!WorldSpaceExperimentView.TryIntersectAoiMatrix(inverses[i],i==0,origin,unitDirection,out distance,out local)||distance>=nearest)continue;
                nearest=distance;category=i==0?(local.z<=-.4999f?"task":"other"):i==1?(local.z>=0?"remaining_face":"other"):"eyes";
            }
            return category;
        }
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        static bool Finite(Vector3 value){return Finite(value.x)&&Finite(value.y)&&Finite(value.z);}
        static Vec3 ToVec(Vector3 value){return new Vec3(value.x,value.y,value.z);}
        static Rotation ToRotation(Quaternion value){return new Rotation(value.x,value.y,value.z,value.w);}
        static bool FiniteTransform(Transform value)
        {
            if(value==null||!Finite(value.position)||!Finite(value.lossyScale)||!ToRotation(value.rotation).IsUnit)return false;
            var scale=value.lossyScale;if(scale.x<=0||scale.y<=0||scale.z<=0)return false;
            var expected=Matrix4x4.TRS(value.position,value.rotation,scale);var actual=value.localToWorldMatrix;
            for(int i=0;i<16;i++)if(!Finite(actual[i])||Mathf.Abs(expected[i]-actual[i])>.0001f)return false;
            return true;
        }
    }
}
#endif
