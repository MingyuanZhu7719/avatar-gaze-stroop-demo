using System;
using System.Globalization;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class AppLanguageTests
    {
        public static void Register(TestSuite tests)
        {
            tests.Run("English application resources preserve current and default numeric culture",()=>{
                var previousUi=Thread.CurrentThread.CurrentUICulture;
                var previousDefaultUi=CultureInfo.DefaultThreadCurrentUICulture;
                var previousCulture=Thread.CurrentThread.CurrentCulture;
                var previousDefaultCulture=CultureInfo.DefaultThreadCurrentCulture;
                try
                {
                    Thread.CurrentThread.CurrentUICulture=CultureInfo.GetCultureInfo("zh-CN");
                    CultureInfo.DefaultThreadCurrentUICulture=CultureInfo.GetCultureInfo("zh-CN");
                    var numeric=CultureInfo.GetCultureInfo("fr-FR");
                    var defaultNumeric=CultureInfo.GetCultureInfo("tr-TR");
                    Thread.CurrentThread.CurrentCulture=numeric;
                    CultureInfo.DefaultThreadCurrentCulture=defaultNumeric;
                    string number=(1234.5m).ToString();
                    string date=new DateTime(2026,11,2).ToString("d");
                    for(int repeat=0;repeat<2;repeat++)
                    {
                        AppLanguage.UseEnglishResources();
                        Check.Equal("en",Thread.CurrentThread.CurrentUICulture.Name);
                        Check.Equal("en",CultureInfo.DefaultThreadCurrentUICulture.Name);
                        Check.True(Object.ReferenceEquals(numeric,Thread.CurrentThread.CurrentCulture));
                        Check.True(Object.ReferenceEquals(defaultNumeric,CultureInfo.DefaultThreadCurrentCulture));
                        Check.Equal(number,(1234.5m).ToString());
                        Check.Equal(date,new DateTime(2026,11,2).ToString("d"));
                    }
                    string inheritedUi=null;Exception failure=null;
                    var worker=new Thread(()=>{try{inheritedUi=Thread.CurrentThread.CurrentUICulture.Name;}catch(Exception error){failure=error;}});
                    worker.IsBackground=true;worker.Start();
                    Check.True(worker.Join(5000),"Managed resource culture probe did not finish");
                    Check.Equal<Exception>(null,failure);Check.Equal("en",inheritedUi);
                }
                finally
                {
                    CultureInfo.DefaultThreadCurrentUICulture=previousDefaultUi;
                    Thread.CurrentThread.CurrentUICulture=previousUi;
                    CultureInfo.DefaultThreadCurrentCulture=previousDefaultCulture;
                    Thread.CurrentThread.CurrentCulture=previousCulture;
                }
            });
        }
    }
}
