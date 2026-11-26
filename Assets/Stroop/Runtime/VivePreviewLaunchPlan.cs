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
        public const string OpenVrRuntimeVariable="VR_OVERRIDE";
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
            get{return ResolveRegisteredSteamVrManifest(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"openvr","openvrpaths.vrpath"));}
        }

        // SteamVR's registration is the source of truth, including installations
        // on another drive. Never silently fall back to an old default copy.
        public static string ResolveRegisteredSteamVrManifest(string registrationPath)
        {
            string registration=Absolute(registrationPath);
            if(!File.Exists(registration))throw new FileNotFoundException("SteamVR registration is missing. Start the installed SteamVR once, then reopen this launcher.",registration);
            if(new FileInfo(registration).Length>65536)throw new InvalidDataException("SteamVR registration is unexpectedly large.");
            RejectLinks(registration);
            Dictionary<string,object> document;
            try{document=ManifestReader.Parse(File.ReadAllText(registration,Encoding.UTF8));}
            catch(InvalidDataException error){throw new InvalidDataException("SteamVR registration is malformed. Start SteamVR to refresh its registered installation.",error);}
            object id,version,paths;
            if(!document.TryGetValue("jsonid",out id)||!Object.Equals(id,"vrpathreg")||
                !document.TryGetValue("version",out version)||!Object.Equals(version,1L)||
                !document.TryGetValue("runtime",out paths)||!(paths is List<object>))
                throw new InvalidDataException("SteamVR registration has an unsupported format.");
            var runtimes=(List<object>)paths;
            if(runtimes.Count==0||!(runtimes[0] is string))
                throw new InvalidDataException("SteamVR registration has no primary runtime directory.");
            string root;
            try{root=Absolute((string)runtimes[0]);}
            catch(ArgumentException error){throw new InvalidDataException("The registered SteamVR directory is not an absolute local path.",error);}
            catch(NotSupportedException error){throw new InvalidDataException("The registered SteamVR directory is unsupported.",error);}
            // OpenVR selects the first registered runtime; skipping a stale
            // first entry could split OpenXR and OpenVR across installations.
            string manifest=Path.Combine(root,"steamxr_win64.json");
            ValidateRuntimeManifest(manifest);
            return manifest;
        }

        public static string GetOpenVrLibraryPath(string manifestPath)
        {
            string manifest=Absolute(manifestPath);
            ValidateRuntimeManifest(manifest);
            string library=Path.Combine(Path.GetDirectoryName(manifest),"bin","win64","openvr_api.dll");
            if(!File.Exists(library))throw new FileNotFoundException("The selected SteamVR installation is missing its Windows x64 controller-model library.",library);
            RejectLinks(library);
            return library;
        }

        public static string ValidateRuntimeManifest(string manifestPath)
        {
            string manifest=Absolute(manifestPath);
            if(!String.Equals(Path.GetFileName(manifest),"steamxr_win64.json",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The VIVE preview requires SteamVR's installed Windows x64 runtime manifest.");
            if(!File.Exists(manifest))throw new FileNotFoundException("The selected SteamVR installation is missing steamxr_win64.json. Start SteamVR and verify its installation. No other runtime is used.",manifest);
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
            if(!File.Exists(runtimeLibrary))throw new FileNotFoundException("Installed SteamVR Windows x64 runtime library is missing. No player was launched.",runtimeLibrary);
            RejectLinks(manifest);RejectLinks(runtimeLibrary);
            return runtimeLibrary;
        }

        public static VivePreviewLaunchPlan Create(string packageDirectory,string manifestPath)
        {
            string package=Absolute(packageDirectory),manifest=Absolute(manifestPath);
            string runtimeLibrary=ValidateRuntimeManifest(manifest);
            string playerDirectory=Path.GetFullPath(Path.Combine(package,"UnityPlayer"));
            string player=Path.Combine(playerDirectory,PlayerName);
            string dataDirectory=Path.Combine(playerDirectory,"AvatarStroopViveEyePlayer_Data");
            if(!File.Exists(player)||!Directory.Exists(dataDirectory))
                throw new FileNotFoundException("The VIVE Unity player or its data folder is missing. Keep the complete preview package together.");
            RejectLinks(player);RejectLinks(dataDirectory);
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
            // OpenVR's native client also resolves its runtime internally.
            // Pin that child-only lookup to the selected OpenXR installation.
            result.EnvironmentVariables[OpenVrRuntimeVariable]=Path.GetDirectoryName(RuntimeManifestPath);
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

        // Bounded JSON parser shared by the runtime manifest and OpenVR's path
        // registration. Duplicate keys and unsupported value types fail closed.
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
                    Space();string key=StringValue();Space();Take(':');object value=Value(depth+1);
                    if(result.ContainsKey(key))throw Invalid();result.Add(key,value);Space();
                    if(Peek('}')){offset++;return result;}Take(',');
                }
            }
            object Value(int depth)
            {
                if(depth>8)throw Invalid();Space();
                if(Peek('"'))return StringValue();
                if(Peek('{'))return ObjectValue(depth);
                if(Peek('['))
                {
                    offset++;Space();var values=new List<object>();
                    if(Peek(']')){offset++;return values;}
                    while(true)
                    {
                        values.Add(Value(depth+1));Space();
                        if(Peek(']')){offset++;return values;}Take(',');
                    }
                }
                if(Token("true"))return true;
                if(Token("false"))return false;
                if(Token("null"))return null;
                int start=offset;if(Peek('-'))offset++;
                if(Peek('0'))offset++;
                else
                {
                    if(offset>=text.Length||text[offset]<'1'||text[offset]>'9')throw Invalid();
                    while(offset<text.Length&&text[offset]>='0'&&text[offset]<='9')offset++;
                }
                long number;
                if(!Int64.TryParse(text.Substring(start,offset-start),System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,out number))throw Invalid();
                return number;
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
