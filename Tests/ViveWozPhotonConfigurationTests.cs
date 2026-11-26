using System;
using System.IO;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveWozPhotonConfigurationTests
    {
        public static void Register(TestSuite tests)
        {
            tests.Run("Photon private config requires valid fixed region and GUID without leaking values", () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "stroop-photon-config-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, ViveWozPhotonConfiguration.FileName);
                const string id = "12345678-1234-1234-1234-123456789abc";
                string appId, region, issue;
                try
                {
                    Check.True(!ViveWozPhotonConfiguration.TryLoad(directory, out appId, out region, out issue));
                    File.WriteAllText(path, "{\"region\":\"asia\",\"appId\":\"" + id + "\"}");
                    Check.True(ViveWozPhotonConfiguration.TryLoad(directory, out appId, out region, out issue));
                    Check.Equal(id, appId); Check.Equal("asia", region); Check.Equal("", issue);
                    foreach (var malformed in new[] {
                        "{\"appId\":\"" + id + "\",\"region\":\"asia\",\"region\":\"eu\"}",
                        "{\"appId\":\"" + id + "\",\"region\":\"asia\",}",
                        "{\"appId\":\"" + id + "\",\"region\":\"asia\" \"appVersion\":\"avatar-stroop-woz-v1\"}",
                        "{\"appId\":\"" + id + "\",\"region\":\"asia\",\"server\":\"example\"}",
                        "{\"appId\":\"" + id + "\",\"region\":\"ASIA\"}",
                        "{\"appId\":\"00000000-0000-0000-0000-000000000000\",\"region\":\"asia\"}",
                        "{\"appId\":\"" + id + "\",\"region\":\"asia\",\"appVersion\":\"other\"}",
                        "{\"appId\":\"" + id + "\",\"region\":\"asia\",\"appVersion\":\"avatar-stroop-woz-v1\"}",
                        "{\"appId\":\"" + id + "\",\"region\":\"asia\",\"appVersion\":\"avatar-woz-photon-1\"}"
                    })
                    {
                        File.WriteAllText(path, malformed);
                        Check.True(!ViveWozPhotonConfiguration.TryLoad(directory, out appId, out region, out issue));
                        Check.Equal<string>(null, appId); Check.Equal<string>(null, region);
                        Check.True(!issue.Contains(id));
                    }
                }
                finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); }
            });
        }
    }
}
