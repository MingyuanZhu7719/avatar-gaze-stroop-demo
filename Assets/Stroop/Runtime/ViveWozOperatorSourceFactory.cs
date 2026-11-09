using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Wires an already-owned operator provider into the development peer's source
    // factory. Construction and Create never start/stop a worker, OpenXR, native
    // runtime, network, recording or a task. The caller explicitly owns those
    // lifetimes and must reuse one provider/worker; a device name is not proof of
    // physical separation or authentication.
    public sealed class ViveWozOperatorSourceFactory
    {
        readonly string sessionId,clockId,mappingVersion;
        readonly IClock clock;
        readonly IViveProEyeProvider provider;
        readonly IViveProEyeCoordinateMapper mapper;
        readonly double maximumAge;
        readonly int ownerThread;
        ViveProEyeHeadEyeSource current;
        int block=-1,segment=-1;
        Condition? condition;
        bool clockOrMappingFault;
        double lastLocalTime=-1;
        public string StreamId {get;private set;}
        // This is the existing adapter's deliberately non-serial device label;
        // it does not identify or prove independence of a physical HMD.
        public string DeviceId {get{return "vive-pro-eye-no-serial";}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool MappingValidatedForApparatus {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}

        public ViveWozOperatorSourceFactory(string sessionId,string epoch,IClock clock,IViveProEyeProvider provider,
            IViveProEyeCoordinateMapper mapper,double maximumObservationAgeSeconds,bool explicitDevelopmentOnly)
        {
            if(!explicitDevelopmentOnly)throw new InvalidOperationException("Explicit development-only opt-in is required");
            if(clock==null||provider==null||mapper==null)throw new ArgumentNullException("clock/provider/mapper");
            WireCodec.RequireIdentity(sessionId);WireCodec.RequireIdentity(epoch);WireCodec.RequireIdentity(clock.ClockId);WireCodec.RequireIdentity(mapper.Version);
            if(!WireCodec.Finite(maximumObservationAgeSeconds)||maximumObservationAgeSeconds<=0)
                throw new ArgumentOutOfRangeException("maximumObservationAgeSeconds");
            this.sessionId=sessionId;this.clock=clock;this.provider=provider;this.mapper=mapper;maximumAge=maximumObservationAgeSeconds;
            clockId=clock.ClockId;mappingVersion=mapper.Version;StreamId=ViveDevelopmentTransport.OperatorStream(mappingVersion,epoch);
            ownerThread=Thread.CurrentThread.ManagedThreadId;
        }

        // Compatible with ViveDevelopmentAppAdapter's sourceFactory delegate.
        // A context is reused without resetting its source sequence; advancing
        // to an older block/segment is refused. D and non-task phases carry no
        // live avatar pose. It returns a source only, never evidence of readiness.
        public ViveProEyeHeadEyeSource Create(TaskSnapshot task)
        {
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread)
                throw new InvalidOperationException("Operator source factory belongs to its owning thread");
            double now=clock.Now;
            if(clockOrMappingFault||clock.ClockId!=clockId||mapper.Version!=mappingVersion||
                !WireCodec.Finite(now)||now<0||now<lastLocalTime)
            {
                clockOrMappingFault=true;
                throw new InvalidOperationException("Clock or mapping fault requires a new explicit epoch");
            }
            lastLocalTime=now;
            if(task==null||task.SessionId!=sessionId||task.Phase!=Phase.MainTask||task.BlockIndex<0||task.Segment<0||
                !task.Condition.HasValue||task.Condition==Condition.D||String.IsNullOrWhiteSpace(task.TrialId)||!task.Word.HasValue||!task.Ink.HasValue)
                throw new InvalidOperationException("A matching active visible task context is required");
            if(task.BlockIndex<block||(task.BlockIndex==block&&task.Segment<segment))
                throw new InvalidOperationException("Older block or segment requires a new explicit epoch");
            if(task.BlockIndex==block&&condition.HasValue&&task.Condition!=condition)
                throw new InvalidOperationException("A block condition cannot change within an operator source epoch");
            if(current!=null&&task.BlockIndex==block&&task.Segment==segment)return current;
            var next=new ViveProEyeHeadEyeSource(sessionId,task.BlockIndex,task.Segment,StreamId,clock,provider,mapper,maximumAge);
            current=next;block=task.BlockIndex;segment=task.Segment;condition=task.Condition;return next;
        }
    }
}
