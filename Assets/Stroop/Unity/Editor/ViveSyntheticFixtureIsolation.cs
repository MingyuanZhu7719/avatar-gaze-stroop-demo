#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Explicit batch-only fixture preparation. This opens one already-saved
    // scene in a separately prepared project; it never saves an open scene.
    internal static class ViveSyntheticFixtureIsolation
    {
        const string AnchorScene="Assets/Stroop/Generated/ParticipantQuestPreview.unity";
        const string ExpectedEditor="6000.3.25f1";
        [Serializable] sealed class Preparation
        {public int format=0;public string source=null;public string editor_version=null;}
        [Serializable] sealed class Configuration
        {public string editor=null;public string participant_scene=null;public bool automatic_xr_start=false;}

        static void Require(bool condition,string reason)
        {if(!condition)throw new InvalidOperationException("Synthetic VIVE fixture isolation: "+reason);}
        static bool IsAbsoluteLocalPath(string value)
        {
            if(String.IsNullOrWhiteSpace(value)||!Path.IsPathRooted(value))return false;
            // Path.IsPathRooted alone accepts Windows drive-relative C:folder
            // and current-drive-rooted \\folder. Avoid those and network paths.
            if(Path.DirectorySeparatorChar=='\\')
                return value.Length>=3&&Char.IsLetter(value[0])&&value[1]==':'&&
                    (value[2]=='\\'||value[2]=='/');
            return true;
        }
        static string AbsoluteDirectory(string value,string label)
        {
            Require(IsAbsoluteLocalPath(value),label+" must be an explicit absolute local directory");
            string path=Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            Require(IsAbsoluteLocalPath(path),label+" must not be a filesystem root");
            Require(Directory.Exists(path),label+" is unavailable");return path;
        }
        static void NoReparse(string path)
        {Require((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"reparse points are not allowed within the isolated fixture project");}
        static void CheckAncestors(string path)
        {
            DirectoryInfo current=new DirectoryInfo(path);
            while(current!=null){NoReparse(current.FullName);current=current.Parent;}
        }
        static bool ContainsOrEquals(string parent,string child)
        {return String.Equals(parent,child,StringComparison.OrdinalIgnoreCase)||child.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}
        // JsonUtility supplies false for a missing bool, so require exactly one
        // unescaped top-level key with an explicit false literal as well.
        static bool HasExplicitFalse(string json,string key)
        {
            if(String.IsNullOrEmpty(json))return false;
            int depth=0,count=0;bool valid=false;
            for(int i=0;i<json.Length;i++)
            {
                char c=json[i];
                if(c=='{'||c=='['){depth++;continue;}
                if(c=='}'||c==']'){depth--;if(depth<0)return false;continue;}
                if(c!='"')continue;
                int start=++i;bool escaped=false;
                while(i<json.Length&&json[i]!='"')
                {if(json[i]=='\\'){escaped=true;i++;}i++;}
                if(i>=json.Length)return false;
                bool match=!escaped&&depth==1&&i-start==key.Length&&String.CompareOrdinal(json,start,key,0,key.Length)==0;
                if(!match)continue;
                int j=i+1;while(j<json.Length&&Char.IsWhiteSpace(json[j]))j++;
                if(j>=json.Length||json[j]!=':')continue;
                count++;j++;while(j<json.Length&&Char.IsWhiteSpace(json[j]))j++;
                bool literal=j+5<=json.Length&&String.CompareOrdinal(json,j,"false",0,5)==0;
                j+=literal?5:0;while(j<json.Length&&Char.IsWhiteSpace(json[j]))j++;
                valid=literal&&j<json.Length&&(json[j]==','||json[j]=='}');
            }
            return depth==0&&count==1&&valid;
        }
        static void CheckComponents(string parent,string child)
        {
            NoReparse(parent);
            string current=parent;
            string relative=child.Substring(parent.Length).TrimStart(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            foreach(string component in relative.Split(new[]{Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar},StringSplitOptions.RemoveEmptyEntries))
            {current=Path.Combine(current,component);NoReparse(current);}
        }

        internal static void OpenKnownSavedAnchor()
        {
            Require(Application.isBatchMode&&!Application.isPlaying&&Application.unityVersion==ExpectedEditor,
                "batch Edit Mode with the fixed Unity version is required");
            Require(Environment.GetEnvironmentVariable("STROOP_VIVE_SYNTHETIC_FIXTURE")=="1",
                "explicit synthetic fixture marker is required");
            string workspace=AbsoluteDirectory(Environment.GetEnvironmentVariable("STROOP_VIVE_FIXTURE_WORKSPACE"),"workspace");
            string expected=AbsoluteDirectory(Environment.GetEnvironmentVariable("STROOP_VIVE_FIXTURE_PROJECT"),"fixture project");
            string actual=Path.GetFullPath(Path.Combine(Application.dataPath,"..")).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            Require(String.Equals(actual,expected,StringComparison.OrdinalIgnoreCase),"explicit project does not match the running project");
            Require(actual.StartsWith(workspace+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"project must be a strict workspace child");
            CheckAncestors(workspace);CheckComponents(workspace,actual);
            string preparationPath=Path.Combine(actual,"quest-preview-preparation.json");
            string configuredPath=Path.Combine(actual,"quest-preview-configured.json");
            string anchorPath=Path.Combine(actual,AnchorScene.Replace('/',Path.DirectorySeparatorChar));
            Require(File.Exists(preparationPath)&&File.Exists(configuredPath)&&File.Exists(anchorPath),"configured isolated project or saved anchor is missing");
            CheckComponents(actual,preparationPath);CheckComponents(actual,configuredPath);CheckComponents(actual,anchorPath);
            var preparation=JsonUtility.FromJson<Preparation>(File.ReadAllText(preparationPath));
            Require(preparation!=null&&preparation.format==1&&preparation.editor_version==ExpectedEditor&&
                IsAbsoluteLocalPath(preparation.source),
                "project preparation marker does not identify a separate source");
            string source=Path.GetFullPath(preparation.source).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            Require(IsAbsoluteLocalPath(source)&&Directory.Exists(source),"declared source directory is unavailable");CheckAncestors(source);
            Require(!ContainsOrEquals(source,actual)&&!ContainsOrEquals(actual,source),"source and fixture project must not contain one another");
            string configurationText=File.ReadAllText(configuredPath);
            var configuration=JsonUtility.FromJson<Configuration>(configurationText);
            Require(configuration!=null&&configuration.editor==ExpectedEditor&&configuration.participant_scene==AnchorScene&&
                !configuration.automatic_xr_start&&HasExplicitFalse(configurationText,"automatic_xr_start"),
                "project is not configured for the saved, manually started XR preview");
            Require(SceneManager.sceneCount==1,"exactly one initial scene is required");
            Scene initial=SceneManager.GetSceneAt(0);
            Require(initial.IsValid()&&initial.isLoaded&&!initial.isDirty,"initial scene must be loaded and unmodified");
            Require(String.IsNullOrEmpty(initial.path)||String.Equals(initial.path,AnchorScene,StringComparison.Ordinal),
                "an unrelated scene is open");
            Scene anchor=EditorSceneManager.OpenScene(AnchorScene,OpenSceneMode.Single);
            Require(anchor.IsValid()&&anchor.isLoaded&&!anchor.isDirty&&String.Equals(anchor.path,AnchorScene,StringComparison.Ordinal),
                "saved fixture anchor could not be opened cleanly");
        }
    }
}
#endif
