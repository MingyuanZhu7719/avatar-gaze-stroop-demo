using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class JournalTests
    {
        static JournalIdentity Identity(string role="participant",string session="journal-test")
        {return new JournalIdentity(session,role,"test-monotonic",new ExportProvenance("journal-fixture","software stimulus fixture","software response fixture","fixture-digest"));}
        static Dictionary<string,string> Fields(string value="value")
        {return new Dictionary<string,string>{{"value",value}};}
        static string Temp()
        {string path=Path.Combine(Path.GetTempPath(),"stroop-journal-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
        static byte[] CleanBytes(string role="participant",string session="journal-test")
        {
            var memory=new MemoryStream();using(var writer=new ContinuousJournal(memory,()=>{},Identity(role,session),true))
            {Check.True(writer.TryAppend("first",Fields("one")));Check.True(writer.TryAppend("second",Fields("two")));Check.True(writer.Close());}
            return memory.ToArray();
        }
        static JournalReadResult Read(byte[] bytes,Action<JournalRecord> callback=null)
        {using(var stream=new MemoryStream(bytes,false))return JournalPrefixReader.Read(stream,callback);}
        static List<byte[]> Frames(byte[] bytes)
        {
            var frames=new List<byte[]>();int offset=8;
            while(offset<bytes.Length){int length=BitConverter.ToInt32(bytes,offset)+40;frames.Add(bytes.Skip(offset).Take(length).ToArray());offset+=length;}
            return frames;
        }
        static byte[] Join(params byte[][] pieces)
        {using(var memory=new MemoryStream()){foreach(var piece in pieces)memory.Write(piece,0,piece.Length);return memory.ToArray();}}
        static byte[] Rehash(byte[] frame,byte[] previous)
        {
            byte[] copy=(byte[])frame.Clone(),payload=copy.Skip(8).Take(copy.Length-40).ToArray();
            using(var sha=SHA256.Create())
            {byte[] hash=sha.ComputeHash(Join(previous,payload));Buffer.BlockCopy(hash,0,copy,copy.Length-32,32);}
            return copy;
        }
        static byte[] Hash(byte[] frame){return frame.Skip(frame.Length-32).ToArray();}
        sealed class PartialWriteStream:MemoryStream
        {
            public int? Remaining;
            public override void Write(byte[] buffer,int offset,int count)
            {
                if(!Remaining.HasValue){base.Write(buffer,offset,count);return;}
                int take=Math.Min(count,Remaining.Value);base.Write(buffer,offset,take);Remaining-=take;
                if(take<count)throw new IOException("injected partial write");
            }
        }
        sealed class ShortReadStream:MemoryStream
        {
            public ShortReadStream(byte[] bytes):base(bytes,false){}
            public override int Read(byte[] buffer,int offset,int count){return base.Read(buffer,offset,Math.Min(3,count));}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("M4 journal preserves Unicode multiline nullable text and exact values",()=>{
                var memory=new MemoryStream();var rows=new List<JournalRecord>();int flushes=0;
                using(var writer=new ContinuousJournal(memory,()=>flushes++,Identity("operator","session\n中文"),true))
                {
                    Check.Equal(0L,writer.LastWrittenSequence);Check.Equal(0L,writer.LastFlushedSequence);Check.Equal(1,flushes);
                    var values=new Dictionary<string,string>{{"unicode","中文 👁"},{"multiline","a\r\nb\n\"c\",\t"},{"nullable",null},{"empty",""},{"number","0.12345678901234567"}};
                    Check.True(writer.TryAppend("operator_acquired",values));values["unicode"]="mutated";
                    Check.Equal(1L,writer.LastWrittenSequence);Check.Equal(0L,writer.LastFlushedSequence);
                    Check.True(writer.Flush());Check.Equal(1L,writer.LastFlushedSequence);Check.Equal(memory.Length,writer.LastFlushedOffset);
                    Check.True(writer.Close());Check.True(writer.Close());Check.True(!writer.TryAppend("after",Fields()));
                }
                using(var stream=new ShortReadStream(memory.ToArray()))
                {
                    var result=JournalPrefixReader.Read(stream,rows.Add);Check.Equal(JournalReadStatus.CleanClose,result.Status);Check.Equal(3L,result.RecordCount);
                    Check.True(result.CleanCloseMarkerObserved);Check.Equal(memory.Length,result.VerifiedBytes);Check.Equal("operator",result.Identity.Role);
                    Check.Equal("session\n中文",result.Identity.SessionId);Check.Equal("SIMULATION",result.Identity.DataLabel);
                    Check.Equal("fixture-digest",result.Identity.Provenance.BuildSourceDigest);
                }
                var record=rows[1];Check.Equal(1L,record.Sequence);Check.Equal("operator_acquired",record.Kind);
                Check.Equal("中文 👁",record.Fields["unicode"]);Check.Equal("a\r\nb\n\"c\",\t",record.Fields["multiline"]);
                Check.Equal(null,record.Fields["nullable"]);Check.Equal("",record.Fields["empty"]);Check.Equal("0.12345678901234567",record.Fields["number"]);
            });
            suite.Run("M4 append and close bytes never stand in for successful flush acknowledgment",()=>{
                var memory=new MemoryStream();bool fail=false;
                using(var writer=new ContinuousJournal(memory,()=>{if(fail)throw new IOException("injected flush failure");},Identity(),true))
                {
                    long acknowledged=writer.LastFlushedOffset;Check.True(writer.TryAppend("response",Fields()));fail=true;
                    Check.True(!writer.Flush());Check.True(writer.IsFaulted);Check.Equal(0L,writer.LastFlushedSequence);Check.Equal(acknowledged,writer.LastFlushedOffset);
                    Check.Equal(1L,writer.LastWrittenSequence);Check.True(!writer.TryAppend("lost",Fields()));Check.True(!writer.Close());
                    var prefix=Read(memory.ToArray());Check.Equal(JournalReadStatus.UncleanEnd,prefix.Status);Check.Equal(2L,prefix.RecordCount);
                }
                var closing=new MemoryStream();int calls=0;
                using(var writer=new ContinuousJournal(closing,()=>{if(++calls>1)throw new IOException("close flush failure");},Identity(),true))
                {
                    Check.True(!writer.Close());Check.True(writer.IsFaulted);Check.Equal(0L,writer.LastFlushedSequence);Check.Equal(1L,writer.LastWrittenSequence);
                    var prefix=Read(closing.ToArray());Check.True(prefix.CleanCloseMarkerObserved);Check.Equal(JournalReadStatus.CleanClose,prefix.Status);
                    // The offline observation does not know that the runtime's durable flush failed.
                }
            });
            suite.Run("M4 partial write latches failure and retains only verified prefix",()=>{
                var memory=new PartialWriteStream();using(var writer=new ContinuousJournal(memory,()=>{},Identity(),true))
                {
                    long before=writer.LastFlushedOffset;memory.Remaining=12;
                    Check.True(!writer.TryAppend("partial",Fields()));Check.True(writer.IsFaulted);Check.True(writer.Error.Contains("partial write"));
                    Check.Equal(0L,writer.LastWrittenSequence);Check.Equal(before,writer.LastFlushedOffset);
                    long physical=memory.Length;Check.True(!writer.TryAppend("later",Fields()));Check.Equal(physical,memory.Length);
                    var result=Read(memory.ToArray());Check.Equal(JournalReadStatus.Truncated,result.Status);Check.Equal(before,result.VerifiedBytes);Check.Equal(1L,result.RecordCount);
                }
                var initial=new PartialWriteStream{Remaining=3};using(var writer=new ContinuousJournal(initial,()=>{},Identity(),true))
                {Check.True(writer.IsFaulted);Check.Equal(-1L,writer.LastWrittenSequence);Check.Equal(-1L,writer.LastFlushedSequence);Check.Equal(0L,writer.LastFlushedOffset);}
                Check.Equal(0L,Read(initial.ToArray()).VerifiedBytes);
            });
            suite.Run("M4 complete frame prefixes recover without disposal or a close marker",()=>{
                var memory=new MemoryStream();var writer=new ContinuousJournal(memory,()=>{},Identity(),true);
                Check.True(writer.TryAppend("first",Fields()));Check.True(writer.Flush());byte[] abrupt=memory.ToArray();
                var result=Read(abrupt);Check.Equal(JournalReadStatus.UncleanEnd,result.Status);Check.True(!result.CleanCloseMarkerObserved);
                Check.Equal(2L,result.RecordCount);Check.Equal((long)abrupt.Length,result.VerifiedBytes);writer.Dispose();
                Check.Equal(JournalReadStatus.Truncated,Read(Encoding.ASCII.GetBytes("ASJNL001")).Status);
            });
            suite.Run("M4 every truncation within a final frame preserves the earlier complete records",()=>{
                byte[] bytes=CleanBytes();var frames=Frames(bytes);int boundary=bytes.Length-frames.Last().Length;
                for(int length=boundary+1;length<bytes.Length;length++)
                {var result=Read(bytes.Take(length).ToArray());Check.Equal(JournalReadStatus.Truncated,result.Status);Check.Equal((long)boundary,result.VerifiedBytes);Check.Equal(3L,result.RecordCount);Check.True(!result.CleanCloseMarkerObserved);}
                byte[] corrupt=(byte[])bytes.Clone();corrupt[boundary+10]^=1;
                var invalid=Read(corrupt);Check.Equal(JournalReadStatus.Invalid,invalid.Status);Check.Equal((long)boundary,invalid.VerifiedBytes);
                var trailing=Read(Join(bytes,new byte[]{1}));Check.Equal(JournalReadStatus.Invalid,trailing.Status);Check.True(trailing.CleanCloseMarkerObserved);
            });
            suite.Run("M4 checksum sequence identity reorder and duplicate violations stop at first bad frame",()=>{
                byte[] bytes=CleanBytes();var frames=Frames(bytes);byte[] magic=bytes.Take(8).ToArray();
                foreach(byte[] candidate in new[]{Join(magic,frames[0],frames[2],frames[1]),Join(magic,frames[0],frames[1],frames[1])})
                    Check.Equal(JournalReadStatus.Invalid,Read(candidate).Status);
                // Recompute a valid chain with the wrong sequence: checksum alone is insufficient.
                byte[] duplicate=Rehash(frames[1],Hash(frames[1]));var repeated=Read(Join(magic,frames[0],frames[1],duplicate));
                Check.Equal(JournalReadStatus.Invalid,repeated.Status);Check.Equal(2L,repeated.RecordCount);Check.True(repeated.Error.Contains("sequence"));
                // A frame from another session is invalid even after its chain has been recomputed.
                var other=Frames(CleanBytes("operator","other-session"));byte[] mixed=Rehash(other[1],Hash(frames[0]));
                var identity=Read(Join(magic,frames[0],mixed));Check.Equal(JournalReadStatus.Invalid,identity.Status);Check.Equal(1L,identity.RecordCount);Check.True(identity.Error.Contains("identity"));
                byte[] head=(byte[])frames[0].Clone();head[12]^=1;var brokenHeader=Read(Join(magic,head));
                Check.Equal(0L,brokenHeader.VerifiedBytes);Check.Equal(null,brokenHeader.Identity);
            });
            suite.Run("M4 corrupted interior frame length is rejected before reading an invented tail",()=>{
                byte[] bytes=CleanBytes();var frames=Frames(bytes);int boundary=8+frames[0].Length;
                byte[] damaged=(byte[])bytes.Clone();byte[] inflated=BitConverter.GetBytes(ContinuousJournal.MaximumFrameBytes-1);
                Buffer.BlockCopy(inflated,0,damaged,boundary,4);
                var result=Read(damaged);Check.Equal(JournalReadStatus.Invalid,result.Status);Check.Equal((long)boundary,result.VerifiedBytes);
                Check.Equal(1L,result.RecordCount);Check.True(result.Error.Contains("complement"));
            });
            suite.Run("M4 frame bounds reserved kinds and nonempty stream protect append contract",()=>{
                var memory=new MemoryStream();using(var writer=new ContinuousJournal(memory,()=>{},Identity(),true))
                {
                    long length=memory.Length;Check.Throws<ArgumentException>(()=>writer.TryAppend("__close",Fields()));
                    Check.Throws<ArgumentException>(()=>writer.TryAppend("large",Fields(new string('x',ContinuousJournal.MaximumFrameBytes))));
                    Check.Equal(length,memory.Length);Check.Equal(0L,writer.LastWrittenSequence);Check.True(!writer.IsFaulted);
                }
                Check.Throws<ArgumentException>(()=>new ContinuousJournal(memory,()=>{},Identity(),true));
                Check.Throws<ArgumentException>(()=>Identity("owner"));
                byte[] bytes=CleanBytes(),oversized=bytes.Take(8).Concat(new byte[]{255,255,255,127,0,0,0,128}).ToArray();
                var result=Read(oversized);Check.Equal(JournalReadStatus.Invalid,result.Status);Check.Equal(0L,result.VerifiedBytes);
            });
            suite.Run("M4 file creation and prefix recovery never overwrite existing paths",()=>{
                string root=Temp();try
                {
                    string source=Path.Combine(root,"source.journal"),destination=Path.Combine(root,"recovered.journal");
                    byte[] complete=CleanBytes();var frames=Frames(complete);long boundary=complete.Length-frames.Last().Length;
                    File.WriteAllBytes(source,complete.Take((int)boundary+9).ToArray());
                    using(var collision=ContinuousJournal.CreateNew(source,Identity()))
                    {Check.True(collision.IsFaulted);Check.Equal(-1L,collision.LastWrittenSequence);Check.Equal(-1L,collision.LastFlushedSequence);}
                    Check.Equal((int)boundary+9,File.ReadAllBytes(source).Length);
                    var result=JournalPrefixReader.RecoverVerifiedPrefix(source,destination);Check.Equal(JournalReadStatus.Truncated,result.Status);
                    Check.True(complete.Take((int)boundary).SequenceEqual(File.ReadAllBytes(destination)));
                    Check.Equal(JournalReadStatus.UncleanEnd,Read(File.ReadAllBytes(destination)).Status);
                    Check.Throws<IOException>(()=>JournalPrefixReader.RecoverVerifiedPrefix(source,destination));
                    Check.Throws<IOException>(()=>JournalPrefixReader.RecoverVerifiedPrefix(source,source));
                    string fresh=Path.Combine(root,"fresh.journal");using(var writer=ContinuousJournal.CreateNew(fresh,Identity()))
                    {Check.True(!writer.IsFaulted,writer.Error);Check.True(writer.TryAppend("observation",Fields()));Check.True(writer.Close());}
                    Check.Equal(JournalReadStatus.CleanClose,Read(File.ReadAllBytes(fresh)).Status);
                    using(var blocked=ContinuousJournal.CreateNew(Path.Combine(source,"child.journal"),Identity()))Check.True(blocked.IsFaulted);
                }finally{Directory.Delete(root,true);}
            });
            suite.Run("M4 reader propagates consumer failures instead of mislabeling stored bytes",()=>{
                Check.Throws<IOException>(()=>Read(CleanBytes(),record=>{throw new IOException("consumer failed");}));
            });
        }
    }
}
