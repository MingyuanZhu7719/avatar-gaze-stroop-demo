using System;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // VRAW0003 task records only. This is a bounded measurement candidate,
    // not a reservation, write authorization, confirmation or completeness
    // proof. It does not include the first manifest frame or magic bytes.
    internal static class ViveRawTaskFrameMeasurement
    {
        const int FrameOverhead=40,FixedTaskPayloadBytes=57,MaximumPayloadBytes=1048576,MaximumTextBytes=32768;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);

        static bool Text(string value,bool required,long limit,ref long size)
        {
            if(value!=null&&value.Length>MaximumTextBytes)return false;
            if(required&&String.IsNullOrWhiteSpace(value))return false;
            if(value==null)return true; // its four-byte length is already counted
            int bytes=Utf8.GetByteCount(value);
            if(bytes>MaximumTextBytes)return false;
            size=checked(size+bytes);
            return size<=limit;
        }

        internal static bool TryMeasure(long ordinal,JournalEvent item,ViveNamedSessionFrameAdmission.Manifest manifest,
            long maximumFramedBytes,out int framedBytes)
        {
            framedBytes=0;
            if(ordinal<1||item==null||manifest==null||((manifest.Context==null||!manifest.Context.IsLegacy)&&(manifest.Plan==null||manifest.Intent==null))||maximumFramedBytes<FrameOverhead+FixedTaskPayloadBytes)return false;
            try
            {
                if(item.Fields.Count>128)return false;
                // UTF-8 uses at least one byte per UTF-16 code unit. Reject
                // overlong inputs before shared equality/provenance checks or
                // whitespace/UTF-8 scans, including untrusted metadata fields.
                if(TooLong(item.ClockId)||TooLong(item.SessionId)||TooLong(item.TrialId)||TooLong(item.Kind)||
                    TooLong(manifest.ClockId)||TooLong(manifest.Session))return false;
                foreach(var field in item.Fields)if(TooLong(field.Key)||TooLong(field.Value))return false;
                ViveRawSessionJournalCodec.ValidateTaskRecord(item,manifest);
                long limit=Math.Min(maximumFramedBytes,(long)MaximumPayloadBytes+FrameOverhead);
                // Fixed bytes include all four top-level string length words,
                // not per-field length words. No payload or key list is built.
                long size=FrameOverhead+FixedTaskPayloadBytes;
                if(!Text(item.ClockId,false,limit,ref size)||!Text(item.SessionId,false,limit,ref size)||
                    !Text(item.TrialId,false,limit,ref size)||!Text(item.Kind,true,limit,ref size))return false;
                foreach(var field in item.Fields)
                {
                    size=checked(size+8);if(size>limit)return false;
                    if(!Text(field.Key,true,limit,ref size)||!Text(field.Value,false,limit,ref size))return false;
                }
                framedBytes=checked((int)size);return true;
            }
            catch(Exception error) when(error is InvalidOperationException||error is EncoderFallbackException||error is OverflowException)
            {framedBytes=0;return false;}
        }
        static bool TooLong(string value){return value!=null&&value.Length>MaximumTextBytes;}
    }
}
