using System;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveSameFileReadbackTests
    {
        static readonly byte[] Digest=new byte[32];
        static ViveRawSessionReadResult Valid()
        {
            return new ViveRawSessionReadResult { Status=ViveRawSessionReadStatus.VerifiedPrefix,
                TrailingFrameTruncated=false,CaptureTerminalObserved=true,VerifiedBytes=500,
                VerifiedRecordCount=7,VerifiedTaskRecords=3,VerifiedObservationRecords=1,VerifiedStatusRecords=1,
                VerifiedPrefixChainDigestHex=new string('0',64) };
        }
        static bool Matches(ViveRawSessionReadResult value)
        {return ViveRawSessionJournalSink.ReadbackMatches(value,500,7,3,1,1,Digest);}
        public static void Register(TestSuite suite)
        {
            suite.Run("same-file readback requires terminal length counts and exact chain digest",()=>{
                var r=Valid();Check.True(Matches(r));r.VerifiedBytes--;Check.True(!Matches(r));r=Valid();r.VerifiedRecordCount--;Check.True(!Matches(r));
                r=Valid();r.VerifiedTaskRecords--;Check.True(!Matches(r));r=Valid();r.VerifiedObservationRecords--;Check.True(!Matches(r));
                r=Valid();r.VerifiedStatusRecords--;Check.True(!Matches(r));r=Valid();r.VerifiedPrefixChainDigestHex=new string('1',64);Check.True(!Matches(r));
            });
            suite.Run("same-file readback rejects truncated absent and invalid results",()=>{
                var r=Valid();r.TrailingFrameTruncated=true;Check.True(!Matches(r));r=Valid();r.CaptureTerminalObserved=false;Check.True(!Matches(r));
                r=Valid();r.Status=ViveRawSessionReadStatus.Invalid;Check.True(!Matches(r));Check.True(!Matches(null));
                Check.True(!ViveRawSessionJournalSink.ReadbackMatches(Valid(),500,7,3,1,1,new byte[31]));
            });
        }
    }
}
