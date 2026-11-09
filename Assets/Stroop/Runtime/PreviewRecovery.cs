using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Development preview restart only. This never rehydrates an experiment,
    // resets a receiver, selects a source, or resumes a participant task.
    public static class PreviewRecovery
    {
        public const string Guidance = "Connection interrupted. Owner: end and start NEW session. Progress will not resume.";
        const string ReceiptTag="ASPREVIEWCLOSED",ReceiptVersion="1";
        const int MaximumReceiptBytes=4096;
        static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);

        public static string EndReason(string requestId)
        {
            if(!Hex(requestId,32))throw new ArgumentException("A canonical 32-character lowercase hexadecimal restart request ID is required","requestId");
            return "owner_requested_preview_restart:"+requestId;
        }

        sealed class ClosedEvidence
        {
            public JournalReadResult Journal;
            public string JournalId,Digest;
            public int EndEvents;
            public bool MatchingTechnicalEnd;
        }

        // Called once by the journal owner after successful capture, close and
        // disposal. The receipt attests a returned journal Flush(true), not a
        // physical power-loss guarantee or completion of a study.
        internal static void WriteClosedReceipt(ContinuousJournal journal)
        {
            if(journal==null||journal.IsFaulted||String.IsNullOrEmpty(journal.Path)||
                journal.LastFlushedSequence<0||journal.LastFlushedSequence!=journal.LastWrittenSequence)
                throw new InvalidOperationException("Successful file-journal close and flush are required for a close receipt");
            var evidence=ReadClosed(journal.Path,journal.Identity.SessionId,journal.Identity.Role,null);
            if(evidence.JournalId!=journal.JournalId||evidence.Journal.RecordCount-1!=journal.LastFlushedSequence||
                evidence.Journal.VerifiedBytes!=journal.LastFlushedOffset||evidence.Journal.Identity.ClockId!=journal.Identity.ClockId)
                throw new InvalidDataException("Journal bytes do not match the acknowledged close boundary");
            string line=String.Join("|",new[]{ReceiptTag,ReceiptVersion,Id(journal.Identity.SessionId),journal.Identity.Role,
                Id(journal.Identity.ClockId),journal.JournalId,journal.LastFlushedSequence.ToString(Invariant),
                journal.LastFlushedOffset.ToString(Invariant),evidence.Digest})+"\n";
            byte[] bytes=Encoding.ASCII.GetBytes(line);
            if(bytes.Length>MaximumReceiptBytes)throw new InvalidDataException("Close receipt exceeds its size limit");
            string destination=journal.Path+".closed";
            // Publish only after the new receipt itself has flushed and closed.
            // On failure retain any staging evidence; never delete or overwrite.
            string pending=destination+".pending-"+Guid.NewGuid().ToString("N");
            using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
            File.Move(pending,destination);
        }

        public static bool VerifyClosedSession(string directory,string sessionId,string requestId,out string reason)
        {
            reason=null;
            try
            {
                string expectedEnd=EndReason(requestId);WireCodec.RequireIdentity(sessionId);
                if(String.IsNullOrWhiteSpace(directory)||!Path.IsPathRooted(directory))
                    throw new ArgumentException("An absolute preview session directory is required");
                string root=Path.GetFullPath(directory);
                foreach(string role in new[]{"participant","operator"})
                {
                    string path=Path.Combine(root,role+".journal");
                    var evidence=ReadClosed(path,sessionId,role,role=="participant"?expectedEnd:null);
                    VerifyReceipt(path+".closed",sessionId,role,evidence);
                    if(role=="participant"&&(evidence.EndEvents!=1||!evidence.MatchingTechnicalEnd))
                        throw new InvalidDataException("Participant journal does not contain exactly the requested TechnicalEnd");
                }
                return true;
            }
            catch(Exception error) when(ExpectedFailure(error))
            {reason="Preview restart verification failed: "+error.GetType().Name+": "+error.Message;return false;}
        }

        static ClosedEvidence ReadClosed(string path,string sessionId,string role,string expectedEnd)
        {
            var evidence=new ClosedEvidence();
            // Deny concurrent writes while reading the complete chain and hash.
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                evidence.Journal=JournalPrefixReader.Read(stream,record=>
                {
                    string value;
                    if(record.Sequence==0&&record.Fields.TryGetValue("journal_id",out value))evidence.JournalId=value;
                    if(record.Kind=="engine_event"&&record.Fields.TryGetValue("event_kind",out value)&&value=="session_ended")
                    {
                        evidence.EndEvents++;
                        string end,detail;
                        if(record.Fields.TryGetValue("field:reason",out end)&&end=="TechnicalEnd"&&
                            record.Fields.TryGetValue("field:detail",out detail)&&detail==expectedEnd)evidence.MatchingTechnicalEnd=true;
                    }
                });
                var result=evidence.Journal;
                if(result.Status!=JournalReadStatus.CleanClose||!result.CleanCloseMarkerObserved||result.VerifiedBytes!=stream.Length||
                    result.RecordCount<2||result.Identity==null||!Hex(evidence.JournalId,32))
                    throw new InvalidDataException(role+" journal is not a complete valid closed chain: "+result.Status+" "+result.Error);
                if(result.Identity.SessionId!=sessionId||result.Identity.Role!=role)
                    throw new InvalidDataException(role+" journal belongs to a different session or role");
                stream.Position=0;using(var sha=SHA256.Create())evidence.Digest=Digest(sha.ComputeHash(stream));
            }
            return evidence;
        }

        static void VerifyReceipt(string path,string sessionId,string role,ClosedEvidence evidence)
        {
            byte[] bytes;
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length<1||stream.Length>MaximumReceiptBytes)throw new InvalidDataException(role+" close receipt size is invalid");
                bytes=new byte[(int)stream.Length];int offset=0;
                while(offset<bytes.Length)
                {int count=stream.Read(bytes,offset,bytes.Length-offset);if(count==0)throw new EndOfStreamException("Close receipt changed while reading");offset+=count;}
            }
            if(bytes[bytes.Length-1]!=10)throw new InvalidDataException(role+" close receipt is incomplete");
            for(int i=0;i<bytes.Length-1;i++)if(bytes[i]<33||bytes[i]>126)throw new InvalidDataException(role+" close receipt contains invalid bytes");
            var fields=Encoding.ASCII.GetString(bytes,0,bytes.Length-1).Split('|');
            if(fields.Length!=9||fields[0]!=ReceiptTag||fields[1]!=ReceiptVersion||fields[2]!=Id(sessionId)||fields[3]!=role||
                fields[4]!=Id(evidence.Journal.Identity.ClockId)||fields[5]!=evidence.JournalId||
                fields[6]!=(evidence.Journal.RecordCount-1).ToString(Invariant)||fields[7]!=evidence.Journal.VerifiedBytes.ToString(Invariant)||
                fields[8]!=evidence.Digest)
                throw new InvalidDataException(role+" close receipt does not match the journal identity, bytes and acknowledged flush boundary");
        }

        static bool Hex(string value,int length)
        {if(value==null||value.Length!=length)return false;foreach(char c in value)if(!(c>='0'&&c<='9')&&!(c>='a'&&c<='f'))return false;return true;}
        static string Id(string value){return Convert.ToBase64String(Utf8.GetBytes(value));}
        static string Digest(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
        internal static bool ExpectedFailure(Exception error)
        {return error is IOException||error is InvalidDataException||error is UnauthorizedAccessException||error is System.Security.SecurityException||
            error is NotSupportedException||error is ArgumentException||error is InvalidOperationException||error is OverflowException;}
    }
}
