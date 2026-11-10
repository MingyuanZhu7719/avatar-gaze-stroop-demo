#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
using System;
using System.IO;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    internal sealed partial class ViveNamedRawRecordingPump
    {
        // Sealed test assembly only. Uses the real producer/status/consumer/drain
        // path, but owns only memory bytes. No file factory or native receipt.
        internal sealed class MemoryJournalFixture : Writer
        {
            readonly ViveNamedSessionFrameAdmission admission;
            readonly MemoryStream bytes=new MemoryStream();
            readonly object gate=new object();
            byte[] digest=new byte[32];long ordinal,tasks,eyes,statuses;
            internal int CompleteCalls {get;private set;}
            internal int CleanupCalls {get;private set;}
            internal MemoryJournalFixture(ViveNamedSessionFrameAdmission admission)
            {if(admission==null||!admission.SyntheticOnly||!admission.SessionContext.IsLegacyWoz)throw new InvalidOperationException("Synthetic WoZ admission required");this.admission=admission;}
            internal byte[] Snapshot(){lock(gate)return bytes.ToArray();}
            void Write(byte[] payload,bool first=false)
            {byte[] next;var frame=ViveRawSessionJournalCodec.FrameV5(payload,digest,first,out next);bytes.Write(frame,0,frame.Length);digest=next;}
            internal override bool Append(ViveNamedSessionFrameAdmission.ConsumerReceipt receipt)
            {
                ViveNamedSessionFrameAdmission.Manifest manifest;
                if(receipt==null||receipt.Ordinal!=ordinal||!admission.TryGetManifestForReservedConsumer(receipt,receipt.Record,out manifest))return false;
                lock(gate)
                {
                    if(bytes.Length==0)Write(ViveRawSessionJournalCodec.CurrentManifestPayload(manifest),true);
                    var status=receipt.Record as ViveProEyeWorker.ObservedEyeStatus;
                    Write(status==null?ViveRawSessionJournalCodec.RecordPayload(++ordinal,receipt.Record,manifest):ViveRawSessionJournalCodec.StatusPayloadV3(++ordinal,status));
                    if(status!=null)statuses++;else if(receipt.Record is JournalEvent)tasks++;else if(receipt.Record is ViveNamedSessionFrameAdmission.Frame)eyes++;
                }
                return admission.TryAcknowledgeSyntheticForTest(receipt);
            }
            internal override bool Complete()
            {
                long water,cursor;double at;
                if(!admission.TryGetDrainedSealBoundary(out water,out cursor,out at)||water!=ordinal)return false;
                lock(gate){CompleteCalls++;Write(ViveRawSessionJournalCodec.TerminalPayloadV3(ordinal+1,cursor,tasks,eyes,statuses,water,at));}return true;
            }
            internal override void Cleanup(){lock(gate)CleanupCalls++;}
            internal override ViveRawSessionJournalSink.BlockPlanReceiptPoll PollBlockPlan(ViveBlockPlanRequest request,out ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt)
            {receipt=null;return ViveRawSessionJournalSink.BlockPlanReceiptPoll.Faulted;}
            internal override void RevokeBlockPlanReceipt(){}
            internal override int CompletionSnapshot {get{return -1;}}
            internal override int CleanupSnapshot {get{return -1;}}
            internal override bool DirectoryCleanupPending {get{return false;}}
            internal override bool SourceCloseReturned {get{return false;}}
            internal override int InstalledDestinationState {get{return -1;}}
        }
        internal static ViveNamedRawRecordingPump StartWozMemoryJournalFixture(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,out MemoryJournalFixture writer)
        {
            if(participant==null||worker==null||clock==null||admission==null||!admission.SyntheticOnly||!admission.SessionContext.IsLegacyWoz||
                !admission.UsesExactSources(participant,worker,clock)||worker.UsesInstalledSdkPath||!participant.UsesEventClock(clock)||!worker.UsesClock(clock))
                throw new InvalidOperationException("Exact synthetic WoZ sources required");
            object run;if(!admission.TryEnableWorkerStatus(128,out run))throw new InvalidOperationException("Synthetic status unavailable");
            writer=new MemoryJournalFixture(admission);
            try{return new ViveNamedRawRecordingPump(participant,worker,clock,admission,writer,run);}
            catch{admission.Withdraw();worker.TryStopStatusEvidence(run);throw;}
        }
    }
}
#endif
