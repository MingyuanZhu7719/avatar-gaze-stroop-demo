using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Actual observed Simulation participant; staging and decoding use only
    // private synthetic memory and never touch a device, network or file.
    public static class VivePracticeStartMemoryStageTests
    {
        sealed class Clock:IClock
        {
            internal double Value=10;internal Action OnNow,OnId;
            public string ClockId {get{var a=OnId;OnId=null;if(a!=null)a();return "memory-stage-clock";}}
            public double Now {get{var a=OnNow;OnNow=null;if(a!=null)a();return Value;}}
        }
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;internal readonly VivePracticeStartBinding Binding;
            internal readonly VivePracticeStartBinding.Ticket Ticket;internal readonly VivePracticeContextV3 Context;
            internal Fixture()
            {
                var config=new SessionConfiguration("memory-stage-session",42,
                    new Allocation("memory-stage-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"memory-stage-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                Binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Clock.Value=11;Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;Check.True(Binding.TryAcquire(1,4096,out ticket));Ticket=ticket;
                Context=MakeContext("memory-stage-session","memory-stage-clock",1,11);
            }
            internal VivePracticeStartMemoryStage Stage(VivePracticeContextV3 context=null,int maximum=65536)
            {return new VivePracticeStartMemoryStage(Task,Clock,Choices,Binding,context??Context,maximum);}
        }
        static VivePracticeContextV3 MakeContext(string session,string clock,int attempt,double start)
        {return new VivePracticeContextV3(session,"participant","stream","device",clock,"mapping",attempt,"epoch",0,null,VivePracticeEpochReasonV3.Initial,start);}
        static void Resume(Fixture f)
        {
            f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
            f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);
        }
        static VivePracticeStartEnvelopeReadResult Decode(byte[] bytes)
        {using(var stream=new MemoryStream(bytes,false))return VivePracticeStartEnvelopeCodec.Decode(stream,65536);}
        sealed class PendingSink:IVivePracticeStartFakeSink
        {
            internal VivePracticeContextV3 Context;internal VivePracticeMemoryPublication Publication;internal Action AfterEncode;internal int Calls;
            public bool AcceptSynthetic(VivePracticeStartBatch batch)
            {
                Calls++;Publication.Pending=VivePracticeStartEnvelopeCodec.Encode(Context,batch,65536);
                if(AfterEncode!=null)AfterEncode();return true;
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("practice start memory stage produces one complete defensive historical envelope",()=>{
                var f=new Fixture();var stage=f.Stage();Check.True(stage.TryStage(f.Ticket));
                Check.Equal(VivePracticeStartReservationOutcome.AcceptedByFake,stage.Outcome);
                Check.True(!stage.CanWrite&&!stage.IncludesEyeData&&!stage.ResearchGateOpen);
                var original=stage.CopyEnvelopeForTest();Check.True(original!=null&&original.Length>1);
                var copy=stage.CopyEnvelopeForTest();Check.True(!Object.ReferenceEquals(original,copy));copy[0]^=0xff;
                var fresh=stage.CopyEnvelopeForTest();Check.Equal(original[0],fresh[0]);
                var result=Decode(fresh);Check.Equal(VivePracticeStartEnvelopeStatus.Complete,result.Status);
                Check.Equal(3,result.Events.Count);Check.Equal("practice_started",result.Events[0].Kind);
                Check.Equal("phase_changed",result.Events[1].Kind);Check.Equal("trial_planned",result.Events[2].Kind);
                Check.Equal(f.Task.CurrentTrial.TrialId,result.Events[2].TrialId);
                Check.True(!result.ConsentAuthenticated&&!result.IdentityAuthenticated&&!result.DurableStorageProven);
                Check.True(!stage.TryStage(f.Ticket));Check.True(!stage.TryStage(f.Ticket));
            });
            suite.Run("practice start memory stage requires complete commit and explicit subsequent prefix",()=>{
                var f=new Fixture();var stage=f.Stage();Check.True(stage.TryStage(f.Ticket));var bytes=stage.CopyEnvelopeForTest();
                var shortBytes=new byte[bytes.Length-1];Buffer.BlockCopy(bytes,0,shortBytes,0,shortBytes.Length);
                var incomplete=Decode(shortBytes);Check.Equal(VivePracticeStartEnvelopeStatus.Incomplete,incomplete.Status);Check.Equal(0,incomplete.Events.Count);
                var joined=new byte[bytes.Length*2];Buffer.BlockCopy(bytes,0,joined,0,bytes.Length);Buffer.BlockCopy(bytes,0,joined,bytes.Length,bytes.Length);
                var strict=Decode(joined);Check.Equal(VivePracticeStartEnvelopeStatus.Invalid,strict.Status);Check.Equal(0,strict.Events.Count);
                var first=VivePracticeStartEnvelopeCodec.DecodePrefix(joined,0,joined.Length,65536);
                Check.Equal(VivePracticeStartEnvelopeStatus.Complete,first.Status);Check.Equal(bytes.Length,first.ConsumedBytes);
                var second=VivePracticeStartEnvelopeCodec.DecodePrefix(joined,first.ConsumedBytes,joined.Length-first.ConsumedBytes,65536);
                Check.Equal(VivePracticeStartEnvelopeStatus.Complete,second.Status);Check.Equal(3,second.Events.Count);
                // A second parsable prefix does not prove legitimate epoch continuity.
            });
            suite.Run("practice start memory stage rejects context object mismatches",()=>{
                var f=new Fixture();bool session=false,clock=false,attempt=false;
                try{f.Stage(MakeContext("other-session","memory-stage-clock",1,11));}catch(ArgumentException){session=true;}
                try{f.Stage(MakeContext("memory-stage-session","other-clock",1,11));}catch(ArgumentException){clock=true;}
                try{f.Stage(MakeContext("memory-stage-session","memory-stage-clock",2,11));}catch(ArgumentException){attempt=true;}
                Check.True(session&&clock&&attempt);
                var other=new Fixture();bool foreign=false;
                try{new VivePracticeStartMemoryStage(other.Task,f.Clock,f.Choices,f.Binding,f.Context,65536);}
                catch(ArgumentException){foreign=true;}Check.True(foreign);
            });
            suite.Run("practice start memory stage wrong start or bound exposes no partial envelope",()=>{
                for(int mode=0;mode<2;mode++)
                {
                    var f=new Fixture();var stage=mode==0?f.Stage(MakeContext("memory-stage-session","memory-stage-clock",1,12)):f.Stage(null,256);
                    Check.True(!stage.TryStage(f.Ticket));Check.True(stage.CopyEnvelopeForTest()==null);
                    Check.True(!stage.TryStage(f.Ticket));Check.True(stage.CopyEnvelopeForTest()==null);
                }
            });
            suite.Run("practice start memory stage rejects withdrawal pause and ABA",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var f=new Fixture();var stage=f.Stage();
                    if(mode==0)f.Choices.Withdraw();else if(mode==1)f.Task.ParticipantPause();
                    else{f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);}
                    Check.True(!stage.TryStage(f.Ticket));Check.True(stage.CopyEnvelopeForTest()==null);
                }
            });
            suite.Run("practice start memory stage rejects withdrawal or task mutation in clock callback",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var f=new Fixture();var stage=f.Stage();if(mode==0)f.Clock.OnNow=f.Choices.Withdraw;
                    else if(mode==1)f.Clock.OnNow=f.Task.ParticipantPause;else f.Clock.OnId=f.Task.ParticipantWithdraw;
                    Check.True(!stage.TryStage(f.Ticket));Check.True(stage.CopyEnvelopeForTest()==null);
                }
            });
            suite.Run("practice start memory publication never commits after in-flight withdrawal or task change",()=>{
                for(int mode=0;mode<4;mode++)
                {
                    var f=new Fixture();var publication=new VivePracticeMemoryPublication();
                    var reservation=new VivePracticeStartReservation(f.Task,f.Clock,f.Choices,f.Binding);
                    var sink=new PendingSink{Context=f.Context,Publication=publication};bool joined=true;
                    if(mode==0)sink.AfterEncode=f.Choices.Withdraw;
                    else if(mode==1)sink.AfterEncode=f.Task.ParticipantPause;
                    else if(mode==2)sink.AfterEncode=()=>{f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);};
                    else sink.AfterEncode=()=>{var thread=new Thread(f.Choices.Withdraw){IsBackground=true};thread.Start();joined=thread.Join(5000);};
                    Check.True(!reservation.TryAcceptMemory(f.Ticket,sink,publication));Check.Equal(1,sink.Calls);Check.True(joined);
                    Check.True(publication.Pending!=null&&publication.Committed==null);
                    Check.Equal(VivePracticeStartReservationOutcome.Uncertain,reservation.Outcome);
                    Check.True(!reservation.TryAcceptMemory(f.Ticket,sink,publication));Check.Equal(1,sink.Calls);
                }
            });
            suite.Run("practice start memory stage withdraw denies new copies but cannot recall an old copy",()=>{
                var f=new Fixture();var stage=f.Stage();Check.True(stage.TryStage(f.Ticket));
                var oldCopy=stage.CopyEnvelopeForTest();Check.True(oldCopy!=null);
                f.Choices.Withdraw();Check.True(stage.CopyEnvelopeForTest()==null);
                Check.Equal(VivePracticeStartEnvelopeStatus.Complete,Decode(oldCopy).Status);
            });
            suite.Run("practice start memory stage refuses foreign thread and preserves safety",()=>{
                var f=new Fixture();var stage=f.Stage();bool result=true;
                var thread=new Thread(()=>result=stage.TryStage(f.Ticket)){IsBackground=true};thread.Start();Check.True(thread.Join(5000));
                Check.True(!result&&stage.CopyEnvelopeForTest()==null&&!stage.TryStage(f.Ticket));
                f.Task.ParticipantWithdraw();Check.Equal(Phase.Ended,f.Task.Phase);
            });
        }
    }
}
