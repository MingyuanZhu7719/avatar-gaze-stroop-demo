using System;
using System.IO;
using System.Threading;
using AvatarStroop.Core;

#if VIVE_RAW_SYNTHETIC_TEST && UNITY_5_3_OR_NEWER
#error VIVE_RAW_SYNTHETIC_TEST is restricted to standalone synthetic test builds.
#endif

namespace AvatarStroop.Runtime
{
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
    // Numeric and enum-only, bounded standalone diagnostics. Never accepts
    // paths, event payloads, gaze samples, exception messages, or identities.
    internal static class ViveSyntheticNativeTrace
    {
        internal enum Stage { Append, Manifest, Encode, Budget, Lease, Prepare, Create, Verify, DataWrite, PlanFlush, Acknowledge, AppendException, AppendFailed, ProducerState, ProducerStatus, ProducerTicket, ProducerStamp, ProducerEpoch }
        static readonly string[] items=new string[4096];static int next;
        static void Put(string text){int index=Interlocked.Increment(ref next)-1;if(index>=0&&index<items.Length)Volatile.Write(ref items[index],text);}
        internal static void Mark(Stage stage){Put("stage="+stage);}
        internal static void Reserved(ViveRawIoFence.IoOperation op){Put("reserved="+op);}
        // Callsite entered is not proof the native function itself began:
        // wrappers may throw while allocating/marshalling first.
        internal static void Called(ViveRawIoFence.IoOperation op){Put("callsite_entered="+op);}
        internal static void NativeError(ViveRawIoFence.IoOperation op,int error){Put("native_error operation="+op+" code="+error);}
        internal static void ValidationFailure(ViveRawIoFence.IoOperation op){Put("validation_failed operation="+op);}
        internal static void NtStatus(ViveRawIoFence.IoOperation op,int status){Put("ntstatus operation="+op+" code="+status);}
        internal static void Finish(ViveRawIoFence.IoOperation op,bool returned,bool succeeded,ViveRawIoFence.IoOutcome outcome)
        {Put("finish="+op+" returned="+returned+" success="+succeeded+" outcome="+outcome);}
        internal static void Dump(){int count=Math.Min(Volatile.Read(ref next),items.Length);for(int i=0;i<count;i++){string item=Volatile.Read(ref items[i]);if(item!=null)Console.WriteLine("TRACE "+i+" "+item);}if(Volatile.Read(ref next)>items.Length)Console.WriteLine("TRACE truncated");}
    }
#endif
    // Controlled development raw capture, not an export/publisher. Only the
    // guarded installed entry creates it. Its schema retains the adapter's available
    // left/right/combined rays and head rotation, but lacks images, pupil and
    // head position; capture continuity and hardware timing remain unknown.
    internal sealed class ViveRawSessionJournalSink
    {
        readonly ViveNamedSessionFrameAdmission admission;readonly string selectedDirectory;ViveRawDirectoryLease directoryLease;
        readonly bool explicitDirectoryConfiguration;
        readonly Thread creatorThread;readonly ViveRawByteBudget budget;
        ViveRawRelativeFileCapability capability;ViveRawRelativeFileCapability.OwnedFile file;
        ViveRawExclusiveDestination installedDestination;
        ViveRawPrivateStage installedPrivateStage;
        bool installedCloseStarted;
        bool installedStageStartFailed;
        bool installedCsvCloseStarted;
        int installedSourceCloseValidated;
        System.Diagnostics.Stopwatch installedCleanupClock;
        Thread writerThread;int failed,cleanupState,openAttempted,completionState,sourceCloseReturned;long expectedOrdinal,bytesWritten;
        long confirmedTasks,confirmedEyes,confirmedStatuses,confirmedTaskCursor=-2,confirmedStatusSequence=-1;WriteProof issuedProof;
        NativeAuthority issuedNativeAuthority;
        BlockPlanWriteReceipt issuedBlockPlanReceipt;
        // Invoked exclusively by the in-memory synthetic publication path.
        // Neither hook is reachable from the native writer or real receipt.
        internal Action BeforeSyntheticReceiptPublishForTest {get;set;}
        internal Action AfterSyntheticReceiptPublishForTest {get;set;}
        double confirmedStatusAt=-1,latestObservedAt=-1;bool taskEnded,taskCompleted,endedPhase,extraTaskAfterEnd;
        byte[] previous=new byte[32];
        // Build availability only. Exact directory authority, cleanup and
        // independent readback remain mandatory at their operation boundaries.
        internal bool ProductionGateOpen {get{return ViveControlledRecordingBuildPolicy.Available&&(!admission.IsLegacy||ViveLegacyRawRecordingBuildPolicy.Available)&&
            (!admission.SessionContext.IsLegacyWoz||ViveLegacyWozRecordingBuildPolicy.Available);}}
        // Completion retains a separate gate and still requires each OS
        // operation's own revocable reservation and truthful receipt.
        internal bool CompletionGateOpen {get{return ProductionGateOpen;}}
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        internal enum IntegrationFixtureScenario { Success, ParticipantWithdrawalBeforeRawIdentity, CsvBudgetFailure, WithdrawBeforeReport }
        const string IntegrationFixtureRoot=""; // Public source: no native fixture directory is authorized.
        const string LegacyIntegrationFixtureRoot=""; // Public source: no native fixture directory is authorized.
        const string WozIntegrationFixtureRoot=""; // Public source: no native fixture directory is authorized.
        enum IntegrationFixtureKind { None, Named, Legacy, LegacyWoz }
        readonly IntegrationFixtureKind integrationFixtureKind;
        readonly IntegrationFixtureScenario integrationScenario;
        static int integrationFixtureCreated;
        internal IntegrationFixtureScenario IntegrationScenario {get{return integrationScenario;}}
        internal bool IsLegacyIntegrationFixture {get{return integrationFixtureKind==IntegrationFixtureKind.Legacy&&IsNativeFixture;}}
        internal bool IsWozIntegrationFixture {get{return integrationFixtureKind==IntegrationFixtureKind.LegacyWoz&&IsNativeFixture;}}
        internal string ExactIntegrationFixtureDirectory
        {get{return integrationFixtureKind==IntegrationFixtureKind.LegacyWoz?WozIntegrationFixtureDirectory(integrationScenario):
            integrationFixtureKind==IntegrationFixtureKind.Legacy?LegacyIntegrationFixtureDirectory(integrationScenario):
            integrationFixtureKind==IntegrationFixtureKind.Named?IntegrationFixtureDirectory(integrationScenario):null;}}
        ViveRawSessionJournalSink(ViveNamedSessionFrameAdmission admission,string directory,ViveRawByteBudget budget,
            IntegrationFixtureKind kind,IntegrationFixtureScenario scenario):this(admission,directory,budget,true)
        {integrationFixtureKind=kind;integrationScenario=scenario;nativeFixture=true;}
        internal static string IntegrationFixtureDirectory(IntegrationFixtureScenario scenario)
        {
            if(String.IsNullOrEmpty(IntegrationFixtureRoot))throw new InvalidOperationException("Native integration fixtures are disabled in the public source.");
            if(!Enum.IsDefined(typeof(IntegrationFixtureScenario),scenario))throw new ArgumentOutOfRangeException("scenario");
            return Path.Combine(IntegrationFixtureRoot,scenario.ToString());
        }
        internal static ViveRawSessionJournalSink CreateIntegrationFixtureForTest(ViveNamedSessionFrameAdmission admission,
            ViveRawByteBudget budget,IntegrationFixtureScenario scenario)
        {
            string directory=IntegrationFixtureDirectory(scenario);
            if(admission==null||admission.IsLegacy||!admission.SyntheticOnly||budget==null||!Directory.Exists(directory)||
                Directory.GetFileSystemEntries(directory).Length!=0)throw new InvalidOperationException("A fresh fixed synthetic directory is required.");
            for(var ancestor=new DirectoryInfo(directory);ancestor!=null;ancestor=ancestor.Parent)
                if((ancestor.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Synthetic directory ancestry is unavailable.");
            if(Interlocked.CompareExchange(ref integrationFixtureCreated,1,0)!=0)
                throw new InvalidOperationException("Only one integration fixture may be created per process.");
            return new ViveRawSessionJournalSink(admission,directory,budget,IntegrationFixtureKind.Named,scenario);
        }
        internal static string LegacyIntegrationFixtureDirectory(IntegrationFixtureScenario scenario)
        {
            if(String.IsNullOrEmpty(LegacyIntegrationFixtureRoot))throw new InvalidOperationException("Native integration fixtures are disabled in the public source.");
            if(!Enum.IsDefined(typeof(IntegrationFixtureScenario),scenario))throw new ArgumentOutOfRangeException("scenario");
            return Path.Combine(LegacyIntegrationFixtureRoot,scenario.ToString());
        }
        internal static ViveRawSessionJournalSink CreateLegacyIntegrationFixtureForTest(ViveNamedSessionFrameAdmission admission,
            ViveRawByteBudget budget,IntegrationFixtureScenario scenario)
        {
            // Validate the sealed source/design before even inspecting a path.
            if(admission==null||!admission.SyntheticOnly||!admission.IsLegacy||admission.SessionContext==null||
                !admission.SessionContext.IsLegacy||admission.SessionContext.IsLegacyWoz||budget==null)
                throw new InvalidOperationException("An explicit synthetic legacy context is required.");
            string directory=LegacyIntegrationFixtureDirectory(scenario);
            if(!Directory.Exists(directory))
                throw new InvalidOperationException("A fresh fixed synthetic legacy directory is required.");
            for(var ancestor=new DirectoryInfo(directory);ancestor!=null;ancestor=ancestor.Parent)
                if((ancestor.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Synthetic directory ancestry is unavailable.");
            if(Directory.GetFileSystemEntries(directory).Length!=0)
                throw new InvalidOperationException("A fresh fixed synthetic legacy directory is required.");
            if(Interlocked.CompareExchange(ref integrationFixtureCreated,1,0)!=0)
                throw new InvalidOperationException("Only one integration fixture may be created per process.");
            return new ViveRawSessionJournalSink(admission,directory,budget,IntegrationFixtureKind.Legacy,scenario);
        }
        internal static string WozIntegrationFixtureDirectory(IntegrationFixtureScenario scenario)
        {
            if(String.IsNullOrEmpty(WozIntegrationFixtureRoot))throw new InvalidOperationException("Native integration fixtures are disabled in the public source.");
            if(!Enum.IsDefined(typeof(IntegrationFixtureScenario),scenario))throw new ArgumentOutOfRangeException("scenario");
            return Path.Combine(WozIntegrationFixtureRoot,scenario.ToString());
        }
        internal static ViveRawSessionJournalSink CreateWozIntegrationFixtureForTest(ViveNamedSessionFrameAdmission admission,
            ViveRawByteBudget budget,IntegrationFixtureScenario scenario)
        {
            if(admission==null||!admission.SyntheticOnly||!admission.IsLegacy||admission.SessionContext==null||
                !admission.SessionContext.IsLegacyWoz||budget==null)
                throw new InvalidOperationException("An explicit synthetic WoZ recording context is required.");
            string directory=WozIntegrationFixtureDirectory(scenario);
            if(!Directory.Exists(directory))throw new InvalidOperationException("A fresh fixed WoZ fixture directory is required.");
            for(var ancestor=new DirectoryInfo(directory);ancestor!=null;ancestor=ancestor.Parent)
                if((ancestor.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Synthetic directory ancestry is unavailable.");
            if(Directory.GetFileSystemEntries(directory).Length!=0)
                throw new InvalidOperationException("A fresh fixed WoZ fixture directory is required.");
            if(Interlocked.CompareExchange(ref integrationFixtureCreated,1,0)!=0)
                throw new InvalidOperationException("Only one integration fixture may be created per process.");
            return new ViveRawSessionJournalSink(admission,directory,budget,IntegrationFixtureKind.LegacyWoz,scenario);
        }
        internal bool IsIntegrationFixtureFor(ViveNamedSessionFrameAdmission expected,ViveRawRevocation shared,string directory)
        {
            // The factory's immutable kind chooses the root. The supplied
            // path only compares against it and never selects an authority.
            bool legacy=integrationFixtureKind==IntegrationFixtureKind.Legacy||integrationFixtureKind==IntegrationFixtureKind.LegacyWoz;
            if(integrationFixtureKind==IntegrationFixtureKind.None)return false;
            string expectedDirectory=ExactIntegrationFixtureDirectory;
            return IsNativeFixture&&expected!=null&&expected.SyntheticOnly&&expected.IsLegacy==legacy&&
                Object.ReferenceEquals(admission,expected)&&admission.IoFence.MatchesAuthority(expected,shared)&&
                String.Equals(selectedDirectory,expectedDirectory,StringComparison.Ordinal)&&
                String.Equals(directory,selectedDirectory,StringComparison.Ordinal);
        }
#endif
        internal bool HasDestinationLifecycle {get{var value=Volatile.Read(ref installedDestination);return value!=null&&value.LifecycleAvailable;}}
#if VIVE_RAW_SYNTHETIC_TEST
        bool nativeFixture;
        internal bool IsNativeFixture {get{return nativeFixture;}}
        internal ViveNamedSessionFrameAdmission FixtureAdmission {get{return nativeFixture?admission:null;}}
        internal static ViveRawSessionJournalSink CreateNativeFixtureForTest(ViveNamedSessionFrameAdmission admission,string directory,ViveRawByteBudget budget)
        {
            const string allowed=""; // Public source: machine-only native fixture is disabled.
            if(admission==null||admission.IsLegacy||!admission.SyntheticOnly||String.IsNullOrEmpty(allowed)||!String.Equals(directory,allowed,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only the fixed synthetic fixture directory is permitted.");
            var sink=new ViveRawSessionJournalSink(admission,directory,budget,true);sink.nativeFixture=true;return sink;
        }
#else
        internal bool IsNativeFixture {get{return false;}}
#endif
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        // Synthetic fixture only: no Unity/product call site or real-data
        // authority. 1 captures, 2 exceeds the capture budget, 3 injects a
        // capture error. The only accessor returns a one-shot synthetic array
        // after the existing scan and complete close sequence have returned.
        int syntheticTeeMode;
        byte[] syntheticReadbackSnapshot;
        ViveRawPrivateStage syntheticPrivateStage;
        bool syntheticRelativeStageRequested;
        bool syntheticRelativeCloseStarted;
        bool syntheticRelativeCsvRequested,syntheticRelativeCsvCloseStarted;
        int syntheticSourceCloseValidated;
        long syntheticCsvMaximumBytes;
        int syntheticRelativeCleanupTimeoutMilliseconds;
        System.Diagnostics.Stopwatch syntheticRelativeCleanupClock;
        internal Action BeforeSyntheticRelativeStageConstructionForTest;
        internal Action BeforeSyntheticRelativeTargetCreateForTest;
        internal Action<string> AfterSyntheticRelativeTargetOpenForTest;
        internal Action<ViveRawSiblingStream> AfterSyntheticRelativeTargetUseForTest;
        internal ViveRawPrivateStage SyntheticRelativeStageForTest {get{return syntheticRelativeStageRequested?syntheticPrivateStage:null;}}
        internal void NotifySyntheticRelativeTargetOpenForTest(string relativeName,ViveRawSiblingStream target)
        {if(IsNativeFixture){var hook=AfterSyntheticRelativeTargetOpenForTest;if(hook!=null)hook(relativeName);
            var targetHook=AfterSyntheticRelativeTargetUseForTest;if(targetHook!=null)targetHook(target);}}
        internal void NotifySyntheticRelativeTargetCreateForTest()
        {if(IsNativeFixture){var hook=BeforeSyntheticRelativeTargetCreateForTest;if(hook!=null)hook();}}
        internal void ConfigureSyntheticRelativeStageForTest(int cleanupTimeoutMilliseconds=30000)
        {
            if(!IsNativeFixture||writerThread!=null||syntheticTeeMode!=0||syntheticPrivateStage!=null||syntheticRelativeStageRequested||
                cleanupTimeoutMilliseconds<1||cleanupTimeoutMilliseconds>3600000)
                throw new InvalidOperationException("Synthetic relative private staging unavailable.");
            syntheticRelativeCleanupTimeoutMilliseconds=cleanupTimeoutMilliseconds;syntheticRelativeStageRequested=true;
        }
        internal void ConfigureSyntheticRelativeCsvForTest(long csvMaximumBytes,int cleanupTimeoutMilliseconds=30000)
        {
            if(csvMaximumBytes<1024)throw new ArgumentOutOfRangeException("csvMaximumBytes");
            ConfigureSyntheticRelativeStageForTest(cleanupTimeoutMilliseconds);
            syntheticCsvMaximumBytes=csvMaximumBytes;syntheticRelativeCsvRequested=true;
        }
        internal void ConfigureSyntheticPrivateStageForTest(ViveRawPrivateStage stage)
        {
            if(!IsNativeFixture||stage==null||writerThread!=null||syntheticTeeMode!=0||syntheticPrivateStage!=null||syntheticRelativeStageRequested)
                throw new InvalidOperationException("Synthetic private staging unavailable.");
            syntheticPrivateStage=stage;
        }
        internal bool IsSyntheticPrivateStageForTest(ViveRawPrivateStage stage)
        {return IsNativeFixture&&stage!=null&&Object.ReferenceEquals(syntheticPrivateStage,stage);}
        internal bool MayStartSyntheticStageClose(ViveRawPrivateStage stage)
        {return IsNativeFixture&&syntheticRelativeStageRequested&&Volatile.Read(ref completionState)==8&&
            Object.ReferenceEquals(Thread.CurrentThread,writerThread)&&Object.ReferenceEquals(syntheticPrivateStage,stage);}
        internal void ConfigureSyntheticTeeForTest(int mode)
        {
            if (!IsNativeFixture || writerThread != null || mode < 1 || mode > 3 || syntheticTeeMode != 0 || syntheticPrivateStage != null || syntheticRelativeStageRequested)
                throw new InvalidOperationException("Synthetic tee setup unavailable.");
            syntheticTeeMode = mode;
        }
        internal byte[] TakeSyntheticTeeAfterCloseForTest()
        { return IsNativeFixture && Volatile.Read(ref completionState) == 7 ? Interlocked.Exchange(ref syntheticReadbackSnapshot, null) : null; }
        void ClearSyntheticTeeForTest()
        { var value = Interlocked.Exchange(ref syntheticReadbackSnapshot, null); if (value != null) Array.Clear(value, 0, value.Length);
            if(syntheticPrivateStage!=null)syntheticPrivateStage.Abandon(); }
#endif
        internal bool CanRunNative {get{return (ProductionGateOpen&&ViveRawRelativeFileCapability.ProductionGateOpen)||IsNativeFixture;}}
        internal bool CanCompleteNative {get{return CompletionGateOpen||IsNativeFixture;}}
        internal bool BelongsTo(ViveNamedSessionFrameAdmission value){return Object.ReferenceEquals(admission,value);}
        internal ViveRawByteBudget SelectedBudget {get{return budget;}}
        internal bool HasNativeConfiguration {get{return explicitDirectoryConfiguration&&selectedDirectory!=null&&budget!=null;}}
        // Only an explicit pre-start plan is a flush barrier. Ordinary trial,
        // response and eye records continue through the bounded FIFO without
        // waiting for an individual flush or display receipt.
        internal static bool IsPrestartPlan(JournalEvent value)
        {return value!=null&&value.Kind=="block_plan_prepared";}
        internal enum NativeStep { LeasePrepare,Prepare,Create,Verify,Data,BlockPlanFlush,Terminal,Flush,Readback,Close,CloseDirectory,CloseAncestor,IdentityWitness }
        internal enum BlockPlanReceiptPoll { Pending,Ready,Faulted }
        // A software/API receipt only. It is bound to the exact FIFO event,
        // admission and software run; it cannot prove display, consent,
        // complete eye capture or power-loss durability.
        internal sealed class BlockPlanWriteReceipt
        {
            readonly ViveRawSessionJournalSink sink;readonly JournalEvent planned;
            readonly bool fixtureOnly;
            internal readonly Guid SoftwareRunId;internal readonly long FifoOrdinal,WriteOperation,FlushOperation;
            internal Action AfterSyntheticClaimForTest {get;set;}
            internal bool FixtureOnly {get{return fixtureOnly;}}
#if VIVE_RAW_SYNTHETIC_TEST
            internal bool BelongsToNativeFixtureForTest(ViveRawSessionJournalSink value)
            {return fixtureOnly&&Object.ReferenceEquals(sink,value)&&sink.IsNativeFixture;}
#endif
            internal BlockPlanWriteReceipt(ViveRawSessionJournalSink sink,JournalEvent planned,Guid softwareRun,long ordinal,long writeOperation,long flushOperation,bool fixtureOnly=false)
            {this.sink=sink;this.planned=planned;SoftwareRunId=softwareRun;FifoOrdinal=ordinal;WriteOperation=writeOperation;FlushOperation=flushOperation;this.fixtureOnly=fixtureOnly;}
            internal bool Matches(ViveBlockPlanRequest request)
            {return sink.admission.TryValidateBlockPlanRequest(request,planned);}
            internal bool TryConsume(ViveBlockPlanRequest request){return Consume(request,false);}
            // This route is never accepted by the production receipt method or
            // native capability; it exercises only a synthetic memory slot.
            internal bool TryConsumeSyntheticForTest(ViveBlockPlanRequest request)
            {
                return Consume(request,true);
            }
            bool Consume(ViveBlockPlanRequest request,bool synthetic)
            {
                if(synthetic!=fixtureOnly||(synthetic?!sink.admission.SyntheticOnly:!sink.ProductionGateOpen)||
                    !Object.ReferenceEquals(Thread.CurrentThread,sink.creatorThread)||
                    Volatile.Read(ref sink.failed)!=0||sink.admission.IoFence.IsRevoked||!Matches(request)||
                    !Object.ReferenceEquals(Interlocked.CompareExchange(ref sink.issuedBlockPlanReceipt,null,this),this))return false;
                if(synthetic){var hook=AfterSyntheticClaimForTest;if(hook!=null)hook();}
                // A concurrent withdrawal or task mutation can invalidate the
                // receipt after it is consumed; it must never be replayed.
                return Volatile.Read(ref sink.failed)==0&&!sink.admission.IoFence.IsRevoked&&Matches(request);
            }
        }
        internal BlockPlanReceiptPoll TryGetBlockPlanReceipt(ViveBlockPlanRequest request,out BlockPlanWriteReceipt result)
        {
            result=null;
            if(!ProductionGateOpen||request==null||!Object.ReferenceEquals(Thread.CurrentThread,creatorThread)||
                Volatile.Read(ref failed)!=0||admission.IoFence.IsRevoked)return BlockPlanReceiptPoll.Faulted;
            var candidate=Volatile.Read(ref issuedBlockPlanReceipt);
            if(candidate==null)return BlockPlanReceiptPoll.Pending;
            if(candidate.FixtureOnly||!candidate.Matches(request)||Volatile.Read(ref failed)!=0||admission.IoFence.IsRevoked)
            {Fail(null);return BlockPlanReceiptPoll.Faulted;}
            result=candidate;return BlockPlanReceiptPoll.Ready;
        }
        internal void RevokeBlockPlanWriteReceipt(){Interlocked.Exchange(ref issuedBlockPlanReceipt,null);}
        // Permanently synthetic, no file, no native permit and never accepted
        // by the product receipt API. Tests exercise the real slot/validation
        // code without manufacturing a production Write/Flush claim.
        internal bool TryIssueSyntheticBlockPlanReceiptForTest(JournalEvent planned,long ordinal,long writeOperation,long flushOperation)
        {
            if(!admission.SyntheticOnly||ProductionGateOpen||IsNativeFixture||!Object.ReferenceEquals(Thread.CurrentThread,creatorThread)||
                Volatile.Read(ref failed)!=0||admission.IoFence.IsRevoked||planned==null||planned.Kind!="block_plan_prepared"||ordinal<0||writeOperation<0||flushOperation<=writeOperation)return false;
            var value=new BlockPlanWriteReceipt(this,planned,Guid.Empty,ordinal,writeOperation,flushOperation,true);
            return PublishBlockPlanReceipt(value,true);
        }
        // Common single-slot publication. Native callers reach this only after
        // FIFO ACK and all corresponding task bookkeeping have completed.
        bool PublishBlockPlanReceipt(BlockPlanWriteReceipt value,bool synthetic)
        {
            if(value==null||value.FixtureOnly!=synthetic||
                (synthetic?(!admission.SyntheticOnly||ProductionGateOpen):!ProductionGateOpen)||
                !Object.ReferenceEquals(Thread.CurrentThread,synthetic&&!IsNativeFixture?creatorThread:writerThread))return false;
            if(synthetic)
            {
                try{var before=BeforeSyntheticReceiptPublishForTest;if(before!=null)before();}
                catch(Exception e) when(!(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException))
                {admission.IoFence.Abort();return false;}
            }
            if(Volatile.Read(ref failed)!=0||admission.IoFence.IsRevoked||
                Interlocked.CompareExchange(ref issuedBlockPlanReceipt,value,null)!=null)return false;
            if(synthetic)
            {
                try{var after=AfterSyntheticReceiptPublishForTest;if(after!=null)after();}
                catch(Exception e) when(!(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException))
                {admission.IoFence.Abort();Interlocked.CompareExchange(ref issuedBlockPlanReceipt,null,value);return false;}
            }
            if(Volatile.Read(ref failed)!=0||admission.IoFence.IsRevoked)
            {Interlocked.CompareExchange(ref issuedBlockPlanReceipt,null,value);return false;}
            return true;
        }
        internal BlockPlanReceiptPoll TryGetSyntheticBlockPlanReceiptForTest(ViveBlockPlanRequest request,out BlockPlanWriteReceipt result)
        {
            result=null;if(!admission.SyntheticOnly||ProductionGateOpen||request==null||!Object.ReferenceEquals(Thread.CurrentThread,creatorThread)||
                Volatile.Read(ref failed)!=0||admission.IoFence.IsRevoked)return BlockPlanReceiptPoll.Faulted;
            var value=Volatile.Read(ref issuedBlockPlanReceipt);if(value==null)return BlockPlanReceiptPoll.Pending;
            if(!value.FixtureOnly||!value.Matches(request)||Volatile.Read(ref failed)!=0||admission.IoFence.IsRevoked)return BlockPlanReceiptPoll.Faulted;
            result=value;return BlockPlanReceiptPoll.Ready;
        }
        // This is an in-process misuse guard, not protection against malicious
        // reflection or a compromised process. Only this sink can publish a
        // live instance in its private one-use slot; a separately constructed
        // object is never an authority to write arbitrary bytes.
        internal sealed class NativeAuthority
        {
            readonly ViveRawSessionJournalSink owner;readonly NativeStep step;
            readonly ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;readonly object record;
            readonly ViveRawRelativeFileCapability capability;readonly ViveRawRelativeFileCapability.OwnedFile file;
            readonly byte[] frame;readonly long water,cursor;readonly double at;
            internal NativeAuthority(ViveRawSessionJournalSink owner,NativeStep step,ViveNamedSessionFrameAdmission.ConsumerReceipt receipt,
                object record,ViveRawRelativeFileCapability capability,ViveRawRelativeFileCapability.OwnedFile file,byte[] frame,long water,long cursor,double at)
            {this.owner=owner;this.step=step;this.receipt=receipt;this.record=record;this.capability=capability;this.file=file;this.frame=frame;this.water=water;this.cursor=cursor;this.at=at;}
            internal ViveRawSessionJournalSink Owner {get{return owner;}}
            internal bool TryConsume(NativeStep requested,ViveRawRelativeFileCapability target,ViveRawRelativeFileCapability.OwnedFile targetFile,byte[] bytes)
            {
                if(requested!=step||!owner.CanRunNative||owner.admission.IoFence.IsRevoked||
                    !Object.ReferenceEquals(Thread.CurrentThread,owner.writerThread)||
                    !Object.ReferenceEquals(capability,target)||!Object.ReferenceEquals(file,targetFile)||!Object.ReferenceEquals(frame,bytes)||
                    !Object.ReferenceEquals(Volatile.Read(ref owner.issuedNativeAuthority),this))return false;
                if(step!=NativeStep.LeasePrepare&&step!=NativeStep.Prepare&&!Object.ReferenceEquals(owner.capability,target))return false;
                if((step==NativeStep.Data||step==NativeStep.BlockPlanFlush||step==NativeStep.Terminal||step==NativeStep.Flush||step==NativeStep.Readback||step==NativeStep.Close||step==NativeStep.IdentityWitness)&&
                    !Object.ReferenceEquals(owner.file,targetFile))return false;
                if(step==NativeStep.LeasePrepare||step==NativeStep.Prepare||step==NativeStep.Create||step==NativeStep.Verify||step==NativeStep.Data||step==NativeStep.BlockPlanFlush)
                {
                    if(!owner.admission.ConsumerReservationCurrent(receipt,record))return false;
                }
                else
                {
                    long w,c;double t;
                    if(!owner.CanCompleteNative||!owner.admission.TryGetDrainedSealBoundary(out w,out c,out t)||w!=water||c!=cursor||t!=at)return false;
                }
                if(Interlocked.CompareExchange(ref owner.issuedNativeAuthority,null,this)!=this)return false;
                return !owner.admission.IoFence.IsRevoked;
            }
        }
        internal bool HasExactNativeConfiguration(ViveNamedSessionFrameAdmission expected,ViveRawIoFence fence,ViveRawDirectoryLease lease,string directory)
        {return HasNativeConfiguration&&Object.ReferenceEquals(admission,expected)&&admission.IsIoFenceAuthority(fence)&&
            Object.ReferenceEquals(directoryLease,lease)&&lease!=null&&lease.IsFencedFor(admission,fence)&&String.Equals(selectedDirectory,directory,StringComparison.Ordinal);}
        internal bool HasExactSelectedDirectory(ViveNamedSessionFrameAdmission expected,ViveRawIoFence fence,string directory)
        {return HasNativeConfiguration&&directoryLease==null&&Object.ReferenceEquals(admission,expected)&&admission.IsIoFenceAuthority(fence)&&
            String.Equals(selectedDirectory,directory,StringComparison.Ordinal);}
        // Arm an explicit local export request before the writer starts. This
        // reserves no file and does not establish participant consent or
        // publication authority. Ordinary builds remain closed.
        internal bool TryArmInstalledDestination(ViveRawExclusiveDestination value,ViveNamedSessionFrameAdmission expected,ViveRawRevocation authority)
        {
            return value!=null&&value.LifecycleAvailable&&expected!=null&&
                Object.ReferenceEquals(Thread.CurrentThread,creatorThread)&&writerThread==null&&
                Object.ReferenceEquals(admission,expected)&&admission.IoFence.MatchesAuthority(expected,authority)&&
                value.BelongsTo(this,expected,authority)&&
                !admission.IoFence.IsRevoked&&Interlocked.CompareExchange(ref installedDestination,value,null)==null;
        }
        internal bool MayStartInstalledDestination(ViveRawExclusiveDestination value,ViveRawRelativeFileCapability expected,
            ViveNamedSessionFrameAdmission expectedAdmission,ViveRawRevocation authority)
        {return value!=null&&value.LifecycleAvailable&&
            value!=null&&Object.ReferenceEquals(Volatile.Read(ref installedDestination),value)&&
            Object.ReferenceEquals(expected,capability)&&Object.ReferenceEquals(admission,expectedAdmission)&&
            admission.IoFence.MatchesAuthority(expectedAdmission,authority)&&!admission.IoFence.IsRevoked&&
            Object.ReferenceEquals(Thread.CurrentThread,writerThread)&&Volatile.Read(ref completionState)==3;}
        internal bool MayCloseInstalledDestination(ViveRawExclusiveDestination value,ViveRawPrivateStage stage,
            ViveNamedSessionFrameAdmission expectedAdmission,ViveRawRevocation authority)
        {return value!=null&&value.LifecycleAvailable&&
            Object.ReferenceEquals(Volatile.Read(ref installedDestination),value)&&Object.ReferenceEquals(installedPrivateStage,stage)&&
            Object.ReferenceEquals(admission,expectedAdmission)&&admission.IoFence.MatchesAuthority(expectedAdmission,authority)&&
            !admission.IoFence.IsRevoked&&Object.ReferenceEquals(Thread.CurrentThread,writerThread)&&
            Volatile.Read(ref sourceCloseReturned)!=0&&Volatile.Read(ref completionState)==8;}
        internal bool MayRunInstalledIdentityStep(ViveRawExclusiveDestination value,ViveRawPrivateStage stage,
            ViveNamedSessionFrameAdmission expectedAdmission,ViveRawRevocation authority,bool csv)
        {
            return value!=null&&value.LifecycleAvailable&&
                Object.ReferenceEquals(Volatile.Read(ref installedDestination),value)&&Object.ReferenceEquals(installedPrivateStage,stage)&&
                Object.ReferenceEquals(admission,expectedAdmission)&&admission.IoFence.MatchesAuthority(expectedAdmission,authority)&&
                !admission.IoFence.IsRevoked&&stage!=null&&stage.IsIdentityCloseWorker(csv)&&
                Volatile.Read(ref sourceCloseReturned)!=0&&Volatile.Read(ref completionState)==8;
        }
        // Only the installed stage for this sink may observe the post-close
        // revalidated seal. It is stronger than SourceCloseReturned alone.
        internal bool MayOpenInstalledCsv(ViveRawExclusiveDestination value,ViveRawPrivateStage stage,
            ViveNamedSessionFrameAdmission expectedAdmission,ViveRawRevocation authority)
        {return value!=null&&value.LifecycleAvailable&&value.CsvRequested&&
            Object.ReferenceEquals(Volatile.Read(ref installedDestination),value)&&Object.ReferenceEquals(installedPrivateStage,stage)&&
            Object.ReferenceEquals(admission,expectedAdmission)&&admission.IoFence.MatchesAuthority(expectedAdmission,authority)&&
            !admission.IoFence.IsRevoked&&Volatile.Read(ref completionState)==8&&Volatile.Read(ref installedSourceCloseValidated)!=0;}
        NativeAuthority IssueNative(NativeStep step,ViveNamedSessionFrameAdmission.ConsumerReceipt receipt,object record,
            ViveRawRelativeFileCapability target,ViveRawRelativeFileCapability.OwnedFile targetFile,byte[] frame,long water=-1,long cursor=-1,double at=-1)
        {
            var value=new NativeAuthority(this,step,receipt,record,target,targetFile,frame,water,cursor,at);
            if(Interlocked.CompareExchange(ref issuedNativeAuthority,value,null)!=null)throw new InvalidOperationException("Native authority already outstanding.");
            return value;
        }
        internal sealed class WriteProof
        {
            readonly ViveRawSessionJournalSink sink;readonly ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
            readonly object record;readonly long ordinal;
            internal WriteProof(ViveRawSessionJournalSink sink,ViveNamedSessionFrameAdmission.ConsumerReceipt receipt,object record,long ordinal)
            {this.sink=sink;this.receipt=receipt;this.record=record;this.ordinal=ordinal;}
            internal bool TryConsume(ViveNamedSessionFrameAdmission owner,ViveNamedSessionFrameAdmission.ConsumerReceipt value)
            {
                return Object.ReferenceEquals(owner,sink.admission)&&Object.ReferenceEquals(value,receipt)&&
                    value!=null&&value.Ordinal==ordinal&&Object.ReferenceEquals(value.Record,record)&&
                    Object.ReferenceEquals(Interlocked.CompareExchange(ref sink.issuedProof,null,this),this);
            }
        }
        // Compatibility for closed-gate tests only. It does not authorize an
        // absolute-path open and supplies neither a lease nor a byte budget.
        internal ViveRawSessionJournalSink(ViveNamedSessionFrameAdmission admission,string path):this(admission,path,null){}
        internal ViveRawSessionJournalSink(ViveNamedSessionFrameAdmission admission,string path,ViveRawByteBudget budget)
        {
            if(admission==null||String.IsNullOrEmpty(path)||!Path.IsPathRooted(path)||path.Length<4||path[1]!=':'||
                (path[2]!='\\'&&path[2]!='/')||!String.Equals(Path.GetFullPath(path),path,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An absolute local raw journal path is required.");
            this.admission=admission;this.budget=budget;creatorThread=Thread.CurrentThread;
        }
        // Legacy pre-opened lease is retained only for closed-gate tests. It
        // cannot configure a real native writer; its opening was not fenced.
        internal ViveRawSessionJournalSink(ViveNamedSessionFrameAdmission admission,ViveRawDirectoryLease lease,string directory,ViveRawByteBudget budget)
        {
            if(admission==null||lease==null||budget==null)throw new ArgumentNullException("Explicit admission, directory lease and budget required.");
            this.admission=admission;directoryLease=lease;selectedDirectory=ViveRawDirectoryLease.NormalizeCandidate(directory);
            this.budget=budget;creatorThread=Thread.CurrentThread;
        }
        // Pure selection only; no path is opened on the UI thread. The exact
        // named directory is later opened on the bound writer with I/O permits.
        // This declaration is not participant authorization or consent.
        internal ViveRawSessionJournalSink(ViveNamedSessionFrameAdmission admission,string directory,ViveRawByteBudget budget,bool explicitSelection)
        {
            if(admission==null||budget==null)throw new ArgumentNullException("admission/budget");
            if(!explicitSelection)throw new ArgumentException("Explicit directory configuration required.");
            this.admission=admission;this.budget=budget;selectedDirectory=ViveRawDirectoryLease.NormalizeCandidate(directory);
            explicitDirectoryConfiguration=true;creatorThread=Thread.CurrentThread;
        }
        // No caller-provided bytes, streams, callbacks, or claimed success are
        // accepted. One receipt has at most one open/write attempt.
        internal bool AppendOne(ViveNamedSessionFrameAdmission.ConsumerReceipt receipt)
        {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            if(IsNativeFixture)ViveSyntheticNativeTrace.Mark(ViveSyntheticNativeTrace.Stage.Append);
#endif
            if(!CanRunNative||(!ViveRawRelativeFileCapability.ProductionGateOpen&&!IsNativeFixture)||!HasNativeConfiguration)
            {Fail(receipt);return false;}
            if(Object.ReferenceEquals(Thread.CurrentThread,creatorThread)||Volatile.Read(ref failed)!=0||receipt==null||
                !Object.ReferenceEquals(receipt.Owner,admission)||Interlocked.Exchange(ref receipt.WriteStarted,1)!=0)
            {Fail(receipt);return false;}
            var thread=Thread.CurrentThread;var bound=Interlocked.CompareExchange(ref writerThread,thread,null);
            if(bound!=null&&!Object.ReferenceEquals(bound,thread)){Fail(receipt);return false;}
            object record=Volatile.Read(ref receipt.Record);
            if(receipt.Ordinal!=expectedOrdinal||!admission.ConsumerReservationCurrent(receipt,record)){Fail(receipt);return false;}
            try
            {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(IsNativeFixture)ViveSyntheticNativeTrace.Mark(ViveSyntheticNativeTrace.Stage.Manifest);
#endif
                ViveNamedSessionFrameAdmission.Manifest manifest;
                if(!admission.TryGetManifestForReservedConsumer(receipt,record,out manifest)){Fail(receipt);return false;}
                if(expectedOrdinal==0)confirmedTaskCursor=manifest.TaskBaseline;
                var taskRecord=record as JournalEvent;
                var eyeRecord=record as ViveNamedSessionFrameAdmission.Frame;
                var statusRecord=record as ViveProEyeWorker.ObservedEyeStatus;
                if(taskRecord==null&&eyeRecord==null&&statusRecord==null)throw new InvalidOperationException("Unknown raw record.");
                bool isBlockPlan=IsPrestartPlan(taskRecord);
                if(isBlockPlan&&Volatile.Read(ref issuedBlockPlanReceipt)!=null)
                    throw new InvalidOperationException("Previous block plan receipt not consumed.");
                if(taskRecord!=null&&(confirmedTasks==Int64.MaxValue||confirmedTaskCursor==Int64.MaxValue||taskRecord.Sequence!=confirmedTaskCursor+1))throw new InvalidOperationException("Task sequence unavailable.");
                if(eyeRecord!=null&&(taskEnded||confirmedEyes==Int64.MaxValue||eyeRecord.TaskEventCursor!=confirmedTaskCursor))throw new InvalidOperationException("Eye task context unavailable.");
                if(statusRecord!=null&&(confirmedStatuses==Int64.MaxValue||confirmedStatusSequence==Int64.MaxValue||
                    statusRecord.Sequence!=confirmedStatusSequence+1||statusRecord.PollReturnLocalSeconds<manifest.ChoiceTime||
                    statusRecord.PollReturnLocalSeconds<confirmedStatusAt))
                    throw new InvalidOperationException("Status sequence unavailable.");
                byte[] frame,digest;bool first=expectedOrdinal==0;int actualHeaderBytes=0;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(IsNativeFixture)ViveSyntheticNativeTrace.Mark(ViveSyntheticNativeTrace.Stage.Encode);
#endif
                byte[] payload=statusRecord==null?ViveRawSessionJournalCodec.RecordPayload(checked(expectedOrdinal+1),record,manifest):
                    ViveRawSessionJournalCodec.StatusPayloadV3(checked(expectedOrdinal+1),statusRecord);
                if(first)
                {
                    byte[] headDigest;byte[] head=FrameForDesign(ViveRawSessionJournalCodec.CurrentManifestPayload(manifest),previous,true,out headDigest);
                    actualHeaderBytes=head.Length;
                    byte[] body=FrameForDesign(payload,headDigest,false,out digest);
                    frame=new byte[head.Length+body.Length];Buffer.BlockCopy(head,0,frame,0,head.Length);Buffer.BlockCopy(body,0,frame,head.Length,body.Length);
                }
                else frame=FrameForDesign(payload,previous,false,out digest);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(IsNativeFixture)ViveSyntheticNativeTrace.Mark(ViveSyntheticNativeTrace.Stage.Budget);
#endif
                if(!budget.FitsData(bytesWritten,frame.Length)||
                    !admission.VerifyQueuedFrameBudget(receipt,record,budget,frame.Length,actualHeaderBytes))
                    throw new InvalidOperationException("Raw journal budget unavailable.");
                if(!admission.ConsumerReservationCurrent(receipt,record)){Fail(receipt);return false;}
                // The capability creates a unique child relative to its
                // pinned directory handle; it owns each native permit/handle.
                // Neither selection nor identity is participant authorization.
                if(file==null)
                {
                    Volatile.Write(ref openAttempted,1);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(IsNativeFixture)ViveSyntheticNativeTrace.Mark(ViveSyntheticNativeTrace.Stage.Lease);
#endif
                    var leaseAuthority=IssueNative(NativeStep.LeasePrepare,receipt,record,null,null,null);
                    if(!ViveRawDirectoryLease.TryOpenExistingFenced(admission,admission.IoFence,selectedDirectory,leaseAuthority,out directoryLease)||directoryLease==null)
                    {Fail(receipt);return false;}
                    var prepare=IssueNative(NativeStep.Prepare,receipt,record,null,null,null);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(IsNativeFixture)ViveSyntheticNativeTrace.Mark(ViveSyntheticNativeTrace.Stage.Prepare);
#endif
                    if(!ViveRawRelativeFileCapability.TryPrepare(admission,admission.IoFence,directoryLease,selectedDirectory,prepare,out capability)||capability==null||
                        !capability.TryCreate(IssueNative(NativeStep.Create,receipt,record,capability,null,null))||
                        !capability.TryVerifyAndTransfer(IssueNative(NativeStep.Verify,receipt,record,capability,null,null),out file)||file==null)
                    {Fail(receipt);return false;}
                }
                // Even this recheck cannot eliminate the race to the native
                // call; the capability's exact permit controls each operation.
                if(!admission.ConsumerReservationCurrent(receipt,record)){Fail(receipt);return false;}
                // A returned Write is only API acceptance; it is not a flush,
                // clean close, durability proof or complete coverage assertion.
                long writeOperation,flushOperation=-1;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(IsNativeFixture)ViveSyntheticNativeTrace.Mark(ViveSyntheticNativeTrace.Stage.DataWrite);
#endif
                if(!file.TryWrite(frame,false,IssueNative(NativeStep.Data,receipt,record,capability,file,frame),out writeOperation)||writeOperation<0)
                {Fail(receipt);return false;}
                if(isBlockPlan&&(!file.TryBlockPlanFlush(IssueNative(NativeStep.BlockPlanFlush,receipt,record,capability,file,null),out flushOperation)||flushOperation<=writeOperation))
                {Fail(receipt);return false;}
                bytesWritten+=frame.Length;previous=digest;expectedOrdinal++;
                var proof=new WriteProof(this,receipt,record,receipt.Ordinal);issuedProof=proof;
                if(!admission.TryAcknowledgeConsumer(receipt,proof)){Fail(receipt);return false;}
                // Only this writer's acknowledged FIFO items advance counts.
                if(taskRecord!=null)
                {
                    confirmedTasks++;confirmedTaskCursor=taskRecord.Sequence;latestObservedAt=Math.Max(latestObservedAt,taskRecord.Time);
                    string value;
                    if(taskEnded)
                    {
                        if(!endedPhase&&taskRecord.Kind=="phase_changed"&&taskRecord.Fields.TryGetValue("to",out value)&&value=="Ended")endedPhase=true;
                        else extraTaskAfterEnd=true;
                    }
                    if(taskRecord.Kind=="session_ended")
                    {if(taskEnded)extraTaskAfterEnd=true;taskEnded=true;taskCompleted=taskRecord.Fields.TryGetValue("reason",out value)&&value=="Completed";}
                }
                else if(eyeRecord!=null){confirmedEyes++;latestObservedAt=Math.Max(latestObservedAt,eyeRecord.CheckedAt);}
                else{confirmedStatuses++;confirmedStatusSequence=statusRecord.Sequence;confirmedStatusAt=statusRecord.PollReturnLocalSeconds;latestObservedAt=Math.Max(latestObservedAt,confirmedStatusAt);}
                if(isBlockPlan)
                {
                    var blockPlanReceipt=new BlockPlanWriteReceipt(this,taskRecord,manifest.SoftwareRunId,receipt.Ordinal,writeOperation,flushOperation,IsNativeFixture);
                    if(!PublishBlockPlanReceipt(blockPlanReceipt,IsNativeFixture)){Fail(receipt);return false;}
                }
                return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(IsNativeFixture)ViveSyntheticNativeTrace.Mark(ViveSyntheticNativeTrace.Stage.AppendException);
#endif
                Fail(receipt);return false;
            }
        }
        byte[] FrameForDesign(byte[] payload,byte[] previousDigest,bool first,out byte[] digest)
        {return admission.SessionContext.IsLegacyWoz?ViveRawSessionJournalCodec.FrameV5(payload,previousDigest,first,out digest):
            admission.IsLegacy?ViveRawSessionJournalCodec.FrameV4(payload,previousDigest,first,out digest):ViveRawSessionJournalCodec.FrameV3(payload,previousDigest,first,out digest);}
        void Fail(ViveNamedSessionFrameAdmission.ConsumerReceipt receipt)
        {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            if(IsNativeFixture)ViveSyntheticNativeTrace.Mark(ViveSyntheticNativeTrace.Stage.AppendFailed);
            ClearSyntheticTeeForTest();
#endif
            admission.IoFence.Abort();Interlocked.Exchange(ref failed,1);Interlocked.Exchange(ref issuedProof,null);Interlocked.Exchange(ref issuedNativeAuthority,null);Interlocked.Exchange(ref issuedBlockPlanReceipt,null);admission.ConsumerFailed(receipt);}
        // Writer thread only, exactly one attempt. The sealed/drained boundary
        // comes from the admission and all counts from this sink's ACKs. A
        // successful terminal/flush/dispose is not proof of complete sampling,
        // research consent, identity or power-loss durability.
        internal bool CompleteSealed()
        {
            if(installedDestination!=null&&Volatile.Read(ref completionState)==8)
                return ContinueInstalledRelativeCleanup();
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            if(IsNativeFixture&&syntheticRelativeStageRequested&&Volatile.Read(ref completionState)==8)
                return ContinueSyntheticRelativeCleanup();
#endif
            if(!CanRunNative||!CanCompleteNative||budget==null||writerThread==null||!Object.ReferenceEquals(Thread.CurrentThread,writerThread)||
                Volatile.Read(ref failed)!=0||file==null||capability==null||Interlocked.CompareExchange(ref completionState,1,0)!=0)return false;
            try
            {
                long water,cursor;double at;
                if(!admission.TryGetDrainedSealBoundary(out water,out cursor,out at)||water!=expectedOrdinal||cursor!=confirmedTaskCursor||
                    !taskEnded||!taskCompleted||!endedPhase||extraTaskAfterEnd||Volatile.Read(ref issuedBlockPlanReceipt)!=null||at<latestObservedAt||
                    checked(confirmedTasks+confirmedEyes+confirmedStatuses)!=expectedOrdinal)throw new InvalidOperationException("Unverified terminal boundary.");
                byte[] digest;byte[] payload=ViveRawSessionJournalCodec.TerminalPayloadV3(checked(expectedOrdinal+1),cursor,
                    confirmedTasks,confirmedEyes,confirmedStatuses,water,at);
                byte[] frame=FrameForDesign(payload,previous,false,out digest);
                if(!budget.FitsTerminal(bytesWritten,frame.Length))throw new InvalidOperationException("Raw journal limit reached.");
                long checkWater,checkCursor;double checkAt;
                if(!admission.TryGetDrainedSealBoundary(out checkWater,out checkCursor,out checkAt)||checkWater!=water||checkCursor!=cursor||checkAt!=at)
                    throw new InvalidOperationException("Terminal authority changed.");
                long terminalOperation;
                if(!file.TryWrite(frame,true,IssueNative(NativeStep.Terminal,null,null,capability,file,frame,water,cursor,at),out terminalOperation)||terminalOperation<0)throw new InvalidOperationException("Terminal write unconfirmed.");
                bytesWritten+=frame.Length;previous=digest;Volatile.Write(ref completionState,2);
                if(!admission.TryGetDrainedSealBoundary(out checkWater,out checkCursor,out checkAt))throw new InvalidOperationException("Terminal outcome uncertain.");
                if(!file.TryFlush(IssueNative(NativeStep.Flush,null,null,capability,file,null,water,cursor,at)))throw new InvalidOperationException("Flush unconfirmed.");
                Volatile.Write(ref completionState,3);
                if(!admission.TryGetDrainedSealBoundary(out checkWater,out checkCursor,out checkAt))throw new InvalidOperationException("Flush outcome uncertain.");
                // Validate the just-written bytes through the same exclusive
                // open file object before closing it. This is not an export,
                // current authorization, capture coverage or durability proof.
                long requiredRecords=checked(expectedOrdinal+2);
                if(requiredRecords>25000000)throw new InvalidOperationException("Readback record limit reached.");
                // A local output is optional and independently gated. Its
                // asynchronous target open must never delay this source scan.
                // Failure leaves output unknown, not a successful export.
                if(installedDestination!=null)
                {
                    bool started=installedDestination.TryStartForWriter(capability,out installedPrivateStage);
                    installedStageStartFailed=!started||installedPrivateStage==null;
                }
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(IsNativeFixture&&syntheticRelativeStageRequested)
                {
                    // Synthetic-only: source capability already owns the
                    // validated directory and source handle. Each target
                    // create/verification call is ordered with withdrawal by
                    // the participant's shared stage-I/O claim.
                    try{var hook=BeforeSyntheticRelativeStageConstructionForTest;if(hook!=null)hook();
                        if(syntheticRelativeCsvRequested)
                            syntheticPrivateStage=ViveRawPrivateStage.CreateSyntheticCsvForTest(
                                owner=>capability.OpenSiblingForPrivateStage(owner,admission),
                                owner=>capability.OpenSyntheticCsvSiblingForPrivateStage(owner,admission),
                                ()=>Volatile.Read(ref syntheticSourceCloseValidated)!=0,budget.MaximumBytes,syntheticCsvMaximumBytes,
                                CancellationToken.None,admission.SyntheticStageAuthorityForTest);
                        else syntheticPrivateStage=ViveRawPrivateStage.CreateSyntheticAsyncForTest(
                            owner=>capability.OpenSiblingForPrivateStage(owner,admission),budget.MaximumBytes,
                            CancellationToken.None,admission.SyntheticStageAuthorityForTest);}
                    catch(OutOfMemoryException){}
                    catch(Exception e) when(!(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException)){}
                }
#endif
                using(var input=file.TryOpenReadback(bytesWritten,IssueNative(NativeStep.Readback,null,null,capability,file,null,water,cursor,at)))
                {
                    if(input==null)throw new InvalidOperationException("Same-file readback unavailable.");
                    if(installedPrivateStage!=null)
                    {
                        var read=ViveRawSessionJournalReader.ScanWithPrivateStage(input,budget.MaximumBytes,(int)requiredRecords,CancellationToken.None,installedPrivateStage);
                        bool matched=ReadbackMatches(read,bytesWritten,requiredRecords,confirmedTasks,confirmedEyes,confirmedStatuses,previous)&&!admission.IoFence.IsRevoked;
                        installedPrivateStage.FinishScan(read,matched);
                        if(!matched)throw new InvalidOperationException("Same-file readback unconfirmed.");
                    }
                    else
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(IsNativeFixture && syntheticPrivateStage!=null)
                    {
                        var read=ViveRawSessionJournalReader.ScanWithPrivateStage(input,budget.MaximumBytes,(int)requiredRecords,CancellationToken.None,syntheticPrivateStage);
                        bool matched=ReadbackMatches(read,bytesWritten,requiredRecords,confirmedTasks,confirmedEyes,confirmedStatuses,previous)&&!admission.IoFence.IsRevoked;
                        syntheticPrivateStage.FinishScan(read,matched);
                        if(!matched)throw new InvalidOperationException("Same-file readback unconfirmed.");
                    }
                    else if(IsNativeFixture && syntheticTeeMode != 0)
                    {
                        int limit=syntheticTeeMode==2?1:ViveSyntheticReadbackTee.MaximumCaptureBytes;
                        Action hook=syntheticTeeMode==3?(Action)(()=>{throw new IOException("synthetic capture failure");}):null;
                        using(var tee=new ViveSyntheticReadbackTee(input,bytesWritten,limit,hook))
                        {
                            var read=ViveRawSessionJournalReader.Scan(tee,true,true,budget.MaximumBytes,(int)requiredRecords,CancellationToken.None);
                            if(!ReadbackMatches(read,bytesWritten,requiredRecords,confirmedTasks,confirmedEyes,confirmedStatuses,previous)||admission.IoFence.IsRevoked)
                                throw new InvalidOperationException("Same-file readback unconfirmed.");
                            try { syntheticReadbackSnapshot=tee.SnapshotForTest(read,requiredRecords,confirmedTasks,confirmedEyes,confirmedStatuses,previous,false); }
                            catch(OutOfMemoryException) { ClearSyntheticTeeForTest(); }
                        }
                    }
                    else
#endif
                    {
                    var read=ViveRawSessionJournalReader.Scan(input,true,true,budget.MaximumBytes,(int)requiredRecords,CancellationToken.None);
                    if(!ReadbackMatches(read,bytesWritten,requiredRecords,confirmedTasks,confirmedEyes,confirmedStatuses,previous)||admission.IoFence.IsRevoked)
                        throw new InvalidOperationException("Same-file readback unconfirmed.");
                    }
                }
                if(!admission.TryGetDrainedSealBoundary(out checkWater,out checkCursor,out checkAt)||checkWater!=water||checkCursor!=cursor||checkAt!=at)
                    throw new InvalidOperationException("Readback outcome uncertain.");
                for(int step=0;step<=4;step++)
                    if(!file.TryIdentityStep(IssueNative(NativeStep.IdentityWitness,null,null,capability,file,null,water,cursor,at),
                        (ViveRawRelativeFileCapability.IdentityStep)step))throw new InvalidOperationException("Source identity witness unconfirmed.");
                if(!file.TryClose(IssueNative(NativeStep.Close,null,null,capability,file,null,water,cursor,at)))throw new InvalidOperationException("File close unconfirmed.");
                Volatile.Write(ref sourceCloseReturned,1);
                Volatile.Write(ref completionState,4);
                for(int step=5;step<=11;step++)
                    if(!file.TryIdentityStep(IssueNative(NativeStep.IdentityWitness,null,null,capability,file,null,water,cursor,at),
                        (ViveRawRelativeFileCapability.IdentityStep)step))throw new InvalidOperationException("Source identity after close unconfirmed.");
                if(!admission.TryGetDrainedSealBoundary(out checkWater,out checkCursor,out checkAt))throw new InvalidOperationException("File close outcome uncertain.");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(IsNativeFixture&&syntheticRelativeCsvRequested)
                {
                    if(checkWater!=water||checkCursor!=cursor||checkAt!=at)throw new InvalidOperationException("Post-close seal boundary changed.");
                    Volatile.Write(ref syntheticSourceCloseValidated,1);
                }
#endif
                if(installedDestination!=null&&installedPrivateStage!=null)
                {
                    if(installedDestination.CsvRequested&&(checkWater!=water||checkCursor!=cursor||checkAt!=at))
                        throw new InvalidOperationException("Post-close seal boundary changed.");
                    installedCleanupClock=System.Diagnostics.Stopwatch.StartNew();
                    Volatile.Write(ref completionState,8);
                    if(installedDestination.CsvRequested)Volatile.Write(ref installedSourceCloseValidated,1);
                    return ContinueInstalledRelativeCleanup();
                }
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(IsNativeFixture&&syntheticRelativeStageRequested)
                {
                    // Source close returned. The writer can poll pending
                    // target/lease cleanup without waiting on target I/O.
                    // This configurable cleanup deadline is not a storage
                    // capacity, coverage, or performance guarantee.
                    syntheticRelativeCleanupClock=System.Diagnostics.Stopwatch.StartNew();
                    Volatile.Write(ref completionState,8);
                    return ContinueSyntheticRelativeCleanup();
                }
#endif
                // Each directory/ancestor close has its own sink authority and
                // native permit. Any failure stops without retry; status 6 is
                // pending cleanup, not a claim about media durability.
                if(!capability.TryCloseDirectory(IssueNative(NativeStep.CloseDirectory,null,null,capability,null,null,water,cursor,at)))
                {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    ClearSyntheticTeeForTest();
#endif
                    Volatile.Write(ref completionState,6);return false;}
                bool ancestorsClosed=false;
                for(int i=0;i<ViveRawDirectoryLease.MaxAncestorHandles&&!ancestorsClosed;i++)
                {
                    if(!admission.TryGetDrainedSealBoundary(out checkWater,out checkCursor,out checkAt)||
                        checkWater!=water||checkCursor!=cursor||checkAt!=at)throw new InvalidOperationException("Ancestor close authority changed.");
                    if(!capability.TryCloseNextAncestor(IssueNative(NativeStep.CloseAncestor,null,null,capability,null,null,water,cursor,at),out ancestorsClosed))
                    {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        ClearSyntheticTeeForTest();
#endif
                        Volatile.Write(ref completionState,6);return false;}
                }
                if(!ancestorsClosed||capability.DirectoryCleanupPending){
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    ClearSyntheticTeeForTest();
#endif
                    Volatile.Write(ref completionState,6);return false;}
                // Keep the existing source and directory cleanup authority
                // until those calls return, then reject a missing requested
                // output stage rather than reporting source-only success.
                if(RejectFailedInstalledOutputStartup())return false;
                Volatile.Write(ref completionState,7);
                return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {if(installedDestination!=null)installedDestination.MarkInstalledUnknown();Volatile.Write(ref completionState,5);Fail(null);return false;}
        }
        // Writer-only classification after the existing fallback cleanup.
        // No native call or successful completion is issued by this helper.
        bool RejectFailedInstalledOutputStartup()
        {
            if(installedDestination==null||!installedStageStartFailed)return false;
            installedDestination.MarkInstalledUnknown();
            Volatile.Write(ref completionState,5);
            Fail(null);
            return true;
        }
        internal bool InstalledRelativeCompletionPending
        {get{return installedDestination!=null&&Volatile.Read(ref completionState)==8;}}
        // Independent diagnostic snapshots. Neither returned source close nor
        // target close is publication authority or a durability proof.
        internal int InstalledDestinationState
        {get{var value=Volatile.Read(ref installedDestination);return value==null?-1:(int)value.State;}}
        bool ContinueInstalledRelativeCleanup()
        {
            var destination=installedDestination;var stage=installedPrivateStage;
            if(destination==null||stage==null||!Object.ReferenceEquals(Thread.CurrentThread,writerThread)||
                Volatile.Read(ref completionState)!=8)return false;
            // Checking before each poll conservatively rejects late results.
            // This bound cannot cancel an OS operation already in flight.
            int timeout=destination.CsvRequested&&!installedCloseStarted?
                destination.CsvCleanupTimeoutMilliseconds:destination.CleanupTimeoutMilliseconds;
            if(installedCleanupClock==null||installedCleanupClock.ElapsedMilliseconds>=timeout)
            {destination.MarkInstalledUnknown();Volatile.Write(ref completionState,6);return false;}
            if(!stage.WorkerExited||(installedCloseStarted&&!stage.InstalledCloseDone))return false;
            if(stage.State!=ViveRawPrivateStage.Outcome.PrivateCandidate)
            {destination.MarkInstalledUnknown();Volatile.Write(ref completionState,6);return false;}
            if(destination.CsvRequested)
            {
                if(!installedCsvCloseStarted)
                {
                    installedCsvCloseStarted=true;
                    if(!stage.StartInstalledCsvClose(destination)){destination.MarkInstalledUnknown();Volatile.Write(ref completionState,6);}
                    return false;
                }
                if(!stage.InstalledCsvCloseDone)return false;
                if(!stage.InstalledCsvCloseSucceeded){destination.MarkInstalledUnknown();Volatile.Write(ref completionState,6);return false;}
                // Give the raw close/directory cleanup their own deadline.
                if(!installedCloseStarted)installedCleanupClock.Restart();
            }
            if(!installedCloseStarted)
            {
                installedCloseStarted=true;
                if(!stage.StartInstalledTargetClose(destination)){destination.MarkInstalledUnknown();Volatile.Write(ref completionState,6);}
                return false;
            }
            if(!stage.InstalledCloseSucceeded)
            {destination.MarkInstalledUnknown();Volatile.Write(ref completionState,6);return false;}
            destination.MarkInstalledCloseReturned(stage);
            try
            {
                long water,cursor;double at;
                if(!admission.TryGetDrainedSealBoundary(out water,out cursor,out at))throw new InvalidOperationException("Cleanup authority unavailable.");
                if(!capability.TryCloseDirectory(IssueNative(NativeStep.CloseDirectory,null,null,capability,null,null,water,cursor,at)))
                {destination.MarkInstalledUnknown();Volatile.Write(ref completionState,6);return false;}
                bool ancestorsClosed=false;
                for(int i=0;i<ViveRawDirectoryLease.MaxAncestorHandles&&!ancestorsClosed;i++)
                {
                    long checkWater,checkCursor;double checkAt;
                    if(!admission.TryGetDrainedSealBoundary(out checkWater,out checkCursor,out checkAt)||
                        checkWater!=water||checkCursor!=cursor||checkAt!=at)throw new InvalidOperationException("Ancestor close authority changed.");
                    if(!capability.TryCloseNextAncestor(IssueNative(NativeStep.CloseAncestor,null,null,capability,null,null,water,cursor,at),out ancestorsClosed))
                    {destination.MarkInstalledUnknown();Volatile.Write(ref completionState,6);return false;}
                }
                if(!ancestorsClosed||capability.DirectoryCleanupPending)
                {destination.MarkInstalledUnknown();Volatile.Write(ref completionState,6);return false;}
                Volatile.Write(ref completionState,7);return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {destination.MarkInstalledUnknown();Volatile.Write(ref completionState,6);return false;}
        }
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        internal bool SyntheticRelativeCompletionPending {get{return IsNativeFixture&&syntheticRelativeStageRequested&&Volatile.Read(ref completionState)==8;}}
        bool ContinueSyntheticRelativeCleanup()
        {
            if(!IsNativeFixture||!syntheticRelativeStageRequested||!Object.ReferenceEquals(Thread.CurrentThread,writerThread)||
                Volatile.Read(ref completionState)!=8)return false;
            // A poll after the deadline is conservatively UNKNOWN even if a
            // target call happened to return between polls. No late result is
            // promoted to an authorized close or retried.
            if(syntheticRelativeCleanupClock==null||syntheticRelativeCleanupClock.ElapsedMilliseconds>=syntheticRelativeCleanupTimeoutMilliseconds)
            {if(syntheticPrivateStage!=null)syntheticPrivateStage.Abandon();Volatile.Write(ref completionState,6);return false;}
            // No wait or OS call while the target worker is still alive. A
            // future pump tick may poll; explicit withdrawal revokes claims.
            if(syntheticPrivateStage!=null&&(!syntheticPrivateStage.WorkerExited||
                (syntheticRelativeCloseStarted&&!syntheticPrivateStage.SyntheticCloseDone)))
            {
                return false;
            }
            if(syntheticPrivateStage==null||syntheticPrivateStage.State!=ViveRawPrivateStage.Outcome.PrivateCandidate)
            {if(syntheticPrivateStage!=null)syntheticPrivateStage.Abandon();Volatile.Write(ref completionState,6);return false;}
            if(syntheticRelativeCsvRequested)
            {
                if(!syntheticRelativeCsvCloseStarted)
                {
                    syntheticRelativeCsvCloseStarted=true;
                    if(!syntheticPrivateStage.StartSyntheticCsvCloseForTest(this))
                    {syntheticPrivateStage.Abandon();Volatile.Write(ref completionState,6);}
                    return false;
                }
                if(!syntheticPrivateStage.SyntheticCsvCloseDoneForTest)return false;
                if(!syntheticPrivateStage.SyntheticCsvCloseSucceededForTest)
                {syntheticPrivateStage.Abandon();Volatile.Write(ref completionState,6);return false;}
            }
            if(!syntheticRelativeCloseStarted)
            {
                syntheticRelativeCloseStarted=true;
                if(!syntheticPrivateStage.StartSyntheticTargetClose(this))
                {syntheticPrivateStage.Abandon();Volatile.Write(ref completionState,6);}
                return false;
            }
            if(!syntheticPrivateStage.SyntheticCloseSucceeded)
            {syntheticPrivateStage.Abandon();Volatile.Write(ref completionState,6);return false;}
            try
            {
                long water,cursor;double at;
                if(!admission.TryGetDrainedSealBoundary(out water,out cursor,out at))throw new InvalidOperationException("Cleanup authority unavailable.");
                if(!capability.TryCloseDirectory(IssueNative(NativeStep.CloseDirectory,null,null,capability,null,null,water,cursor,at)))
                {Volatile.Write(ref completionState,6);return false;}
                bool ancestorsClosed=false;
                for(int i=0;i<ViveRawDirectoryLease.MaxAncestorHandles&&!ancestorsClosed;i++)
                {
                    long checkWater,checkCursor;double checkAt;
                    if(!admission.TryGetDrainedSealBoundary(out checkWater,out checkCursor,out checkAt)||
                        checkWater!=water||checkCursor!=cursor||checkAt!=at)throw new InvalidOperationException("Ancestor close authority changed.");
                    if(!capability.TryCloseNextAncestor(IssueNative(NativeStep.CloseAncestor,null,null,capability,null,null,water,cursor,at),out ancestorsClosed))
                    {Volatile.Write(ref completionState,6);return false;}
                }
                if(!ancestorsClosed||capability.DirectoryCleanupPending){Volatile.Write(ref completionState,6);return false;}
                Volatile.Write(ref completionState,7);return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Volatile.Write(ref completionState,6);return false;}
        }
#endif
        // 0 not attempted, 1 reserved, 2 terminal write returned, 3 flush
        // returned, 4 file close returned, 5 failure/uncertain, 6 directory
        // or ancestor-handle cleanup pending, 7 all reviewed close calls
        // returned; synthetic-only 8 means source close returned and target
        // and directory cleanup are still pending. No durability proof.
        internal int CompletionState {get{return Volatile.Read(ref completionState);}}
        internal bool SourceCloseReturned {get{return Volatile.Read(ref sourceCloseReturned)!=0;}}
        // Strict consistency only: this is not a consent, source-authorization,
        // complete-sampling or power-loss durability assertion.
        internal static bool ReadbackMatches(ViveRawSessionReadResult read,long bytes,long records,long tasks,long eyes,long statuses,byte[] digest)
        {
            if(read==null||digest==null||digest.Length!=32||bytes<8||records<2||tasks<0||eyes<0||statuses<0)return false;
            string expectedDigest=BitConverter.ToString(digest).Replace("-",String.Empty).ToLowerInvariant();
            return read.Status==ViveRawSessionReadStatus.VerifiedPrefix&&!read.TrailingFrameTruncated&&read.CaptureTerminalObserved&&
                read.VerifiedBytes==bytes&&read.VerifiedRecordCount==records&&read.VerifiedTaskRecords==tasks&&
                read.VerifiedObservationRecords==eyes&&read.VerifiedStatusRecords==statuses&&
                String.Equals(read.VerifiedPrefixChainDigestHex,expectedDigest,StringComparison.Ordinal);
        }
        internal bool DirectoryCleanupPending
        {
            get
            {
                if(Volatile.Read(ref openAttempted)==0)return false;
                var current=Volatile.Read(ref capability);
                // An attempted preparation that did not return a capability
                // remains unknown; it is never reported as cleanly closed.
                return current==null||current.DirectoryCleanupPending;
            }
        }
        // Background writer only. Withdrawal/fault does not initiate another
        // native call; the capability must retain every unresolved handle.
        internal bool CleanupAfterTerminal()
        {
            if(installedDestination!=null)installedDestination.MarkInstalledUnknown();
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            if(IsNativeFixture&&syntheticRelativeStageRequested&&syntheticPrivateStage!=null)syntheticPrivateStage.Abandon();
#endif
            if(writerThread==null||!Object.ReferenceEquals(Thread.CurrentThread,writerThread)||
                (Volatile.Read(ref failed)==0&&admission.State!=2&&admission.State!=3)||
                Interlocked.CompareExchange(ref cleanupState,1,0)!=0)return false;
            var handle=Volatile.Read(ref file);
            if(handle==null&&capability==null){Volatile.Write(ref cleanupState,Volatile.Read(ref openAttempted)==0?4:3);return false;}
            Volatile.Write(ref cleanupState,6);return false;
        }
        internal int CleanupState {get{return Volatile.Read(ref cleanupState);}}
        internal bool DurableStorageProven {get{return false;}}
    }
}
