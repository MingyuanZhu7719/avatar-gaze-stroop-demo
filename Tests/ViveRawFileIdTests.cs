using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveRawFileIdTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE raw file IDs require a complete nonzero 128-bit value",()=>{
                Check.True(!ViveRawFileId.IsValid(null));
                Check.True(!ViveRawFileId.IsValid(new byte[15]));
                Check.True(!ViveRawFileId.IsValid(new byte[17]));
                Check.True(!ViveRawFileId.IsValid(new byte[16]));
                for(int index=0;index<16;index++)
                {
                    var id=new byte[16];id[index]=1;
                    Check.True(ViveRawFileId.IsValid(id));
                }
            });
            suite.Run("VIVE raw file identity comparison rejects unavailable or different values",()=>{
                var first=new byte[16];first[0]=1;var same=(byte[])first.Clone();
                Check.True(ViveRawFileId.Matches(7,first,7,same));
                Check.True(!ViveRawFileId.Matches(7,new byte[16],7,new byte[16]));
                Check.True(!ViveRawFileId.Matches(7,first,7,new byte[16]));
                Check.True(!ViveRawFileId.Matches(7,first,8,same));
                same[15]=1;Check.True(!ViveRawFileId.Matches(7,first,7,same));
                Check.True(!ViveRawFileId.Matches(7,first,7,null));
                Check.True(!ViveRawFileId.Matches(7,first,7,new byte[15]));
            });
            suite.Run("VIVE directory identity snapshots fail closed for unavailable IDs",()=>{
                var zero=new ViveRawDirectoryLease.DirectoryIdentity(7,new byte[16]);
                Check.True(!zero.Matches(7,new byte[16]));
                var bytes=new byte[16];bytes[15]=9;
                var valid=new ViveRawDirectoryLease.DirectoryIdentity(7,bytes);
                bytes[15]=0;
                var same=new byte[16];same[15]=9;
                Check.True(valid.Matches(7,same));
                Check.True(!valid.Matches(7,new byte[16]));
                Check.True(!valid.Matches(8,same));
            });
        }
    }
}
