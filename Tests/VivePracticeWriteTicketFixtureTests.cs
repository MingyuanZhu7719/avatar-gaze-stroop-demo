using System;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic model only: no real stream, gaze, session, authority, or device.
    public static class VivePracticeWriteTicketFixtureTests
    {
        sealed class Fixture
        {
            internal readonly PracticeFixtureClock Clock=new PracticeFixtureClock(1);
            internal readonly PracticeFixtureContext Context=new PracticeFixtureContext("fixture-session","fixture-person","fixture-stream","fixture-device","fixture-clock","fixture-map","fixture-epoch",1,0);
            internal readonly PracticeFixtureSink Sink=new PracticeFixtureSink();internal readonly PracticeFixtureCoordinator Coordinator;
            internal Fixture(){Coordinator=new PracticeFixtureCoordinator(Context,Clock,Sink,0.2);}
            internal void Choices(){Check.True(Coordinator.ChooseEye());Check.True(Coordinator.ChooseTask());}
            internal PracticeFixtureTicket Ticket(){Clock.Set(1.15);return Coordinator.Issue(new PracticeFixtureFrame(Context,1,1.1,true,42));}
            internal void Commit(){Choices();var t=Ticket();Check.True(t!=null);Check.Equal(PracticeFixtureCommit.MemoryCommitted,Coordinator.Commit(t));}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("Practice fixture requires both synthetic choices and a later valid frame",()=>{
                var f=new Fixture();f.Clock.Set(1.15);var frame=new PracticeFixtureFrame(f.Context,1,1.1,true,42);
                Check.True(f.Coordinator.Issue(frame)==null);Check.True(f.Coordinator.ChooseEye());Check.True(f.Coordinator.Issue(frame)==null);
                Check.True(f.Coordinator.ChooseTask());Check.True(f.Coordinator.Issue(frame)==null);f.Clock.Set(1.2);
                Check.True(f.Coordinator.Issue(new PracticeFixtureFrame(f.Context,1,1.16,false,42))==null);
                Check.True(f.Coordinator.Issue(new PracticeFixtureFrame(f.Context,1,1.16,true,42))!=null);
                Check.True(f.Coordinator.SyntheticFixtureOnly&&!f.Coordinator.ProductionGateOpen&&!f.Coordinator.ConsentAuthenticated&&!f.Coordinator.DeviceAuthenticated&&!f.Coordinator.DurabilityProven);
            });
            suite.Run("Practice fixture commits a synthetic byte then simulates write and flush",()=>{
                var f=new Fixture();f.Commit();Check.Equal((byte)42,f.Coordinator.SyntheticMemoryMarker);
                Check.True(f.Coordinator.BeginSyntheticWrite());f.Coordinator.CompleteSyntheticWrite(PracticeFixtureIoResult.Complete);
                Check.True(f.Coordinator.BeginSyntheticFlush());f.Coordinator.CompleteSyntheticFlush(PracticeFixtureIoResult.Complete);
                Check.Equal(PracticeFixtureState.SyntheticComplete,f.Coordinator.State);Check.Equal(1,f.Sink.WriteStarts);Check.Equal(1,f.Sink.FlushStarts);
                Check.True(!f.Coordinator.BeginSyntheticWrite()&&!f.Coordinator.BeginSyntheticFlush());Check.True(!f.Coordinator.DurabilityProven);
            });
            suite.Run("Practice fixture ticket cannot replay or cross coordinators",()=>{
                var a=new Fixture();var b=new Fixture();a.Choices();b.Choices();var t=a.Ticket();Check.True(t!=null);
                Check.Equal(PracticeFixtureCommit.RejectedBeforeCommit,b.Coordinator.Commit(t));Check.Equal(PracticeFixtureCommit.RejectedBeforeCommit,a.Coordinator.Commit(t));
                Check.True(!a.Coordinator.HasSyntheticMemory&&!b.Coordinator.HasSyntheticMemory);
            });
            suite.Run("Practice fixture rejects copied context and overlapping tickets",()=>{
                var f=new Fixture();f.Choices();f.Clock.Set(1.15);
                var other=new PracticeFixtureContext("fixture-session","fixture-person","fixture-stream","fixture-device","fixture-clock","fixture-map","fixture-epoch",1,0);
                Check.True(f.Coordinator.Issue(new PracticeFixtureFrame(other,1,1.1,true,42))==null);var t=f.Ticket();Check.True(t!=null);Check.True(f.Ticket()==null);
                Check.Equal(PracticeFixtureCommit.MemoryCommitted,f.Coordinator.Commit(t));Check.Equal(PracticeFixtureCommit.RejectedBeforeCommit,f.Coordinator.Commit(t));
            });
            suite.Run("Practice fixture rejects a cloned ticket without consuming the original",()=>{
                var f=new Fixture();f.Choices();var t=f.Ticket();Check.True(t!=null);
                var clone=new PracticeFixtureTicket(t.Issuer,t.Frame,t.Head,t.Lease,t.Writer,t.TaskGeneration,t.ConsentGeneration,t.SourceGeneration);
                Check.Equal(PracticeFixtureCommit.RejectedBeforeCommit,f.Coordinator.Commit(clone));Check.True(!f.Coordinator.HasSyntheticMemory);
                Check.Equal(PracticeFixtureCommit.MemoryCommitted,f.Coordinator.Commit(t));
            });
            suite.Run("Practice fixture task change and revert invalidate ticket",()=>{
                var f=new Fixture();f.Choices();var t=f.Ticket();Check.True(f.Coordinator.ChangeTask(false));Check.True(f.Coordinator.ChangeTask(true));
                Check.Equal(PracticeFixtureCommit.RejectedBeforeCommit,f.Coordinator.Commit(t));Check.True(!f.Coordinator.HasSyntheticMemory);
            });
            suite.Run("Practice fixture lease loss and restoration invalidate ticket",()=>{
                var f=new Fixture();f.Choices();var t=f.Ticket();Check.True(f.Coordinator.ChangeSource(false,false));Check.True(f.Coordinator.ChangeSource(true,true));
                Check.Equal(PracticeFixtureCommit.RejectedBeforeCommit,f.Coordinator.Commit(t));Check.True(!f.Coordinator.HasSyntheticMemory);
            });
            suite.Run("Practice fixture rejects expired and clock rollback tickets",()=>{
                var f=new Fixture();f.Choices();var t=f.Ticket();f.Clock.Set(1.31);Check.Equal(PracticeFixtureCommit.RejectedBeforeCommit,f.Coordinator.Commit(t));Check.True(!f.Coordinator.HasSyntheticMemory);
                f=new Fixture();f.Choices();t=f.Ticket();f.Clock.Set(1.05);Check.Equal(PracticeFixtureCommit.RejectedBeforeCommit,f.Coordinator.Commit(t));Check.Equal(PracticeFixtureState.Faulted,f.Coordinator.State);
            });
            suite.Run("Practice fixture wrong-thread consume faults and consumes once",()=>{
                var f=new Fixture();f.Choices();var t=f.Ticket();PracticeFixtureCommit result=PracticeFixtureCommit.MemoryCommitted;
                var thread=new Thread(()=>result=f.Coordinator.Commit(t));thread.Start();Check.True(thread.Join(3000));
                Check.Equal(PracticeFixtureCommit.RejectedBeforeCommit,result);Check.Equal(PracticeFixtureState.Faulted,f.Coordinator.State);
                Check.Equal(PracticeFixtureCommit.RejectedBeforeCommit,f.Coordinator.Commit(t));Check.True(!f.Coordinator.HasSyntheticMemory);
            });
            suite.Run("Practice fixture revoke before commit or write prohibits new I/O",()=>{
                var f=new Fixture();f.Choices();var t=f.Ticket();f.Coordinator.Revoke();Check.Equal(PracticeFixtureCommit.RejectedBeforeCommit,f.Coordinator.Commit(t));
                Check.True(!f.Coordinator.BeginSyntheticWrite());Check.Equal(0,f.Sink.WriteStarts);Check.Equal(0,f.Sink.FlushStarts);
                f=new Fixture();f.Commit();f.Coordinator.Revoke();Check.Equal(PracticeFixtureState.Revoked,f.Coordinator.State);
                Check.True(!f.Coordinator.BeginSyntheticWrite()&&!f.Coordinator.BeginSyntheticFlush());Check.Equal(0,f.Sink.WriteStarts);
            });
            suite.Run("Practice fixture revoke during write is immediate and uncertain",()=>{
                var f=new Fixture();f.Commit();Check.True(f.Coordinator.BeginSyntheticWrite());
                var thread=new Thread(()=>f.Coordinator.Revoke());thread.Start();Check.True(thread.Join(3000));
                f.Coordinator.CompleteSyntheticWrite(PracticeFixtureIoResult.Complete);Check.Equal(PracticeFixtureState.Uncertain,f.Coordinator.State);
                Check.True(!f.Coordinator.BeginSyntheticWrite()&&!f.Coordinator.BeginSyntheticFlush());Check.Equal(1,f.Sink.WriteStarts);Check.Equal(0,f.Sink.FlushStarts);
            });
            suite.Run("Practice fixture revoke after write or during flush blocks further I/O",()=>{
                var f=new Fixture();f.Commit();Check.True(f.Coordinator.BeginSyntheticWrite());f.Coordinator.CompleteSyntheticWrite(PracticeFixtureIoResult.Complete);
                f.Coordinator.Revoke();Check.Equal(PracticeFixtureState.Uncertain,f.Coordinator.State);Check.True(!f.Coordinator.BeginSyntheticFlush());Check.Equal(0,f.Sink.FlushStarts);
                f=new Fixture();f.Commit();Check.True(f.Coordinator.BeginSyntheticWrite());f.Coordinator.CompleteSyntheticWrite(PracticeFixtureIoResult.Complete);Check.True(f.Coordinator.BeginSyntheticFlush());
                f.Coordinator.Revoke();f.Coordinator.CompleteSyntheticFlush(PracticeFixtureIoResult.Complete);Check.Equal(PracticeFixtureState.Uncertain,f.Coordinator.State);Check.Equal(1,f.Sink.FlushStarts);
            });
            suite.Run("Practice fixture partial write or failed flush is never retried",()=>{
                var f=new Fixture();f.Commit();Check.True(f.Coordinator.BeginSyntheticWrite());f.Coordinator.CompleteSyntheticWrite(PracticeFixtureIoResult.Partial);
                Check.Equal(PracticeFixtureState.Uncertain,f.Coordinator.State);Check.True(!f.Coordinator.BeginSyntheticWrite()&&!f.Coordinator.BeginSyntheticFlush());Check.Equal(1,f.Sink.WriteStarts);
                f=new Fixture();f.Commit();Check.True(f.Coordinator.BeginSyntheticWrite());f.Coordinator.CompleteSyntheticWrite(PracticeFixtureIoResult.Complete);Check.True(f.Coordinator.BeginSyntheticFlush());
                f.Coordinator.CompleteSyntheticFlush(PracticeFixtureIoResult.Failed);Check.Equal(PracticeFixtureState.Uncertain,f.Coordinator.State);Check.True(!f.Coordinator.BeginSyntheticFlush());Check.Equal(1,f.Sink.FlushStarts);
            });
            suite.Run("Practice fixture task or source changes prevent pending I/O",()=>{
                var f=new Fixture();f.Commit();Check.True(f.Coordinator.ChangeTask(false));Check.Equal(PracticeFixtureState.Invalidated,f.Coordinator.State);Check.True(!f.Coordinator.BeginSyntheticWrite());
                f=new Fixture();f.Commit();Check.True(f.Coordinator.BeginSyntheticWrite());Check.True(f.Coordinator.ChangeSource(false,false));
                Check.Equal(PracticeFixtureState.Uncertain,f.Coordinator.State);Check.True(!f.Coordinator.BeginSyntheticFlush());
            });
        }
    }
}
