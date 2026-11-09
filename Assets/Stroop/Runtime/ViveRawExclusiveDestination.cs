using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Controlled development destination. Each request must additionally
    // bind a user-selected directory, current participant authorization and
    // an independently reviewed publication decision. FileShare.None is an
    // active-handle sharing mode, not confidentiality or a durability proof.
    internal sealed class ViveRawExclusiveDestination
    {
        internal enum Outcome { NotStarted, Staging, CloseReturned, Unknown }
        internal static bool ProductionGateOpen {get{return ViveControlledRecordingBuildPolicy.Available;}}
        // The standalone exception belongs only to the exact fixed-directory
        // synthetic instance. It is absent from Unity/player builds; the
        // installed factories and all product gates remain unchanged.
        internal bool LifecycleAvailable
        {
            get
            {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(IsIntegrationFixture)return true;
#endif
                return ProductionGateOpen&&sink!=null&&sink.ProductionGateOpen&&sink.CompletionGateOpen&&
                    ViveRawRelativeFileCapability.ProductionGateOpen&&admission!=null&&!admission.SyntheticOnly&&
                    (!CsvRequested||ViveRawReadableAnalysisCsv.ProductionGateOpen);
            }
        }
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        bool integrationFixture;
        internal bool IsIntegrationFixture {get{return integrationFixture&&sink!=null&&sink.IsIntegrationFixtureFor(admission,authority,selectedDirectory);}}
        internal static ViveRawExclusiveDestination CreateIntegrationFixtureForTest(ViveNamedSessionFrameAdmission admission,
            ViveRawSessionJournalSink sink,ViveRawRevocation shared)
        {
            if(sink==null)throw new ArgumentNullException("sink");
            string directory=sink.ExactIntegrationFixtureDirectory;
            if(!sink.IsIntegrationFixtureFor(admission,shared,directory)||sink.SelectedBudget==null||!shared.MayReserve)
                throw new InvalidOperationException("The exact synthetic source and authority are required.");
            long csvBytes=sink.IntegrationScenario==ViveRawSessionJournalSink.IntegrationFixtureScenario.CsvBudgetFailure?1024:33554432;
            var value=new ViveRawExclusiveDestination(admission,sink,shared,sink.SelectedBudget.MaximumBytes,60000,directory,csvBytes,60000);
            value.integrationFixture=true;
            if(!sink.TryArmInstalledDestination(value,admission,shared))throw new InvalidOperationException("Synthetic destination could not be armed.");
            return value;
        }
#endif
        static readonly object RetentionLock=new object();
        static readonly List<ViveRawExclusiveDestination> Retained=new List<ViveRawExclusiveDestination>();
        readonly ViveNamedSessionFrameAdmission admission;
        readonly ViveRawSessionJournalSink sink;
        readonly ViveRawRevocation authority;
        readonly long maximumBytes;
        readonly int cleanupTimeoutMilliseconds;
        readonly long csvMaximumBytes;
        readonly int csvCleanupTimeoutMilliseconds;
        readonly string selectedDirectory;
        readonly Thread requestOwner;
        ViveRawRelativeFileCapability capability;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        FileStream target;
        int closeAttempted;
#endif
        ViveRawPrivateStage stage;
        int state,reportAttempted,csvReportAttempted;
        ViveRawExclusiveDestination(ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink,ViveRawRevocation authority,long maximumBytes=0,int cleanupTimeoutMilliseconds=0,string selectedDirectory=null)
        {this.admission=admission;this.sink=sink;this.authority=authority;this.maximumBytes=maximumBytes;
            this.cleanupTimeoutMilliseconds=cleanupTimeoutMilliseconds;this.selectedDirectory=selectedDirectory;requestOwner=Thread.CurrentThread;}
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        void Keep(){lock(RetentionLock)if(!Retained.Contains(this))Retained.Add(this);}
        void ReleaseAfterConfirmedClose(){lock(RetentionLock)Retained.Remove(this);}
        // A previous CloseReturned is not continuing authority. A later
        // withdrawal changes this view to UNKNOWN; already exposed bytes
        // cannot be recalled by the application.
        internal Outcome State
        {
            get
            {
                if(authority.IsRevoked)return Outcome.Unknown;
                var current=(Outcome)Volatile.Read(ref state);
                if(current==Outcome.Staging&&stage!=null)
                {
                    var stageState=stage.State;
                    if(stageState==ViveRawPrivateStage.Outcome.Unavailable||stageState==ViveRawPrivateStage.Outcome.RevokedOrIoUnknown)
                        return Outcome.Unknown;
                }
                return current;
            }
        }
        // The old parameterless probe is deliberately not an authorization.
        internal static bool TryCreateInstalled(out ViveRawExclusiveDestination destination)
        {destination=null;return false;}

        // A configured owner request is only a software request for a local
        // output. It is not evidence of informed consent or authority to
        // publish data. Compile-time development availability is not proof
        // that independent research or actual hardware acceptance passed.
        // This method opens no directory or file and may only arm the exact
        // selected sink on its owner thread before its writer is started.
        internal static bool TryCreateInstalled(ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink,
            ViveRawRevocation authority,string selectedDirectory,long maximumBytes,int cleanupTimeoutMilliseconds,bool explicitOwnerRequest,
            out ViveRawExclusiveDestination destination)
        {
            destination=null;
            if(!ProductionGateOpen||!ViveRawRelativeFileCapability.ProductionGateOpen||!explicitOwnerRequest||
                admission==null||admission.SyntheticOnly||sink==null||authority==null||!sink.ProductionGateOpen||
                !sink.CompletionGateOpen||!admission.IoFence.MatchesAuthority(admission,authority)||
                !sink.HasExactSelectedDirectory(admission,admission.IoFence,selectedDirectory)||
                sink.SelectedBudget==null||maximumBytes<sink.SelectedBudget.MaximumBytes||
                maximumBytes>ViveRawByteBudget.MaximumSupportedBytes||cleanupTimeoutMilliseconds<1||
                cleanupTimeoutMilliseconds>3600000||!authority.MayReserve)return false;
            var candidate=new ViveRawExclusiveDestination(admission,sink,authority,maximumBytes,cleanupTimeoutMilliseconds,selectedDirectory);
            if(!sink.TryArmInstalledDestination(candidate,admission,authority))return false;
            destination=candidate;return true;
        }

        // A separate, explicitly bounded CSV request. No handle is opened
        // here; the same session and withdrawal authority own both targets.
        // This entry follows the same controlled development build policy.
        internal static bool TryCreateInstalledWithCsv(ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink,
            ViveRawRevocation authority,string selectedDirectory,long rawMaximumBytes,int rawCleanupTimeoutMilliseconds,
            long csvMaximumBytes,int csvCleanupTimeoutMilliseconds,bool explicitOwnerRequest,out ViveRawExclusiveDestination destination)
        {
            destination=null;
            if(!ProductionGateOpen||!ViveRawReadableAnalysisCsv.ProductionGateOpen||!ViveRawRelativeFileCapability.ProductionGateOpen||
                !explicitOwnerRequest||admission==null||admission.SyntheticOnly||sink==null||authority==null||
                !sink.ProductionGateOpen||!sink.CompletionGateOpen||!admission.IoFence.MatchesAuthority(admission,authority)||
                !sink.HasExactSelectedDirectory(admission,admission.IoFence,selectedDirectory)||sink.SelectedBudget==null||
                rawMaximumBytes<sink.SelectedBudget.MaximumBytes||rawMaximumBytes>ViveRawByteBudget.MaximumSupportedBytes||
                rawCleanupTimeoutMilliseconds<1||rawCleanupTimeoutMilliseconds>3600000||csvMaximumBytes<1024||
                csvMaximumBytes>ViveRawByteBudget.MaximumSupportedBytes||csvCleanupTimeoutMilliseconds<1||
                csvCleanupTimeoutMilliseconds>3600000||!authority.MayReserve)return false;
            var candidate=new ViveRawExclusiveDestination(admission,sink,authority,rawMaximumBytes,rawCleanupTimeoutMilliseconds,
                selectedDirectory,csvMaximumBytes,csvCleanupTimeoutMilliseconds);
            if(!sink.TryArmInstalledDestination(candidate,admission,authority))return false;
            destination=candidate;return true;
        }

        ViveRawExclusiveDestination(ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink,ViveRawRevocation authority,
            long maximumBytes,int cleanupTimeoutMilliseconds,string selectedDirectory,long csvMaximumBytes,int csvCleanupTimeoutMilliseconds)
            :this(admission,sink,authority,maximumBytes,cleanupTimeoutMilliseconds,selectedDirectory)
        {this.csvMaximumBytes=csvMaximumBytes;this.csvCleanupTimeoutMilliseconds=csvCleanupTimeoutMilliseconds;}
        internal bool CsvRequested {get{return csvMaximumBytes!=0;}}
        internal long CsvMaximumBytes {get{return csvMaximumBytes;}}
        internal int CsvCleanupTimeoutMilliseconds {get{return csvCleanupTimeoutMilliseconds;}}
        internal bool SourceCloseValidated(ViveRawPrivateStage value)
        {return LifecycleAvailable&&CsvRequested&&Object.ReferenceEquals(value,stage)&&sink.MayOpenInstalledCsv(this,value,admission,authority);}
        internal Stream OpenCsvTargetForStage(ViveRawPrivateStage value)
        {
            if(!SourceCloseValidated(value)||capability==null)throw new InvalidOperationException("CSV target is unavailable.");
            return capability.OpenInstalledCsvSiblingForPrivateStage(value,admission,this,authority);
        }

        // Writer-only preparation against the already verified directory.
        // Construct the deferred stage first, publish exact ownership, then
        // start it: no target factory can race ahead of ownership binding.
        internal bool TryStartForWriter(ViveRawRelativeFileCapability verifiedCapability,out ViveRawPrivateStage result)
        {
            result=null;
            if(!LifecycleAvailable||verifiedCapability==null||!sink.MayStartInstalledDestination(this,verifiedCapability,admission,authority))return false;
            capability=verifiedCapability;
            try
            {
                stage=ViveRawPrivateStage.CreateInstalledDeferred(this,admission,maximumBytes,CancellationToken.None,authority);
                if(stage==null||!stage.StartInstalled(this)){if(stage!=null)stage.Abandon();Volatile.Write(ref state,(int)Outcome.Unknown);return false;}
                Volatile.Write(ref state,(int)Outcome.Staging);result=stage;return true;
            }
            catch(OutOfMemoryException){if(stage!=null)stage.Abandon();Volatile.Write(ref state,(int)Outcome.Unknown);return false;}
            catch(Exception error) when(!Fatal(error))
            {if(stage!=null)stage.Abandon();Volatile.Write(ref state,(int)Outcome.Unknown);return false;}
        }
        internal bool CanStartStage(ViveRawPrivateStage value,ViveNamedSessionFrameAdmission expected,ViveRawRevocation shared)
        {return LifecycleAvailable&&Object.ReferenceEquals(value,stage)&&Object.ReferenceEquals(expected,admission)&&
            Object.ReferenceEquals(shared,authority)&&capability!=null&&sink.MayStartInstalledDestination(this,capability,admission,authority);}
        internal bool CanPrepareStage(ViveNamedSessionFrameAdmission expected,ViveRawRevocation shared)
        {return LifecycleAvailable&&stage==null&&Object.ReferenceEquals(expected,admission)&&Object.ReferenceEquals(shared,authority)&&
            capability!=null&&sink.MayStartInstalledDestination(this,capability,admission,authority);}
        internal Stream OpenTargetForStage(ViveRawPrivateStage value)
        {
            if(!LifecycleAvailable||!Object.ReferenceEquals(value,stage)||capability==null||
                !sink.BelongsTo(admission)||!admission.IoFence.MatchesAuthority(admission,authority))
                throw new InvalidOperationException("Installed private target is unavailable.");
            return capability.OpenSiblingForPrivateStage(value,admission);
        }
        internal bool CanCloseStage(ViveRawPrivateStage value,ViveNamedSessionFrameAdmission expected,ViveRawRevocation shared)
        {return LifecycleAvailable&&Object.ReferenceEquals(value,stage)&&Object.ReferenceEquals(expected,admission)&&
            Object.ReferenceEquals(shared,authority)&&sink.MayCloseInstalledDestination(this,value,admission,authority);}
        internal int CleanupTimeoutMilliseconds {get{return cleanupTimeoutMilliseconds;}}
        internal bool TryIdentityStep(ViveRawPrivateStage value,bool csv,ViveRawRelativeFileCapability.IdentityStep step)
        {
            bool eligible=LifecycleAvailable&&Object.ReferenceEquals(value,stage)&&capability!=null&&
                (!csv||CsvRequested)&&sink.MayRunInstalledIdentityStep(this,value,admission,authority,csv)&&
                value.HasCurrentTargetClaim(admission,admission.IoFence);
            if(!eligible)return false;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            if(IsIntegrationFixture&&!csv&&step==ViveRawRelativeFileCapability.IdentityStep.DirectoryBefore&&
                sink.IntegrationScenario==ViveRawSessionJournalSink.IntegrationFixtureScenario.ParticipantWithdrawalBeforeRawIdentity)
            {authority.RevokeForParticipant();admission.Withdraw();return false;}
#endif
            return capability.TrySiblingIdentityStep(value,admission,csv,step);
        }
        internal bool BelongsTo(ViveRawSessionJournalSink expectedSink,ViveNamedSessionFrameAdmission expectedAdmission,ViveRawRevocation shared)
        {return Object.ReferenceEquals(sink,expectedSink)&&Object.ReferenceEquals(admission,expectedAdmission)&&Object.ReferenceEquals(authority,shared);}
        // This records only a returned close call; it is not publication,
        // current consent, source completeness or storage durability.
        internal void MarkInstalledCloseReturned(ViveRawPrivateStage value)
        {if(LifecycleAvailable&&Object.ReferenceEquals(stage,value)&&!authority.IsRevoked)Volatile.Write(ref state,(int)Outcome.CloseReturned);}
        internal void MarkInstalledUnknown()
        {if(stage!=null)stage.Abandon();Volatile.Write(ref state,(int)Outcome.Unknown);}

        // Bind every reported field to this destination's actual closed
        // target and scanned source, not merely to another completed slot.
        // The caller must still own the one-shot report claim throughout.
        bool ValidateCandidateMetadata(bool csv,ViveRawRevocation shared,string path,ulong volume,string id,long bytes,string digest)
        {
            var cap=capability;var value=stage;
            if(!LifecycleAvailable||!Object.ReferenceEquals(shared,authority)||authority.IsRevoked||
                cap==null||value==null||(csv&&!CsvRequested)||String.IsNullOrEmpty(selectedDirectory)||
                !value.InstalledReportClaimLive(this)||!cap.IdentityObservedAfterClose(value,admission,csv))return false;
            try
            {
                string name,actualId,actualDigest;ulong actualVolume;long actualBytes;
                bool identity=csv?cap.TryGetClosedCsvIdentity(value,admission,out name,out actualVolume,out actualId):
                    cap.TryGetClosedSiblingIdentity(value,admission,out name,out actualVolume,out actualId);
                if(!identity||String.IsNullOrEmpty(name)||!String.Equals(Path.GetFileName(name),name,StringComparison.Ordinal)||
                    !value.TryGetInstalledVerifiedSummary(this,out actualBytes,out actualDigest))return false;
                return volume==actualVolume&&bytes==actualBytes&&
                    String.Equals(id,actualId,StringComparison.Ordinal)&&String.Equals(digest,actualDigest,StringComparison.Ordinal)&&
                    String.Equals(path,Path.Combine(selectedDirectory,name),StringComparison.Ordinal)&&
                    value.InstalledReportClaimLive(this)&&!authority.IsRevoked;
            }
            catch(Exception error) when(!Fatal(error)){return false;}
        }

        // The reported name was matched after close while an identity witness
        // still held the original object. It is not proof that a later lookup
        // still names that object, that consent remains current, or that bytes
        // reached durable media. Once handed to a caller it cannot be recalled.
        internal sealed class LocalCandidateReceipt
        {
            readonly ViveRawRevocation authority;
            readonly bool reopenedIdentityVerified;
            internal readonly string UnverifiedPath,TargetFileIdAtOpen,VerifiedChainDigest;
            internal readonly ulong TargetVolumeAtOpen;
            internal readonly long VerifiedBytes;
            internal bool WithdrawalNotObserved {get{return !authority.IsRevoked;}}
            internal bool ReopenedIdentityVerified {get{return reopenedIdentityVerified;}}
            internal bool Durable {get{return false;}}
            internal LocalCandidateReceipt(ViveRawRevocation authority,string path,ulong volume,string id,long bytes,string digest,
                ViveRawExclusiveDestination owner=null)
            {this.authority=authority;UnverifiedPath=path;TargetVolumeAtOpen=volume;TargetFileIdAtOpen=id;VerifiedBytes=bytes;VerifiedChainDigest=digest;
                reopenedIdentityVerified=owner!=null&&owner.ValidateCandidateMetadata(false,authority,path,volume,id,bytes,digest);}
        }
        // One explicit owner review may claim a candidate only after the
        // source, target and directory close calls returned and the pump's
        // shared completion claim returned. The report claim competes with
        // the participant's same withdrawal authority; an already-claimed
        // report may still escape before a later withdrawal becomes visible.
        internal bool TryGetInstalledLocalCandidate(bool explicitOwnerReview,out LocalCandidateReceipt receipt)
        {
            receipt=null;var value=stage;var directory=selectedDirectory;var cap=capability;
            if(!LifecycleAvailable||!explicitOwnerReview||!Object.ReferenceEquals(Thread.CurrentThread,requestOwner)||
                value==null||cap==null||String.IsNullOrEmpty(directory)||sink.CompletionState!=7||
                !sink.SourceCloseReturned||sink.DirectoryCleanupPending||State!=Outcome.CloseReturned||
                admission.IoFence.CompletionOutcome!=ViveRawRevocation.Outcome.CallsReturned||authority.IsRevoked||
                Interlocked.CompareExchange(ref reportAttempted,1,0)!=0)return false;
            if(!value.TryBeginInstalledReport(this))return false;
            try
            {
                string name,id,digest;ulong volume;long bytes;
                if(!value.TryGetInstalledVerifiedSummary(this,out bytes,out digest)||
                    !cap.TryGetClosedSiblingIdentity(value,admission,out name,out volume,out id)||
                    String.IsNullOrEmpty(name)||!String.Equals(Path.GetFileName(name),name,StringComparison.Ordinal)||
                    String.IsNullOrEmpty(id)||!value.InstalledReportClaimLive(this))return false;
                string path=Path.Combine(directory,name);
                if(!value.InstalledReportClaimLive(this))return false;
                var candidate=new LocalCandidateReceipt(authority,path,volume,id,bytes,digest,this);
                if(!candidate.ReopenedIdentityVerified||!value.InstalledReportClaimLive(this))return false;
                receipt=candidate;return true;
            }
            catch(Exception error) when(!Fatal(error)){return false;}
            finally{value.EndInstalledReport(this);}
        }

        // CSV has a distinct target identity and a separate one-shot review.
        // The source digest/length describe the VRAW that was converted, not
        // the size, durability or a later reopen of the CSV path.
        internal sealed class LocalCsvCandidateReceipt
        {
            readonly ViveRawRevocation authority;
            readonly bool reopenedIdentityVerified;
            internal readonly string UnverifiedPath,TargetFileIdAtOpen,SourceVerifiedChainDigest;
            internal readonly ulong TargetVolumeAtOpen;
            internal readonly long SourceVerifiedBytes;
            internal bool WithdrawalNotObserved {get{return !authority.IsRevoked;}}
            internal bool ReopenedIdentityVerified {get{return reopenedIdentityVerified;}}
            internal bool Durable {get{return false;}}
            internal LocalCsvCandidateReceipt(ViveRawRevocation shared,string path,ulong volume,string id,long bytes,string digest,
                ViveRawExclusiveDestination owner=null)
            {authority=shared;UnverifiedPath=path;TargetVolumeAtOpen=volume;TargetFileIdAtOpen=id;SourceVerifiedBytes=bytes;SourceVerifiedChainDigest=digest;
                reopenedIdentityVerified=owner!=null&&owner.ValidateCandidateMetadata(true,shared,path,volume,id,bytes,digest);}
        }
        internal bool TryGetInstalledLocalCsvCandidate(bool explicitOwnerReview,out LocalCsvCandidateReceipt receipt)
        {
            receipt=null;var value=stage;var directory=selectedDirectory;var cap=capability;
            if(!LifecycleAvailable||!CsvRequested||!explicitOwnerReview||!Object.ReferenceEquals(Thread.CurrentThread,requestOwner)||
                value==null||cap==null||String.IsNullOrEmpty(directory)||sink.CompletionState!=7||!sink.SourceCloseReturned||
                sink.DirectoryCleanupPending||State!=Outcome.CloseReturned||!value.InstalledCsvCloseSucceeded||
                admission.IoFence.CompletionOutcome!=ViveRawRevocation.Outcome.CallsReturned||authority.IsRevoked||
                Interlocked.CompareExchange(ref csvReportAttempted,1,0)!=0)return false;
            if(!value.TryBeginInstalledReport(this))return false;
            try
            {
                string name,id,digest;ulong volume;long bytes;
                if(!value.TryGetInstalledVerifiedSummary(this,out bytes,out digest)||
                    !cap.TryGetClosedCsvIdentity(value,admission,out name,out volume,out id)||
                    String.IsNullOrEmpty(name)||!String.Equals(Path.GetFileName(name),name,StringComparison.Ordinal)||
                    String.IsNullOrEmpty(id)||!value.InstalledReportClaimLive(this))return false;
                string path=Path.Combine(directory,name);
                if(!value.InstalledReportClaimLive(this))return false;
                var candidate=new LocalCsvCandidateReceipt(authority,path,volume,id,bytes,digest,this);
                if(!candidate.ReopenedIdentityVerified||!value.InstalledReportClaimLive(this))return false;
                receipt=candidate;return true;
            }
            catch(Exception error) when(!Fatal(error)){return false;}
            finally{value.EndInstalledReport(this);}
        }

#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        internal ViveRawPrivateStage StageForTest {get{return stage;}}
        internal static bool TryCreateSyntheticForTest(ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink,
            ViveRawRevocation authority,string directory,long maximumBytes,out ViveRawExclusiveDestination destination)
        {
            destination=null;
            const string allowed=""; // Public source: machine-only native fixture is disabled.
            if(admission==null||admission.IsLegacy||sink==null||authority==null||!admission.SyntheticOnly||!sink.IsNativeFixture||
                !sink.BelongsTo(admission)||!admission.IoFence.MatchesAuthority(admission,authority)||
                String.IsNullOrEmpty(allowed)||!String.Equals(directory,allowed,StringComparison.OrdinalIgnoreCase))return false;
            var value=new ViveRawExclusiveDestination(admission,sink,authority);
            destination=value;value.Keep();
            // Until CreateNew and stage setup both return, even an exception
            // leaves the create/handle outcome conservatively unknown.
            Volatile.Write(ref value.state,(int)Outcome.Unknown);
            try
            {
                string path=Path.Combine(directory,"synthetic-export.partial.vraw");
                // The stage binds the shared authority and claims the actual
                // CreateNew before this callback executes. A claim already in
                // flight at withdrawal has UNKNOWN outcome; never retry it.
                value.stage=new ViveRawPrivateStage(()=>{
                    value.target=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
                    return value.target;
                },maximumBytes,CancellationToken.None,authority);
                sink.ConfigureSyntheticPrivateStageForTest(value.stage);
                if(!authority.MayReserve||value.stage.State==ViveRawPrivateStage.Outcome.RevokedOrIoUnknown)
                {value.stage.Abandon();Volatile.Write(ref value.state,(int)Outcome.Unknown);return false;}
                Volatile.Write(ref value.state,(int)Outcome.Staging);
                if(!authority.MayReserve||value.stage.State==ViveRawPrivateStage.Outcome.RevokedOrIoUnknown)
                {value.stage.Abandon();Volatile.Write(ref value.state,(int)Outcome.Unknown);return false;}
                return true;
            }
            catch(Exception error) when(!Fatal(error))
            {
                if(value.stage!=null)value.stage.Abandon();
                Volatile.Write(ref value.state,(int)Outcome.Unknown);return false;
            }
        }
        internal bool TryCloseSyntheticAfterSinkForTest()
        {
            if(stage==null||!sink.IsSyntheticPrivateStageForTest(stage)||!sink.BelongsTo(admission)||
                !admission.IoFence.MatchesAuthority(admission,authority)||sink.CompletionState!=7||
                !stage.WorkerExited||stage.State!=ViveRawPrivateStage.Outcome.PrivateCandidate||
                Interlocked.CompareExchange(ref closeAttempted,1,0)!=0)return false;
            // Dispose can itself block, throw, or race with withdrawal. Set
            // UNKNOWN before claiming it, so even fatal exceptions cannot be
            // mistaken for a safely staged or published result.
            Volatile.Write(ref state,(int)Outcome.Unknown);
            bool closed=stage.TryCloseTargetAfterCandidate();
            if(!closed){Volatile.Write(ref state,(int)Outcome.Unknown);return false;}
            Volatile.Write(ref state,(int)Outcome.CloseReturned);ReleaseAfterConfirmedClose();return true;
        }
#endif
    }
}
