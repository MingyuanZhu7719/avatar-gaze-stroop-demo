using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Simulation-only differential checks; no runtime, device or file is opened.
    public static class VivePracticeEyeFencedDifferentialTests
    {
        sealed class Clock:IClock
        {
            public double Time=10;public Action Hook;
            public string ClockId {get{return "synthetic-diff-clock";}}
            public double Now {get{var h=Hook;Hook=null;if(h!=null)h();return Time;}}
        }
        sealed class Provider:IViveProEyeProvider
        {
            public ViveProEyePoll Next;public Action Hook;public bool Throw;public int Calls;
            public ViveProEyePoll Poll(){Calls++;var h=Hook;Hook=null;if(h!=null)h();if(Throw)throw new InvalidOperationException("synthetic");return Next;}
        }
        sealed class Mapper:IViveProEyeCoordinateMapper
        {
            readonly ViveProEyeRehearsalCoordinates inner=new ViveProEyeRehearsalCoordinates();public Action Hook;public string OverrideVersion;public int Calls;
            public string Version {get{return OverrideVersion??inner.Version;}}
            public bool TryMapHead(Rotation raw,out Rotation mapped){Calls++;return inner.TryMapHead(raw,out mapped);}
            public bool TryMapEye(Rotation raw,Vec3 direction,out Vec3 mapped){Calls++;var h=Hook;Hook=null;if(h!=null)h();return inner.TryMapEye(raw,direction,out mapped);}
        }
        static ViveDevelopmentParticipant Task(Clock clock,string session)
        {
            var allocation=new Allocation("synthetic-diff-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var config=new SessionConfiguration(session,42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"synthetic-diff-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var task=new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));return task;
        }
        static ViveProEyePoll Poll(int sequence,double eyeTime,long headSequence,double headTime,bool headValid=true,
            ViveEyeRuntimeState runtime=ViveEyeRuntimeState.Working,ViveEyeCalibrationState calibration=ViveEyeCalibrationState.ReportedReady,
            bool connected=true,int? code=0,Vec3? direction=null)
        {
            var ray=new ViveEyeRay(2,true,true,new Vec3(1,2,3),direction??new Vec3(1.2,0,1.6));
            var eye=new ViveEyeFrame(sequence,sequence*10,eyeTime,false,ray,ray,ray);
            var head=new ViveHeadFrame(headSequence,headTime,true,headValid,Rotation.Identity);
            return new ViveProEyePoll(head,eye,runtime,calibration,connected,code,2);
        }
        static ViveProEyePoll NoHead(ViveProEyePoll p)
        {return new ViveProEyePoll(null,p.Eye,p.Runtime,p.Calibration,p.EyeDeviceConnected,p.NativeResultCode,p.NativeRuntimeStatus);}
        static VivePracticeContextV3 Context(ViveDevelopmentParticipant task,IViveProEyeCoordinateMapper mapper,double start,
            string session=null,int attempt=1,string clock=null,string mapping=null)
        {return new VivePracticeContextV3(session??task.SessionId,"synthetic-participant","synthetic-stream","synthetic-device",clock??"synthetic-diff-clock",
            mapping??mapper.Version,attempt,"synthetic-epoch",0,null,VivePracticeEpochReasonV3.Initial,start);}
        sealed class Pair:IDisposable
        {
            public readonly Clock AClock=new Clock(),BClock=new Clock();
            public readonly Provider AProvider=new Provider(),BProvider=new Provider();
            public readonly IViveProEyeCoordinateMapper AMapper,BMapper;
            public readonly ViveDevelopmentParticipant ATask,BTask;
            public readonly VivePracticeEyeSource A;public readonly VivePracticeEyeFencedSource B;
            public Pair(bool customMapper=false)
            {
                AMapper=customMapper?(IViveProEyeCoordinateMapper)new Mapper():new ViveProEyeRehearsalCoordinates();
                BMapper=customMapper?(IViveProEyeCoordinateMapper)new Mapper():new ViveProEyeRehearsalCoordinates();
                ATask=Task(AClock,"synthetic-diff-session");BTask=Task(BClock,"synthetic-diff-session");
                A=VivePracticeEyeSource.CreateSynthetic(ATask,AClock,new ViveExclusiveObservationProvider(AProvider,AClock).Acquire(),AMapper,.25,true,true);
                B=VivePracticeEyeFencedSource.CreateSynthetic(BTask,BClock,new ViveExclusiveObservationProvider(BProvider,BClock).Acquire(),BMapper,.25,true,true);
            }
            public void At(double time){AClock.Time=BClock.Time=time;}
            public void Frames(ViveProEyePoll p){AProvider.Next=BProvider.Next=p;}
            public void Start()
            {At(10.2);ATask.OwnerBeginPractice(Actor.Owner);BTask.OwnerBeginPractice(Actor.Owner);Check.True(A.BindPractice());Check.True(B.BindPractice(Context(BTask,BMapper,10.2)));}
            public void CompareRead(bool expected)
            {
                VivePracticeEyeCandidate x,y;bool a=A.TryRead(out x),b=B.TryRead(out y);Check.Equal(expected,a);Check.Equal(a,b);Check.Equal(A.Status,B.Status);
                Check.Equal(A.RecoveryDecisionRequired,B.RecoveryDecisionRequired);
                if(!a){Check.True(x==null&&y==null);return;}
                Check.True(x!=null&&y!=null&&!x.CanWrite&&!y.CanWrite);Check.Equal(x.PracticeAttempt,y.PracticeAttempt);
                Check.Equal(x.PracticeStartSequence,y.PracticeStartSequence);Check.Equal(x.DerivedAvailable,y.DerivedAvailable);Check.Equal(x.HeadUsable,y.HeadUsable);
                Check.Equal(x.RawPoll.Head==null,y.RawPoll.Head==null);Check.Equal(x.RawPoll.Eye.FrameSequence,y.RawPoll.Eye.FrameSequence);
                Check.Equal(x.RawPoll.Eye.Timestamp,y.RawPoll.Eye.Timestamp);Check.Equal(x.RawPoll.Eye.ObservedLocalSeconds,y.RawPoll.Eye.ObservedLocalSeconds);
                Check.Equal(x.RawPoll.Eye.Combined.ValidityMask,y.RawPoll.Eye.Combined.ValidityMask);
                Check.Equal(x.RawPoll.Eye.Combined.Direction.X,y.RawPoll.Eye.Combined.Direction.X);
                if(x.DerivedAvailable){Check.Near(x.MappedDirection.Value.X,y.MappedDirection.Value.X);Check.Near(x.MappedDirection.Value.Z,y.MappedDirection.Value.Z);}
                Check.True(x.Context==null&&y.Context!=null&&y.Context.SessionId==BTask.SessionId&&y.Context.RecordingEpochId=="synthetic-epoch");
                Check.True(x.UsesClock(AClock)&&y.UsesClock(BClock)&&!y.UsesClock(AClock));Check.Near(x.MaximumAgeSeconds,y.MaximumAgeSeconds);
            }
            public void Dispose(){A.Dispose();B.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("practice fenced source matches reference readiness and strictly newer gaze",()=>{
                using(var f=new Pair())
                {
                    f.At(10.11);f.Frames(Poll(1,10.1,1,10.1));Check.True(f.A.PollReadiness());Check.True(f.B.PollReadiness());f.Start();
                    f.At(10.21);f.Frames(Poll(1,10.1,2,10.21));f.CompareRead(false);
                    f.Frames(Poll(2,10.2,3,10.21));f.CompareRead(false);
                    f.At(10.23);f.Frames(Poll(3,10.22,4,10.23));f.CompareRead(true);
                }
            });
            suite.Run("practice fenced source matches reference raw only and returns later derived",()=>{
                using(var f=new Pair())
                {
                    f.Start();f.At(10.31);f.Frames(NoHead(Poll(1,10.3,1,10.3)));f.CompareRead(true);
                    f.At(10.33);f.Frames(Poll(2,10.32,2,10.32));f.CompareRead(true);
                }
            });
            suite.Run("practice fenced source matches reference pre-epoch head sanitization",()=>{
                using(var f=new Pair())
                {f.Start();f.At(10.31);f.Frames(Poll(1,10.3,1,10.2));f.CompareRead(true);}
            });
            suite.Run("practice fenced source matches reference duplicate and ordered failure",()=>{
                using(var f=new Pair())
                {
                    f.Start();f.At(10.31);f.Frames(Poll(2,10.3,1,10.3));f.CompareRead(true);
                    f.At(10.32);f.Frames(Poll(2,10.3,2,10.32));f.CompareRead(false);
                    f.At(10.33);f.Frames(Poll(1,10.29,3,10.33));f.CompareRead(false);Check.True(f.B.RecoveryDecisionRequired);
                }
            });
            suite.Run("practice fenced source matches reference runtime and native failures",()=>{
                foreach(int i in new[]{0,1,2,3})using(var f=new Pair())
                {
                    f.Start();f.At(10.31);f.Frames(i==0?Poll(1,10.3,1,10.3,runtime:ViveEyeRuntimeState.Unknown):
                        i==1?Poll(1,10.3,1,10.3,calibration:ViveEyeCalibrationState.Required):
                        i==2?Poll(1,10.3,1,10.3,connected:false):Poll(1,10.3,1,10.3,code:null));
                    f.CompareRead(false);Check.True(f.B.RecoveryDecisionRequired);
                }
            });
            suite.Run("practice fenced source matches reference task pause reentrancy",()=>{
                using(var f=new Pair())
                {f.Start();f.At(10.31);f.Frames(Poll(1,10.3,1,10.3));f.AProvider.Hook=()=>f.ATask.ParticipantPause();f.BProvider.Hook=()=>f.BTask.ParticipantPause();
                    f.CompareRead(false);Check.True(f.B.RecoveryDecisionRequired);}
            });
            suite.Run("practice fenced source invalidates proof for no-op and safety callbacks",()=>{
                foreach(bool safety in new[]{false,true})using(var f=new Pair())
                {f.Start();f.At(10.31);f.Frames(Poll(1,10.3,1,10.3));
                    f.BProvider.Hook=safety?(Action)(()=>f.BTask.ParticipantPause()):(()=>f.BTask.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));
                    VivePracticeEyeCandidate value;Check.True(!f.B.TryRead(out value)&&value==null&&f.B.RecoveryDecisionRequired);}
            });
            suite.Run("practice fenced source rejects mismatched declared contexts",()=>{
                foreach(int i in new[]{0,1,2,3,4})using(var f=new Pair())
                {
                    f.At(10.2);f.BTask.OwnerBeginPractice(Actor.Owner);
                    var context=Context(f.BTask,f.BMapper,i==4?10.19:10.2,i==0?"other-session":null,i==1?2:1,i==2?"other-clock":null,i==3?"other-mapping":null);
                    Check.True(!f.B.BindPractice(context)&&f.B.RecoveryDecisionRequired&&!f.B.CanWrite);
                }
            });
            suite.Run("practice fenced source rejects callback exception and backwards clock",()=>{
                using(var f=new Pair())
                {f.Start();f.At(10.31);f.AProvider.Throw=f.BProvider.Throw=true;f.CompareRead(false);Check.True(f.B.RecoveryDecisionRequired);}
                using(var f=new Pair())
                {f.Start();f.At(10.1);f.Frames(Poll(1,10.09,1,10.09));f.CompareRead(false);Check.True(f.B.RecoveryDecisionRequired);}
            });
            suite.Run("practice fenced source releases proof on duplicate and after close",()=>{
                using(var f=new Pair())
                {f.Start();f.At(10.31);f.Frames(Poll(1,10.3,1,10.3));f.CompareRead(true);
                    f.At(10.32);f.Frames(Poll(1,10.3,2,10.32));f.CompareRead(false);
                    f.At(10.33);f.Frames(Poll(2,10.32,3,10.33));f.CompareRead(true);f.B.Dispose();
                    VivePracticeEyeCandidate candidate;Check.True(!f.B.TryRead(out candidate)&&candidate==null);}
            });
            suite.Run("practice fenced source matches reference missing and unusable raw gaze",()=>{
                using(var f=new Pair())
                {f.Start();f.At(10.31);f.Frames(null);f.CompareRead(false);Check.True(f.B.RecoveryDecisionRequired);}
                using(var f=new Pair())
                {f.Start();f.At(10.31);f.Frames(Poll(1,10.3,1,10.3,direction:new Vec3(0,0,0)));f.CompareRead(false);Check.True(f.B.RecoveryDecisionRequired);}
            });
            suite.Run("practice fenced source matches reference clock callback mutation and stale eye",()=>{
                using(var f=new Pair())
                {f.Start();f.At(10.31);f.Frames(Poll(1,10.3,1,10.3));
                    f.AProvider.Hook=()=>f.AClock.Hook=()=>f.ATask.ParticipantPause();f.BProvider.Hook=()=>f.BClock.Hook=()=>f.BTask.ParticipantPause();
                    f.CompareRead(false);Check.True(f.B.RecoveryDecisionRequired);}
                using(var f=new Pair())
                {f.Start();f.At(10.31);f.Frames(Poll(1,10.3,1,10.3));
                    f.AProvider.Hook=()=>f.AClock.Hook=()=>f.AClock.Time=10.7;f.BProvider.Hook=()=>f.BClock.Hook=()=>f.BClock.Time=10.7;
                    f.CompareRead(false);Check.True(f.B.RecoveryDecisionRequired);}
            });
            suite.Run("practice fenced source rejects cross-thread and preserves source failure",()=>{
                using(var f=new Pair())
                {f.Start();bool accepted=true;var thread=new Thread(()=>{VivePracticeEyeCandidate value;accepted=f.B.TryRead(out value);});
                    thread.Start();Check.True(thread.Join(5000)&&!accepted&&f.B.RecoveryDecisionRequired);f.At(10.31);f.Frames(Poll(1,10.3,1,10.3));
                    VivePracticeEyeCandidate candidate;Check.True(!f.B.TryRead(out candidate)&&candidate==null);Check.Equal(0,f.BProvider.Calls);}
            });
            suite.Run("practice fenced source rejects missing consent and releases cooperative lease",()=>{
                foreach(bool eye in new[]{false,true})foreach(bool taskChoice in new[]{false,true})
                {
                    if(eye&&taskChoice)continue;var clock=new Clock();var task=Task(clock,"synthetic-diff-consent");var provider=new Provider();
                    var broker=new ViveExclusiveObservationProvider(provider,clock);var lease=broker.Acquire();bool threw=false;
                    try{VivePracticeEyeFencedSource.CreateSynthetic(task,clock,lease,new Mapper(),.25,eye,taskChoice);}catch(InvalidOperationException){threw=true;}
                    Check.True(threw);Check.Equal(0,provider.Calls);using(var next=broker.Acquire()){}
                }
            });
            suite.Run("practice fenced source requires enabled observation and actual clock instance",()=>{
                var clock=new Clock();var allocation=new Allocation("synthetic-plain",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var config=new SessionConfiguration("synthetic-plain-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"synthetic-plain-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                var plain=new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);var provider=new Provider();var broker=new ViveExclusiveObservationProvider(provider,clock);
                bool disabled=false;try{VivePracticeEyeFencedSource.CreateSynthetic(plain,clock,broker.Acquire(),new Mapper(),.25,true,true);}catch(InvalidOperationException){disabled=true;}
                Check.True(disabled);using(var next=broker.Acquire()){}
                var observed=Task(clock,"synthetic-other-clock");var sameLabel=new Clock();bool mismatch=false;
                try{VivePracticeEyeFencedSource.CreateSynthetic(observed,sameLabel,broker.Acquire(),new Mapper(),.25,true,true);}catch(ArgumentException){mismatch=true;}
                Check.True(mismatch);using(var next=broker.Acquire()){}
            });
            suite.Run("practice fenced source abandons proof after provider failure",()=>{
                using(var f=new Pair())
                {f.Start();f.At(10.31);f.BProvider.Throw=true;VivePracticeEyeCandidate candidate;
                    Check.True(!f.B.TryRead(out candidate)&&candidate==null&&f.B.RecoveryDecisionRequired);
                    ViveDevelopmentParticipant.PracticeObservationFence token;Check.True(f.BTask.TryAcquirePracticeObservationFence(f.BClock,out token));
                    f.BTask.AbandonPracticeObservationFence(token);}
            });
            suite.Run("practice fenced source rejects unverified retry and recovery contexts",()=>{
                using(var f=new Pair())
                {
                    f.At(10.2);f.BTask.OwnerBeginPractice(Actor.Owner);
                    var recovery=new VivePracticeContextV3(f.BTask.SessionId,"synthetic-participant","synthetic-stream","synthetic-device",
                        f.BClock.ClockId,f.BMapper.Version,1,"synthetic-recovery",1,"synthetic-prior",VivePracticeEpochReasonV3.ResumedAfterInterruption,10.2);
                    Check.True(!f.B.BindPractice(recovery)&&f.B.RecoveryDecisionRequired&&!f.B.CanWrite);
                }
                using(var f=new Pair())
                {
                    f.At(10.2);f.BTask.OwnerBeginPractice(Actor.Owner);
                    var retry=new VivePracticeContextV3(f.BTask.SessionId,"synthetic-participant","synthetic-stream","synthetic-device",
                        f.BClock.ClockId,f.BMapper.Version,2,"synthetic-retry",0,"synthetic-prior",VivePracticeEpochReasonV3.PracticeRetry,10.2);
                    Check.True(!f.B.BindPractice(retry)&&f.B.RecoveryDecisionRequired&&!f.B.CanWrite);
                }
                using(var f=new Pair())
                {
                    f.At(10.2);f.BTask.OwnerBeginPractice(Actor.Owner);
                    var field=typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                    Check.True(field!=null);var experiment=(Experiment)field.GetValue(f.BTask);
                    for(int i=0;i<8;i++)
                    {
                        var trial=experiment.CurrentTrial;experiment.MarkPresented(trial.TrialId);
                        var wrong=(Colour)(((int)trial.Stimulus.Ink+1)%4);experiment.ButtonChanged(wrong,true);experiment.ButtonChanged(wrong,false);
                        if(experiment.Phase==Phase.PracticeCorrection)experiment.ContinuePractice();
                    }
                    Check.Equal(Phase.PracticeRetry,experiment.Phase);
                    for(int i=0;i<8&&!f.BTask.SynchronizeObservation();i++){}
                    Check.True(!f.BTask.ObservationBlocked);f.At(10.4);f.BTask.OwnerBeginPractice(Actor.Owner);
                    Check.Equal(2,f.BTask.CurrentTrial.PracticeAttempt);
                    Check.True(!f.B.BindPractice(Context(f.BTask,f.BMapper,10.4,attempt:2))&&f.B.RecoveryDecisionRequired&&!f.B.CanWrite);
                }
            });
            suite.Run("practice fenced source rejects initial bind after same-attempt pause and resume",()=>{
                using(var f=new Pair())
                {
                    f.At(10.2);f.BTask.OwnerBeginPractice(Actor.Owner);var first=f.BTask.CurrentTrial;
                    f.At(10.21);f.BTask.ParticipantPause();Check.Equal(Phase.Paused,f.BTask.Phase);Check.Equal(TrialOutcome.Interrupted,first.Outcome);
                    var field=typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                    Check.True(field!=null);var experiment=(Experiment)field.GetValue(f.BTask);
                    f.At(10.22);experiment.Resume(true);
                    for(int i=0;i<8&&!f.BTask.SynchronizeObservation();i++){}
                    Check.True(!f.BTask.ObservationBlocked);Check.Equal(Phase.Practice,f.BTask.Phase);
                    Check.Equal(1,f.BTask.CurrentTrial.PracticeAttempt);Check.True(f.BTask.CurrentTrial.TrialId!=first.TrialId);
                    Check.True(!f.B.BindPractice(Context(f.BTask,f.BMapper,10.2))&&f.B.RecoveryDecisionRequired&&!f.B.CanWrite);
                }
            });
            suite.Run("practice fenced source rejects unseen pause resume within the same attempt",()=>{
                using(var f=new Pair())
                {
                    f.Start();f.At(10.31);f.BProvider.Next=Poll(1,10.3,1,10.3);VivePracticeEyeCandidate candidate;
                    Check.True(f.B.TryRead(out candidate)&&candidate!=null);var first=f.BTask.CurrentTrial;
                    f.At(10.32);f.BTask.ParticipantPause();Check.Equal(TrialOutcome.Interrupted,first.Outcome);
                    var field=typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                    Check.True(field!=null);var experiment=(Experiment)field.GetValue(f.BTask);
                    f.At(10.33);experiment.Resume(true);
                    for(int i=0;i<8&&!f.BTask.SynchronizeObservation();i++){}
                    Check.True(!f.BTask.ObservationBlocked);Check.Equal(Phase.Practice,f.BTask.Phase);
                    Check.Equal(1,f.BTask.CurrentTrial.PracticeAttempt);Check.True(f.BTask.CurrentTrial.TrialId!=first.TrialId);
                    f.At(10.35);f.BProvider.Next=Poll(2,10.34,2,10.34);
                    Check.True(!f.B.TryRead(out candidate)&&candidate==null&&f.B.RecoveryDecisionRequired&&!f.B.CanWrite);
                }
            });
            suite.Run("practice fenced source permits ordinary correction and next trial within an attempt",()=>{
                using(var f=new Pair())
                {
                    f.Start();f.At(10.31);f.Frames(Poll(1,10.3,1,10.3));f.CompareRead(true);
                    var field=typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                    Check.True(field!=null);var first=(Experiment)field.GetValue(f.ATask);var second=(Experiment)field.GetValue(f.BTask);
                    foreach(var experiment in new[]{first,second})
                    {
                        var trial=experiment.CurrentTrial;experiment.MarkPresented(trial.TrialId);
                        var wrong=(Colour)(((int)trial.Stimulus.Ink+1)%4);experiment.ButtonChanged(wrong,true);experiment.ButtonChanged(wrong,false);
                        Check.Equal(Phase.PracticeCorrection,experiment.Phase);
                    }
                    for(int i=0;i<8&&!f.ATask.SynchronizeObservation();i++){}
                    for(int i=0;i<8&&!f.BTask.SynchronizeObservation();i++){}
                    Check.True(!f.ATask.ObservationBlocked&&!f.BTask.ObservationBlocked);
                    f.At(10.33);f.Frames(Poll(2,10.32,2,10.32));f.CompareRead(true);
                    first.ContinuePractice();second.ContinuePractice();
                    for(int i=0;i<8&&!f.ATask.SynchronizeObservation();i++){}
                    for(int i=0;i<8&&!f.BTask.SynchronizeObservation();i++){}
                    Check.Equal(Phase.Practice,first.Phase);Check.Equal(Phase.Practice,second.Phase);
                    f.At(10.35);f.Frames(Poll(3,10.34,3,10.34));f.CompareRead(true);
                }
            });
            suite.Run("practice fenced source rejects coverage gap crossed within provider or clock callback",()=>{
                foreach(bool duringClock in new[]{false,true})using(var f=new Pair())
                {
                    f.Start();f.At(10.31);f.BProvider.Next=Poll(1,10.3,1,10.3);VivePracticeEyeCandidate candidate;
                    Check.True(f.B.TryRead(out candidate)&&candidate!=null);
                    f.At(10.50);
                    if(duringClock)
                    {f.BProvider.Next=Poll(2,10.49,2,10.49);f.BProvider.Hook=()=>f.BClock.Hook=()=>f.BClock.Time=10.56;}
                    else
                    {f.BProvider.Next=Poll(2,10.55,2,10.55);f.BProvider.Hook=()=>f.BClock.Time=10.56;}
                    Check.True(!f.B.TryRead(out candidate)&&candidate==null&&f.B.RecoveryDecisionRequired);
                    Check.Equal("eye_coverage_gap",f.B.LastIssue);
                }
            });
            suite.Run("practice fenced source matches reference invalid and regressed head with valid raw",()=>{
                using(var f=new Pair())
                {
                    f.Start();f.At(10.31);f.Frames(Poll(1,10.3,1,10.3,headValid:false));f.CompareRead(true);
                    f.At(10.33);f.Frames(Poll(2,10.32,2,10.1));f.CompareRead(true);
                    Check.Equal(f.A.LastHeadDisposition,f.B.LastHeadDisposition);Check.True(!f.B.LastHeadUsable&&!f.B.LastDerivedDirectionUsable);
                }
            });
            suite.Run("practice fenced source detects device faults on duplicate eye frame",()=>{
                foreach(int i in new[]{0,1,2,3})using(var f=new Pair())
                {
                    f.Start();f.At(10.31);f.Frames(Poll(1,10.3,1,10.3));f.CompareRead(true);f.At(10.32);
                    f.Frames(i==0?Poll(1,10.3,2,10.32,runtime:ViveEyeRuntimeState.Unknown):
                        i==1?Poll(1,10.3,2,10.32,calibration:ViveEyeCalibrationState.Required):
                        i==2?Poll(1,10.3,2,10.32,connected:false):Poll(1,10.3,2,10.32,code:null));
                    f.CompareRead(false);Check.True(f.B.RecoveryDecisionRequired);
                }
            });
            suite.Run("practice fenced source never calls an untrusted mapper and returns raw only",()=>{
                using(var f=new Pair(customMapper:true))
                {
                    f.At(10.11);f.Frames(Poll(1,10.1,1,10.1));
                    Check.True(!f.A.PollReadiness()&&!f.B.PollReadiness());
                    Check.Equal(0,((Mapper)f.AMapper).Calls);Check.Equal(0,((Mapper)f.BMapper).Calls);
                }
                using(var f=new Pair(customMapper:true))
                {
                    f.Start();f.At(10.31);f.Frames(Poll(1,10.3,1,10.3));
                    var a=(Mapper)f.AMapper;var b=(Mapper)f.BMapper;
                    a.Hook=()=>a.OverrideVersion="synthetic-changed-mapping";
                    b.Hook=()=>b.OverrideVersion="synthetic-changed-mapping";
                    VivePracticeEyeCandidate original,fenced;Check.True(f.A.TryRead(out original));Check.True(f.B.TryRead(out fenced));
                    Check.Equal(0,a.Calls);Check.Equal(0,b.Calls);Check.True(a.Hook!=null&&b.Hook!=null);
                    Check.True(original!=null&&fenced!=null&&original.HeadUsable&&fenced.HeadUsable);
                    Check.True(!original.DerivedAvailable&&!fenced.DerivedAvailable&&!original.MappedDirection.HasValue&&!fenced.MappedDirection.HasValue);
                    Check.True(original.RawPoll.Eye!=null&&fenced.RawPoll.Eye!=null&&!original.CanWrite&&!fenced.CanWrite);
                    Check.Equal(VivePracticeEyeStatus.AcceptedRawOnlyCandidate,f.A.Status);Check.Equal(f.A.Status,f.B.Status);
                }
            });
        }
    }
}
