#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    public static class ViveResponsiveOwnerLayoutFixture
    {
        const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        const BindingFlags S=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static int assertions;
        static readonly Type Layout=typeof(ViveStroopPreviewRig).Assembly.GetType("AvatarStroop.Unity.ViveOwnerDesktopLayout",true);
        static void Require(bool value,string why){if(!value)throw new InvalidOperationException("Responsive owner UI: "+why);assertions++;}
        internal static object Get(object o,string n){return o.GetType().GetField(n,I).GetValue(o);}
        internal static void Set(object o,string n,object v){o.GetType().GetField(n,I).SetValue(o,v);}
        internal static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,I).Invoke(o,a);}
        public static void Validate()
        {
            assertions=0;
            foreach(float width in new[]{320f,480f,800f,1280f})foreach(float height in new[]{480f,720f,1080f})
            {
                var panel=ViveStroopPreviewRig.OwnerPanelBounds(width,height);
                Require(panel.xMin>=0&&panel.yMin>=0&&panel.xMax<=width&&panel.yMax<=height,"panel extends outside window");
                Require(Mathf.Abs(panel.height-(height-24))<.01f,"tall window still wastes scroll height");
                float content=panel.width-68;
                foreach(int count in new[]{1,2,4})
                {
                    float min=count==4?38:110;
                    int columns=(int)Layout.GetMethod("SelectionColumns",S).Invoke(null,new object[]{content,count,min});
                    Require(columns>=1&&columns<=count&&(content-6*(columns-1))/columns>=Mathf.Min(min,content),"responsive column sizing clips a choice");
                }
            }
            Require((float)Layout.GetMethod("ScaleForDpi",S).Invoke(null,new object[]{192f})==2f,"DPI scaling changed");
            Debug.Log("VIVE_RESPONSIVE_OWNER_LAYOUT_FIXTURE_PASSED: "+assertions+" bounds/column/DPI assertions. Actual IMGUI is tested by ValidateGui.");
        }

        static ViveResponsiveOwnerGuiWindow window;
        static GameObject ownerObject,rigObject;
        static Scene scene;
        static int sizeIndex,stage,lastPaint,switches,localIndex;
        static double deadline;
        static string output;
        static readonly Vector2[] sizes={new Vector2(320,640),new Vector2(480,720),new Vector2(800,900),new Vector2(1280,900)};
        public static void ValidateGui()
        {
            Validate();
            Require(Application.isBatchMode&&!Application.isPlaying,"isolated batch Editor only");
            Require(SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null,"actual IMGUI requires graphics, not -nographics");
            output=Environment.GetEnvironmentVariable("STROOP_OWNER_UI_FIXTURE_OUTPUT");
            Require(!String.IsNullOrEmpty(output)&&Path.IsPathRooted(output)&&!Directory.Exists(output)&&!File.Exists(output),"fresh explicit output directory required");
            Directory.CreateDirectory(output);
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            ownerObject=new GameObject("SyntheticResponsiveOwner");SceneManager.MoveGameObjectToScene(ownerObject,scene);
            rigObject=new GameObject("SyntheticResponsiveRig");SceneManager.MoveGameObjectToScene(rigObject,scene);
            var owner=ownerObject.AddComponent<ViveStroopDevelopmentBootstrap>();var rig=rigObject.AddComponent<ViveStroopPreviewRig>();
            Set(owner,"rig",rig);Set(rig,"development",owner);
            Set(owner,"ownerRunMode",0);Set(owner,"selectedDesign",3);Set(owner,"namedPlanCount",3);
            Set(owner,"namedPlanOptions",new[]{0,1,0,1});Set(owner,"namedPlanLists",new[]{0,1,2,3});
            window=ScriptableObject.CreateInstance<ViveResponsiveOwnerGuiWindow>();window.Rig=rig;window.Owner=owner;
            window.minSize=new Vector2(200,200);window.maxSize=new Vector2(1800,1200);window.titleContent=new GUIContent("Synthetic responsive owner fixture");
            sizeIndex=0;stage=0;switches=0;localIndex=0;deadline=EditorApplication.timeSinceStartup+120;
            window.ShowUtility();EditorApplication.update+=Step;
        }
        static void Step()
        {
            try
            {
                Require(EditorApplication.timeSinceStartup<deadline,"actual IMGUI repaint timeout");
                if(window.Failure!=null)throw window.Failure;
                if(stage==0)
                {
                    var size=sizes[sizeIndex];window.position=new Rect(30,30,size.x,size.y);
                    Set(window.Rig,"ownerScroll",Vector2.zero);Set(window.Owner,"selectedDesign",3);
                    lastPaint=window.Paint;stage=1;window.Repaint();return;
                }
                window.Repaint();if(window.Paint<=lastPaint)return;lastPaint=window.Paint;
                if(stage>=10){LocalStep();return;}
                if(stage==1||stage==3)
                {
                    VerifyGrid();Require((int)Get(window.Owner,"selectedDesign")==3,"switch back did not select Named multi-block");
                    if(stage==1)Save("top-"+(int)sizes[sizeIndex].x);
                    if(switches++<2){Click("preview-design:0");stage=2;return;}
                    Set(window.Rig,"ownerScroll",new Vector2(0,100000));stage=4;return;
                }
                if(stage==2)
                {
                    Require((int)Get(window.Owner,"selectedDesign")==0,"actual mouse click did not select Legacy");
                    Click("preview-design:3");stage=3;return;
                }
                VerifyGrid();var viewport=window.Rectangles["desktop-scroll"];var submit=window.Rectangles["setup-submit"];
                Require(submit.yMin>=viewport.yMin-2&&submit.yMax<=viewport.yMax+2,"bottom action is not reachable by vertical scroll");
                Require(((Vector2)Get(window.Rig,"ownerScroll")).x==0,"body acquired horizontal scroll");
                var options=(int[])Get(window.Owner,"namedPlanOptions");var lists=(int[])Get(window.Owner,"namedPlanLists");
                for(int i=0;i<4;i++)Require(options[i]==i%2&&lists[i]==i,"resize/design switches altered deliberate block settings");
                Require(Get(window.Owner,"participant")==null&&Get(window.Owner,"namedRawPump")==null&&Get(window.Rig,"worker")==null&&!window.Rig.ViewRunning,"layout test started task/eye/XR/output");
                Save("bottom-"+(int)sizes[sizeIndex].x);
                if(++sizeIndex<sizes.Length){stage=0;switches=0;return;}
                stage=10;LocalStep();
            }
            catch(Exception e){Debug.LogException(e);Finish(1);}
        }
        static void LocalStep()
        {
            if(stage==10)
            {
                window.position=new Rect(30,30,localIndex==0?320:800,localIndex==0?640:900);
                Set(window.Owner,"ownerRunMode",1);Set(window.Owner,"selectedDesign",3);Set(window.Owner,"namedPlanCount",3);
                Set(window.Owner,"namedPlanOptions",new[]{0,1,0,-1});Set(window.Owner,"namedPlanLists",new[]{0,1,2,-1});
                Set(window.Owner,"recordingDirectory",Path.Combine(output,"MustNotCreateSession"));
                Set(window.Rig,"ownerScroll",Vector2.zero);stage=11;window.Repaint();return;
            }
            if(stage==11)
            {
                VerifyGrid();Require(window.Rectangles.ContainsKey("block-3-validation")&&!window.Enabled["setup-submit"],"incomplete local block lacks inline feedback or Prepare remains enabled");
                Require(((string)Call(window.Owner,"RecordingSetupValidationReason")).StartsWith("Block 4:",StringComparison.Ordinal),"local missing-row summary points to wrong block");
                Save("local-missing-top-"+(int)window.position.width);stage=12;return;
            }
            if(stage==12)
            {
                var first=window.Rectangles["block-3-option:0"];var last=window.Rectangles["block-3-list:2"];var viewport=window.Rectangles["desktop-scroll"];
                if(first.yMin<viewport.yMin+2||last.yMax>viewport.yMax-2)
                {
                    float delta=first.yMin<viewport.yMin+2?-5:5;
                    window.SendEvent(new Event{type=EventType.ScrollWheel,delta=new Vector2(0,delta),mousePosition=viewport.center-window.Origin});window.Repaint();return;
                }
                stage=13;
            }
            if(stage==13)
            {
                var row=window.Rectangles["block-3-option:0"];var viewport=window.Rectangles["desktop-scroll"];
                Require(row.yMin>=viewport.yMin-2&&row.yMax<=viewport.yMax+2,"normal scrolling did not expose the unresolved block");
                Save("local-missing-row-"+(int)window.position.width);Click("block-3-option:0");stage=14;return;
            }
            if(stage==14){Click("block-3-list:2");stage=15;return;}
            Require(Call(window.Owner,"RecordingSetupValidationReason")==null&&!window.Rectangles.ContainsKey("block-3-validation"),"deliberate mouse selections did not clear missing-row validation");
            var options=(int[])Get(window.Owner,"namedPlanOptions");var lists=(int[])Get(window.Owner,"namedPlanLists");
            Require(options[0]==0&&options[1]==1&&options[2]==0&&options[3]==0&&lists[0]==0&&lists[1]==1&&lists[2]==2&&lists[3]==2,"local selection changed unrelated scientific controls");
            Require(Get(window.Owner,"participant")==null&&Get(window.Owner,"recordingSetup")==null&&Get(window.Owner,"consentAutoStart")==null&&
                Get(window.Owner,"namedRawPump")==null&&Get(window.Rig,"worker")==null&&!window.Rig.ViewRunning&&!Directory.Exists(Path.Combine(output,"MustNotCreateSession")),
                "correcting layout/selections started setup, consent, files or XR");
            Save("local-corrected-row-"+(int)window.position.width);
            if(++localIndex<2){stage=10;return;}
            Debug.Log("VIVE_RESPONSIVE_OWNER_GUI_FIXTURE_PASSED: "+assertions+" assertions; actual production IMGUI at four sizes, repeated mouse design switches, local four-block missing-row feedback and actual mouse-wheel scrolling, deliberate correction without file/consent action. No XR/player or recording. Windows DPI switching not exercised.");
            Finish(0);
        }
        static void VerifyGrid()
        {
            Require(window.Rectangles.ContainsKey("block-3-list:3"),"fourth block controls were not laid out");
            var viewport=window.Rectangles["desktop-scroll"];var footer=window.Rectangles["desktop-close"];
            Require(viewport.yMax<=footer.yMin&&footer.yMax<=window.Origin.y+window.position.height,"footer overlaps scroll or leaves window");
            var skin=(GUISkin)Get(window.Rig,"ownerSkin");
            foreach(var pair in window.Rectangles)
            {
                if(pair.Key.StartsWith("desktop-",StringComparison.Ordinal)||pair.Key.EndsWith("validation",StringComparison.Ordinal))continue;
                Require(pair.Value.xMin>=viewport.xMin-2&&pair.Value.xMax<=viewport.xMax+2,"choice clips horizontally: "+pair.Key);
                string text=pair.Key=="preview-design:1"?"Named Automatic (one block)":pair.Key=="preview-design:2"?"Named No Avatar (one block)":
                    pair.Key.EndsWith("option:0",StringComparison.Ordinal)?"Automatic":pair.Key.EndsWith("option:1",StringComparison.Ordinal)?"No Avatar":null;
                if(text!=null)Require(pair.Value.height+1>=skin.button.CalcHeight(new GUIContent(text),pair.Value.width),"wrapped label clipped vertically: "+text);
            }
        }
        static void Click(string key)
        {
            var r=window.Rectangles[key];var view=window.Rectangles["desktop-scroll"];
            Require(r.yMin>=view.yMin-1&&r.yMax<=view.yMax+1,"test target not visible: "+key);
            var point=r.center-window.Origin;
            bool down=window.SendEvent(new Event{type=EventType.MouseDown,button=0,mousePosition=point});
            bool up=window.SendEvent(new Event{type=EventType.MouseUp,button=0,mousePosition=point});window.Repaint();
            Debug.Log("OWNER_UI_CLICK "+key+" rect="+r+" origin="+window.Origin+" point="+point+" down="+down+" up="+up);
        }
        static void Save(string name)
        {
            int width=(int)window.position.width,height=(int)window.position.height;
            var colors=UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(window.Origin,width,height);
            var texture=new Texture2D(width,height,TextureFormat.RGB24,false);
            try{texture.SetPixels(colors);texture.Apply();File.WriteAllBytes(Path.Combine(output,name+".png"),texture.EncodeToPNG());}
            finally{UnityEngine.Object.DestroyImmediate(texture);}
        }
        static void Finish(int code)
        {
            EditorApplication.update-=Step;
            if(window!=null){window.Close();window=null;}
            if(ownerObject!=null)UnityEngine.Object.DestroyImmediate(ownerObject);if(rigObject!=null)UnityEngine.Object.DestroyImmediate(rigObject);
            if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            EditorApplication.Exit(code);
        }
        internal static void SetObserver(Action<string,Rect> observe){Layout.GetField("ObserveForFixture",S).SetValue(null,observe);}
    }
    public sealed class ViveResponsiveOwnerGuiWindow:EditorWindow
    {
        internal ViveStroopPreviewRig Rig;
        internal ViveStroopDevelopmentBootstrap Owner;
        internal int Paint;
        internal Vector2 Origin;
        internal Exception Failure;
        internal readonly Dictionary<string,Rect> Rectangles=new Dictionary<string,Rect>();
        internal readonly Dictionary<string,bool> Enabled=new Dictionary<string,bool>();
        void OnGUI()
        {
            if(Rig==null)return;
            try
            {
                if(Event.current.type==EventType.Repaint){Rectangles.Clear();Enabled.Clear();Origin=GUIUtility.GUIToScreenPoint(Vector2.zero);}
                ViveResponsiveOwnerLayoutFixture.SetObserver((key,rect)=>{Rectangles[key]=rect;Enabled[key]=GUI.enabled;});
                ViveResponsiveOwnerLayoutFixture.Call(Rig,"DrawOwnerDesktop",position.width,position.height);
                if(Event.current.type==EventType.Repaint)Paint++;
            }
            catch(Exception e){Failure=e;}
            finally{ViveResponsiveOwnerLayoutFixture.SetObserver(null);}
        }
    }
}
#endif
