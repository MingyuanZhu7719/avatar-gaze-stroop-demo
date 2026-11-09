using System;
using System.Reflection;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Hook is called only by an explicitly instrumented isolated source copy.
    // Normal production code never references this type.
    internal static class ViveRetryPublicationTestHook
    {
        internal static Action OnLocked;
        internal static void Invoke(){var action=OnLocked;if(action!=null)action();}
    }
    public static class VivePracticeRetryPublicationInterleavingTests
    {
        const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        sealed class Harness
        {
            readonly object fixture;
            internal Harness()
            {
                var type=typeof(VivePracticeRetryFlowTests).GetNestedType("Fixture",BindingFlags.NonPublic);
                Check.True(type!=null);fixture=Activator.CreateInstance(type,true);
                Invoke("CompleteFirst");
                var task=(ViveDevelopmentParticipant)Get("Task");
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                task.UpdateAuxiliary(false,false,false,0,true);Invoke("IssueRetry");
            }
            internal object Get(string name){var f=fixture.GetType().GetField(name,Hidden);Check.True(f!=null);return f.GetValue(fixture);}
            internal void Invoke(string name,params object[] args)
            {var method=fixture.GetType().GetMethod(name,Hidden);Check.True(method!=null);method.Invoke(fixture,args);}
            internal VivePracticeTaskEventCursor OldCursor {get{return (VivePracticeTaskEventCursor)Get("FirstCursor");}}
            internal VivePracticeOwnedMemoryStart OldHost {get{return (VivePracticeOwnedMemoryStart)Get("First");}}
            internal VivePracticeOwnedMemoryStart NewHost {get{return (VivePracticeOwnedMemoryStart)Get("Second");}}
            internal VivePracticeStartBinding.Ticket Ticket {get{return (VivePracticeStartBinding.Ticket)Get("SecondTicket");}}
            internal VivePracticeConsentAuthority Choices {get{return (VivePracticeConsentAuthority)Get("Choices");}}
            internal ViveExclusiveMemoryStore Store {get{return (ViveExclusiveMemoryStore)Get("Store");}}
            internal VivePracticeStartReservation OldReservation {get{return Field<VivePracticeStartReservation>(OldHost,"reservation");}}
            internal ViveExclusiveMemoryTarget NewTarget {get{return Field<ViveExclusiveMemoryTarget>(NewHost,"target");}}
            internal void Prepare(Func<int,ViveFakeWritePlan> plan){Invoke("BeginSecond",plan);}
            internal bool Start(){return NewHost.TryStart(Ticket);}
        }
        static T Field<T>(object value,string name) where T:class
        {var field=value.GetType().GetField(name,Hidden);Check.True(field!=null);return (T)field.GetValue(value);}
        static void Same(byte[] a,byte[] b)
        {Check.True(a!=null&&b!=null&&a.Length==b.Length);for(int i=0;i<a.Length;i++)Check.Equal(a[i],b[i]);}
        static void WrongThreadFault(VivePracticeStartReservation reservation)
        {bool result=true;var thread=new Thread(()=>result=reservation.TryAccept(null,null)){IsBackground=true};thread.Start();Check.True(thread.Join(5000)&&!result);}
        public static void Register(TestSuite suite)
        {
            suite.Run("retry publication old cursor stop before fake target rejects",()=>{
                var f=new Harness();var first=f.Store.CopyForTest("first");f.Prepare(length=>{f.OldCursor.Stop();return new ViveFakeWritePlan(length,false,null);});
                Check.True(!f.Start());Check.True(f.Store.CopyForTest("second")==null);Same(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry publication old reservation fault before fake target rejects",()=>{
                var f=new Harness();var first=f.Store.CopyForTest("first");f.Prepare(length=>{WrongThreadFault(f.OldReservation);return new ViveFakeWritePlan(length,false,null);});
                Check.True(!f.Start()&&f.OldReservation.IsFaulted);Check.True(f.Store.CopyForTest("second")==null);Same(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry publication revoke before fake target rejects",()=>{
                var f=new Harness();var first=f.Store.CopyForTest("first");f.Prepare(length=>{f.Choices.Withdraw();return new ViveFakeWritePlan(length,false,null);});
                Check.True(!f.Start()&&f.Choices.Revoked);Check.True(f.Store.CopyForTest("second")==null);Same(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry publication old cursor stop during fake write is uncertain",()=>{
                var f=new Harness();var first=f.Store.CopyForTest("first");f.Prepare(length=>new ViveFakeWritePlan(length,false,f.OldCursor.Stop));
                Check.True(!f.Start());Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("second"));
                var second=f.Store.CopyForTest("second");Check.True(!f.Start());Same(second,f.Store.CopyForTest("second"));Same(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry publication partial new fake target remains uncertain",()=>{
                var f=new Harness();var first=f.Store.CopyForTest("first");f.Prepare(length=>new ViveFakeWritePlan(20,true,null));
                Check.True(!f.Start());Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("second"));
                var second=f.Store.CopyForTest("second");Check.Equal(20,second.Length);Check.True(!f.Start());Same(second,f.Store.CopyForTest("second"));Same(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry publication completed before later old cursor stop",()=>{
                var f=new Harness();f.Prepare(null);Check.True(f.Start());VivePracticeOwnedStartReceipt receipt;
                Check.True(f.NewHost.TryTakeReceipt(out receipt)&&receipt!=null);f.OldCursor.Stop();
                Check.True(f.OldCursor.Faulted&&f.NewHost.Outcome==VivePracticeStartReservationOutcome.AcceptedByFake);
            });
        }
        // Only register this against the isolated copy containing the test hook.
        public static void RegisterInstrumented(TestSuite suite)
        {
            for(int index=0;index<4;index++)
            {
                int mode=index;
                suite.Run("INSTRUMENTED publication lock orders competing action "+mode,()=>{
                    var f=new Harness();f.Prepare(null);Thread worker=null;bool attempted=false,blocked=false,joined=false;
                    using(var started=new ManualResetEvent(false))
                    {
                        ViveRetryPublicationTestHook.OnLocked=()=>{
                            Action operation=mode==0?(Action)f.OldCursor.Stop:
                                mode==1?(Action)(()=>f.OldReservation.TryAccept(null,null)):
                                mode==2?(Action)f.Choices.Withdraw:(Action)f.NewTarget.MarkOutcomeUncertain;
                            worker=new Thread(()=>{started.Set();operation();}){IsBackground=true};worker.Start();
                            attempted=started.WaitOne(5000);
                            blocked=attempted&&SpinWait.SpinUntil(()=>
                                (worker.ThreadState&System.Threading.ThreadState.WaitSleepJoin)!=0,1000)&&!worker.Join(0);
                        };
                        bool published;
                        try{published=f.Start();}finally{ViveRetryPublicationTestHook.OnLocked=null;}
                        if(worker!=null)joined=worker.Join(5000);
                        Check.True(published&&attempted&&blocked&&joined,"publication must complete before blocked competing action");
                        if(mode==0)Check.True(f.OldCursor.Faulted);
                        if(mode==1)Check.True(f.OldReservation.IsFaulted);
                        if(mode==2)Check.True(f.Choices.Revoked);
                        if(mode==3)Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("second"));
                        VivePracticeOwnedStartReceipt receipt;
                        bool usable=f.NewHost.TryTakeReceipt(out receipt);
                        if(mode<2)Check.True(usable&&receipt!=null,"a later old-side fault must not rewrite the completed publication");
                        else Check.True(!usable&&receipt==null,"later withdrawal or target failure must invalidate new use");
                    }
                });
            }
        }
    }
}
