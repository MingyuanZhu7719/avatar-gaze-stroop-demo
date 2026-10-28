using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Apps
{
    // Separate desktop console. This does not create a participant-facing configuration menu.
    public static class OwnerConsole
    {
        public static int Main(string[] args)
        {
            try
            {
                if(args.Length<2)return Usage();
                OwnerCommandKind kind;ControlSelection? source=null;
                switch(args[1].ToLowerInvariant())
                {case "practice":kind=OwnerCommandKind.Practice;break;case "lock":kind=OwnerCommandKind.Lock;break;case "begin":kind=OwnerCommandKind.Begin;break;case "export":kind=OwnerCommandKind.Export;break;default:return Usage();}
                int at=2;
                if(kind==OwnerCommandKind.Lock)
                {
                    if(args.Length<=at)return Usage();
                    switch(args[at++].ToLowerInvariant())
                    {case "automatic":source=ControlSelection.Automatic;break;case "simulator":source=ControlSelection.SimulatorRehearsal;break;case "live":source=ControlSelection.LiveWoz;break;default:return Usage();}
                }
                string id=Guid.NewGuid().ToString("N");
                if(args.Length>at){if(args.Length!=at+2||args[at]!="--id")return Usage();id=args[at+1];at+=2;}
                if(args.Length!=at)return Usage();
                var command=new OwnerCommand(id,kind,source);string path=OwnerCommandFile.AbsolutePath(args[0]);
                Console.WriteLine("DEVELOPMENT ONLY — simulation owner command; no apparatus authorization.");
                Console.WriteLine("request_id="+command.RequestId);
                var prior=OwnerCommandFile.FindAcknowledgement(path,command);
                if(prior!=null)return Report(prior);
                OwnerCommandFile.Append(path,command);
                // This short wait is console convenience, not an experimental timing threshold.
                var timer=Stopwatch.StartNew();
                while(timer.Elapsed.TotalSeconds<2)
                {var ack=OwnerCommandFile.FindAcknowledgement(path,command);if(ack!=null)return Report(ack);Thread.Sleep(50);}
                Console.WriteLine("QUEUED; execution is unconfirmed. Read "+path+".ack or retry this exact command with --id "+command.RequestId+".");return 0;
            }
            catch(Exception error) when(error is ArgumentException||error is IOException||error is UnauthorizedAccessException)
            {Console.Error.WriteLine("Owner command was not confirmed: "+Display(error.Message));return 3;}
        }
        static int Report(OwnerAcknowledgement ack)
        {Console.WriteLine((ack.Accepted?"ACCEPTED":"REJECTED")+" request_id="+ack.RequestId+" reason="+Display(ack.Reason));return ack.Accepted?0:4;}
        static string Display(string value)
        {var chars=value.ToCharArray();for(int i=0;i<chars.Length;i++)if(Char.IsControl(chars[i]))chars[i]=' ';return new String(chars);}
        static int Usage()
        {
            Console.Error.WriteLine("Usage: OwnerConsole <absolute-command-file> practice|begin|export [--id canonical-guid-N]");
            Console.Error.WriteLine("       OwnerConsole <absolute-command-file> lock automatic|simulator|live [--id canonical-guid-N]");return 2;
        }
    }
}
