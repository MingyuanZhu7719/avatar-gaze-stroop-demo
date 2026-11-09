using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AvatarStroop.Runtime
{
    // Controlled development entry only. No file path, directory
    // handle or raw file handle is exported to a caller. A selected path and
    // matching identity do not prove a person's authorization, confidentiality,
    // consent, or a supported anti-reparse contract.
    internal sealed partial class ViveRawRelativeFileCapability
    {
        static readonly object RetentionGate=new object();
        static readonly List<ViveRawRelativeFileCapability> Retained=new List<ViveRawRelativeFileCapability>();
        readonly ViveRawIoFence fence;readonly ViveRawDirectoryLease lease;readonly ViveRawSessionJournalSink sink;readonly Thread owner;
        ViveRawDirectoryLease.ExclusiveOwner leaseOwner;
        RawNativeHandle directory,file;OwnedFile transferred;object fileIdentityKey;ulong directoryVolume;int state,taken,fileClosed;
        string sourceName;Native.FileId directoryIdentity;IdentityWitnessSlot sourceWitness;
        internal enum IdentityStep { DirectoryBefore, WitnessOpen, WitnessType, WitnessId, WitnessInformation,
            DirectoryAfter, Reopen, ReopenType, ReopenId, ReopenInformation, ReopenClose, WitnessClose }
        // One invocation performs at most one native operation. The owning
        // capability is retained before construction; no finalizer/retry is
        // used to clean up an unknown witness, reopen or original handle.
        sealed class IdentityWitnessSlot
        {
            readonly RawNativeHandle directory,original;
            readonly string name,expectedId,directoryId;readonly ulong volume,directoryVolume;
            RawNativeHandle witness,reopened;int phase,unknown;
            uint observedType;Native.FileId observedId;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            internal Action<IdentityStep> BeforeNativeForTest;
            internal int NativeCallsForTest;
            internal int? LastOpenStatusForTest;
            internal bool UnknownForTest {get{return Volatile.Read(ref unknown)!=0;}}
#endif
            internal IdentityWitnessSlot(RawNativeHandle directory,Native.FileId directoryIdentity,
                RawNativeHandle original,string name,ulong volume,byte[] id)
            {
                this.directory=directory;this.original=original;this.name=name;this.volume=volume;
                expectedId=Hex(id);directoryId=Hex(directoryIdentity.Id);directoryVolume=directoryIdentity.Volume;
            }
            static string Hex(byte[] value){return value==null?null:BitConverter.ToString(value).Replace("-",String.Empty);}
            internal bool ReadyForOriginalClose {get{return Volatile.Read(ref unknown)==0&&Volatile.Read(ref phase)==5;}}
            internal bool Complete {get{return Volatile.Read(ref unknown)==0&&Volatile.Read(ref phase)==12;}}
            internal void Abort(){Volatile.Write(ref unknown,1);}
            internal bool Run(IdentityStep step)
            {
                if(Volatile.Read(ref unknown)!=0)return false;
                if((int)step<0||(int)step>11||(int)step!=Volatile.Read(ref phase)||directory==null||directory.IsClosed||directory.IsInvalid||
                    original==null||String.IsNullOrEmpty(name)||expectedId==null||directoryId==null||
                    ((int)step<5?(original.IsClosed||original.IsInvalid):(!original.IsClosed||original.CloseOutcome!=1)))
                {Abort();return false;}
                bool success=false;
                try
                {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    var hook=BeforeNativeForTest;if(hook!=null)hook(step);
                    NativeCallsForTest++;
#endif
                    switch(step)
                    {
                        case IdentityStep.DirectoryBefore:
                        case IdentityStep.DirectoryAfter:
                            Native.FileId dir;
                            success=Native.GetId(directory,out dir)&&dir.Volume==directoryVolume&&
                                String.Equals(Hex(dir.Id),directoryId,StringComparison.Ordinal);break;
                        case IdentityStep.WitnessOpen:
                        case IdentityStep.Reopen:
                            int status;ulong information;
                            if(step==IdentityStep.WitnessOpen)
                                Native.Open(name,directory,0x80,7,1,0x00200040,ref witness,out status,out information);
                            else Native.Open(name,directory,0x80,7,1,0x00200040,ref reopened,out status,out information);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                            LastOpenStatusForTest=status;
#endif
                            var opened=step==IdentityStep.WitnessOpen?witness:reopened;
                            success=status==0&&information==1&&opened!=null&&!opened.IsInvalid&&!opened.IsClosed;break;
                        case IdentityStep.WitnessType:
                        case IdentityStep.ReopenType:
                            observedType=Native.GetFileType(step==IdentityStep.WitnessType?witness:reopened);
                            success=observedType==1;break;
                        case IdentityStep.WitnessId:
                        case IdentityStep.ReopenId:
                            success=Native.GetId(step==IdentityStep.WitnessId?witness:reopened,out observedId)&&
                                observedId.Volume==volume&&String.Equals(Hex(observedId.Id),expectedId,StringComparison.Ordinal);break;
                        case IdentityStep.WitnessInformation:
                        case IdentityStep.ReopenInformation:
                            Native.FileInformation info;
                            success=Native.GetInfo(step==IdentityStep.WitnessInformation?witness:reopened,out info)&&
                                ViveRawPostCloseIdentity.Matches(volume,expectedId,observedType,info.Attributes,observedId.Volume,observedId.Id);break;
                        case IdentityStep.ReopenClose:
                            reopened.Dispose();success=reopened.CloseOutcome==1;break;
                        case IdentityStep.WitnessClose:
                            witness.Dispose();success=witness.CloseOutcome==1;break;
                    }
                    if(success)Volatile.Write(ref phase,(int)step+1);
                    return success;
                }
                finally{if(!success)Abort();}
            }
        }
        sealed class SiblingIdentity
        {
            internal readonly string Name;internal readonly ulong Volume;internal readonly string FileIdHex;
            internal SiblingIdentity(string name,ulong volume,byte[] fileId)
            {Name=name;Volume=volume;FileIdHex=BitConverter.ToString(fileId).Replace("-",String.Empty).ToLowerInvariant();}
        }
        sealed class SiblingSlot
        {internal RawNativeHandle Handle;internal ViveRawSiblingStream Stream;internal ViveRawPrivateStage Stage;internal SiblingIdentity Identity;internal IdentityWitnessSlot Witness;}
        SiblingSlot sibling;int siblingAttempted;
        // Independent CSV target. A failed create/verification is
        // retained as uncertain; it must not reuse the raw sibling slot.
        SiblingSlot csvSibling;int csvSiblingAttempted;
        // Root and every ancestor require their own confirmed close. The
        // process registry intentionally remains occupied even after these
        // return; restart policy is still under separate review.
        internal bool DirectoryCleanupPending {get{return directory!=null||!lease.ExclusiveCleanupComplete;}}
        internal static bool ProductionGateOpen {get{return ViveControlledRecordingBuildPolicy.Available;}}
        // First write may contain magic plus a maximum-size manifest frame and
        // one maximum-size data frame. This does not bypass the sink budget.
        internal const int MaximumWriteBytes=2*(1048576+40)+8;
        internal static bool IsLegalWriteLength(int length){return length>0&&length<=MaximumWriteBytes;}
        internal static int RetainedCount {get{lock(RetentionGate)return Retained.Count;}}

        ViveRawRelativeFileCapability(ViveRawIoFence fence,ViveRawDirectoryLease lease,ViveRawSessionJournalSink sink)
        {this.fence=fence;this.lease=lease;this.sink=sink;owner=Thread.CurrentThread;}
        internal bool MatchesNativeBinding(ViveRawIoFence expectedFence,ViveRawSessionJournalSink expectedSink)
        {return Object.ReferenceEquals(fence,expectedFence)&&Object.ReferenceEquals(sink,expectedSink);}
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        bool OnOwner(){return Object.ReferenceEquals(Thread.CurrentThread,owner);}
        void Keep(){lock(RetentionGate)if(!Retained.Contains(this))Retained.Add(this);}
        void Uncertain(){Volatile.Write(ref state,4);fence.Abort();Keep();}

        // Pre-session, read-only directory preparation on a dedicated worker.
        // It is not participant consent; the chosen path is never used again
        // for file creation. The caller must bind the returned capability to
        // an independently validated participant/session before any real use.
        internal static bool TryPrepare(ViveNamedSessionFrameAdmission admission,ViveRawIoFence fence,
            ViveRawDirectoryLease lease,string selectedDirectory,ViveRawSessionJournalSink.NativeAuthority authority,out ViveRawRelativeFileCapability capability)
        {
            capability=null;
            if(!ProductionGateOpen&&(authority==null||authority.Owner==null||!authority.Owner.IsNativeFixture))return false;
            if(admission==null||fence==null||lease==null||authority==null||authority.Owner==null||!admission.IsIoFenceAuthority(fence)||
                String.IsNullOrEmpty(selectedDirectory)||!authority.Owner.HasExactNativeConfiguration(admission,fence,lease,selectedDirectory)||
                !authority.TryConsume(ViveRawSessionJournalSink.NativeStep.Prepare,null,null,null))return false;
            var c=new ViveRawRelativeFileCapability(fence,lease,authority.Owner);
            lock(RetentionGate){if(Retained.Count!=0)return false;Retained.Add(c);}
            try
            {
                string full=ViveRawDirectoryLease.NormalizeCandidate(selectedDirectory);
                if(!lease.TryTakeExclusive(out c.leaseOwner)){c.Uncertain();return false;}
                ViveRawIoFence.Permit permit;bool returned=false,success=false;int status=0;ulong info=0;
                if(!fence.TryReserve(ViveRawIoFence.IoOperation.PrepareOpen,out permit)){c.Uncertain();return false;}
                try{
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(c.sink.IsNativeFixture)ViveSyntheticNativeTrace.Called(ViveRawIoFence.IoOperation.PrepareOpen);
#endif
                    Native.Open(@"\??\"+full,null,0x001000A0,3,1,0x21,ref c.directory,out status,out info);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(c.sink.IsNativeFixture&&status!=0)ViveSyntheticNativeTrace.NtStatus(ViveRawIoFence.IoOperation.PrepareOpen,status);
#endif
                    returned=true;success=status==0&&info==1&&c.directory!=null&&!c.directory.IsInvalid;}
                catch(Exception e) when(!Fatal(e)){}
                if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){c.Uncertain();return false;}
                ViveRawDirectoryLease.DirectoryIdentity selected=null;returned=false;success=false;
                if(!fence.TryReserve(ViveRawIoFence.IoOperation.PrepareLeaseId,out permit)){c.Uncertain();return false;}
                try{
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(c.sink.IsNativeFixture)ViveSyntheticNativeTrace.Called(ViveRawIoFence.IoOperation.PrepareLeaseId);
#endif
                    success=lease.TryReadExclusiveDirectoryIdentity(c.leaseOwner,out selected);returned=true;}
                catch(Exception e) when(!Fatal(e)){}
                if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){c.Uncertain();return false;}
                Native.FileId id=default(Native.FileId);returned=false;success=false;
                if(!fence.TryReserve(ViveRawIoFence.IoOperation.PrepareDirectoryId,out permit)){c.Uncertain();return false;}
                try{
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(c.sink.IsNativeFixture)ViveSyntheticNativeTrace.Called(ViveRawIoFence.IoOperation.PrepareDirectoryId);
#endif
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    success=c.sink.IsNativeFixture?Native.GetIdForSyntheticDiagnostic(c.directory,out id):Native.GetId(c.directory,out id);
#else
                    success=Native.GetId(c.directory,out id);
#endif
                    returned=true;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(success&&!selected.Matches(id.Volume,id.Id)&&c.sink.IsNativeFixture)ViveSyntheticNativeTrace.ValidationFailure(ViveRawIoFence.IoOperation.PrepareDirectoryId);
#endif
                    success=success&&selected.Matches(id.Volume,id.Id);}
                catch(Exception e) when(!Fatal(e)){}
                if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){c.Uncertain();return false;}
                c.directoryIdentity=id;c.directoryVolume=id.Volume;Volatile.Write(ref c.state,1);capability=c;return true;
            }
            catch(Exception e) when(!Fatal(e)){c.Uncertain();return false;}
        }

        // One Create permit corresponds to exactly one NtCreateFile call. If
        // revoke wins after reservation, the handle and outcome are retained
        // as uncertain: no Dispose, retry, automatic reopen, or deletion.
        internal bool TryCreate(ViveRawSessionJournalSink.NativeAuthority authority)
        {
            if(!sink.CanRunNative||!OnOwner()||Volatile.Read(ref state)!=1||directory==null||authority==null||
                !Object.ReferenceEquals(authority.Owner,sink)||!authority.TryConsume(ViveRawSessionJournalSink.NativeStep.Create,this,null,null))return false;
            var name="vraw-"+Guid.NewGuid().ToString("N")+".vraw";
            sourceName=name;
            // The name is kept only in the shared revocable state. This opaque
            // key contains no filename or file ID and grants no read access.
            if(!fence.TryBeginNativeFile(this,sink,name,out fileIdentityKey)){Uncertain();return false;}
            ViveRawIoFence.Permit permit;
            if(!fence.TryReserve(ViveRawIoFence.IoOperation.Create,out permit)){Uncertain();return false;}
            bool returned=false,success=false;int status=0;ulong info=0;
            try
            {
                // FILE_CREATE, FILE_NON_DIRECTORY_FILE, OPEN_REPARSE_POINT,
                // synchronous I/O, share=0; null security descriptor means
                // system default/inherited ACL, not universal read access.
                // Retain FILE_READ_DATA on the same exclusive file object so
                // the finished bytes can be checked before its final close.
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(sink.IsNativeFixture)ViveSyntheticNativeTrace.Called(ViveRawIoFence.IoOperation.Create);
#endif
                Native.Open(name,directory,0x00100083,0,2,0x00200060,ref file,out status,out info);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(sink.IsNativeFixture&&status!=0)ViveSyntheticNativeTrace.NtStatus(ViveRawIoFence.IoOperation.Create,status);
#endif
                returned=true;success=status==0&&info==2&&file!=null&&!file.IsInvalid;
            }
            catch(Exception e) when(!Fatal(e)){}
            var outcome=fence.Finish(permit,returned,success);
            if(outcome!=ViveRawIoFence.IoOutcome.ApiReturned){Uncertain();return false;}
            Volatile.Write(ref state,2);return true;
        }

        // Each verification uses its own reservation and one native call.
        // File IDs identify open objects, not directory authorization.
        internal bool TryVerifyAndTransfer(ViveRawSessionJournalSink.NativeAuthority authority,out OwnedFile result)
        {
            result=null;if(!sink.CanRunNative||!OnOwner()||Volatile.Read(ref state)!=2||file==null||authority==null||
                !Object.ReferenceEquals(authority.Owner,sink)||!authority.TryConsume(ViveRawSessionJournalSink.NativeStep.Verify,this,null,null))return false;
            ViveRawIoFence.Permit permit;bool returned=false,success=false;uint type=0;
            if(!fence.TryReserve(ViveRawIoFence.IoOperation.VerifyFileType,out permit))return false;
            try{type=Native.GetFileType(file);returned=true;success=type==1;}catch(Exception e) when(!Fatal(e)){}
            if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){Uncertain();return false;}
            Native.FileId id=default(Native.FileId);returned=false;success=false;
            if(!fence.TryReserve(ViveRawIoFence.IoOperation.VerifyFileId,out permit)){Uncertain();return false;}
            try{success=Native.GetId(file,out id);returned=true;success=success&&id.Volume==directoryVolume;}catch(Exception e) when(!Fatal(e)){}
            if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){Uncertain();return false;}
            Native.FileInformation details=default(Native.FileInformation);returned=false;success=false;
            if(!fence.TryReserve(ViveRawIoFence.IoOperation.VerifyFileInformation,out permit)){Uncertain();return false;}
            try{success=Native.GetInfo(file,out details);returned=true;success=success&&(details.Attributes&0x410)==0;}catch(Exception e) when(!Fatal(e)){}
            if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){Uncertain();return false;}
            if(!fence.TryVerifyNativeFile(fileIdentityKey,this,sink,file,id.Volume,id.Id)){Uncertain();return false;}
            if(Interlocked.CompareExchange(ref taken,1,0)!=0){Uncertain();return false;}
            if(!fence.NativeFileCurrent(fileIdentityKey,this,sink,file)){Uncertain();return false;}
            sourceWitness=new IdentityWitnessSlot(directory,directoryIdentity,file,sourceName,id.Volume,id.Id);
            transferred=new OwnedFile(this,file,fileIdentityKey);file=null;Volatile.Write(ref state,3);
            // A success is an observed transfer, not continuing authority:
            // every subsequent operation rechecks the same shared state.
            if(!transferred.BindingCurrent()){Uncertain();return false;}
            result=transferred;return true;
        }

        // Called only by the exact stage worker while it owns the participant-
        // bound target-I/O claim. The child name is generated
        // locally; the selected directory path is not reopened. The slot is
        // process-rooted before NtCreateFile so an uncertain operation cannot
        // lose its root or resulting handle to finalization.
        internal Stream OpenSiblingForPrivateStage(ViveRawPrivateStage stage,ViveNamedSessionFrameAdmission admission)
        {
            if(!sink.CanRunNative||stage==null||!stage.IsTargetWorkerThread||admission==null||!sink.BelongsTo(admission)||
                !stage.HasCurrentTargetClaim(admission,fence)||Volatile.Read(ref state)!=3||directory==null||
                transferred==null||!transferred.IsSourceForSibling(stage,admission)||Interlocked.CompareExchange(ref siblingAttempted,1,0)!=0)
                throw new InvalidOperationException("Private sibling target is unavailable.");
            var slot=new SiblingSlot{Stage=stage};sibling=slot;Keep();
            string name="vstage-"+Guid.NewGuid().ToString("N")+".partial.vraw";
            int status=0;ulong information=0;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            sink.NotifySyntheticRelativeTargetCreateForTest();
#endif
            if(!stage.HasCurrentTargetClaim(admission,fence))throw new IOException("Private sibling create reservation stopped.");
            Native.Open(name,directory,0x00100083,0,2,0x00200060,ref slot.Handle,out status,out information);
            if(status!=0||information!=2||slot.Handle==null||slot.Handle.IsInvalid)
                throw new IOException("Private sibling create outcome is unconfirmed.");
            slot.Stream=new ViveRawSiblingStream(slot.Handle,(volume,id)=>{
                if(!transferred.IsDistinctSibling(stage,admission,volume,id))return false;
                // This binds the generated name to the identity observed on
                // the exclusive handle. It does not prove a later path reopen.
                Volatile.Write(ref slot.Identity,new SiblingIdentity(name,volume,id));return true;
            });
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            sink.NotifySyntheticRelativeTargetOpenForTest(name,slot.Stream);
#endif
            return slot.Stream;
        }

        // The *same* stage worker must already
        // hold its participant-bound I/O claim. The original source close has
        // returned; its pre-close verified identity remains bound to this
        // live shared authority. The raw sibling stays open and exclusive.
        // This method does not establish consent, scan validity or readiness
        // for publication, and does not reopen any path.
        Stream OpenCsvSiblingCore(ViveRawPrivateStage stage,ViveNamedSessionFrameAdmission admission)
        {
            var raw=Volatile.Read(ref sibling);
            var rawIdentity=raw==null?null:Volatile.Read(ref raw.Identity);
            if(!sink.SourceCloseReturned||!sink.CanRunNative||stage==null||!stage.IsTargetWorkerThread||
                admission==null||!sink.BelongsTo(admission)||!stage.HasCurrentTargetClaim(admission,fence)||
                Volatile.Read(ref state)!=3||directory==null||transferred==null||!transferred.IsSourceForSibling(stage,admission)||
                raw==null||!Object.ReferenceEquals(raw.Stage,stage)||raw.Stream==null||raw.Stream.OutcomeUnknown||
                raw.Stream.CloseReturned||!raw.Stream.CanRead||!raw.Stream.CanSeek||rawIdentity==null||
                Interlocked.CompareExchange(ref csvSiblingAttempted,1,0)!=0)
                throw new InvalidOperationException("Synthetic CSV sibling target is unavailable.");
            var slot=new SiblingSlot{Stage=stage};csvSibling=slot;Keep();
            string name="vstage-"+Guid.NewGuid().ToString("N")+".partial.csv";
            int status=0;ulong information=0;
            if(!stage.HasCurrentTargetClaim(admission,fence))throw new IOException("Synthetic CSV create reservation stopped.");
            Native.Open(name,directory,0x00100083,0,2,0x00200060,ref slot.Handle,out status,out information);
            if(status!=0||information!=2||slot.Handle==null||slot.Handle.IsInvalid)
                throw new IOException("Synthetic CSV create outcome is unconfirmed.");
            slot.Stream=new ViveRawSiblingStream(slot.Handle,(volume,id)=>{
                if(!Object.ReferenceEquals(Volatile.Read(ref sibling),raw)||!Object.ReferenceEquals(Volatile.Read(ref raw.Identity),rawIdentity)||
                    raw.Stream.OutcomeUnknown||raw.Stream.CloseReturned||!raw.Stream.CanRead||!raw.Stream.CanSeek||
                    !transferred.IsDistinctSibling(stage,admission,volume,id)||
                    !SyntheticIdentityDistinct(rawIdentity.Volume,rawIdentity.FileIdHex,volume,id))return false;
                Volatile.Write(ref slot.Identity,new SiblingIdentity(name,volume,id));return true;
            });
            return slot.Stream;
        }
        internal Stream OpenInstalledCsvSiblingForPrivateStage(ViveRawPrivateStage stage,ViveNamedSessionFrameAdmission admission,
            ViveRawExclusiveDestination owner,ViveRawRevocation authority)
        {
            if(!sink.HasDestinationLifecycle||admission==null||
                owner==null||authority==null||!sink.MayOpenInstalledCsv(owner,stage,admission,authority))
                throw new InvalidOperationException("Installed CSV target is unavailable.");
            return OpenCsvSiblingCore(stage,admission);
        }
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        internal Stream OpenSyntheticCsvSiblingForPrivateStage(ViveRawPrivateStage stage,ViveNamedSessionFrameAdmission admission)
        {
            if(!sink.IsNativeFixture||admission==null||!admission.SyntheticOnly)
                throw new InvalidOperationException("Synthetic CSV target is unavailable.");
            return OpenCsvSiblingCore(stage,admission);
        }
