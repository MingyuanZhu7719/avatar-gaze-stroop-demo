using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Injected in-memory fakes only. No filesystem, real participants, device,
    // network, actual Stream, or File.Move are touched by this test suite.
    public static class VivePracticeNativeTargetTests
    {
        sealed class FakeHandle:IVivePracticeNativeHandle,IVivePracticeNativeErrorInfo
        {
            internal int Writes,Flushes,Closes;internal bool Partial,ThrowWrite,ThrowFlush,ThrowClose,CloseResult=true;
            internal int? WriteError,FlushError,CloseError;public int? LastNativeError {get{return CloseError;}}
            internal Action OnWrite,OnFlush,OnClose;internal readonly List<byte[]> Captured=new List<byte[]>();
            public int Write(byte[] bytes)
            {
                Writes++;int n=Partial?Math.Max(0,bytes.Length-1):bytes.Length;
                if(OnWrite!=null)OnWrite();var copy=new byte[n];Buffer.BlockCopy(bytes,0,copy,0,n);Captured.Add(copy);
                if(WriteError.HasValue)throw new VivePracticeNativeCallException(WriteError.Value);
                if(ThrowWrite)throw new IOException("synthetic-write");return n;
            }
            public void Flush(){Flushes++;if(OnFlush!=null)OnFlush();if(FlushError.HasValue)throw new VivePracticeNativeCallException(FlushError.Value);if(ThrowFlush)throw new IOException("synthetic-flush");}
            public bool Close(){Closes++;if(OnClose!=null)OnClose();if(ThrowClose)throw new IOException("synthetic-close");return CloseResult;}
        }
        sealed class FakeFactory:IVivePracticeNativeFactory
        {
            internal int Creates;internal bool ThrowCreate,CreateCleanupUncertain;internal int? CreateError,CreateCleanupError;internal Action OnCreate;internal string RequestedPath;
            internal readonly FakeHandle Handle=new FakeHandle();
            public IVivePracticeNativeHandle CreateNew(string absolutePath)
            {
                Creates++;RequestedPath=absolutePath;if(OnCreate!=null)OnCreate();
                if(CreateError.HasValue||CreateCleanupError.HasValue||CreateCleanupUncertain)throw new VivePracticeNativeCallException(CreateError,CreateCleanupError,CreateCleanupUncertain);
                if(ThrowCreate)throw new IOException("synthetic-create");return Handle;
            }
        }
        sealed class Fixture
        {
            internal const string DirectoryPath=@"C:\synthetic-fixture\selected";
            internal readonly object Context=new object();internal readonly FakeFactory Factory=new FakeFactory();internal readonly VivePracticeNativeTarget Target;
            internal Fixture(int maximumBytes=64){Target=new VivePracticeNativeTarget(DirectoryPath,Context,Factory,maximumBytes);}
            internal void Authorize(){Check.True(Target.ChooseEye());Check.True(Target.ChooseTask());}
            internal void Open(){Authorize();Check.True(Target.Create());}
            internal static byte[] Bytes(){return new byte[]{1,2,3};}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("Practice native target requires choices before fake create",()=>{
                var f=new Fixture();Check.True(!f.Target.Create());Check.Equal(0,f.Factory.Creates);
                Check.True(f.Target.ChooseEye());Check.True(!f.Target.Create());Check.Equal(0,f.Factory.Creates);
                Check.True(f.Target.ChooseTask());Check.True(f.Target.Create());Check.Equal(1,f.Factory.Creates);
                Check.True(!f.Target.ProductionGateOpen&&!f.Target.ConsentAuthenticated&&!f.Target.DurableStorageProven);
            });
            suite.Run("Practice native target requests a single random non-identifying component",()=>{
                var f=new Fixture();f.Open();string path=f.Factory.RequestedPath;
                Check.Equal(path,f.Target.OutputPath);int split=path.LastIndexOfAny(new[]{'\\','/'});Check.Equal(Fixture.DirectoryPath,path.Substring(0,split));
                string name=path.Substring(split+1);Guid parsed;
                Check.True(name.StartsWith("practice-",StringComparison.Ordinal)&&name.EndsWith(".bin",StringComparison.Ordinal));
                Check.Equal(9+32+4,name.Length);Check.True(Guid.TryParseExact(name.Substring(9,32),"N",out parsed));
                Check.True(name.IndexOfAny(new[]{'\\','/',':'})<0);Check.True(!f.Target.Create());Check.Equal(1,f.Factory.Creates);
            });
            suite.Run("Practice native target appends exact ordered synthetic frames",()=>{
                var f=new Fixture(6);f.Open();Check.True(f.Target.Append(f.Context,0,Fixture.Bytes()));
                Check.True(f.Target.Append(f.Context,1,Fixture.Bytes()));Check.Equal(2,f.Factory.Handle.Writes);
                Check.Equal((byte)1,f.Factory.Handle.Captured[0][0]);Check.True(f.Target.Flush());Check.True(f.Target.Close());
                Check.Equal(VivePracticeNativeState.Closed,f.Target.State);Check.True(f.Target.HandleClosed);
                Check.True(!f.Target.Close()&&!f.Target.Cleanup());Check.Equal(1,f.Factory.Handle.Flushes);Check.Equal(1,f.Factory.Handle.Closes);
            });
            suite.Run("Practice native target freezes caller buffer before fake write",()=>{
                var f=new Fixture();f.Open();var caller=Fixture.Bytes();f.Factory.Handle.OnWrite=()=>caller[0]=9;
                Check.True(f.Target.Append(f.Context,0,caller));Check.Equal((byte)1,f.Factory.Handle.Captured[0][0]);
                Check.Equal((byte)9,caller[0]);Check.True(f.Target.Close());
            });
            suite.Run("Practice native target context sequence and budget errors forbid clean close",()=>{
                for(int variant=0;variant<3;variant++){
                    var f=new Fixture(3);f.Open();if(variant==2)Check.True(f.Target.Append(f.Context,0,Fixture.Bytes()));
                    object context=variant==0?new object():f.Context;int sequence=variant==1?1:variant==2?1:0;
                    Check.True(!f.Target.Append(context,sequence,Fixture.Bytes()));Check.Equal(VivePracticeNativeState.Faulted,f.Target.State);
                    Check.True(!f.Target.Flush()&&!f.Target.Close()&&!f.Target.Append(f.Context,0,Fixture.Bytes()));
                    Check.True(f.Target.CleanupRequired);Check.True(!f.Target.Cleanup());Check.Equal(1,f.Factory.Handle.Closes);
                    Check.True(f.Target.HandleClosed);Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);
                }
            });
            suite.Run("Practice native target fake create failure is uncertain and never retried",()=>{
                var f=new Fixture();f.Authorize();f.Factory.ThrowCreate=true;Check.True(!f.Target.Create());
                Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);Check.True(!f.Target.Create()&&!f.Target.Flush()&&!f.Target.Cleanup());
                Check.Equal(1,f.Factory.Creates);
            });
            suite.Run("Practice native target rejects ambiguous and nonabsolute directory strings",()=>{
                foreach(string path in new[]{@"C:relative",@"\\server\share",@"\\?\C:\device",@"C:\base\..\other",@"C:\base\.",@"C:\folder ",@"C:\folder.",@"C:\folder:stream"})
                    Check.Throws<ArgumentException>(()=>new VivePracticeNativeTarget(path,new object(),new FakeFactory(),64));
                var valid=new VivePracticeNativeTarget(@"C:\",new object(),new FakeFactory(),64);Check.True(valid.OutputPath.IndexOf("practice-",StringComparison.Ordinal)>=0);
            });
            suite.Run("Practice native target preserves sanitized denied evidence and never retries",()=>{
                var f=new Fixture();f.Authorize();f.Factory.CreateError=5;Check.True(!f.Target.Create());
                Check.True(f.Target.AccessDeniedObserved);Check.Equal((int?)5,f.Target.LastNativeError);Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);
                Check.True(!f.Target.Create());Check.Equal(1,f.Factory.Creates);
                f=new Fixture();f.Open();f.Factory.Handle.WriteError=5;Check.True(!f.Target.Append(f.Context,0,Fixture.Bytes()));
                Check.True(f.Target.AccessDeniedObserved);Check.Equal((int?)5,f.Target.LastNativeError);f.Factory.Handle.CloseResult=false;f.Factory.Handle.CloseError=6;
                Check.True(!f.Target.Cleanup());Check.Equal(1,f.Factory.Handle.Closes);Check.Equal((int?)6,f.Target.LastNativeError);Check.True(f.Target.AccessDeniedObserved);
                f=new Fixture();f.Open();f.Factory.Handle.FlushError=5;Check.True(!f.Target.Flush());Check.True(f.Target.AccessDeniedObserved);f.Target.Dispose();
                f=new Fixture();f.Authorize();f.Factory.CreateError=6;f.Factory.CreateCleanupError=5;f.Factory.CreateCleanupUncertain=true;Check.True(!f.Target.Create());
                Check.Equal((int?)6,f.Target.LastNativeError);Check.Equal((int?)5,f.Target.LastNativeCleanupError);Check.True(f.Target.NativeCleanupOutcomeUncertain&&f.Target.AccessDeniedObserved);
            });
            suite.Run("Practice native target owner finally disposes one known handle",()=>{
                var f=new Fixture();f.Open();f.Target.Revoke();f.Target.Dispose();Check.True(f.Target.HandleClosed);Check.Equal(1,f.Factory.Handle.Closes);
                f.Target.Dispose();Check.Equal(1,f.Factory.Handle.Closes);Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);
                f=new Fixture();f.Open();f.Factory.Handle.Partial=true;Check.True(!f.Target.Append(f.Context,0,Fixture.Bytes()));f.Target.Dispose();
                Check.True(f.Target.HandleClosed);Check.Equal(1,f.Factory.Handle.Closes);Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);
            });
            suite.Run("Practice native target dispose before create permanently closes admission",()=>{
                for(int choices=0;choices<=2;choices++){
                    var f=new Fixture();if(choices>=1)Check.True(f.Target.ChooseEye());if(choices==2)Check.True(f.Target.ChooseTask());
                    f.Target.Dispose();Check.Equal(VivePracticeNativeState.Disposed,f.Target.State);
                    Check.True(!f.Target.ChooseEye()&&!f.Target.ChooseTask()&&!f.Target.Create());Check.Equal(0,f.Factory.Creates);
                }
            });
            suite.Run("Practice native target in-flight dispose defers one cleanup until return",()=>{
                var f=new Fixture();f.Open();f.Factory.Handle.OnWrite=()=>f.Target.Dispose();
                Check.True(!f.Target.Append(f.Context,0,Fixture.Bytes()));Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);
                Check.Equal(1,f.Factory.Handle.Writes);Check.Equal(1,f.Factory.Handle.Closes);Check.True(f.Target.HandleClosed);
                Check.True(!f.Target.Append(f.Context,0,Fixture.Bytes())&&!f.Target.Flush());f.Target.Dispose();Check.Equal(1,f.Factory.Handle.Closes);
                f=new Fixture();f.Authorize();f.Factory.OnCreate=()=>f.Target.Dispose();Check.True(!f.Target.Create());
                Check.True(f.Target.HandleClosed);Check.Equal(1,f.Factory.Handle.Closes);Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);
            });
            suite.Run("Practice native target foreign idle dispose requires owner cleanup",()=>{
                var f=new Fixture();f.Open();var thread=new Thread(()=>f.Target.Dispose());thread.Start();Check.True(thread.Join(3000));
                Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);Check.True(f.Target.CleanupRequired&&!f.Target.HandleClosed);
                Check.True(!f.Target.Append(f.Context,0,Fixture.Bytes())&&!f.Target.Flush());Check.Equal(0,f.Factory.Handle.Closes);
                f.Target.Dispose();Check.True(f.Target.HandleClosed);Check.Equal(1,f.Factory.Handle.Closes);f.Target.Dispose();Check.Equal(1,f.Factory.Handle.Closes);
            });
            suite.Run("Practice native target revoke before create reserves no fake operation",()=>{
                var f=new Fixture();f.Authorize();f.Target.Revoke();Check.Equal(VivePracticeNativeState.Revoked,f.Target.State);
                Check.True(!f.Target.Create()&&!f.Target.Append(f.Context,0,Fixture.Bytes())&&!f.Target.Flush()&&!f.Target.Close());Check.Equal(0,f.Factory.Creates);
            });
            suite.Run("Practice native target pre-reserved fake create after revoke is uncertain",()=>{
                var f=new Fixture();f.Authorize();bool hook=false;
                f.Target.AfterReservation=()=>{f.Target.AfterReservation=null;f.Target.Revoke();hook=true;};
                Check.True(!f.Target.Create());Check.True(hook);Check.Equal(1,f.Factory.Creates);
                Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);Check.True(f.Target.CleanupRequired);
                Check.True(!f.Target.Cleanup());Check.True(f.Target.HandleClosed);Check.Equal(1,f.Factory.Handle.Closes);
                Check.True(!f.Target.Create()&&!f.Target.Cleanup());Check.Equal(1,f.Factory.Creates);
            });
            suite.Run("Practice native target revoke during fake create keeps known handle for cleanup",()=>{
                var f=new Fixture();f.Authorize();f.Factory.OnCreate=()=>f.Target.Revoke();Check.True(!f.Target.Create());
                Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);Check.True(f.Target.CleanupRequired);
                Check.True(!f.Target.Cleanup());Check.True(f.Target.HandleClosed);Check.Equal(1,f.Factory.Handle.Closes);
            });
            suite.Run("Practice native target revoke after open starts no fake writes",()=>{
                var f=new Fixture();f.Open();f.Target.Revoke();Check.Equal(VivePracticeNativeState.Revoked,f.Target.State);
                Check.True(!f.Target.Append(f.Context,0,Fixture.Bytes())&&!f.Target.Flush()&&!f.Target.Close());
                Check.Equal(0,f.Factory.Handle.Writes);Check.Equal(0,f.Factory.Handle.Flushes);
                Check.True(!f.Target.Cleanup());Check.True(f.Target.HandleClosed);Check.Equal(1,f.Factory.Handle.Closes);
            });
            suite.Run("Practice native target revoke during fake write is uncertain and nonblocking",()=>{
                var f=new Fixture();f.Open();f.Factory.Handle.OnWrite=()=>{var t=new Thread(()=>f.Target.Revoke());t.Start();Check.True(t.Join(3000));};
                Check.True(!f.Target.Append(f.Context,0,Fixture.Bytes()));Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);
                Check.Equal(1,f.Factory.Handle.Writes);Check.True(!f.Target.Append(f.Context,0,Fixture.Bytes())&&!f.Target.Flush());
                Check.Equal(0,f.Factory.Handle.Flushes);Check.True(!f.Target.Cleanup());Check.True(f.Target.HandleClosed);Check.Equal(1,f.Factory.Handle.Closes);
            });
            suite.Run("Practice native target fake partial or thrown write is uncertain with no retry",()=>{
                foreach(bool partial in new[]{false,true}){
                    var f=new Fixture();f.Open();f.Factory.Handle.Partial=partial;f.Factory.Handle.ThrowWrite=!partial;
                    Check.True(!f.Target.Append(f.Context,0,Fixture.Bytes()));Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);
                    Check.Equal(partial?2:3,f.Factory.Handle.Captured[0].Length);Check.True(!f.Target.Append(f.Context,0,Fixture.Bytes())&&!f.Target.Flush());
                    Check.Equal(1,f.Factory.Handle.Writes);Check.True(!f.Target.Cleanup());Check.Equal(1,f.Factory.Handle.Closes);
                }
            });
            suite.Run("Practice native target fake flush failure or revoke forbids retry",()=>{
                foreach(bool revoke in new[]{false,true}){
                    var f=new Fixture();f.Open();Check.True(f.Target.Append(f.Context,0,Fixture.Bytes()));
                    if(revoke)f.Factory.Handle.OnFlush=()=>f.Target.Revoke();else f.Factory.Handle.ThrowFlush=true;
                    Check.True(!f.Target.Flush());Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);
                    Check.True(!f.Target.Flush()&&!f.Target.Append(f.Context,1,Fixture.Bytes()));Check.Equal(1,f.Factory.Handle.Flushes);
                    Check.True(!f.Target.Cleanup());Check.Equal(1,f.Factory.Handle.Closes);
                }
            });
            suite.Run("Practice native target fake close failure or revoke never retries",()=>{
                foreach(int mode in new[]{0,1,2}){
                    var f=new Fixture();f.Open();if(mode==0)f.Factory.Handle.CloseResult=false;else if(mode==1)f.Factory.Handle.ThrowClose=true;else f.Factory.Handle.OnClose=()=>f.Target.Revoke();
                    Check.True(!f.Target.Close());Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);Check.True(!f.Target.Cleanup()&&!f.Target.Close());
                    Check.Equal(1,f.Factory.Handle.Closes);Check.Equal(mode==2,f.Target.HandleClosed);
                }
            });
            suite.Run("Practice native target reentrancy and foreign owner cannot reserve",()=>{
                var f=new Fixture();f.Open();bool nested=true;f.Factory.Handle.OnWrite=()=>nested=f.Target.Append(f.Context,0,Fixture.Bytes());
                Check.True(!f.Target.Append(f.Context,0,Fixture.Bytes()));Check.True(!nested);Check.Equal(1,f.Factory.Handle.Writes);Check.Equal(VivePracticeNativeState.Uncertain,f.Target.State);
                f=new Fixture();f.Open();bool foreign=true;var t=new Thread(()=>foreign=f.Target.Flush());t.Start();Check.True(t.Join(3000));Check.True(!foreign);
                Check.Equal(VivePracticeNativeState.Faulted,f.Target.State);Check.Equal(0,f.Factory.Handle.Flushes);
            });
        }
    }
}
