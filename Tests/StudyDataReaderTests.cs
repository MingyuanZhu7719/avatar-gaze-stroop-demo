#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class StudyDataReaderTests
    {
        static byte[] Csv(byte[] bytes)
        {
            using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
            {
                Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,
                    ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,4194304,5000,33554432,CancellationToken.None));
                return output.ToArray();
            }
        }
        static StudyDataReadSummary Read(byte[] bytes,string format,List<StudyDataRecord> records=null,long maximumBytes=33554432,int maximumRecords=5000)
        {using(var input=new MemoryStream(bytes,false))return StudyDataReader.Read(input,format,record=>{if(records!=null)records.Add(record);},CancellationToken.None,maximumBytes,maximumRecords);}
        static string EncodedRow(string kind,string field,string value,long ordinal=0)
        {
            string raw=ordinal.ToString(CultureInfo.InvariantCulture)+","+kind+","+field+",utf8_b64,"+Convert.ToBase64String(Encoding.UTF8.GetBytes(value))+"\n";
            return (string)typeof(ViveRawReadableAnalysisCsv).GetMethod("ConvertFrame",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{raw});
        }
        static string Header()
        {return (string)typeof(ViveRawReadableAnalysisCsv).GetMethod("Header",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null);}
        static string Meta()
        {return (string)typeof(ViveRawReadableAnalysisCsv).GetMethod("Meta",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{"scope","VERIFIED_FRAMES_ONLY"});}
        public static void Register(TestSuite suite)
        {
            suite.Run("study reader matches original VRAW and readable CSV records exactly",()=>{
                byte[] original=ViveRawTrialProjectionTests.FourBlocks();byte[] snapshot=(byte[])original.Clone();
                var rawRecords=new List<StudyDataRecord>();var csvRecords=new List<StudyDataRecord>();
                var raw=Read(original,"vraw",rawRecords);var csv=Read(Csv(original),"csv",csvRecords);
                Check.Equal("Complete",raw.Status);Check.Equal("Complete",csv.Status);
                Check.True(raw.ChainVerified&&!csv.ChainVerified&&raw.SourceTerminalObserved&&csv.SourceTerminalObserved);
                Check.Equal(raw.VerifiedChainSha256,csv.VerifiedChainSha256);Check.Equal(raw.RecordCount,csv.RecordCount);
                Check.Equal(rawRecords.Count,csvRecords.Count-1);
                for(int i=0;i<rawRecords.Count;i++)
                {
                    Check.Equal(rawRecords[i].Ordinal,csvRecords[i].Ordinal);Check.Equal(rawRecords[i].Kind,csvRecords[i].Kind);
                    Check.Equal(rawRecords[i].Fields.Count,csvRecords[i].Fields.Count);
                    foreach(var field in rawRecords[i].Fields)Check.Equal(field.Value,csvRecords[i].Fields[field.Key],field.Key);
                }
                for(int i=0;i<original.Length;i++)Check.Equal(snapshot[i],original[i]);
                Check.True(!ViveRawReadableAnalysisCsv.ProductionGateOpen,"Offline read must not enable recording/export production gates.");
            });
            suite.Run("study reader rejects corrupt VRAW and distinguishes a verified incomplete prefix",()=>{
                byte[] corrupt=ViveRawTrialProjectionTests.FourBlocks();corrupt[corrupt.Length-1]^=1;
                Check.Equal("Rejected",Read(corrupt,"vraw").Status);
                var truncated=Read(ViveRawTrialProjectionTests.FourBlocks(truncateTerminal:true),"vraw");
                Check.Equal("Incomplete",truncated.Status);Check.True(truncated.ChainVerified&&!truncated.SourceTerminalObserved);
                Check.Equal("Rejected",Read(ViveRawSessionTerminalTests.Fixture(reason:"Withdrawn"),"vraw").Status);
            });
            suite.Run("study CSV decodes original text and RFC4180 quoting without formula escape leakage",()=>{
                string value="=SUM(1,2)\r\n\"quoted\", trailing";
                string text=Header()+Meta()+EncodedRow("manifest","source",value);
                var records=new List<StudyDataRecord>();var result=Read(Encoding.UTF8.GetBytes(text),"csv",records);
                Check.Equal("Incomplete",result.Status);Check.Equal(value,records[0].Fields["source"]);
                Check.Equal(1L,result.RecordCount);
            });
            suite.Run("study CSV rejects unrelated headers malformed quoting and mixed duplicate ordinals",()=>{
                Check.Equal("Rejected",Read(Encoding.UTF8.GetBytes("target,time,x,y\nA,1,2,3\n"),"csv").Status);
                string prefix=Header()+Meta()+EncodedRow("manifest","source","synthetic");
                Check.Equal("Rejected",Read(Encoding.UTF8.GetBytes(prefix+EncodedRow("task","event_kind","trial_planned",0)),"csv").Status);
                Check.Equal("Rejected",Read(Encoding.UTF8.GetBytes(prefix+EncodedRow("manifest","source","again",0)),"csv").Status);
                Check.Equal("Rejected",Read(Encoding.UTF8.GetBytes(prefix+EncodedRow("task","event_kind","trial_planned",2)),"csv").Status);
                Check.Equal("Rejected",Read(Encoding.UTF8.GetBytes(prefix+"\"unterminated"),"csv").Status);
                Check.True(Read(Encoding.UTF8.GetBytes(prefix+"\"unterminated"),"csv").TrailingFrameTruncated);
                Check.Equal("Rejected",Read(Encoding.UTF8.GetBytes(prefix+"\"closed\"invalid"),"csv").Status);
            });
            suite.Run("study reader enforces input and record budgets and cancellation",()=>{
                byte[] bytes=ViveRawTrialProjectionTests.FourBlocks();byte[] csv=Csv(bytes);
                Check.Equal("LimitExceeded",Read(bytes,"vraw",null,bytes.Length-1).Status);
                Check.Equal("LimitExceeded",Read(csv,"csv",null,csv.Length-1).Status);
                Check.Equal("LimitExceeded",Read(bytes,"vraw",null,4194304,1).Status);
                Check.Equal("LimitExceeded",Read(csv,"csv",null,4194304,1).Status);
                using(var cancel=new CancellationTokenSource())using(var input=new MemoryStream(bytes,false))
                {
                    cancel.Cancel();bool called=false;
                    Check.Equal("Cancelled",StudyDataReader.Read(input,"vraw",record=>called=true,cancel.Token).Status);
                    Check.True(!called&&input.Position==0);
                }
                using(var writable=new MemoryStream(bytes,true))
                    Check.Equal("Rejected",StudyDataReader.Read(writable,"vraw",record=>{},CancellationToken.None).Status);
            });
            suite.Run("study CSV does not treat a missing footer as a completed export",()=>{
                string complete=Encoding.UTF8.GetString(Csv(ViveRawTrialProjectionTests.FourBlocks()));
                int footer=complete.IndexOf("-1,export,verified_chain_sha256",StringComparison.Ordinal);Check.True(footer>0);
                var partial=Read(Encoding.UTF8.GetBytes(complete.Substring(0,footer)),"csv");
                Check.Equal("Incomplete",partial.Status);Check.True(partial.SourceTerminalObserved&&!partial.ChainVerified);
                string badCount=complete.Replace("verified_record_count,text,", "verified_record_count,text,9");
                Check.Equal("Rejected",Read(Encoding.UTF8.GetBytes(badCount),"csv").Status);
            });
        }
    }
}
#endif
