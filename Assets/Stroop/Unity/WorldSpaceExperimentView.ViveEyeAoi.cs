using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarStroop.Unity
{
    public sealed partial class WorldSpaceExperimentView
    {
        int eyeAoiLastRenderFrame=-1;
        double? eyeAoiLastRenderUnitySeconds;
        long eyeAoiLastRenderTaskRevision=-1,eyeAoiLastRenderAvatarRevision=-1;
        Transform[] eyeAoiGeometry;
        static readonly string[] EyeAoiNames={"task","head","left_eye","right_eye","left_pupil","right_pupil"};
        sealed class EyeAoiScene
        {
            internal double Started,Completed,UnitySeconds;
            internal int Frame,Block;
            internal Phase Phase;
            internal string Trial;
            internal long TaskRevision,AvatarRevision;
            internal bool CameraUsable,GeometryValid,Applicable;
            internal ViveEyeAoiGeometry[] Geometry;
            internal Matrix4x4[] Inverses;
        }
        EyeAoiScene eyeAoiScene;
        IClock eyeAoiClock;
        const double EyeAoiMaximumSceneAgeSeconds=.1;

        void ObserveViveEyeAoiCameraRender(Camera camera)
        {
            if(camera!=presentationCamera||camera==null||!camera.isActiveAndEnabled||camera.cullingMask==0||
                camera.targetTexture!=null||camera.rect.width<=0||camera.rect.height<=0||
                GraphicsSettings.currentRenderPipeline!=null||role!=Actor.Participant||!PresentationIsAvailable()||!isActiveAndEnabled)return;
            eyeAoiLastRenderFrame=Time.frameCount;eyeAoiLastRenderUnitySeconds=Time.realtimeSinceStartupAsDouble;
            eyeAoiLastRenderTaskRevision=presentationRevision;eyeAoiLastRenderAvatarRevision=avatarRevision;
            if(eyeAoiClock==null)return;
            // Geometry must belong to this completed participant-camera pass,
            // with the same task/avatar revisions captured before culling. An
            // enabled renderer alone does not manufacture a render observation.
            if(capturedAvatar==null||capturedAvatarFrame!=Time.frameCount||capturedTaskRevision!=presentationRevision||
                capturedAvatarRevision!=avatarRevision){eyeAoiScene=null;return;}
            try{CaptureViveEyeAoiRenderedScene(eyeAoiClock);}
            catch(Exception){eyeAoiScene=null;} // Optional telemetry cannot bypass or interrupt task render safety.
        }

        void ResolveEyeAoiGeometry()
        {
            if(eyeAoiGeometry!=null)return;
            eyeAoiGeometry=new Transform[6];
            if(contentRoot!=null)eyeAoiGeometry[0]=contentRoot.Find("ClassroomEnvironment/Whiteboard/WritingArea");
            if(avatarHead!=null)eyeAoiGeometry[1]=avatarHead.Find("HeadShape");
            if(leftEye!=null){eyeAoiGeometry[2]=leftEye.Find("White");eyeAoiGeometry[4]=leftEye.Find("Pupil");}
            if(rightEye!=null){eyeAoiGeometry[3]=rightEye.Find("White");eyeAoiGeometry[5]=rightEye.Find("Pupil");}
        }

        // Bind the same software clock used by admission before rendering starts.
        // Do not query new-frame head readiness here: before ReadHead its sequence
        // intentionally belongs to the preceding frame and cannot authorize a view.
        public void BindViveEyeAoiRenderClock(IClock clock)
        {
            if(object.ReferenceEquals(clock,eyeAoiClock))return;
            eyeAoiClock=clock;eyeAoiScene=null;eyeAoiLastRenderFrame=-1;eyeAoiLastRenderUnitySeconds=null;
            eyeAoiLastRenderTaskRevision=eyeAoiLastRenderAvatarRevision=-1;
        }

        // Copy actual transforms and visibility while the participant camera has
        // just completed its pass. The next Update can mask/hide everything again
        // without altering this immutable historical scene observation.
        void CaptureViveEyeAoiRenderedScene(IClock clock)
        {
            eyeAoiScene=null;ResolveEyeAoiGeometry();
            var scene=new EyeAoiScene {Started=clock.Now,UnitySeconds=Time.realtimeSinceStartupAsDouble,Frame=Time.frameCount,
                Phase=currentView==null?Phase.Preparation:currentView.Phase,Block=currentView==null?-1:currentView.BlockIndex,
                Trial=currentView==null?null:currentView.TrialId,TaskRevision=presentationRevision,AvatarRevision=avatarRevision,
                Applicable=currentView!=null&&currentView.Phase==Phase.MainTask&&currentView.OperatorAvatarVisible,
                CameraUsable=presentationCamera!=null&&presentationCamera.isActiveAndEnabled&&presentationCamera.cullingMask!=0&&
                    presentationCamera.targetTexture==null&&PresentationIsAvailable()&&!hasPresentationFault&&isActiveAndEnabled&&role==Actor.Participant,
                GeometryValid=true,Geometry=new ViveEyeAoiGeometry[6],Inverses=new Matrix4x4[6]};
            var invalid=new Vec3(Double.NaN,Double.NaN,Double.NaN);
            for(int i=0;i<6;i++)
            {
                var target=eyeAoiGeometry[i];
                bool enabled=target!=null&&AoiRendererEnabled(target)&&scene.CameraUsable&&scene.Phase==Phase.MainTask&&(i==0||scene.Applicable);
                if(target==null)
                {
                    scene.Geometry[i]=new ViveEyeAoiGeometry(EyeAoiNames[i],false,invalid,new Rotation(Double.NaN,Double.NaN,Double.NaN,Double.NaN),invalid);
                    if(i==0||scene.Applicable)scene.GeometryValid=false;
                }
                else
                {
                    scene.Geometry[i]=new ViveEyeAoiGeometry(EyeAoiNames[i],enabled,AoiVec(target.position),AoiRotation(target.rotation),AoiVec(target.lossyScale));
                    if((i==0||scene.Applicable)&&(!enabled||!AoiFiniteTransform(target)))scene.GeometryValid=false;
                    scene.Inverses[i]=target.worldToLocalMatrix;
                }
            }
            scene.Completed=clock.Now;eyeAoiScene=scene;
        }

        // This method must be called on Unity's main thread. Camera pose is sampled
        // now and geometry comes from a recent completed render, while the native
        // eye sample retains its original identity/time. None is backdated.
        public ViveEyeAoiObservation CaptureViveEyeAoi(ViveEyeFrame eye,double captureLocalSeconds)
        {return CaptureViveEyeAoiCore(eye,captureLocalSeconds,null);}
        public ViveEyeAoiObservation CaptureViveEyeAoi(ViveEyeFrame eye,IClock clock)
        {
            if(clock==null)throw new ArgumentNullException("clock");
            BindViveEyeAoiRenderClock(clock);
            return CaptureViveEyeAoiCore(eye,clock.Now,()=>clock.Now);
        }
        ViveEyeAoiObservation CaptureViveEyeAoiCore(ViveEyeFrame eye,double captureLocalSeconds,Func<double> captureCompleted)
        {
            if(eye==null)throw new ArgumentNullException("eye");
            double unitySeconds=Time.realtimeSinceStartupAsDouble;
            bool active=currentView!=null&&currentView.Phase==Phase.MainTask;
            bool applicable=active&&currentView.OperatorAvatarVisible;
            var cameraTransform=presentationCamera!=null?presentationCamera.transform:null;
            var invalid=new Vec3(Double.NaN,Double.NaN,Double.NaN);
            Vec3 cameraPosition=cameraTransform==null?invalid:AoiVec(cameraTransform.position);
            Rotation cameraRotation=cameraTransform==null?new Rotation(Double.NaN,Double.NaN,Double.NaN,Double.NaN):AoiRotation(cameraTransform.rotation);
            bool cameraUsable=cameraTransform!=null&&AoiFinite(cameraTransform.position)&&cameraRotation.IsUnit&&
                presentationCamera.isActiveAndEnabled&&presentationCamera.targetTexture==null&&
                !hasPresentationFault&&isActiveAndEnabled&&role==Actor.Participant;
            bool originValid=cameraTransform!=null&&eye.Combined!=null&&eye.Combined.OriginReportedValid&&eye.Combined.OriginMillimeters.IsFinite;
            Vec3 unit;
            bool directionValid=cameraTransform!=null&&eye.Combined!=null&&eye.Combined.DirectionReportedValid&&
                ViveProEyeSdkDirection.TryGetUnitDirection(eye.Combined.Direction,out unit);
            Vector3 worldOrigin=Vector3.zero,worldDirection=Vector3.zero;
            Vec3 origin=invalid,direction=invalid;
            if(cameraTransform!=null&&originValid)
            {
                var raw=eye.Combined.OriginMillimeters;
                worldOrigin=cameraTransform.TransformPoint(new Vector3((float)(-raw.X*.001),(float)(raw.Y*.001),(float)(raw.Z*.001)));
                originValid=AoiFinite(worldOrigin);if(originValid)origin=AoiVec(worldOrigin);
            }
            if(cameraTransform!=null&&directionValid)
            {
                // Match the installed SRanipal Unity Ray convention. The native
                // combined ray is independent of the left/right channel rays.
                ViveProEyeSdkDirection.TryGetUnitDirection(eye.Combined.Direction,out unit);
                worldDirection=cameraTransform.rotation*new Vector3((float)-unit.X,(float)unit.Y,(float)unit.Z);
                directionValid=AoiFinite(worldDirection)&&worldDirection.sqrMagnitude>0;
                if(directionValid){worldDirection.Normalize();direction=AoiVec(worldDirection);}
            }
            var scene=eyeAoiScene;
            var snapshots=scene==null?new ViveEyeAoiGeometry[0]:scene.Geometry;
            string category="invalid",reason;
            if(!active){category="outside_task";reason="outside_main_task";}
            else if(!AoiFinite(captureLocalSeconds)||captureLocalSeconds<eye.ObservedLocalSeconds)reason="invalid_capture_time";
            else if(scene==null)reason="no_completed_render_scene";
            else if(scene.Frame<0||scene.Frame>Time.frameCount||(long)Time.frameCount-scene.Frame>1)reason="stale_render_scene";
            else if(scene.Phase!=currentView.Phase||scene.Block!=currentView.BlockIndex||scene.Trial!=currentView.TrialId||
                scene.Applicable!=applicable||scene.TaskRevision!=presentationRevision)reason="scene_task_context_changed";
            else if(!AoiFinite(scene.Started)||!AoiFinite(scene.Completed)||scene.Completed<scene.Started||scene.Completed>captureLocalSeconds)reason="invalid_scene_clock_bracket";
            else if(!IsRecentViveEyeAoiScene(scene.Frame,Time.frameCount,scene.Completed,captureLocalSeconds))reason="stale_render_scene";
            else if(!cameraUsable||!scene.CameraUsable)reason="presentation_unavailable";
            else if(eye.NoUser||!originValid||!directionValid)reason="invalid_combined_ray";
            else if(!scene.GeometryValid)reason="geometry_unavailable";
            else
            {
                category="other";reason="valid_surface_proxy";float nearest=Single.PositiveInfinity;
                for(int i=0;i<6;i++)
                {
                    if(!scene.Geometry[i].Enabled)continue;
                    float distance;Vector3 localHit;
                    if(!TryIntersectAoiMatrix(scene.Inverses[i],i==0,worldOrigin,worldDirection,out distance,out localHit)||distance>=nearest)continue;
                    nearest=distance;
                    // The dark board's front (-Z) is the task surface. The front
                    // (+Z) hemisphere of the head is the face; back-head hits are
                    // other and can occlude eyes behind the head. Nearest surfaces
                    // make eyes and remaining face mutually exclusive.
                    category=i==0?(localHit.z<=-.4999f?"task":"other"):
                        i==1?(localHit.z>=0?"remaining_face":"other"):"eyes";
                }
            }
            double? completed=captureCompleted==null?(double?)null:captureCompleted();
            if(completed.HasValue&&(!AoiFinite(completed.Value)||completed.Value<captureLocalSeconds))
            {category="invalid";reason="invalid_capture_clock_bracket";}
            return new ViveEyeAoiObservation(captureLocalSeconds,completed,unitySeconds,Time.frameCount,
                scene==null?(double?)null:scene.Started,scene==null?(double?)null:scene.Completed,scene==null?(double?)null:scene.UnitySeconds,
                scene==null?-1:scene.Frame,scene==null?-1:scene.TaskRevision,scene==null?-1:scene.AvatarRevision,
                eyeAoiLastRenderFrame,eyeAoiLastRenderUnitySeconds,eyeAoiLastRenderTaskRevision,eyeAoiLastRenderAvatarRevision,
                currentView==null?Phase.Preparation:currentView.Phase,currentView==null?-1:currentView.BlockIndex,
                currentView==null?null:currentView.TrialId,eye.FrameSequence,eye.Timestamp,eye.ObservedLocalSeconds,category,reason,
                active,applicable,originValid,directionValid,cameraPosition,cameraRotation,origin,direction,snapshots);
        }

        bool AoiRendererEnabled(Transform value)
        {
            if(value==null||!value.gameObject.activeInHierarchy)return false;
            var renderer=value.GetComponent<Renderer>();
            return renderer!=null&&renderer.enabled&&presentationCamera!=null&&
                (presentationCamera.cullingMask&(1<<value.gameObject.layer))!=0;
        }
        internal static bool IsRecentViveEyeAoiScene(int sceneFrame,int currentFrame,double completed,double capture)
        {
            return sceneFrame>=0&&currentFrame>=sceneFrame&&(long)currentFrame-sceneFrame<=1&&
                AoiFinite(completed)&&AoiFinite(capture)&&completed>=0&&capture>=completed&&
                capture-completed<=EyeAoiMaximumSceneAgeSeconds;
        }
        static bool AoiFinite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        static bool AoiFinite(Vector3 value){return AoiFinite(value.x)&&AoiFinite(value.y)&&AoiFinite(value.z);}
        static Vec3 AoiVec(Vector3 value){return new Vec3(value.x,value.y,value.z);}
        static Rotation AoiRotation(Quaternion value){return new Rotation(value.x,value.y,value.z,value.w);}
        static bool AoiFiniteTransform(Transform target)
        {
            var scale=target.lossyScale;
            if(!AoiFinite(target.position)||!AoiFinite(scale)||scale.x<=0||scale.y<=0||scale.z<=0||!AoiRotation(target.rotation).IsUnit)return false;
            // Position/rotation/scale snapshots cannot reproduce arbitrary shear;
            // refuse that geometry rather than exporting an inaccurate transform.
            var reconstructed=Matrix4x4.TRS(target.position,target.rotation,scale);var actual=target.localToWorldMatrix;
            for(int i=0;i<16;i++)if(!AoiFinite(actual[i])||Mathf.Abs(actual[i]-reconstructed[i])>0.0001f)return false;
            return true;
        }
        internal static bool TryIntersectAoiSurface(Transform target,bool box,Vector3 origin,Vector3 direction,out float distance,out Vector3 localHit)
        {
            distance=0;localHit=Vector3.zero;
            if(target==null||!AoiFiniteTransform(target)||!AoiFinite(origin)||!AoiFinite(direction)||direction.sqrMagnitude<=0)return false;
            return TryIntersectAoiMatrix(target.worldToLocalMatrix,box,origin,direction,out distance,out localHit);
        }
        static bool TryIntersectAoiMatrix(Matrix4x4 inverse,bool box,Vector3 origin,Vector3 direction,out float distance,out Vector3 localHit)
        {
            distance=0;localHit=Vector3.zero;
            var o=inverse.MultiplyPoint3x4(origin);var d=inverse.MultiplyVector(direction);
            // Do not normalize d: its parameter remains metres along the original
            // unit world ray, including non-uniform scale and rotated primitives.
            double enter,leave;
            if(box)
            {
                enter=Double.NegativeInfinity;leave=Double.PositiveInfinity;
                for(int axis=0;axis<3;axis++)
                {
                    if(Math.Abs(d[axis])<1e-12){if(o[axis]<-.5f||o[axis]>.5f)return false;continue;}
                    double a=(-.5-o[axis])/d[axis],b=(.5-o[axis])/d[axis];
                    if(a>b){double swap=a;a=b;b=swap;}
                    enter=Math.Max(enter,a);leave=Math.Min(leave,b);if(enter>leave)return false;
                }
            }
            else
            {
                double a=Vector3.Dot(d,d),b=2.0*Vector3.Dot(o,d),c=Vector3.Dot(o,o)-.25;
                double discriminant=b*b-4*a*c;if(a<=0||discriminant<0)return false;
                double root=Math.Sqrt(discriminant);enter=(-b-root)/(2*a);leave=(-b+root)/(2*a);
            }
            double hit=enter>0?enter:leave;if(!AoiFinite(hit)||hit<=0||hit>Single.MaxValue)return false;
            distance=(float)hit;localHit=o+d*distance;return AoiFinite(localHit);
        }
    }
}
