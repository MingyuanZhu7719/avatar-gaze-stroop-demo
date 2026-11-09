using System;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Explicit owner-operated, development-only; call Draw from an existing
    // desktop UI at a safe idle stage. Never starts capture or writes files.
    public sealed class ViveSessionEvidencePanel
    {
        readonly ViveSessionEvidenceController controller = new ViveSessionEvidenceController();
        string path = "";
        static readonly string[] MemoryViews={"Readable records","Manifest CSV","Fields CSV","Observed events CSV"};
        GUIStyle wrap;bool memoryV3,recoveryChoice,showSensitive,memoryWasPopulated;int page,memoryView;Vector2 scroll;
        // Called by the owner when this panel becomes hidden or the view stops.
        // Releases references; managed strings are not securely wiped.
        public void ClearSensitive()
        {
            controller.ClearMemory();path="";memoryV3=false;recoveryChoice=false;
            showSensitive=false;memoryWasPopulated=false;page=0;memoryView=0;scroll=Vector2.zero;
        }
        public void Draw()
        {
            if(wrap==null)wrap=new GUIStyle(GUI.skin.label){wordWrap=true};
            GUILayout.Label("Development journal summary (read-only; v2 participant journals only)",wrap);
            GUILayout.Label("Paste the absolute local path of an existing .vdev journal. Analysis may take time.",wrap);
            path = GUILayout.TextField(path);
            if (GUILayout.Button("Add clean journal to summary")) controller.AddClean(path, true);
            if (GUILayout.Button("Add verified incomplete prefix")) controller.AddVerifiedPrefix(path, true, true);
            GUILayout.Label(controller.Message,wrap);
            var s = controller.Summary;
            GUILayout.Label("Accepted: " + s.JournalsAccepted + "; clean closes: " + s.CleanCloseJournals + "; recovered prefixes: " + s.RecoveredPrefixJournals + "; without context: " + s.JournalsWithoutContext,wrap);
            GUILayout.Label("Declared eye source: " + (s.DeclaredEyeDataSource.HasValue?s.DeclaredEyeDataSource.Value.ToString():"unknown") + "; decoded task event origin: " + (s.DecodedAuthoritativeEventOrigin.HasValue?s.DecodedAuthoritativeEventOrigin.Value.ToString():"unknown") + ". These labels do not independently authenticate the source.",wrap);
            GUILayout.Label("Initial unknown windows: " + s.InitialUnknownWindowsSeen + "; recovery unknown windows: " + s.RecoveryUnknownWindowsSeen + "; gaps: " + s.ExplicitGapsSeen + "; unclassified task events: " + s.UnclassifiedTaskEventsSeen,wrap);
            GUILayout.Label("Practice start/end markers seen: " + s.PracticeStartMarkerSeen + "/" + s.PracticeEndMarkerSeen + "; session end marker seen: " + s.SessionEndMarkerSeen,wrap);
            foreach (var b in s.Blocks)
                GUILayout.Label("Block " + (b.BlockIndex + 1) + ": configuration/task end/questionnaire end markers " + b.ConfigurationMarkerSeen + "/" + b.TaskEndMarkerSeen + "/" + b.QuestionnaireEndMarkerSeen + "; rating items " + b.DistinctKnownRatingItemsSeen + "; segment contexts " + b.DistinctSegmentContextsSeen + "; duplicate rating item " + b.DuplicateRatingItemSeen,wrap);
            GUILayout.Label(s.Scope,wrap);
            GUILayout.Label("Selected files only. Practice and missing segments may not be recorded. No export is saved. Source identities, complete coverage, durability and research validity are not proven.",wrap);
            GUILayout.Space(8);
            GUILayout.Label("Sensitive in-memory journal preview (selected v2/v3 only)",wrap);
            GUILayout.Label("This reads the path above only when you press Add. The result may contain eye data and identifiers. No file or clipboard is written; unsupported/missing sources remain unknown. V3 support is for framed VPRAC003 journals, not practice-start envelopes.",wrap);
            GUILayout.Label("Sources are shown separately and are not deduplicated by event or aligned to trials across devices. Only data source, session, participant, device, clock and mapping declarations are compared; operator ID and status are not compared. V3 does not record operator status. No identity is authenticated.",wrap);
            GUILayout.Label("Limits: 8 sources, 4 MiB input and 5,000 records per source, 2 MiB for each in-memory text view. An over-limit addition is refused, never silently truncated.",wrap);
            GUILayout.Label("Selected-source in-memory CSV preview only: b64: values are exact UTF-8 strings encoded as Base64; u64: values are decimal text; bool: fields distinguish null and missing fields. No source times or dates are converted. Fields are sorted for display; original binary framing and hashes are not reproduced. No file or clipboard is written.",wrap);
            GUILayout.Label("Observed events CSV contains only recorded task-event and coverage-marker rows; state:missing and state:null differ. It does not fill absent trials, match eye samples to trials, align clocks, or establish complete session coverage. Named fields are shown only when actually present in a decoded event; the named current-session table is separate.",wrap);
            memoryV3=GUILayout.Toggle(memoryV3,"The selected source is a v3 practice journal (otherwise v2 participant journal).");
            if(GUILayout.Button("Add clean journal to in-memory preview"))
            {showSensitive=false;page=0;memoryView=0;if(controller.AddMemory(path,memoryV3,true,false,false))memoryWasPopulated=true;}
            recoveryChoice=GUILayout.Toggle(recoveryChoice,"I explicitly choose to inspect an incomplete verified prefix.");
            GUI.enabled=recoveryChoice;
            if(GUILayout.Button("Add incomplete verified prefix to in-memory preview"))
            {showSensitive=false;page=0;memoryView=0;if(controller.AddMemory(path,memoryV3,true,true,recoveryChoice))memoryWasPopulated=true;recoveryChoice=false;}
            GUI.enabled=true;
            GUILayout.Label(controller.MemoryMessage,wrap);
            GUILayout.Label("Selected preview sources: "+controller.MemorySourceCount+". The memory view expires 60 seconds after the latest addition. After expiration, the input path is cleared on the next screen update; it is also cleared on clear/exit. If the UI is not running, the timer does not immediately erase that path. Preview construction can create multiple managed copies; clearing releases references but does not securely wipe them.",wrap);
            if(controller.MemorySourceCount>0)GUILayout.Label(controller.MemoryReport,wrap);
            if(GUILayout.Button("Clear sensitive in-memory preview and path"))ClearSensitive();
            if(memoryWasPopulated&&controller.MemorySourceCount==0)ClearSensitive();
            if(controller.MemorySourceCount==0)showSensitive=false;
            if(!showSensitive&&controller.MemorySourceCount>0&&GUILayout.Button("Show sensitive content on this screen")){showSensitive=true;page=0;}
            if(showSensitive)
            {
                if(GUILayout.Button("Hide sensitive content"))showSensitive=false;
                if(showSensitive)
                {
                    int selected=GUILayout.Toolbar(memoryView,MemoryViews);
                    if(selected!=memoryView){memoryView=selected;page=0;scroll=Vector2.zero;}
                    string text=memoryView==1?controller.MemoryManifestCsv:memoryView==2?controller.MemoryFieldCsv:memoryView==3?controller.MemoryEventCsv:controller.MemoryText;
                    const int size=1500;int pages=Math.Max(1,(text.Length+size-1)/size);
                    page=Mathf.Clamp(page,0,pages-1);
                    GUILayout.Label("Screen pages can split rows; this preview does not save or publish data.",wrap);
                    GUILayout.BeginHorizontal();
                    GUI.enabled=page>0;if(GUILayout.Button("Previous"))page--;
                    GUI.enabled=page+1<pages;if(GUILayout.Button("Next"))page++;
                    GUI.enabled=true;GUILayout.Label("Page "+(page+1)+" / "+pages);GUILayout.EndHorizontal();
                    int start=page*size,length=Math.Min(size,text.Length-start);
                    scroll=GUILayout.BeginScrollView(scroll,GUILayout.Height(220));
                    GUILayout.Label(text.Substring(start,length),wrap);
                    GUILayout.EndScrollView();
                }
            }
        }
    }
}
