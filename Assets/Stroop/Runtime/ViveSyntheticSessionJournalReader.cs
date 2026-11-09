using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    internal enum ViveSyntheticSessionReadStatus { StructuralCloseObserved,VerifiedPrefix,RecoveryRequired,Invalid,ReadFailure,LimitExceeded }
    internal sealed class ViveSyntheticSessionReadResult
    {
        internal ViveSyntheticSessionReadStatus Status {get;set;}
        internal ViveSessionFrameStatus FramingStatus {get;set;}
        internal long VerifiedBytes {get;set;}
        internal string Token {get;set;}
        internal bool CloseMarkerObserved {get;set;}
        internal IReadOnlyList<ViveSessionFramedRecord> Records {get;set;}
        internal bool WriterAcknowledgementProven {get{return false;}}
        internal bool ConsentAuthenticated {get{return false;}}
        internal bool SourceAuthenticated {get{return false;}}
        internal bool DataScopeAuthenticated {get{return false;}}
        internal bool ContainsPotentiallySensitiveData {get{return true;}}
        internal bool DurableStorageProven {get{return false;}}
        internal bool CompleteSessionProven {get{return false;}}
    }
    // Strict reader for the current sealed fake-only VSESS004 writer's
    // declared schema. A source can lie about SYNTHETIC_ONLY; that declaration
    // is not authenticated, and records may therefore still be sensitive.
    // This is not a reader for real participant or device data. A close frame
    // proves only observed structure: an in-flight close may land after withdrawal.
    // All callback data is privately staged until the final outcome; I/O,
    // budget or callback failures never yield records. Nonclosed or malformed
    // verified prefixes require a separate explicit recovery decision.
    internal static class ViveSyntheticSessionJournalReader
    {
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static bool Header(ViveSessionFramedRecord record)
        {
            string schema,scope,authority;
            return record.Ordinal==0&&record.Kind=="header"&&record.Fields.Count==3&&
                record.Fields.TryGetValue("schema",out schema)&&schema=="vive-session-journal-v4"&&
                record.Fields.TryGetValue("data_scope",out scope)&&scope=="SYNTHETIC_ONLY"&&
                record.Fields.TryGetValue("identity_authority",out authority)&&authority=="DECLARED_ONLY";
        }
        static bool Ordinary(ViveSessionFramedRecord record)
        {
            if(record.Kind!="task_event"&&record.Kind!="eye_sample"&&record.Kind!="coverage_unknown")return false;
            if(record.Fields.Count<1||record.Fields.Count>16)return false;
            int bytes=0;
            foreach(var item in record.Fields)
            {
                if(String.IsNullOrEmpty(item.Key)||item.Key.Length>64||(item.Value!=null&&item.Value.Length>1024))return false;
                foreach(char c in item.Key)
                    if(!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'))return false;
                bytes=checked(bytes+Utf8.GetByteCount(item.Key)+(item.Value==null?0:Utf8.GetByteCount(item.Value))+8);
                if(bytes>8192)return false;
            }
            return true;
        }
        static bool Close(ViveSessionFramedRecord record,long ordinaryCount)
        {
            string raw;long parsed;
            return record.Kind=="close"&&record.Fields.Count==1&&record.Fields.TryGetValue("record_count",out raw)&&
                Int64.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out parsed)&&parsed>=0&&
                parsed==ordinaryCount&&raw==parsed.ToString(CultureInfo.InvariantCulture);
        }
        static IReadOnlyList<ViveSessionFramedRecord> Empty()
        {return new ReadOnlyCollection<ViveSessionFramedRecord>(new List<ViveSessionFramedRecord>());}
        internal static ViveSyntheticSessionReadResult Read(Stream input,bool explicitReadOnlyAction,bool explicitPrefixRecovery,
            long maximumBytes,int maximumRecords)
        {
            if(!explicitReadOnlyAction)throw new InvalidOperationException("Explicit read-only action required.");
            if(maximumRecords<2||maximumRecords>1000000)throw new ArgumentOutOfRangeException("maximumRecords");
            var staged=new List<ViveSessionFramedRecord>();bool header=false,close=false;long ordinary=0;
            var scan=ViveSessionFrameScanner.Scan(input,true,maximumBytes,maximumRecords,ContinuousJournal.MaximumFrameBytes,4096,record=>{
                if(!header){if(!Header(record))return false;header=true;staged.Add(record);return true;}
                if(close)return false;
                if(record.Kind=="close")
                {if(!Close(record,ordinary))return false;close=true;staged.Add(record);return true;}
                if(!Ordinary(record))return false;
                ordinary=checked(ordinary+1);staged.Add(record);return true;
            });
            // Until a complete structural result or separately authorized
            // prefix is accepted, expose no token or verified byte count.
            var result=new ViveSyntheticSessionReadResult{FramingStatus=scan.Status,VerifiedBytes=0,
                Token=null,CloseMarkerObserved=false,Records=Empty()};
            if(scan.Status==ViveSessionFrameStatus.ReadFailure||scan.Status==ViveSessionFrameStatus.ConsumerFailed)
            {result.Status=ViveSyntheticSessionReadStatus.ReadFailure;return result;}
            if(scan.Status==ViveSessionFrameStatus.BudgetExceeded)
            {result.Status=ViveSyntheticSessionReadStatus.LimitExceeded;return result;}
            if(!header)
            {result.Status=ViveSyntheticSessionReadStatus.Invalid;return result;}
            if(scan.Status==ViveSessionFrameStatus.EndOfInput&&close)
            {
                result.Status=ViveSyntheticSessionReadStatus.StructuralCloseObserved;result.CloseMarkerObserved=true;
                result.Token=scan.Token;result.VerifiedBytes=scan.VerifiedBytes;
                result.Records=new ReadOnlyCollection<ViveSessionFramedRecord>(staged);return result;
            }
            if(!explicitPrefixRecovery)
            {result.Status=ViveSyntheticSessionReadStatus.RecoveryRequired;return result;}
            result.Status=ViveSyntheticSessionReadStatus.VerifiedPrefix;result.CloseMarkerObserved=close;
            result.Token=scan.Token;result.VerifiedBytes=scan.VerifiedBytes;
            result.Records=new ReadOnlyCollection<ViveSessionFramedRecord>(staged);return result;
        }
    }
}
