#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity.Editor
{
    // Actual synthetic camera renders, without XR, acquisition or networking.
    // Comparing each label against its hidden render catches missing glyphs or
    // atlas bindings that a text string / renderer-enabled assertion cannot.
    public static class ViveWozOperatorHudFixture
    {
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Operator HUD fixture: "+message);}

        static Color32[] Read(Camera camera,RenderTexture target,Texture2D pixels)
        {
            camera.Render();
            RenderTexture.active=target;
            pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0);
            pixels.Apply();
            return pixels.GetPixels32();
        }

        static void CheckGlyphs(Camera camera,WorldSpaceExperimentView view,string stage,string[] required)
        {
            var root=camera.transform.Find("HeadsetVisibleDevelopmentWarning");
            Require(root!=null&&Mathf.Abs(root.localPosition.z-2f)<.001f,"HUD is not at 2 metres");
            var target=new RenderTexture(1000,760,24,RenderTextureFormat.ARGB32);
            var pixels=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
            var oldTarget=camera.targetTexture;var oldActive=RenderTexture.active;
            try
            {
                camera.targetTexture=target;
                // Edit-mode synthetic renders do not run MonoBehaviour.LateUpdate.
                // Use the same layout pass the live operator runs each frame.
                typeof(WorldSpaceExperimentView).GetMethod("LayoutHud",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(view,null);
                Read(camera,target,pixels);
                foreach(string name in required)
                    Require(root.Find(name).GetComponent<MeshRenderer>().enabled,"required label hidden in the actual layout: "+name);
                string output=Environment.GetEnvironmentVariable("STROOP_VIVE_OPERATOR_OUTPUT");
                if(!String.IsNullOrWhiteSpace(output)&&Path.IsPathRooted(output))
                    File.WriteAllBytes(Path.GetFullPath(output)+"-hud-"+stage+".png",pixels.EncodeToPNG());

                // Freeze this completed layout for differential rendering: the
                // usual pre-cull layout would otherwise re-enable hidden labels.
                view.enabled=false;
                var labels=root.GetComponentsInChildren<TextMesh>(true);
                foreach(var label in labels)label.GetComponent<MeshRenderer>().enabled=false;
                var background=Read(camera,target,pixels);
                foreach(string name in required)
                {
                    var item=root.Find(name);Require(item!=null,"missing label "+name);
                    var text=item.GetComponent<TextMesh>();var renderer=item.GetComponent<MeshRenderer>();
                    Require(text!=null&&!String.IsNullOrWhiteSpace(text.text)&&renderer.localBounds.size.y>.001f,
                        "empty glyph mesh for "+name);
                    renderer.enabled=true;
                    var shown=Read(camera,target,pixels);int visible=0;
                    for(int i=0;i<shown.Length;i++)
                    {
                        int difference=Math.Abs(shown[i].r-background[i].r)+Math.Abs(shown[i].g-background[i].g)+Math.Abs(shown[i].b-background[i].b);
                        if(difference>90&&Math.Max(shown[i].r,Math.Max(shown[i].g,shown[i].b))>100)visible++;
                    }
                    Require(visible>=30,"label did not produce visible glyph pixels: "+name+" ("+visible+")");
                    renderer.enabled=false;
                }
            }
            finally
            {
                view.enabled=true;camera.targetTexture=oldTarget;RenderTexture.active=oldActive;
                UnityEngine.Object.DestroyImmediate(pixels);UnityEngine.Object.DestroyImmediate(target);
            }
        }

        public static void Validate()
        {
            Require(SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null,"render verification needs a graphics device; do not use -nographics");
            var camera=new GameObject("OperatorHudFixtureCamera").AddComponent<Camera>();
            var view=new GameObject("OperatorHudFixtureView").AddComponent<WorldSpaceExperimentView>();
            try
            {
                camera.transform.position=new Vector3(0,1.6f,0);camera.nearClipPlane=.05f;camera.farClipPlane=20;
                view.Initialize(camera,Actor.Operator);
                camera.transform.rotation=Quaternion.identity;
                view.PlaceWozOperatorView(camera.transform.position);
                view.ConfigureWozOperatorHeadsetPresentation();
                view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(null));
                view.SetWozOperatorHeadsetStatus("Waiting for the participant.\nUse the desktop to create a room.");
                CheckGlyphs(camera,view,"setup",new[]{"PermanentDevelopmentBanner","RoleAndPhase","ParticipantInstructions","WorldSpaceFaultMessages"});
                view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(new TaskSnapshot("hud-render",0,1,"trial",Colour.Red,Colour.Blue,Phase.MainTask,Condition.A)));
                view.BeginWozParticipantAvatarView(camera.transform.position);
                view.ShowWozParticipantPose(Rotation.Identity,new Vec3(0,0,1));
                view.SetWozOperatorHeadsetStatus("Look naturally at the participant or whiteboard.");
                CheckGlyphs(camera,view,"task",new[]{"WorldSpaceFaultMessages"});
                var message=camera.transform.Find("HeadsetVisibleDevelopmentWarning/ParticipantInstructions").GetComponent<TextMesh>();
                Require(message.text.Length==0,"live task retains a central instruction panel");
                var root=camera.transform.Find("HeadsetVisibleDevelopmentWarning");
                Require(!root.Find("WarningBacking").gameObject.activeSelf&&!root.Find("OperatorInstructionsBacking").gameObject.activeSelf,
                    "live task retains an opaque header or central panel over the participant or board");
                Debug.Log("WOZ_OPERATOR_HUD_RENDER_PASS: setup and task glyphs rendered; 2 m central headset panel; live centre clear. Synthetic mono only, not headset acceptance.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(view.gameObject);
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }
        }
    }
}
#endif
