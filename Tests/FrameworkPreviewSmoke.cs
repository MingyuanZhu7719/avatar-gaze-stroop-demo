using System;
using System.Runtime.InteropServices;

namespace AvatarStroop.Tests
{
    // A separate console entry point: execute directly on Windows, never via Mono.
    // The launcher and this probe must use the same installed Framework references.
    public static class FrameworkPreviewSmoke
    {
        public static int Main(string[] args)
        {
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT ||
                Type.GetType("Mono.Runtime")!=null || typeof(object).Assembly.GetName().Name!="mscorlib")
            {
                Console.Error.WriteLine("This compatibility probe requires the installed Windows .NET Framework CLR.");
                return 2;
            }
            Console.WriteLine("Framework CLR "+Environment.Version+"; runtime "+RuntimeEnvironment.GetRuntimeDirectory());
            var suite=new TestSuite();
            DesktopPreviewTests.Register(suite);
            OwnerCommandTests.Register(suite);
            Console.WriteLine("FRAMEWORK PREVIEW RESULT "+suite.Passed+" passed; "+suite.Failed+" failed");
            return suite.Failed==0?0:1;
        }
    }
}