#endif
        internal static bool SyntheticIdentityDistinct(ulong firstVolume,string firstIdHex,ulong secondVolume,byte[] secondId)
        {
            if(firstVolume!=secondVolume||firstIdHex==null||firstIdHex.Length!=32||secondId==null||secondId.Length!=16)return false;
            bool firstNonzero=false,secondNonzero=false;
            foreach(char c in firstIdHex){if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F')))return false;if(c!='0')firstNonzero=true;}
            foreach(byte b in secondId)if(b!=0)secondNonzero=true;
            return firstNonzero&&secondNonzero&&!String.Equals(firstIdHex,BitConverter.ToString(secondId).Replace("-",String.Empty),StringComparison.OrdinalIgnoreCase);
        }
        // Invoked only inside a fresh shared stage claim on its exact close
        // worker. This is not a writer-thread permit or a report claim.
        internal bool TrySiblingIdentityStep(ViveRawPrivateStage stage,ViveNamedSessionFrameAdmission admission,bool csv,IdentityStep step)
        {
            var slot=csv?Volatile.Read(ref csvSibling):Volatile.Read(ref sibling);
            if(!sink.CanRunNative||stage==null||!stage.IsIdentityCloseWorker(csv)||admission==null||
                !sink.BelongsTo(admission)||!stage.HasCurrentTargetClaim(admission,fence)||state!=3||
                directory==null||slot==null||!Object.ReferenceEquals(slot.Stage,stage)||slot.Identity==null||
                slot.Stream==null||slot.Stream.OutcomeUnknown)return false;
            try
            {
                if(slot.Witness==null)
                {
                    if(step!=IdentityStep.DirectoryBefore)return false;
                    Keep();slot.Witness=new IdentityWitnessSlot(directory,directoryIdentity,slot.Handle,
                        slot.Identity.Name,slot.Identity.Volume,HexBytes(slot.Identity.FileIdHex));
                }
                if(!slot.Witness.Run(step)||!stage.HasCurrentTargetClaim(admission,fence))
                {slot.Witness.Abort();Uncertain();return false;}
                return true;
            }
            catch(Exception e) when(!Fatal(e)){if(slot.Witness!=null)slot.Witness.Abort();Uncertain();return false;}
        }
        static byte[] HexBytes(string value)
        {
            if(value==null||value.Length!=32)throw new InvalidOperationException("Verified identity unavailable.");
            var result=new byte[16];for(int i=0;i<16;i++)result[i]=Convert.ToByte(value.Substring(i*2,2),16);return result;
        }
        // A point-in-time observation, not continuing path authority. The
        // exact stage must still own its report claim, all file witnesses
        // and directory cleanup must have completed, and withdrawal must not
        // have revoked this session. Callers cannot supply a success boolean.
        internal bool IdentityObservedAfterClose(ViveRawPrivateStage stage,ViveNamedSessionFrameAdmission admission,bool csv)
        {
            var slot=csv?Volatile.Read(ref csvSibling):Volatile.Read(ref sibling);
            return sink.HasDestinationLifecycle&&sink.CanCompleteNative&&sink.CompletionState==7&&!DirectoryCleanupPending&&
                sourceWitness!=null&&sourceWitness.Complete&&admission!=null&&sink.BelongsTo(admission)&&
                stage!=null&&stage.HasCurrentTargetClaim(admission,fence)&&slot!=null&&Object.ReferenceEquals(slot.Stage,stage)&&
                slot.Stream!=null&&slot.Stream.CloseReturned&&!slot.Stream.OutcomeUnknown&&slot.Witness!=null&&slot.Witness.Complete;
        }
        internal bool TryGetClosedCsvIdentity(ViveRawPrivateStage stage,ViveNamedSessionFrameAdmission admission,
            out string relativeName,out ulong volume,out string fileIdHex)
        {
            relativeName=null;volume=0;fileIdHex=null;
            var slot=Volatile.Read(ref csvSibling);if(slot==null||!Object.ReferenceEquals(slot.Stage,stage)||
                slot.Stream==null||!slot.Stream.CloseReturned||slot.Witness==null||!slot.Witness.Complete||!stage.HasCurrentTargetClaim(admission,fence))return false;
            var identity=Volatile.Read(ref slot.Identity);if(identity==null)return false;
            relativeName=identity.Name;volume=identity.Volume;fileIdHex=identity.FileIdHex;return true;
        }
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        internal bool TryGetClosedSyntheticCsvIdentity(ViveRawPrivateStage stage,ViveNamedSessionFrameAdmission admission,
            out string relativeName,out ulong volume,out string fileIdHex)
        {return TryGetClosedCsvIdentity(stage,admission,out relativeName,out volume,out fileIdHex);}
#endif

        // The caller must hold the exact stage's shared report claim. This
        // exposes the identity only after the witness/reopen sequence returned.
        // A later lookup of the reported path remains unverified.
        internal bool TryGetClosedSiblingIdentity(ViveRawPrivateStage stage,ViveNamedSessionFrameAdmission admission,
            out string relativeName,out ulong volume,out string fileIdHex)
        {
            relativeName=null;volume=0;fileIdHex=null;
            var slot=Volatile.Read(ref sibling);if(slot==null||!Object.ReferenceEquals(slot.Stage,stage)||
                slot.Stream==null||!slot.Stream.CloseReturned||slot.Witness==null||!slot.Witness.Complete||!stage.HasCurrentTargetClaim(admission,fence))return false;
            var identity=Volatile.Read(ref slot.Identity);if(identity==null)return false;
            relativeName=identity.Name;volume=identity.Volume;fileIdHex=identity.FileIdHex;return true;
        }

        // Directory cleanup has its own per-call reservation. The candidate
        // stays process-registered even after closes until restart review.
        internal bool TryCloseDirectory(ViveRawSessionJournalSink.NativeAuthority authority)
        {
            if(!sink.CanRunNative||!OnOwner()||Volatile.Read(ref fileClosed)!=1||directory==null||transferred==null||!transferred.BindingCurrent()||authority==null||
                !Object.ReferenceEquals(authority.Owner,sink)||!authority.TryConsume(ViveRawSessionJournalSink.NativeStep.CloseDirectory,this,null,null))return false;
            // A live or uncertain sibling retains the directory lease. The
            // source file's close remains separately reported as returned.
            var child=Volatile.Read(ref sibling);
            if(child!=null&&(child.Stream==null||!child.Stream.CloseReturned||child.Witness==null||!child.Witness.Complete))return false;
            var csvChild=Volatile.Read(ref csvSibling);
            if(csvChild!=null&&(csvChild.Stream==null||!csvChild.Stream.CloseReturned||csvChild.Witness==null||!csvChild.Witness.Complete))return false;
            if(sourceWitness==null||!sourceWitness.Complete)return false;
            ViveRawIoFence.Permit permit;if(!fence.TryReserve(ViveRawIoFence.IoOperation.CloseDirectory,out permit))return false;
            bool returned=false,success=false;
            try{directory.Dispose();returned=true;success=directory.CloseOutcome==1;}catch(Exception e) when(!Fatal(e)){}
            if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){Uncertain();return false;}
            directory=null;return true;
        }

        // Bounded by the lease's 120-handle path limit. One call performs at
        // most one native ancestor close; the caller issues fresh authority
        // for each call and must stop on any failure or uncertain outcome.
        internal bool TryCloseNextAncestor(ViveRawSessionJournalSink.NativeAuthority authority,out bool complete)
        {
            complete=false;
            if(!sink.CanRunNative||!OnOwner()||Volatile.Read(ref fileClosed)!=1||directory!=null||transferred==null||!transferred.BindingCurrent()||authority==null||
                !Object.ReferenceEquals(authority.Owner,sink)||!authority.TryConsume(ViveRawSessionJournalSink.NativeStep.CloseAncestor,this,null,null))return false;
            bool result;
#if VIVE_RAW_SYNTHETIC_TEST
            result=sink.IsNativeFixture?lease.TryCloseNextSyntheticForTest(sink.FixtureAdmission,leaseOwner,fence,out complete):lease.TryCloseNextExclusive(leaseOwner,fence,out complete);
#else
            result=lease.TryCloseNextExclusive(leaseOwner,fence,out complete);
#endif
            if(!result){Uncertain();return false;}
            return true;
        }

        // Single writer only. Synchronous WriteFile avoids managed FileStream
        // buffers but uses normal OS cache; Flush success is not durability.
        internal sealed class OwnedFile
        {
            readonly ViveRawRelativeFileCapability parent;readonly RawNativeHandle handle;readonly object identityKey;
            byte[] inFlight;GCHandle inFlightPin;ViveRawNativeOutputStorage inFlightWritten;bool pinHeld,terminal,flushed,closed,readbackStarted,readbackComplete;
            internal OwnedFile(ViveRawRelativeFileCapability parent,RawNativeHandle handle,object identityKey){this.parent=parent;this.handle=handle;this.identityKey=identityKey;}
            internal bool BindingCurrent(){return parent.fence.NativeFileCurrent(identityKey,parent,parent.sink,handle);}
            internal bool IsSourceForSibling(ViveRawPrivateStage stage,ViveNamedSessionFrameAdmission admission)
            {return parent.fence.NativeSourceCurrentForSibling(admission,stage,identityKey,parent,parent.sink,handle);}
            internal bool IsDistinctSibling(ViveRawPrivateStage stage,ViveNamedSessionFrameAdmission admission,ulong volume,byte[] targetId)
            {return parent.fence.NativeSiblingIdentityDistinct(admission,stage,identityKey,parent,parent.sink,handle,volume,targetId);}
            internal bool TryWrite(byte[] bytes,bool isTerminal,ViveRawSessionJournalSink.NativeAuthority authority,out long sequence)
            {
                sequence=-1;
                if(!parent.sink.CanRunNative||!parent.OnOwner()||!BindingCurrent()||closed||terminal||bytes==null||!IsLegalWriteLength(bytes.Length)||authority==null||
                    !Object.ReferenceEquals(authority.Owner,parent.sink)||!authority.TryConsume(isTerminal?ViveRawSessionJournalSink.NativeStep.Terminal:ViveRawSessionJournalSink.NativeStep.Data,parent,this,bytes))return false;
                ViveRawIoFence.Permit permit;
                if(!parent.fence.TryReserve(isTerminal?ViveRawIoFence.IoOperation.TerminalWrite:ViveRawIoFence.IoOperation.DataWrite,out permit))
                    return false;
                bool returned=false,success=false;uint count=0;
                try
                {
                    inFlight=(byte[])bytes.Clone();
                    inFlightPin=GCHandle.Alloc(inFlight,GCHandleType.Pinned);pinHeld=true;
                    inFlightWritten=new ViveRawNativeOutputStorage(sizeof(uint));
                    // Even after this check, withdrawal can race with the OS
                    // call. The fence marks any such reservation unknown.
                    if(!parent.fence.IsRevoked)
                    {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        if(parent.sink.IsNativeFixture)ViveSyntheticNativeTrace.Called(isTerminal?ViveRawIoFence.IoOperation.TerminalWrite:ViveRawIoFence.IoOperation.DataWrite);
#endif
                        success=Native.Write(handle,inFlightPin.AddrOfPinnedObject(),(uint)inFlight.Length,inFlightWritten.Address);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        if(!success&&parent.sink.IsNativeFixture)ViveSyntheticNativeTrace.NativeError(isTerminal?ViveRawIoFence.IoOperation.TerminalWrite:ViveRawIoFence.IoOperation.DataWrite,Marshal.GetLastWin32Error());
#endif
                        returned=true;
                        if(success){count=unchecked((uint)Marshal.ReadInt32(inFlightWritten.Address));success=count==inFlight.Length;}
                    }
                }
                catch(Exception e) when(!Fatal(e)){}
                if(parent.fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){parent.Uncertain();return false;}
                Array.Clear(inFlight,0,inFlight.Length);if(pinHeld){inFlightPin.Free();pinHeld=false;}
                if(inFlightWritten!=null){inFlightWritten.ReleaseAfterConfirmedCompletion();inFlightWritten=null;}
                inFlight=null;if(isTerminal)terminal=true;sequence=permit.Sequence;return true;
            }
            internal bool TryFlush(ViveRawSessionJournalSink.NativeAuthority authority)
            {
                if(!parent.sink.CanRunNative||!parent.OnOwner()||!BindingCurrent()||closed||!terminal||flushed||authority==null||
                    !Object.ReferenceEquals(authority.Owner,parent.sink)||!authority.TryConsume(ViveRawSessionJournalSink.NativeStep.Flush,parent,this,null))return false;
                ViveRawIoFence.Permit permit;if(!parent.fence.TryReserve(ViveRawIoFence.IoOperation.Flush,out permit))return false;
                bool returned=false,success=false;try{
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(parent.sink.IsNativeFixture)ViveSyntheticNativeTrace.Called(ViveRawIoFence.IoOperation.Flush);
#endif
                    success=Native.Flush(handle);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(!success&&parent.sink.IsNativeFixture)ViveSyntheticNativeTrace.NativeError(ViveRawIoFence.IoOperation.Flush,Marshal.GetLastWin32Error());
#endif
                    returned=true;}catch(Exception e) when(!Fatal(e)){}
                if(parent.fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){parent.Uncertain();return false;}
                flushed=true;return true;
            }
            // Only the writer thread may inspect a sealed, flushed file. This
            // retains the same exclusive open file object; it does not expose
            // a path or handle, prove durability, or grant a later export.
            internal Stream TryOpenReadback(long expectedLength,ViveRawSessionJournalSink.NativeAuthority authority)
            {
                if(!parent.sink.CanRunNative||!parent.OnOwner()||!BindingCurrent()||closed||!terminal||!flushed||readbackStarted||
                    expectedLength<0||authority==null||!Object.ReferenceEquals(authority.Owner,parent.sink)||
                    !authority.TryConsume(ViveRawSessionJournalSink.NativeStep.Readback,parent,this,null))return null;
                readbackStarted=true;
                ViveRawIoFence.Permit permit;bool returned=false,success=false;long length=-1;
                if(!parent.fence.TryReserve(ViveRawIoFence.IoOperation.ReadbackSize,out permit))return null;
                try{success=Native.GetSize(handle,out length)&&length==expectedLength;returned=true;}catch(Exception e) when(!Fatal(e)){}
                if(parent.fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){parent.Uncertain();return null;}
                returned=false;success=false;
                if(!parent.fence.TryReserve(ViveRawIoFence.IoOperation.ReadbackSeek,out permit))return null;
                try{success=Native.SeekStart(handle);returned=true;}catch(Exception e) when(!Fatal(e)){}
                if(parent.fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){parent.Uncertain();return null;}
                return new ReadOnlyFileView(this,expectedLength);
            }
            sealed class ReadOnlyFileView:Stream
            {
                readonly OwnedFile owner;long remaining;bool disposed;
                internal ReadOnlyFileView(OwnedFile owner,long length){this.owner=owner;remaining=length;}
                public override bool CanRead {get{return !disposed;}} public override bool CanSeek {get{return false;}} public override bool CanWrite {get{return false;}}
                public override long Length {get{throw new NotSupportedException();}}
                public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
                public override void Flush(){throw new NotSupportedException();}
                public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
                public override void SetLength(long value){throw new NotSupportedException();}
                public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
                protected override void Dispose(bool disposing)
                {
                    if(!disposed&&remaining==0&&owner.parent.OnOwner()&&owner.BindingCurrent())owner.readbackComplete=true;
                    disposed=true;base.Dispose(disposing);
                }
                public override int Read(byte[] buffer,int offset,int count)
                {
                    if(disposed)throw new ObjectDisposedException("ReadOnlyFileView");
                    if(buffer==null)throw new ArgumentNullException("buffer");
                    if(offset<0||count<0||offset>buffer.Length-count)throw new ArgumentOutOfRangeException();
                    if(!owner.parent.OnOwner()||!owner.BindingCurrent()||owner.closed)throw new IOException("Readback authority unavailable.");
                    if(count==0||remaining==0)return 0;
                    int requested=(int)Math.Min(Math.Min((long)count,remaining),65536L);
                    return owner.ReadChunk(buffer,offset,requested,ref remaining);
                }
            }
            int ReadChunk(byte[] destination,int offset,int requested,ref long remaining)
            {
                ViveRawIoFence.Permit permit;
                if(!parent.fence.TryReserve(ViveRawIoFence.IoOperation.ReadbackData,out permit))throw new IOException("Readback unavailable.");
                bool returned=false,success=false;uint count=0;
                try
                {
                    // A possibly pending native operation retains these fields
                    // just as the existing write path retains its buffers.
                    inFlight=new byte[requested];inFlightPin=GCHandle.Alloc(inFlight,GCHandleType.Pinned);pinHeld=true;
                    inFlightWritten=new ViveRawNativeOutputStorage(sizeof(uint));
                    if(!parent.fence.IsRevoked)
                    {
                        success=Native.Read(handle,inFlightPin.AddrOfPinnedObject(),(uint)requested,inFlightWritten.Address);returned=true;
                        if(success){count=unchecked((uint)Marshal.ReadInt32(inFlightWritten.Address));success=count>0&&count<=(uint)requested;}
                    }
                }
                catch(Exception e) when(!Fatal(e)){}
                if(parent.fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){parent.Uncertain();throw new IOException("Readback uncertain.");}
                if(!BindingCurrent())
                {
                    Array.Clear(inFlight,0,inFlight.Length);if(pinHeld){inFlightPin.Free();pinHeld=false;}
                    if(inFlightWritten!=null){inFlightWritten.ReleaseAfterConfirmedCompletion();inFlightWritten=null;}
                    inFlight=null;parent.Uncertain();throw new IOException("Readback authority changed.");
                }
                Buffer.BlockCopy(inFlight,0,destination,offset,(int)count);remaining-=count;
                Array.Clear(inFlight,0,inFlight.Length);if(pinHeld){inFlightPin.Free();pinHeld=false;}
                if(inFlightWritten!=null){inFlightWritten.ReleaseAfterConfirmedCompletion();inFlightWritten=null;}
                inFlight=null;return (int)count;
            }
            // A block-plan flush acknowledges only this one OS call. The sink's
            // one-use authority binds it to the exact current plan record;
            // it does not mark the terminal flush or prove durable storage.
            internal bool TryBlockPlanFlush(ViveRawSessionJournalSink.NativeAuthority authority,out long sequence)
            {
                sequence=-1;
                if(!parent.sink.CanRunNative||!parent.OnOwner()||!BindingCurrent()||closed||terminal||authority==null||
                    !Object.ReferenceEquals(authority.Owner,parent.sink)||!authority.TryConsume(ViveRawSessionJournalSink.NativeStep.BlockPlanFlush,parent,this,null))return false;
                ViveRawIoFence.Permit permit;if(!parent.fence.TryReserve(ViveRawIoFence.IoOperation.BlockPlanFlush,out permit))return false;
                bool returned=false,success=false;try{
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(parent.sink.IsNativeFixture)ViveSyntheticNativeTrace.Called(ViveRawIoFence.IoOperation.BlockPlanFlush);
#endif
                    success=Native.Flush(handle);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(!success&&parent.sink.IsNativeFixture)ViveSyntheticNativeTrace.NativeError(ViveRawIoFence.IoOperation.BlockPlanFlush,Marshal.GetLastWin32Error());
#endif
                    returned=true;}catch(Exception e) when(!Fatal(e)){}
                if(parent.fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){parent.Uncertain();return false;}
                sequence=permit.Sequence;return true;
            }
            internal bool TryClose(ViveRawSessionJournalSink.NativeAuthority authority)
            {
                if(!parent.sink.CanRunNative||!parent.OnOwner()||!BindingCurrent()||closed||!flushed||!readbackComplete||parent.sourceWitness==null||!parent.sourceWitness.ReadyForOriginalClose||authority==null||
                    !Object.ReferenceEquals(authority.Owner,parent.sink)||!authority.TryConsume(ViveRawSessionJournalSink.NativeStep.Close,parent,this,null))return false;
                ViveRawIoFence.Permit permit;if(!parent.fence.TryReserve(ViveRawIoFence.IoOperation.Close,out permit))return false;
                bool returned=false,success=false;
                try{handle.Dispose();returned=true;success=handle.CloseOutcome==1;}catch(Exception e) when(!Fatal(e)){}
                if(parent.fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned){parent.Uncertain();return false;}
                closed=true;Volatile.Write(ref parent.fileClosed,1);return true;
            }
            internal bool TryIdentityStep(ViveRawSessionJournalSink.NativeAuthority authority,IdentityStep step)
            {
                if(!parent.sink.CanRunNative||!parent.OnOwner()||!BindingCurrent()||!flushed||!readbackComplete||
                    parent.sourceWitness==null||authority==null||!Object.ReferenceEquals(authority.Owner,parent.sink)||
                    !authority.TryConsume(ViveRawSessionJournalSink.NativeStep.IdentityWitness,parent,this,null))return false;
                ViveRawIoFence.Permit permit;
                var operation=(ViveRawIoFence.IoOperation)(23+(int)step);
                if(!parent.fence.TryReserve(operation,out permit)){parent.sourceWitness.Abort();return false;}
                bool returned=false,success=false;
                try{success=parent.sourceWitness.Run(step);returned=true;}catch(Exception e) when(!Fatal(e)){}
                if(parent.fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned)
                {parent.sourceWitness.Abort();parent.Uncertain();return false;}
                return true;
            }
        }

        internal sealed class RawNativeHandle:SafeHandleZeroOrMinusOneIsInvalid
        {
            int closeOutcome;internal RawNativeHandle(IntPtr value):base(true){SetHandle(value);}internal int CloseOutcome {get{return Volatile.Read(ref closeOutcome);}}
            protected override bool ReleaseHandle(){bool ok=Native.Close(handle);Volatile.Write(ref closeOutcome,ok?1:-1);return ok;}
        }
        internal static class Native
        {
            [StructLayout(LayoutKind.Sequential)]struct UnicodeString{internal ushort Length,MaximumLength;internal IntPtr Buffer;}
            [StructLayout(LayoutKind.Sequential)]struct Attributes{internal uint Length;internal IntPtr Root,Name;internal uint Flags;internal IntPtr Security,Qos;}
            [StructLayout(LayoutKind.Sequential)]struct IoStatus{internal IntPtr Status;internal UIntPtr Information;}
            [StructLayout(LayoutKind.Sequential)]internal struct FileId{internal ulong Volume;[MarshalAs(UnmanagedType.ByValArray,SizeConst=16)]internal byte[] Id;}
            [StructLayout(LayoutKind.Sequential)]internal struct FileInformation
            {internal uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,VolumeSerial,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
            [DllImport("ntdll.dll",ExactSpelling=true)]static extern int NtCreateFile(IntPtr handleOutput,uint access,IntPtr attributes,IntPtr io,IntPtr allocation,uint fileAttributes,uint share,uint disposition,uint options,IntPtr ea,uint eaLength);
            [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]internal static extern uint GetFileType(RawNativeHandle handle);
            [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetFileInformationByHandleEx(RawNativeHandle handle,int info,out FileId value,uint size);
            [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetFileInformationByHandle(RawNativeHandle handle,out FileInformation value);
            [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool WriteFile(RawNativeHandle handle,IntPtr data,uint length,IntPtr written,IntPtr overlapped);
            [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool ReadFile(RawNativeHandle handle,IntPtr data,uint length,IntPtr read,IntPtr overlapped);
            [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool SetFilePointerEx(RawNativeHandle handle,long distance,IntPtr newPosition,uint method);
            [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetFileSizeEx(RawNativeHandle handle,out long length);
            [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool FlushFileBuffers(RawNativeHandle handle);
            [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool CloseHandle(IntPtr handle);
            internal static bool GetId(RawNativeHandle h,out FileId id){return GetFileInformationByHandleEx(h,18,out id,(uint)Marshal.SizeOf(typeof(FileId)))&&ViveRawFileId.IsValid(id.Id);}
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            internal static bool GetIdForSyntheticDiagnostic(RawNativeHandle h,out FileId id)
            {
                bool ok=GetFileInformationByHandleEx(h,18,out id,(uint)Marshal.SizeOf(typeof(FileId)));
                if(!ok){int error=Marshal.GetLastWin32Error();ViveSyntheticNativeTrace.NativeError(ViveRawIoFence.IoOperation.PrepareDirectoryId,error);return false;}
                if(!ViveRawFileId.IsValid(id.Id)){ViveSyntheticNativeTrace.ValidationFailure(ViveRawIoFence.IoOperation.PrepareDirectoryId);return false;}
                return true;
            }
#endif
            internal static bool GetInfo(RawNativeHandle h,out FileInformation info){return GetFileInformationByHandle(h,out info);}
            internal static bool Write(RawNativeHandle h,IntPtr data,uint n,IntPtr written){return WriteFile(h,data,n,written,IntPtr.Zero);}
            internal static bool Read(RawNativeHandle h,IntPtr data,uint n,IntPtr read){return ReadFile(h,data,n,read,IntPtr.Zero);}
            internal static bool SeekStart(RawNativeHandle h){return SetFilePointerEx(h,0,IntPtr.Zero,0);}
            internal static bool GetSize(RawNativeHandle h,out long length){return GetFileSizeEx(h,out length);}
            internal static bool Flush(RawNativeHandle h){return FlushFileBuffers(h);}
            internal static bool Close(IntPtr h){return CloseHandle(h);}
            // STATUS_PENDING is not assumed impossible. Native buffers, the
            // returned handle and the root DangerousAddRef are retained for
            // process lifetime, with no polling, retry or attempted cleanup.
            // That is still not a completed operation or a close guarantee.
            sealed class PendingOpen
            {internal IntPtr Chars,Name,AttributesBlock;internal ViveRawNativeOutputStorage StatusBlock,HandleBlock;internal RawNativeHandle Root,Result;internal bool RootHeld;}
            static readonly object PendingGate=new object();static readonly List<PendingOpen> Pending=new List<PendingOpen>();
            internal static void Open(string name,RawNativeHandle root,uint access,uint share,uint disposition,uint options,ref RawNativeHandle destination,out int status,out ulong information)
            {
                IntPtr chars=IntPtr.Zero,ptr=IntPtr.Zero,attrsPtr=IntPtr.Zero;
                ViveRawNativeOutputStorage ioOutput=null,handleOutput=null;bool held=false,attempted=false,completed=false;
                PendingOpen value=null;
                try
                {
                    chars=Marshal.StringToHGlobalUni(name);var u=new UnicodeString{Length=checked((ushort)(name.Length*2)),MaximumLength=checked((ushort)((name.Length+1)*2)),Buffer=chars};
                    ptr=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));Marshal.StructureToPtr(u,ptr,false);
                    if(root!=null)root.DangerousAddRef(ref held);
                    var a=new Attributes{Length=(uint)Marshal.SizeOf(typeof(Attributes)),Root=root==null?IntPtr.Zero:root.DangerousGetHandle(),Name=ptr,Flags=0x40,Security=IntPtr.Zero,Qos=IntPtr.Zero};
                    attrsPtr=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Attributes)));Marshal.StructureToPtr(a,attrsPtr,false);
                    ioOutput=new ViveRawNativeOutputStorage(Marshal.SizeOf(typeof(IoStatus)));
                    handleOutput=new ViveRawNativeOutputStorage(IntPtr.Size);Marshal.WriteIntPtr(handleOutput.Address,new IntPtr(-1));
                    // Root and all unmanaged buffers are registered before
                    // entering the OS. An exception after entry remains an
                    // unknown operation and must not free those buffers.
                    value=new PendingOpen{Chars=chars,Name=ptr,AttributesBlock=attrsPtr,StatusBlock=ioOutput,HandleBlock=handleOutput,Root=root,RootHeld=held};
                    lock(PendingGate)Pending.Add(value);
                    attempted=true;
                    status=NtCreateFile(handleOutput.Address,access,attrsPtr,ioOutput.Address,IntPtr.Zero,0x80,share,disposition,options,IntPtr.Zero,0);
                    // Pending and every nonzero/contradictory result stay in
                    // the registry; do not read a possibly unfinished handle.
                    if(status!=0){information=0;return;}
                    var io=(IoStatus)Marshal.PtrToStructure(ioOutput.Address,typeof(IoStatus));
                    if(io.Status!=IntPtr.Zero){status=unchecked((int)0xC0000001);information=0;return;}
                    IntPtr raw=Marshal.ReadIntPtr(handleOutput.Address);
                    if(raw==IntPtr.Zero||raw==new IntPtr(-1)){status=unchecked((int)0xC0000001);information=0;return;}
                    value.Result=new RawNativeHandle(raw);destination=value.Result;
                    information=io.Information.ToUInt64();completed=true;return;
                }
                finally
                {
                    if(!attempted||completed)
                    {
                        if(value!=null)lock(PendingGate)Pending.Remove(value);
                        if(held)root.DangerousRelease();if(handleOutput!=null)handleOutput.ReleaseAfterConfirmedCompletion();if(ioOutput!=null)ioOutput.ReleaseAfterConfirmedCompletion();
                        if(attrsPtr!=IntPtr.Zero)Marshal.FreeHGlobal(attrsPtr);if(ptr!=IntPtr.Zero)Marshal.FreeHGlobal(ptr);if(chars!=IntPtr.Zero)Marshal.FreeHGlobal(chars);
                    }
                }
            }
        }
    }
}
