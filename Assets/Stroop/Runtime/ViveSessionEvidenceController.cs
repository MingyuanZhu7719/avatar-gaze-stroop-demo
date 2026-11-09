using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AvatarStroop.Runtime
{
    // Development-only summary of explicitly selected v2 journals. This never
    // writes, exports, starts capture, or exposes input paths in status text.
    public sealed class ViveSessionEvidenceController
    {
        readonly ViveSessionEvidenceAnalyzer analyzer;
        readonly Func<string, Stream> open;
        readonly object memoryGate=new object();
        readonly ViveJournalMemoryPreview memory=new ViveJournalMemoryPreview();
        readonly int memoryLifetimeMilliseconds;
        Timer memoryTimer;int memoryGeneration;
        bool busy;
        public string Message { get; private set; }
        public ViveSessionObservedSummary Summary { get; private set; }
        string memoryMessage;
        public string MemoryMessage {get{lock(memoryGate)return memoryMessage;}private set{lock(memoryGate)memoryMessage=value;}}
        public int MemorySourceCount {get{lock(memoryGate)return memory.Count;}}
        public string MemoryText {get{lock(memoryGate)return memory.Text;}}
        public string MemoryReport {get{lock(memoryGate)return memory.Report;}}
        public string MemoryManifestCsv {get{lock(memoryGate)return memory.ManifestCsv;}}
        public string MemoryFieldCsv {get{lock(memoryGate)return memory.FieldCsv;}}
        public string MemoryEventCsv {get{lock(memoryGate)return memory.EventCsv;}}

        public ViveSessionEvidenceController() : this(OpenLocalReadOnly) {}
        internal ViveSessionEvidenceController(Func<string, Stream> openReadOnly) : this(openReadOnly,60000) {}
        internal ViveSessionEvidenceController(Func<string, Stream> openReadOnly,int previewLifetimeMilliseconds)
        {
            if (openReadOnly == null) throw new ArgumentNullException("openReadOnly");
            if(previewLifetimeMilliseconds<1||previewLifetimeMilliseconds>60000)throw new ArgumentOutOfRangeException("previewLifetimeMilliseconds");
            open = openReadOnly;
            memoryLifetimeMilliseconds=previewLifetimeMilliseconds;
            analyzer = new ViveSessionEvidenceAnalyzer(64, 100000, 67108864);
            Summary = analyzer.Snapshot();
            Message = "Enter an existing v2 development journal path, then choose an action.";
            MemoryMessage = "No sensitive in-memory preview.";
        }
        public bool AddClean(string path, bool explicitAction)
        { return Add(path, explicitAction, false, false); }
        public bool AddVerifiedPrefix(string path, bool explicitAction, bool explicitRecovery)
        { return Add(path, explicitAction, true, explicitRecovery); }

        // A separate deliberate action; the only output is a short-lived memory
        // view. Neither this method nor the panel writes files or clipboard data.
        public bool AddMemory(string path,bool v3,bool explicitAction,bool recovery,bool explicitRecovery)
        {
            if(!explicitAction||(recovery&&!explicitRecovery))
            {MemoryMessage="Explicit read-only action and separate incomplete-prefix confirmation are required.";return false;}
            lock(memoryGate)
            {
                bool sourceOpened=false;
                try
                {
                    if(!IsAbsoluteDrivePath(path))throw new IOException();
                    bool added;using(var source=open(path)){sourceOpened=true;added=memory.Add(source,v3,true,recovery);}
                    if(!added){MemoryMessage="No source added ("+memory.Issue+"). Earlier sources remain until cleared or expired.";return false;}
                    try
                    {
                        int generation=++memoryGeneration;
                        if(memoryTimer!=null)memoryTimer.Dispose();
                        memoryTimer=new Timer(_=>ExpireMemory(generation),null,memoryLifetimeMilliseconds,Timeout.Infinite);
                    }
                    catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
                    {
                        memoryGeneration++;memory.Clear();if(memoryTimer!=null){memoryTimer.Dispose();memoryTimer=null;}
                        MemoryMessage="Preview cleared because its expiration timer could not be started.";return false;
                    }
                    MemoryMessage="Source added. Sensitive preview expires 60 seconds after the latest addition; no file or clipboard was written.";
                    return true;
                }
                catch(Exception e) when(!(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException))
                {
                    // A stream may throw during Dispose after Add committed. Clear
                    // all preview references rather than leave data without a timer.
                    if(sourceOpened)
                    {
                        memoryGeneration++;memory.Clear();if(memoryTimer!=null){memoryTimer.Dispose();memoryTimer=null;}
                        MemoryMessage="Source cleanup or analysis failed; all preview references were cleared.";
                    }
                    else MemoryMessage="Source could not be opened. Earlier sources remain until cleared or expired.";
                    return false;
                }
            }
        }
        void ExpireMemory(int generation)
        {
            lock(memoryGate)if(generation==memoryGeneration)
            {memory.Clear();MemoryMessage="Sensitive preview expired. No file was written.";if(memoryTimer!=null){memoryTimer.Dispose();memoryTimer=null;}}
        }
        public void ClearMemory()
        {
            lock(memoryGate){memoryGeneration++;if(memoryTimer!=null){memoryTimer.Dispose();memoryTimer=null;}
                memory.Clear();MemoryMessage="Preview references cleared; no secure memory wipe is claimed.";}
        }

        // Reading is synchronous; do not stall a participant or a recorder.
        public static bool CanReview(bool viewRunning, bool hasParticipant, bool taskEnded,
            bool hasRecording, bool recordingStopConfirmed, bool segmentOpen)
        {
            return !viewRunning && (!hasParticipant || taskEnded) &&
                (!hasRecording || (recordingStopConfirmed && !segmentOpen));
        }

        bool Add(string path, bool explicitAction, bool recovery, bool explicitRecovery)
        {
            if (busy) throw new InvalidOperationException("Analysis in progress.");
            if (!explicitAction || (recovery && !explicitRecovery))
            { Message = "No source added. Previously accepted sources remain in the summary. Explicit read-only action and separate prefix recovery confirmation are required."; return false; }
            busy = true;
            try
            {
                if (!IsAbsoluteDrivePath(path))
                { Message = "No source added. Previously accepted sources remain in the summary. Enter an absolute local drive path."; return false; }
                bool accepted;
                using (var source = open(path))
                    accepted = analyzer.AddJournal(source, true, recovery);
                Summary = analyzer.Snapshot();
                Message = accepted ? "Journal added to the selected-source summary; no export was saved."
                    : DescribeIssue(analyzer.LastIssue) + " Previously accepted sources remain in the summary.";
                return accepted;
            }
            catch (Exception e) when (!(e is OutOfMemoryException || e is StackOverflowException || e is AccessViolationException))
            { Message = "Journal could not be opened or analyzed. Previously accepted sources remain in the summary."; return false; }
            finally { busy = false; }
        }
        internal static bool IsAbsoluteDrivePath(string path)
        {
            return !String.IsNullOrEmpty(path) && path.Length >= 3 &&
                ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) &&
                path[1] == ':' && (path[2] == '\\' || path[2] == '/') && path.IndexOf(':', 2) < 0;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        internal static Stream OpenLocalReadOnly(string requested)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || !IsAbsoluteDrivePath(requested)) throw new IOException();
            string path = Path.GetFullPath(requested);
            if (!IsAbsoluteDrivePath(path)) throw new IOException();
            string root = Path.GetPathRoot(path);
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) throw new IOException();
            // Refuse all visible reparse points, not merely those leading to a
            // network path. A handle check below also detects path substitution.
            string part = root;
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException();
            foreach (string component in path.Substring(root.Length).Split(new[]{'\\','/'}, StringSplitOptions.RemoveEmptyEntries))
            {
                part = Path.Combine(part, component);
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException();
            }
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var result = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(file.SafeFileHandle, result, (uint)result.Capacity, 0);
                string final = result.ToString();
                if (length == 0 || length >= result.Capacity || !final.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                    !String.Equals(final.Substring(4), path, StringComparison.OrdinalIgnoreCase)) throw new IOException();
                return file;
            }
            catch { file.Dispose(); throw; }
        }
        static string DescribeIssue(string issue)
        {
            switch (issue)
            {
                case "incomplete_prefix_requires_separate_confirmation": return "Incomplete journal: use the separate verified-prefix action only if intended. No data was added.";
                case "different_declared_identity": return "Declared identity differs from earlier sources; no data was added.";
                case "duplicate_declared_stream": case "duplicate_task_sequence": return "Duplicate stream or task marker; no data was added.";
                case "analysis_limit": return "Analysis limit reached; no data was added.";
                default: return "Journal unavailable, invalid, incompatible, or over a limit; no data was added.";
            }
        }
    }
}
