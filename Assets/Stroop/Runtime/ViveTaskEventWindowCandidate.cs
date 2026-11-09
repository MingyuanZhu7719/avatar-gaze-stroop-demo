using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Read-only development bridge to actual participant task declarations.
    // One event per page bounds this bridge's own output, not the allocations
    // made internally by ReadEventsAfter. It does not attach to a recorder,
    // authorize a camera, sample eyes, open a file, or prove display/durability.
    // Callers must fence their own publication against later withdrawal: an
    // already returned managed string cannot be revoked or securely erased.
    internal sealed class ViveTaskEventWindowCandidate
    {
        internal sealed class Snapshot
        {
            internal readonly long Sequence;internal readonly double LocalEventTime;
            internal readonly int Block,Segment;internal readonly string ClockId,Kind,TrialId,Word,Ink,Response,DeclaredCorrect,DeclaredOnset,DeclaredReactionSeconds,Actor;
            internal bool IsTaskDeclaration {get{return Kind!="OTHER";}}
            internal string ActualVisibility {get{return "UNKNOWN";}}
            internal string ActualValidity {get{return "UNKNOWN";}}
            internal string EyeCoverage {get{return "UNKNOWN";}}
            internal bool Durable {get{return false;}}
            internal Snapshot(JournalEvent e)
            {
                Sequence=e.Sequence;LocalEventTime=e.Time;Block=e.BlockIndex;Segment=e.Segment;ClockId=Bound(e.ClockId);
                Kind=Selected(e.Kind)?e.Kind:"OTHER";
                if(Kind!="OTHER")
                {
                    TrialId=Bound(e.TrialId);Word=Field(e,"word");Ink=Field(e,"ink");Response=Field(e,"response");
                    DeclaredCorrect=Field(e,"correct");DeclaredOnset=Field(e,"onset");DeclaredReactionSeconds=Field(e,"rt_seconds");Actor=Field(e,"actor");
                    if(Kind=="trial_planned"){Word=Field(e,"planned_word");Ink=Field(e,"planned_ink");}
                }
            }
        }
        readonly ViveDevelopmentParticipant participant;readonly MonotonicClock clock;readonly Thread owner;
        readonly ViveRawRevocation revocation;readonly ViveNamedSimulationPlan plan;readonly ViveNamedRecordingIntent intent;readonly string session;
        ViveDevelopmentParticipant.NamedRecordingTaskStamp pageStamp;object pendingToken;long cursor;double lastTime=-1,pendingTime;
        int stopped;
#if VIVE_TASK_WINDOW_TEST_HOOKS
        internal Action BeforePendingPublishForTest {get;set;}
        internal Action AfterPendingPublishForTest {get;set;}
        internal bool HasPendingForTest {get{return revocation.SimulationWindowHasPage(this);}}
#endif
        ViveTaskEventWindowCandidate(ViveDevelopmentParticipant p,MonotonicClock c,ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp)
        {participant=p;clock=c;owner=Thread.CurrentThread;revocation=p.NamedRawRevocation;plan=stamp.Plan;intent=stamp.Intent;session=stamp.Session;cursor=stamp.EventCursor;}
        internal bool PreWindowCoverageKnown {get{return false;}}
        internal bool ProductAuthority {get{return false;}}
        internal bool Stopped {get{return Volatile.Read(ref stopped)!=0||revocation.IsRevoked;}}
        internal long Cursor {get{return cursor;}}
        static bool Selected(string kind){return kind=="trial_planned"||kind=="stimulus_presented"||kind=="first_response"||kind=="trial_interrupted";}
        static string Bound(string s){if(s!=null&&s.Length>128)throw new InvalidOperationException("Selected field exceeds bound.");return s;}
        static string Field(JournalEvent e,string name){string value;return e.Fields.TryGetValue(name,out value)?Bound(value):null;}
        static bool Same(ViveDevelopmentParticipant.NamedRecordingTaskStamp a,ViveDevelopmentParticipant.NamedRecordingTaskStamp b)
        {return a!=null&&b!=null&&Object.ReferenceEquals(a.Owner,b.Owner)&&Object.ReferenceEquals(a.Clock,b.Clock)&&
            Object.ReferenceEquals(a.Plan,b.Plan)&&Object.ReferenceEquals(a.Intent,b.Intent)&&a.Generation==b.Generation&&a.EventCursor==b.EventCursor&&
            a.Phase==b.Phase&&a.Block==b.Block&&a.Segment==b.Segment&&String.Equals(a.Session,b.Session,StringComparison.Ordinal)&&
            String.Equals(a.TrialId,b.TrialId,StringComparison.Ordinal);}
        bool Identity(ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp)
        {return stamp!=null&&Object.ReferenceEquals(stamp.Owner,participant)&&Object.ReferenceEquals(stamp.Clock,clock)&&
            Object.ReferenceEquals(stamp.Plan,plan)&&Object.ReferenceEquals(stamp.Intent,intent)&&String.Equals(stamp.Session,session,StringComparison.Ordinal);}
        bool Live(){return Volatile.Read(ref stopped)==0&&!revocation.IsRevoked&&Object.ReferenceEquals(Thread.CurrentThread,owner);}
        void Stop(){Interlocked.Exchange(ref stopped,1);revocation.CloseSimulationWindow(this);pendingToken=null;pageStamp=null;}
        internal void Close(){Stop();}
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        internal static bool TryOpen(ViveDevelopmentParticipant participant,MonotonicClock clock,out ViveTaskEventWindowCandidate result)
        {
            result=null;if(participant==null||clock==null||participant.IsResearchSession||!participant.NamedRecordingRequested||
                participant.NamedRawRevocation.IsRevoked||!participant.UsesEventClock(clock))return false;
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp before,after;
                if(!participant.TryCaptureNamedRecordingTaskStamp(clock,out before)||before==null||before.Plan==null||before.Intent==null||
                    !before.Intent.TaskRequested||String.IsNullOrWhiteSpace(before.Session)||before.Session.Length>128||before.EventCursor< -1||
                    !participant.TryCaptureNamedRecordingTaskStamp(clock,out after)||!Same(before,after)||participant.NamedRawRevocation.IsRevoked)return false;
                var candidate=new ViveTaskEventWindowCandidate(participant,clock,after);
                if(!candidate.revocation.TryAttachSimulationWindow(candidate))return false;
                if(candidate.revocation.IsRevoked){candidate.Stop();return false;}
                result=candidate;return true;
            }
            catch(Exception e) when(!Fatal(e)){return false;}
        }
        // Returns false on terminal fault; true + available=false means no new
        // event. A pending page must be consumed before reading another.
        internal bool TryReadNext(out bool available)
        {
            available=false;if(!Live()||pendingToken!=null||cursor==Int64.MaxValue){Stop();return false;}
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp before,after;
                if(!participant.TryCaptureNamedRecordingTaskStamp(clock,out before)||!Identity(before)||before.EventCursor<cursor){Stop();return false;}
                var events=participant.ReadEventsAfter(cursor,1);
                if(events.Count>1){Stop();return false;}
                if(events.Count==0)
                {
                    if(before.EventCursor!=cursor||!participant.TryCaptureNamedRecordingTaskStamp(clock,out after)||!Same(before,after)||!Live())
                    {Stop();return false;}return true;
                }
                var e=events[0];
                if(e==null||e.Sequence!=cursor+1||e.Sequence>before.EventCursor||!String.Equals(e.SessionId,session,StringComparison.Ordinal)||
                    !String.Equals(e.ClockId,clock.ClockId,StringComparison.Ordinal)||!Object.ReferenceEquals(e.NamedPlan,plan)||
                    !Object.ReferenceEquals(e.NamedRecordingIntent,intent)||Double.IsNaN(e.Time)||Double.IsInfinity(e.Time)||e.Time<0||e.Time<lastTime)
                {Stop();return false;}
                var snapshot=new Snapshot(e);
                if(!participant.TryCaptureNamedRecordingTaskStamp(clock,out after)||!Same(before,after)||!Live()){Stop();return false;}
#if VIVE_TASK_WINDOW_TEST_HOOKS
                var beforeHook=BeforePendingPublishForTest;if(beforeHook!=null)beforeHook();
#endif
                var token=new object();pageStamp=after;pendingTime=e.Time;pendingToken=token;
                if(!revocation.TryPublishSimulationPage(this,token,snapshot)){Stop();return false;}
#if VIVE_TASK_WINDOW_TEST_HOOKS
                var afterHook=AfterPendingPublishForTest;if(afterHook!=null)afterHook();
#endif
                // A concurrent stop/withdrawal before or during publication
                // must not leave a pending page once this call has returned.
                if(!Live()){Stop();return false;}available=true;return true;
            }
            catch(Exception e) when(!Fatal(e)){Stop();return false;}
        }
        internal bool TryTake(out Snapshot result)
        {
            result=null;if(!Live()||pendingToken==null||pageStamp==null){Stop();return false;}
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp before,after;
                if(!participant.TryCaptureNamedRecordingTaskStamp(clock,out before)||!Same(pageStamp,before)||
                    !participant.TryCaptureNamedRecordingTaskStamp(clock,out after)||!Same(before,after)||!Live()){Stop();return false;}
                object payload;
                if(!revocation.TryClaimSimulationPage(this,pendingToken,out payload)){Stop();return false;}
                var value=payload as Snapshot;pendingToken=null;pageStamp=null;
                if(value==null){Stop();return false;}cursor=value.Sequence;lastTime=pendingTime;
                if(!Live()){Stop();return false;}result=value;return true;
            }
            catch(Exception e) when(!Fatal(e)){Stop();return false;}
        }
    }
}
