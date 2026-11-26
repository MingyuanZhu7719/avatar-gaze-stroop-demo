using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace AvatarStroop.Runtime
{
    /// <summary>Private per-package Photon settings. Values never enter diagnostics.</summary>
    public static class ViveWozPhotonConfiguration
    {
        public const string FileName = "photon-woz.local.json";

        public static bool TryLoad(out string appId, out string region, out string issue)
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                    return TryLoad(Path.GetDirectoryName(process.MainModule.FileName), out appId, out region, out issue);
            }
            catch
            {
                appId = region = null;
                issue = "Could not locate Photon configuration beside the program.";
                return false;
            }
        }

        public static bool TryLoad(string directory, out string appId, out string region, out string issue)
        {
            appId = region = null;
            issue = "Photon configuration is missing or invalid. Use the complete configured package.";
            try
            {
                var path = Path.Combine(directory, FileName);
                if (!File.Exists(path) || new FileInfo(path).Length > 4096) return false;
                var text = File.ReadAllText(path).Trim().TrimStart('\uFEFF');
                // Configuration values are deliberately restricted to plain ASCII.
                // Reject duplicate keys, escapes, arrays and extra JSON members.
                var pattern = @"\A\s*\{\s*(?:""(?<key>appId|region)""\s*:\s*""(?<value>[A-Za-z0-9\-]+)""\s*(?:,\s*|(?=\})))+\}\s*\z";
                var match = Regex.Match(text, pattern);
                if (!match.Success || Regex.IsMatch(text, @",\s*\}")) return false;
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < match.Groups["key"].Captures.Count; i++)
                {
                    var key = match.Groups["key"].Captures[i].Value;
                    if (values.ContainsKey(key)) return false;
                    values.Add(key, match.Groups["value"].Captures[i].Value);
                }
                string id, fixedRegion;
                Guid parsed;
                if (!values.TryGetValue("appId", out id) || !Guid.TryParseExact(id, "D", out parsed) || parsed == Guid.Empty ||
                    !values.TryGetValue("region", out fixedRegion) || !Regex.IsMatch(fixedRegion, @"\A[a-z]{2,8}\z")) return false;
                appId = id;
                region = fixedRegion;
                issue = string.Empty;
                return true;
            }
            catch { return false; }
        }
    }
}
