using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic target only: no operating-system file, device, network, real
    // participant identity, recording approval or durable output is exercised.
    public static class VivePracticeOwnedMemoryStartTests
    {
        sealed class Clock:IClock
        {
            internal double Value=10;internal Action OnNow,OnId;
            public string ClockId {get{var a=OnId;OnId=null;if(a!=null)a();return "owned-start-clock";}}
            public double Now {get{var a=OnNow;OnNow=null;if(a!=null)a();return Value;}}
        }
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;internal readonly VivePracticeStartBinding Binding;
            internal readonly VivePracticeStartBinding.Ticket Ticket;internal readonly VivePracticeContextV3 Context;
            internal readonly ViveExclusiveMemoryStore Store=new ViveExclusiveMemoryStore(8);
            internal Fixture()
            {
                var config=new SessionConfiguration("owned-start-session",42,
                    new Allocation("owned-start-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"owned-start-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                Binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Clock.Value=11;Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;Check.True(Binding.TryAcquire(1,4096,out ticket));Ticket=ticket;
                Context=MakeContext("owned-start-session","owned-start-clock",1,11);
            }
            internal VivePracticeOwnedMemoryStart Start(string id="target",Func<int,ViveFakeWritePlan> plan=null,VivePracticeContextV3 context=null)
            {return new VivePracticeOwnedMemoryStart(Task,Clock,Choices,Binding,context??Context,Store,id,65536,32,plan);}
        }
        static VivePracticeContextV3 MakeContext(string session,string clock,int attempt,double start)
        {return new VivePracticeContextV3(session,"participant","stream","device",clock,"mapping",attempt,"epoch",0,null,VivePracticeEpochReasonV3.Initial,start);}
        static void Bytes(byte[] expected,byte[] actual)
        {Check.True(expected!=null&&actual!=null&&expected.Length==actual.Length);for(int i=0;i<expected.Length;i++)Check.Equal(expected[i],actual[i]);}
        static void Resume(Fixture f)
        {f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);}
        static VivePracticeMultiReadResult Recover(byte[] bytes){return VivePracticeMultiReader.Read(bytes,true,65536,32);}
        public static void Register(TestSuite suite)
        {
            suite.Run("owned memory start stores the actual ticket's three committed task events",()=>{
                var f=new Fixture();var host=f.Start();Check.True(host.TryStart(f.Ticket));
                Check.Equal(VivePracticeStartReservationOutcome.AcceptedByFake,host.Outcome);
                Check.True(!host.ResearchGateOpen&&!host.RealFileOwnershipProven&&!host.EyeDataIncluded);
                var bytes=f.Store.CopyForTest("target");Check.True(bytes!=null&&bytes.Length>0);
                var result=Recover(bytes);Check.Equal(VivePracticeMultiStatus.Unclean,result.Status);Check.Equal(3,result.Events.Count);
                Check.Equal("practice_started",result.Events[0].Kind);Check.Equal("phase_changed",result.Events[1].Kind);
                Check.Equal("trial_planned",result.Events[2].Kind);Check.Equal(f.Task.CurrentTrial.TrialId,result.Events[2].TrialId);
                Check.True(!result.ConsentAuthenticated&&!result.DurableStorageProven&&!result.FullPracticeCoverageProven);
                Check.True(!host.TryStart(f.Ticket));Bytes(bytes,f.Store.CopyForTest("target"));
            });
            suite.Run("owned memory start never overwrites an existing opaque id",()=>{
                var f=new Fixture();ViveExclusiveMemoryTarget existing;
                Check.Equal(ViveFakeCreateResult.Created,f.Store.TryCreateNew("target",65536,out existing));
                var original=new byte[]{1,2,3};Check.Equal(ViveFakeWriteResult.Accepted,existing.Append(original,new ViveFakeWritePlan(3,false,null)));
                var host=f.Start();Check.True(!host.TryStart(f.Ticket));Bytes(original,f.Store.CopyForTest("target"));
                Check.True(!host.TryStart(f.Ticket));Bytes(original,f.Store.CopyForTest("target"));
            });
            suite.Run("owned memory start requires live ticket and matching participant and context",()=>{
                var f=new Fixture();var other=new Fixture();bool wrongTask=false,wrongSession=false,wrongClock=false;
                try{new VivePracticeOwnedMemoryStart(other.Task,f.Clock,f.Choices,f.Binding,f.Context,f.Store,"wrong",65536,32);}catch(ArgumentException){wrongTask=true;}
                try{f.Start("wrong",null,MakeContext("other","owned-start-clock",1,11));}catch(ArgumentException){wrongSession=true;}
                try{f.Start("wrong",null,MakeContext("owned-start-session","other",1,11));}catch(ArgumentException){wrongClock=true;}
                Check.True(wrongTask&&wrongSession&&wrongClock);
                var mismatched=f.Start("target",null,MakeContext("owned-start-session","owned-start-clock",1,12));
                Check.True(!mismatched.TryStart(f.Ticket));Check.True(f.Store.CopyForTest("target")==null);
                Check.True(!mismatched.TryStart(f.Ticket));
            });
            suite.Run("owned memory start cannot use an old batch or copied byte array as authority",()=>{
                foreach(var method in typeof(VivePracticeOwnedMemoryStart).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
                    if(method.Name=="TryStart")
                    {var args=method.GetParameters();Check.Equal(1,args.Length);Check.Equal(typeof(VivePracticeStartBinding.Ticket),args[0].ParameterType);}
                var f=new Fixture();var host=f.Start();Check.True(host.TryStart(f.Ticket));var old=f.Store.CopyForTest("target");
                var again=f.Start("another");Check.True(!again.TryStart(f.Ticket));Check.True(f.Store.CopyForTest("another")==null);
                Check.True(old!=null);Bytes(old,f.Store.CopyForTest("target"));
            });
            suite.Run("owned memory start does not create a target after withdrawal",()=>{
                var f=new Fixture();var host=f.Start();f.Choices.Withdraw();Check.True(!host.TryStart(f.Ticket));
                Check.True(f.Store.CopyForTest("target")==null&&f.Store.StateForTest("target")==null);
            });
            suite.Run("owned memory start does not create after withdrawal or pause in plan callback",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var f=new Fixture();Action action=mode==0?(Action)f.Choices.Withdraw:mode==1?f.Task.ParticipantPause:
                        (Action)(()=>{f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);});
                    var host=f.Start("target",length=>{action();return new ViveFakeWritePlan(length,false,null);});
                    Check.True(!host.TryStart(f.Ticket));Check.True(f.Store.CopyForTest("target")==null);
                    Check.Equal(VivePracticeStartReservationOutcome.Uncertain,host.Outcome);
                }
            });
            suite.Run("owned memory start retains a partial write and refuses any retry",()=>{
                foreach(int prefix in new[]{0,1,20})
                {
                    var f=new Fixture();var host=f.Start("target",length=>new ViveFakeWritePlan(prefix,true,null));
                    Check.True(!host.TryStart(f.Ticket));Check.Equal(VivePracticeStartReservationOutcome.Uncertain,host.Outcome);
                    var bytes=f.Store.CopyForTest("target");Check.True(bytes!=null&&bytes.Length==prefix);
                    Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                    Check.True(!host.TryStart(f.Ticket));Bytes(bytes,f.Store.CopyForTest("target"));
                    ViveExclusiveMemoryTarget duplicate;Check.Equal(ViveFakeCreateResult.AlreadyExists,f.Store.TryCreateNew("target",65536,out duplicate));
                }
            });
            suite.Run("owned memory start callback withdrawal after full append marks target uncertain",()=>{
                var f=new Fixture();var host=f.Start("target",length=>new ViveFakeWritePlan(length,false,f.Choices.Withdraw));
                Check.True(!host.TryStart(f.Ticket));var bytes=f.Store.CopyForTest("target");Check.True(bytes!=null&&bytes.Length>0);
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                Check.Equal(VivePracticeStartReservationOutcome.Uncertain,host.Outcome);
                // A structurally complete frame does not prove the application
                // choice was still active at the actual commit boundary.
                Check.Equal(3,Recover(bytes).Events.Count);Check.True(!host.TryStart(f.Ticket));Bytes(bytes,f.Store.CopyForTest("target"));
            });
            suite.Run("owned memory start pause and ABA after append preserve but invalidate output",()=>{
                for(int mode=0;mode<2;mode++)
                {
                    var f=new Fixture();Action action=mode==0?(Action)f.Task.ParticipantPause:
                        (Action)(()=>{f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);});
                    var host=f.Start("target",length=>new ViveFakeWritePlan(length,false,action));
                    Check.True(!host.TryStart(f.Ticket));Check.Equal(VivePracticeStartReservationOutcome.Uncertain,host.Outcome);
                    Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                    var bytes=f.Store.CopyForTest("target");Check.True(bytes!=null&&bytes.Length>0);Check.True(!host.TryStart(f.Ticket));
                    Bytes(bytes,f.Store.CopyForTest("target"));
                }
            });
            suite.Run("owned memory start exception after prefix preserves uncertain evidence",()=>{
                var f=new Fixture();var host=f.Start("target",length=>new ViveFakeWritePlan(20,false,()=>{throw new InvalidOperationException();}));
                Check.True(!host.TryStart(f.Ticket));var bytes=f.Store.CopyForTest("target");Check.True(bytes!=null&&bytes.Length==20);
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                Check.True(!host.TryStart(f.Ticket));Bytes(bytes,f.Store.CopyForTest("target"));
            });
            suite.Run("owned memory start cross-thread withdrawal cannot assert a clean output",()=>{
                var f=new Fixture();Exception error=null;bool joined=false;
                var host=f.Start("target",length=>new ViveFakeWritePlan(length,false,()=>{
                    var thread=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(Exception e){error=e;}}){IsBackground=true};
                    thread.Start();joined=thread.Join(5000);
                }));
                Check.True(!host.TryStart(f.Ticket)&&joined&&error is InvalidOperationException&&f.Choices.Revoked);
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                var bytes=f.Store.CopyForTest("target");Check.True(bytes!=null&&bytes.Length>0);Check.True(!host.TryStart(f.Ticket));
                f.Task.ParticipantWithdraw();Check.Equal(Phase.Ended,f.Task.Phase);
            });
            suite.Run("owned memory start keeps target and writer private",()=>{
                var fields=typeof(VivePracticeOwnedMemoryStart).GetFields(BindingFlags.Public|BindingFlags.Instance);
                foreach(var field in fields)Check.True(field.FieldType!=typeof(ViveExclusiveMemoryTarget)&&field.FieldType!=typeof(VivePracticeMultiWriter)&&!typeof(Stream).IsAssignableFrom(field.FieldType));
                foreach(var prop in typeof(VivePracticeOwnedMemoryStart).GetProperties(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
                    Check.True(prop.PropertyType!=typeof(ViveExclusiveMemoryTarget)&&prop.PropertyType!=typeof(VivePracticeMultiWriter)&&!typeof(Stream).IsAssignableFrom(prop.PropertyType));
                foreach(var field in typeof(VivePracticeOwnedStartReceipt).GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
                {
                    Check.True(field.FieldType!=typeof(ViveExclusiveMemoryTarget)&&field.FieldType!=typeof(VivePracticeMultiWriter)&&!typeof(Stream).IsAssignableFrom(field.FieldType),"receipt must not expose a target or mutable writer");
                    Check.True(field.IsInitOnly,"receipt must not expose mutable state");
                }
                foreach(var prop in typeof(VivePracticeOwnedStartReceipt).GetProperties(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
                    Check.True(prop.PropertyType!=typeof(ViveExclusiveMemoryTarget)&&prop.PropertyType!=typeof(VivePracticeMultiWriter)&&!typeof(Stream).IsAssignableFrom(prop.PropertyType));
            });
            suite.Run("owned memory start receipt is bound to its host and claimable once",()=>{
                var f=new Fixture();var host=f.Start();Check.True(host.TryStart(f.Ticket));VivePracticeOwnedStartReceipt receipt;
                Check.True(host.TryTakeReceipt(out receipt)&&receipt!=null);VivePracticeOwnedStartReceipt again;
                Check.True(!host.TryTakeReceipt(out again)&&again==null);
                var other=new Fixture();var otherHost=other.Start("other");
                Check.True(!otherHost.TryTakeReceipt(out again));
                VivePracticeCursorAnchor anchor;
                Check.True(!otherHost.TryConsumeForCursor(receipt,out anchor)&&anchor==null);
                var copied=new VivePracticeOwnedStartReceipt(receipt.Issuer,receipt.Participant,receipt.Choices,receipt.Clock,
                    receipt.TargetReceipt,receipt.Batch,receipt.ChoiceGeneration);
                var wrongTask=new VivePracticeOwnedStartReceipt(receipt.Issuer,other.Task,receipt.Choices,receipt.Clock,
                    receipt.TargetReceipt,receipt.Batch,receipt.ChoiceGeneration);
                var wrongClock=new VivePracticeOwnedStartReceipt(receipt.Issuer,receipt.Participant,receipt.Choices,other.Clock,
                    receipt.TargetReceipt,receipt.Batch,receipt.ChoiceGeneration);
                var wrongChoices=new VivePracticeOwnedStartReceipt(receipt.Issuer,receipt.Participant,other.Choices,receipt.Clock,
                    receipt.TargetReceipt,receipt.Batch,receipt.ChoiceGeneration);
                var wrongGeneration=new VivePracticeOwnedStartReceipt(receipt.Issuer,receipt.Participant,receipt.Choices,receipt.Clock,
                    receipt.TargetReceipt,receipt.Batch,receipt.ChoiceGeneration+1);
                var clonedBatch=new VivePracticeStartBatch(receipt.Batch.SessionId,receipt.Batch.ClockId,receipt.Batch.Attempt,
                    receipt.Batch.Revision,receipt.Batch.Events);
                var wrongBatch=new VivePracticeOwnedStartReceipt(receipt.Issuer,receipt.Participant,receipt.Choices,receipt.Clock,
                    receipt.TargetReceipt,clonedBatch,receipt.ChoiceGeneration);
                var clonedTarget=new ViveExclusiveMemoryTarget.Receipt(receipt.TargetReceipt.Ordinal,receipt.TargetReceipt.TotalLength,
                    receipt.TargetReceipt.CopyPayloadDigest());
                var wrongTarget=new VivePracticeOwnedStartReceipt(receipt.Issuer,receipt.Participant,receipt.Choices,receipt.Clock,
                    clonedTarget,receipt.Batch,receipt.ChoiceGeneration);
                foreach(var forged in new[]{copied,wrongTask,wrongClock,wrongChoices,wrongGeneration,wrongBatch,wrongTarget})
                    Check.True(!host.TryConsumeForCursor(forged,out anchor)&&anchor==null);
                Check.True(host.TryConsumeForCursor(receipt,out anchor)&&anchor!=null);
                Check.True(Object.ReferenceEquals(anchor.Issuer,host)&&Object.ReferenceEquals(anchor.Participant,f.Task)&&
                    Object.ReferenceEquals(anchor.Clock,f.Clock)&&Object.ReferenceEquals(anchor.Choices,f.Choices)&&
                    Object.ReferenceEquals(anchor.Context,f.Context));
                Check.Equal(receipt.ChoiceGeneration,anchor.ChoiceGeneration);Check.Equal(receipt.Batch.Revision,anchor.StartRevision);
                Check.Equal(receipt.Batch.Events[2].Sequence,anchor.StartSequence);Check.Equal(receipt.Batch.Events[2].Time,anchor.StartTime);
                Check.True(host.IsCurrentForCursor(anchor));
                var clonedAnchor=new VivePracticeCursorAnchor(anchor.Issuer,anchor.Participant,anchor.Clock,anchor.Choices,anchor.ChoiceGeneration,
                    anchor.StartSequence,anchor.StartRevision,anchor.StartTime,anchor.StartSnapshot,anchor.StartTrace,anchor.Context);
                Check.True(!host.IsCurrentForCursor(clonedAnchor)&&host.IsCurrentForCursor(anchor));
                Check.True(!host.TryConsumeForCursor(receipt,out anchor)&&anchor==null);
            });
            suite.Run("owned memory start receipt cannot be claimed after withdrawal or task change",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var f=new Fixture();var host=f.Start();Check.True(host.TryStart(f.Ticket));
                    if(mode==0)f.Choices.Withdraw();else if(mode==1)f.Task.ParticipantPause();
                    else{f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);}
                    VivePracticeOwnedStartReceipt receipt;Check.True(!host.TryTakeReceipt(out receipt)&&receipt==null);
                }
            });
            suite.Run("owned memory start receipt cannot be consumed after withdrawal or task change",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var f=new Fixture();var host=f.Start();Check.True(host.TryStart(f.Ticket));VivePracticeOwnedStartReceipt receipt;
                    Check.True(host.TryTakeReceipt(out receipt));
                    if(mode==0)f.Choices.Withdraw();else if(mode==1)f.Task.ParticipantPause();
                    else{f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);}
                    VivePracticeCursorAnchor anchor;Check.True(!host.TryConsumeForCursor(receipt,out anchor)&&anchor==null);
                }
            });
            suite.Run("owned memory start rejects recursive host entry before target creation",()=>{
                var f=new Fixture();VivePracticeOwnedMemoryStart host=null;bool nested=true;
                host=f.Start("target",length=>{nested=host.TryStart(f.Ticket);return new ViveFakeWritePlan(length,false,null);});
                Check.True(!host.TryStart(f.Ticket)&&!nested);Check.True(f.Store.CopyForTest("target")==null);
                Check.Equal(VivePracticeStartReservationOutcome.Uncertain,host.Outcome);
            });
            suite.Run("owned memory start refuses a foreign live reservation guard",()=>{
                var f=new Fixture();var other=new Fixture();var host=f.Start();bool foreignResult=true,foreignInvoked=false;
                var otherReservation=new VivePracticeStartReservation(other.Task,other.Clock,other.Choices,other.Binding);
                var foreignGuard=new VivePracticeStartReservation.OperationGuard(otherReservation,null,null);
                f.Clock.OnNow=()=>{foreignInvoked=true;foreignResult=host.AcceptSynthetic(f.Ticket.Batch,foreignGuard);};
                Check.True(!host.TryStart(f.Ticket));Check.True(foreignInvoked&&!foreignResult);
                Check.True(f.Store.CopyForTest("target")==null);
            });
        }
    }
}
