using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // The sink is exclusively a development live layer. Its Clear must not
    // clear or modify an Automatic/Quest layer. No production host is wired here.
    public interface IViveLivePresentationSink
    {
        void Apply(AvatarOutput output);
        void Clear();
    }

    // All identities, validity and observation times here are caller-declared.
    // They are not a phase-bound proof, authenticated device, hardware capture
    // time, or apparatus-calibrated mapping. Never use this as a study gate.
    public sealed class ViveLivePresentationFrame
    {
        public string Session {get;private set;}
        public int Block {get;private set;}
        public int Segment {get;private set;}
        public string TrialId {get;private set;}
        public Condition Condition {get;private set;}
        public long Sequence {get;private set;}
        public double ReceivedLocal {get;private set;}
        public string MappingVersion {get;private set;}
        public Rotation Head {get;private set;}
        public Vec3 Eye {get;private set;}
        public bool HeadValid {get;private set;}
        public bool EyeValid {get;private set;}
        public ViveLivePresentationFrame(string session,int block,int segment,string trialId,Condition condition,long sequence,
            double receivedLocal,string mappingVersion,Rotation head,Vec3 eye,bool headValid,bool eyeValid)
        {
            Session=session;Block=block;Segment=segment;TrialId=trialId;Condition=condition;Sequence=sequence;
            ReceivedLocal=receivedLocal;MappingVersion=mappingVersion;Head=head;Eye=eye;HeadValid=headValid;EyeValid=eyeValid;
        }
    }

    // Synthetic/development-only presentation candidate, not connected to any
    // host or network. The caller must invoke it on every render tick, including
    // invalidation. Head and eye are already mapped avatar-root-local values.
    // Clock and payload authenticity are deliberately NOT inferred from strings.
    public sealed class ViveLivePresentationAdapter
    {
        readonly IClock clock;
        readonly string clockId,session,mapping;
        readonly double maximumAge;
        readonly IViveLivePresentationSink sink;
        readonly int owner;
        int terminal;
        bool busy,hasContext,continuityBreak;
        int block,segment;
        string trial;
        Condition condition;
        long lastSequence=-1;
        double lastNow=-1;
        public bool CanEnterLiveCountdown {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public bool ApparatusMappingValidated {get{return false;}}
        public bool Faulted {get{return Interlocked.CompareExchange(ref terminal,0,0)!=0;}}

        public ViveLivePresentationAdapter(IClock clock,string expectedSession,string expectedMapping,double maximumAge,
            IViveLivePresentationSink sink,bool explicitDevelopmentOnly)
        {
            if(!explicitDevelopmentOnly)throw new InvalidOperationException("Explicit development scope required");
            if(clock==null||sink==null)throw new ArgumentNullException("clock/sink");
            if(String.IsNullOrWhiteSpace(expectedSession)||String.IsNullOrWhiteSpace(expectedMapping)||
                !Finite(maximumAge)||maximumAge<=0)throw new ArgumentException("Explicit session, mapping and age required");
            string id=clock.ClockId;if(String.IsNullOrWhiteSpace(id))throw new ArgumentException("Explicit local clock identity required");
            this.clock=clock;clockId=id;session=expectedSession;mapping=expectedMapping;this.maximumAge=maximumAge;this.sink=sink;
            owner=Thread.CurrentThread.ManagedThreadId;
        }
        static bool Finite(double x){return !Double.IsNaN(x)&&!Double.IsInfinity(x);}
        void Fault(){Interlocked.Exchange(ref terminal,1);}
        bool Enter()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner){Fault();return false;}
            if(busy){Fault();return false;}
            busy=true;return true;
        }
        // A failing sink can prevent physical hide; this fault is latched and
        // must be handled by the host (e.g. mask the camera and stop the task).
        bool Hide()
        {try{sink.Clear();return !Faulted;}catch(Exception){Fault();return false;}}
        public void Clear()
        {
            if(!Enter())return;
            try{if(hasContext)continuityBreak=true;hasContext=false;Hide();}finally{busy=false;}
        }
        public bool Present(TaskSnapshot task,ControlSelection? selection,ViveLivePresentationFrame frame)
        {
            if(!Enter())return false;
            try
            {
                // Always invalidate the prior live presentation before checking a
                // new context/sample. The sink is distinct from Automatic output.
                if(!Hide()||Faulted)return false;
                bool freshSequence=frame!=null&&frame.Sequence>=0&&frame.Sequence>lastSequence;
                if(freshSequence)lastSequence=frame.Sequence;
                if(task==null||selection!=ControlSelection.LiveWoz||task.Phase!=Phase.MainTask||
                    task.SessionId!=session||task.BlockIndex<0||task.Segment<0||String.IsNullOrWhiteSpace(task.TrialId)||
                    !task.Condition.HasValue||task.Condition.Value==Condition.D)
                {if(hasContext)continuityBreak=true;hasContext=false;return false;}
                bool changed=continuityBreak||(hasContext&&(block!=task.BlockIndex||segment!=task.Segment||trial!=task.TrialId||condition!=task.Condition.Value));
                block=task.BlockIndex;segment=task.Segment;trial=task.TrialId;condition=task.Condition.Value;hasContext=true;
                continuityBreak=false;
                if(changed)return false;
                if(!freshSequence)return false;
                if(frame.Session!=session||frame.Block!=block||frame.Segment!=segment||frame.TrialId!=trial||
                    frame.Condition!=condition||frame.MappingVersion!=mapping||!frame.HeadValid||!frame.EyeValid||
                    !frame.Head.IsUnit||!frame.Eye.IsUnit||!Finite(frame.ReceivedLocal)||frame.ReceivedLocal<0)return false;
                string observedId=clock.ClockId;double now=clock.Now;
                if(observedId!=clockId||!Finite(now)||now<0||now<lastNow){Fault();return false;}
                lastNow=now;
                if(Faulted||frame.ReceivedLocal>now||now-frame.ReceivedLocal>maximumAge)return false;
                // No reference/automatic fallback: a complete pair or no avatar.
                var output=new AvatarOutput{AvatarVisible=true,SelectedSource=ControlSource.LiveOperator,
                    ActualSource=ControlSource.LiveOperator,ActualPose=new AvatarPose(frame.Head,frame.Eye),
                    HeadValid=true,EyeValid=true,MappingVersion=mapping,Status=GazeOutputStatus.SampleAvailable};
                try{sink.Apply(output);}catch(Exception){Fault();Hide();return false;}
                if(Faulted){Hide();return false;}
                return true;
            }
            catch(Exception){Fault();Hide();return false;}
            finally{busy=false;}
        }
    }
}
