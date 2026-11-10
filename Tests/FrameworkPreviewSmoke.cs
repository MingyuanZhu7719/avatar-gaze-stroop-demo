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
            // Exercise authentication on CLR4 as well as the portable runtime.
            suite.Run("WoZ authenticated LAN byte-array suite", () => Check.Equal(0, WozAuthenticatedLanTests.RunAll()));
            suite.Run("WoZ authenticated control byte-array suite", () => Check.Equal(0, WozAuthenticatedControlTests.RunAll()));
            AppLanguageTests.Register(suite);
            DesktopPreviewTests.Register(suite);
            OwnerCommandTests.Register(suite);
            PreviewRecoveryTests.Register(suite);
            PreviewRestartWorkflowTests.Register(suite);
            PreviewExportReceiptTests.Register(suite);
            Console.WriteLine("FRAMEWORK PREVIEW RESULT "+suite.Passed+" passed; "+suite.Failed+" failed");
            return suite.Failed==0?0:1;
        }
    }
}
