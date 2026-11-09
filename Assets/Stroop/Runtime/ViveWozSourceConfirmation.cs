using System;
using System.Globalization;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Separate development control message; neither declared IDs, a matching
    // endpoint, nor the challenge are cryptographic peer authentication.
    public enum ViveWozSourceAction { Confirm=1, Revoke=2 }
    public enum ViveWozSourceEvidenceState { Awaiting, ConfirmedCandidate, Revoked, Faulted }

    // An integration must bind this reader to its own current authoritative
    // task/source state, not reconstruct selection from TaskSnapshot or packet
    // text. TaskRevision must advance once for every authoritative task/source
    // mutation (including pause/resume) and never be collapsed or reset within
    // this context. A jump is rejected; an integration that cannot supply this
    // complete stream must remain blocked. The booleans are still local claims,
    // not peer authentication. No such concrete production binding exists here.
    public interface IViveWozSourceStateReader
    {ViveWozSourceLocalState ReadCurrent();}
    public sealed class ViveWozSourceLocalState
    {
        public string BoundSessionId {get;private set;} public string BoundOwnerId {get;private set;}
        public TaskSnapshot Task {get;private set;} public ControlSelection? LockedSelection {get;private set;}
        public bool ExplicitLocalChoiceStillActive {get;private set;} public bool TaskFresh {get;private set;} public long TaskRevision {get;private set;}
        public ViveWozSourceLocalState(string session,string owner,TaskSnapshot task,ControlSelection? lockedSelection,bool explicitLocalChoiceStillActive,bool taskFresh,long taskRevision)
        {BoundSessionId=session;BoundOwnerId=owner;Task=task;LockedSelection=lockedSelection;ExplicitLocalChoiceStillActive=explicitLocalChoiceStillActive;TaskFresh=taskFresh;TaskRevision=taskRevision;}
    }
    static class ViveWozSourceStateChecks
    {
        internal static int PhaseOrder(Phase phase)
        {return phase==Phase.BlockReady?0:phase==Phase.Countdown?1:phase==Phase.MainTask?2:-1;}
        internal static bool SameTask(TaskSnapshot a,TaskSnapshot b)
        {return a!=null&&b!=null&&a.SessionId==b.SessionId&&a.BlockIndex==b.BlockIndex&&a.Segment==b.Segment&&a.Phase==b.Phase&&
            a.Condition==b.Condition&&a.TrialId==b.TrialId&&a.Word==b.Word&&a.Ink==b.Ink;}
        internal static bool Read(IViveWozSourceStateReader reader,ViveWozSourceContext context,out TaskSnapshot task,out long revision)
        {
            task=null;revision=-1;try
            {
                var current=reader.ReadCurrent();
                if(current==null||current.BoundSessionId!=context.SessionId||current.BoundOwnerId!=context.OwnerId||
                    current.LockedSelection!=ControlSelection.LiveWoz||!current.ExplicitLocalChoiceStillActive||!current.TaskFresh||current.TaskRevision<0||current.TaskRevision==Int64.MaxValue||
                    !context.MatchesTask(current.Task))return false;
                task=current.Task;revision=current.TaskRevision;return true;
            }
            catch(Exception){return false;}
        }
    }

    public sealed class ViveWozSourceContext
    {
        public string SessionId {get;private set;} public string OwnerId {get;private set;}
        public string OperatorStreamId {get;private set;} public string MappingVersion {get;private set;}
        public string Epoch {get;private set;} public string Challenge {get;private set;}
        public string SenderClockId {get;private set;} public string ReceiverClockId {get;private set;}
        public int Block {get;private set;} public int Segment {get;private set;} public Condition Condition {get;private set;}
        public ViveWozSourceContext(string session,string owner,string operatorStream,string mappingVersion,string epoch,string challenge,
            string senderClock,string receiverClock,int block,int segment,Condition condition)
        {
            foreach(string value in new[]{session,owner,operatorStream,mappingVersion,epoch,challenge,senderClock,receiverClock})WireCodec.RequireIdentity(value);
            if(senderClock==receiverClock||operatorStream!=ViveDevelopmentTransport.OperatorStream(mappingVersion,epoch)||
                block<0||block>3||segment<0||!Enum.IsDefined(typeof(Condition),condition))throw new ArgumentException("Invalid live development context");
            SessionId=session;OwnerId=owner;OperatorStreamId=operatorStream;MappingVersion=mappingVersion;Epoch=epoch;Challenge=challenge;
            SenderClockId=senderClock;ReceiverClockId=receiverClock;Block=block;Segment=segment;Condition=condition;
        }
        public bool MatchesTask(TaskSnapshot task)
        {return task!=null&&task.SessionId==SessionId&&task.BlockIndex==Block&&task.Segment==Segment&&task.Condition==Condition&&
            (task.Phase==Phase.BlockReady||task.Phase==Phase.Countdown||task.Phase==Phase.MainTask);}
        internal bool Same(ViveWozSourceContext other)
        {return other!=null&&SessionId==other.SessionId&&OwnerId==other.OwnerId&&OperatorStreamId==other.OperatorStreamId&&
            MappingVersion==other.MappingVersion&&Epoch==other.Epoch&&Challenge==other.Challenge&&SenderClockId==other.SenderClockId&&
            ReceiverClockId==other.ReceiverClockId&&Block==other.Block&&Segment==other.Segment&&Condition==other.Condition;}
    }
    public sealed class ViveWozSourceMessage
    {
        public ViveWozSourceContext Context {get;private set;} public ViveWozSourceAction Action {get;private set;}
        public long Sequence {get;private set;} public double SentLocalSeconds {get;private set;}
        internal ViveWozSourceMessage(ViveWozSourceContext context,ViveWozSourceAction action,long sequence,double sent)
        {
            if(context==null||!Enum.IsDefined(typeof(ViveWozSourceAction),action)||sequence<0||sequence==Int64.MaxValue||
                !WireCodec.Finite(sent)||sent<0)throw new ArgumentException("Invalid source control message");
            Context=context;Action=action;Sequence=sequence;SentLocalSeconds=sent;
        }
    }

    // A bounded canonical codec, independent of Quest/ASW v2. Decoding alone
    // never grants source selection, tracking readiness or apparatus access.
    public static class ViveWozSourceWire
    {
        public const string Version="1";public const int MaximumBytes=2048;
        static readonly UTF8Encoding Strict=new UTF8Encoding(false,true);static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        static string Id(string value){WireCodec.RequireIdentity(value);return Convert.ToBase64String(Strict.GetBytes(value));}
        static string ReadId(string value){if(value.Length>172)throw new FormatException();string id=Strict.GetString(Convert.FromBase64String(value));if(Id(id)!=value)throw new FormatException();return id;}
        static string Number(long value){return value.ToString(Inv);}
        static long ReadNumber(string token){long value;if(token.Length==0||token.Length>20||!Int64.TryParse(token,NumberStyles.AllowLeadingSign,Inv,out value)||Number(value)!=token)throw new FormatException();return value;}
        static string Time(double value){if(!WireCodec.Finite(value))throw new ArgumentException("Nonfinite time");return unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("x16",Inv);}
        static double ReadTime(string token)
        {
            if(token.Length!=16)throw new FormatException();foreach(char c in token)if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')))throw new FormatException();
            ulong bits;if(!UInt64.TryParse(token,NumberStyles.AllowHexSpecifier,Inv,out bits))throw new FormatException();
            double value=BitConverter.Int64BitsToDouble(unchecked((long)bits));if(!WireCodec.Finite(value))throw new FormatException();return value;
        }
        public static byte[] Encode(ViveWozSourceMessage message)
        {
            if(message==null)throw new ArgumentNullException("message");var c=message.Context;
            string value=String.Join("|",new[]{"VLSC",Version,message.Action==ViveWozSourceAction.Confirm?"CONFIRM":"REVOKE",Id(c.SessionId),Id(c.OwnerId),
                Id(c.OperatorStreamId),Id(c.MappingVersion),Id(c.Epoch),Id(c.Challenge),Id(c.SenderClockId),Id(c.ReceiverClockId),
                Number(c.Block),Number(c.Segment),c.Condition.ToString(),Number(message.Sequence),Time(message.SentLocalSeconds)});
            var bytes=Encoding.ASCII.GetBytes(value);if(bytes.Length>MaximumBytes)throw new ArgumentException("Source message too large");return bytes;
        }
        public static bool TryDecode(byte[] bytes,out ViveWozSourceMessage message,out string error)
        {
            message=null;error=null;if(bytes==null||bytes.Length==0||bytes.Length>MaximumBytes){error="size";return false;}
            foreach(byte b in bytes)if(b<33||b>126){error="non_ascii_or_whitespace";return false;}
            var f=Encoding.ASCII.GetString(bytes).Split('|');if(f.Length!=16||f[0]!="VLSC"||f[1]!=Version){error="envelope_or_version";return false;}
            try
            {
                ViveWozSourceAction action;if(f[2]=="CONFIRM")action=ViveWozSourceAction.Confirm;else if(f[2]=="REVOKE")action=ViveWozSourceAction.Revoke;else throw new FormatException();
                Condition condition;if(!Enum.TryParse<Condition>(f[13],false,out condition)||condition.ToString()!=f[13])throw new FormatException();
                var context=new ViveWozSourceContext(ReadId(f[3]),ReadId(f[4]),ReadId(f[5]),ReadId(f[6]),ReadId(f[7]),ReadId(f[8]),ReadId(f[9]),ReadId(f[10]),
                    checked((int)ReadNumber(f[11])),checked((int)ReadNumber(f[12])),condition);
                message=new ViveWozSourceMessage(context,action,ReadNumber(f[14]),ReadTime(f[15]));return true;
            }
            catch(Exception e) when(e is ArgumentException||e is FormatException||e is OverflowException)
            {message=null;error="invalid_payload:"+e.GetType().Name;return false;}
        }
    }

    // This owner-side object provides local state before any send is attempted.
    // Revoke is terminal immediately; the caller must clear/pause locally and
    // treat an unsuccessful/uncertain send as loss, never automatic retry.
    public sealed class ViveWozSourceOwner
    {
        readonly ViveWozSourceContext context;readonly IClock clock;readonly IViveWozSourceStateReader stateReader;readonly int ownerThread;
        long sequence,lastTaskRevision=-1;double lastTime=-1;int lastPhase=-1,threadFault;bool confirmed,terminal;TaskSnapshot lastTask;
        public bool LocallyConfirmed {get{if(ThreadFaulted())return false;Owner();if(!confirmed||terminal)return false;TaskSnapshot task;if(!ReadState(out task)){confirmed=false;terminal=true;return false;}
            try{Now();if(Volatile.Read(ref threadFault)!=0){confirmed=false;terminal=true;return false;}return true;}catch(Exception){return false;}}}
        public bool Terminal {get{return ThreadFaulted()||terminal;}}
        public bool CryptographicallyAuthenticated {get{return false;}}
        public ViveWozSourceOwner(ViveWozSourceContext context,IClock senderClock,IViveWozSourceStateReader stateReader)
        {if(context==null||senderClock==null||stateReader==null||senderClock.ClockId!=context.SenderClockId)throw new ArgumentException("Matching sender clock and bound source state required");this.context=context;clock=senderClock;this.stateReader=stateReader;ownerThread=Thread.CurrentThread.ManagedThreadId;}
        bool ThreadFaulted()
        {if(Thread.CurrentThread.ManagedThreadId!=ownerThread)Interlocked.Exchange(ref threadFault,1);return Volatile.Read(ref threadFault)!=0;}
        void Owner()
        {
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread)
            {Interlocked.Exchange(ref threadFault,1);throw new InvalidOperationException("Owner thread required");}
            if(Volatile.Read(ref threadFault)!=0){confirmed=false;terminal=true;}
        }
        double Now()
        {
            try{double now=clock.Now;if(clock.ClockId!=context.SenderClockId||!WireCodec.Finite(now)||now<0||now<lastTime)throw new InvalidOperationException();lastTime=now;return now;}
            catch(Exception e){confirmed=false;terminal=true;throw new InvalidOperationException("Sender clock failed",e);}
        }
        bool ReadState(out TaskSnapshot task)
        {long revision;if(!ViveWozSourceStateChecks.Read(stateReader,context,out task,out revision))return false;
            int phase=ViveWozSourceStateChecks.PhaseOrder(task.Phase);if(phase<lastPhase||(lastPhase>=0&&phase>lastPhase+1)||revision<lastTaskRevision||
                (lastTaskRevision>=0&&(lastTaskRevision==Int64.MaxValue||revision>lastTaskRevision+1))||
                (revision==lastTaskRevision&&!ViveWozSourceStateChecks.SameTask(lastTask,task)))return false;
            lastPhase=phase;lastTaskRevision=revision;lastTask=task;return true;}
        public byte[] Confirm(bool explicitOwnerChoice)
        {
            Owner();TaskSnapshot task;
            if(terminal)throw new InvalidOperationException("Owner source epoch is terminal");
            if(!explicitOwnerChoice||confirmed||!ReadState(out task)||task.Phase!=Phase.BlockReady)
            {if(confirmed){confirmed=false;terminal=true;}
                throw new InvalidOperationException("One explicit live source choice in BlockReady is required");
            }
            double now=Now();var bytes=ViveWozSourceWire.Encode(new ViveWozSourceMessage(context,ViveWozSourceAction.Confirm,sequence,now));
            if(Volatile.Read(ref threadFault)!=0){confirmed=false;terminal=true;throw new InvalidOperationException("Owner source epoch is terminal");}
            sequence++;confirmed=true;return bytes;
        }
        public byte[] Revoke(bool explicitOwnerChoice)
        {
            Owner();if(!explicitOwnerChoice||terminal)throw new InvalidOperationException("Explicit unrepeated revocation required");
            // Clear locally BEFORE touching the clock/encoding or transport.
            confirmed=false;terminal=true;double now=Now();return ViveWozSourceWire.Encode(new ViveWozSourceMessage(context,ViveWozSourceAction.Revoke,sequence,now));
        }
        public void SignalSendFailure(){Owner();confirmed=false;terminal=true;}
    }

    // Development evidence only. Caller must route each accepted packet and
    // each loss signal on the owner thread, clear live pose, pause and verify
    // independently. No sender authentication or network delivery is implied.
    public sealed class ViveWozSourceReceiver
    {
        readonly ViveWozSourceContext context;readonly IClock clock;readonly ClockSynchronizer mapping;readonly IViveWozSourceStateReader stateReader;
        readonly double maximumAge,maximumUncertainty,contextStart;readonly int ownerThread;
        double lastNow,receivedAt,remoteSent,deadline;long expectedSequence,lastTaskRevision=-1;int lastPhase=-1,threadFault,externalFault;bool selected;TaskSnapshot lastTask;
        ViveWozSourceEvidenceState state;string lastIssue,externalIssue;
        public ViveWozSourceEvidenceState State {get{return ThreadFaulted()?ViveWozSourceEvidenceState.Faulted:state;}private set{state=value;}}
        public string LastIssue {get{return ThreadFaulted()?AsyncFaultIssue():lastIssue;}private set{lastIssue=value;}}
        public bool CryptographicallyAuthenticated {get{return false;}} public bool CanEnterCountdown {get{return false;}}
        public bool MustClearLivePose {get{if(ThreadFaulted())return true;try{return !Candidate();}catch(Exception){Fail("receiver_safety_check_failed");return true;}}}
        public bool MustPauseLiveTask {get{if(ThreadFaulted())return true;try{if(State==ViveWozSourceEvidenceState.ConfirmedCandidate)Candidate();
            return State==ViveWozSourceEvidenceState.Revoked||State==ViveWozSourceEvidenceState.Faulted;}
            catch(Exception){Fail("receiver_safety_check_failed");return true;}}}
        public ViveWozSourceReceiver(ViveWozSourceContext context,IClock receiverClock,ClockSynchronizer mapping,IViveWozSourceStateReader stateReader,
            double maximumAgeSeconds,double maximumUncertaintySeconds,bool explicitLocalLiveChoice)
        {
            if(!explicitLocalLiveChoice)throw new InvalidOperationException("Explicit local live choice is required");
            if(context==null||receiverClock==null||mapping==null||stateReader==null||receiverClock.ClockId!=context.ReceiverClockId||
                !WireCodec.Finite(maximumAgeSeconds)||maximumAgeSeconds<=0||!WireCodec.Finite(maximumUncertaintySeconds)||maximumUncertaintySeconds<=0)
                throw new ArgumentException("Explicit context, clock and freshness settings required");
            var snapshot=mapping.Snapshot();if(snapshot.LocalClockId!=context.ReceiverClockId||snapshot.RemoteClockId!=context.SenderClockId)
                throw new ArgumentException("Matching clock mapping required");
            double now=receiverClock.Now;if(!WireCodec.Finite(now)||now<0)throw new ArgumentException("Valid receiver clock required");
            this.context=context;clock=receiverClock;this.mapping=mapping;this.stateReader=stateReader;maximumAge=maximumAgeSeconds;maximumUncertainty=maximumUncertaintySeconds;
            contextStart=lastNow=now;ownerThread=Thread.CurrentThread.ManagedThreadId;State=ViveWozSourceEvidenceState.Awaiting;
        }
        bool HasAsyncFault(){return Volatile.Read(ref threadFault)!=0||Volatile.Read(ref externalFault)!=0;}
        string AsyncFaultIssue(){return Volatile.Read(ref threadFault)!=0?"receiver_thread_violation":Volatile.Read(ref externalIssue)??lastIssue??"receiver_external_loss";}
        // This only withdraws a synthetic declaration; it does not authenticate
        // a peer or claim that an already returned pose can be recalled.
        internal bool InvalidateFromAnyThread(){return Interlocked.CompareExchange(ref externalFault,1,0)==0;}
        // The wrapper may attach a reason only to the transition it first won.
        // An earlier unlabelled loss stays unlabelled rather than being rewritten.
        internal void RecordInitialExternalLoss(string reason)
        {
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread){Interlocked.Exchange(ref threadFault,1);throw new InvalidOperationException("Receiver owner thread required");}
            if(Volatile.Read(ref externalFault)!=0&&state!=ViveWozSourceEvidenceState.Faulted&&state!=ViveWozSourceEvidenceState.Revoked&&!String.IsNullOrWhiteSpace(reason))
                Interlocked.CompareExchange(ref externalIssue,"loss:"+reason,null);
        }
        bool ThreadFaulted()
        {if(Thread.CurrentThread.ManagedThreadId!=ownerThread)Interlocked.Exchange(ref threadFault,1);return HasAsyncFault();}
        void Owner()
        {
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread)
            {Interlocked.Exchange(ref threadFault,1);throw new InvalidOperationException("Receiver owner thread required");}
            if(HasAsyncFault())Fail(AsyncFaultIssue());
        }
        bool Fail(string issue){selected=false;State=ViveWozSourceEvidenceState.Faulted;LastIssue=issue;return false;}
        bool TryNow(out double now)
        {
            now=Double.NaN;
            try{now=clock.Now;if(clock.ClockId!=context.ReceiverClockId||!WireCodec.Finite(now)||now<0||now<lastNow)return Fail("receiver_clock_invalid");lastNow=now;return true;}
            catch(Exception){return Fail("receiver_clock_failed");}
        }
        bool Fresh(double remote,double received,double now,out double safeDeadline)
        {
            safeDeadline=Double.NaN;
            var s=mapping.Snapshot();double local,uncertainty;
            if(!s.Available||s.LocalClockId!=context.ReceiverClockId||s.RemoteClockId!=context.SenderClockId||
                !mapping.TryMapRemote(remote,out local,out uncertainty)||!WireCodec.Finite(uncertainty)||uncertainty<0||uncertainty>maximumUncertainty)return false;
            double earliest=local-uncertainty,latest=local+uncertainty;
            safeDeadline=earliest+maximumAge;
            return WireCodec.Finite(earliest)&&WireCodec.Finite(latest)&&WireCodec.Finite(safeDeadline)&&earliest>=contextStart&&latest<=received&&received<=now&&now<=safeDeadline;
        }
        bool ReadState(out TaskSnapshot task)
        {long revision;if(!ViveWozSourceStateChecks.Read(stateReader,context,out task,out revision))return false;
            int phase=ViveWozSourceStateChecks.PhaseOrder(task.Phase);if(phase<lastPhase||(lastPhase>=0&&phase>lastPhase+1)||revision<lastTaskRevision||
                (lastTaskRevision>=0&&(lastTaskRevision==Int64.MaxValue||revision>lastTaskRevision+1))||
                (revision==lastTaskRevision&&!ViveWozSourceStateChecks.SameTask(lastTask,task)))return false;
            lastPhase=phase;lastTaskRevision=revision;lastTask=task;return true;}
        public bool Observe(byte[] bytes,bool configuredEndpointMatched)
        {
            Owner();if(State==ViveWozSourceEvidenceState.Faulted||State==ViveWozSourceEvidenceState.Revoked)return false;
            double now;if(!TryNow(out now))return false;
            ViveWozSourceMessage message;string error;
            if(!ViveWozSourceWire.TryDecode(bytes,out message,out error))return Fail("decode:"+error);
            TaskSnapshot currentTask;
            if(!configuredEndpointMatched||!context.Same(message.Context)||!ReadState(out currentTask))return Fail("peer_or_task_context_mismatch");
            // Even a late/reordered matching revoke invalidates before any
            // sequence/time check; failure remains terminal, never live.
            if(message.Action==ViveWozSourceAction.Revoke)selected=false;
            if(message.Sequence!=expectedSequence||message.Sequence==Int64.MaxValue-1)return Fail("duplicate_reordered_or_exhausted_sequence");
            double candidateDeadline;
            if(!Fresh(message.SentLocalSeconds,now,now,out candidateDeadline))return Fail("source_message_stale_future_or_clock_unavailable");
            if(HasAsyncFault())return Fail(AsyncFaultIssue());
            if(message.Action==ViveWozSourceAction.Confirm)
            {
                if(selected||currentTask.Phase!=Phase.BlockReady)return Fail("confirmation_not_permitted");
                selected=true;State=ViveWozSourceEvidenceState.ConfirmedCandidate;receivedAt=now;remoteSent=message.SentLocalSeconds;deadline=candidateDeadline;
            }
            else {selected=false;State=ViveWozSourceEvidenceState.Revoked;}
            expectedSequence++;
            if(HasAsyncFault())return Fail(AsyncFaultIssue());
            LastIssue=null;return true;
        }
        public bool Candidate()
        {
            Owner();if(State!=ViveWozSourceEvidenceState.ConfirmedCandidate||!selected)return false;
            double now;if(!TryNow(out now))return false;
            TaskSnapshot task;if(!ReadState(out task))return Fail("current_task_or_source_changed_or_stale");
            double revisedDeadline;
            if(now>deadline||!Fresh(remoteSent,receivedAt,now,out revisedDeadline)||now>Math.Min(deadline,revisedDeadline))
                return Fail("confirmation_expired_or_mapping_changed");
            if(HasAsyncFault())return Fail(AsyncFaultIssue());
            deadline=Math.Min(deadline,revisedDeadline);
            return true;
        }
        public void SignalLoss(string reason)
        {Owner();if(String.IsNullOrWhiteSpace(reason)){Fail("loss_reason_invalid");throw new ArgumentException("Explicit loss reason required");}
            string issue="loss:"+reason;
            if(State!=ViveWozSourceEvidenceState.Faulted)Fail(issue);}
    }
}
