using System;
using System.IO;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class DesktopPreviewTests
    {
        static DesktopPreviewOptions Options(params string[] args){return DesktopPreviewOptions.Parse(args,"preview-default",44510,44511);}
        static DesktopPreviewStatus Status(long sequence=0,string message="Waiting for owner")
        {return new DesktopPreviewStatus("preview-session",sequence,Phase.Preparation,-1,null,null,false,message);}
        static void WithDirectory(Action<string> test)
        {
            string path=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"stroop-preview-test-"+Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(path);try{test(path);}finally{Directory.Delete(path,true);}
        }
        static void InvalidField(string wire,int index,string value)
        {var fields=wire.Split('|');fields[index]=value;Check.Throws<FormatException>(()=>DesktopPreviewStatus.Parse(String.Join("|",fields)));}
        public static void Register(TestSuite suite)
        {
            suite.Run("Desktop preview defaults and unknown Unity arguments preserve owner-only startup",()=>{
                var options=Options("preview.exe","-screen-width","1200","-logFile","preview.log");
                Check.Equal("preview-default",options.SessionId);Check.Equal(44510,options.ParticipantPort);Check.Equal(44511,options.OperatorPort);
                Check.Equal<string>(null,options.OwnerCommandsPath);Check.Equal<string>(null,options.JournalPath);Check.Equal<string>(null,options.StatusPath);Check.Equal<string>(null,options.StartupPath);
                Check.Equal(Phase.Preparation,Fixtures.New(new ManualClock()).Phase);
            });
            suite.Run("Desktop preview explicit session ports and existing absolute parent paths round trip",()=>WithDirectory(folder=>{
                string commands=Path.Combine(folder,"owner.commands"),journal=Path.Combine(folder,"journal.bin"),status=Path.Combine(folder,"status.txt"),startup=Path.Combine(folder,"startup");
                var options=Options("--stroop-session","new-session","--stroop-participant-port","1","--stroop-operator-port","65535",
                    "--stroop-owner-commands",commands,"--stroop-journal",journal,"--stroop-status",status,"--stroop-startup",startup);
                Check.Equal("new-session",options.SessionId);Check.Equal(1,options.ParticipantPort);Check.Equal(65535,options.OperatorPort);
                Check.Equal(commands,options.OwnerCommandsPath);Check.Equal(journal,options.JournalPath);Check.Equal(status,options.StatusPath);Check.Equal(startup,options.StartupPath);
            }));
            suite.Run("Desktop preview rejects duplicate missing empty malformed or equal-port options",()=>WithDirectory(folder=>{
                foreach(string option in new[]{"--stroop-session","--stroop-participant-port","--stroop-operator-port","--stroop-owner-commands","--stroop-journal","--stroop-status","--stroop-startup"})
                {
                    string value=option=="--stroop-session"?"session":option.Contains("port")?"44512":Path.Combine(folder,"file");
                    Check.Throws<ArgumentException>(()=>Options(option));Check.Throws<ArgumentException>(()=>Options(option,""));
                    Check.Throws<ArgumentException>(()=>Options(option,"--stroop-session"));Check.Throws<ArgumentException>(()=>Options(option,value,option,value));
                    Check.Throws<ArgumentException>(()=>Options(option,"-logFile"));
                }
                foreach(string port in new[]{"0","65536","-1","1.0","word","+12"," 12","999999999999999"})
                    Check.Throws<ArgumentException>(()=>Options("--stroop-participant-port",port));
                Check.Throws<ArgumentException>(()=>Options("--stroop-participant-port","44511"));
                Check.Throws<ArgumentException>(()=>DesktopPreviewOptions.Parse(new string[0],"session",0,1));
            }));
            suite.Run("Desktop preview validates session transport bounds and absolute distinct file paths",()=>WithDirectory(folder=>{
                foreach(string session in new[]{" ","bad\nsession",new string('x',129),new string('x',128)})
                    Check.Throws<ArgumentException>(()=>Options("--stroop-session",session));
                Check.Throws<ArgumentException>(()=>Options("--stroop-status","relative.txt"));
                Check.Throws<DirectoryNotFoundException>(()=>Options("--stroop-status",Path.Combine(folder,"missing","status")));
                Check.Throws<ArgumentException>(()=>Options("--stroop-status",folder));
                Check.Throws<ArgumentException>(()=>Options("--stroop-status",Path.Combine(folder,"same"),"--stroop-journal",Path.Combine(folder,"same")));
                Check.Throws<ArgumentException>(()=>Options("--stroop-startup",Path.Combine(folder,"startup"),"--stroop-status",Path.Combine(folder,"startup.Participant.ready")));
                Check.Throws<ArgumentException>(()=>Options("--stroop-owner-commands",Path.Combine(folder,"commands"),"--stroop-status",Path.Combine(folder,"commands.ack")));
                if(Path.DirectorySeparatorChar=='\\')foreach(string path in new[]{"C:relative.txt","\\root-relative.txt"})
                    Check.Throws<ArgumentException>(()=>Options("--stroop-status",path));
            }));
            suite.Run("Desktop preview status preserves identity state and escaped diagnostics without score fields",()=>{
                var status=new DesktopPreviewStatus("preview-session",42,Phase.BlockReady,2,Condition.D,ControlSelection.Automatic,true,"Recording failed | diagnostic\n中文");
                var read=DesktopPreviewStatus.Parse(status.Encode());
                Check.Equal(status.SessionId,read.SessionId);Check.Equal(42L,read.Sequence);Check.Equal(Phase.BlockReady,read.Phase);Check.Equal(2,read.BlockIndex);
                Check.Equal((Condition?)Condition.D,read.Condition);Check.Equal((ControlSelection?)ControlSelection.Automatic,read.LockedSource);Check.True(read.Ready);Check.Equal(status.Message,read.Message);
                Check.Equal(10,status.Encode().Split('|').Length);Check.True(status.Encode().IndexOf('\n')<0);
            });
            suite.Run("Desktop preview malformed status enums numbers identity and readiness fail closed",()=>{
                string wire=Status().Encode();
                InvalidField(wire,1,"2");InvalidField(wire,2,Convert.ToBase64String(Encoding.UTF8.GetBytes("bad\nsession")));
                foreach(string value in new[]{"-1","01","+1","9223372036854775808"})InvalidField(wire,3,value);
                foreach(string value in new[]{"0","preparation","Unknown"})InvalidField(wire,4,value);
                InvalidField(wire,5,"4");InvalidField(wire,5,"-2");InvalidField(wire,6,"Unknown");InvalidField(wire,7,"0");InvalidField(wire,8,"true");InvalidField(wire,8,"1");
                InvalidField(wire,9,"%%%%");Check.Throws<FormatException>(()=>DesktopPreviewStatus.Parse(wire+"|extra"));
                Check.Throws<ArgumentException>(()=>new DesktopPreviewStatus("session",0,Phase.Preparation,-1,null,null,true,""));
                Check.Throws<ArgumentException>(()=>new DesktopPreviewStatus("session",0,(Phase)99,-1,null,null,false,""));
                Check.Throws<ArgumentException>(()=>new DesktopPreviewStatus("session",0,Phase.BlockReady,0,null,ControlSelection.Automatic,true,""));
                Check.Throws<ArgumentException>(()=>Status(0,new string('x',DesktopPreviewStatus.MaximumMessageBytes+1)));
            });
            suite.Run("Desktop preview status uses atomic replacement and never treats damaged or missing files as ready",()=>WithDirectory(folder=>{
                string path=Path.Combine(folder,"status");Check.Equal<DesktopPreviewStatus>(null,DesktopPreviewStatus.Read(path));
                DesktopPreviewStatus.Write(path,Status(1));DesktopPreviewStatus.Write(path,Status(2));Check.Equal(2L,DesktopPreviewStatus.Read(path).Sequence);
                Check.Equal(0,Directory.GetFiles(folder,"*.tmp").Length);
                File.WriteAllText(path,"ASDESKTOP|1|partial");Check.Throws<FormatException>(()=>DesktopPreviewStatus.Read(path));
                File.WriteAllBytes(path,new byte[DesktopPreviewStatus.MaximumFileBytes+1]);Check.Throws<InvalidDataException>(()=>DesktopPreviewStatus.Read(path));
                File.WriteAllBytes(path,new byte[]{0xff});Check.Throws<DecoderFallbackException>(()=>DesktopPreviewStatus.Read(path));
                Check.Throws<FormatException>(()=>DesktopPreviewStatus.Parse(new string('x',DesktopPreviewStatus.MaximumFileBytes+1)));
            }));
            suite.Run("Desktop preview startup waits for both bound roles and explicit matching release",()=>WithDirectory(folder=>{
                string path=Path.Combine(folder,"startup");Check.True(!DesktopPreviewStartup.IsReleased(path,"session"));
                DesktopPreviewStartup.WriteReady(path,"session",Actor.Participant);Check.True(!DesktopPreviewStartup.IsReleased(path,"session"));
                File.WriteAllText(path,"ASPREVIEWSTART|1|session",new UTF8Encoding(false));Check.True(!DesktopPreviewStartup.IsReleased(path,"session"));
                DesktopPreviewStartup.WriteReady(path,"session",Actor.Operator);Check.True(DesktopPreviewStartup.IsReleased(path,"session"));
                Check.Throws<IOException>(()=>DesktopPreviewStartup.WriteReady(path,"session",Actor.Participant));
                Check.Throws<InvalidDataException>(()=>DesktopPreviewStartup.IsReleased(path,"other-session"));
            }));
            suite.Run("Desktop preview startup rejects altered ready and release files without choosing a source",()=>WithDirectory(folder=>{
                string path=Path.Combine(folder,"startup");DesktopPreviewStartup.WriteReady(path,"session",Actor.Participant);DesktopPreviewStartup.WriteReady(path,"session",Actor.Operator);
                File.WriteAllText(path,"ASPREVIEWSTART|1|other-session",new UTF8Encoding(false));Check.Throws<InvalidDataException>(()=>DesktopPreviewStartup.IsReleased(path,"session"));
                File.WriteAllText(path,"ASPREVIEWSTART|1|session",new UTF8Encoding(false));File.WriteAllText(path+".Operator.ready","ASPREVIEWREADY|1|session|Participant",new UTF8Encoding(false));
                Check.Throws<InvalidDataException>(()=>DesktopPreviewStartup.IsReleased(path,"session"));
                Check.Throws<ArgumentException>(()=>DesktopPreviewStartup.WriteReady(path,"session",Actor.Owner));
            }));
            suite.Run("Desktop preview publishes a release only after both matching ready files and never overwrites it",()=>WithDirectory(folder=>{
                string path=Path.Combine(folder,"startup");Check.Throws<InvalidOperationException>(()=>DesktopPreviewStartup.WriteRelease(path,"session"));
                DesktopPreviewStartup.WriteReady(path,"session",Actor.Participant);Check.Throws<InvalidOperationException>(()=>DesktopPreviewStartup.WriteRelease(path,"session"));
                DesktopPreviewStartup.WriteReady(path,"session",Actor.Operator);DesktopPreviewStartup.WriteRelease(path,"session");
                Check.True(DesktopPreviewStartup.IsReleased(path,"session"));Check.Throws<IOException>(()=>DesktopPreviewStartup.WriteRelease(path,"session"));
                Check.Equal(0,Directory.GetFiles(folder,"*.tmp").Length);
            }));
        }
    }
}
