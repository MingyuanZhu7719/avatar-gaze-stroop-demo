using System;
using System.IO;
using System.Text;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // One explicitly selected source only. This controller never saves,
    // publishes, copies to the clipboard, or starts a recording. The caller
    // must ensure the view and any recorder have stopped before showing it.
    public sealed class ViveRawSessionEvidenceController
    {
        readonly object gate=new object();
        readonly Func<string,Stream> open;
        readonly Func<Stream,ViveRawSessionReadResult> read;
        readonly int lifetimeMilliseconds;
        string preview, message="No raw journal preview.";
        Timer timer;
        long generation;
        bool busy,trailingFrameTruncated;

        public ViveRawSessionEvidenceController()
            : this(ViveSessionEvidenceController.OpenLocalReadOnly,
                source=>ViveRawSessionJournalReader.Read(source,true,true,4194304,5000),60000) {}

        // Test seam takes only synthetic in-memory streams. The production
        // constructor fixes both the opener and bounded, strict reader.
        internal ViveRawSessionEvidenceController(Func<string,Stream> openReadOnly,
            Func<Stream,ViveRawSessionReadResult> readOnly,int lifetime)
        {
            if(openReadOnly==null||readOnly==null)throw new ArgumentNullException();
            if(lifetime<1||lifetime>60000)throw new ArgumentOutOfRangeException("lifetime");
            open=openReadOnly;read=readOnly;lifetimeMilliseconds=lifetime;
        }
        public bool HasPreview {get{lock(gate)return preview!=null;}}
        public string PreviewText {get{lock(gate)return preview;}}
        public string Message {get{lock(gate)return message;}}
        public bool TrailingFrameTruncated {get{lock(gate)return trailingFrameTruncated;}}

        public bool TryLoad(string path,bool explicitAction,bool explicitPrefixRecovery)
        {
            if(!explicitAction||!explicitPrefixRecovery)
            {lock(gate)message="Select a source and explicitly choose to inspect its incomplete verified prefix.";return false;}
            long version;
            lock(gate)
            {
                if(busy){message="A read is already in progress.";return false;}
                busy=true;version=++generation;
                if(timer!=null){timer.Dispose();timer=null;}
                preview=null;trailingFrameTruncated=false;message="Reading the selected source.";
            }
            string candidate=null;bool truncated=false;
            try
            {
                if(!ViveSessionEvidenceController.IsAbsoluteDrivePath(path))throw new IOException();
                // No staged result is committed before successful disposal.
                using(var source=open(path))
                {
                    var result=read(source);
                    if(result!=null&&result.Status==ViveRawSessionReadStatus.VerifiedPrefix)
                    {candidate=result.DisplayText;truncated=result.TrailingFrameTruncated;}
                }
                if(candidate==null||candidate.Length>2097152||new UTF8Encoding(false,true).GetByteCount(candidate)>2097152)throw new IOException();
                lock(gate)
                {
                    if(generation!=version)return false;
                    try
                    {
                        timer=new Timer(_=>Expire(version),null,lifetimeMilliseconds,Timeout.Infinite);
                        preview=candidate;trailingFrameTruncated=truncated;
                        message="Selected verified prefix is available in memory for up to 60 seconds. No file was written.";
                        return true;
                    }
                    catch(Exception error) when(!Fatal(error))
                    {preview=null;if(timer!=null){timer.Dispose();timer=null;}message="Preview could not be safely retained and was cleared.";return false;}
                }
            }
            catch(Exception error) when(!Fatal(error))
            {
                lock(gate)if(generation==version){preview=null;message="Source unavailable, invalid, incompatible, or over a limit. No preview was retained.";}
                return false;
            }
            finally{lock(gate)busy=false;}
        }
        static bool Fatal(Exception error)
        {return error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException;}
        void Expire(long version)
        {
            lock(gate)if(generation==version)
            {preview=null;trailingFrameTruncated=false;generation++;if(timer!=null){timer.Dispose();timer=null;}message="Preview expired; references were released.";}
        }
        // Call on panel hide, focus loss, view start, disable and destroy.
        // Managed strings can have copies and cannot be securely wiped here.
        public void ClearSensitive()
        {
            lock(gate)
            {generation++;preview=null;trailingFrameTruncated=false;if(timer!=null){timer.Dispose();timer=null;}message="Preview references cleared; secure erasure is not claimed.";}
        }
    }
}
