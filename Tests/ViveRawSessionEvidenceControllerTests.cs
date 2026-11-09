using System;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveRawSessionEvidenceControllerTests
    {
        const string Path=@"C:\fixture-private-id.vraw";
        sealed class DisposeFailure:MemoryStream
        {
            internal DisposeFailure():base(new byte[0],false){}
            protected override void Dispose(bool disposing){base.Dispose(disposing);throw new IOException("private-id");}
        }
        static ViveRawSessionReadResult Prefix(string text,bool truncated=false)
        {return new ViveRawSessionReadResult{Status=ViveRawSessionReadStatus.VerifiedPrefix,DisplayText=text,TrailingFrameTruncated=truncated};}
        static Stream ReadOnlyMemory(){return new MemoryStream(new byte[0],false);}
        static byte[] SyntheticManifest()
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"geometry-fixture");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"plan-fixture",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"intent-fixture",true,true);
            var manifest=new ViveNamedSessionFrameAdmission.Manifest("fixture-session","fixture-clock",plan,intent,true);
            manifest.SealChoiceTime(0.5,-1);byte[] digest;
            return ViveRawSessionJournalCodec.Frame(ViveRawSessionJournalCodec.ManifestPayload(manifest),new byte[32],true,out digest);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("raw preview requires selection and explicit incomplete prefix consent",()=>{
                int opens=0;var c=new ViveRawSessionEvidenceController(p=>{opens++;return ReadOnlyMemory();},s=>Prefix("fixture"),60000);
                Check.True(!c.TryLoad(Path,false,true));Check.True(!c.TryLoad(Path,true,false));
                Check.Equal(0,opens);Check.True(!c.HasPreview);Check.True(c.TryLoad(Path,true,true));
                Check.Equal(1,opens);Check.Equal("fixture",c.PreviewText);c.ClearSensitive();Check.True(!c.HasPreview);
            });
            suite.Run("raw preview rejects invalid result and oversize utf8 without exposing content",()=>{
                var c=new ViveRawSessionEvidenceController(p=>ReadOnlyMemory(),s=>new ViveRawSessionReadResult{Status=ViveRawSessionReadStatus.Invalid,DisplayText="secret"},60000);
                Check.True(!c.TryLoad(Path,true,true));Check.True(c.PreviewText==null);Check.True(!c.Message.Contains("secret"));
                var large=new ViveRawSessionEvidenceController(p=>ReadOnlyMemory(),s=>Prefix(new string('\u00e9',1100000)),60000);
                Check.True(!large.TryLoad(Path,true,true));Check.True(!large.HasPreview);
            });
            suite.Run("raw preview disposal failure cannot retain result or reveal exception",()=>{
                var c=new ViveRawSessionEvidenceController(p=>new DisposeFailure(),s=>Prefix("secret"),60000);
                Check.True(!c.TryLoad(Path,true,true));Check.True(!c.HasPreview);Check.True(!c.Message.Contains("private-id"));
            });
            suite.Run("raw preview expires and clears truncated indicator",()=>{
                var c=new ViveRawSessionEvidenceController(p=>ReadOnlyMemory(),s=>Prefix("fixture",true),25);
                Check.True(c.TryLoad(Path,true,true));Check.True(c.TrailingFrameTruncated);
                Check.True(SpinWait.SpinUntil(()=>!c.HasPreview,3000));Check.True(!c.TrailingFrameTruncated);
            });
            suite.Run("raw preview clear wins against a read in progress",()=>{
                var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();bool result=true;
                var c=new ViveRawSessionEvidenceController(p=>ReadOnlyMemory(),s=>{entered.Set();release.Wait();return Prefix("secret");},60000);
                var thread=new Thread(()=>result=c.TryLoad(Path,true,true));thread.Start();
                try{Check.True(entered.Wait(3000));c.ClearSensitive();release.Set();Check.True(thread.Join(3000));Check.True(!result);Check.True(!c.HasPreview);}
                finally{release.Set();thread.Join(3000);entered.Dispose();release.Dispose();}
            });
            suite.Run("raw preview controller cross reads synthetic VRAW through strict reader",()=>{
                byte[] fixture=SyntheticManifest();
                var c=new ViveRawSessionEvidenceController(p=>new MemoryStream(fixture,false),
                    s=>ViveRawSessionJournalReader.Read(s,true,true,4194304,5000),60000);
                Check.True(c.TryLoad(Path,true,true));Check.True(c.HasPreview);
                Check.True(c.PreviewText.Contains(",manifest,"));Check.True(!c.TrailingFrameTruncated);
                c.ClearSensitive();Check.True(!c.HasPreview);
            });
        }
    }
}
