using System;
using System.Collections.Generic;
using System.IO;

namespace AvatarStroop.Runtime
{
    internal sealed class LifecycleReadWait
    {
        public long Sequence {get;internal set;}
        public string Path {get;internal set;}
        public string Operation {get;internal set;}
        public int HResult {get;internal set;}
    }

    // Local simulator-host coordination only. Each call makes one full-file read
    // attempt; the caller retains its existing polling and timeout budget.
    internal sealed class SimulationLifecycleFiles
    {
        readonly Func<string,string> read;
        readonly List<LifecycleReadWait> waits=new List<LifecycleReadWait>();
        public long WaitCount {get;private set;}
        public IReadOnlyList<LifecycleReadWait> RecentWaits {get{return waits.AsReadOnly();}}
        public SimulationLifecycleFiles():this(File.ReadAllText){}
        internal SimulationLifecycleFiles(Func<string,string> read)
        {this.read=read??throw new ArgumentNullException("read");}
        public bool TryReadText(string path,out string text)
        {
            text=null;
            try {text=read(path);return true;}
            // Exact HRESULT_FROM_WIN32 values: do not defer access-denied,
            // malformed-data, missing-file or unrelated facility errors.
            catch(IOException error) when(error.HResult==unchecked((int)0x80070020)||error.HResult==unchecked((int)0x80070021))
            {
                WaitCount++;
                waits.Add(new LifecycleReadWait{Sequence=WaitCount,Path=path,Operation="read_all_text",HResult=error.HResult});
                if(waits.Count>64)waits.RemoveAt(0);
                return false;
            }
        }
        public bool TryReadExact(string path,string expected)
        {
            string text;if(!TryReadText(path,out text))return false;
            if(text!=expected)throw new InvalidDataException("Lifecycle record has the wrong identity or content: "+path);
            return true;
        }
    }
}
