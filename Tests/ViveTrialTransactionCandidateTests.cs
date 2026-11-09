using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveTrialTransactionCandidateTests
    {
        const string Session="synthetic-session",Trial="synthetic-trial";
        static ViveTrialTransactionCandidate Open(out object run)
        {
            var candidate=new ViveTrialTransactionCandidate(Session,Trial,5);run=candidate.SyntheticRun;
            Check.True(candidate.OpenSynthetic(Session,Trial,run,5,100));return candidate;
        }
        static void RenderAndAction(ViveTrialTransactionCandidate candidate,object run)
        {
            // These are independent clock domains; Unity's lower number is not
            // treated as earlier than the local Stopwatch value.
            Check.True(candidate.ObserveSyntheticRender(Session,Trial,run,5,7,42,1.25,100.1));
            Check.True(candidate.TentativeSyntheticAction(Session,Trial,run,5,2,100.2));
        }
        static ViveDevelopmentParticipant Participant(MonotonicClock clock)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"render-proxy-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-render-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-render-intent",true,true);
            var config=SessionConfiguration.NamedRecordingDevelopment("synthetic-render-session",42,plan,intent);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"render-proxy-geometry");
            var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var participant=new ViveDevelopmentParticipant(config,clock,geometry,mapping,.5,true);
            participant.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));participant.OwnerBeginPractice(Actor.Owner);
            return participant;
        }
        static ViveDevelopmentParticipant BeforePractice(MonotonicClock clock)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"render-proxy-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-block-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-block-intent",true,true);
            var config=SessionConfiguration.NamedRecordingDevelopment("synthetic-block-session",42,plan,intent);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,1),new Vec3(1,0,0),"render-proxy-geometry");
            var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var participant=new ViveDevelopmentParticipant(config,clock,geometry,mapping,.5,true);
            participant.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));return participant;
        }
        static JournalEvent CopyPlanned(JournalEvent source,IDictionary<string,string> fields,ViveNamedSimulationPlan plan,ViveNamedSimulationBlock block,ViveNamedRecordingIntent intent)
        {return new JournalEvent(source.Sequence,source.Time,source.ClockId,source.SessionId,source.BlockIndex,source.Segment,source.TrialId,source.Kind,fields,source.NamedOption,plan,block,intent);}
        static Dictionary<string,string> CopyFields(IReadOnlyDictionary<string,string> source)
        {
            var copy=new Dictionary<string,string>();
            foreach(var entry in source)copy.Add(entry.Key,entry.Value);
            return copy;
        }
        static void Trackpad(ViveDevelopmentParticipant participant,bool pressed,Colour colour=Colour.Red)
        {
            double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0;
            double y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
            participant.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,pressed,x,y));
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("block plan request binds authoritative prepared practice and exact FIFO copy",()=>{
                var clock=new MonotonicClock("synthetic-block-request-clock");var participant=BeforePractice(clock);
                var prepared=participant.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
                ViveBlockPlanRequest request;Check.True(ViveBlockPlanRequest.TryCapture(participant,clock,prepared,out request));
                Check.True(request.PracticeScope&&request.TryValidateCurrentForReceipt(participant,clock));
                var copied=participant.ReadEventsAfter(prepared.Sequence-1,1)[0];
                Check.True(!Object.ReferenceEquals(prepared,copied)&&request.MatchesWriterPlannedEvent(copied));
                var fields=CopyFields(copied.Fields);fields["item_00_word"]=fields["item_00_word"]=="Red"?"Blue":"Red";
                Check.True(!request.MatchesWriterPlannedEvent(CopyPlanned(copied,fields,copied.NamedPlan,copied.NamedBlock,copied.NamedRecordingIntent)));
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(participant.TryCaptureNamedRecordingTaskStamp(clock,out stamp));
                Check.True(request.BelongsTo(participant,clock,stamp.Plan,stamp.Intent,participant.NamedRawRevocation));
                Check.True(!request.BelongsTo(participant,new MonotonicClock(clock.ClockId),stamp.Plan,stamp.Intent,participant.NamedRawRevocation));
                bool foreign=true;var thread=new Thread(()=>foreign=request.TryValidateCurrentForReceipt(participant,clock));
                thread.Start();Check.True(thread.Join(3000));Check.True(!foreign);
            });
            suite.Run("block plan request cannot survive mutation or shared revoke",()=>{
                var clock=new MonotonicClock("synthetic-block-change-clock");var participant=BeforePractice(clock);
                var prepared=participant.PrepareNamedRecordingBlockPlan(Actor.Owner,true);ViveBlockPlanRequest request;
                Check.True(ViveBlockPlanRequest.TryCapture(participant,clock,prepared,out request));
                participant.Tick();Check.True(!request.TryValidateCurrentForReceipt(participant,clock));
                participant.NamedRawRevocation.Revoke();Check.True(!request.MatchesWriterPlannedEvent(prepared));
                Check.True(!ViveBlockPlanRequest.TryCapture(participant,clock,prepared,out request)&&request==null);
            });
            suite.Run("synthetic trial transaction remains unknown after its in-memory sequence",()=>{
                object run;var candidate=Open(out run);Check.Equal("UNKNOWN",candidate.ActualValidity);
                RenderAndAction(candidate,run);Check.True(candidate.FinalizeSynthetic(Session,Trial,run,5,100.3));
                Check.True(candidate.SyntheticSequenceCompleted&&!candidate.DurabilityProven&&!candidate.ResearchAdmission);
                Check.Equal("UNKNOWN",candidate.ActualValidity);
            });
            suite.Run("synthetic trial candidate rejects non-synthetic and mismatched identities",()=>{
                Check.Throws<ArgumentException>(()=>new ViveTrialTransactionCandidate("real-session",Trial,0));
                foreach(int mismatch in new[]{0,1,2,3})
                {
                    object run;var candidate=Open(out run);
                    Check.True(!candidate.ObserveSyntheticRender(mismatch==0?"synthetic-other":Session,mismatch==1?"synthetic-other":Trial,
                        mismatch==2?new object():run,mismatch==3?6:5,7,42,1,100.1));
                    Check.True(candidate.TerminalUnknown&&!candidate.SyntheticSequenceCompleted);Check.Equal("UNKNOWN",candidate.ActualValidity);
                }
            });
            suite.Run("synthetic trial ordering duplicate and backward local time fail closed",()=>{
                object run;var a=Open(out run);Check.True(!a.TentativeSyntheticAction(Session,Trial,run,5,1,100.1));Check.True(a.TerminalUnknown);
                var b=Open(out run);Check.True(b.ObserveSyntheticRender(Session,Trial,run,5,1,1,1,100.1));
                Check.True(!b.ObserveSyntheticRender(Session,Trial,run,5,1,1,1,100.1));Check.True(b.TerminalUnknown);
                var c=Open(out run);Check.True(!c.ObserveSyntheticRender(Session,Trial,run,5,1,1,1,99));Check.True(c.TerminalUnknown);
            });
            suite.Run("synthetic withdrawal and fault latch unknown across a completed sequence",()=>{
                object run;var candidate=Open(out run);RenderAndAction(candidate,run);
                var thread=new Thread(candidate.Withdraw);thread.Start();Check.True(thread.Join(3000));
                Check.True(!candidate.FinalizeSynthetic(Session,Trial,run,5,100.3)&&candidate.TerminalUnknown);
                var already=Open(out run);RenderAndAction(already,run);Check.True(already.FinalizeSynthetic(Session,Trial,run,5,100.3));
                already.Fail();Check.True(!already.SyntheticSequenceCompleted&&already.TerminalUnknown);Check.Equal("UNKNOWN",already.ActualValidity);
            });
            suite.Run("foreign thread cannot apply a synthetic trial action",()=>{
                object run;var candidate=Open(out run);bool result=true;
                var thread=new Thread(()=>result=candidate.ObserveSyntheticRender(Session,Trial,run,5,1,1,1,100.1));thread.Start();Check.True(thread.Join(3000));
                Check.True(!result&&candidate.TerminalUnknown);
            });
            suite.Run("synthetic finalize and concurrent withdrawal always converge to unknown",()=>{
                for(int iteration=0;iteration<64;iteration++)
                {
                    object run;var candidate=Open(out run);RenderAndAction(candidate,run);
                    using(var ready=new ManualResetEvent(false))using(var go=new ManualResetEvent(false))
                    {
                        var thread=new Thread(()=>{ready.Set();go.WaitOne();candidate.Withdraw();});thread.Start();
                        try{Check.True(ready.WaitOne(3000));go.Set();candidate.FinalizeSynthetic(Session,Trial,run,5,100.3);}
                        finally{go.Set();Check.True(thread.Join(3000));}
                    }
                    Check.True(candidate.TerminalUnknown&&!candidate.SyntheticSequenceCompleted);
                    Check.Equal("UNKNOWN",candidate.ActualValidity);
                }
            });
            suite.Run("VIVE render proxy is ephemeral and tied to the actual simulation task clock and trial",()=>{
                var clock=new MonotonicClock("synthetic-render-clock");var participant=Participant(clock);var trial=participant.CurrentTrial;
                ViveRenderProxyObservation observed;
                Check.True(ViveRenderProxyObservation.TryObserve(participant,clock,trial.TrialId,1.25,out observed));
                Check.Equal(trial.TrialId,observed.Trial);Check.Equal(clock.ClockId,observed.TaskClock);Check.Equal(1.25,observed.UnityRenderProxySeconds);
                Check.Equal(trial.Stimulus.Word,observed.TaskModelWord);Check.Equal(trial.Stimulus.Ink,observed.TaskModelInk);
                Check.True(observed.TaskLocalObservationSeconds>=0&&!observed.HasPriorTrialOpenProof&&!observed.Durable&&!observed.AuthorizesMutation);
                Check.True(!trial.Onset.HasValue);
                Check.True(!ViveRenderProxyObservation.TryObserve(participant,clock,"synthetic-wrong-trial",1.25,out observed)&&observed==null);
                Check.True(!ViveRenderProxyObservation.TryObserve(participant,new MonotonicClock("synthetic-other-clock"),trial.TrialId,1.25,out observed));
                Check.True(!ViveRenderProxyObservation.TryObserve(participant,clock,trial.TrialId,Double.NaN,out observed));
                participant.ParticipantWithdraw();
                Check.True(!ViveRenderProxyObservation.TryObserve(participant,clock,trial.TrialId,1.25,out observed));
            });
            suite.Run("VIVE render proxy refuses a separately revoked admission before task withdrawal",()=>{
                var clock=new MonotonicClock("synthetic-render-revoked-clock");var participant=Participant(clock);
                var trial=participant.CurrentTrial;ViveRenderProxyObservation observed;
                Check.True(ViveRenderProxyObservation.TryObserve(participant,clock,trial.TrialId,1,out observed));
                participant.NamedRawRevocation.Revoke();
                Check.True(!ViveRenderProxyObservation.TryObserve(participant,clock,trial.TrialId,1,out observed)&&observed==null);
                Check.True(!trial.Onset.HasValue&&trial.Outcome==TrialOutcome.Pending);
            });
            suite.Run("VIVE render proxy fails closed when the task lock is busy",()=>{
                var clock=new MonotonicClock("synthetic-render-busy-clock");var participant=Participant(clock);string trial=participant.CurrentTrial.TrialId;
                using(var ready=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {
                    var thread=new Thread(()=>{lock(participant.NamedRecordingGate){ready.Set();release.WaitOne(3000);}});thread.Start();
                    try{Check.True(ready.WaitOne(3000));ViveRenderProxyObservation observed;
                        Check.True(!ViveRenderProxyObservation.TryObserve(participant,clock,trial,1,out observed)&&observed==null);}
                    finally{release.Set();Check.True(thread.Join(3000));}
                }
            });
            suite.Run("actual simulation trial-open request binds task event but never claims receipt or presentation",()=>{
                var clock=new MonotonicClock("synthetic-open-clock");var participant=Participant(clock);var trial=participant.CurrentTrial;
                ViveTrialOpenRequest request;
                Check.True(ViveTrialOpenRequest.TryCapture(participant,clock,out request));
                Check.Equal(trial.SessionId,request.Session);Check.Equal(trial.TrialId,request.Trial);Check.Equal(clock.ClockId,request.ClockId);
                Check.Equal("synthetic-render-plan",request.PlanId);Check.Equal("synthetic-render-intent",request.IntentId);Check.Equal(trial.ListId,request.ListId);
                Check.Equal(trial.Stimulus.Word,request.PlannedWord);Check.Equal(trial.Stimulus.Ink,request.PlannedInk);
                var taskView=participant.ParticipantView;
                Check.Equal(request.Trial,taskView.TrialId);Check.Equal(request.PlannedWord,taskView.Stimulus.Word);Check.Equal(request.PlannedInk,taskView.Stimulus.Ink);
                Check.Equal(trial.PlannedIndex,request.PlannedIndex);Check.Equal(trial.PracticeAttempt,request.PracticeAttempt);
                Check.True(request.PlannedEventSequence>=0&&request.PlannedEventSequence<=request.TaskCursor&&request.TaskLocalObserved>=0);
                Check.True(!request.HasWriteReceipt&&!request.HasFlushReceipt&&!request.MayShowStimulus);Check.Equal("UNKNOWN",request.Validity);
                Check.True(!trial.Onset.HasValue);
                Check.True(!ViveTrialOpenRequest.TryCapture(participant,new MonotonicClock("synthetic-wrong-open-clock"),out request)&&request==null);
                participant.MarkPresented(trial.TrialId);
                Check.True(!ViveTrialOpenRequest.TryCapture(participant,clock,out request)&&request==null);
            });
            suite.Run("actual simulation trial-open request cannot use busy task or withdrawn trial",()=>{
                var clock=new MonotonicClock("synthetic-open-busy-clock");var participant=Participant(clock);
                using(var ready=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {
                    var thread=new Thread(()=>{lock(participant.NamedRecordingGate){ready.Set();release.WaitOne(3000);}});thread.Start();
                    try{Check.True(ready.WaitOne(3000));ViveTrialOpenRequest request;
                        Check.True(!ViveTrialOpenRequest.TryCapture(participant,clock,out request)&&request==null);}
                    finally{release.Set();Check.True(thread.Join(3000));}
                }
                participant.ParticipantWithdraw();ViveTrialOpenRequest after;
                Check.True(!ViveTrialOpenRequest.TryCapture(participant,clock,out after)&&after==null);
            });
            suite.Run("trial-open receipt checks bind exact simulation instances and owner thread",()=>{
                var clock=new MonotonicClock("synthetic-receipt-clock");var participant=Participant(clock);
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(participant.TryCaptureNamedRecordingTaskStamp(clock,out stamp));
                ViveTrialOpenRequest request;Check.True(ViveTrialOpenRequest.TryCapture(participant,clock,out request));
                Check.True(request.BelongsTo(participant,clock,stamp.Plan,stamp.Intent,participant.NamedRawRevocation));
                Check.True(!request.BelongsTo(Participant(clock),clock,stamp.Plan,stamp.Intent,participant.NamedRawRevocation));
                Check.True(!request.BelongsTo(participant,clock,stamp.Plan,stamp.Intent,new ViveRawRevocation()));
                Check.True(!request.BelongsTo(participant,new MonotonicClock("synthetic-receipt-clock"),stamp.Plan,stamp.Intent,participant.NamedRawRevocation));
                var otherPlan=ViveNamedSimulationPlan.Select(Actor.Owner,stamp.Plan.PlanId,stamp.Plan.Blocks[0].Selection);
                var otherIntent=ViveNamedRecordingIntent.Select(Actor.Owner,stamp.Intent.IntentId,true,true);
                Check.True(!request.BelongsTo(participant,clock,otherPlan,stamp.Intent,participant.NamedRawRevocation));
                Check.True(!request.BelongsTo(participant,clock,stamp.Plan,otherIntent,participant.NamedRawRevocation));
                bool foreign=true;var thread=new Thread(()=>foreign=request.TryValidateCurrentForReceipt(participant,clock));
                thread.Start();Check.True(thread.Join(3000));Check.True(!foreign);
                Check.True(!request.HasWriteReceipt&&!request.HasFlushReceipt&&!request.MayShowStimulus);
            });
            suite.Run("trial-open writer match uses exact planned cursor and rejects mutation and revocation",()=>{
                var clock=new MonotonicClock("synthetic-receipt-event-clock");var participant=Participant(clock);
                ViveTrialOpenRequest request;Check.True(ViveTrialOpenRequest.TryCapture(participant,clock,out request));
                JournalEvent planned=null;foreach(var item in participant.ReadEventsAfter(-1,4096))if(item.Sequence==request.PlannedEventSequence)planned=item;
                Check.True(planned!=null);
                Check.Equal(request.PlannedEventSequence,request.TaskCursor);
                Check.True(request.MatchesWriterPlannedEvent(planned));Check.True(request.TryValidateCurrentForReceipt(participant,clock));
                var fields=CopyFields(planned.Fields);fields["list"]="synthetic-wrong-list";
                Check.True(!request.MatchesWriterPlannedEvent(CopyPlanned(planned,fields,planned.NamedPlan,planned.NamedBlock,planned.NamedRecordingIntent)));
                participant.Tick();Check.True(!request.TryValidateCurrentForReceipt(participant,clock));
                participant.ParticipantWithdraw();Check.True(!request.MatchesWriterPlannedEvent(planned));
                Check.True(!request.TryValidateCurrentForReceipt(participant,clock));
            });
            suite.Run("trial-open receipt rejects cursor later than planned event",()=>{
                var clock=new MonotonicClock("synthetic-receipt-later-clock");var participant=Participant(clock);
                var experimentField=typeof(ViveDevelopmentParticipant).GetField("experiment",BindingFlags.Instance|BindingFlags.NonPublic);
                var eventsField=typeof(Experiment).GetField("events",BindingFlags.Instance|BindingFlags.NonPublic);
                Check.True(experimentField!=null&&eventsField!=null);
                var events=(List<JournalEvent>)eventsField.GetValue(experimentField.GetValue(participant));
                var planned=events[events.Count-1];Check.Equal("trial_planned",planned.Kind);
                events.Add(new JournalEvent(events.Count,planned.Time,planned.ClockId,planned.SessionId,planned.BlockIndex,planned.Segment,
                    planned.TrialId,"synthetic_unrelated",CopyFields(planned.Fields),planned.NamedOption,planned.NamedPlan,planned.NamedBlock,planned.NamedRecordingIntent));
                ViveTrialOpenRequest request;Check.True(ViveTrialOpenRequest.TryCapture(participant,clock,out request));
                Check.True(request.TaskCursor>request.PlannedEventSequence);
                Check.True(!request.MatchesWriterPlannedEvent(planned));Check.True(!request.TryValidateCurrentForReceipt(participant,clock));
            });
            suite.Run("trial-open planned event rejects mismatched list index plan intent and block",()=>{
                var clock=new MonotonicClock("synthetic-open-fields-clock");var participant=Participant(clock);var trial=participant.CurrentTrial;
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(participant.TryCaptureNamedRecordingTaskStamp(clock,out stamp));
                JournalEvent planned=null;foreach(var item in participant.ReadEventsAfter(-1,4096))if(item.Kind=="trial_planned")planned=item;
                Check.True(planned!=null&&ViveTrialOpenRequest.MatchesPlannedEvent(planned,stamp,trial,clock));
                var fields=CopyFields(planned.Fields);fields["list"]="synthetic-wrong-list";
                Check.True(!ViveTrialOpenRequest.MatchesPlannedEvent(CopyPlanned(planned,fields,stamp.Plan,trial.NamedBlock,stamp.Intent),stamp,trial,clock));
                fields=CopyFields(planned.Fields);fields["planned_index"]="999";
                Check.True(!ViveTrialOpenRequest.MatchesPlannedEvent(CopyPlanned(planned,fields,stamp.Plan,trial.NamedBlock,stamp.Intent),stamp,trial,clock));
                foreach(var key in new[]{"planned_stimulus_schema","planned_word","planned_ink"})
                {
                    fields=CopyFields(planned.Fields);fields.Remove(key);
                    var missing=CopyPlanned(planned,fields,stamp.Plan,trial.NamedBlock,stamp.Intent);
                    Check.True(!ViveTrialOpenRequest.MatchesPlannedEvent(missing,stamp,trial,clock));
                    ViveTrialOpenRequest exact;Check.True(ViveTrialOpenRequest.TryCapture(participant,clock,out exact));
                    Check.True(!exact.MatchesWriterPlannedEvent(missing));
                    fields=CopyFields(planned.Fields);fields[key]=key=="planned_stimulus_schema"?"2":
                        ((Colour)(((int)(key=="planned_word"?trial.Stimulus.Word:trial.Stimulus.Ink)+1)%4)).ToString();
                    var wrong=CopyPlanned(planned,fields,stamp.Plan,trial.NamedBlock,stamp.Intent);
                    Check.True(!ViveTrialOpenRequest.MatchesPlannedEvent(wrong,stamp,trial,clock));
                    Check.True(!exact.MatchesWriterPlannedEvent(wrong));
                }
                fields=CopyFields(planned.Fields);
                var otherPlan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-other-plan",stamp.Plan.Blocks[0].Selection);
                var otherIntent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-other-intent",true,true);
                Check.True(!ViveTrialOpenRequest.MatchesPlannedEvent(CopyPlanned(planned,fields,otherPlan,trial.NamedBlock,stamp.Intent),stamp,trial,clock));
                Check.True(!ViveTrialOpenRequest.MatchesPlannedEvent(CopyPlanned(planned,fields,stamp.Plan,trial.NamedBlock,otherIntent),stamp,trial,clock));
                Check.True(!ViveTrialOpenRequest.MatchesPlannedEvent(CopyPlanned(planned,fields,stamp.Plan,stamp.Plan.Blocks[0],stamp.Intent),stamp,trial,clock));
            });
            suite.Run("trial-open request fails closed when the bounded event window no longer includes the plan",()=>{
                var clock=new MonotonicClock("synthetic-open-window-clock");var participant=Participant(clock);
                var experimentField=typeof(ViveDevelopmentParticipant).GetField("experiment",BindingFlags.Instance|BindingFlags.NonPublic);
                var eventsField=typeof(Experiment).GetField("events",BindingFlags.Instance|BindingFlags.NonPublic);
                Check.True(experimentField!=null&&eventsField!=null);
                var experiment=(Experiment)experimentField.GetValue(participant);var events=(List<JournalEvent>)eventsField.GetValue(experiment);
                var template=events[events.Count-1];
                for(int i=0;i<65;i++)events.Add(new JournalEvent(events.Count,template.Time,template.ClockId,template.SessionId,
                    template.BlockIndex,template.Segment,template.TrialId,"synthetic_unrelated",CopyFields(template.Fields),
                    template.NamedOption,template.NamedPlan,template.NamedBlock,template.NamedRecordingIntent));
                ViveTrialOpenRequest request;Check.True(!ViveTrialOpenRequest.TryCapture(participant,clock,out request)&&request==null);
            });
            suite.Run("trial-open capture refuses absent or incorrect stimulus provenance",()=>{
                var clock=new MonotonicClock("synthetic-open-stimulus-clock");var participant=Participant(clock);
                var experimentField=typeof(ViveDevelopmentParticipant).GetField("experiment",BindingFlags.Instance|BindingFlags.NonPublic);
                var eventsField=typeof(Experiment).GetField("events",BindingFlags.Instance|BindingFlags.NonPublic);
                Check.True(experimentField!=null&&eventsField!=null);
                var events=(List<JournalEvent>)eventsField.GetValue(experimentField.GetValue(participant));
                int index=events.FindIndex(value=>value.Kind=="trial_planned");Check.True(index>=0);var planned=events[index];
                foreach(var key in new[]{"planned_stimulus_schema","planned_word","planned_ink"})
                {
                    var fields=CopyFields(planned.Fields);fields.Remove(key);
                    events[index]=CopyPlanned(planned,fields,planned.NamedPlan,planned.NamedBlock,planned.NamedRecordingIntent);
                    ViveTrialOpenRequest request;Check.True(!ViveTrialOpenRequest.TryCapture(participant,clock,out request)&&request==null);
                    fields=CopyFields(planned.Fields);fields[key]=key=="planned_stimulus_schema"?"2":
                        ((Colour)(((int)(key=="planned_word"?participant.CurrentTrial.Stimulus.Word:participant.CurrentTrial.Stimulus.Ink)+1)%4)).ToString();
                    events[index]=CopyPlanned(planned,fields,planned.NamedPlan,planned.NamedBlock,planned.NamedRecordingIntent);
                    Check.True(!ViveTrialOpenRequest.TryCapture(participant,clock,out request)&&request==null);
                }
                events[index]=planned;ViveTrialOpenRequest restored;
                Check.True(ViveTrialOpenRequest.TryCapture(participant,clock,out restored));
                Check.True(!restored.MayShowStimulus&&!restored.HasWriteReceipt&&!restored.HasFlushReceipt);
            });
            suite.Run("trial-open request refuses a duplicate plan event within its bounded window",()=>{
                var clock=new MonotonicClock("synthetic-open-duplicate-clock");var participant=Participant(clock);
                var experimentField=typeof(ViveDevelopmentParticipant).GetField("experiment",BindingFlags.Instance|BindingFlags.NonPublic);
                var eventsField=typeof(Experiment).GetField("events",BindingFlags.Instance|BindingFlags.NonPublic);
                Check.True(experimentField!=null&&eventsField!=null);
                var experiment=(Experiment)experimentField.GetValue(participant);var events=(List<JournalEvent>)eventsField.GetValue(experiment);
                JournalEvent planned=null;foreach(var item in events)if(item.Kind=="trial_planned")planned=item;
                Check.True(planned!=null);
                events.Add(new JournalEvent(events.Count,planned.Time,planned.ClockId,planned.SessionId,planned.BlockIndex,planned.Segment,
                    planned.TrialId,planned.Kind,CopyFields(planned.Fields),planned.NamedOption,planned.NamedPlan,planned.NamedBlock,planned.NamedRecordingIntent));
                ViveTrialOpenRequest request;Check.True(!ViveTrialOpenRequest.TryCapture(participant,clock,out request)&&request==null);
            });
            suite.Run("trial-open request binds the actual main block after normal practice",()=>{
                var clock=new MonotonicClock("synthetic-open-main-clock");var participant=Participant(clock);
                for(int i=0;i<8;i++)
                {
                    Trackpad(participant,false);var practice=participant.CurrentTrial;participant.MarkPresented(practice.TrialId);
                    Trackpad(participant,true,practice.Stimulus.Ink);Trackpad(participant,false);
                    if(participant.Phase==Phase.Practice&&Object.ReferenceEquals(participant.CurrentTrial,practice))
                        throw new InvalidOperationException("Synthetic practice did not advance at index "+i);
                }
                Check.Equal(Phase.BlockPreparation,participant.Phase);participant.OwnerConfirmNamedPlan(Actor.Owner,"synthetic-owner");
                participant.OwnerBeginBlock(Actor.Owner);Thread.Sleep(3100);participant.Tick();Trackpad(participant,false);
                Check.Equal(Phase.MainTask,participant.Phase);var trial=participant.CurrentTrial;ViveTrialOpenRequest request;
                Check.True(ViveTrialOpenRequest.TryCapture(participant,clock,out request));
                Check.Equal(trial.TrialId,request.Trial);Check.Equal(0,request.Block);Check.Equal(0,request.PlannedIndex);
                Check.True(!request.MayShowStimulus&&!request.HasFlushReceipt);Check.Equal("UNKNOWN",request.Validity);
            });
            suite.Run("trial-open request distinguishes a second practice attempt",()=>{
                var clock=new MonotonicClock("synthetic-open-retry-clock");var participant=Participant(clock);
                for(int i=0;i<8;i++)
                {
                    Trackpad(participant,false);var practice=participant.CurrentTrial;participant.MarkPresented(practice.TrialId);
                    var wrong=(Colour)(((int)practice.Stimulus.Ink+1)%4);Trackpad(participant,true,wrong);Trackpad(participant,false);
                    Check.Equal(Phase.PracticeCorrection,participant.Phase);
                    participant.UpdateAuxiliary(false,false,false,0,true);participant.UpdateAuxiliary(true,false,false,0,true);
                    participant.UpdateAuxiliary(false,false,false,0,true);
                }
                Check.Equal(Phase.PracticeRetry,participant.Phase);Trackpad(participant,false);participant.OwnerBeginPractice(Actor.Owner);
                ViveTrialOpenRequest request;Check.True(ViveTrialOpenRequest.TryCapture(participant,clock,out request));
                Check.Equal(2,request.PracticeAttempt);Check.Equal(participant.CurrentTrial.TrialId,request.Trial);
                Check.True(!request.MayShowStimulus&&!request.HasWriteReceipt);Check.Equal("UNKNOWN",request.Validity);
            });
        }
    }
}
