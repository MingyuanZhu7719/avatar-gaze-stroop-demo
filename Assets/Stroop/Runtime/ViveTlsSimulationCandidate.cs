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
        SslStream tls;bool attempted,closed,peerPinMatched;volatile bool authenticated;int markerWriter;string lastFailure;
        internal bool AuthenticatedFixture {get{lock(gate){return authenticated&&!closed;}}}
        // Fixed stage/type/HRESULT only: no exception messages, certificate
        // details, device identifiers or data enter a fixture diagnostic.
        internal string LastFailure {get{lock(gate){return lastFailure;}}}
        internal static string SafeExceptionKind(Exception error)
        {
            if(error==null)return "none";
            string result=error.GetType().Name+":"+error.HResult.ToString("X8");
            if(error.InnerException!=null)result+="/"+error.InnerException.GetType().Name+":"+error.InnerException.HResult.ToString("X8");
            return result;
        }
        void NoteFailure(string stage,Exception error=null)
        {lock(gate){if(lastFailure==null)lastFailure=stage+(error==null?"":":"+SafeExceptionKind(error));}}
        internal bool CanEnterCountdown {get{return false;}}
        internal bool CanRecordOrExport {get{return false;}}
        internal bool CanTransmitRealData {get{return false;}}
        internal ViveTlsSimulationCandidate(ViveTlsSimulationRole role,ViveTlsSimulationStream stream,byte[] expectedPeerPin)
        {
            if(!Enum.IsDefined(typeof(ViveTlsSimulationRole),role)||stream==null||expectedPeerPin==null||expectedPeerPin.Length!=32)
                throw new ArgumentException("Explicit simulation role, in-memory stream and SHA-256 peer pin are required");
            this.role=role;this.stream=stream;this.expectedPeerPin=(byte[])expectedPeerPin.Clone();
        }
        // Expected pin and certificate DER are public identity data, not a
        // private key. Constant-work comparison avoids early byte differences.
        internal static bool MatchesPin(byte[] certificateDer,byte[] pin)
        {
            if(certificateDer==null||certificateDer.Length==0||pin==null||pin.Length!=32)return false;
            byte[] digest;using(var sha=SHA256.Create())digest=sha.ComputeHash(certificateDer);
            int difference=0;for(int i=0;i<32;i++)difference|=digest[i]^pin[i];return difference==0;
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
                    if(!MatchesPin(peer.RawData,expectedPeerPin)){NoteFailure("peer_pin_invalid");return false;}
                    // Pinned self-signed identities intentionally do not rely on
                    // system CA/name matching. TLS still proves key possession.
                    lock(gate){if(closed)return false;peerPinMatched=true;return true;}
                }
            }
            catch(Exception e){NoteFailure("peer_validation_exception",e);return false;}
        }
        // May block only inside a synthetic fixture; caller must use a worker
        // and Dispose to cancel. There is no protocol downgrade or retry.
        internal bool Authenticate(X509Certificate2 localCertificate)
        {
            lock(gate){if(attempted||closed)throw new InvalidOperationException("Fresh fixture required");attempted=true;}
            string stage="local_identity";
            try
            {
                if(localCertificate==null||!localCertificate.HasPrivateKey||!CertificateMatchesRole(localCertificate,role==ViveTlsSimulationRole.OperatorServer)||
                    MatchesPin(localCertificate.RawData,expectedPeerPin)){NoteFailure("local_identity_invalid");return Fail();}
                stage="stream_create";
                var candidate=new SslStream(stream,false,ValidatePeer);
                bool accepted;
                lock(gate){accepted=!closed;if(accepted)tls=candidate;}
                if(!accepted){candidate.Dispose();return false;}
                stage=role==ViveTlsSimulationRole.ParticipantClient?"client_handshake":"server_handshake";
                if(role==ViveTlsSimulationRole.ParticipantClient)
                    candidate.AuthenticateAsClient("simulation-peer",new X509CertificateCollection{localCertificate},SslProtocols.Tls13,false);
                else candidate.AuthenticateAsServer(localCertificate,true,SslProtocols.Tls13,false);
                bool verified=MeetsPolicy(peerPinMatched,candidate.IsAuthenticated,candidate.IsMutuallyAuthenticated,candidate.IsEncrypted,candidate.IsSigned,
                    candidate.SslProtocol,candidate.IsServer,role==ViveTlsSimulationRole.OperatorServer,candidate.RemoteCertificate!=null)&&
                    MatchesPin(candidate.RemoteCertificate.GetRawCertData(),expectedPeerPin);
                if(!verified)NoteFailure("handshake_policy_rejected");
                lock(gate){accepted=!closed&&verified;if(accepted)authenticated=true;}
                return accepted||Fail();
            }
            catch(Exception e){NoteFailure(stage+"_exception",e);return Fail();}
        }
        // Fixed non-sensitive marker only. No caller-supplied payload or real
        // head/eye/task data can enter this simulation candidate.
        internal bool TryWriteSimulationMarker()
        {
            if(Interlocked.CompareExchange(ref markerWriter,1,0)!=0)return false;
            try
            {
                SslStream selected;
                lock(gate){if(closed||!authenticated||tls==null)return false;selected=tls;}
                // Do not hold the lifecycle lock during TLS I/O: a concurrent
                // Dispose must be able to close the sealed stream to wake it.
                try
                {
                    var marker=new byte[]{0x53,0x49,0x4d};selected.Write(marker,0,marker.Length);selected.Flush();
                    lock(gate){return !closed&&authenticated&&Object.ReferenceEquals(selected,tls);}
                }
                catch(Exception){return Fail();}
            }
            finally{Interlocked.Exchange(ref markerWriter,0);}
        }
        bool Fail(){Dispose();return false;}
        public void Dispose()
        {
            SslStream existing;
            lock(gate){if(closed)return;closed=true;authenticated=false;existing=tls;tls=null;}
            // Close the fixture first to wake a blocked handshake before TLS
            // cleanup. No key zeroization guarantee is claimed for managed data.
            stream.Dispose();if(existing!=null)existing.Dispose();
            Array.Clear(expectedPeerPin,0,expectedPeerPin.Length);
        }
    }
}
#endif
