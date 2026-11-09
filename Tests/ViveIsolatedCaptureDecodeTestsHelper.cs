using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    // Decoder for complete synthetic in-memory frames only. It does not verify
    // persistence, consent, device data, or authenticate a capture.
    internal static class ViveIsolatedCaptureDecodeHelper
    {
        internal sealed class DecodedFrame
        {
            internal long Ordinal { get; private set; }
            internal long Revision { get; private set; }
            internal string Operation { get; private set; }
            internal IReadOnlyList<JournalEvent> Events { get; private set; }

            internal DecodedFrame(long ordinal, long revision, string operation, IList<JournalEvent> events)
            {
                Ordinal=ordinal; Revision=revision; Operation=operation;
                Events=new ReadOnlyCollection<JournalEvent>(new List<JournalEvent>(events));
            }
        }

        static readonly byte[] Magic=Encoding.ASCII.GetBytes("SYNTHETIC-COMMIT-V1");
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);

        internal static IReadOnlyList<DecodedFrame> Decode(byte[] bytes)
        {
            if(bytes==null)throw new ArgumentNullException("bytes");
            var frames=new List<DecodedFrame>();
            int offset=0;
            while(offset<bytes.Length)
            {
                if(bytes.Length-offset<44)throw Invalid("truncated frame header");
                for(int i=0;i<Magic.Length;i++)
                    if(bytes[offset+i]!=Magic[i])throw Invalid("frame magic");
                long ordinal=BitConverter.ToInt64(bytes,offset+24);
                long revision=BitConverter.ToInt64(bytes,offset+32);
                int payloadLength=BitConverter.ToInt32(bytes,offset+40);
                if(ordinal!=frames.Count||revision!=ordinal||payloadLength<1)
                    throw Invalid("frame sequence or payload length");
                int overhead=ordinal==0?177:169;
                long frameLength=(long)overhead+payloadLength;
                if(frameLength>262144||frameLength>bytes.Length-offset)
                    throw Invalid("truncated or oversized frame");
                using(var stream=new MemoryStream(bytes,offset+overhead,payloadLength,false))
                using(var reader=new BinaryReader(stream,Utf8))
                {
                    if(Text(reader)!="ISOLATED_SIMULATION_V1"||reader.ReadInt64()!=revision)
                        throw Invalid("payload identity");
                    string operation=Text(reader);
                    int steps=Count(reader,1024);
                    if(ordinal==0)
                    {
                        if(operation!="BASELINE"||steps!=1)throw Invalid("baseline");
                        SkipSnapshot(reader);
                    }
                    else
                    {
                        if(String.IsNullOrEmpty(operation)||steps<1)throw Invalid("commit");
                        for(int i=0;i<steps;i++)
                        {
                            if(reader.ReadInt32()!=i)throw Invalid("step order");
                            SkipSnapshot(reader);SkipSnapshot(reader);
                        }
                    }
                    int count=Count(reader,1024);
                    var events=new List<JournalEvent>(count);
                    for(int i=0;i<count;i++)events.Add(ReadEvent(reader));
                    if(stream.Position!=stream.Length)throw Invalid("trailing payload");
                    frames.Add(new DecodedFrame(ordinal,revision,operation,events));
                }
                offset=checked(offset+(int)frameLength);
            }
            return new ReadOnlyCollection<DecodedFrame>(frames);
        }

        static void SkipSnapshot(BinaryReader reader)
        {
            int count=Count(reader,32768);
            for(int i=0;i<count;i++)
            {
                switch(reader.ReadByte())
                {
                    case 0: case 2: case 3: break;
                    case 1: Text(reader);break;
                    case 4: reader.ReadInt32();break;
                    case 5: case 6: case 7: reader.ReadInt64();break;
                    case 8: Text(reader);reader.ReadInt64();break;
                    default: throw Invalid("snapshot value type");
                }
            }
        }

        static JournalEvent ReadEvent(BinaryReader reader)
        {
            long sequence=reader.ReadInt64();
            double time=BitConverter.Int64BitsToDouble(reader.ReadInt64());
            string clock=Text(reader),session=Text(reader);
            int block=reader.ReadInt32(),segment=reader.ReadInt32();
            string trial=Text(reader),kind=Text(reader);
            int count=Count(reader,64);
            var fields=new Dictionary<string,string>(StringComparer.Ordinal);
            for(int i=0;i<count;i++)
            {
                string key=Text(reader),value=Text(reader);
                if(String.IsNullOrWhiteSpace(key)||fields.ContainsKey(key))throw Invalid("event field key");
                fields.Add(key,value);
            }
            return new JournalEvent(sequence,time,clock,session,block,segment,trial,kind,fields);
        }

        static int Count(BinaryReader reader,int maximum)
        {
            int count=reader.ReadInt32();
            if(count<0||count>maximum)throw Invalid("count");
            return count;
        }
        static string Text(BinaryReader reader)
        {
            int count=reader.ReadInt32();
            if(count==-1)return null;
            if(count<0||count>4096||count>reader.BaseStream.Length-reader.BaseStream.Position)throw Invalid("text length");
            byte[] bytes=reader.ReadBytes(count);
            if(bytes.Length!=count)throw Invalid("truncated text");
            return Utf8.GetString(bytes);
        }
        static InvalidDataException Invalid(string reason){return new InvalidDataException("Invalid synthetic frame: "+reason);}
    }
}
