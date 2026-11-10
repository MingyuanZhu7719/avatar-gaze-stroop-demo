// Unity 6000's actual API profile does not expose SslProtocols.Tls13. The
// candidate is therefore unavailable in every Unity Editor/player build; do
// not substitute an integer protocol value or silently downgrade. The ordinary
// portable test environment retains this simulation-only reference candidate.
#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace AvatarStroop.Runtime
{
    internal enum ViveTlsSimulationRole { ParticipantClient=1,OperatorServer=2 }

    // A sealed, bounded in-process duplex. It has no socket, process, file,
    // clipboard or caller-provided write callback. Close either side to cancel a
    // blocked synthetic handshake. Data here are fixtures only.
    internal sealed class ViveTlsSimulationStream : Stream
    {
        sealed class Pipe {internal readonly object Gate=new object();internal readonly Queue<byte> Bytes=new Queue<byte>();internal bool Closed;}
        const int Capacity=1024*1024;
        readonly Pipe incoming,outgoing;readonly int readTimeoutMilliseconds;volatile bool closed;
        ViveTlsSimulationStream(Pipe input,Pipe output,int readTimeoutMilliseconds){incoming=input;outgoing=output;this.readTimeoutMilliseconds=readTimeoutMilliseconds;}
        internal static void CreatePair(out ViveTlsSimulationStream participant,out ViveTlsSimulationStream op)
        {CreatePairForFixture(5000,out participant,out op);}
        // A fixture may shorten the timeout; production sockets are not exposed.
        internal static void CreatePairForFixture(int timeoutMilliseconds,out ViveTlsSimulationStream participant,out ViveTlsSimulationStream op)
        {
            if(timeoutMilliseconds<1||timeoutMilliseconds>10000)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            var a=new Pipe();var b=new Pipe();participant=new ViveTlsSimulationStream(a,b,timeoutMilliseconds);op=new ViveTlsSimulationStream(b,a,timeoutMilliseconds);
        }
        public override bool CanRead {get{return !closed;}} public override bool CanWrite {get{return !closed;}} public override bool CanSeek {get{return false;}}
        public override long Length {get{throw new NotSupportedException();}} public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
        public override void Flush(){}
        public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
        public override void SetLength(long value){throw new NotSupportedException();}
        static void Bounds(byte[] buffer,int offset,int count)
        {if(buffer==null)throw new ArgumentNullException("buffer");if(offset<0||count<0||offset>buffer.Length-count)throw new ArgumentOutOfRangeException("offset/count");}
        public override int Read(byte[] buffer,int offset,int count)
        {
            Bounds(buffer,offset,count);if(count==0)return 0;
            long start=Stopwatch.GetTimestamp();
            lock(incoming.Gate)
            {
                while(incoming.Bytes.Count==0&&!incoming.Closed)
                {
                    double elapsed=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;
                    if(elapsed<0||elapsed>=readTimeoutMilliseconds)throw new IOException("Synthetic duplex read timed out");
                    Monitor.Wait(incoming.Gate,Math.Max(1,(int)Math.Ceiling(readTimeoutMilliseconds-elapsed)));
                }
                if(incoming.Bytes.Count==0)return 0;
                int n=Math.Min(count,incoming.Bytes.Count);for(int i=0;i<n;i++)buffer[offset+i]=incoming.Bytes.Dequeue();return n;
            }
        }
        public override void Write(byte[] buffer,int offset,int count)
        {
            Bounds(buffer,offset,count);
            lock(outgoing.Gate)
            {
                if(closed||outgoing.Closed)throw new IOException("Synthetic duplex closed");
                if(count>Capacity-outgoing.Bytes.Count)throw new IOException("Synthetic duplex capacity exceeded");
                for(int i=0;i<count;i++)outgoing.Bytes.Enqueue(buffer[offset+i]);Monitor.PulseAll(outgoing.Gate);
            }
        }
        internal int Pending {get{lock(incoming.Gate)return incoming.Bytes.Count;}}
        protected override void Dispose(bool disposing)
        {
            if(disposing&&!closed)
            {
                closed=true;
                lock(incoming.Gate){incoming.Closed=true;incoming.Bytes.Clear();Monitor.PulseAll(incoming.Gate);}
                lock(outgoing.Gate){outgoing.Closed=true;outgoing.Bytes.Clear();Monitor.PulseAll(outgoing.Gate);}
            }
            base.Dispose(disposing);
        }
    }

    // Test-only TLS 1.3 mutual-certificate candidate. The only transport is the
    // sealed in-memory fixture above; there is no socket, data-frame, device or
    // production UI API. A caller supplies a certificate object; this class
    // never creates, imports, stores or exports private keys/certificates. The
    // caller must independently establish that a supplied test key is ephemeral.
    // Pinning is meaningful only if the exact certificate fingerprint was
    // verified out of band for the intended peer and role.
    internal sealed class ViveTlsSimulationCandidate : IDisposable
    {

        readonly object gate=new object();readonly ViveTlsSimulationRole role;readonly ViveTlsSimulationStream stream;readonly byte[] expectedPeerPin;
        readonly int handshakeMilliseconds,shutdownMilliseconds;
        SslStream tls;Thread handshakeWorker;bool attempted,closed,peerPinMatched;volatile bool authenticated;int markerWriter,markerReader;
        long handshakeStarted;string lastFailure,lastCleanupFailure,cleanupStatus="not_started",authenticationStatus="not_started",markerStatus="not_started";
        bool policyObserved,policyTls13,policyMutual,policyEncrypted,policySigned,policyRole,policyPin;
#if VIVE_TLS_SAFE_TESTS
        internal bool InjectCleanupFailureForFixture,InjectCleanupJoinFailureForFixture;
        internal int DelayAfterJoinForFixture;
        internal bool HandshakeStoppedForFixture {get{lock(gate){return handshakeWorker==null||!handshakeWorker.IsAlive;}}}
        internal bool PinsClearedForFixture {get{lock(gate){foreach(byte b in expectedPeerPin)if(b!=0)return false;return true;}}}
#endif
        internal bool AuthenticatedFixture {get{lock(gate){return authenticated&&!closed;}}}
        internal string AuthenticationStatus {get{lock(gate){return authenticationStatus;}}}
        internal string LastFailure {get{lock(gate){return lastFailure;}}}
        internal string CleanupStatus {get{lock(gate){return cleanupStatus;}}}
        internal string LastCleanupFailure {get{lock(gate){return lastCleanupFailure;}}}
        internal string MarkerStatus {get{lock(gate){return markerStatus;}}}
        // Observation only, not an authentication capability or product gate.
        internal string NegotiatedPolicyDiagnostics {get{lock(gate){return "observed="+policyObserved+";tls13="+policyTls13+";mutual="+policyMutual+
            ";encrypted="+policyEncrypted+";signed="+policySigned+";role="+policyRole+";exact_pin="+policyPin;}}}
        // A fixed allowlist prevents custom exception type names from becoming
        // identifiers. Never include messages, stacks, subjects, pins or keys.
        static string ExceptionKind(Exception error)
        {
            if(error is System.ComponentModel.Win32Exception)return "Win32Exception";
            if(error is CryptographicException)return "CryptographicException";
            if(error is AuthenticationException)return "AuthenticationException";
            if(error is OperationCanceledException)return "OperationCanceledException";
            if(error is ThreadInterruptedException)return "ThreadInterruptedException";
            if(error is ObjectDisposedException)return "ObjectDisposedException";
            if(error is IOException)return "IOException";
            if(error is TimeoutException)return "TimeoutException";
            if(error is PlatformNotSupportedException)return "PlatformNotSupportedException";
            if(error is NotSupportedException)return "NotSupportedException";
            if(error is UnauthorizedAccessException)return "UnauthorizedAccessException";
            if(error is System.Security.SecurityException)return "SecurityException";
            if(error is ArgumentException)return "ArgumentException";
            if(error is InvalidOperationException)return "InvalidOperationException";
            return "Exception";
        }
        static string ExceptionCode(Exception error)
        {
            string result=ExceptionKind(error)+":"+error.HResult.ToString("X8");
            var native=error as System.ComponentModel.Win32Exception;
            if(native!=null)result+=":native="+native.NativeErrorCode.ToString("X8");
            return result;
        }
        internal static string SafeExceptionKind(Exception error)
        {
            if(error==null)return "none";
            string result=ExceptionCode(error);
            if(error.InnerException!=null)result+="/"+ExceptionCode(error.InnerException);
            return result;
        }
        void NoteFailure(string stage,Exception error=null)
        {lock(gate){if(lastFailure==null)lastFailure=stage+(error==null?"":":"+SafeExceptionKind(error));}}
        void NoteCleanupFailure(string stage,Exception error)
        {lock(gate){if(lastCleanupFailure==null)lastCleanupFailure=stage+":"+SafeExceptionKind(error);}}
        internal bool CanEnterCountdown {get{return false;}}
        internal bool CanRecordOrExport {get{return false;}}
        internal bool CanTransmitRealData {get{return false;}}
        internal ViveTlsSimulationCandidate(ViveTlsSimulationRole role,ViveTlsSimulationStream stream,byte[] expectedPeerPin)
            :this(role,stream,expectedPeerPin,5000,500){}
        internal ViveTlsSimulationCandidate(ViveTlsSimulationRole role,ViveTlsSimulationStream stream,byte[] expectedPeerPin,int handshakeMilliseconds,int shutdownMilliseconds)
        {
            if(!Enum.IsDefined(typeof(ViveTlsSimulationRole),role)||stream==null||expectedPeerPin==null||expectedPeerPin.Length!=32)
                throw new ArgumentException("Explicit simulation role, in-memory stream and SHA-256 peer pin are required");
            if(handshakeMilliseconds<1||handshakeMilliseconds>10000||shutdownMilliseconds<1||shutdownMilliseconds>1000)
                throw new ArgumentOutOfRangeException("fixture deadline");
            this.role=role;this.stream=stream;this.expectedPeerPin=(byte[])expectedPeerPin.Clone();
            this.handshakeMilliseconds=handshakeMilliseconds;this.shutdownMilliseconds=shutdownMilliseconds;
        }
        // Expected pin and certificate DER are public identity data, not a
        // private key. Constant-work comparison avoids early byte differences.
        internal static bool MatchesPin(byte[] certificateDer,byte[] pin)
        {
            if(certificateDer==null||certificateDer.Length==0||pin==null||pin.Length!=32)return false;
            byte[] digest;using(var sha=SHA256.Create())digest=sha.ComputeHash(certificateDer);
            int difference=0;for(int i=0;i<32;i++)difference|=digest[i]^pin[i];return difference==0;
        }
        // Crypto/provider work stays outside the lifecycle lock so the total
        // deadline can close the fixture even if that work does not return.
        bool MatchesExpectedPeer(byte[] certificateDer)
        {
            if(certificateDer==null||certificateDer.Length==0)return false;
            byte[] digest;using(var sha=SHA256.Create())digest=sha.ComputeHash(certificateDer);
            try
            {
                lock(gate)
                {
                    if(closed)return false;
                    int difference=0;for(int i=0;i<32;i++)difference|=digest[i]^expectedPeerPin[i];return difference==0;
                }
            }
            finally{Array.Clear(digest,0,digest.Length);}
        }
        internal static bool HasOnlyRoleUsage(IList<string> usages,bool isServer)
        {
            string required=isServer?"1.3.6.1.5.5.7.3.1":"1.3.6.1.5.5.7.3.2";
            return usages!=null&&usages.Count==1&&usages[0]==required;
        }
        // This fixture intentionally pins self-signed peers rather than relying
        // on a chain's extension validation. Unknown critical semantics cannot
        // be ignored; unknown noncritical extensions preserve the old policy.
        internal static bool AllowsExtensionOid(string oid,bool critical)
        {
            if(String.IsNullOrWhiteSpace(oid))return false;
            bool understood=oid=="2.5.29.37"||oid=="2.5.29.15"||oid=="2.5.29.19";
            return understood||!critical;
        }
        internal static bool AllowsExtension(X509Extension extension)
        {return extension!=null&&extension.Oid!=null&&AllowsExtensionOid(extension.Oid.Value,extension.Critical);}
        static bool CertificateMatchesRole(X509Certificate2 certificate,bool isServer)
        {
            var now=DateTime.UtcNow;
            if(now<certificate.NotBefore.ToUniversalTime()||now>certificate.NotAfter.ToUniversalTime())return false;
            bool sawUsage=false,sawKeyUsage=false,sawConstraints=false;
            foreach(X509Extension extension in certificate.Extensions)
            {
                if(!AllowsExtension(extension))return false;
                if(extension.Oid.Value=="2.5.29.37")
                {
                    if(sawUsage)return false;sawUsage=true;
                    var parsed=new X509EnhancedKeyUsageExtension(extension,extension.Critical);var values=new List<string>();
                    foreach(Oid item in parsed.EnhancedKeyUsages)values.Add(item.Value);
                    if(!HasOnlyRoleUsage(values,isServer))return false;
                }
                else if(extension.Oid.Value=="2.5.29.15")
                {
                    if(sawKeyUsage)return false;sawKeyUsage=true;
                    var parsed=new X509KeyUsageExtension(extension,extension.Critical);
                    if((parsed.KeyUsages&X509KeyUsageFlags.DigitalSignature)==0||(parsed.KeyUsages&X509KeyUsageFlags.KeyCertSign)!=0)return false;
                }
                else if(extension.Oid.Value=="2.5.29.19")
                {
                    if(sawConstraints)return false;sawConstraints=true;
                    if(new X509BasicConstraintsExtension(extension,extension.Critical).CertificateAuthority)return false;
                }
            }
            return sawUsage&&sawKeyUsage;
        }
        // A pure rejection policy, never an authentication receipt. The only
        // caller that can activate this fixture reads these values from its own
        // private SslStream after a real handshake.
        internal static bool MeetsPolicy(bool pinMatched,bool isAuthenticated,bool isMutual,bool isEncrypted,bool isSigned,
            SslProtocols protocol,bool actualServer,bool expectedServer,bool peerCertificatePresent)
        {return pinMatched&&isAuthenticated&&isMutual&&isEncrypted&&isSigned&&protocol==SslProtocols.Tls13&&actualServer==expectedServer&&peerCertificatePresent;}
        bool ValidatePeer(object sender,X509Certificate certificate,X509Chain chain,SslPolicyErrors errors)
        {
            if(certificate==null||(errors&SslPolicyErrors.RemoteCertificateNotAvailable)!=0){NoteFailure("peer_missing");return false;}
            try
            {
                using(var peer=new X509Certificate2(certificate))
                {
                    if(!CertificateMatchesRole(peer,role==ViveTlsSimulationRole.ParticipantClient)){NoteFailure("peer_role_invalid");return false;}
                    bool pinMatches=MatchesExpectedPeer(peer.RawData);
                    lock(gate)
                    {
                        if(closed)return false;
                        if(!pinMatches){NoteFailure("peer_pin_invalid");return false;}
                    }
                    // Pinned self-signed identities intentionally do not rely on
                    // system CA/name matching. TLS still proves key possession.
                    lock(gate){if(closed)return false;peerPinMatched=true;return true;}
                }
            }
            catch(Exception e){NoteFailure("peer_validation_exception",e);return false;}
        }

        // The deadline covers local checks, SslStream construction, both TLS
        // handshake roles and post-auth policy inspection. Read timeouts alone
        // cannot enforce it: a peer may keep producing partial bytes.
        static int Remaining(long start,int milliseconds)
        {
            double elapsed=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;
            return elapsed<0||elapsed>=milliseconds?0:Math.Max(1,(int)Math.Ceiling(milliseconds-elapsed));
        }
        bool Expired {get{return Remaining(handshakeStarted,handshakeMilliseconds)==0;}}
        internal bool Authenticate(X509Certificate2 localCertificate)
        {return AuthenticateBounded(()=>AuthenticateCore(localCertificate));}
        bool AuthenticateBounded(Action operation)
        {
            Thread worker=null;bool started=false;
            lock(gate)
            {
                if(attempted||closed)throw new InvalidOperationException("Fresh fixture required");
                attempted=true;authenticationStatus="running";handshakeStarted=Stopwatch.GetTimestamp();
                try
                {
                    worker=new Thread(()=>{
                        try{operation();}
                        catch(Exception e){NoteFailure("handshake_worker_exception",e);}
                    });
                    worker.IsBackground=true;handshakeWorker=worker;
                    // Starting under the lifecycle lock prevents Close from
                    // missing a worker published but not started.
                    worker.Start();started=true;
                }
                catch(Exception e){handshakeWorker=null;NoteFailure("handshake_worker_start_exception",e);authenticationStatus="failed";}
            }
            if(!started){Close(false);return false;}
            bool joined;
            try{joined=worker.Join(Remaining(handshakeStarted,handshakeMilliseconds));}
            catch(Exception e)
            {
                NoteFailure("handshake_wait_exception",e);
                lock(gate){if(!closed)authenticationStatus="failed";}
                Close(false);return false;
            }
            if(!joined)
            {
                lock(gate)
                {
                    if(!closed){NoteFailure("handshake_deadline_exceeded");authenticationStatus="deadline_exceeded";}
                }
                Close(false);return false;
            }
#if VIVE_TLS_SAFE_TESTS
            Thread.Sleep(Math.Min(1000,Math.Max(0,DelayAfterJoinForFixture)));
#endif
            lock(gate)
            {
                // Publication itself must still be inside the total deadline,
                // even when the worker finished before this caller resumed.
                if(!closed&&Expired){NoteFailure("handshake_deadline_exceeded");authenticationStatus="deadline_exceeded";}
                else if(!closed&&authenticated)return true;
                else if(!closed&&authenticationStatus=="running")authenticationStatus="failed";
            }
            Close(false);return false;
        }
        void AuthenticateCore(X509Certificate2 localCertificate)
        {
            string stage="local_identity";
            try
            {
                if(localCertificate==null||!localCertificate.HasPrivateKey||!CertificateMatchesRole(localCertificate,role==ViveTlsSimulationRole.OperatorServer))
                {NoteFailure("local_identity_invalid");return;}
                bool sameIdentity=MatchesExpectedPeer(localCertificate.RawData);
                lock(gate)
                {
                    if(closed)return;
                    if(sameIdentity){NoteFailure("local_identity_invalid");return;}
                }
                stage="stream_create";
                var candidate=new SslStream(stream,false,ValidatePeer);
                bool accepted;
                lock(gate){accepted=!closed;if(accepted)tls=candidate;}
                if(!accepted)
                {
                    try{candidate.Dispose();}catch(Exception e){NoteCleanupFailure("unpublished_tls_dispose",e);}
                    return;
                }
                stage=role==ViveTlsSimulationRole.ParticipantClient?"client_handshake":"server_handshake";
                if(role==ViveTlsSimulationRole.ParticipantClient)
                    candidate.AuthenticateAsClient("simulation-peer",new X509CertificateCollection{localCertificate},SslProtocols.Tls13,false);
                else candidate.AuthenticateAsServer(localCertificate,true,SslProtocols.Tls13,false);
                stage="post_auth_policy";
                bool isAuthenticated=candidate.IsAuthenticated,mutual=candidate.IsMutuallyAuthenticated,encrypted=candidate.IsEncrypted,signed=candidate.IsSigned;
                SslProtocols protocol=candidate.SslProtocol;bool actualServer=candidate.IsServer;
                var peer=candidate.RemoteCertificate;
                bool pin=peer!=null&&MatchesExpectedPeer(peer.GetRawCertData());
                lock(gate)
                {
                    if(closed)return;
                    if(Expired){NoteFailure("handshake_deadline_exceeded");authenticationStatus="deadline_exceeded";return;}
                    pin&=peerPinMatched;
                    policyObserved=true;policyTls13=protocol==SslProtocols.Tls13;policyMutual=mutual;policyEncrypted=encrypted;policySigned=signed;
                    policyRole=actualServer==(role==ViveTlsSimulationRole.OperatorServer);policyPin=pin;
                    if(!MeetsPolicy(pin,isAuthenticated,mutual,encrypted,signed,protocol,actualServer,role==ViveTlsSimulationRole.OperatorServer,peer!=null))
                    {NoteFailure("post_auth_policy_rejected");return;}
                    if(Expired){NoteFailure("handshake_deadline_exceeded");authenticationStatus="deadline_exceeded";return;}
                    authenticated=true;authenticationStatus="authenticated";
                }
            }
            catch(Exception e){NoteFailure(stage+"_exception",e);}
        }
#if VIVE_TLS_SAFE_TESTS
        // Compiled only into the certificate-free focused test binary. This
        // exercises the real deadline/shutdown path without TLS, OS keys,
        // callbacks, payload injection or the ability to create authentication.
        internal bool RunDeadlineProbeForFixture(int waitMilliseconds,bool blockOnRead,bool failFirst)
        {
            if(waitMilliseconds<0||waitMilliseconds>1000)throw new ArgumentOutOfRangeException("fixture wait");
            return AuthenticateBounded(()=>{
                if(failFirst)NoteFailure("fixture_primary_failure",new IOException("redacted fixture"));
                if(blockOnRead)stream.Read(new byte[1],0,1);
                else Thread.Sleep(waitMilliseconds);
            });
        }
#endif
        // Fixed non-sensitive marker only, with fixed-size verification. No
        // caller-supplied payload or real head/eye/task data can enter here.
        internal bool TryWriteSimulationMarker(){return TryMarker(false);}
        internal bool TryReadSimulationMarker(){return TryMarker(true);}
        bool TryMarker(bool read)
        {
            if(read)
            {if(Interlocked.CompareExchange(ref markerReader,1,0)!=0)return false;}
            else if(Interlocked.CompareExchange(ref markerWriter,1,0)!=0)return false;
            try
            {
                SslStream selected;
                lock(gate)
                {
                    if(closed||!authenticated||tls==null){markerStatus=closed?"closed":"not_authenticated";return false;}
                    selected=tls;markerStatus=read?"reading":"writing";
                }
                string stage=read?"marker_read":"marker_write";
                try
                {
                    var marker=new byte[]{0x53,0x49,0x4d};
                    if(read)
                    {
                        var received=new byte[3];int total=0;
                        while(total<received.Length)
                        {
                            int count=selected.Read(received,total,received.Length-total);
                            if(count==0){NoteFailure("marker_eof");lock(gate){markerStatus="failed";}Close(false);return false;}
                            total+=count;
                        }
                        int different=0;for(int i=0;i<3;i++)different|=marker[i]^received[i];
                        if(different!=0){NoteFailure("marker_mismatch");lock(gate){markerStatus="failed";}Close(false);return false;}
                    }
                    else{selected.Write(marker,0,marker.Length);selected.Flush();}
                    lock(gate)
                    {
                        if(closed||!authenticated||!Object.ReferenceEquals(selected,tls)){markerStatus="cancelled";return false;}
                        markerStatus=read?"verified":"written";return true;
                    }
                }
                catch(Exception e){NoteFailure(stage+"_exception",e);lock(gate){if(markerStatus!="cancelled")markerStatus="failed";}Close(false);return false;}
            }
            finally{if(read)Interlocked.Exchange(ref markerReader,0);else Interlocked.Exchange(ref markerWriter,0);}
        }
        public void Dispose(){Close(true);}
        void Close(bool cancellation)
        {
            SslStream existing;Thread worker;long started=Stopwatch.GetTimestamp();
            lock(gate)
            {
                if(closed)return;
                if(cancellation&&attempted&&authenticationStatus=="running"){NoteFailure("cancelled");authenticationStatus="cancelled";}
                else if(authenticationStatus=="authenticated"||authenticationStatus=="not_started")authenticationStatus="closed";
                if(cancellation&&(markerStatus=="reading"||markerStatus=="writing")){NoteFailure("marker_cancelled");markerStatus="cancelled";}
                closed=true;authenticated=false;existing=tls;tls=null;worker=handshakeWorker;cleanupStatus="running";
                // Clear even if stream/TLS disposal fails or native work does
                // not stop. All pin comparisons share this same lock.
                Array.Clear(expectedPeerPin,0,expectedPeerPin.Length);
            }
            // Sealed bounded duplex close is independent of native TLS and
            // wakes both endpoints before potentially blocking TLS disposal.
            try{stream.Dispose();}catch(Exception e){NoteCleanupFailure("duplex_dispose",e);}
            Thread cleanup=null;
            bool needsCleanup=existing!=null;
#if VIVE_TLS_SAFE_TESTS
            needsCleanup|=InjectCleanupFailureForFixture;
#endif
            if(needsCleanup)
            {
                try
                {
                    cleanup=new Thread(()=>{
                        try
                        {
                            if(existing!=null)existing.Dispose();
#if VIVE_TLS_SAFE_TESTS
                            if(InjectCleanupFailureForFixture)throw new IOException("redacted cleanup fixture");
#endif
                        }
                        catch(Exception e){NoteCleanupFailure("tls_dispose",e);}
                    });
                    cleanup.IsBackground=true;cleanup.Start();
                }
                catch(Exception e){NoteCleanupFailure("cleanup_worker_start",e);cleanup=null;}
            }
            bool uncertain=false;
            bool cleanupStopped=JoinForShutdown(cleanup,started,ref uncertain);
            bool workerStopped=JoinForShutdown(worker,started,ref uncertain);
            lock(gate){cleanupStatus=uncertain?"shutdown_unverified":!cleanupStopped||!workerStopped?"shutdown_timeout":lastCleanupFailure==null?"complete":"failed";}
        }
        bool JoinForShutdown(Thread worker,long started,ref bool uncertain)
        {
            if(worker==null||worker==Thread.CurrentThread)return true;
            try
            {
#if VIVE_TLS_SAFE_TESTS
                if(InjectCleanupJoinFailureForFixture)throw new ThreadInterruptedException("redacted join fixture");
#endif
                return worker.Join(Remaining(started,shutdownMilliseconds));
            }
            catch(Exception e){uncertain=true;NoteCleanupFailure("shutdown_join",e);return false;}
        }
    }
}
#endif
