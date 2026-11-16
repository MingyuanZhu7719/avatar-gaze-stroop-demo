using System;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveCsvPathPolicyTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("CSV path budget uses the actual native sibling filename",()=>{
                var id=new Guid("12345678-1234-1234-1234-123456789abc");
                string leaf=ViveCsvPathPolicy.StageFileName(id);
                Check.Equal("vstage-12345678123412341234123456789abc.partial.csv",leaf);
                Check.Equal(51,leaf.Length);
                string exact="C:\\"+new string('a',204);
                Check.Equal(259,(exact+"\\"+leaf).Length);
                Check.True(ViveCsvPathPolicy.FitsSessionDirectory(exact));
                Check.True(ViveCsvPathPolicy.FitsSessionDirectory(exact+"\\"));
                Check.True(!ViveCsvPathPolicy.FitsSessionDirectory(exact+"a"));
            });
            suite.Run("CSV open rejects an overlong existing path without changing generic raw paths",()=>{
                Check.True(ViveCsvPathPolicy.FitsExistingFile(new string('x',259)));
                Check.True(!ViveCsvPathPolicy.FitsExistingFile(new string('x',260)));
                Check.True(!ViveCsvPathPolicy.FitsExistingFile(null));
                string directory="C:\\"+new string('a',205);
                Check.Equal(directory,ViveRawDirectoryLease.NormalizeCandidate(directory));
                Check.True(!ViveCsvPathPolicy.FitsSessionDirectory(directory));
            });
            suite.Run("compact session proposals retain the full random identity",()=>{
                var proposal=ViveLocalSessionDirectory.Propose(@"C:\study");
                string leaf=proposal.DirectoryPath.Substring(proposal.ParentPath.Length+1);
                Check.Equal(51,leaf.Length);
                Check.True(leaf.StartsWith("s-",StringComparison.Ordinal));
                Guid parsed;Check.True(Guid.TryParseExact(leaf.Substring(19),"N",out parsed));
                Check.True(!proposal.Attempted&&!proposal.Created);
            });
        }
    }
}
