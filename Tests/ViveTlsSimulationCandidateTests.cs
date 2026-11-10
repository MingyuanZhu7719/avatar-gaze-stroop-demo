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
#if VIVE_TLS_SAFE_TESTS
        sealed class PrivateIdentityMustNotAppearException:Exception {}
#endif
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
                    Check.Equal("not_authenticated",candidate.MarkerStatus);
                    Check.True(!candidate.TryReadSimulationMarker());Check.Equal(0,op.Pending);
                    Check.True(candidate.LastFailure==null);
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
                Check.Throws<ArgumentNullException>(()=>participant.Write(null,0,1));
                Check.Throws<ArgumentOutOfRangeException>(()=>participant.Read(new byte[1],-1,1));
                Check.Throws<ArgumentOutOfRangeException>(()=>participant.Write(new byte[1],0,2));
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

#if VIVE_TLS_SAFE_TESTS
            suite.Run("VIVE synthetic TLS total deadline closes a still-blocked read",()=>{
                ViveTlsSimulationStream participant,op;ViveTlsSimulationStream.CreatePairForFixture(5000,out participant,out op);
                using(var candidate=new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,new byte[32],35,250))
                {
                    var timer=Stopwatch.StartNew();Check.True(!candidate.RunDeadlineProbeForFixture(0,true,false));
                    Check.True(timer.ElapsedMilliseconds<1500);Check.Equal("handshake_deadline_exceeded",candidate.LastFailure);
                    Check.Equal("deadline_exceeded",candidate.AuthenticationStatus);Check.Equal("complete",candidate.CleanupStatus);
                    Check.True(candidate.PinsClearedForFixture);Check.True(!candidate.AuthenticatedFixture);
                    Check.Equal(0,op.Pending);Check.Throws<IOException>(()=>op.Write(new byte[1],0,1));
                    Check.Throws<InvalidOperationException>(()=>candidate.Authenticate(null));
                }
                op.Dispose();
            });
            suite.Run("VIVE synthetic TLS shutdown budget does not hide an unfinished worker",()=>{
                ViveTlsSimulationStream participant,op;ViveTlsSimulationStream.CreatePair(out participant,out op);
                using(var candidate=new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,new byte[32],25,25))
                {
                    var timer=Stopwatch.StartNew();Check.True(!candidate.RunDeadlineProbeForFixture(300,false,false));
                    Check.True(timer.ElapsedMilliseconds<1500);Check.Equal("shutdown_timeout",candidate.CleanupStatus);
                    Check.Equal("handshake_deadline_exceeded",candidate.LastFailure);Check.True(candidate.PinsClearedForFixture);
                    Check.True(SpinWait.SpinUntil(()=>candidate.HandshakeStoppedForFixture,1500));
                    Check.True(!candidate.AuthenticatedFixture);Check.Equal("shutdown_timeout",candidate.CleanupStatus);
                }
                op.Dispose();
            });
            suite.Run("VIVE synthetic TLS first failure survives timeout and cleanup failure",()=>{
                ViveTlsSimulationStream participant,op;ViveTlsSimulationStream.CreatePair(out participant,out op);
                var pin=new byte[32];for(int i=0;i<pin.Length;i++)pin[i]=9;
                using(var candidate=new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,pin,35,250))
                {
                    candidate.InjectCleanupFailureForFixture=true;
                    Check.True(!candidate.RunDeadlineProbeForFixture(0,true,true));
                    Check.True(candidate.LastFailure.StartsWith("fixture_primary_failure:IOException:"));
                    Check.True(candidate.LastCleanupFailure.StartsWith("tls_dispose:IOException:"));
                    Check.Equal("failed",candidate.CleanupStatus);Check.True(candidate.PinsClearedForFixture);Check.Equal((byte)9,pin[0]);
                    Check.True(!candidate.LastFailure.Contains("redacted"));Check.True(!candidate.LastCleanupFailure.Contains("redacted"));
                    candidate.Dispose();Check.Equal("failed",candidate.CleanupStatus);
                    Check.True(!candidate.CanEnterCountdown&&!candidate.CanRecordOrExport&&!candidate.CanTransmitRealData);
                }
                op.Dispose();
            });
            suite.Run("VIVE synthetic TLS cancellation is terminal and independently diagnosed",()=>{
                ViveTlsSimulationStream participant,op;ViveTlsSimulationStream.CreatePair(out participant,out op);
                using(var candidate=new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,new byte[32],3000,250))
                {
                    Exception unexpected=null;
                    var caller=new Thread(()=>{try{candidate.RunDeadlineProbeForFixture(0,true,false);}catch(Exception e){unexpected=e;}});
                    caller.IsBackground=true;caller.Start();Check.True(SpinWait.SpinUntil(()=>candidate.AuthenticationStatus=="running",1000));
                    candidate.Dispose();Check.True(caller.Join(1500));Check.True(unexpected==null);
                    Check.Equal("cancelled",candidate.LastFailure);Check.Equal("cancelled",candidate.AuthenticationStatus);
                    Check.Equal("complete",candidate.CleanupStatus);Check.True(candidate.PinsClearedForFixture);
                    Check.True(!candidate.TryReadSimulationMarker());Check.Equal("closed",candidate.MarkerStatus);
                }
                op.Dispose();
            });
            suite.Run("VIVE synthetic TLS refuses publication after the total deadline",()=>{
                ViveTlsSimulationStream participant,op;ViveTlsSimulationStream.CreatePair(out participant,out op);
                using(var candidate=new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,new byte[32],35,250))
                {
                    candidate.DelayAfterJoinForFixture=80;
                    Check.True(!candidate.RunDeadlineProbeForFixture(0,false,false));
                    Check.Equal("handshake_deadline_exceeded",candidate.LastFailure);
                    Check.Equal("deadline_exceeded",candidate.AuthenticationStatus);Check.True(!candidate.AuthenticatedFixture);
                    Check.Equal("complete",candidate.CleanupStatus);
                }
                op.Dispose();
            });
            suite.Run("VIVE synthetic TLS join errors never claim completed shutdown",()=>{
                ViveTlsSimulationStream participant,op;ViveTlsSimulationStream.CreatePair(out participant,out op);
                using(var candidate=new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,new byte[32]))
                {
                    candidate.InjectCleanupJoinFailureForFixture=true;
                    Check.True(!candidate.Authenticate(null));Check.Equal("local_identity_invalid",candidate.LastFailure);
                    Check.Equal("shutdown_unverified",candidate.CleanupStatus);
                    Check.True(candidate.LastCleanupFailure.StartsWith("shutdown_join:ThreadInterruptedException:"));
                    Check.True(!candidate.LastCleanupFailure.Contains("redacted"));Check.True(candidate.PinsClearedForFixture);
                    Check.True(!candidate.AuthenticatedFixture);
                }
                op.Dispose();
            });
            suite.Run("VIVE synthetic TLS preserves a fixed native error code without messages",()=>{
                const string secret="private native diagnostic message";
                var native=new System.ComponentModel.Win32Exception(unchecked((int)0x8009030e),secret);
                string safe=ViveTlsSimulationCandidate.SafeExceptionKind(new AuthenticationException(secret,native));
                Check.True(safe.Contains("/Win32Exception:"));Check.True(safe.EndsWith(":native=8009030E"));
                Check.True(!safe.Contains(secret));
            });
            suite.Run("VIVE synthetic TLS diagnostic type names are allowlisted",()=>{
                string result=ViveTlsSimulationCandidate.SafeExceptionKind(new PrivateIdentityMustNotAppearException());
                Check.True(result.StartsWith("Exception:"));Check.True(!result.Contains("PrivateIdentity"));
            });
            suite.Run("VIVE synthetic TLS fixture deadline arguments are bounded",()=>{
                ViveTlsSimulationStream participant,op;ViveTlsSimulationStream.CreatePair(out participant,out op);
                try
                {
                    Check.Throws<ArgumentOutOfRangeException>(()=>new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,new byte[32],0,20));
                    Check.Throws<ArgumentOutOfRangeException>(()=>new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,new byte[32],10001,20));
                    Check.Throws<ArgumentOutOfRangeException>(()=>new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,new byte[32],20,0));
                    Check.Throws<ArgumentOutOfRangeException>(()=>new ViveTlsSimulationCandidate(ViveTlsSimulationRole.ParticipantClient,participant,new byte[32],20,1001));
                }
                finally{participant.Dispose();op.Dispose();}
            });
#endif
        }
    }
}
