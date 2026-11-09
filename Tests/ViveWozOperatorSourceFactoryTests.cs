using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveWozOperatorSourceFactoryTests
    {
        sealed class Provider : IViveProEyeProvider
        {public int Calls;public ViveProEyePoll Poll(){Calls++;return null;}}
        static TaskSnapshot Task(int block=0,int segment=0,Condition condition=Condition.A,Phase phase=Phase.MainTask,string session="woz-session")
        {return new TaskSnapshot(session,block,segment,"trial",Colour.Red,Colour.Blue,phase,condition);}
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE WoZ operator source factory is explicitly configured and dormant",()=>{
                var clock=new ManualClock();var provider=new Provider();var mapper=new ViveProEyeRehearsalCoordinates();
                Check.Throws<InvalidOperationException>(()=>new ViveWozOperatorSourceFactory("woz-session","epoch",clock,provider,mapper,.1,false));
                var factory=new ViveWozOperatorSourceFactory("woz-session","epoch",clock,provider,mapper,.1,true);
                Check.Equal(ViveDevelopmentTransport.OperatorStream(mapper.Version,"epoch"),factory.StreamId);
                Check.Equal("vive-pro-eye-no-serial",factory.DeviceId);
                Check.True(!factory.CanRecordOrExport&&!factory.HardwareCaptureTimeVerified&&!factory.MappingValidatedForApparatus);
                var source=factory.Create(Task());Check.Equal(0,provider.Calls);Check.Equal(factory.StreamId,source.StreamId);
                Check.Equal(ControlSource.LiveOperator,source.Source);Check.Equal(StreamRole.OperatorControl,source.Role);
                Check.True(Object.ReferenceEquals(source,factory.Create(Task())));Check.Equal(0,provider.Calls);
            });
            suite.Run("VIVE WoZ operator source factory fails closed for invalid or regressed task contexts",()=>{
                var clock=new ManualClock();var provider=new Provider();var factory=new ViveWozOperatorSourceFactory("woz-session","epoch",clock,provider,new ViveProEyeRehearsalCoordinates(),.1,true);
                Check.Throws<InvalidOperationException>(()=>factory.Create(null));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(session:"wrong-session")));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(phase:Phase.BlockReady)));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(condition:Condition.D)));
                var first=factory.Create(Task(0,0));var resumed=factory.Create(Task(0,1));Check.True(!Object.ReferenceEquals(first,resumed));
                Check.True(Object.ReferenceEquals(resumed,factory.Create(Task(0,1))));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(0,1,condition:Condition.B)));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(0,2,condition:Condition.B)));
                var later=factory.Create(Task(2,0));Check.True(!Object.ReferenceEquals(later,resumed));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(1,0)));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(2,0,condition:Condition.D)));
                Check.Equal(0,provider.Calls);
            });
            suite.Run("VIVE WoZ operator source factory enforces its owning thread",()=>{
                var factory=new ViveWozOperatorSourceFactory("woz-session","epoch",new ManualClock(),new Provider(),new ViveProEyeRehearsalCoordinates(),.1,true);
                Exception failure=null;var thread=new Thread(()=>{try{factory.Create(Task());}catch(Exception error){failure=error;}});
                thread.Start();Check.True(thread.Join(3000));Check.True(failure is InvalidOperationException);
                Check.True(factory.Create(Task())!=null);
            });
            suite.Run("VIVE WoZ operator source factory latches local clock rollback",()=>{
                var clock=new ManualClock {Now=1};var factory=new ViveWozOperatorSourceFactory("woz-session","epoch",clock,new Provider(),new ViveProEyeRehearsalCoordinates(),.1,true);
                Check.True(factory.Create(Task())!=null);clock.Now=.5;
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task()));clock.Now=2;
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(1,0)));
            });
        }
    }
}
