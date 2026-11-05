using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AvatarStroop.Runtime
{
    // Launch-only configuration. No registry, user/system environment or current
    // process environment is modified. The OpenXR loader sees the child override
    // before Unity's native pre-initialization, which can run before Mono Awake.
    public sealed class VivePreviewLaunchPlan
    {
        public const string RuntimeVariable="XR_RUNTIME_JSON";
        public const string MarkerVariable="STROOP_VIVE_PREVIEW_RUNTIME";
        public const string MarkerValue="steamvr-child-process-v1";
        public const string PlayerName="AvatarStroopViveEyePlayer.exe";
        public const string LauncherProcessName="AvatarStroopViveEyePreview";
        public const string PlayerProcessName="AvatarStroopViveEyePlayer";
        public string PlayerPath {get;private set;}
        public string WorkingDirectory {get;private set;}
        public string RuntimeManifestPath {get;private set;}
        public string RuntimeLibraryPath {get;private set;}
        public static string InstalledSteamVrManifest
        {
            get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam","steamapps","common","SteamVR","steamxr_win64.json");}
        }

        public static VivePreviewLaunchPlan Create(string packageDirectory,string manifestPath)
        {
            string package=Absolute(packageDirectory),manifest=Absolute(manifestPath);
            if(!String.Equals(Path.GetFileName(manifest),"steamxr_win64.json",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The VIVE preview requires SteamVR's installed Windows x64 runtime manifest.");
            if(!File.Exists(manifest))throw new FileNotFoundException("Installed SteamVR runtime manifest is missing. This launcher expects the standard Program Files (x86)/Steam/steamapps/common/SteamVR installation. No player was launched.");
            if(new FileInfo(manifest).Length>65536)throw new InvalidDataException("SteamVR runtime manifest is unexpectedly large.");
            var document=ManifestReader.Parse(File.ReadAllText(manifest,Encoding.UTF8));
            object version,runtimeObject;
            if(!document.TryGetValue("file_format_version",out version)||!Object.Equals(version,"1.0.0")||
                !document.TryGetValue("runtime",out runtimeObject)||!(runtimeObject is Dictionary<string,object>))
                throw new InvalidDataException("SteamVR runtime manifest has an unsupported format.");
            var runtime=(Dictionary<string,object>)runtimeObject;
            object name,valve,library;
            if(!runtime.TryGetValue("name",out name)||!Object.Equals(name,"SteamVR")||
                !runtime.TryGetValue("VALVE_runtime_is_steamvr",out valve)||!Object.Equals(valve,true)||
                !runtime.TryGetValue("library_path",out library)||!(library is string)||
                !String.Equals(((string)library).Replace('\\','/'),"bin/vrclient_x64.dll",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Runtime manifest does not identify the installed SteamVR Windows x64 library. No fallback runtime is used.");
            string runtimeLibrary=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifest),"bin","vrclient_x64.dll"));
            if(!File.Exists(runtimeLibrary))throw new FileNotFoundException("Installed SteamVR Windows x64 runtime library is missing. No player was launched.");
            string playerDirectory=Path.GetFullPath(Path.Combine(package,"UnityPlayer"));
            string player=Path.Combine(playerDirectory,PlayerName);
            string dataDirectory=Path.Combine(playerDirectory,"AvatarStroopViveEyePlayer_Data");
            if(!File.Exists(player)||!Directory.Exists(dataDirectory))
                throw new FileNotFoundException("The VIVE Unity player or its data folder is missing. Keep the complete preview package together.");
            RejectLinks(manifest);RejectLinks(runtimeLibrary);RejectLinks(player);RejectLinks(dataDirectory);
            return new VivePreviewLaunchPlan {PlayerPath=player,WorkingDirectory=playerDirectory,
                RuntimeManifestPath=manifest,RuntimeLibraryPath=runtimeLibrary};
        }

        public ProcessStartInfo CreateProcessStartInfo()
        {
            var result=new ProcessStartInfo {FileName=PlayerPath,WorkingDirectory=WorkingDirectory,UseShellExecute=false,
                CreateNoWindow=false,WindowStyle=ProcessWindowStyle.Normal,Arguments=""};
            // The inherited dictionary belongs to this proposed CHILD process.
            // Deliberately do not call Environment.SetEnvironmentVariable.
            result.EnvironmentVariables[RuntimeVariable]=RuntimeManifestPath;
            result.EnvironmentVariables[MarkerVariable]=MarkerValue;
            return result;
        }

        public static bool IsExpectedChildEnvironment(string marker,string manifest)
        {
            if(marker!=MarkerValue||String.IsNullOrWhiteSpace(manifest)||!Path.IsPathRooted(manifest))return false;
            try{return String.Equals(Path.GetFileName(Absolute(manifest)),"steamxr_win64.json",StringComparison.OrdinalIgnoreCase);}
            catch(ArgumentException){return false;}
            catch(NotSupportedException){return false;}
            catch(PathTooLongException){return false;}
        }
        static string Absolute(string path)
        {
            if(String.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path))throw new ArgumentException("A fully qualified local path is required.");
            string full=Path.GetFullPath(path);
            if(Path.DirectorySeparatorChar=='\\'&&(full.StartsWith("\\\\",StringComparison.Ordinal)||Path.GetPathRoot(path).Length<3))
                throw new ArgumentException("A fully qualified local drive path is required.");
            return full;
        }
        static void RejectLinks(string path)
        {
            for(string current=path;!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("Linked runtime/player paths are not used by this launcher.");
        }

        // Bounded, strict parser for the installed manifest's object/string/bool
        // schema; duplicate keys and unsupported value types fail closed.
        sealed class ManifestReader
        {
            readonly string text;int offset;
            ManifestReader(string value){text=value;}
            internal static Dictionary<string,object> Parse(string text)
            {
                var parser=new ManifestReader(text);var result=parser.ObjectValue(0);parser.Space();
                if(parser.offset!=text.Length)throw Invalid();return result;
            }
            Dictionary<string,object> ObjectValue(int depth)
            {
                if(depth>8)throw Invalid();Space();Take('{');var result=new Dictionary<string,object>(StringComparer.Ordinal);Space();
                if(Peek('}')){offset++;return result;}
                while(true)
                {
                    Space();string key=StringValue();Space();Take(':');Space();object value;
                    if(Peek('"'))value=StringValue();
                    else if(Peek('{'))value=ObjectValue(depth+1);
                    else if(Token("true"))value=true;
                    else if(Token("false"))value=false;
                    else throw Invalid();
                    if(result.ContainsKey(key))throw Invalid();result.Add(key,value);Space();
                    if(Peek('}')){offset++;return result;}Take(',');
                }
            }
            string StringValue()
            {
                Take('"');var result=new StringBuilder();
                while(offset<text.Length)
                {
                    char next=text[offset++];if(next=='"')return result.ToString();if(next<32)throw Invalid();
                    if(next=='\\')
                    {
                        if(offset>=text.Length)throw Invalid();next=text[offset++];
                        switch(next)
                        {
                            case '"':case '\\':case '/':result.Append(next);break;
                            case 'b':result.Append('\b');break;case 'f':result.Append('\f');break;
                            case 'n':result.Append('\n');break;case 'r':result.Append('\r');break;case 't':result.Append('\t');break;
                            case 'u':
                                if(offset+4>text.Length)throw Invalid();int code;
                                if(!Int32.TryParse(text.Substring(offset,4),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out code))throw Invalid();
                                result.Append((char)code);offset+=4;break;
                            default:throw Invalid();
                        }
                    }
                    else result.Append(next);
                }
                throw Invalid();
            }
            void Space(){while(offset<text.Length&&(text[offset]==' '||text[offset]=='\t'||text[offset]=='\n'||text[offset]=='\r'))offset++;}
            bool Peek(char value){return offset<text.Length&&text[offset]==value;}
            void Take(char value){if(!Peek(value))throw Invalid();offset++;}
            bool Token(string value){if(offset+value.Length>text.Length||String.CompareOrdinal(text,offset,value,0,value.Length)!=0)return false;offset+=value.Length;return true;}
            static InvalidDataException Invalid(){return new InvalidDataException("SteamVR runtime manifest is malformed or unsupported. No player was launched.");}
        }
    }
}
