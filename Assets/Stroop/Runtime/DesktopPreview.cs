using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Development process wiring only: no experiment factors or owner actions.
    public sealed class DesktopPreviewOptions
    {
        public string SessionId {get;private set;}
        public int ParticipantPort {get;private set;}
        public int OperatorPort {get;private set;}
        public string OwnerCommandsPath {get;private set;}
        public string JournalPath {get;private set;}
        public string StatusPath {get;private set;}
        public string StartupPath {get;private set;}

        public static DesktopPreviewOptions Parse(string[] args,string defaultSession,int defaultParticipantPort,int defaultOperatorPort)
        {
            if(args==null)throw new ArgumentNullException("args");
            var result=new DesktopPreviewOptions{SessionId=defaultSession,ParticipantPort=defaultParticipantPort,OperatorPort=defaultOperatorPort};
            var seen=new HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<args.Length;i++)
            {
                string option=args[i];
                if(option!="--stroop-session"&&option!="--stroop-participant-port"&&option!="--stroop-operator-port"&&
                    option!="--stroop-owner-commands"&&option!="--stroop-journal"&&option!="--stroop-status"&&option!="--stroop-startup")continue;
                if(!seen.Add(option))throw new ArgumentException("Duplicate preview option: "+option);
                if(i+1>=args.Length||String.IsNullOrWhiteSpace(args[i+1])||args[i+1].StartsWith("-",StringComparison.Ordinal))
                    throw new ArgumentException("A nonempty value is required for "+option);
                string value=args[++i];
                switch(option)
                {
                    case "--stroop-session":result.SessionId=value;break;
                    case "--stroop-participant-port":result.ParticipantPort=Port(value);break;
                    case "--stroop-operator-port":result.OperatorPort=Port(value);break;
                    case "--stroop-owner-commands":result.OwnerCommandsPath=DesktopPreviewFiles.AbsolutePath(value);break;
                    case "--stroop-journal":result.JournalPath=DesktopPreviewFiles.AbsolutePath(value);break;
                    case "--stroop-status":result.StatusPath=DesktopPreviewFiles.AbsolutePath(value);break;
                    case "--stroop-startup":result.StartupPath=DesktopPreviewFiles.AbsolutePath(value);break;
                }
            }
            DesktopPreviewFiles.Session(result.SessionId);
            // Session-derived trial identities must also fit the existing wire limit.
            WireCodec.RequireIdentity(result.SessionId+"/b3/t23");
            if(result.ParticipantPort<1||result.ParticipantPort>65535||result.OperatorPort<1||result.OperatorPort>65535||result.ParticipantPort==result.OperatorPort)
                throw new ArgumentException("Distinct participant and operator ports in 1..65535 are required");
            var paths=new HashSet<string>(Path.DirectorySeparatorChar=='\\'?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
            foreach(string path in new[]{result.OwnerCommandsPath,result.JournalPath,result.StatusPath,result.StartupPath})
                if(path!=null&&!paths.Add(path))throw new ArgumentException("Preview command, journal, status and startup files must use distinct paths");
            if(result.OwnerCommandsPath!=null)foreach(string suffix in new[]{".ack",".pending",".session",".owner-lock"})
                if(!paths.Add(result.OwnerCommandsPath+suffix))throw new ArgumentException("Preview paths overlap an owner coordination file");
            if(result.StartupPath!=null)foreach(string suffix in new[]{".Participant.ready",".Operator.ready"})
                if(!paths.Add(result.StartupPath+suffix))throw new ArgumentException("Preview paths overlap a startup coordination file");
            return result;
        }
        static int Port(string value)
        {
            int port;
            if(!Int32.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out port)||port<1||port>65535)
                throw new ArgumentException("Preview ports must be integers in 1..65535");
            return port;
        }
    }

    internal static class DesktopPreviewFiles
    {
        internal static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        internal static void Session(string session){WireCodec.RequireIdentity(session);}
        internal static string AbsolutePath(string path)
        {
            if(String.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path))throw new ArgumentException("Preview files require an absolute path");
            string root=Path.GetPathRoot(path);
            if(Path.DirectorySeparatorChar=='\\'&&(root.Length<3||(!root.StartsWith("\\\\",StringComparison.Ordinal)&&root[1]!=':')))
                throw new ArgumentException("Preview files require a fully qualified absolute path");
            string full=Path.GetFullPath(path);
            if(!Directory.Exists(Path.GetDirectoryName(full)))throw new DirectoryNotFoundException("Preview file parent directory must already exist");
            if(Directory.Exists(full))throw new ArgumentException("Preview file path names a directory");
            return full;
        }
        internal static byte[] Read(string path,int limit)
        {
            try
            {
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                {
                    if(stream.Length>limit)throw new InvalidDataException("Preview file exceeds its size limit");
                    byte[] bytes=new byte[(int)stream.Length];int at=0;
                    while(at<bytes.Length){int count=stream.Read(bytes,at,bytes.Length-at);if(count==0)throw new InvalidDataException("Incomplete preview file");at+=count;}
                    if(stream.ReadByte()!=-1)throw new InvalidDataException("Preview file changed while reading");
                    return bytes;
                }
            }
            catch(FileNotFoundException){return null;}
        }
        internal static void CreateNew(string path,string text)
        {
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                byte[] bytes=Utf8.GetBytes(text);
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                // Publish a complete record, while refusing to overwrite an old launch.
                File.Move(temporary,path);
            }
            finally
            {try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
    }

    // A bounded observation only. GUI freshness and owner-command acknowledgement
    // remain separate; this record carries no score or command authority.
    public sealed class DesktopPreviewStatus
    {
        public const int MaximumFileBytes=16*1024;
        public const int MaximumMessageBytes=2048;
        public string SessionId {get;private set;}
        public long Sequence {get;private set;}
        public Phase Phase {get;private set;}
        public int BlockIndex {get;private set;}
        public Condition? Condition {get;private set;}
        public ControlSelection? LockedSource {get;private set;}
        public bool Ready {get;private set;}
        public string Message {get;private set;}
        public DesktopPreviewStatus(string session,long sequence,Phase phase,int blockIndex,Condition? condition,ControlSelection? lockedSource,bool ready,string message)
        {
            DesktopPreviewFiles.Session(session);
            if(sequence<0||!Enum.IsDefined(typeof(Phase),phase)||blockIndex< -1||blockIndex>3||
                (condition.HasValue&&!Enum.IsDefined(typeof(Condition),condition.Value))||
                (lockedSource.HasValue&&!Enum.IsDefined(typeof(ControlSelection),lockedSource.Value)))
                throw new ArgumentException("Invalid preview status sequence, phase, block or source");
            if((blockIndex>=0)!=condition.HasValue||(lockedSource.HasValue&&blockIndex<0)||(ready&&!lockedSource.HasValue))
                throw new ArgumentException("Preview readiness requires an explicitly locked block source");
            message=message??String.Empty;
            if(DesktopPreviewFiles.Utf8.GetByteCount(message)>MaximumMessageBytes)throw new ArgumentException("Preview status message is too long");
            SessionId=session;Sequence=sequence;Phase=phase;BlockIndex=blockIndex;Condition=condition;LockedSource=lockedSource;Ready=ready;Message=message;
        }
        public string Encode()
        {
            return String.Join("|",new[]{"ASDESKTOP","1",Base64(SessionId),Sequence.ToString(CultureInfo.InvariantCulture),Phase.ToString(),
                BlockIndex.ToString(CultureInfo.InvariantCulture),Condition.HasValue?Condition.Value.ToString():"~",
                LockedSource.HasValue?LockedSource.Value.ToString():"~",Ready?"1":"0",Base64(Message)});
        }
        public static DesktopPreviewStatus Parse(string text)
        {
            if(text==null||DesktopPreviewFiles.Utf8.GetByteCount(text)>MaximumFileBytes)throw new FormatException("Invalid preview status size");
            string[] fields=text.Split(new[]{'|'});
            if(fields.Length!=10||fields[0]!="ASDESKTOP"||fields[1]!="1"||(fields[8]!="0"&&fields[8]!="1"))throw new FormatException("Invalid preview status record");
            try
            {
                return new DesktopPreviewStatus(Unbase64(fields[2]),Number(fields[3]),Token<Phase>(fields[4]),checked((int)Number(fields[5])),
                    fields[6]=="~"?(Condition?)null:Token<Condition>(fields[6]),fields[7]=="~"?(ControlSelection?)null:Token<ControlSelection>(fields[7]),
                    fields[8]=="1",Unbase64(fields[9]));
            }
            catch(ArgumentException error){throw new FormatException("Invalid preview status values",error);}
            catch(OverflowException error){throw new FormatException("Invalid preview status integer",error);}
        }
        static string Base64(string value){return Convert.ToBase64String(DesktopPreviewFiles.Utf8.GetBytes(value));}
        static string Unbase64(string value)
        {byte[] bytes=Convert.FromBase64String(value);if(Convert.ToBase64String(bytes)!=value)throw new FormatException("Noncanonical preview text");return DesktopPreviewFiles.Utf8.GetString(bytes);}
        static long Number(string value)
        {long number;if(!Int64.TryParse(value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out number)||number.ToString(CultureInfo.InvariantCulture)!=value)throw new FormatException("Invalid preview integer");return number;}
        static T Token<T>(string value) where T:struct
        {T token;if(!Enum.TryParse(value,false,out token)||!Enum.IsDefined(typeof(T),token)||token.ToString()!=value)throw new FormatException("Invalid preview enum");return token;}
        public static void Write(string path,DesktopPreviewStatus status)
        {
            if(status==null)throw new ArgumentNullException("status");
            path=DesktopPreviewFiles.AbsolutePath(path);
            byte[] bytes=DesktopPreviewFiles.Utf8.GetBytes(status.Encode());
            if(bytes.Length>MaximumFileBytes)throw new InvalidDataException("Preview status exceeds its size limit");
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
            }
            finally
            {
                try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
        // Only a missing file returns null. Malformed/oversized/unreadable files fail closed.
        public static DesktopPreviewStatus Read(string path)
        {
            byte[] bytes=DesktopPreviewFiles.Read(DesktopPreviewFiles.AbsolutePath(path),MaximumFileBytes);
            return bytes==null?null:Parse(DesktopPreviewFiles.Utf8.GetString(bytes));
        }
    }

    // A fresh per-launch local barrier prevents early UDP sends to an unbound peer.
    public static class DesktopPreviewStartup
    {
        public static void WriteReady(string path,string session,Actor role)
        {
            DesktopPreviewFiles.Session(session);
            if(role!=Actor.Participant&&role!=Actor.Operator)throw new ArgumentException("A preview participant/operator role is required");
            path=DesktopPreviewFiles.AbsolutePath(path)+"."+role+".ready";
            DesktopPreviewFiles.CreateNew(path,"ASPREVIEWREADY|1|"+session+"|"+role);
        }
        public static void WriteRelease(string path,string session)
        {
            DesktopPreviewFiles.Session(session);path=DesktopPreviewFiles.AbsolutePath(path);
            if(!Matches(path+".Participant.ready","ASPREVIEWREADY|1|"+session+"|Participant")||
                !Matches(path+".Operator.ready","ASPREVIEWREADY|1|"+session+"|Operator"))
                throw new InvalidOperationException("Both preview windows must be ready before release");
            DesktopPreviewFiles.CreateNew(path,"ASPREVIEWSTART|1|"+session);
        }
        public static bool IsReleased(string path,string session)
        {
            DesktopPreviewFiles.Session(session);path=DesktopPreviewFiles.AbsolutePath(path);
            bool participant=Matches(path+".Participant.ready","ASPREVIEWREADY|1|"+session+"|Participant");
            bool operatorReady=Matches(path+".Operator.ready","ASPREVIEWREADY|1|"+session+"|Operator");
            bool released=Matches(path,"ASPREVIEWSTART|1|"+session);
            return participant&&operatorReady&&released;
        }
        static bool Matches(string path,string expected)
        {
            byte[] bytes=DesktopPreviewFiles.Read(path,512);if(bytes==null)return false;
            if(DesktopPreviewFiles.Utf8.GetString(bytes)!=expected)throw new InvalidDataException("Preview startup file has the wrong session, role or content: "+Path.GetFileName(path));
            return true;
        }
    }
}
