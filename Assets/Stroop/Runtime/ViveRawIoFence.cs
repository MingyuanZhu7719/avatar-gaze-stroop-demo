using System;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // One-way bit created with the participant, before admission or an I/O
    // worker exists. Both the participant's first withdrawal instruction and
    // every later I/O reservation consult this exact same instance.
    internal sealed class ViveRawRevocation
    {
        // A single atomic state makes revocation and the optional one-page
        // simulation window mutually ordered. The page is not an authority;
        // already claimed data and managed strings cannot be recalled.
        sealed class WindowState
        {
            internal readonly object Owner,Token,Payload;
            internal WindowState(object owner,object token,object payload){Owner=owner;Token=token;Payload=payload;}
        }
        // The verified identity lives in the *same* slot that Revoke clears.
        // Neither this slot nor its opaque key is a consent, read, reopen,
        // export, close, or durability authority. An already returned key or
        // native handle cannot be erased from another thread's local stack.
        sealed class NativeFileState
        {
            internal readonly object Key,Capability,Sink,Admission,Handle;
            internal readonly string RelativeName;
            internal readonly ulong Volume;
            readonly byte[] fileId;
            internal bool Verified {get{return Handle!=null&&fileId!=null;}}
            internal NativeFileState(object key,object capability,object sink,object admission,string name,object handle,ulong volume,byte[] id)
            {Key=key;Capability=capability;Sink=sink;Admission=admission;RelativeName=name;Handle=handle;Volume=volume;fileId=id==null?null:(byte[])id.Clone();}
            internal bool Matches(object key,object capability,object sink,object admission,object handle)
            {return Verified&&Object.ReferenceEquals(Key,key)&&Object.ReferenceEquals(Capability,capability)&&Object.ReferenceEquals(Sink,sink)&&
                Object.ReferenceEquals(Admission,admission)&&Object.ReferenceEquals(Handle,handle);}
            internal bool DistinctTarget(ulong volume,byte[] targetId)
            {
                if(!Verified||volume!=Volume||targetId==null||targetId.Length!=16)return false;
                bool nonzero=false,different=false;
                for(int i=0;i<16;i++){nonzero|=targetId[i]!=0;different|=targetId[i]!=fileId[i];}
                return nonzero&&different;
            }
        }
        sealed class CompletedNativeFileState
        {internal readonly NativeFileState File;internal CompletedNativeFileState(NativeFileState file){File=file;}}
        static readonly object Revoked=new object(),WindowClosed=new object(),CallsReturned=new object();
        object state;
        // One word orders the participant's first withdrawal signal, a
        // technical revoke, and a private-stage target I/O claim. A claimed
        // operation may already be in flight when a stop wins later.
        const int ParticipantBit=1,PrivateStageStoppedBit=2,PrivateStageIoBit=4;
        int participantWithdrawal;
        object privateStageOwner;
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
        internal Action AfterPrivateStageStopForTest,AfterPrivateStageBindForTest;
#endif
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        internal Action AfterWithdrawalSignalForTest,BeforeClaimCasForTest,BeforeCompleteCasForTest,
            BeforeNativeBeginCasForTest,BeforeNativeVerifyCasForTest;
        internal bool NativeFileSlotPresentForTest {get{var value=Volatile.Read(ref state);return value is NativeFileState||value is CompletedNativeFileState;}}
#endif
        internal enum Outcome { Live, CallsReturned, Revoked }
        void StopPrivateStage(int bits)
        {
            while(true){int old=Volatile.Read(ref participantWithdrawal);
                if((old&bits)==bits)return;
                if(Interlocked.CompareExchange(ref participantWithdrawal,old|bits,old)==old)return;}
        }
        internal void Revoke(){StopPrivateStage(PrivateStageStoppedBit);
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
            var hook=AfterPrivateStageStopForTest;if(hook!=null)hook();
#endif
            Interlocked.Exchange(ref state,Revoked);}
        // Signal intent before clearing the window. This irreversible flag is
        // independent of technical Stop and is itself a revocation boundary.
        // A thread suspended before the Exchange must not leave authority live.
        internal void RevokeForParticipant()
        {
            StopPrivateStage(ParticipantBit|PrivateStageStoppedBit);
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            var hook=AfterWithdrawalSignalForTest;if(hook!=null)hook();
#endif
            Revoke();
        }
        internal bool ParticipantWithdrawalRequested {get{return (Volatile.Read(ref participantWithdrawal)&ParticipantBit)!=0;}}
        // The single owner is bound before inspecting its target. Binding
        // does not authorize that target, publication, or a second session.
        internal bool TryBindPrivateStage(object owner)
        {
            if(owner==null||!MayReserve)return false;
            if(Interlocked.CompareExchange(ref privateStageOwner,owner,null)!=null)return false;
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
            var hook=AfterPrivateStageBindForTest;if(hook!=null)hook();
#endif
            return MayReserve&&!PrivateStageStopped(owner);
        }
        internal bool PrivateStageStopped(object owner)
        {return !Object.ReferenceEquals(Volatile.Read(ref privateStageOwner),owner)||
            (Volatile.Read(ref participantWithdrawal)&(ParticipantBit|PrivateStageStoppedBit))!=0;}
        internal bool TryBeginPrivateStageIo(object owner)
        {
            if(!Object.ReferenceEquals(Volatile.Read(ref privateStageOwner),owner))return false;
            while(true){int old=Volatile.Read(ref participantWithdrawal);
                if((old&(ParticipantBit|PrivateStageStoppedBit|PrivateStageIoBit))!=0)return false;
                if(Interlocked.CompareExchange(ref participantWithdrawal,old|PrivateStageIoBit,old)==old)return true;}
        }
        internal void EndPrivateStageIo(object owner)
        {
            if(!Object.ReferenceEquals(Volatile.Read(ref privateStageOwner),owner))return;
            while(true){int old=Volatile.Read(ref participantWithdrawal);
                if((old&PrivateStageIoBit)==0)return;
                if(Interlocked.CompareExchange(ref participantWithdrawal,old&~PrivateStageIoBit,old)==old)return;}
        }
        bool AuthorityStopped {get{return (Volatile.Read(ref participantWithdrawal)&PrivateStageStoppedBit)!=0;}}
        internal bool IsRevoked {get{return AuthorityStopped||Object.ReferenceEquals(Volatile.Read(ref state),Revoked);}}
        internal Outcome CompletionOutcome
        {get{if(AuthorityStopped)return Outcome.Revoked;var value=Volatile.Read(ref state);
            if(AuthorityStopped||Object.ReferenceEquals(value,Revoked))return Outcome.Revoked;
            return Object.ReferenceEquals(value,CallsReturned)||value is CompletedNativeFileState?Outcome.CallsReturned:Outcome.Live;}}
        internal bool MayReserve {get{return CompletionOutcome==Outcome.Live;}}
        // Only an empty/closed simulation window can be sealed. One CAS orders
        // completion against every page operation and direct participant revoke.
        // It states no more than a caller's software claim about API returns.
        internal bool TryCompleteCalls(ViveRawIoFence caller)
        {
            if(caller==null||!caller.CanPublishSharedCompletion(this))return false;
            var old=Volatile.Read(ref state);
            var native=old as NativeFileState;
            if(old!=null&&!Object.ReferenceEquals(old,WindowClosed)&&(native==null||!native.Verified))return false;
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            var hook=BeforeCompleteCasForTest;if(hook!=null)hook();
#endif
            object completed=native==null?CallsReturned:(object)new CompletedNativeFileState(native);
            if(!Object.ReferenceEquals(Interlocked.CompareExchange(ref state,completed,old),old))return false;
            // Participant withdrawal signals intent before its state Exchange.
            // If it is paused in between, do not leave a completed identity in
            // the shared slot after this caller has observed that signal.
            if(IsRevoked){Interlocked.CompareExchange(ref state,Revoked,completed);return false;}
            return true;
        }
        // A provisional name is published before Create; the verified file ID
        // is installed only after the same newly created handle passed all OS
        // checks. The state slot is exclusive with the synthetic page window.
        internal bool TryBeginNativeFile(object capability,object sink,object admission,string relativeName,out object key)
        {
            key=null;if(capability==null||sink==null||admission==null||String.IsNullOrEmpty(relativeName)||!MayReserve)return false;
            var identityKey=new object();var candidate=new NativeFileState(identityKey,capability,sink,admission,relativeName,null,0,null);
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            var hook=BeforeNativeBeginCasForTest;if(hook!=null)hook();
#endif
            if(Interlocked.CompareExchange(ref state,candidate,null)!=null)return false;
            if(!MayReserve){Interlocked.CompareExchange(ref state,Revoked,candidate);return false;}
            key=identityKey;return true;
        }
        internal bool TryVerifyNativeFile(object key,object capability,object sink,object admission,object handle,ulong volume,byte[] fileId)
        {
            if(key==null||handle==null||!ViveRawFileId.IsValid(fileId)||!MayReserve)return false;
            var prior=Volatile.Read(ref state) as NativeFileState;
            if(prior==null||prior.Verified||!Object.ReferenceEquals(prior.Key,key)||!Object.ReferenceEquals(prior.Capability,capability)||
                !Object.ReferenceEquals(prior.Sink,sink)||!Object.ReferenceEquals(prior.Admission,admission))return false;
            var verified=new NativeFileState(key,capability,sink,admission,prior.RelativeName,handle,volume,fileId);
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            var hook=BeforeNativeVerifyCasForTest;if(hook!=null)hook();
#endif
            if(!Object.ReferenceEquals(Interlocked.CompareExchange(ref state,verified,prior),prior))return false;
            if(!MayReserve){Interlocked.CompareExchange(ref state,Revoked,verified);return false;}
            return true;
        }
        // This is a live-state comparison only. It cannot authorize reopening
        // a closed file or establish current participant consent.
        internal bool NativeFileCurrent(object key,object capability,object sink,object admission,object handle)
        {
            if(!MayReserve)return false;var value=Volatile.Read(ref state) as NativeFileState;
            return value!=null&&value.Matches(key,capability,sink,admission,handle)&&MayReserve;
        }
        internal bool NativeTargetDistinct(object key,object capability,object sink,object admission,object handle,ulong volume,byte[] targetId)
        {
            if(!MayReserve)return false;
            var value=Volatile.Read(ref state) as NativeFileState;
            return value!=null&&value.Matches(key,capability,sink,admission,handle)&&value.DistinctTarget(volume,targetId)&&
                Object.ReferenceEquals(value,Volatile.Read(ref state))&&MayReserve;
        }
        internal bool TryAttachSimulationWindow(object owner)
        {
            if(owner==null||!MayReserve)return false;var empty=new WindowState(owner,null,null);
            return Interlocked.CompareExchange(ref state,empty,null)==null&&MayReserve;
        }
        internal bool TryPublishSimulationPage(object owner,object token,object payload)
        {
            if(owner==null||token==null||payload==null||!MayReserve)return false;
            var old=Volatile.Read(ref state) as WindowState;
            if(old==null||!Object.ReferenceEquals(old.Owner,owner)||old.Payload!=null)return false;
            var next=new WindowState(owner,token,payload);
            return Object.ReferenceEquals(Interlocked.CompareExchange(ref state,next,old),old)&&MayReserve;
        }
        internal bool TryClaimSimulationPage(object owner,object token,out object payload)
        {
            payload=null;if(!MayReserve)return false;var old=Volatile.Read(ref state) as WindowState;
            if(old==null||!Object.ReferenceEquals(old.Owner,owner)||!Object.ReferenceEquals(old.Token,token)||old.Payload==null)return false;
            var empty=new WindowState(owner,null,null);
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            var hook=BeforeClaimCasForTest;if(hook!=null)hook();
#endif
            if(!Object.ReferenceEquals(Interlocked.CompareExchange(ref state,empty,old),old))return false;
            if(!MayReserve)return false;
            payload=old.Payload;return true;
        }
        internal void CloseSimulationWindow(object owner)
        {
            if(owner==null)return;
            for(int attempt=0;attempt<64;attempt++){var old=Volatile.Read(ref state) as WindowState;
                if(old==null||!Object.ReferenceEquals(old.Owner,owner))return;
                if(Object.ReferenceEquals(Interlocked.CompareExchange(ref state,WindowClosed,old),old))return;}
            // Contention must not leave a page reachable indefinitely.
            Revoke();
        }
        internal bool SimulationWindowHasPage(object owner)
        {if(!MayReserve)return false;var current=Volatile.Read(ref state) as WindowState;return current!=null&&Object.ReferenceEquals(current.Owner,owner)&&current.Payload!=null&&MayReserve;}
    }
    // Per-operation coordination only: it is not a file opener, storage
    // permission, participant consent, or a durability/close proof. The exact
    // admission must revoke this fence *before* clearing its own state; a
    // separate later revoke cannot prevent a reservation in between. The
    // caller must separately validate handle identity, the legal operation
    // order, accepted byte counts and full experiment/participant authority.
    internal sealed class ViveRawIoFence
    {
        internal enum IoOperation { Create=1,VerifyFileType=2,VerifyFileId=3,VerifyFileInformation=4,DataWrite=5,TerminalWrite=6,Flush=7,Close=8,CloseDirectory=9,BlockPlanFlush=10,PrepareOpen=11,PrepareLeaseId=12,PrepareDirectoryId=13,CloseAncestor=14,LeaseDriveType=15,LeaseOpen=16,LeaseFileType=17,LeaseFileInformation=18,LeaseVolumeInformation=19,ReadbackSeek=20,ReadbackData=21,ReadbackSize=22,
            IdentityDirectoryBefore=23,IdentityWitnessOpen=24,IdentityWitnessType=25,IdentityWitnessId=26,IdentityWitnessInformation=27,
            IdentityDirectoryAfter=28,IdentityReopen=29,IdentityReopenType=30,IdentityReopenId=31,IdentityReopenInformation=32,IdentityReopenClose=33,IdentityWitnessClose=34 }
        internal enum IoOutcome { ApiReturned, Unknown, Rejected }
        internal sealed class Permit
        {
            readonly ViveRawIoFence owner;
            internal readonly IoOperation Operation;internal readonly long Sequence;
            internal bool ApiReturned,ApiSucceeded;internal int Finished;
            internal Permit(ViveRawIoFence owner,IoOperation operation,long sequence)
            {this.owner=owner;Operation=operation;Sequence=sequence;}
            internal bool BelongsTo(ViveRawIoFence candidate){return Object.ReferenceEquals(owner,candidate);}
        }
        readonly ViveNamedSessionFrameAdmission admission;readonly ViveRawRevocation revocation;readonly Thread creator;
        Thread writer;Permit current;long sequence;
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        internal Action BeforeReservationFinalCheckForTest;
#endif
        int bound,active,faulted,uncertain,createIssued,terminalIssued,flushIssued,closeIssued;
        internal ViveRawIoFence(ViveNamedSessionFrameAdmission admission,ViveRawRevocation revocation)
        {this.admission=admission??throw new ArgumentNullException("admission");this.revocation=revocation??throw new ArgumentNullException("revocation");creator=Thread.CurrentThread;}
        internal bool Matches(ViveNamedSessionFrameAdmission value){return Object.ReferenceEquals(admission,value);}
        internal bool MatchesAuthority(ViveNamedSessionFrameAdmission value,ViveRawRevocation authority)
        {return Object.ReferenceEquals(admission,value)&&Object.ReferenceEquals(revocation,authority);}
        bool NativeSiblingBindingCaller(ViveNamedSessionFrameAdmission value,ViveRawPrivateStage stage,
            ViveRawRelativeFileCapability capability,ViveRawSessionJournalSink sink)
        {
            return Object.ReferenceEquals(admission,value)&&stage!=null&&stage.IsTargetWorkerThread&&
                stage.HasCurrentTargetClaim(value,this)&&capability!=null&&sink!=null&&
                capability.MatchesNativeBinding(this,sink)&&sink.BelongsTo(value)&&admission.IsIoFenceAuthority(this)&&
                Volatile.Read(ref bound)!=0&&Volatile.Read(ref faulted)==0&&revocation.MayReserve;
        }
        internal bool NativeSourceCurrentForSibling(ViveNamedSessionFrameAdmission value,ViveRawPrivateStage stage,object sourceKey,
            ViveRawRelativeFileCapability capability,ViveRawSessionJournalSink sink,object sourceHandle)
        {
            return NativeSiblingBindingCaller(value,stage,capability,sink)&&
                revocation.NativeFileCurrent(sourceKey,capability,sink,admission,sourceHandle);
        }
        internal bool NativeSiblingIdentityDistinct(ViveNamedSessionFrameAdmission value,ViveRawPrivateStage stage,object sourceKey,
            ViveRawRelativeFileCapability capability,ViveRawSessionJournalSink sink,object sourceHandle,ulong targetVolume,byte[] targetId)
        {
            return NativeSiblingBindingCaller(value,stage,capability,sink)&&revocation.NativeTargetDistinct(sourceKey,capability,sink,
                admission,sourceHandle,targetVolume,targetId);
        }
        // Owner binds the actual writer Thread object before Start. Thread IDs
        // can be recycled and are deliberately not accepted as identity.
        internal bool BindWriter(Thread value)
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,creator)||value==null||Object.ReferenceEquals(value,creator)||
                !revocation.MayReserve||Volatile.Read(ref faulted)!=0||Interlocked.CompareExchange(ref bound,1,0)!=0)return false;
            writer=value;
            if(!revocation.MayReserve||Volatile.Read(ref faulted)!=0)return false;
            return true;
        }
        // Compatibility property used as an I/O/producer gate. Both complete
        // and revoked are unavailable; this is NOT participant withdrawal.
        internal bool IsRevoked {get{return !revocation.MayReserve;}}
        internal ViveRawRevocation.Outcome CompletionOutcome {get{return revocation.CompletionOutcome;}}
        internal bool CanPublishSharedCompletion(ViveRawRevocation authority)
        {
            return Object.ReferenceEquals(revocation,authority)&&admission.IsIoFenceAuthority(this)&&
                Object.ReferenceEquals(Thread.CurrentThread,Volatile.Read(ref writer))&&Volatile.Read(ref bound)!=0&&
                Volatile.Read(ref active)==0&&Volatile.Read(ref uncertain)==0&&Volatile.Read(ref faulted)==0;
        }
        internal bool TryPublishCallsReturned(){return revocation.TryCompleteCalls(this);}
        bool NativeBindingCaller(ViveRawRelativeFileCapability capability,ViveRawSessionJournalSink sink)
        {
            return capability!=null&&sink!=null&&capability.MatchesNativeBinding(this,sink)&&sink.BelongsTo(admission)&&admission.IsIoFenceAuthority(this)&&
                Object.ReferenceEquals(Thread.CurrentThread,Volatile.Read(ref writer))&&Volatile.Read(ref bound)!=0&&
                Volatile.Read(ref active)==0&&Volatile.Read(ref faulted)==0&&revocation.MayReserve;
        }
        internal bool TryBeginNativeFile(ViveRawRelativeFileCapability capability,ViveRawSessionJournalSink sink,string relativeName,out object key)
        {
            key=null;return NativeBindingCaller(capability,sink)&&
                revocation.TryBeginNativeFile(capability,sink,admission,relativeName,out key);
        }
        internal bool TryVerifyNativeFile(object key,ViveRawRelativeFileCapability capability,ViveRawSessionJournalSink sink,object handle,ulong volume,byte[] fileId)
        {return NativeBindingCaller(capability,sink)&&revocation.TryVerifyNativeFile(key,capability,sink,admission,handle,volume,fileId);}
        internal bool NativeFileCurrent(object key,ViveRawRelativeFileCapability capability,ViveRawSessionJournalSink sink,object handle)
        {return NativeBindingCaller(capability,sink)&&revocation.NativeFileCurrent(key,capability,sink,admission,handle);}
        internal bool IsFaulted {get{return Volatile.Read(ref faulted)!=0;}}
        // Snapshot only; a caller must not treat this as a file-handle-close
        // receipt or use it alone to free a native in-flight buffer.
        internal bool HasReservation {get{return Volatile.Read(ref active)!=0;}}
        internal bool OutcomeUncertain {get{return Volatile.Read(ref uncertain)!=0||(!revocation.MayReserve&&Volatile.Read(ref active)!=0);}}
        static bool SingleShot(IoOperation operation)
        {return operation==IoOperation.Create||operation==IoOperation.TerminalWrite||operation==IoOperation.Flush||operation==IoOperation.Close;}
        bool ClaimSingleShot(IoOperation operation)
        {
            switch(operation)
            {
                case IoOperation.Create:return Interlocked.CompareExchange(ref createIssued,1,0)==0;
                case IoOperation.TerminalWrite:return Interlocked.CompareExchange(ref terminalIssued,1,0)==0;
                case IoOperation.Flush:return Interlocked.CompareExchange(ref flushIssued,1,0)==0;
                case IoOperation.Close:return Interlocked.CompareExchange(ref closeIssued,1,0)==0;
                default:return true;
            }
        }
        // Reservation precedes exactly one potentially blocking API call. It
        // does not call, wrap, retry or verify any operating-system operation.
        // A permit acquired before Revoke may still be invoked/complete later;
        // that outcome must remain unknown and must not enable a second call.
        internal bool TryReserve(IoOperation operation,out Permit permit)
        {
            permit=null;
            if(!Enum.IsDefined(typeof(IoOperation),operation)||!Object.ReferenceEquals(Thread.CurrentThread,Volatile.Read(ref writer))||
                Volatile.Read(ref bound)==0||!revocation.MayReserve||Volatile.Read(ref faulted)!=0)return false;
            if(Interlocked.CompareExchange(ref active,1,0)!=0){Abort();return false;}
            if(!revocation.MayReserve||Volatile.Read(ref faulted)!=0)
            {Interlocked.Exchange(ref active,0);return false;}
            if(sequence==Int64.MaxValue||(SingleShot(operation)&&!ClaimSingleShot(operation)))
            {Abort();Interlocked.Exchange(ref active,0);return false;}
            var value=new Permit(this,operation,++sequence);Volatile.Write(ref current,value);
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            var hook=BeforeReservationFinalCheckForTest;if(hook!=null)hook();
#endif
            if(!revocation.MayReserve||Volatile.Read(ref faulted)!=0)
            {Interlocked.CompareExchange(ref current,null,value);Interlocked.Exchange(ref active,0);return false;}
            permit=value;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            if(admission.SyntheticOnly)ViveSyntheticNativeTrace.Reserved(operation);
#endif
            return true;
        }
        // This is a single atomic stop signal, callable by any thread. It
        // never waits for the writer, native call, queue or file system.
        internal void Revoke()
        {
            revocation.Revoke();
            if(Volatile.Read(ref active)!=0)Interlocked.Exchange(ref uncertain,1);
        }
        internal void Abort()
        {
            Revoke();
            Interlocked.Exchange(ref faulted,1);
        }
        // This records the caller's declaration only; it is not proof that an
        // OS call occurred or a byte count was correct. A reviewed native
        // wrapper must bind its real API result and length to this exact permit.
        // Reports what the writer observed, never what reached stable media.
        // A short/error/throw/unknown operation must pass apiSucceeded=false;
        // no call is automatically retried. A success concurrent with revoke
        // is still unknown, even when the API did return success.
        internal IoOutcome Finish(Permit permit,bool apiReturned,bool apiSucceeded)
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,Volatile.Read(ref writer))||permit==null||!permit.BelongsTo(this)||
                !Object.ReferenceEquals(Volatile.Read(ref current),permit)||Interlocked.Exchange(ref permit.Finished,1)!=0)
            {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(admission.SyntheticOnly&&permit!=null)ViveSyntheticNativeTrace.Finish(permit.Operation,apiReturned,apiSucceeded,IoOutcome.Rejected);
#endif
                Abort();return IoOutcome.Rejected;}
            if(!apiReturned||!apiSucceeded)Abort();
            permit.ApiReturned=apiReturned;permit.ApiSucceeded=apiSucceeded;
            Interlocked.CompareExchange(ref current,null,permit);
            Interlocked.Exchange(ref active,0);
            if(!revocation.MayReserve||Volatile.Read(ref faulted)!=0||Volatile.Read(ref uncertain)!=0)
            {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(admission.SyntheticOnly)ViveSyntheticNativeTrace.Finish(permit.Operation,apiReturned,apiSucceeded,IoOutcome.Unknown);
#endif
                Interlocked.Exchange(ref uncertain,1);return IoOutcome.Unknown;}
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            if(admission.SyntheticOnly)ViveSyntheticNativeTrace.Finish(permit.Operation,apiReturned,apiSucceeded,IoOutcome.ApiReturned);
#endif
            return IoOutcome.ApiReturned;
        }
    }
}
