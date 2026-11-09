using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Simulation-only protocol. Declared identities and endpoints are NOT
    // authentication and no object here can admit a research session.
    public sealed class ViveSimulationTrialTask
    {
        public TaskSnapshot Task {get;private set;}
        public string Epoch {get;private set;}
        public string Mapping {get;private set;}
        public string ParticipantClock {get;private set;}
        public long Revision {get;private set;}
        public long PacketSequence {get;private set;}
        public double SentLocalSeconds {get;private set;}
        public ViveSimulationTrialTask(TaskSnapshot task,string epoch,string mapping,string participantClock,long revision,long packetSequence,double sentLocalSeconds)
        {
            if(task==null)throw new ArgumentNullException("task");
            WireCodec.RequireIdentity(epoch);WireCodec.RequireIdentity(mapping);WireCodec.RequireIdentity(participantClock);
            if(revision<0||packetSequence<0||!WireCodec.Finite(sentLocalSeconds)||sentLocalSeconds<0)throw new ArgumentException("Invalid task evidence");
            Task=task;Epoch=epoch;Mapping=mapping;ParticipantClock=participantClock;Revision=revision;PacketSequence=packetSequence;SentLocalSeconds=sentLocalSeconds;
        }
    }
    public sealed class ViveSimulationTrialPoseFrame
    {
        public ViveSimulationTrialTask Task {get;private set;}
        public string OperatorClock {get;private set;}
        public long Sequence {get;private set;}
        public double TaskReceivedOperatorSeconds {get;private set;}
        public double HeadObservedOperatorSeconds {get;private set;}
        public double EyeObservedOperatorSeconds {get;private set;}
        public double SentOperatorSeconds {get;private set;}
        public Rotation Head {get;private set;}
        public Vec3 Eye {get;private set;}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool CryptographicallyAuthenticated {get{return false;}}
        internal ViveSimulationTrialPoseFrame(ViveSimulationTrialTask task,string operatorClock,long sequence,double taskReceived,double headObserved,double eyeObserved,double sent,Rotation head,Vec3 eye)
        {
            if(task==null)throw new ArgumentNullException("task");WireCodec.RequireIdentity(operatorClock);
            if(sequence<0||!Time(taskReceived)||!Time(headObserved)||!Time(eyeObserved)||!Time(sent)||
                headObserved<=taskReceived||eyeObserved<=taskReceived||sent<headObserved||sent<eyeObserved||!head.IsUnit||!eye.IsUnit)
                throw new ArgumentException("Invalid simulation pose evidence");
            Task=task;OperatorClock=operatorClock;Sequence=sequence;TaskReceivedOperatorSeconds=taskReceived;
            HeadObservedOperatorSeconds=headObserved;EyeObservedOperatorSeconds=eyeObserved;SentOperatorSeconds=sent;Head=head;Eye=eye;
        }
        static bool Time(double value){return WireCodec.Finite(value)&&value>=0;}
    }

    // New explicit codec namespace: unrelated to Quest and task v2 wire. It
    // carries the sender's received task context, not a receiver-inferred trial.
    public static class ViveSimulationTrialPoseCodec
    {
        public const int MaximumBytes=2048;
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static string Id(string value){WireCodec.RequireIdentity(value);return Convert.ToBase64String(Utf8.GetBytes(value));}
        static string ReadId(string value){if(value.Length>172)throw new FormatException("identity too long");string s=Utf8.GetString(Convert.FromBase64String(value));if(Id(s)!=value)throw new FormatException("identity encoding");return s;}
        static string N(long n){return n.ToString(Inv);}
        static long ReadN(string s){long n;if(!Int64.TryParse(s,NumberStyles.None,Inv,out n)||n<0||N(n)!=s)throw new FormatException("integer");return n;}
        static string D(double d){if(!WireCodec.Finite(d))throw new ArgumentException("finite value required");return unchecked((ulong)BitConverter.DoubleToInt64Bits(d)).ToString("x16",Inv);}
        static double ReadD(string s){ulong n;if(s.Length!=16||!UInt64.TryParse(s,NumberStyles.AllowHexSpecifier,Inv,out n)||n.ToString("x16",Inv)!=s)throw new FormatException("float bits");double v=BitConverter.Int64BitsToDouble(unchecked((long)n));if(!WireCodec.Finite(v))throw new FormatException("nonfinite");return v;}
        static string Optional<T>(T? value) where T:struct {return value.HasValue?N(Convert.ToInt64(value.Value,Inv)):"-";}
        static T? ReadOptional<T>(string s) where T:struct {if(s=="-")return null;long n=ReadN(s);if(n>Int32.MaxValue||!Enum.IsDefined(typeof(T),(int)n))throw new FormatException("enum");return (T)Enum.ToObject(typeof(T),(int)n);}
        static string[] TaskFields(ViveSimulationTrialTask t)
        {
            var s=t.Task;return new[]{Id(s.SessionId),Id(t.Epoch),Id(t.Mapping),Id(t.ParticipantClock),N(t.Revision),N(t.PacketSequence),D(t.SentLocalSeconds),
                N(s.BlockIndex+1),N(s.Segment),s.TrialId==null?"-":Id(s.TrialId),Optional(s.Word),Optional(s.Ink),N((int)s.Phase),Optional(s.Condition)};
        }
        static ViveSimulationTrialTask ReadTask(string[] f,int offset)
        {
            string session=ReadId(f[offset]),epoch=ReadId(f[offset+1]),mapping=ReadId(f[offset+2]),clock=ReadId(f[offset+3]);
            long rev=ReadN(f[offset+4]),seq=ReadN(f[offset+5]),b=ReadN(f[offset+7]),seg=ReadN(f[offset+8]),p=ReadN(f[offset+12]);
            if(b>4||seg>Int32.MaxValue||p>Int32.MaxValue||!Enum.IsDefined(typeof(Phase),(int)p))throw new FormatException("task range");
            string trial=f[offset+9]=="-"?null:ReadId(f[offset+9]);
            var task=new TaskSnapshot(session,(int)b-1,(int)seg,trial,ReadOptional<Colour>(f[offset+10]),ReadOptional<Colour>(f[offset+11]),(Phase)p,ReadOptional<Condition>(f[offset+13]));
            return new ViveSimulationTrialTask(task,epoch,mapping,clock,rev,seq,ReadD(f[offset+6]));
        }
        static byte[] Frame(string kind,string[] fields)
        {var b=Encoding.ASCII.GetBytes("VSTP|1|"+kind+"|"+String.Join("|",fields));if(b.Length>MaximumBytes)throw new ArgumentException("packet too long");return b;}
        public static byte[] EncodeTask(ViveSimulationTrialTask task){if(task==null)throw new ArgumentNullException("task");return Frame("T",TaskFields(task));}
        internal static byte[] EncodePose(ViveSimulationTrialPoseFrame p)
        {
            var fields=new List<string>(TaskFields(p.Task));fields.AddRange(new[]{Id(p.OperatorClock),N(p.Sequence),D(p.TaskReceivedOperatorSeconds),D(p.HeadObservedOperatorSeconds),D(p.EyeObservedOperatorSeconds),D(p.SentOperatorSeconds),
                D(p.Head.X),D(p.Head.Y),D(p.Head.Z),D(p.Head.W),D(p.Eye.X),D(p.Eye.Y),D(p.Eye.Z)});return Frame("P",fields.ToArray());
        }
        static string[] Parse(byte[] bytes,string kind,int length)
        {
            if(bytes==null||bytes.Length==0||bytes.Length>MaximumBytes)throw new FormatException("packet length");
            foreach(byte b in bytes)if(b<33||b>126)throw new FormatException("ASCII framing");
            var f=Encoding.ASCII.GetString(bytes).Split('|');if(f.Length!=length||f[0]!="VSTP"||f[1]!="1"||f[2]!=kind)throw new FormatException("envelope");return f;
        }
        public static bool TryDecodeTask(byte[] bytes,out ViveSimulationTrialTask task)
        {task=null;try{task=ReadTask(Parse(bytes,"T",17),3);return true;}catch(Exception e) when(e is ArgumentException||e is FormatException||e is OverflowException){return false;}}
        public static bool TryDecodePose(byte[] bytes,out ViveSimulationTrialPoseFrame pose)
        {
            pose=null;try{var f=Parse(bytes,"P",30);var task=ReadTask(f,3);int i=17;
                pose=new ViveSimulationTrialPoseFrame(task,ReadId(f[i]),ReadN(f[i+1]),ReadD(f[i+2]),ReadD(f[i+3]),ReadD(f[i+4]),ReadD(f[i+5]),
                    new Rotation(ReadD(f[i+6]),ReadD(f[i+7]),ReadD(f[i+8]),ReadD(f[i+9])),new Vec3(ReadD(f[i+10]),ReadD(f[i+11]),ReadD(f[i+12])));return true;
            }catch(Exception e) when(e is ArgumentException||e is FormatException||e is OverflowException){return false;}
        }
    }

    // Sender uses the task bytes it actually observed. The callback invoking
    // ObserveTask is responsible for supplying the real peer endpoint result;
    // neither its claim nor this test-only protocol proves identity.
    public sealed class ViveSimulationTrialPoseSender
    {
        readonly IClock clock;readonly string session,epoch,mapping,participantClock,operatorClock;readonly double maximumAge;
        readonly object gate=new object();readonly int owner=Thread.CurrentThread.ManagedThreadId;
        ViveSimulationTrialTask current;double received,lastNow=-1,lastSent=-1,lastPacketReceipt=-1,lastHead=-1,lastEye=-1;long lastTask=-1,lastRevision=-1,nextPose;
        volatile bool faulted;bool busy;
        public string LastIssue {get;private set;}
        public bool Faulted {get{return faulted;}private set{faulted=value;}}
        public bool CanEnterCountdown {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public ViveSimulationTrialPoseSender(IClock clock,string session,string epoch,string mapping,string participantClock,double maximumAge,bool simulationOnly)
        {
            if(!simulationOnly)throw new InvalidOperationException("Simulation-only sender required");
            if(clock==null)throw new ArgumentNullException("clock");WireCodec.RequireIdentity(session);WireCodec.RequireIdentity(epoch);
            WireCodec.RequireIdentity(mapping);WireCodec.RequireIdentity(participantClock);WireCodec.RequireIdentity(clock.ClockId);
            if(clock.ClockId==participantClock||!WireCodec.Finite(maximumAge)||maximumAge<=0)throw new ArgumentException("Distinct clocks and positive age required");
            this.clock=clock;this.session=session;this.epoch=epoch;this.mapping=mapping;this.participantClock=participantClock;operatorClock=clock.ClockId;this.maximumAge=maximumAge;
        }
        double Now()
        {
            try
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner)throw new InvalidOperationException("Sender belongs to its owner thread");
                double value=clock.Now;
                string observedClock=clock.ClockId;
                if(Faulted||observedClock!=operatorClock||!WireCodec.Finite(value)||value<0||value<lastNow)
                    throw new InvalidOperationException("Sender clock failed; new epoch required");
                lastNow=value;return value;
            }
            catch{Faulted=true;current=null;throw;}
        }
        static bool Active(TaskSnapshot t)
        {return t.Phase==Phase.MainTask&&t.BlockIndex>=0&&t.TrialId!=null&&t.Word.HasValue&&t.Ink.HasValue&&t.Condition.HasValue&&t.Condition.Value!=Condition.D;}
        void Enter()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||busy||Faulted)
            {Faulted=true;current=null;throw new InvalidOperationException("Sender reentrancy, foreign thread or fault; new epoch required");}
            busy=true;
        }
        public bool ObserveTask(byte[] bytes,bool endpointMatched,double originalOperatorReceipt)
        {
            lock(gate){Enter();try{return ObserveTaskCore(bytes,endpointMatched,originalOperatorReceipt);}catch{Faulted=true;current=null;throw;}finally{busy=false;}}
        }
        bool ObserveTaskCore(byte[] bytes,bool endpointMatched,double originalOperatorReceipt)
        {
            double now=Now();ViveSimulationTrialTask task;
            if(!WireCodec.Finite(originalOperatorReceipt)||originalOperatorReceipt<0||originalOperatorReceipt>now||originalOperatorReceipt<lastPacketReceipt)
            {Faulted=true;current=null;LastIssue="operator_receipt_clock_failed";return false;}
            lastPacketReceipt=originalOperatorReceipt;
            if(now-originalOperatorReceipt>maximumAge){LastIssue="stale_task_receipt";return false;}
            if(!endpointMatched||!ViveSimulationTrialPoseCodec.TryDecodeTask(bytes,out task)||task.Task.SessionId!=session||
                task.Epoch!=epoch||task.Mapping!=mapping||task.ParticipantClock!=participantClock){LastIssue="task_peer_or_context_mismatch";return false;}
            if(task.PacketSequence<=lastTask){LastIssue="duplicate_or_old_task";return false;}
            if(task.Revision<lastRevision||task.SentLocalSeconds<lastSent)
            {Faulted=true;current=null;LastIssue="task_revision_or_clock_rollback";return false;}
            if(task.Revision==lastRevision&&current!=null&&!ViveSimulationTrialPoseReceiver.SameTask(current.Task,task.Task))
            {Faulted=true;current=null;LastIssue="task_changed_without_revision";return false;}
            lastTask=task.PacketSequence;lastRevision=task.Revision;lastSent=task.SentLocalSeconds;received=originalOperatorReceipt;current=task;
            LastIssue=Active(task.Task)?null:"task_not_presentable";return Active(task.Task);
        }
        public bool TryCreatePose(double headObserved,double eyeObserved,Rotation head,Vec3 eye,out byte[] bytes)
        {
            lock(gate){Enter();try{return TryCreatePoseCore(headObserved,eyeObserved,head,eye,out bytes);}catch{Faulted=true;current=null;throw;}finally{busy=false;}}
        }
        bool TryCreatePoseCore(double headObserved,double eyeObserved,Rotation head,Vec3 eye,out byte[] bytes)
        {
            bytes=null;double now=Now();
            if(current==null||!Active(current.Task)||now-received>maximumAge){LastIssue="fresh_task_unavailable";return false;}
            if(!WireCodec.Finite(headObserved)||!WireCodec.Finite(eyeObserved)||headObserved<=received||eyeObserved<=received||
                headObserved>now||eyeObserved>now||now-headObserved>maximumAge||now-eyeObserved>maximumAge||
                headObserved<=lastHead||eyeObserved<=lastEye||!head.IsUnit||!eye.IsUnit)
            {LastIssue="fresh_complete_pose_unavailable";return false;}
            if(nextPose==Int64.MaxValue){Faulted=true;current=null;LastIssue="pose_sequence_exhausted";return false;}
            var p=new ViveSimulationTrialPoseFrame(current,operatorClock,nextPose++,received,headObserved,eyeObserved,now,head,eye);
            bytes=ViveSimulationTrialPoseCodec.EncodePose(p);lastHead=headObserved;lastEye=eyeObserved;LastIssue=null;return true;
        }
    }

    // A bounded ledger is populated only after the participant's task-send
    // callback reports socket acceptance. That callback is an integration
    // obligation, not proof of delivery or authentication. Original receive
    // time is supplied by the receiving socket callback and never refreshed by
    // later reads. This class has no device, study gate, persistence or UI.
    public sealed class ViveSimulationTrialPoseReceiver
    {
        readonly IClock clock;readonly ClockSynchronizer mappingClock;readonly string session,epoch,mapping,participantClock,operatorClock;
        readonly object gate=new object();readonly int owner=Thread.CurrentThread.ManagedThreadId;
        readonly double maximumAge;readonly int capacity;readonly Dictionary<long,ViveSimulationTrialTask> sent=new Dictionary<long,ViveSimulationTrialTask>();
        readonly Queue<long> order=new Queue<long>();
        ViveSimulationTrialPoseFrame current;TaskSnapshot lastTaskSnapshot;double currentReceipt,lastNow=-1,lastPacketReceipt=-1,lastTaskSent=-1,lastHead=-1,lastEye=-1;long lastTask=-1,lastRevision=-1,lastPose=-1;
        volatile bool faulted;bool busy;
        public string LastIssue {get;private set;}
        public bool Faulted {get{return faulted;}private set{faulted=value;}}
        public bool CanEnterCountdown {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public bool CryptographicallyAuthenticated {get{return false;}}
        public ViveSimulationTrialPoseReceiver(IClock clock,ClockSynchronizer mappingClock,string session,string epoch,string mapping,string operatorClock,double maximumAge,int ledgerCapacity,bool simulationOnly)
        {
            if(!simulationOnly)throw new InvalidOperationException("Simulation-only receiver required");
            if(clock==null||mappingClock==null)throw new ArgumentNullException("clock/mappingClock");
            WireCodec.RequireIdentity(clock.ClockId);WireCodec.RequireIdentity(session);WireCodec.RequireIdentity(epoch);WireCodec.RequireIdentity(mapping);WireCodec.RequireIdentity(operatorClock);
            if(clock.ClockId==operatorClock||!WireCodec.Finite(maximumAge)||maximumAge<=0||ledgerCapacity<1||ledgerCapacity>4096)throw new ArgumentException("Invalid simulation policy");
            this.clock=clock;this.mappingClock=mappingClock;this.session=session;this.epoch=epoch;this.mapping=mapping;this.operatorClock=operatorClock;
            participantClock=clock.ClockId;this.maximumAge=maximumAge;capacity=ledgerCapacity;
        }
        internal static bool SameTask(TaskSnapshot a,TaskSnapshot b)
        {return a!=null&&b!=null&&a.SessionId==b.SessionId&&a.BlockIndex==b.BlockIndex&&a.Segment==b.Segment&&a.TrialId==b.TrialId&&a.Word==b.Word&&a.Ink==b.Ink&&a.Phase==b.Phase&&a.Condition==b.Condition;}
        double Now()
        {
            try
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner)throw new InvalidOperationException("Receiver belongs to its owner thread");
                double value=clock.Now;
                string observedClock=clock.ClockId;
                if(Faulted||observedClock!=participantClock||!WireCodec.Finite(value)||value<0||value<lastNow)
                    throw new InvalidOperationException("Receiver clock failed; new epoch required");
                lastNow=value;return value;
            }
            catch{Faulted=true;current=null;throw;}
        }
        void Enter()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||busy||Faulted)
            {Faulted=true;current=null;throw new InvalidOperationException("Receiver reentrancy, foreign thread or fault; new epoch required");}
            busy=true;
        }
        public void RecordTaskSocketAccepted(ViveSimulationTrialTask task,bool socketAccepted)
        {
            lock(gate){Enter();try{RecordTaskSocketAcceptedCore(task,socketAccepted);}catch{Faulted=true;current=null;throw;}finally{busy=false;}}
        }
        void RecordTaskSocketAcceptedCore(ViveSimulationTrialTask task,bool socketAccepted)
        {
            double now=Now();
            if(!socketAccepted||task==null||task.Task.SessionId!=session||task.Epoch!=epoch||task.Mapping!=mapping||task.ParticipantClock!=participantClock||
                task.SentLocalSeconds>now||task.PacketSequence<=lastTask||task.Revision<lastRevision||task.SentLocalSeconds<lastTaskSent)
                throw new InvalidOperationException("Increasing explicitly accepted task-send evidence required");
            if(task.Revision==lastRevision&&!SameTask(lastTaskSnapshot,task.Task))
            {Faulted=true;current=null;throw new InvalidOperationException("Task changed without an authority revision");}
            lastTask=task.PacketSequence;lastRevision=task.Revision;lastTaskSent=task.SentLocalSeconds;lastTaskSnapshot=task.Task;sent.Add(task.PacketSequence,task);order.Enqueue(task.PacketSequence);
            while(order.Count>capacity)sent.Remove(order.Dequeue());
        }
        bool Mapped(double remote,double received,double now,bool freshness,out double earliest)
        {
            earliest=Double.NaN;var snapshot=mappingClock.Snapshot();double mapped,uncertainty;
            if(!snapshot.Available||snapshot.LocalClockId!=participantClock||snapshot.RemoteClockId!=operatorClock||
                !mappingClock.TryMapRemote(remote,out mapped,out uncertainty)||!WireCodec.Finite(uncertainty)||uncertainty<0)return false;
            earliest=mapped-uncertainty;
            return earliest>=0&&mapped+uncertainty<=received&&(!freshness||now-earliest<=maximumAge);
        }
        bool Valid(ViveSimulationTrialPoseFrame pose,double received,double now,TaskSnapshot authority,long revision)
        {
            if(pose==null||authority==null||authority.Phase!=Phase.MainTask||!authority.Condition.HasValue||authority.Condition.Value==Condition.D||
                authority.TrialId==null||revision<0||pose.OperatorClock!=operatorClock||pose.Task.Epoch!=epoch||pose.Task.Mapping!=mapping||
                pose.Task.ParticipantClock!=participantClock||pose.Task.Revision!=revision||!SameTask(authority,pose.Task.Task))return false;
            ViveSimulationTrialTask original;
            if(!sent.TryGetValue(pose.Task.PacketSequence,out original)||original.Revision!=pose.Task.Revision||original.SentLocalSeconds!=pose.Task.SentLocalSeconds||
                original.Epoch!=pose.Task.Epoch||original.Mapping!=pose.Task.Mapping||!SameTask(original.Task,pose.Task.Task))return false;
            double taskReceipt,unused;
            return Mapped(pose.TaskReceivedOperatorSeconds,received,now,false,out taskReceipt)&&taskReceipt>=original.SentLocalSeconds&&
                Mapped(pose.HeadObservedOperatorSeconds,received,now,true,out unused)&&Mapped(pose.EyeObservedOperatorSeconds,received,now,true,out unused)&&
                Mapped(pose.SentOperatorSeconds,received,now,false,out unused)&&received<=now&&now-received<=maximumAge;
        }
        public bool Observe(byte[] bytes,bool endpointMatched,double originalLocalReceipt,TaskSnapshot authority,long revision)
        {
            lock(gate){Enter();try{return ObserveCore(bytes,endpointMatched,originalLocalReceipt,authority,revision);}catch{Faulted=true;current=null;throw;}finally{busy=false;}}
        }
        bool ObserveCore(byte[] bytes,bool endpointMatched,double originalLocalReceipt,TaskSnapshot authority,long revision)
        {
            double now=Now();current=null;ViveSimulationTrialPoseFrame pose;
            if(!WireCodec.Finite(originalLocalReceipt)||originalLocalReceipt<0||originalLocalReceipt>now||originalLocalReceipt<lastPacketReceipt)
            {Faulted=true;LastIssue="receiver_receipt_clock_failed";return false;}
            lastPacketReceipt=originalLocalReceipt;
            if(!endpointMatched||!ViveSimulationTrialPoseCodec.TryDecodePose(bytes,out pose)||pose.Sequence<=lastPose||
                pose.HeadObservedOperatorSeconds<=lastHead||pose.EyeObservedOperatorSeconds<=lastEye||!Valid(pose,originalLocalReceipt,now,authority,revision))
            {LastIssue="invalid_stale_or_wrong_trial_pose";return false;}
            lastPose=pose.Sequence;lastHead=pose.HeadObservedOperatorSeconds;lastEye=pose.EyeObservedOperatorSeconds;
            current=pose;currentReceipt=originalLocalReceipt;LastIssue=null;return true;
        }
        public ViveSimulationTrialPoseFrame Current(TaskSnapshot authority,long revision)
        {
            lock(gate){Enter();try{return CurrentCore(authority,revision);}catch{Faulted=true;current=null;throw;}finally{busy=false;}}
        }
        ViveSimulationTrialPoseFrame CurrentCore(TaskSnapshot authority,long revision)
        {
            double now=Now();if(current==null)return null;
            if(!Valid(current,currentReceipt,now,authority,revision)){current=null;LastIssue="pose_expired_or_context_changed";return null;}
            return current;
        }
        public bool TryGetCurrent(TaskSnapshot authority,long revision,out ViveSimulationTrialPoseFrame pose,out double originalLocalReceipt)
        {
            lock(gate){Enter();try{pose=CurrentCore(authority,revision);originalLocalReceipt=pose==null?0:currentReceipt;return pose!=null;}catch{Faulted=true;current=null;throw;}finally{busy=false;}}
        }
        public void Clear(){lock(gate){Enter();try{Now();current=null;}catch{Faulted=true;current=null;throw;}finally{busy=false;}}}
    }
}
