using System;
using System.IO;
using System.Security;
using System.Text;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class LifecycleFileTests
    {
        const int SharingViolation=unchecked((int)0x80070020);
        const int LockViolation=unchecked((int)0x80070021);

        sealed class NonIoLockException : Exception
        {
            public NonIoLockException(int result):base("Non-I/O error with a lock HRESULT"){HResult=result;}
        }

        public static void Register(TestSuite tests)
        {
            tests.Run("Lifecycle reads defer exact sharing and lock violations then return the complete record",()=>{
                const string path="session/start.tsv";
                const string expected="START\t1\tsession-注视\ntrial\ttrial-1\tRed/Blue\n";
                int attempts=0;
                var files=new SimulationLifecycleFiles(requested=>{
                    Check.Equal(path,requested);attempts++;
                    if(attempts==1)throw new IOException("sharing violation",SharingViolation);
                    if(attempts==2)throw new IOException("lock violation",LockViolation);
                    return expected;
                });
                string text="previous contents";
                Check.True(!files.TryReadText(path,out text));Check.Equal(null,text);Check.Equal(1,attempts);
                Check.Equal(1L,files.WaitCount);AssertWait(files.RecentWaits[0],1,path,SharingViolation);
                text="previous contents";
                Check.True(!files.TryReadText(path,out text));Check.Equal(null,text);Check.Equal(2,attempts);
                Check.Equal(2L,files.WaitCount);AssertWait(files.RecentWaits[1],2,path,LockViolation);
                Check.True(files.TryReadText(path,out text));Check.Equal(expected,text);Check.Equal(3,attempts);
                Check.Equal(2L,files.WaitCount);Check.Equal(2,files.RecentWaits.Count);
            });
            tests.Run("Lifecycle exact reads reject wrong sessions and malformed full records after a lock clears",()=>{
                const string path="session/start.tsv",expected="START\t1\tsession-a\n";
                foreach(string invalid in new[]{"START\t1\tsession-b\n","START\t1\tsession-a","START\t1\tsession-a\nextra\n","START\t1\tsession-a\r\n",""," START\t1\tsession-a\n"})
                {
                    int attempts=0;
                    var files=new SimulationLifecycleFiles(requested=>{
                        Check.Equal(path,requested);attempts++;
                        if(attempts==1)throw new IOException("temporary lock",LockViolation);
                        return invalid;
                    });
                    Check.True(!files.TryReadExact(path,expected));Check.Equal(1,attempts);
                    Check.Throws<InvalidDataException>(()=>files.TryReadExact(path,expected));
                    Check.Equal(2,attempts);Check.Equal(1L,files.WaitCount);Check.Equal(1,files.RecentWaits.Count);
                    AssertWait(files.RecentWaits[0],1,path,LockViolation);
                }
                int validAttempts=0;
                var valid=new SimulationLifecycleFiles(pathValue=>{
                    Check.Equal(path,pathValue);validAttempts++;
                    if(validAttempts==1)throw new IOException("temporary sharing",SharingViolation);
                    return expected;
                });
                Check.True(!valid.TryReadExact(path,expected));Check.Equal(1,validAttempts);
                Check.True(valid.TryReadExact(path,expected));Check.Equal(2,validAttempts);Check.Equal(1L,valid.WaitCount);
            });
            tests.Run("Lifecycle reads propagate unrelated I/O and lookalike HRESULTs unchanged",()=>{
                foreach(int result in new[]{unchecked((int)0x80070002),unchecked((int)0x80070003),unchecked((int)0x80070005),
                    unchecked((int)0x8007001F),unchecked((int)0x80040020),unchecked((int)0x80040021),0x00070020,0x00070021})
                    AssertPropagates(new IOException("unrelated I/O",result),false);
                AssertPropagates(new FileNotFoundException("missing lifecycle record"),false);
                AssertPropagates(new DirectoryNotFoundException("missing lifecycle directory"),true);
                AssertPropagates(new InvalidDataException("malformed source record"),true);
            });
            tests.Run("Lifecycle reads propagate security programming and non-I/O lock-code exceptions unchanged",()=>{
                AssertPropagates(new UnauthorizedAccessException("access denied"),false);
                AssertPropagates(new SecurityException("security denied"),true);
                AssertPropagates(new ArgumentException("invalid path"),false);
                AssertPropagates(new InvalidOperationException("invalid reader state"),true);
                AssertPropagates(new ObjectDisposedException("reader"),true);
                AssertPropagates(new NonIoLockException(SharingViolation),false);
                AssertPropagates(new NonIoLockException(LockViolation),true);
            });
            tests.Run("Lifecycle permanent locks make one attempt per caller poll and retain only the last 64 waits",()=>{
                var clock=new ManualClock();const double deadline=70;int attempts=0;
                var files=new SimulationLifecycleFiles(path=>{
                    attempts++;clock.Advance(1);
                    Check.Equal("lifecycle-"+attempts+".tsv",path);
                    // Success is deliberately beyond the caller's budget. A hidden
                    // retry must not consume it and turn a timed-out poll into success.
                    if(attempts<=70)throw new IOException("still locked",attempts%2==1?SharingViolation:LockViolation);
                    return "START\t1\tsession\n";
                });
                while(clock.Now<deadline)
                {
                    int before=attempts;double beforeTime=clock.Now;
                    Check.True(!files.TryReadExact("lifecycle-"+(before+1)+".tsv","START\t1\tsession\n"));
                    Check.Equal(before+1,attempts);Check.Near(beforeTime+1,clock.Now);
                    Check.Equal((long)attempts,files.WaitCount);Check.True(files.RecentWaits.Count<=64);
                }
                Check.Equal(70,attempts);Check.Near(deadline,clock.Now);Check.Equal(70L,files.WaitCount);
                Check.Equal(64,files.RecentWaits.Count);
                for(int i=0;i<64;i++)
                {
                    long sequence=i+7;
                    AssertWait(files.RecentWaits[i],sequence,"lifecycle-"+sequence+".tsv",sequence%2==1?SharingViolation:LockViolation);
                }
            });
            tests.Run("Lifecycle default reader preserves complete Unicode multiline file content",()=>{
                string directory=Path.Combine(Path.GetTempPath(),"stroop-lifecycle-test-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    string path=Path.Combine(directory,"participant-goal.tsv");
                    const string expected="PARTICIPANT_GOAL\t1\tsession-注视\tfault-script\ntrial\t试次-1\tRed/Blue\r\n";
                    File.WriteAllText(path,expected,new UTF8Encoding(true));
                    var files=new SimulationLifecycleFiles();string text;
                    Check.True(files.TryReadText(path,out text));Check.Equal(expected,text);
                    Check.True(files.TryReadExact(path,expected));Check.Equal(0L,files.WaitCount);Check.Equal(0,files.RecentWaits.Count);
                }
                finally{Directory.Delete(directory,true);}
            });
        }

        static void AssertWait(LifecycleReadWait wait,long sequence,string path,int result)
        {
            Check.Equal(sequence,wait.Sequence);Check.Equal(path,wait.Path);
            Check.Equal("read_all_text",wait.Operation);Check.Equal(result,wait.HResult);
        }
        static void AssertPropagates<T>(T expected,bool exact) where T:Exception
        {
            const string path="session/record.tsv";int attempts=0;bool caught=false;
            var files=new SimulationLifecycleFiles(requested=>{Check.Equal(path,requested);attempts++;throw expected;});
            try
            {
                if(exact)files.TryReadExact(path,"expected\n");
                else{string text;files.TryReadText(path,out text);}
            }
            catch(T actual){Check.True(Object.ReferenceEquals(expected,actual),"Reader must preserve the original exception instance");caught=true;}
            Check.True(caught,"Expected the original exception to propagate");Check.Equal(1,attempts);
            Check.Equal(0L,files.WaitCount);Check.Equal(0,files.RecentWaits.Count);
        }
    }
}
