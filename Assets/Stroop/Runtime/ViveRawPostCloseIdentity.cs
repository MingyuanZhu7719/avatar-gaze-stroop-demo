using System;

namespace AvatarStroop.Runtime
{
    // This is only a comparison of two already observed handle identities.
    // It does not open a path, confer authority, prove durability, or ensure
    // that a later lookup still resolves to the same object. A caller must
    // perform all native calls under the original revocable I/O claim.
    internal static class ViveRawPostCloseIdentity
    {
        internal static bool Matches(ulong expectedVolume, string expectedFileIdHex,
            uint observedFileType, uint observedAttributes, ulong observedVolume, byte[] observedFileId)
        {
            if(observedFileType!=1 || (observedAttributes&0x410)!=0 || expectedFileIdHex==null ||
                expectedFileIdHex.Length!=32 || observedFileId==null || observedFileId.Length!=16 ||
                expectedVolume!=observedVolume)return false;
            bool expectedNonzero=false,observedNonzero=false;
            for(int i=0;i<16;i++)
            {
                int high=Hex(expectedFileIdHex[i*2]),low=Hex(expectedFileIdHex[i*2+1]);
                if(high<0||low<0)return false;
                byte expected=(byte)((high<<4)|low);
                expectedNonzero|=expected!=0;observedNonzero|=observedFileId[i]!=0;
                if(expected!=observedFileId[i])return false;
            }
            return expectedNonzero&&observedNonzero;
        }
        static int Hex(char value)
        {
            if(value>='0'&&value<='9')return value-'0';
            if(value>='a'&&value<='f')return value-'a'+10;
            if(value>='A'&&value<='F')return value-'A'+10;
            return -1;
        }
    }
}
