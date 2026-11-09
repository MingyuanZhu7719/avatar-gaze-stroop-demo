using System;
using System.IO;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Fake-only: no filesystem, Stream, historical record, device or user data.
    public static class VivePracticeCreateNewFixtureTests
    {
        sealed class Fixture
        {
            internal readonly PracticeCreateFixtureDirectory Directory=new PracticeCreateFixtureDirectory("synthetic-root");
            internal readonly PracticeCreateFakeFactory Factory=new PracticeCreateFakeFactory();internal readonly object Context=new object();
            internal readonly VivePracticeCreateNewFixture Target;
            internal Fixture(int maximum=64){Target=new VivePracticeCreateNewFixture(Directory,Context,Factory,maximum);}
            internal void Authorize(){Check.True(Target.ChooseEye());Check.True(Target.ChooseTask());}
            internal void Open(){Authorize();Check.True(Target.Create());}
            internal PracticeCreateFixtureBatch Batch(int seq=0){return new PracticeCreateFixtureBatch(Context,seq,42);}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("Create fixture requires both choices and uses exclusive create-new contract",()=>{
                var f=new Fixture();Check.True(!f.Target.Create());Check.Equal(0,f.Factory.CreateCalls);Check.True(f.Target.ChooseEye());
                Check.True(!f.Target.Create());Check.True(f.Target.ChooseTask());Check.True(f.Target.Create());
                Check.Equal(FileMode.CreateNew,f.Factory.Mode);Check.Equal(FileAccess.Write,f.Factory.Access);Check.Equal(FileShare.None,f.Factory.Share);
                Check.True(Object.ReferenceEquals(f.Directory,f.Factory.Directory));Check.True(f.Factory.LastName.StartsWith("practice-",StringComparison.Ordinal));
                Check.True(f.Factory.LastName.EndsWith(".fixture",StringComparison.Ordinal)&&f.Factory.LastName.IndexOfAny(new[]{'\\','/',':'})<0);
                Check.True(f.Target.SyntheticFixtureOnly&&!f.Target.ProductionGateOpen&&!f.Target.ConsentAuthenticated&&!f.Target.PhysicalExclusiveProven&&!f.Target.DurableStorageProven);
            });
            suite.Run("Create fixture allows ordered bounded fake appends and one close",()=>{
                var f=new Fixture(4);f.Open();Check.True(f.Target.Append(f.Batch()));Check.True(f.Target.Append(f.Batch(1)));
                Check.Equal(2,f.Factory.Result.WriteCalls);Check.True(f.Target.Flush());Check.True(f.Target.Close());
                Check.Equal(PracticeCreateFixtureState.Closed,f.Target.State);Check.True(f.Target.HandleClosed);Check.True(!f.Target.Close()&&!f.Target.Cleanup());
                Check.Equal(1,f.Factory.Result.FlushCalls);Check.Equal(1,f.Factory.Result.CloseCalls);
            });
            suite.Run("Create fixture context sequence or capacity failure forbids clean close",()=>{
                for(int variant=0;variant<3;variant++){
                    var f=new Fixture(2);f.Open();if(variant==2)Check.True(f.Target.Append(f.Batch()));
                    var invalid=variant==0?new PracticeCreateFixtureBatch(new object(),0,1):f.Batch(variant==1?1:1);
                    Check.True(!f.Target.Append(invalid));Check.Equal(PracticeCreateFixtureState.Faulted,f.Target.State);
                    Check.True(!f.Target.Flush()&&!f.Target.Close()&&!f.Target.Append(f.Batch()));Check.True(!f.Target.Cleanup());
                    Check.Equal(1,f.Factory.Result.CloseCalls);Check.Equal(PracticeCreateFixtureState.Uncertain,f.Target.State);
                }
            });
            suite.Run("Create fixture failed create is uncertain and never retried",()=>{
                var f=new Fixture();f.Authorize();f.Factory.FailCreate=true;Check.True(!f.Target.Create());Check.Equal(PracticeCreateFixtureState.Uncertain,f.Target.State);
                Check.True(!f.Target.Create()&&!f.Target.Flush()&&!f.Target.Cleanup());Check.Equal(1,f.Factory.CreateCalls);
            });
            suite.Run("Create fixture revoke after reservation may precede fake create",()=>{
                var f=new Fixture();f.Authorize();bool observed=false;
                f.Target.AfterReservation=()=>{f.Target.AfterReservation=null;f.Target.Revoke();observed=true;};
                Check.True(!f.Target.Create());Check.True(observed);Check.Equal(1,f.Factory.CreateCalls);Check.Equal(PracticeCreateFixtureState.Uncertain,f.Target.State);
                Check.True(!f.Target.Create()&&f.Target.CleanupRequired);Check.True(!f.Target.Cleanup());Check.True(f.Target.HandleClosed);Check.Equal(1,f.Factory.Result.CloseCalls);
            });
            suite.Run("Create fixture revoke before reservation makes no fake calls",()=>{
                var f=new Fixture();f.Authorize();f.Target.Revoke();Check.Equal(PracticeCreateFixtureState.Revoked,f.Target.State);
                Check.True(!f.Target.Create()&&!f.Target.Append(f.Batch())&&!f.Target.Flush()&&!f.Target.Close());Check.Equal(0,f.Factory.CreateCalls);
            });
            suite.Run("Create fixture revoke inside create retains handle for one cleanup",()=>{
                var f=new Fixture();f.Authorize();f.Factory.OnCreate=()=>f.Target.Revoke();
                Check.True(!f.Target.Create());Check.Equal(PracticeCreateFixtureState.Uncertain,f.Target.State);Check.True(f.Target.CleanupRequired);
                Check.True(!f.Target.Cleanup());Check.True(f.Target.HandleClosed);Check.Equal(1,f.Factory.Result.CloseCalls);
                Check.True(!f.Target.Cleanup()&&!f.Target.Create());Check.Equal(1,f.Factory.CreateCalls);
            });
            suite.Run("Create fixture revoke while writing latches uncertain without flush or retry",()=>{
                var f=new Fixture();f.Open();f.Factory.Result.OnWrite=()=>{
                    var thread=new Thread(()=>f.Target.Revoke());thread.Start();Check.True(thread.Join(3000));};
                Check.True(!f.Target.Append(f.Batch()));Check.Equal(PracticeCreateFixtureState.Uncertain,f.Target.State);
                Check.Equal(1,f.Factory.Result.WriteCalls);Check.Equal(2,f.Factory.Result.AcceptedBytes);
                Check.True(!f.Target.Append(f.Batch())&&!f.Target.Flush());Check.Equal(0,f.Factory.Result.FlushCalls);
                Check.True(!f.Target.Cleanup());Check.True(f.Target.HandleClosed);Check.Equal(1,f.Factory.Result.CloseCalls);
            });
            suite.Run("Create fixture partial or throwing write is uncertain with no retry",()=>{
                foreach(bool partial in new[]{false,true}){
                    var f=new Fixture();f.Open();f.Factory.Result.PartialWrite=partial;f.Factory.Result.FailWrite=!partial;
                    Check.True(!f.Target.Append(f.Batch()));Check.Equal(PracticeCreateFixtureState.Uncertain,f.Target.State);
                    Check.Equal(partial?1:2,f.Factory.Result.AcceptedBytes);Check.True(!f.Target.Append(f.Batch())&&!f.Target.Flush());
                    Check.Equal(1,f.Factory.Result.WriteCalls);Check.True(!f.Target.Cleanup());Check.Equal(1,f.Factory.Result.CloseCalls);
                }
            });
            suite.Run("Create fixture flush failure or revoke is uncertain and not retried",()=>{
                foreach(bool revoke in new[]{false,true}){
                    var f=new Fixture();f.Open();Check.True(f.Target.Append(f.Batch()));
                    if(revoke)f.Factory.Result.OnFlush=()=>f.Target.Revoke();else f.Factory.Result.FailFlush=true;
                    Check.True(!f.Target.Flush());Check.Equal(PracticeCreateFixtureState.Uncertain,f.Target.State);
                    Check.True(!f.Target.Flush()&&!f.Target.Append(f.Batch(1)));Check.Equal(1,f.Factory.Result.FlushCalls);
                    Check.True(!f.Target.Cleanup());Check.Equal(1,f.Factory.Result.CloseCalls);
                }
            });
            suite.Run("Create fixture cleanup may flush and is attempted only once",()=>{
                var f=new Fixture();f.Open();Check.True(f.Target.Append(f.Batch()));int simulatedImplicit=0;
                f.Factory.Result.OnClose=()=>simulatedImplicit++;f.Target.Revoke();Check.True(f.Target.CleanupMayFlush&&f.Target.CleanupRequired);
                Check.True(!f.Target.Cleanup());Check.Equal(1,simulatedImplicit);Check.True(f.Target.HandleClosed&&!f.Target.CleanupRequired);
                Check.Equal(PracticeCreateFixtureState.Uncertain,f.Target.State);Check.True(!f.Target.Cleanup());Check.Equal(1,f.Factory.Result.CloseCalls);
            });
            suite.Run("Create fixture revoke or failure in close never retries close",()=>{
                foreach(bool revoke in new[]{false,true}){
                    var f=new Fixture();f.Open();if(revoke)f.Factory.Result.OnClose=()=>f.Target.Revoke();else f.Factory.Result.FailClose=true;
                    Check.True(!f.Target.Close());Check.Equal(PracticeCreateFixtureState.Uncertain,f.Target.State);Check.True(!f.Target.Cleanup()&&!f.Target.Close());
                    Check.Equal(1,f.Factory.Result.CloseCalls);Check.Equal(revoke,f.Target.HandleClosed);
                }
            });
            suite.Run("Create fixture reentrant calls and foreign owner cannot reserve",()=>{
                var f=new Fixture();f.Open();bool nested=true;f.Factory.Result.OnWrite=()=>nested=f.Target.Append(f.Batch());
                Check.True(!f.Target.Append(f.Batch()));Check.True(!nested);Check.Equal(1,f.Factory.Result.WriteCalls);Check.Equal(PracticeCreateFixtureState.Uncertain,f.Target.State);
                f=new Fixture();f.Open();bool foreign=true;var t=new Thread(()=>foreign=f.Target.Flush());t.Start();Check.True(t.Join(3000));Check.True(!foreign);
                Check.Equal(PracticeCreateFixtureState.Faulted,f.Target.State);Check.Equal(0,f.Factory.Result.FlushCalls);
            });
        }
    }
}
