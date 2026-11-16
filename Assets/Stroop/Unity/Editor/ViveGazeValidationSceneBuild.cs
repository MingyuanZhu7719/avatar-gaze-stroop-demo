#if STROOP_XR_PREVIEW && UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    public static class ViveGazeValidationSceneBuild
    {
        public const string ScenePath="Assets/Stroop/Generated/ViveGazeValidation.unity";
        sealed class Clock:IClock
        {public double Value=10;public double Now{get{return Value;}}public string ClockId{get{return "synthetic-gaze-validation";}}}
        static int checks;
        static void Require(bool condition,string message)
        {checks++;if(!condition)throw new InvalidOperationException("Gaze-validation scene: "+message);}

        public static Scene CreateScene()
        {
            if(Application.isPlaying)throw new InvalidOperationException("Author validation outside Play Mode.");
            Directory.CreateDirectory(Path.Combine(Application.dataPath,"Stroop","Generated","Materials"));AssetDatabase.Refresh();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var root=new GameObject("ViveFixedTargetValidation");
            var origin=new GameObject("ValidationTrackingOrigin").transform;origin.SetParent(root.transform,false);
            var offset=new GameObject("ValidationReferenceHeight").transform;offset.SetParent(origin,false);
            offset.localPosition=WorldSpaceExperimentView.CenteredParticipantReference;
            var cameraObject=new GameObject("ValidationCamera");cameraObject.tag="MainCamera";cameraObject.transform.SetParent(offset,false);
            var camera=cameraObject.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(.12f,.15f,.17f);camera.nearClipPlane=.05f;camera.farClipPlane=20;
            camera.fieldOfView=WorldSpaceExperimentView.DesktopVerticalFieldOfView;camera.stereoTargetEye=StereoTargetEyeMask.Both;
            camera.allowDynamicResolution=false;camera.cullingMask=~0;
            var geometry=new GameObject("ValidationClassroom");geometry.transform.SetParent(root.transform,false);
            var classroom=geometry.AddComponent<WorldSpaceExperimentView>();classroom.Initialize(camera,Actor.Participant);
            classroom.enabled=false;camera.transform.localRotation=Quaternion.identity;
            // These task witnesses do not participate in the independent mode.
            foreach(var witness in geometry.GetComponentsInChildren<AvatarRenderWitness>(true))UnityEngine.Object.DestroyImmediate(witness);
            foreach(var witness in geometry.GetComponentsInChildren<StimulusRenderWitness>(true))UnityEngine.Object.DestroyImmediate(witness);
            var marker=GameObject.CreatePrimitive(PrimitiveType.Sphere);marker.name="KnownValidationTarget";marker.transform.SetParent(root.transform,false);
            UnityEngine.Object.DestroyImmediate(marker.GetComponent<Collider>());
            var shader=Shader.Find("Unlit/Color");Require(shader!=null,"target shader unavailable");
            var material=new Material(shader){color=new Color(1,.83f,.05f)};
            marker.GetComponent<Renderer>().sharedMaterial=material;
            marker.GetComponent<Renderer>().shadowCastingMode=ShadowCastingMode.Off;marker.GetComponent<Renderer>().receiveShadows=false;
            marker.AddComponent<ViveGazeValidationRenderWitness>();
            var instructionObject=new GameObject("ValidationInstructions");instructionObject.transform.SetParent(camera.transform,false);
            // A readable head-relative panel is used only for countdown/breaks.
            // The whole object, including backing, is hidden during every target.
            instructionObject.transform.localPosition=new Vector3(0,.55f,1.5f);
            var instructions=instructionObject.AddComponent<TextMesh>();instructions.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            instructions.GetComponent<Renderer>().sharedMaterial=instructions.font.material;
            instructions.fontSize=90;instructions.characterSize=.024f;instructions.anchor=TextAnchor.MiddleCenter;
            instructions.alignment=TextAlignment.Center;instructions.richText=false;instructions.color=Color.white;
            var backing=GameObject.CreatePrimitive(PrimitiveType.Quad);backing.name="InstructionBacking";
            backing.transform.SetParent(instructionObject.transform,false);backing.transform.localPosition=new Vector3(0,0,.015f);
            backing.transform.localScale=new Vector3(1.78f,.28f,1);
            UnityEngine.Object.DestroyImmediate(backing.GetComponent<Collider>());
            backing.GetComponent<Renderer>().sharedMaterial=new Material(shader){color=new Color(.025f,.035f,.045f)};
            backing.GetComponent<Renderer>().shadowCastingMode=ShadowCastingMode.Off;backing.GetComponent<Renderer>().receiveShadows=false;
            var view=root.AddComponent<ViveGazeValidationView>();
            view.Configure(classroom,camera,origin,offset,marker.transform,instructions);
            view.SetInstruction("EYE TRACKING CHECK\nFollow the desktop instructions.");
            var controller=root.AddComponent<ViveGazeValidationController>();controller.Configure(view);
            PersistMaterials(scene);Require(EditorSceneManager.SaveScene(scene,ScenePath),"could not save independent validation scene");
            AssetDatabase.SaveAssets();return scene;
        }
        static void PersistMaterials(Scene scene)
        {
            foreach(var renderer in scene.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<Renderer>(true)))
            foreach(var material in renderer.sharedMaterials)
                if(material!=null&&String.IsNullOrEmpty(AssetDatabase.GetAssetPath(material)))
                    AssetDatabase.CreateAsset(material,AssetDatabase.GenerateUniqueAssetPath("Assets/Stroop/Generated/Materials/GazeValidation.mat"));
        }
        static ViveEyeFrame Eye(Camera camera,Vector3 target,double time,bool validOrigin=true,Vector3? nativeOrigin=null)
        {
            var rawOrigin=nativeOrigin??Vector3.zero;
            var worldOrigin=camera.transform.TransformPoint(new Vector3(-rawOrigin.x*.001f,rawOrigin.y*.001f,rawOrigin.z*.001f));
            Vector3 local=Quaternion.Inverse(camera.transform.rotation)*(target-worldOrigin).normalized;
            var ray=new ViveEyeRay(validOrigin?3UL:2UL,validOrigin,true,new Vec3(rawOrigin.x,rawOrigin.y,rawOrigin.z),new Vec3(-local.x,local.y,local.z));
            return new ViveEyeFrame(100,321,time,false,ray,ray,ray);
        }
        static void CameraPass(ViveGazeValidationView view,Clock clock,bool witness=true)
        {
            CameraEvent(view,"BeforeCameraRender");
            if(witness)CameraEvent(view,"ObserveMarkerRender");
            clock.Value+=.001;CameraEvent(view,"AfterCameraRender");
        }
        // Editor fixtures are a separate Unity assembly. Keep synthetic render
        // injection private to this fixture instead of exposing a player API.
        static void CameraEvent(ViveGazeValidationView view,string method)
        {
            var action=typeof(ViveGazeValidationView).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic);
            if(action==null)throw new MissingMethodException(typeof(ViveGazeValidationView).FullName,method);
            action.Invoke(view,new object[]{view.PresentationCamera});
        }
        static bool HistoricalScene(int sceneFrame,int poseFrame,double completed,double callback,double pose)
        {
            var method=typeof(ViveGazeValidationView).GetMethod("IsEligibleHistoricalScene",BindingFlags.Static|BindingFlags.NonPublic);
            if(method==null)throw new MissingMethodException(typeof(ViveGazeValidationView).FullName,"IsEligibleHistoricalScene");
            return (bool)method.Invoke(null,new object[]{sceneFrame,poseFrame,completed,callback,pose});
        }
        public static void RenderFixture(string output)
        {
            Require(Application.isBatchMode&&!Application.isPlaying,"synthetic rendering requires batch mode outside Play Mode");
            if(String.IsNullOrWhiteSpace(output)||!Path.IsPathRooted(output))throw new ArgumentException("An absolute fixture output directory is required.");
            Directory.CreateDirectory(output);
            var scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
            var view=scene.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<ViveGazeValidationView>(true)).Single();
            var clock=new Clock();view.Initialize(clock);var camera=view.PresentationCamera;camera.cullingMask=~0;
            camera.transform.SetPositionAndRotation(WorldSpaceExperimentView.CenteredParticipantReference,Quaternion.identity);
            view.SetInstruction("Keep your head naturally steady.\nTargets begin in 3...");
            Render(camera,Path.Combine(output,"synthetic-validation-instructions.png"));
            foreach(int index in new[]{0,3,7})
            {
                view.ShowTarget(index,"synthetic_render");
                camera.transform.rotation=index==7?Quaternion.identity:Quaternion.LookRotation(view.GetTargetPositionWorld(index)-camera.transform.position,Vector3.up);
                Render(camera,Path.Combine(output,index==0?"synthetic-validation-board.png":index==3?"synthetic-validation-eye.png":"synthetic-validation-background.png"));
                if(index==7)
                {
                    camera.transform.rotation=Quaternion.LookRotation(view.GetTargetPositionWorld(index)-camera.transform.position,Vector3.up);
                    Render(camera,Path.Combine(output,"synthetic-validation-background-centered.png"));
                }
            }
            EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
        }
        static void Render(Camera camera,string path)
        {
            var texture=new RenderTexture(1600,1050,24);var image=new Texture2D(1600,1050,TextureFormat.RGB24,false);
            var previous=RenderTexture.active;var target=camera.targetTexture;
            try
            {
                camera.targetTexture=texture;camera.Render();RenderTexture.active=texture;
                image.ReadPixels(new Rect(0,0,1600,1050),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture=target;RenderTexture.active=previous;
                UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(texture);
            }
        }
        public static void Validate()
        {
            checks=0;
            Require(Application.isBatchMode&&!Application.isPlaying,"synthetic validation requires batch mode outside Play Mode");
            var scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
            var views=scene.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<ViveGazeValidationView>(true)).ToArray();
            Require(views.Length==1,"exactly one validation view required");
            var view=views[0];var camera=view.PresentationCamera;
            Require(camera!=null&&camera.stereoTargetEye==StereoTargetEyeMask.Both,"stereo camera missing");
            Require(view.GetComponent<ViveGazeValidationController>()!=null,"validation controller missing");
            Require(view.TargetMarker!=null&&!view.TargetMarker.gameObject.activeSelf&&view.TargetMarker.GetComponent<Collider>()==null,
                "known target must start hidden and have no collider");
            Require(view.Classroom!=null&&!view.Classroom.enabled,"task presentation behaviour must remain disabled");
            Require(!scene.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<ViveStroopDevelopmentBootstrap>(true)).Any(),"study bootstrap leaked into validation");
            Require(!scene.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<ViveStroopPreviewRig>(true)).Any(),"classroom eye worker leaked into validation");
            var clock=new Clock();view.Initialize(clock);camera.cullingMask=~0;
            camera.transform.SetPositionAndRotation(WorldSpaceExperimentView.CenteredParticipantReference,Quaternion.identity);
            view.SetInstruction("Keep your head naturally steady.\nTargets begin in 3...");
            Require(view.InstructionText.transform.parent==camera.transform&&view.InstructionText.color==Color.white,
                "instructions must be high contrast and head-relative");
            var backing=view.InstructionText.transform.Find("InstructionBacking");
            Require(backing!=null&&backing.GetComponent<Collider>()==null,"instruction backing missing or has a collider");
            float previousAspect=camera.aspect;camera.aspect=1600f/1050;
            for(int i=0;i<view.TargetCount;i++)
            {
                foreach(bool centreTarget in new[]{false,true})
                {
                    camera.transform.rotation=centreTarget?Quaternion.LookRotation(view.GetTargetPositionWorld(i)-camera.transform.position,Vector3.up):Quaternion.identity;
                    var bounds=ProjectedQuad(camera,backing);
                    Require(bounds.xMin>0&&bounds.yMin>0&&bounds.xMax<1&&bounds.yMax<1,"instruction panel cropped in validation camera");
                    // Instructions may occupy the future background-target
                    // direction during countdown. They are never co-present:
                    // the target-loop assertions below verify both text and
                    // backing are hidden before measurement rendering starts.
                }
            }
            camera.aspect=previousAspect;camera.transform.rotation=Quaternion.identity;
            int onsets=0;
            view.TargetFirstRendered+=(target,time,frame)=>{onsets++;Require(target.InitialPositionWorld.HasValue&&target.InitialPositionWorld.Value.IsFinite,"target event lacks actual position");};
            for(int i=0;i<view.TargetCount;i++)
            {
                clock.Value=10+i;view.ShowTarget(i,"fixture");
                Require(!view.InstructionText.gameObject.activeInHierarchy&&!backing.gameObject.activeInHierarchy,
                    "instruction or backing remains visible during target collection");
                view.SetInstruction("This update must stay hidden during a target.");
                Require(!view.InstructionText.gameObject.activeInHierarchy,"instruction update overlaid an active target");
                Require(!view.TargetOnsetLocalSeconds.HasValue,"show call invented a completed-render onset");
                CameraPass(view,clock,false);Require(!view.TargetOnsetLocalSeconds.HasValue,"unwitnessed target produced onset");
                CameraPass(view,clock);Require(view.TargetOnsetLocalSeconds==clock.Value,"first completed render time missing");
                int count=onsets;CameraPass(view,clock);Require(onsets==count,"second/stereo render duplicated target onset");
                clock.Value+=.001;
                var target=view.GetTargetPositionWorld(i);var capture=view.Capture(Eye(camera,target,clock.Value),true);
                Require(capture!=null&&capture.TargetId==view.GetTarget(i,"fixture").Id,"target identity mismatch");
                Require(capture.AoiCategory==view.GetTarget(i,"fixture").ExpectedAoi,"target ray misses intended AOI: "+i+" / "+capture.AoiCategory);
                Require(capture.AoiReason=="valid_surface_proxy"&&capture.PoseValid,"known direction not classified");
                Require(capture.Geometry.Count==6&&capture.Geometry[0].ScaleWorld.X>1.37&&capture.Geometry[0].ScaleWorld.X<1.39,
                    "validation does not reuse actual classroom writing area");
                Require(Math.Abs(capture.TargetPositionWorld.X-target.x)<.000001,"snapshot target position changed");
                VerifyTargetUnobstructed(scene,view,camera.transform.position,target,"nominal "+capture.TargetId);
            }
            Require(onsets==view.TargetCount,"target onset count mismatch");
            VerifyBackgroundEnvelope(scene,view,camera);
            Require(HistoricalScene(10,13,10,10.02,10.035),"delayed callback discarded genuine recent static render");
            Require(!HistoricalScene(10,13,10,10.02,10.101),"historical scene exceeded100ms");
            Require(!HistoricalScene(14,13,10,10.02,10.035),"future Unity render accepted");
            Require(!HistoricalScene(10,13,10.03,10.02,10.035),"render after callback accepted");
            Require(!HistoricalScene(10,13,10,10.04,10.035),"callback after pose accepted");
            clock.Value=20;view.ShowTarget(3,"rotated");CameraPass(view,clock);clock.Value+=.001;
            camera.transform.rotation=Quaternion.Euler(9,26,2);
            var desired=view.GetTargetPositionWorld(3);var rotated=view.Capture(Eye(camera,desired,clock.Value,true,new Vector3(30,2,0)),true);
            Require(rotated.AoiCategory=="eyes","camera rotation/native origin did not preserve the target ray");
            var expectedOrigin=camera.transform.TransformPoint(new Vector3(-.030f,.002f,0));
            Require(Math.Abs(rotated.WorldEyeOrigin.X-expectedOrigin.x)<.000001,"native origin X reflection or millimetre scale changed");
            Require(view.Capture(Eye(camera,desired,clock.Value,false),true).AoiReason=="invalid_combined_ray","invalid origin silently replaced by camera origin");
            Require(view.Capture(Eye(camera,desired,clock.Value),false).AoiReason=="head_pose_unavailable","head-unready sample classified");
            clock.Value+=.101;
            Require(view.Capture(Eye(camera,desired,clock.Value),true).AoiReason=="stale_render_scene","stalled scene accepted");
            camera.cullingMask=0;clock.Value=21;view.ShowTarget(0,"masked");CameraPass(view,clock);
            Require(!view.TargetOnsetLocalSeconds.HasValue,"masked camera produced target onset");
            camera.cullingMask=~0;clock.Value=22;view.ShowTarget(0,"changed");CameraEvent(view,"BeforeCameraRender");CameraEvent(view,"ObserveMarkerRender");
            var head=view.Classroom.AvatarHead;var previous=head.rotation;head.rotation=Quaternion.Euler(0,180,0);clock.Value+=.001;CameraEvent(view,"AfterCameraRender");
            Require(!view.TargetOnsetLocalSeconds.HasValue,"geometry changed during render still produced onset");head.rotation=previous;
            view.ClearTarget();Require(!view.TargetMarker.gameObject.activeSelf,"cleared target remains visible");
            view.SetInstruction("Take a short break.");Require(view.InstructionText.gameObject.activeInHierarchy,"break instructions remained hidden");
            Debug.Log("VIVE_GAZE_VALIDATION_SCENE_FIXTURE_PASSED: "+checks+" checks; synthetic geometry only, no physical accuracy claim.");
            // Validation deliberately does not save synthetic test mutations.
            EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
        }
        static Rect ProjectedQuad(Camera camera,Transform quad)
        {
            float left=Single.PositiveInfinity,right=Single.NegativeInfinity,bottom=Single.PositiveInfinity,top=Single.NegativeInfinity;
            foreach(float x in new[]{-.5f,.5f})foreach(float y in new[]{-.5f,.5f})
            {
                var point=camera.WorldToViewportPoint(quad.TransformPoint(new Vector3(x,y,0)));
                Require(point.z>0,"instruction panel behind camera");
                left=Mathf.Min(left,point.x);right=Mathf.Max(right,point.x);bottom=Mathf.Min(bottom,point.y);top=Mathf.Max(top,point.y);
            }
            return Rect.MinMaxRect(left,bottom,right,top);
        }
        static void VerifyBackgroundEnvelope(Scene scene,ViveGazeValidationView view,Camera camera)
        {
            var originalPosition=camera.transform.position;var originalRotation=camera.transform.rotation;float originalAspect=camera.aspect;
            try
            {
                camera.aspect=1600f/1050;
                var target=view.GetTargetPositionWorld(7);
                Require(Vector3.Distance(target,new Vector3(-.5f,2.30f,1.90f))<.00001f&&view.GetTarget(7,"fixture").ExpectedAoi=="other",
                    "background target must use the v2 central above-board layout and original other category");
                Require(Vector3.Distance(view.TargetMarker.localScale,Vector3.one*ViveGazeValidationView.TargetDiameterMetres)<.00001f,
                    "layout correction changed target diameter");
                // A declared synthetic pose envelope, not replayed participant
                // data and not proof of the unrecorded per-eye HMD projection.
                foreach(var offset in new[]{Vector3.zero,new Vector3(.03f,-.08f,-.04f)})
                foreach(float yaw in new[]{-20f,0f,20f})foreach(float pitch in new[]{-10f,0f,5f})
                {
                    camera.transform.SetPositionAndRotation(WorldSpaceExperimentView.CenteredParticipantReference+offset,Quaternion.Euler(pitch,yaw,0));
                    float radius=ViveGazeValidationView.TargetDiameterMetres*.5f;
                    foreach(var edge in new[]{Vector3.zero,camera.transform.right*radius,-camera.transform.right*radius,
                        camera.transform.up*radius,-camera.transform.up*radius})
                    {
                        var projected=camera.WorldToViewportPoint(target+edge);
                        Require(projected.z>0&&projected.x>.02f&&projected.x<.98f&&projected.y>.02f&&projected.y<.98f,
                            "background target reaches fixture viewport edge within declared pose envelope");
                    }
                    VerifyTargetUnobstructed(scene,view,camera.transform.position,target,"background pose envelope");
                }
                camera.transform.SetPositionAndRotation(WorldSpaceExperimentView.CenteredParticipantReference,Quaternion.identity);
                var blocker=GameObject.CreatePrimitive(PrimitiveType.Cube);blocker.name="SyntheticBackgroundOccluder";
                try
                {
                    blocker.transform.position=(camera.transform.position+target)*.5f;blocker.transform.localScale=Vector3.one*.12f;
                    UnityEngine.Object.DestroyImmediate(blocker.GetComponent<Collider>());
                    bool rejected=false;
                    try{VerifyTargetUnobstructed(scene,view,camera.transform.position,target,"synthetic obstruction control");}
                    catch(InvalidOperationException error){rejected=error.Message.Contains("scene mesh obscures")&&error.Message.Contains(blocker.name);}
                    Require(rejected,"mesh visibility fixture did not detect its synthetic obstruction control");
                }
                finally{UnityEngine.Object.DestroyImmediate(blocker);}
            }
            finally{camera.transform.SetPositionAndRotation(originalPosition,originalRotation);camera.aspect=originalAspect;}
        }
        static void VerifyTargetUnobstructed(Scene scene,ViveGazeValidationView view,Vector3 origin,Vector3 target,string context)
        {
            float targetDistance=Vector3.Distance(origin,target),nearest=Single.PositiveInfinity;
            string surface="none";var direction=(target-origin).normalized;
            // Test actual rendered scene mesh triangles, including room and
            // board-frame geometry that the six analytic AOIs intentionally
            // do not cover. Marker and inactive instruction meshes are excluded.
            foreach(var renderer in scene.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<MeshRenderer>(false)))
            {
                if(!renderer.enabled||renderer.transform==view.TargetMarker||
                    (view.PresentationCamera.cullingMask&(1<<renderer.gameObject.layer))==0)continue;
                var filter=renderer.GetComponent<MeshFilter>();if(filter==null||filter.sharedMesh==null)continue;
                float boundsDistance;if(!renderer.bounds.IntersectRay(new Ray(origin,direction),out boundsDistance)||boundsDistance>targetDistance)continue;
                var vertices=filter.sharedMesh.vertices;var triangles=filter.sharedMesh.triangles;
                for(int i=0;i<triangles.Length;i+=3)
                {
                    float distance;
                    if(IntersectTriangle(origin,direction,renderer.transform.TransformPoint(vertices[triangles[i]]),
                        renderer.transform.TransformPoint(vertices[triangles[i+1]]),renderer.transform.TransformPoint(vertices[triangles[i+2]]),out distance)&&distance<nearest)
                    {nearest=distance;surface=renderer.name;}
                }
            }
            Require(nearest>=targetDistance-ViveGazeValidationView.TargetDiameterMetres*.5f,
                "scene mesh obscures "+context+": "+surface+" at "+nearest+"m before target at "+targetDistance+"m");
        }
        static bool IntersectTriangle(Vector3 origin,Vector3 direction,Vector3 a,Vector3 b,Vector3 c,out float distance)
        {
            distance=0;var edge1=b-a;var edge2=c-a;var cross=Vector3.Cross(direction,edge2);float determinant=Vector3.Dot(edge1,cross);
            if(Mathf.Abs(determinant)<1e-8f)return false;
            float inverse=1/determinant;var delta=origin-a;float u=Vector3.Dot(delta,cross)*inverse;if(u<0||u>1)return false;
            var q=Vector3.Cross(delta,edge1);float v=Vector3.Dot(direction,q)*inverse;if(v<0||u+v>1)return false;
            distance=Vector3.Dot(edge2,q)*inverse;return distance>0;
        }
    }
}
#endif
