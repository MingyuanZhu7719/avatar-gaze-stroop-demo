using System;
using System.IO;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Closed synthetic test seam. No implementation below opens a path,
    // exposes a Stream, handles participant data, or creates a real file.
    // Closed means only fake close returned; it is not session completeness,
    // consent authentication, physical durability or research acceptance.
    internal enum PracticeCreateFixtureState { New,Creating,Open,Writing,Flushing,Closing,Closed,Revoked,Uncertain,Faulted }
    internal sealed class PracticeCreateFixtureDirectory
    {
        internal readonly string Token;
        internal PracticeCreateFixtureDirectory(string token)
        {if(String.IsNullOrWhiteSpace(token)||token.IndexOfAny(new[]{'\\','/',':'})>=0)throw new ArgumentException("Opaque fixture directory required.");Token=token;}
    }
    internal sealed class PracticeCreateFixtureBatch
    {
        internal readonly object Context;internal readonly int Sequence;internal readonly byte Marker;
        internal PracticeCreateFixtureBatch(object context,int sequence,byte marker)
        {Context=context;Sequence=sequence;Marker=marker;}
    }
    // Hooks simulate reentrant and competing operations only. They do not run
    // under the broker state lock and none may call the filesystem.
    internal sealed class PracticeCreateFakeHandle
    {
        internal Action OnWrite {get;set;} internal Action OnFlush {get;set;} internal Action OnClose {get;set;}
        internal bool FailWrite {get;set;} internal bool PartialWrite {get;set;} internal bool FailFlush {get;set;} internal bool FailClose {get;set;}
        internal int WriteCalls,FlushCalls,CloseCalls,AcceptedBytes;
        internal int Write(byte[] data)
        {WriteCalls++;int count=PartialWrite?Math.Min(1,data.Length):data.Length;AcceptedBytes+=count;if(OnWrite!=null)OnWrite();if(FailWrite)throw new IOException("fixture");return count;}
        internal void Flush(){FlushCalls++;if(OnFlush!=null)OnFlush();if(FailFlush)throw new IOException("fixture");}
        // This fake counts a close only; a real FileStream may flush on Dispose.
        internal void Close(){CloseCalls++;if(OnClose!=null)OnClose();if(FailClose)throw new IOException("fixture");}
    }
    internal sealed class PracticeCreateFakeFactory
    {
        internal Action OnCreate {get;set;} internal bool FailCreate {get;set;} internal int CreateCalls;
        internal string LastName;internal FileMode Mode;internal FileAccess Access;internal FileShare Share;
        internal PracticeCreateFixtureDirectory Directory;internal PracticeCreateFakeHandle Result;
        internal PracticeCreateFakeHandle Create(PracticeCreateFixtureDirectory selected,string name,FileMode mode,FileAccess access,FileShare share)
        {
            CreateCalls++;Directory=selected;LastName=name;Mode=mode;Access=access;Share=share;
            if(OnCreate!=null)OnCreate();if(FailCreate)throw new IOException("fixture");
            if(Result==null)Result=new PracticeCreateFakeHandle();return Result;
        }
    }
    // Synthetic state machine only. Reservation and revoke are ordered under a
    // short lock; the fake calls happen outside it. A call reserved earlier can
    // still begin after revoke, so that overlap is UNCERTAIN. No callback or
    // blocking I/O occurs under the lock. This is not a production file factory.
    internal sealed class VivePracticeCreateNewFixture
    {
        readonly object gate=new object(),context;readonly int owner,maxBytes;readonly PracticeCreateFixtureDirectory selected;
        readonly PracticeCreateFakeFactory factory;readonly string name;PracticeCreateFakeHandle handle;
        // Test-only scheduling hook after reservation, before the fake call.
        internal Action AfterReservation {get;set;}
        PracticeCreateFixtureState state=PracticeCreateFixtureState.New;bool eyeChoice,taskChoice,revoked,operationRunning,cleanupAttempted,handleClosed;
        int nextSequence,totalBytes;
        internal VivePracticeCreateNewFixture(PracticeCreateFixtureDirectory selected,object context,PracticeCreateFakeFactory factory,int maxBytes)
        {
            if(selected==null||context==null||factory==null||maxBytes<1||maxBytes>65536)throw new ArgumentException("Bounded synthetic fixture required.");
            this.selected=selected;this.context=context;this.factory=factory;this.maxBytes=maxBytes;owner=Thread.CurrentThread.ManagedThreadId;
            name="practice-"+Guid.NewGuid().ToString("N")+".fixture";
        }
        internal bool SyntheticFixtureOnly {get{return true;}}internal bool ProductionGateOpen {get{return false;}}
        internal bool ConsentAuthenticated {get{return false;}}internal bool PhysicalExclusiveProven {get{return false;}}internal bool DurableStorageProven {get{return false;}}
        internal bool CleanupMayFlush {get{return true;}}internal PracticeCreateFixtureState State {get{lock(gate)return state;}}
        internal bool HandleClosed {get{lock(gate)return handleClosed;}}internal bool OperationRunning {get{lock(gate)return operationRunning;}}
        internal bool CleanupRequired {get{lock(gate)return handle!=null&&!handleClosed&&!cleanupAttempted;}}
        bool Owner(){return Thread.CurrentThread.ManagedThreadId==owner;}
        internal bool ChooseEye(){lock(gate){if(!Owner()||revoked||state!=PracticeCreateFixtureState.New||eyeChoice)return false;eyeChoice=true;return true;}}
        internal bool ChooseTask(){lock(gate){if(!Owner()||revoked||state!=PracticeCreateFixtureState.New||taskChoice)return false;taskChoice=true;return true;}}
        internal void Revoke()
        {
            lock(gate){if(revoked)return;revoked=true;
                if(operationRunning||state==PracticeCreateFixtureState.Creating||state==PracticeCreateFixtureState.Writing||state==PracticeCreateFixtureState.Flushing||state==PracticeCreateFixtureState.Closing)
                    state=PracticeCreateFixtureState.Uncertain;
                else if(state!=PracticeCreateFixtureState.Closed&&state!=PracticeCreateFixtureState.Uncertain&&state!=PracticeCreateFixtureState.Faulted)state=PracticeCreateFixtureState.Revoked;}
        }
        bool Reserve(PracticeCreateFixtureState before,PracticeCreateFixtureState during)
        {lock(gate){if(!Owner()||operationRunning){if(operationRunning)state=PracticeCreateFixtureState.Uncertain;else if(!revoked&&state!=PracticeCreateFixtureState.Closed)state=PracticeCreateFixtureState.Faulted;return false;}
            if(revoked||!eyeChoice||!taskChoice||state!=before)return false;operationRunning=true;state=during;
            if(during==PracticeCreateFixtureState.Closing)cleanupAttempted=true;return true;}}
        internal bool Create()
        {
            if(!Reserve(PracticeCreateFixtureState.New,PracticeCreateFixtureState.Creating))return false;
            PracticeCreateFakeHandle result=null;bool failed=false;
            try{if(AfterReservation!=null)AfterReservation();result=factory.Create(selected,name,FileMode.CreateNew,FileAccess.Write,FileShare.None);if(result==null)failed=true;}
            catch(Exception e) when(!Fatal(e)){failed=true;}
            lock(gate){operationRunning=false;if(result!=null)handle=result;
                if(failed||revoked||state!=PracticeCreateFixtureState.Creating){state=PracticeCreateFixtureState.Uncertain;return false;}
                state=PracticeCreateFixtureState.Open;return true;}
        }
        internal bool Append(PracticeCreateFixtureBatch batch)
        {
            byte[] bytes;
            lock(gate)
            {
                if(!Owner()||operationRunning){if(operationRunning)state=PracticeCreateFixtureState.Uncertain;else if(!revoked&&state!=PracticeCreateFixtureState.Closed)state=PracticeCreateFixtureState.Faulted;return false;}
                if(revoked||state!=PracticeCreateFixtureState.Open||handle==null)return false;
                if(batch==null||!Object.ReferenceEquals(batch.Context,context)||batch.Sequence!=nextSequence||nextSequence==Int32.MaxValue||totalBytes>maxBytes-2)
                {state=PracticeCreateFixtureState.Faulted;return false;}
                bytes=new[]{batch.Marker,(byte)(batch.Marker^0xA5)};operationRunning=true;state=PracticeCreateFixtureState.Writing;
            }
            bool failed=false;int count=-1;
            try{if(AfterReservation!=null)AfterReservation();count=handle.Write(bytes);}catch(Exception e) when(!Fatal(e)){failed=true;}
            lock(gate){operationRunning=false;
                if(failed||count!=bytes.Length||revoked||state!=PracticeCreateFixtureState.Writing){state=PracticeCreateFixtureState.Uncertain;return false;}
                totalBytes+=count;nextSequence++;state=PracticeCreateFixtureState.Open;return true;}
        }
        internal bool Flush()
        {
            if(!Reserve(PracticeCreateFixtureState.Open,PracticeCreateFixtureState.Flushing))return false;
            bool failed=false;try{if(AfterReservation!=null)AfterReservation();handle.Flush();}catch(Exception e) when(!Fatal(e)){failed=true;}
            lock(gate){operationRunning=false;if(failed||revoked||state!=PracticeCreateFixtureState.Flushing){state=PracticeCreateFixtureState.Uncertain;return false;}state=PracticeCreateFixtureState.Open;return true;}
        }
        internal bool Close()
        {
            if(!Reserve(PracticeCreateFixtureState.Open,PracticeCreateFixtureState.Closing))return false;
            return CloseReserved(false);
        }
        // Explicit one-shot cleanup may have hidden I/O in a real adapter. It is
        // allowed after revoke solely to release a known handle and never turns
        // an uncertain/revoked result into a clean one. No automatic retry.
        internal bool Cleanup()
        {
            lock(gate){if(!Owner()||operationRunning){if(operationRunning)state=PracticeCreateFixtureState.Uncertain;else if(!revoked&&state!=PracticeCreateFixtureState.Closed)state=PracticeCreateFixtureState.Faulted;return false;}
                if(cleanupAttempted||handle==null||handleClosed||
                (state!=PracticeCreateFixtureState.Revoked&&state!=PracticeCreateFixtureState.Uncertain&&state!=PracticeCreateFixtureState.Faulted))return false;
                cleanupAttempted=true;operationRunning=true;}
            return CloseReserved(true);
        }
        bool CloseReserved(bool cleanup)
        {
            bool failed=false;try{if(AfterReservation!=null)AfterReservation();handle.Close();}catch(Exception e) when(!Fatal(e)){failed=true;}
            lock(gate){operationRunning=false;if(!failed)handleClosed=true;
                if(cleanup||failed||revoked||state!=PracticeCreateFixtureState.Closing){state=PracticeCreateFixtureState.Uncertain;return false;}
                state=PracticeCreateFixtureState.Closed;return true;}
        }
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
    }
}
