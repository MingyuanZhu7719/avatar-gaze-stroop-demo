using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveTaskEventWindowCandidateTests
    {
        static ViveDevelopmentParticipant Participant(MonotonicClock clock)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"synthetic-window-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-window-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-window-intent",true,true);
            var config=SessionConfiguration.NamedRecordingDevelopment("synthetic-window-session",42,plan,intent);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"synthetic-window-geometry");
            var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var value=new ViveDevelopmentParticipant(config,clock,geometry,mapping,.5,true);
            value.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));return value;
        }
        static ViveTaskEventWindowCandidate Open(ViveDevelopmentParticipant task,MonotonicClock clock)
        {ViveTaskEventWindowCandidate value;Check.True(ViveTaskEventWindowCandidate.TryOpen(task,clock,out value)&&value!=null);return value;}
        static List<ViveTaskEventWindowCandidate.Snapshot> Drain(ViveTaskEventWindowCandidate source)
        {
            var result=new List<ViveTaskEventWindowCandidate.Snapshot>();
            for(int i=0;i<64;i++)
            {bool available;Check.True(source.TryReadNext(out available));if(!available)return result;
                ViveTaskEventWindowCandidate.Snapshot item;Check.True(source.TryTake(out item));result.Add(item);}
            throw new InvalidOperationException("Synthetic event budget exceeded.");
        }
        static void Press(ViveDevelopmentParticipant task,Colour colour)
        {
            double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0;
            double y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("task window observes real simulation plan render and response declarations only",()=>{
                var clock=new MonotonicClock("synthetic-window-clock");var task=Participant(clock);var window=Open(task,clock);
                Check.True(!window.PreWindowCoverageKnown&&!window.ProductAuthority);task.OwnerBeginPractice(Actor.Owner);
                var planned=Drain(window);Check.True(planned.Exists(x=>x.Kind=="trial_planned"));
                var trial=task.CurrentTrial;task.MarkPresented(trial.TrialId);var presented=Drain(window);
                var display=presented.Find(x=>x.Kind=="stimulus_presented");Check.True(display!=null);
                Check.Equal(trial.Stimulus.Word.ToString(),display.Word);Check.Equal(trial.Stimulus.Ink.ToString(),display.Ink);
                Check.Equal("UNKNOWN",display.ActualVisibility);Check.Equal("UNKNOWN",display.ActualValidity);Check.True(!display.Durable);
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Press(task,trial.Stimulus.Ink);var responses=Drain(window);var answer=responses.Find(x=>x.Kind=="first_response");
                Check.True(answer!=null&&answer.Response==trial.Stimulus.Ink.ToString());Check.Equal("UNKNOWN",answer.EyeCoverage);
            });
            suite.Run("task window empty poll and one-at-a-time pages do not invent events",()=>{
                var clock=new MonotonicClock("synthetic-window-empty-clock");var task=Participant(clock);var window=Open(task,clock);
                bool available;Check.True(window.TryReadNext(out available)&&!available);task.OwnerBeginPractice(Actor.Owner);
                var events=Drain(window);Check.True(events.Count>1);for(int i=1;i<events.Count;i++)Check.Equal(events[i-1].Sequence+1,events[i].Sequence);
            });
            suite.Run("task window invalidates staged page after task mutation",()=>{
                var clock=new MonotonicClock("synthetic-window-stale-clock");var task=Participant(clock);var window=Open(task,clock);
                task.OwnerBeginPractice(Actor.Owner);bool available;Check.True(window.TryReadNext(out available)&&available);
                task.MarkPresented(task.CurrentTrial.TrialId);ViveTaskEventWindowCandidate.Snapshot stale;
                Check.True(!window.TryTake(out stale)&&stale==null&&window.Stopped);
            });
            suite.Run("task window withdrawal blocks pending and future output",()=>{
                var clock=new MonotonicClock("synthetic-window-withdraw-clock");var task=Participant(clock);var window=Open(task,clock);
                task.OwnerBeginPractice(Actor.Owner);bool available;Check.True(window.TryReadNext(out available)&&available);
                task.ParticipantWithdraw();ViveTaskEventWindowCandidate.Snapshot stale;
                Check.True(!window.TryTake(out stale)&&stale==null&&window.Stopped);Check.True(!window.TryReadNext(out available));
            });
            suite.Run("task window rejects other clock and overlapping page",()=>{
                var clock=new MonotonicClock("synthetic-window-one-clock");var task=Participant(clock);ViveTaskEventWindowCandidate invalid;
                Check.True(!ViveTaskEventWindowCandidate.TryOpen(task,new MonotonicClock("synthetic-window-other-clock"),out invalid));
                var window=Open(task,clock);task.OwnerBeginPractice(Actor.Owner);bool available;
                Check.True(window.TryReadNext(out available)&&available);Check.True(!window.TryReadNext(out available)&&window.Stopped);
            });
            suite.Run("task window foreign thread cannot read or publish",()=>{
                var clock=new MonotonicClock("synthetic-window-owner-clock");var task=Participant(clock);var window=Open(task,clock);
                bool foreign=true;var thread=new Thread(()=>{bool available;foreign=window.TryReadNext(out available);});
                thread.Start();Check.True(thread.Join(3000));Check.True(!foreign&&window.Stopped);
            });
            suite.Run("task window single-owner shared revoke detaches unread page",()=>{
                var clock=new MonotonicClock("synthetic-window-shared-clock");var task=Participant(clock);var window=Open(task,clock);
                ViveTaskEventWindowCandidate second;Check.True(!ViveTaskEventWindowCandidate.TryOpen(task,clock,out second));
                task.OwnerBeginPractice(Actor.Owner);bool available;Check.True(window.TryReadNext(out available)&&available);
                Check.True(task.NamedRawRevocation.SimulationWindowHasPage(window));task.NamedRawRevocation.Revoke();
                Check.True(task.NamedRawRevocation.IsRevoked&&!task.NamedRawRevocation.SimulationWindowHasPage(window));
                ViveTaskEventWindowCandidate.Snapshot page;Check.True(!window.TryTake(out page)&&page==null);
            });
            suite.Run("task window manual close detaches without revoking participant",()=>{
                var clock=new MonotonicClock("synthetic-window-close-clock");var task=Participant(clock);var window=Open(task,clock);
                task.OwnerBeginPractice(Actor.Owner);bool available;Check.True(window.TryReadNext(out available)&&available);
                Check.True(task.NamedRawRevocation.SimulationWindowHasPage(window));window.Close();
                Check.True(window.Stopped&&!task.NamedRawRevocation.IsRevoked&&!task.NamedRawRevocation.SimulationWindowHasPage(window));
                ViveTaskEventWindowCandidate again;Check.True(!ViveTaskEventWindowCandidate.TryOpen(task,clock,out again));
            });
            suite.Run("task window shared slot isolates owner token and original revoke semantics",()=>{
                var shared=new ViveRawRevocation();var owner=new object();var other=new object();var token=new object();var wrongToken=new object();var payload=new object();object taken;
                Check.True(shared.TryAttachSimulationWindow(owner));Check.True(!shared.TryAttachSimulationWindow(other));
                Check.True(!shared.TryPublishSimulationPage(other,token,payload));Check.True(shared.TryPublishSimulationPage(owner,token,payload));
                Check.True(!shared.TryClaimSimulationPage(owner,wrongToken,out taken));shared.CloseSimulationWindow(other);
                Check.True(shared.SimulationWindowHasPage(owner));shared.Revoke();shared.Revoke();
                Check.True(shared.IsRevoked&&!shared.SimulationWindowHasPage(owner));Check.True(!shared.TryClaimSimulationPage(owner,token,out taken));
                Check.True(!shared.TryAttachSimulationWindow(other)&&!shared.TryPublishSimulationPage(owner,token,payload));
                var ordinary=new ViveRawRevocation();Check.True(!ordinary.IsRevoked);ordinary.Revoke();Check.True(ordinary.IsRevoked);
            });
            suite.Run("task window claimed page is not falsely recalled on later revoke",()=>{
                var shared=new ViveRawRevocation();var owner=new object();var token=new object();var payload=new object();object claimed;
                Check.True(shared.TryAttachSimulationWindow(owner)&&shared.TryPublishSimulationPage(owner,token,payload));
                Check.True(shared.TryClaimSimulationPage(owner,token,out claimed)&&Object.ReferenceEquals(payload,claimed));
                shared.Revoke();Check.True(shared.IsRevoked&&!shared.SimulationWindowHasPage(owner));
                Check.True(Object.ReferenceEquals(payload,claimed)); // Already delivered cannot be recalled.
            });
            suite.Run("task window concurrent shared revoke never leaves published page",()=>{
                for(int i=0;i<32;i++){
                    var shared=new ViveRawRevocation();var owner=new object();var token=new object();var payload=new object();
                    Check.True(shared.TryAttachSimulationWindow(owner));var go=new ManualResetEvent(false);
                    var thread=new Thread(()=>{go.WaitOne();shared.Revoke();});thread.Start();go.Set();
                    shared.TryPublishSimulationPage(owner,token,payload);Check.True(thread.Join(3000));go.Dispose();
                    Check.True(shared.IsRevoked&&!shared.SimulationWindowHasPage(owner));object claim;
                    Check.True(!shared.TryClaimSimulationPage(owner,token,out claim)&&claim==null);
                }
            });
#if VIVE_TASK_WINDOW_TEST_HOOKS
            suite.Run("task window withdrawal before and after page publication clears pending",()=>{
                foreach(bool before in new[]{true,false}){
                    var clock=new MonotonicClock("synthetic-window-publish-withdraw-clock");var task=Participant(clock);var window=Open(task,clock);
                    task.OwnerBeginPractice(Actor.Owner);Action revoke=task.NamedRawRevocation.Revoke;
                    if(before)window.BeforePendingPublishForTest=revoke;else window.AfterPendingPublishForTest=revoke;
                    bool available;Check.True(!window.TryReadNext(out available)&&!available&&window.Stopped&&!window.HasPendingForTest);
                }
            });
            suite.Run("task window competing foreign stop before publication clears pending",()=>{
                var clock=new MonotonicClock("synthetic-window-publish-stop-clock");var task=Participant(clock);var window=Open(task,clock);
                task.OwnerBeginPractice(Actor.Owner);window.BeforePendingPublishForTest=()=>{
                    bool foreign=true;var thread=new Thread(()=>{bool present;foreign=window.TryReadNext(out present);});
                    thread.Start();Check.True(thread.Join(3000));Check.True(!foreign);
                };
                bool available;Check.True(!window.TryReadNext(out available)&&!available&&window.Stopped&&!window.HasPendingForTest);
            });
#endif
        }
    }
}
