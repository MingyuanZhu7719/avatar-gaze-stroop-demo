namespace AvatarStroop.Runtime
{
    // This project treats a zero 128-bit identifier as insufficient identity
    // evidence. This pure check does not establish authorization or path continuity.
    internal static class ViveRawFileId
    {
        internal static bool IsValid(byte[] id)
        {
            if(id==null||id.Length!=16)return false;
            bool nonzero=false;
            for(int i=0;i<id.Length;i++)nonzero|=id[i]!=0;
            return nonzero;
        }
        internal static bool Matches(ulong firstVolume,byte[] first,ulong secondVolume,byte[] second)
        {
            if(firstVolume!=secondVolume||!IsValid(first)||!IsValid(second))return false;
            for(int i=0;i<16;i++)if(first[i]!=second[i])return false;
            return true;
        }
    }
}
