using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Isolated tests only. An opaque key is NOT a filesystem path, and this
    // in-memory dictionary does not establish operating-system exclusivity,
    // prevent links, authenticate a user, or prove persistence/durability.
    internal enum ViveFakeCreateResult { Created,AlreadyExists,Rejected }
    internal enum ViveFakeWriteResult { Accepted,Rejected,Uncertain }
    internal enum ViveFakeTargetState { Open,InFlight,Withdrawn,Uncertain,Closed,Faulted }

    // Simulated outcomes; callbacks exist solely for deterministic tests. This
    // type is not a production storage/provider capability.
    internal sealed class ViveFakeWritePlan
    {
        internal int PrefixBytes {get;private set;}
        internal bool FailAfterPrefix {get;private set;}
        internal Action AfterPrefix {get;private set;}
        internal ViveFakeWritePlan(int prefixBytes,bool failAfterPrefix,Action afterPrefix)
        {PrefixBytes=prefixBytes;FailAfterPrefix=failAfterPrefix;AfterPrefix=afterPrefix;}
    }

    // No delete, replace, caller-owned stream, path or aliases are accepted.
    // Successful creation reserves its key for the lifetime of this store,
    // including failures or cancellation immediately after creation.
    internal sealed class ViveExclusiveMemoryStore
    {
        const int MaximumTargetBytes=1048576;
        readonly object gate=new object();readonly int maximumEntries;
        readonly Dictionary<string,ViveExclusiveMemoryTarget> entries=new Dictionary<string,ViveExclusiveMemoryTarget>(StringComparer.Ordinal);
        internal ViveExclusiveMemoryStore(int maxEntries)
        {if(maxEntries<1||maxEntries>1024)throw new ArgumentOutOfRangeException("maxEntries");maximumEntries=maxEntries;}
        static bool ValidKey(string id)
        {
            if(String.IsNullOrEmpty(id)||id.Length>128)return false;
            foreach(char ch in id)if(!((ch>='a'&&ch<='z')||(ch>='A'&&ch<='Z')||(ch>='0'&&ch<='9')||ch=='_'||ch=='-'))return false;
            return true;
        }
        internal ViveFakeCreateResult TryCreateNew(string opaqueId,int maximumBytes,out ViveExclusiveMemoryTarget target)
        {
            target=null;if(!ValidKey(opaqueId)||maximumBytes<1||maximumBytes>MaximumTargetBytes)return ViveFakeCreateResult.Rejected;
            lock(gate)
            {
                if(entries.ContainsKey(opaqueId))return ViveFakeCreateResult.AlreadyExists;
                if(entries.Count>=maximumEntries)return ViveFakeCreateResult.Rejected;
                var created=new ViveExclusiveMemoryTarget(maximumBytes);entries.Add(opaqueId,created);target=created;
                return ViveFakeCreateResult.Created;
            }
        }
        // Test inspection only: returns a defensive copy, never a raw handle.
        internal byte[] CopyForTest(string opaqueId)
        {
            if(!ValidKey(opaqueId))return null;
            ViveExclusiveMemoryTarget target;lock(gate){if(!entries.TryGetValue(opaqueId,out target))return null;}
            return target.CopyForTest();
        }
        // Diagnostic snapshot only; never exposes the owned target handle.
        internal ViveFakeTargetState? StateForTest(string opaqueId)
        {
            if(!ValidKey(opaqueId))return null;
            ViveExclusiveMemoryTarget target;lock(gate){if(!entries.TryGetValue(opaqueId,out target))return null;}
            return target.State;
        }
    }

    // A single logical owner, no mutable buffer escapes. InFlight is a fake
    // logical boundary, not an OS write-start guarantee. A withdrawal after
    // it makes the result uncertain, even if all fake bytes are present.
    internal sealed class ViveExclusiveMemoryTarget
    {
        readonly object gate=new object();readonly MemoryStream bytes=new MemoryStream();readonly int owner,maximumBytes;
        long lastAppendOrdinal=-1;Receipt currentReceipt;
        ViveFakeTargetState state=ViveFakeTargetState.Open;
        // The issuing target retains the unique object reference. A digest is
        // diagnostic only; it is not a signature or evidence of real storage.
        internal sealed class Receipt
        {
            readonly byte[] payloadDigest;
            internal long Ordinal {get;private set;}
            internal long TotalLength {get;private set;}
            internal Receipt(long ordinal,long totalLength,byte[] digest)
            {Ordinal=ordinal;TotalLength=totalLength;payloadDigest=(byte[])digest.Clone();}
            internal byte[] CopyPayloadDigest(){return (byte[])payloadDigest.Clone();}
            // No allocation or callback during the completion short locks.
            // Caller supplies its own private 32-byte prepared digest.
            internal bool MatchesPayloadDigest(byte[] expected)
            {
                if(expected==null||expected.Length!=32||payloadDigest.Length!=32)return false;
                int difference=0;for(int i=0;i<32;i++)difference|=payloadDigest[i]^expected[i];return difference==0;
            }
        }
        internal ViveFakeTargetState State {get{lock(gate)return state;}}
        internal bool RealFileOwnershipProven {get{return false;}}
        internal bool DurabilityProven {get{return false;}}
        internal ViveExclusiveMemoryTarget(int maximumBytes){this.maximumBytes=maximumBytes;owner=Thread.CurrentThread.ManagedThreadId;}
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        internal ViveFakeWriteResult Append(byte[] payload,ViveFakeWritePlan plan)
        {Receipt ignored;return AppendCore(null,false,payload,plan,false,out ignored);}
        internal ViveFakeWriteResult AppendWithReceipt(byte[] payload,ViveFakeWritePlan plan,out Receipt receipt)
        {return AppendCore(null,false,payload,plan,true,out receipt);}
        // The expected receipt and append begin are checked in the SAME fake
        // target gate. A separate IsCurrentReceipt call cannot guard an append.
        internal ViveFakeWriteResult AppendIfCurrent(Receipt expected,byte[] payload,ViveFakeWritePlan plan,out Receipt receipt)
        {return AppendCore(expected,true,payload,plan,true,out receipt);}
        internal bool IsCurrentReceipt(Receipt receipt)
        {
            lock(gate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner)
                {
                    currentReceipt=null;
                    if(state==ViveFakeTargetState.InFlight)state=ViveFakeTargetState.Uncertain;
                    else if(state==ViveFakeTargetState.Open)state=ViveFakeTargetState.Faulted;
                    return false;
                }
                return receipt!=null&&state==ViveFakeTargetState.Open&&Object.ReferenceEquals(currentReceipt,receipt)&&
                    receipt.Ordinal==lastAppendOrdinal&&receipt.TotalLength==bytes.Length;
            }
        }
        internal bool TargetLockHeld {get{return Monitor.IsEntered(gate);}}
        // Only a sealed synthetic marker is published while the exact fake
        // task-target receipt remains current. No callback or real I/O runs.
        internal bool TryPublishSyntheticEyeUnderTargetLock(VivePracticeOwnedMemoryStart issuer,VivePracticeTaskEventCursor cursor,
            VivePracticeSyntheticEyeJoin join,ViveDevelopmentParticipant.PracticeObservationFence fence,
            VivePracticeSyntheticTicket ticket,Receipt expected)
        {
            if(issuer==null||cursor==null||join==null||fence==null||ticket==null||!cursor.CompletionLockHeld||!issuer.CursorPreTargetLocksHeld)return false;
            lock(gate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||expected==null||state!=ViveFakeTargetState.Open||
                    !Object.ReferenceEquals(currentReceipt,expected)||expected.Ordinal!=lastAppendOrdinal||expected.TotalLength!=bytes.Length)return false;
                return join.PublishUnderTargetLock(cursor,issuer,fence,ticket);
            }
        }
        // Old and new fake targets are locked in that order, after the cursor,
        // both reservations, choices and participant. These methods accept no
        // callback or Stream and perform no I/O while either gate is held.
        internal bool TryPublishRetryUnderOldTargetLock(VivePracticeOwnedMemoryStart oldHost,VivePracticeStartReservation newReservation,
            VivePracticeOwnedMemoryStart newHost,VivePracticeRetryContinuation continuation,VivePracticeConsentAuthority.StartReservation start,
            ViveDevelopmentParticipant.PracticeObservationFence fence,Receipt expected)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||oldHost==null||newReservation==null||!newReservation.CompletionLockHeld||
                continuation==null||continuation.Cursor==null||!continuation.Cursor.CompletionLockHeld||!oldHost.CursorPreTargetLocksHeld)return false;
            lock(gate)
            {
                if(expected==null||state!=ViveFakeTargetState.Open||!Object.ReferenceEquals(currentReceipt,expected)||
                    expected.Ordinal!=lastAppendOrdinal||expected.TotalLength!=bytes.Length)return false;
                return oldHost.TryPublishRetryWithOldTargetLock(newReservation,newHost,continuation,start,fence);
            }
        }
        internal bool TryPublishRetryUnderNewTargetLock(VivePracticeOwnedMemoryStart oldHost,VivePracticeStartReservation newReservation,
            VivePracticeOwnedMemoryStart newHost,VivePracticeRetryContinuation continuation,VivePracticeConsentAuthority.StartReservation start,
            ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||oldHost==null||newHost==null||newReservation==null||
                !newReservation.CompletionLockHeld||continuation==null||continuation.Cursor==null||!continuation.Cursor.CompletionLockHeld||
                !oldHost.CursorPreTargetLocksHeld)return false;
            lock(gate)
            {
                if(state!=ViveFakeTargetState.Open||currentReceipt==null||currentReceipt.Ordinal!=lastAppendOrdinal||
                    currentReceipt.TotalLength!=bytes.Length)return false;
                return newHost.CommitRetryPublicationUnderTargetLocks(oldHost,newReservation,continuation,start,fence);
            }
        }
        // The caller owns cursor -> issuer -> choice -> participant locks.
        // Only the sealed in-memory issuer may perform this final state update;
        // no caller-provided callbacks, real Stream or I/O run under these locks.
        internal bool TryFinalizeCursorReceipt(Receipt receipt,VivePracticeOwnedMemoryStart issuer,
            VivePracticeTaskEventCursor cursor,VivePracticeTaskEventCursor.CursorAppendLease lease)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||issuer==null||cursor==null||lease==null||
                !cursor.CompletionLockHeld||!issuer.CursorPreTargetLocksHeld)return false;
            lock(gate)
            {
                if(!Monitor.IsEntered(gate)||!issuer.CursorCommitLocksHeld||receipt==null||state!=ViveFakeTargetState.Open||
                    !Object.ReferenceEquals(currentReceipt,receipt)||receipt.Ordinal!=lastAppendOrdinal||receipt.TotalLength!=bytes.Length)return false;
                return issuer.CommitCursorUnderTargetLock(receipt,cursor,lease);
            }
        }
        ViveFakeWriteResult AppendCore(Receipt expected,bool requireReceipt,byte[] payload,ViveFakeWritePlan plan,bool issueReceipt,out Receipt receipt)
        {
            receipt=null;Action after;byte[] digest=null,stable;int prefix;bool failAfter;
            lock(gate)
            {
                bool matchesExpected=!requireReceipt||(expected!=null&&state==ViveFakeTargetState.Open&&
                    Object.ReferenceEquals(currentReceipt,expected)&&expected.Ordinal==lastAppendOrdinal&&expected.TotalLength==bytes.Length);
                // Every attempt invalidates any prior receipt, even if rejected.
                currentReceipt=null;
                if(Thread.CurrentThread.ManagedThreadId!=owner)
                {
                    if(state==ViveFakeTargetState.InFlight){state=ViveFakeTargetState.Uncertain;return ViveFakeWriteResult.Uncertain;}
                    if(state==ViveFakeTargetState.Open)state=ViveFakeTargetState.Faulted;
                    return ViveFakeWriteResult.Rejected;
                }
                if(state==ViveFakeTargetState.InFlight){state=ViveFakeTargetState.Uncertain;return ViveFakeWriteResult.Uncertain;}
                if(state!=ViveFakeTargetState.Open)return ViveFakeWriteResult.Rejected;
                if(!matchesExpected){state=ViveFakeTargetState.Faulted;return ViveFakeWriteResult.Rejected;}
                if(payload==null||payload.Length==0||plan==null||plan.PrefixBytes<0||plan.PrefixBytes>payload.Length||lastAppendOrdinal==Int64.MaxValue||
                    bytes.Length>maximumBytes-(long)payload.Length)
                {state=ViveFakeTargetState.Faulted;return ViveFakeWriteResult.Rejected;}
                stable=(byte[])payload.Clone();prefix=plan.PrefixBytes;failAfter=plan.FailAfterPrefix;after=plan.AfterPrefix;
                state=ViveFakeTargetState.InFlight;
            }
            // Crypto providers can invoke implementation-dependent code. Do
            // not hold the target gate across hashing or test callbacks.
            if(issueReceipt)
            {
                try{using(var sha=SHA256.Create())digest=sha.ComputeHash(stable);}
                catch(Exception e) when(!Fatal(e)){lock(gate)state=ViveFakeTargetState.Uncertain;return ViveFakeWriteResult.Uncertain;}
            }
            lock(gate)
            {
                if(state!=ViveFakeTargetState.InFlight){state=ViveFakeTargetState.Uncertain;return ViveFakeWriteResult.Uncertain;}
                try{bytes.Write(stable,0,prefix);}
                catch(Exception e) when(!Fatal(e)){state=ViveFakeTargetState.Uncertain;return ViveFakeWriteResult.Uncertain;}
            }
            bool callbackFailed=false;
            try{if(after!=null)after();}catch(Exception e) when(!Fatal(e)){callbackFailed=true;}
            lock(gate)
            {
                if(state!=ViveFakeTargetState.InFlight||callbackFailed||failAfter||prefix!=stable.Length)
                {state=ViveFakeTargetState.Uncertain;return ViveFakeWriteResult.Uncertain;}
                state=ViveFakeTargetState.Open;lastAppendOrdinal++;
                if(issueReceipt){receipt=new Receipt(lastAppendOrdinal,bytes.Length,digest);currentReceipt=receipt;}
                return ViveFakeWriteResult.Accepted;
            }
        }
        // This affects this fake target only and cannot cancel physical I/O.
        internal void Withdraw()
        {
            lock(gate)
            {
                currentReceipt=null;
                if(state==ViveFakeTargetState.InFlight)state=ViveFakeTargetState.Uncertain;
                else if(state==ViveFakeTargetState.Open)state=ViveFakeTargetState.Withdrawn;
            }
        }
        // An owner-level post-write check may fail after a fake append returned
        // Accepted. Preserve the bytes, but never report the target clean.
        internal void MarkOutcomeUncertain()
        {lock(gate){currentReceipt=null;if(state==ViveFakeTargetState.Open||state==ViveFakeTargetState.InFlight)state=ViveFakeTargetState.Uncertain;}}
        internal bool Complete()
        {
            lock(gate)
            {
                currentReceipt=null;
                if(Thread.CurrentThread.ManagedThreadId!=owner)
                {if(state==ViveFakeTargetState.InFlight)state=ViveFakeTargetState.Uncertain;else if(state==ViveFakeTargetState.Open)state=ViveFakeTargetState.Faulted;return false;}
                if(state==ViveFakeTargetState.InFlight){state=ViveFakeTargetState.Uncertain;return false;}
                if(state!=ViveFakeTargetState.Open)return false;
                state=ViveFakeTargetState.Closed;return true;
            }
        }
        internal byte[] CopyForTest(){lock(gate)return bytes.ToArray();}
    }
}
