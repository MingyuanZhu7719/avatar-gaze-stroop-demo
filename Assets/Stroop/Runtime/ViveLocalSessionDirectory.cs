using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // One proposed, direct child under the explicitly chosen local parent.
    // Proposing does not even open the parent. Creating this empty container
    // neither starts acquisition nor grants the writer any file authority.
    internal sealed class ViveLocalSessionDirectory
    {
        const int ErrorFileExists=80,ErrorAlreadyExists=183;
        readonly string parentPath,directoryPath;
        int attempted,created;
        internal string ParentPath {get{return parentPath;}}
        internal string DirectoryPath {get{return directoryPath;}}
        internal bool Attempted {get{return Volatile.Read(ref attempted)!=0;}}
        internal bool Created {get{return Volatile.Read(ref created)!=0;}}

        ViveLocalSessionDirectory(string parent,string directory)
        {parentPath=parent;directoryPath=directory;}

        internal static ViveLocalSessionDirectory Propose(string parent)
        {
            string normalized=ViveRawDirectoryLease.NormalizeCandidate(parent);
            string name="session-"+DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N");
            // Canonical fully qualified path and bounded lexical length,
            // including the generated leaf, before a GUI can arm it.
            string child=ViveRawDirectoryLease.NormalizeCandidate(normalized+"\\"+name);
            return new ViveLocalSessionDirectory(normalized,child);
        }

        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool CreateDirectoryW(string path,IntPtr securityAttributes);

        internal bool TryCreate(out string reason)
        {
            reason=null;
            if(Interlocked.CompareExchange(ref attempted,1,0)!=0)
            {reason="This proposed session folder has already been attempted. Choose a new proposal to try again.";return false;}
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)
            {reason="Creating a verified local session folder requires supported Windows storage.";return false;}
            bool startedNativeCreate=false,returnedNativeCreate=false;
            try
            {
                // Keep the verified parent and all stable, non-redirected
                // ancestors pinned during the single native call and postcheck.
                using(ViveRawDirectoryLease.OpenExistingCandidate(parentPath))
                {
                    startedNativeCreate=true;
                    bool made=CreateDirectoryW(directoryPath,IntPtr.Zero);
                    int error=made?0:Marshal.GetLastWin32Error();
                    returnedNativeCreate=true;
                    if(!made)
                    {
                        reason=error==ErrorAlreadyExists||error==ErrorFileExists?
                            "A file or folder already uses the proposed session name. Nothing in it was changed. Choose a new proposal.":
                            "Windows did not create the proposed session folder (error "+error.ToString(CultureInfo.InvariantCulture)+"). Choose a new proposal before trying again.";
                        return false;
                    }
                    // CreateDirectoryW does not return an open handle. Verify
                    // the child without following reparse points; never adopt
                    // an existing destination after a failed native create.
                    // This does not claim protection from hostile child swaps
                    // in the gap before this/opening the writer later.
                    using(ViveRawDirectoryLease.OpenExistingCandidate(directoryPath))
                    using(var entries=Directory.EnumerateFileSystemEntries(directoryPath).GetEnumerator())
                    {
                        if(entries.MoveNext())
                        {reason="The new session folder was not empty when verified. Nothing was removed. Choose a new proposal.";return false;}
                    }
                }
                Volatile.Write(ref created,1);
                return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {
                reason=!startedNativeCreate?
                    "The selected parent could not be verified as an existing local fixed-drive directory without redirects. No session folder was requested.":
                    returnedNativeCreate?
                        "The new session folder could not be verified. It may remain on disk; nothing was removed. Choose a new proposal.":
                        "Session-folder creation could not be confirmed. It may remain on disk; nothing was removed. Choose a new proposal.";
                return false;
            }
        }
    }
}
