using System;
using System.IO;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveTlsSimulationCandidateTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE synthetic TLS pin requires exact SHA-256 identity",()=>{
                var fixture=new byte[]{1,2,3,4};byte[] pin;using(var sha=SHA256.Create())pin=sha.ComputeHash(fixture);
                Check.True(ViveTlsSimulationCandidate.MatchesPin(fixture,pin));
                for(int i=0;i<pin.Length;i++){var wrong=(byte[])pin.Clone();wrong[i]^=1;Check.True(!ViveTlsSimulationCandidate.MatchesPin(fixture,wrong));}
                Check.True(!ViveTlsSimulationCandidate.MatchesPin(null,pin));Check.True(!ViveTlsSimulationCandidate.MatchesPin(fixture,null));
                Check.True(!ViveTlsSimulationCandidate.MatchesPin(fixture,new byte[31]));
            });
            suite.Run("VIVE synthetic TLS policy rejects missing guarantees and older protocol",()=>{
                Check.True(!ViveTlsSimulationCandidate.MeetsPolicy(false,true,true,true,true,SslProtocols.Tls13,true,true,true));
                Check.True(!ViveTlsSimulationCandidate.MeetsPolicy(true,false,true,true,true,SslProtocols.Tls13,true,true,true));
                Check.True(!ViveTlsSimulationCandidate.MeetsPolicy(true,true,false,true,true,SslProtocols.Tls13,true,true,true));
                Check.True(!ViveTlsSimulationCandidate.MeetsPolicy(true,true,true,false,true,SslProtocols.Tls13,true,true,true));
                Check.True(!ViveTlsSimulationCandidate.MeetsPolicy(true,true,true,true,false,SslProtocols.Tls13,true,true,true));
                Check.True(!ViveTlsSimulationCandidate.MeetsPolicy(true,true,true,true,true,SslProtocols.Tls12,true,true,true));
                Check.True(!ViveTlsSimulationCandidate.MeetsPolicy(true,true,true,true,true,SslProtocols.Tls13,false,true,true));
                Check.True(!ViveTlsSimulationCandidate.MeetsPolicy(true,true,true,true,true,SslProtocols.Tls13,true,true,false));
                // This pure result is not a handshake or an authorization.
                Check.True(ViveTlsSimulationCandidate.MeetsPolicy(true,true,true,true,true,SslProtocols.Tls13,true,true,true));
            });
            suite.Run("VIVE synthetic TLS role policy rejects wrong or shared certificate usage",()=>{
                const string server="1.3.6.1.5.5.7.3.1",client="1.3.6.1.5.5.7.3.2";
                Check.True(ViveTlsSimulationCandidate.HasOnlyRoleUsage(new[]{server},true));
                Check.True(ViveTlsSimulationCandidate.HasOnlyRoleUsage(new[]{client},false));
                Check.True(!ViveTlsSimulationCandidate.HasOnlyRoleUsage(new[]{server},false));
                Check.True(!ViveTlsSimulationCandidate.HasOnlyRoleUsage(new[]{client},true));
                Check.True(!ViveTlsSimulationCandidate.HasOnlyRoleUsage(new[]{server,client},true));
                Check.True(!ViveTlsSimulationCandidate.HasOnlyRoleUsage(new string[0],false));
                Check.True(!ViveTlsSimulationCandidate.HasOnlyRoleUsage(null,true));
            });
            suite.Run("VIVE synthetic TLS refuses unsupported critical extensions",()=>{
                var understood=new[]{"2.5.29.37","2.5.29.15","2.5.29.19"};
                foreach(string oid in understood)
                {
                    Check.True(ViveTlsSimulationCandidate.AllowsExtension(new X509Extension(new Oid(oid),new byte[]{5,0},true)));
                    Check.True(ViveTlsSimulationCandidate.AllowsExtension(new X509Extension(new Oid(oid),new byte[]{5,0},false)));
                }
                Check.True(!ViveTlsSimulationCandidate.AllowsExtension(new X509Extension(new Oid("1.2.3.4"),new byte[]{5,0},true)));
                Check.True(ViveTlsSimulationCandidate.AllowsExtension(new X509Extension(new Oid("1.2.3.4"),new byte[]{5,0},false)));
                Check.True(!ViveTlsSimulationCandidate.AllowsExtension(null));
                Check.True(!ViveTlsSimulationCandidate.AllowsExtensionOid(null,true));
                Check.True(!ViveTlsSimulationCandidate.AllowsExtensionOid("",false));
                Check.True(!ViveTlsSimulationCandidate.AllowsExtensionOid(" ",false));
            });
            suite.Run("VIVE synthetic TLS cannot emit application marker before an actual handshake",()=>{
                ViveTlsSimulationStream participant,op;ViveTlsSimulationStream.CreatePair(out participant,out op);
                using(var candidate=new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,new byte[32]))
                {
                    Check.True(!candidate.AuthenticatedFixture&&!candidate.CanEnterCountdown&&!candidate.CanRecordOrExport&&!candidate.CanTransmitRealData);
                    Check.True(!candidate.TryWriteSimulationMarker());Check.Equal(0,op.Pending);
                    Check.True(!candidate.Authenticate(null));Check.Equal("local_identity_invalid",candidate.LastFailure);
                    Check.True(!candidate.TryWriteSimulationMarker());Check.Equal(0,op.Pending);
                    Check.Throws<InvalidOperationException>(()=>candidate.Authenticate(null));
                }
                op.Dispose();
            });
            suite.Run("VIVE synthetic TLS fixture is bounded in-memory only and closes both ends",()=>{
                ViveTlsSimulationStream participant,op;ViveTlsSimulationStream.CreatePair(out participant,out op);
                Check.Throws<ArgumentException>(()=>new ViveTlsSimulationCandidate((ViveTlsSimulationRole)99,participant,new byte[32]));
                Check.Throws<ArgumentException>(()=>new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,new byte[31]));
                participant.Write(new byte[]{7,8},0,2);Check.Equal(2,op.Pending);var buffer=new byte[2];Check.Equal(2,op.Read(buffer,0,2));
                Check.Equal((byte)7,buffer[0]);Check.Equal((byte)8,buffer[1]);
                var excessive=new byte[1024*1024+1];Check.Throws<IOException>(()=>participant.Write(excessive,0,excessive.Length));
                participant.Dispose();Check.Equal(0,op.Read(buffer,0,2));Check.Throws<IOException>(()=>op.Write(buffer,0,1));op.Dispose();
            });
            suite.Run("VIVE synthetic TLS stalled in-memory read has a finite deadline",()=>{
                ViveTlsSimulationStream participant,op;
                Check.Throws<ArgumentOutOfRangeException>(()=>{ViveTlsSimulationStream a,b;ViveTlsSimulationStream.CreatePairForFixture(0,out a,out b);});
                Check.Throws<ArgumentOutOfRangeException>(()=>{ViveTlsSimulationStream a,b;ViveTlsSimulationStream.CreatePairForFixture(10001,out a,out b);});
                ViveTlsSimulationStream.CreatePairForFixture(25,out participant,out op);
                var clock=Stopwatch.StartNew();
                try{Check.Throws<IOException>(()=>participant.Read(new byte[1],0,1));Check.True(clock.ElapsedMilliseconds<2000,"fixture read was not bounded");}
                finally{participant.Dispose();op.Dispose();}
            });
            suite.Run("VIVE synthetic TLS dispose releases a waiting in-memory reader",()=>{
                ViveTlsSimulationStream participant,op;ViveTlsSimulationStream.CreatePairForFixture(5000,out participant,out op);
                using(var started=new ManualResetEvent(false))
                {
                    int result=-1;Exception failure=null;
                    var reader=new Thread(()=>{started.Set();try{result=participant.Read(new byte[1],0,1);}catch(Exception e){failure=e;}});
                    reader.IsBackground=true;reader.Start();Check.True(started.WaitOne(1000));op.Dispose();
                    Check.True(reader.Join(2000),"closed fixture did not release its reader");
                    Check.True(failure==null);Check.Equal(0,result);
                }
                participant.Dispose();
            });
            suite.Run("VIVE synthetic TLS diagnostic excludes exception messages",()=>{
                const string secret="fixture-secret-must-not-be-logged";
                var error=new IOException(secret,new InvalidOperationException(secret));
                string safe=ViveTlsSimulationCandidate.SafeExceptionKind(error);
                Check.True(safe.StartsWith("IOException:"));Check.True(safe.Contains("/InvalidOperationException:"));
                Check.True(!safe.Contains(secret));Check.Equal("none",ViveTlsSimulationCandidate.SafeExceptionKind(null));
            });
        }
    }
}
