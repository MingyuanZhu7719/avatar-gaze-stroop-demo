using System;
using System.Collections.Generic;
using System.IO;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentJournalTests
    {
        sealed class PartialStream:MemoryStream
        {
            public int Remaining=Int32.MaxValue;
            public override void Write(byte[] bytes,int offset,int count)
            {
                int written=Math.Min(Remaining,count);base.Write(bytes,offset,written);Remaining-=written;
                if(written<count)throw new IOException("synthetic partial write");
            }
        }
        sealed class ShortReadStream:MemoryStream
        {
            public ShortReadStream(byte[] bytes):base(bytes,false){}
            public override int Read(byte[] bytes,int offset,int count){return base.Read(bytes,offset,Math.Min(3,count));}
        }
        static ViveDevelopmentProvenance Identity(string stream="fixture/participant-1")
        {return new ViveDevelopmentProvenance("session","participant","operator",ViveDevelopmentMeasurementRole.Participant,stream,"device","clock","mapping-v3",-123,10,11,9.8,10.2,true);}
        static ViveDevelopmentExportRecord Observation(long ordinal=0,ViveDevelopmentProvenance identity=null)
        {return ViveDevelopmentExportRecord.Observation(identity??Identity(),ViveDevelopmentDataSource.SyntheticFixture,ordinal,0,0,99,2,new Vec3(0,0,2),true,new Vec3(0,0,1),"tracking-local-rehearsal",true);}
        static ViveDevelopmentExportRecord Gap(long ordinal)
        {return ViveDevelopmentExportRecord.Gap(Identity(),ViveDevelopmentDataSource.SyntheticFixture,ordinal,0,0,10.3,10.4,ViveDevelopmentGapReason.Stale);}
        static ViveDevelopmentJournalReadResult Read(byte[] bytes,Action<ViveDevelopmentJournalRecord> callback=null)
        {using(var stream=new ShortReadStream(bytes))return ViveDevelopmentJournalReader.Read(stream,callback);}
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE development journal requires opt-in and a fresh stream",()=>{
                var memory=new MemoryStream();Check.Throws<InvalidOperationException>(()=>new ViveDevelopmentJournal(memory,()=>{},Identity(),ViveDevelopmentDataSource.SyntheticFixture,false,true));
                Check.Equal(0L,memory.Length);memory.WriteByte(1);
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentJournal(memory,()=>{},Identity(),ViveDevelopmentDataSource.SyntheticFixture,true,true));
                Check.Equal(1L,memory.Length);
            });
            suite.Run("VIVE development journal writes separate raw derived gap and task evidence",()=>{
                var memory=new MemoryStream();var rows=new List<ViveDevelopmentJournalRecord>();int flushes=0;
                using(var journal=new ViveDevelopmentJournal(memory,()=>flushes++,Identity(),ViveDevelopmentDataSource.SyntheticFixture,true,true))
                {
                    Check.Equal(0L,journal.LastWrittenSequence);Check.Equal(0L,journal.LastFlushAcknowledgedSequence);Check.Equal(1,flushes);
                    Check.True(journal.TryAppendObservation(Observation()));
                    Check.True(journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.StimulusPresented,10.25,0,0,"MainTask","trial-1",null));
                    Check.True(journal.TryAppendObservation(Gap(1)));Check.Equal(0L,journal.LastFlushAcknowledgedSequence);
                    Check.True(journal.Flush());Check.Equal(3L,journal.LastFlushAcknowledgedSequence);Check.Equal(memory.Length,journal.LastFlushAcknowledgedOffset);
                    Check.True(journal.Close());Check.True(journal.Close());Check.True(!journal.TryAppendObservation(Observation(2)));
                }
                var result=Read(memory.ToArray(),rows.Add);Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,result.Status);Check.Equal(5L,result.RecordCount);
                Check.True(result.CloseMarkerObserved);Check.Equal(memory.Length,result.VerifiedBytes);Check.Equal("SyntheticFixture",rows[0].Fields["data_source"]);
                Check.Equal("false",rows[0].Fields["hardware_capture_time_verified"]);Check.Equal("false",rows[0].Fields["apparatus_mapping_validated"]);
                Check.True(!rows[0].Fields.ContainsKey("run_kind"));Check.Equal("-123",rows[1].Fields["native_eye_timestamp_opaque"]);
                Check.Equal("2",rows[1].Fields["raw_combined_sdk_direction_z"]);Check.Equal("1",rows[1].Fields["derived_direction_z"]);
                Check.Equal("tracking-local-rehearsal",rows[1].Fields["derived_direction_frame"]);Check.Equal("true",rows[1].Fields["eye_usable"]);
                Check.Equal("task_event",rows[2].Kind);Check.Equal("StimulusPresented",rows[2].Fields["event_kind"]);
                Check.Equal("gap",rows[3].Kind);Check.Equal("Stale",rows[3].Fields["gap_reason"]);Check.Equal(null,rows[3].Fields["eye_observed_local_seconds"]);
            });
            suite.Run("VIVE development journal rejects cross-stream ordinals and invalid task events before writing",()=>{
                var memory=new MemoryStream();using(var journal=new ViveDevelopmentJournal(memory,()=>{},Identity(),ViveDevelopmentDataSource.SyntheticFixture,true,true))
                {
                    long before=memory.Length;Check.Throws<ArgumentException>(()=>journal.TryAppendObservation(Observation(1)));
                    Check.Throws<ArgumentException>(()=>journal.TryAppendObservation(Observation(0,Identity("other-stream"))));
                    Check.Throws<ArgumentOutOfRangeException>(()=>journal.TryAppendTaskEvent((ViveDevelopmentTaskEventKind)99,1,0,0,"MainTask",null,null));
                    Check.Throws<ArgumentOutOfRangeException>(()=>journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.Paused,Double.NaN,0,0,"Paused",null,null));
                    Check.Throws<ArgumentException>(()=>journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.Paused,1,0,0," ",null,null));
                    Check.Equal(before,memory.Length);Check.True(!journal.IsFaulted);Check.True(journal.TryAppendObservation(Observation()));
                    Check.Throws<ArgumentException>(()=>journal.TryAppendObservation(Observation()));
                }
            });
            suite.Run("VIVE development journal partial writes latch failure and preserve verified prefix",()=>{
                var memory=new PartialStream();using(var journal=new ViveDevelopmentJournal(memory,()=>{},Identity(),ViveDevelopmentDataSource.SyntheticFixture,true,true))
                {
                    long acknowledged=journal.LastFlushAcknowledgedOffset;memory.Remaining=10;
                    Check.True(!journal.TryAppendObservation(Observation()));Check.True(journal.IsFaulted);Check.Equal("IOException",journal.Error);
                    Check.Equal(0L,journal.LastWrittenSequence);Check.Equal(0L,journal.LastFlushAcknowledgedSequence);Check.Equal(acknowledged,journal.LastFlushAcknowledgedOffset);
                    long physical=memory.Length;Check.True(!journal.TryAppendObservation(Observation()));Check.True(!journal.Close());Check.Equal(physical,memory.Length);
                    var recovered=Read(memory.ToArray());Check.Equal(ViveDevelopmentJournalReadStatus.Truncated,recovered.Status);
                    Check.Equal(acknowledged,recovered.VerifiedBytes);Check.Equal(1L,recovered.RecordCount);
                }
            });
            suite.Run("VIVE development journal flush failure never advances acknowledgment",()=>{
                var memory=new MemoryStream();int calls=0;
                using(var journal=new ViveDevelopmentJournal(memory,()=>{if(++calls>1)throw new IOException("synthetic flush failure");},Identity(),ViveDevelopmentDataSource.SyntheticFixture,true,true))
                {
                    long acknowledged=journal.LastFlushAcknowledgedOffset;Check.True(journal.TryAppendObservation(Observation()));
                    Check.True(!journal.Close());Check.True(journal.IsFaulted);Check.Equal(0L,journal.LastFlushAcknowledgedSequence);Check.Equal(acknowledged,journal.LastFlushAcknowledgedOffset);
                    Check.Equal(2L,journal.LastWrittenSequence);var result=Read(memory.ToArray());Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,result.Status);
                    // A readable close marker cannot prove that a failed flush was durable.
                }
            });
            suite.Run("VIVE development journal distinguishes real-source label from invalid native evidence",()=>{
                // All bytes here are synthetic even though the test exercises the
                // serialization label that a real explicitly opted-in source uses.
                var memory=new MemoryStream();var rows=new List<ViveDevelopmentJournalRecord>();
                var invalid=ViveDevelopmentExportRecord.Observation(Identity(),ViveDevelopmentDataSource.RealVive,0,0,0,
                    100,0,new Vec3(Double.NaN,0,1),false,null,null,false);
                using(var journal=new ViveDevelopmentJournal(memory,()=>{},Identity(),ViveDevelopmentDataSource.RealVive,true,true))
                {Check.True(journal.TryAppendObservation(invalid));Check.True(journal.Close());}
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(memory.ToArray(),rows.Add).Status);
                Check.Equal("RealVive",rows[0].Fields["data_source"]);Check.Equal("false",rows[1].Fields["eye_usable"]);
                Check.Equal("NaN",rows[1].Fields["raw_combined_sdk_direction_x"]);Check.Equal(null,rows[1].Fields["derived_direction_x"]);
            });
            suite.Run("VIVE development journal reader distinguishes verified prefix truncation and corruption",()=>{
                var memory=new MemoryStream();var journal=new ViveDevelopmentJournal(memory,()=>{},Identity(),ViveDevelopmentDataSource.SyntheticFixture,true,true);
                long header=memory.Length;Check.True(journal.TryAppendObservation(Observation()));long sampleEnd=memory.Length;
                Check.True(journal.TryAppendTaskEvent(ViveDevelopmentTaskEventKind.Paused,10.3,0,0,"Paused",null,"fixture"));
                byte[] unclosed=memory.ToArray();var result=Read(unclosed);Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,result.Status);Check.Equal(3L,result.RecordCount);
                for(int cut=(int)header+1;cut<sampleEnd;cut++)
                {
                    var prefix=new byte[cut];Array.Copy(unclosed,prefix,cut);var truncated=Read(prefix);
                    Check.Equal(ViveDevelopmentJournalReadStatus.Truncated,truncated.Status);Check.Equal(header,truncated.VerifiedBytes);
                }
                var corrupt=(byte[])unclosed.Clone();corrupt[(int)sampleEnd-1]^=1;var invalid=Read(corrupt);
                Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,invalid.Status);Check.Equal(header,invalid.VerifiedBytes);
                journal.Dispose();
            });
            suite.Run("VIVE development journal reader propagates consumer failures",()=>{
                var memory=new MemoryStream();using(var journal=new ViveDevelopmentJournal(memory,()=>{},Identity(),ViveDevelopmentDataSource.SyntheticFixture,true,true)){Check.True(journal.Close());}
                Check.Throws<IOException>(()=>Read(memory.ToArray(),record=>{throw new IOException("consumer failure");}));
            });
        }
    }
}
