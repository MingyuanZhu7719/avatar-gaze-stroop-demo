using System.Globalization;
using System.Threading;

namespace AvatarStroop.Core
{
    public static class AppLanguage
    {
        // Process-local managed resource lookup only. Preserve numeric/date
        // culture and raw records, paths, and native engine/OS diagnostics.
        public static void UseEnglishResources()
        {
            var english=CultureInfo.GetCultureInfo("en");
            CultureInfo.DefaultThreadCurrentUICulture=english;
            Thread.CurrentThread.CurrentUICulture=english;
        }
    }
}
