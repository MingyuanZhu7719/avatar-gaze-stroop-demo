using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentJournalExtractionTests
    {
        sealed class ForwardOnlyStream : Stream
        {
            readonly MemoryStream input;
            public ForwardOnlyStream(byte[] bytes){input=new MemoryStream(bytes,false);}
            public override bool CanRead {get{return true;}}
            public override bool CanSeek {get{return false;}}
            public override bool CanWrite {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}
            public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] buffer,int offset,int count){return input.Read(buffer,offset,Math.Min(3,count));}
            public override void Flush(){throw new NotSupportedException();}
            public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long value){throw new NotSupportedException();}
            public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
        }
        sealed class FailingReadStream : MemoryStream
        {
            readonly long failAfter;
            public FailingReadStream(byte[] bytes,long failAfter):base(bytes,false){this.failAfter=failAfter;}
            public override int Read(byte[] bytes,int offset,int count)
            {
                if(Position>=failAfter)throw new IOException("synthetic input failure");
                return base.Read(bytes,offset,(int)Math.Min(count,failAfter-Position));
            }
        }
        static ViveDevelopmentProvenance Identity(string participant="participant")
        {return new ViveDevelopmentProvenance("session",participant,"operator",ViveDevelopmentMeasurementRole.Participant,"participant-stream","device","clock","mapping-v3",-7,10,11,9.9,10.2,true);}
        static ViveDevelopmentExportRecord Sample(ViveDevelopmentProvenance identity,long ordinal)
        {return ViveDevelopmentExportRecord.Observation(identity,ViveDevelopmentDataSource.SyntheticFixture,ordinal,1,2,-42,2,new Vec3(0,0,2),true,new Vec3(0,0,1),"tracking-local",true);}
        static byte[] Journal(bool close,string participant="participant")
        {
            var identity=Identity(participant);var memory=new MemoryStream();
            var journal=new ViveDevelopmentJournal(memory,()=>{},identity,ViveDevelopmentDataSource.SyntheticFixture,true,true);
            Check.True(journal.TryAppendObservation(Sample(identity,0)));
            Check.True(journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.StimulusPresented,10.3,1,2,"MainTask","trial-1","fixture, \"shown\""));
            Check.True(journal.TryAppendObservation(ViveDevelopmentExportRecord.Gap(identity,ViveDevelopmentDataSource.SyntheticFixture,1,1,2,10.3,10.4,ViveDevelopmentGapReason.Stale)));
            if(close)Check.True(journal.Close());return memory.ToArray();
        }
        static ViveDevelopmentJournalExtractionResult Extract(byte[] bytes,bool recovery=false,int rows=100,int limit=100000)
        {using(var input=new MemoryStream(bytes,false))return ViveDevelopmentJournalExtraction.Extract(input,recovery,rows,limit);}
        static List<string> CsvLine(string line)
        {
            var fields=new List<string>();var field=new StringBuilder();bool quote=false;
            for(int i=0;i<line.Length;i++)
            {char c=line[i];if(c=='"'){if(quote&&i+1<line.Length&&line[i+1]=='"'){field.Append('"');i++;}else quote=!quote;}else if(c==','&&!quote){fields.Add(field.ToString());field.Clear();}else field.Append(c);}
            if(quote)throw new InvalidDataException("Fixture CSV quote mismatch");fields.Add(field.ToString());return fields;
        }
        static Dictionary<string,string> Row(string header,string line)
        {var names=CsvLine(header);var values=CsvLine(line);Check.Equal(names.Count,values.Count);var row=new Dictionary<string,string>();for(int i=0;i<names.Count;i++)row.Add(names[i],values[i]);return row;}
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE extraction preserves clean evidence identity raw derived time and gaps",()=>{
                var bytes=Journal(true);var result=Extract(bytes);Check.True(result.SourceCleanClose);Check.Equal(3L,result.EvidenceRows);Check.Equal((long)bytes.Length,result.VerifiedBytes);
                var lines=result.Csv.TrimEnd('\n').Split('\n');Check.Equal(4,lines.Length);
                var sample=Row(lines[0],lines[1]);Check.Equal("CleanClose",sample["source_status"]);Check.Equal("SyntheticFixture",sample["data_source"]);
                var token=new byte[16];Array.Copy(bytes,24,token,0,16);Check.Equal(new Guid(token).ToString("N"),result.SourceJournalId);Check.Equal(result.SourceJournalId,sample["source_journal_id"]);
                Check.Equal("-7",sample["native_eye_timestamp_opaque"]);Check.Equal("-42",sample["native_frame_sequence"]);
                Check.Equal("2",sample["raw_combined_sdk_direction_z"]);Check.Equal("1",sample["derived_direction_z"]);
                Check.Equal("tracking-local",sample["derived_direction_frame"]);Check.Equal("false",sample["hardware_capture_time_verified"]);
                var task=Row(lines[0],lines[2]);Check.Equal("StimulusPresented",task["event_kind"]);Check.Equal("fixture, \"shown\"",task["detail"]);
                var gap=Row(lines[0],lines[3]);Check.Equal("gap",gap["source_record_kind"]);Check.Equal("Stale",gap["gap_reason"]);Check.Equal("",gap["native_eye_timestamp_opaque"]);
            });
            suite.Run("VIVE extraction requires explicit incomplete recovery and retains status",()=>{
                var bytes=Journal(false);Check.Throws<InvalidDataException>(()=>Extract(bytes));
                var recovered=Extract(bytes,true);Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,recovered.SourceStatus);Check.True(!recovered.SourceCleanClose);
                Check.Equal("UncleanEnd",Row(recovered.Csv.TrimEnd('\n').Split('\n')[0],recovered.Csv.TrimEnd('\n').Split('\n')[1])["source_status"]);
                var truncated=new byte[bytes.Length-1];Array.Copy(bytes,truncated,truncated.Length);
                Check.Throws<InvalidDataException>(()=>Extract(truncated));var prefix=Extract(truncated,true);
                Check.Equal(ViveDevelopmentJournalReadStatus.Truncated,prefix.SourceStatus);Check.Equal(2L,prefix.EvidenceRows);
            });
            suite.Run("VIVE extraction rejects corruption by default and explicitly labels recovered prefix",()=>{
                var bytes=Journal(false);bytes[bytes.Length-1]^=1;Check.Throws<InvalidDataException>(()=>Extract(bytes));
                var result=Extract(bytes,true);Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,result.SourceStatus);Check.True(!result.SourceCleanClose);Check.Equal(2L,result.EvidenceRows);
                var lines=result.Csv.TrimEnd('\n').Split('\n');Check.Equal("Invalid",Row(lines[0],lines[1])["source_status"]);
            });
            suite.Run("VIVE extraction refuses unsafe text and bounded input or output",()=>{
                Check.Throws<InvalidDataException>(()=>Extract(Journal(true,"=unsafe")));
                Check.Throws<InvalidDataException>(()=>Extract(Journal(true),false,2));
                Check.Throws<InvalidDataException>(()=>Extract(Journal(true),false,100,10));
                Check.Throws<ArgumentOutOfRangeException>(()=>Extract(Journal(true),false,0));
                Check.Throws<ArgumentOutOfRangeException>(()=>Extract(Journal(true),false,1,0));
            });
            suite.Run("VIVE extraction rejects invalid or missing verified headers even in recovery",()=>{
                Check.Throws<InvalidDataException>(()=>Extract(new byte[0],true));
                var bytes=Journal(true);bytes[0]^=1;Check.Throws<InvalidDataException>(()=>Extract(bytes,true));
            });
            suite.Run("VIVE extraction does not emit partial output after an input read failure",()=>{
                var bytes=Journal(true);using(var stream=new FailingReadStream(bytes,bytes.Length-1))
                    Check.Throws<IOException>(()=>ViveDevelopmentJournalExtraction.Extract(stream,true,100,100000));
            });
            suite.Run("VIVE extraction never emits metadata-free CSV for an empty verified prefix",()=>{
                var memory=new MemoryStream();var journal=new ViveDevelopmentJournal(memory,()=>{},Identity(),ViveDevelopmentDataSource.SyntheticFixture,true,true);
                var headerOnly=memory.ToArray();
                Check.Throws<InvalidDataException>(()=>Extract(headerOnly,true));
                var truncated=new byte[headerOnly.Length+1];Array.Copy(headerOnly,truncated,headerOnly.Length);truncated[truncated.Length-1]=1;
                Check.Throws<InvalidDataException>(()=>Extract(truncated,true));
                var invalid=new byte[headerOnly.Length+8];Array.Copy(headerOnly,invalid,headerOnly.Length);
                Check.Throws<InvalidDataException>(()=>Extract(invalid,true));
                Check.True(journal.Close());Check.Throws<InvalidDataException>(()=>Extract(memory.ToArray(),true));
            });
            suite.Run("VIVE extraction distinguishes journal tokens and reads forward-only sources",()=>{
                var first=Journal(true);var second=Journal(true);var fromSeekable=Extract(first);ViveDevelopmentJournalExtractionResult fromForward;
                using(var forward=new ForwardOnlyStream(first))fromForward=ViveDevelopmentJournalExtraction.Extract(forward,false,100,100000);
                Check.Equal(fromSeekable.SourceJournalId,fromForward.SourceJournalId);Check.Equal(fromSeekable.Csv,fromForward.Csv);
                Check.True(fromSeekable.SourceJournalId!=Extract(second).SourceJournalId);
                Check.Equal(fromForward.SourceJournalId,Row(fromForward.Csv.TrimEnd('\n').Split('\n')[0],fromForward.Csv.TrimEnd('\n').Split('\n')[1])["source_journal_id"]);
            });
        }
    }
}
