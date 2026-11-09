using System;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Desktop owner UI; host must draw only when the view and recorder have
    // stopped. The host also calls ClearSensitive on hide/focus loss/exit.
    public sealed class ViveRawSessionEvidencePanel
    {
        readonly ViveRawSessionEvidenceController controller=new ViveRawSessionEvidenceController();
        string path="";
        bool prefixChoice,show,wasPopulated;
        int page;
        Vector2 scroll;
        GUIStyle wrap;

        public void ClearSensitive()
        {
            controller.ClearSensitive();path="";prefixChoice=false;show=false;
            wasPopulated=false;page=0;scroll=Vector2.zero;
        }
        public void Draw()
        {
            if(wrap==null)wrap=new GUIStyle(GUI.skin.label){wordWrap=true};
            GUILayout.Label("Selected VRAW0002 source: read-only memory preview",wrap);
            GUILayout.Label("This format has no verified final close. A verified prefix does not establish complete coverage, consent, source identity, durability, or research validity. It may contain eye data and identifiers.",wrap);
            GUILayout.Label("No file or clipboard is written. Input is limited to one selected local file, 4 MiB and 5,000 frames; the display is bounded. No missing trial or cross-device alignment is inferred.",wrap);
            path=GUILayout.TextField(path);
            prefixChoice=GUILayout.Toggle(prefixChoice,"I explicitly choose to inspect the selected incomplete verified prefix.");
            bool oldEnabled=GUI.enabled;
            GUI.enabled=oldEnabled&&prefixChoice;
            if(GUILayout.Button("Read selected verified prefix"))
            {
                show=false;page=0;scroll=Vector2.zero;
                wasPopulated=controller.TryLoad(path,true,prefixChoice);
                prefixChoice=false;
            }
            GUI.enabled=oldEnabled;
            GUILayout.Label(controller.Message,wrap);
            if(controller.HasPreview&&controller.TrailingFrameTruncated)
                GUILayout.Label("The final frame was incomplete and was discarded; this preview stops before it.",wrap);
            if(controller.HasPreview)
                GUILayout.Label("Rows use ordinal/record kind/field/type/value. f64_bits_hex preserves floating-point bits; utf8_b64 represents exact UTF-8 strings.",wrap);
            GUILayout.Label("Preview references expire after 60 seconds. The path is cleared on the next screen update after expiration, and on clear, hide, focus loss or exit. Managed copies cannot be securely erased here.",wrap);
            if(GUILayout.Button("Clear raw preview and path"))ClearSensitive();
            if(wasPopulated&&!controller.HasPreview)ClearSensitive();
            if(!controller.HasPreview)show=false;
            if(!show&&controller.HasPreview&&GUILayout.Button("Show sensitive content on this screen")){show=true;page=0;}
            if(show)
            {
                if(GUILayout.Button("Hide sensitive content"))show=false;
                if(show)
                {
                    string text=controller.PreviewText;
                    if(text==null){show=false;return;}
                    const int size=1500;int pages=Math.Max(1,(text.Length+size-1)/size);
                    page=Mathf.Clamp(page,0,pages-1);
                    GUILayout.Label("Screen pages may split rows; nothing is saved.",wrap);
                    GUILayout.BeginHorizontal();
                    oldEnabled=GUI.enabled;GUI.enabled=oldEnabled&&page>0;if(GUILayout.Button("Previous"))page--;
                    GUI.enabled=oldEnabled&&page+1<pages;if(GUILayout.Button("Next"))page++;
                    GUI.enabled=oldEnabled;GUILayout.Label("Page "+(page+1)+" / "+pages);GUILayout.EndHorizontal();
                    int start=page*size,length=Math.Min(size,text.Length-start);
                    scroll=GUILayout.BeginScrollView(scroll,GUILayout.Height(220));
                    GUILayout.Label(text.Substring(start,length),wrap);
                    GUILayout.EndScrollView();
                }
            }
        }
    }
}
