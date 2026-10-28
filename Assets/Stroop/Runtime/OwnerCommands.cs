using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum OwnerCommandKind { Practice, Lock, Begin, Export }
    public sealed class OwnerCommand
    {
        public string RequestId {get;private set;}
        public OwnerCommandKind Kind {get;private set;}
        public ControlSelection? Source {get;private set;}
        public OwnerCommand(string requestId,OwnerCommandKind kind,ControlSelection? source=null)
        {
            if(!OwnerCommandCodec.ValidId(requestId)||!Enum.IsDefined(typeof(OwnerCommandKind),kind)||
                (kind==OwnerCommandKind.Lock)!=source.HasValue || (source.HasValue&&!Enum.IsDefined(typeof(ControlSelection),source.Value)))
                throw new ArgumentException("Canonical request ID and whitelisted command/source required");
            RequestId=requestId;Kind=kind;Source=source;
        }
    }
    public sealed class OwnerAcknowledgement
    {
        public string RequestId {get;private set;}
        public bool Accepted {get;private set;}
        public string Reason {get;private set;}
        public string CommandHash {get;private set;}
        public OwnerAcknowledgement(string requestId,bool accepted,string reason,string commandHash)
        {
            if((requestId!=null&&!OwnerCommandCodec.ValidId(requestId)) || (commandHash!=null&&!OwnerCommandCodec.ValidHash(commandHash)) || (requestId==null)!= (commandHash==null))
                throw new ArgumentException("Invalid acknowledgement identity");
            if(String.IsNullOrEmpty(reason))throw new ArgumentException("Explicit acknowledgement reason required");
            RequestId=requestId;Accepted=accepted;Reason=reason;CommandHash=commandHash;
        }
    }
    public static class OwnerCommandCodec
    {
        public const int MaximumCommandBytes=256;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        internal static bool ValidId(string id)
        {Guid parsed;return id!=null&&id.Length==32&&Guid.TryParseExact(id,"N",out parsed)&&parsed.ToString("N")==id;}
        internal static bool ValidHash(string hash)
        {if(hash==null||hash.Length!=64)return false;foreach(char c in hash)if(!(c>='0'&&c<='9')&&!(c>='a'&&c<='f'))return false;return true;}
        public static string Encode(OwnerCommand command)
        {if(command==null)throw new ArgumentNullException("command");return "ASOWNER|1|"+command.RequestId+"|"+command.Kind+"|"+(command.Source.HasValue?command.Source.Value.ToString():"~");}
        public static bool TryParse(string line,out OwnerCommand command,out string reason)
        {
            command=null;reason="malformed_command";
            if(line==null||line.Length>MaximumCommandBytes)return false;
            foreach(char c in line)if(c<33||c>126)return false;
            var f=line.Split('|');if(f.Length!=5||f[0]!="ASOWNER"||f[1]!="1"||!ValidId(f[2]))return false;
            OwnerCommandKind kind;ControlSelection source;
            if(!Enum.TryParse(f[3],false,out kind)||!Enum.IsDefined(typeof(OwnerCommandKind),kind)||kind.ToString()!=f[3])return false;
            ControlSelection? selected=null;
            if(f[4]!="~")
            {if(!Enum.TryParse(f[4],false,out source)||!Enum.IsDefined(typeof(ControlSelection),source)||source.ToString()!=f[4])return false;selected=source;}
            if((kind==OwnerCommandKind.Lock)!=selected.HasValue){reason="source_only_and_required_for_lock";return false;}
            command=new OwnerCommand(f[2],kind,selected);reason=null;return true;
        }
        public static string Hash(OwnerCommand command)
        {using(var sha=SHA256.Create()){var bytes=sha.ComputeHash(Encoding.ASCII.GetBytes(Encode(command)));return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}}
        public static string EncodeAcknowledgement(OwnerAcknowledgement ack)
        {
            if(ack==null)throw new ArgumentNullException("ack");
            byte[] reason=Utf8.GetBytes(ack.Reason);if(reason.Length>2048)throw new ArgumentException("Acknowledgement reason too long");
            return "ASOWNERACK|1|"+(ack.RequestId??"~")+"|"+(ack.Accepted?"Accepted":"Rejected")+"|"+(ack.CommandHash??"~")+"|"+Convert.ToBase64String(reason);
        }
        public static bool TryParseAcknowledgement(string line,out OwnerAcknowledgement ack)
        {
            ack=null;if(line==null||line.Length>4096)return false;
            var f=line.Split('|');if(f.Length!=6||f[0]!="ASOWNERACK"||f[1]!="1"||(f[3]!="Accepted"&&f[3]!="Rejected"))return false;
            try
            {
                byte[] bytes=Convert.FromBase64String(f[5]);if(bytes.Length>2048||Convert.ToBase64String(bytes)!=f[5])return false;
                ack=new OwnerAcknowledgement(f[2]=="~"?null:f[2],f[3]=="Accepted",Utf8.GetString(bytes),f[4]=="~"?null:f[4]);return true;
            }
            catch(ArgumentException){return false;}catch(FormatException){return false;}
        }
        internal static string SafeReason(string reason)
        {try{return !String.IsNullOrEmpty(reason)&&Utf8.GetByteCount(reason)<=2048?reason:"handler_rejection_invalid_reason";}catch(ArgumentException){return "handler_rejection_invalid_reason";}}
    }

    public static class OwnerCommandFile
    {
        internal const int MaximumFileBytes=1024*1024;
        public static string AbsolutePath(string path)
        {
            if(String.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path))throw new ArgumentException("Owner must supply an absolute local command-file path");
            var full=Path.GetFullPath(path);if(!Directory.Exists(Path.GetDirectoryName(full)))throw new DirectoryNotFoundException("Owner command-file directory must already exist");return full;
        }
        public static void Append(string absolutePath,OwnerCommand command)
        {AppendLine(AbsolutePath(absolutePath),OwnerCommandCodec.Encode(command));}
        internal static void AppendLine(string path,string line)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(line+"\n");
            using(var stream=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read))
            {
                if(stream.Length+bytes.Length>MaximumFileBytes)throw new IOException("Development command journal limit reached; start a new explicit session queue");
                if(stream.Length>0){stream.Position=stream.Length-1;if(stream.ReadByte()!=10)throw new IOException("Incomplete file tail requires owner review; append refused");}
                stream.Position=stream.Length;stream.Write(bytes,0,bytes.Length);stream.Flush(true);
            }
        }
        internal static byte[] ReadBytes(string path)
        {
            if(!File.Exists(path))return new byte[0];
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
            {
                if(stream.Length>MaximumFileBytes)throw new IOException("Development command journal exceeds size limit");
                var bytes=new byte[(int)stream.Length];int at=0;
                while(at<bytes.Length){int read=stream.Read(bytes,at,bytes.Length-at);if(read==0)throw new IOException("Command file changed while reading");at+=read;}
                return bytes;
            }
        }
        internal static string[] CompleteJournal(string path)
        {
            byte[] bytes=ReadBytes(path);if(bytes.Length==0)return new string[0];
            if(bytes[bytes.Length-1]!=10)throw new IOException("Incomplete journal requires owner review: "+Path.GetFileName(path));
            string text=new UTF8Encoding(false,true).GetString(bytes);return text.Substring(0,text.Length-1).Split('\n');
        }
        public static OwnerAcknowledgement FindAcknowledgement(string absolutePath,OwnerCommand command)
        {
            var path=AbsolutePath(absolutePath)+".ack";byte[] bytes=ReadBytes(path);int last=Array.LastIndexOf(bytes,(byte)10);if(last<0)return null;
            string text=Encoding.ASCII.GetString(bytes,0,last);OwnerAcknowledgement found=null;string hash=OwnerCommandCodec.Hash(command);
            foreach(var line in text.Split('\n'))
            {OwnerAcknowledgement ack;if(OwnerCommandCodec.TryParseAcknowledgement(line,out ack)&&ack.RequestId==command.RequestId&&ack.CommandHash==hash)found=ack;}
            return found;
        }
    }

    // This whitelist invokes owner capabilities supplied by the local host; it creates no authority or source fallback.
    public static class OwnerCommandExecution
    {
        public static string Execute(OwnerCommand command,Action practice,Action<ControlSelection> lockBlock,Action begin,Action export)
        {
            if(command==null)throw new ArgumentNullException("command");
            switch(command.Kind)
            {
                case OwnerCommandKind.Practice:if(practice==null)return "practice_unavailable";practice();break;
                case OwnerCommandKind.Lock:
                    if(command.Source==ControlSelection.LiveWoz)return "live_woz_adapter_unavailable_in_development_simulation";
                    if(lockBlock==null)return "lock_unavailable";lockBlock(command.Source.Value);break;
                case OwnerCommandKind.Begin:if(begin==null)return "begin_unavailable";begin();break;
                case OwnerCommandKind.Export:if(export==null)return "export_unavailable";export();break;
                default:return "command_unavailable";
            }
            return null;
        }
    }

    // Trusted-local DEVELOPMENT IPC only. A lifetime lock and durable intent prevent automatic replay after an uncertain execution.
    public sealed class OwnerCommandDispatcher : IDisposable
    {
        readonly Func<OwnerCommand,string> handler;
        readonly FileStream ownerLock;
        readonly Dictionary<string,OwnerAcknowledgement> completed=new Dictionary<string,OwnerAcknowledgement>();
        readonly Dictionary<string,string> pending=new Dictionary<string,string>();
        byte[] consumed=new byte[0];int observedLength;
        bool disposed;
        public string CommandPath {get;private set;}
        public string AcknowledgementPath {get{return CommandPath+".ack";}}
        public string SessionId {get;private set;}
        public OwnerCommandDispatcher(string absolutePath,string sessionId,Func<OwnerCommand,string> handler)
        {
            if(handler==null||String.IsNullOrWhiteSpace(sessionId)||sessionId.Length>256)throw new ArgumentException("Explicit session and command handler required");
            CommandPath=OwnerCommandFile.AbsolutePath(absolutePath);SessionId=sessionId;this.handler=handler;
            ownerLock=new FileStream(CommandPath+".owner-lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            try
            {
                string marker="ASOWNERSESSION|1|"+Convert.ToBase64String(new UTF8Encoding(false,true).GetBytes(sessionId));
                var existing=OwnerCommandFile.CompleteJournal(CommandPath+".session");
                if(existing.Length==0)OwnerCommandFile.AppendLine(CommandPath+".session",marker);
                else if(existing.Length!=1||existing[0]!=marker)throw new InvalidOperationException("Owner command queue belongs to another session; use a fresh explicit path");
                foreach(var line in OwnerCommandFile.CompleteJournal(AcknowledgementPath))
                {
                    OwnerAcknowledgement ack;if(!OwnerCommandCodec.TryParseAcknowledgement(line,out ack))throw new IOException("Malformed acknowledgement journal requires owner review");
                    if(ack.RequestId!=null&&!completed.ContainsKey(ack.RequestId))completed.Add(ack.RequestId,ack);
                }
                foreach(var line in OwnerCommandFile.CompleteJournal(CommandPath+".pending"))
                {
                    var f=line.Split('|');if(f.Length!=4||f[0]!="ASOWNERPENDING"||f[1]!="1"||!OwnerCommandCodec.ValidId(f[2])||!OwnerCommandCodec.ValidHash(f[3]))throw new IOException("Malformed pending journal requires owner review");
                    if(pending.ContainsKey(f[2])&&pending[f[2]]!=f[3])throw new IOException("Conflicting pending journal requires owner review");pending[f[2]]=f[3];
                }
                foreach(var intent in pending)if(!completed.ContainsKey(intent.Key))
                {
                    var uncertain=new OwnerAcknowledgement(intent.Key,false,"previous_execution_outcome_uncertain_no_automatic_retry",intent.Value);
                    completed.Add(intent.Key,uncertain);AppendAck(uncertain);
                }
            }
            catch{ownerLock.Dispose();throw;}
        }
        void AppendAck(OwnerAcknowledgement ack) {OwnerCommandFile.AppendLine(AcknowledgementPath,OwnerCommandCodec.EncodeAcknowledgement(ack));}
        public int DispatchPending()
        {
            if(disposed)throw new ObjectDisposedException("OwnerCommandDispatcher");
            byte[] bytes=OwnerCommandFile.ReadBytes(CommandPath);
            if(bytes.Length<observedLength||bytes.Length<consumed.Length)throw new IOException("Owner command file was truncated; explicit review required");
            for(int i=0;i<consumed.Length;i++)if(consumed[i]!=bytes[i])throw new IOException("Owner command file was replaced or modified; explicit review required");
            observedLength=bytes.Length;int start=consumed.Length,count=0;
            for(int end=start;end<bytes.Length;end++)
            {
                if(bytes[end]!=10)continue;
                string line=end-start>OwnerCommandCodec.MaximumCommandBytes?null:Encoding.ASCII.GetString(bytes,start,end-start);
                bool ascii=true;for(int i=start;i<end;i++)if(bytes[i]<33||bytes[i]>126){ascii=false;break;}
                OwnerCommand command;string reason;
                if(!ascii||!OwnerCommandCodec.TryParse(line,out command,out reason))AppendAck(new OwnerAcknowledgement(null,false,"malformed_command",null));
                else Dispatch(command);
                start=end+1;consumed=new byte[start];Array.Copy(bytes,consumed,start);count++;
            }
            return count;
        }
        void Dispatch(OwnerCommand command)
        {
            string hash=OwnerCommandCodec.Hash(command);OwnerAcknowledgement prior;
            if(completed.TryGetValue(command.RequestId,out prior))
            {
                AppendAck(prior.CommandHash==hash?prior:new OwnerAcknowledgement(command.RequestId,false,"request_id_conflicts_with_prior_command",hash));return;
            }
            OwnerCommandFile.AppendLine(CommandPath+".pending","ASOWNERPENDING|1|"+command.RequestId+"|"+hash);pending[command.RequestId]=hash;
            OwnerAcknowledgement result;
            try
            {
                string rejection=handler(command);
                result=new OwnerAcknowledgement(command.RequestId,rejection==null,rejection==null?"accepted":OwnerCommandCodec.SafeReason(rejection),hash);
            }
            catch(Exception error)
            {result=new OwnerAcknowledgement(command.RequestId,false,OwnerCommandCodec.SafeReason("handler_error_"+error.GetType().Name+":"+error.Message+";outcome_may_be_uncertain_no_automatic_retry"),hash);}
            // Cache before append: if acknowledgement I/O fails, the same process can retry acknowledgement only.
            completed.Add(command.RequestId,result);AppendAck(result);
        }
        public void Dispose(){if(disposed)return;disposed=true;ownerLock.Dispose();}
    }
}
