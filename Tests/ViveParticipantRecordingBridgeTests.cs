using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveParticipantRecordingBridgeTests
    {
        sealed class Clock:IClock {public double Now {get;set;} public string ClockId {get;set;}="participant-clock";}
        sealed class Provider:IViveProEyeProvider {public ViveProEyePoll Next;public int Calls;public ViveProEyePoll Poll(){Calls++;return Next;}}
        static ViveDevelopmentProvenance Identity()
        {return new ViveDevelopmentProvenance("session","participant","operator",ViveDevelopmentMeasurementRole.Participant,
            "participant/vive/epoch","participant-device","participant-clock",ViveProEyeRehearsalCoordinates.MappingVersion,
            null,0,0,0,null,true);}
        static ViveEyeRay Ray(Vec3 direction,bool valid=true,ulong mask=2)
        {return new ViveEyeRay(mask,true,valid,new Vec3(1,2,3),direction);}
        static ViveProEyePoll Frame(int sequence,double time,Vec3 direction,bool nativeValid=true,
            ViveEyeCalibrationState calibration=ViveEyeCalibrationState.ReportedReady)
        {
            return new ViveProEyePoll(new ViveHeadFrame(sequence,time,true,true,Rotation.Identity),
                new ViveEyeFrame(sequence,-100+sequence,time,false,null,null,Ray(direction,nativeValid)),
                ViveEyeRuntimeState.Working,calibration,true,0);
        }
        static ViveParticipantEyeSource Source(Clock clock,Provider provider,int block=1,int segment=2,string session="session")
        {return new ViveParticipantEyeSource(session,block,segment,"participant/vive/epoch","participant-device",
            "operator/vive/epoch","operator-device",clock,provider,new ViveProEyeRehearsalCoordinates());}
        static ViveParticipantRecordingBridge Create(Clock clock,Provider provider,MemoryStream memory,
            Action flush=null,bool eye=true,bool task=true,int sourceBlock=1,int sourceSegment=2,string sourceSession="session")
        {return new ViveParticipantRecordingBridge(Source(clock,provider,sourceBlock,sourceSegment,sourceSession),clock,Identity(),
            ViveDevelopmentDataSource.SyntheticFixture,1,2,memory,flush??(()=>{}),eye,task);}
        static ViveDevelopmentParticipant TaskInFirstBlock(Clock clock,bool completeCountdown=true)
        {
            var allocation=new Allocation("fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var configuration=new SessionConfiguration("session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var task=new ViveDevelopmentParticipant(configuration,clock,DevelopmentFixtures.Geometry(),map,.5,true);
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));task.OwnerBeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {
                var trial=task.CurrentTrial;task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));task.MarkPresented(trial.TrialId);
                var ink=trial.Stimulus.Ink;
                double x=ink==Colour.Green?1:ink==Colour.Yellow?-1:0,y=ink==Colour.Red?1:ink==Colour.Blue?-1:0;
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
            }
            task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"owner");task.OwnerBeginBlock(Actor.Owner);
            if(completeCountdown){clock.Now+=3;task.Tick();}
            return task;
        }
        static List<JournalEvent> FirstEpochEvents(ViveDevelopmentParticipant task)
        {
            var result=new List<JournalEvent>();foreach(var item in task.ReadEventsAfter(-1,4096))
                if(item.BlockIndex==0&&item.Segment==0)result.Add(item);return result;
        }
        static ViveDevelopmentJournalReadResult Read(MemoryStream memory,List<ViveDevelopmentJournalRecord> rows=null)
        {return ViveDevelopmentJournalReader.Read(new MemoryStream(memory.ToArray()),rows==null?(Action<ViveDevelopmentJournalRecord>)null:rows.Add);}

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE recorder detects bypassed successful or failed source reads before polling or closing",()=>{
                foreach(bool missing in new[]{false,true})
                {
                    var clock=new Clock{Now=1};var provider=new Provider{Next=missing?null:Frame(1,1,new Vec3(0,0,1))};
                    var source=Source(clock,provider);var memory=new MemoryStream();
                    var bridge=new ViveParticipantRecordingBridge(source,clock,Identity(),ViveDevelopmentDataSource.SyntheticFixture,1,2,memory,()=>{},true,true);
                    ViveParticipantEyeObservation ignored;source.TryRead(out ignored);int calls=provider.Calls;
                    Check.Equal(ViveParticipantRecordingPoll.Faulted,bridge.Poll());Check.Equal(calls,provider.Calls);
                    Check.True(bridge.IsFaulted&&!bridge.Close());Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(memory).Status);
                }
                var c=new Clock{Now=1};var p=new Provider{Next=Frame(1,1,new Vec3(0,0,1))};var s=Source(c,p);var m=new MemoryStream();
                var b=new ViveParticipantRecordingBridge(s,c,Identity(),ViveDevelopmentDataSource.SyntheticFixture,1,2,m,()=>{},true,true);
                ViveParticipantEyeObservation o;Check.True(s.TryRead(out o));Check.True(!b.Close()&&b.IsFaulted);
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(m).Status);
            });
            suite.Run("VIVE recorder faults permanently on source read counter exhaustion",()=>{
                var c=new Clock{Now=1};var p=new Provider{Next=Frame(1,1,new Vec3(0,0,1))};var s=Source(c,p);
                var field=typeof(ViveParticipantEyeSource).GetField("readAttemptGeneration",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                field.SetValue(s,Int64.MaxValue-1);var m=new MemoryStream();
                var b=new ViveParticipantRecordingBridge(s,c,Identity(),ViveDevelopmentDataSource.SyntheticFixture,1,2,m,()=>{},true,true);
                Check.Equal(ViveParticipantRecordingPoll.Faulted,b.Poll());Check.Equal(0,p.Calls);Check.True(b.IsFaulted&&!b.Close());
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(m).Status);
                Check.Throws<InvalidOperationException>(()=>new ViveParticipantRecordingBridge(s,c,Identity(),ViveDevelopmentDataSource.SyntheticFixture,1,2,new MemoryStream(),()=>{},true,true));
            });
            suite.Run("VIVE participant recorder requires both explicit opt-ins before writing or polling",()=>{
                var clock=new Clock{Now=1};var provider=new Provider();
                foreach(var opts in new[]{Tuple.Create(false,false),Tuple.Create(false,true),Tuple.Create(true,false)})
                {var memory=new MemoryStream();Check.Throws<InvalidOperationException>(()=>Create(clock,provider,memory,eye:opts.Item1,task:opts.Item2));Check.Equal(0L,memory.Length);Check.Equal(0,provider.Calls);}
            });
            suite.Run("VIVE participant recorder preserves raw derived clocks task event and explicit gap",()=>{
                var clock=new Clock{Now=1};var provider=new Provider{Next=Frame(1,1,new Vec3(1.2,0,1.6))};var memory=new MemoryStream();
                var bridge=Create(clock,provider,memory);Check.Equal(0,provider.Calls);Check.True(!bridge.IsFaulted);
                Check.Equal(ViveParticipantRecordingPoll.Recorded,bridge.Poll());
                Check.Equal(ViveParticipantRecordingPoll.NoNewEyeFrame,bridge.Poll());Check.Equal(1L,bridge.ObservationsWritten);
                Check.True(bridge.RecordTaskEvent(ViveDevelopmentTaskEventKind.StimulusPresented,1,1,2,"MainTask","trial-1","synthetic"));
                clock.Now=2;Check.True(bridge.RecordGap(1.2,1.8,ViveDevelopmentGapReason.DeviceUnavailable));
                provider.Next=Frame(2,1.95,new Vec3(0,0,1),calibration:ViveEyeCalibrationState.Required);
                Check.Equal(ViveParticipantRecordingPoll.RecordedUnavailable,bridge.Poll());Check.True(bridge.Flush());Check.True(bridge.Close());
                Check.Equal(ViveParticipantRecordingPoll.Stopped,bridge.Poll());
                Check.True(!bridge.HardwareCaptureTimeVerified&&!bridge.ApparatusMappingValidated&&!bridge.ContinuousCoverageEstablished);
                var rows=new List<ViveDevelopmentJournalRecord>();var result=Read(memory,rows);Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,result.Status);
                Check.Equal("observation",rows[1].Kind);Check.Equal("1.2",rows[1].Fields["raw_combined_sdk_direction_x"]);
                Check.Equal("-99",rows[1].Fields["native_eye_timestamp_opaque"]);Check.Equal("true",rows[1].Fields["native_combined_direction_reported_valid"]);
                Check.Equal("true",rows[1].Fields["eye_usable"]);Check.Equal("unity-tracking-local-rehearsal-unvalidated",rows[1].Fields["derived_direction_frame"]);
                Check.Equal("task_event",rows[2].Kind);Check.Equal("trial-1",rows[2].Fields["trial_id"]);
                Check.Equal("gap",rows[3].Kind);Check.Equal("DeviceUnavailable",rows[3].Fields["gap_reason"]);
                Check.Equal("false",rows[4].Fields["eye_usable"]);Check.Equal(null,rows[4].Fields["derived_direction_x"]);
                Check.Equal(2L,bridge.ObservationsWritten);Check.Equal(1L,bridge.GapsWritten);Check.Equal(1L,bridge.TaskEventsWritten);
            });
            suite.Run("VIVE participant recorder rejects source context and clock mismatches before writing",()=>{
                var clock=new Clock{Now=1};var provider=new Provider{Next=Frame(1,1,new Vec3(0,0,1))};var memory=new MemoryStream();
                Check.Throws<ArgumentException>(()=>Create(clock,provider,memory,sourceBlock:0));Check.Equal(0L,memory.Length);
                var otherClock=new Clock{Now=1};var source=Source(clock,provider);
                Check.Throws<ArgumentException>(()=>new ViveParticipantRecordingBridge(source,otherClock,Identity(),
                    ViveDevelopmentDataSource.SyntheticFixture,1,2,memory,()=>{},true,true));Check.Equal(0L,memory.Length);
            });
            suite.Run("VIVE participant recorder rejects gaps without known prior observation",()=>{
                var clock=new Clock{Now=2};var provider=new Provider();var memory=new MemoryStream();var bridge=Create(clock,provider,memory);
                Check.True(!bridge.RecordGap(1,2,ViveDevelopmentGapReason.Missing));Check.True(bridge.IsFaulted&&!bridge.Close());
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(memory).Status);
            });
            suite.Run("VIVE participant recorder latches rollback and malformed authoritative events",()=>{
                var clock=new Clock{Now=2};var provider=new Provider();var memory=new MemoryStream();var bridge=Create(clock,provider,memory);
                Check.True(!bridge.RecordTaskEvent(ViveDevelopmentTaskEventKind.Paused,2.1,1,2,"Paused",null,null));
                Check.True(bridge.IsFaulted&&!bridge.Close());
                clock=new Clock{Now=2};provider=new Provider();memory=new MemoryStream();bridge=Create(clock,provider,memory);clock.Now=1;
                Check.Equal(ViveParticipantRecordingPoll.Faulted,bridge.Poll());Check.True(bridge.IsFaulted&&!bridge.Close());
            });
            suite.Run("VIVE participant recorder flush failure is sticky and cannot claim clean completion",()=>{
                var clock=new Clock{Now=1};var provider=new Provider{Next=Frame(1,1,new Vec3(0,0,1))};var memory=new MemoryStream();int flushes=0;
                var bridge=Create(clock,provider,memory,()=>{if(++flushes>1)throw new IOException("synthetic");});
                Check.Equal(ViveParticipantRecordingPoll.Recorded,bridge.Poll());Check.True(!bridge.Flush());Check.True(bridge.IsFaulted);
                Check.Equal(0L,bridge.LastFlushAcknowledgedSequence);Check.True(!bridge.Close());
                Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(memory).Status);
            });
            suite.Run("VIVE participant recorder does not turn old samples into new evidence after a gap",()=>{
                var clock=new Clock{Now=1};var provider=new Provider{Next=Frame(1,1,new Vec3(0,0,1))};var memory=new MemoryStream();var bridge=Create(clock,provider,memory);
                Check.Equal(ViveParticipantRecordingPoll.Recorded,bridge.Poll());clock.Now=2;Check.True(bridge.RecordGap(1.2,1.98,ViveDevelopmentGapReason.Stale));
                provider.Next=Frame(2,1.95,new Vec3(0,0,1));Check.Equal(ViveParticipantRecordingPoll.Faulted,bridge.Poll());
                Check.True(bridge.IsFaulted&&!bridge.Close());Check.Equal(1L,bridge.ObservationsWritten);
            });
            suite.Run("VIVE participant recorder distinguishes duplicate source failure and missing eye without inventing gaps",()=>{
                var clock=new Clock{Now=1};var provider=new Provider{Next=Frame(1,1,new Vec3(0,0,1))};var memory=new MemoryStream();var bridge=Create(clock,provider,memory);
                Check.Equal(ViveParticipantRecordingPoll.Recorded,bridge.Poll());
                Check.Equal(ViveParticipantRecordingPoll.NoNewEyeFrame,bridge.Poll());Check.Equal(ViveParticipantReadStatus.Duplicate,bridge.LastSourceStatus);
                provider.Next=new ViveProEyePoll(null,null,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                Check.Equal(ViveParticipantRecordingPoll.SourceUnavailable,bridge.Poll());Check.Equal(ViveParticipantReadStatus.Missing,bridge.LastSourceStatus);
                Check.Equal(0L,bridge.GapsWritten);provider.Next=null;
                Check.Equal(ViveParticipantRecordingPoll.Faulted,bridge.Poll());Check.Equal(ViveParticipantReadStatus.ProviderFailure,bridge.LastSourceStatus);
                Check.True(bridge.IsFaulted&&!bridge.Close());Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(memory).Status);
            });
            suite.Run("VIVE participant recorder fails closed when journal v1 cannot represent a missing combined ray",()=>{
                var clock=new Clock{Now=1};var eye=new ViveEyeFrame(1,1,1,false,null,null,null);
                var provider=new Provider{Next=new ViveProEyePoll(new ViveHeadFrame(1,1,true,true,Rotation.Identity),eye,
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0)};
                var memory=new MemoryStream();var bridge=Create(clock,provider,memory);
                Check.Equal(ViveParticipantRecordingPoll.Faulted,bridge.Poll());Check.Equal(0L,bridge.ObservationsWritten);
                Check.True(bridge.IsFaulted&&!bridge.Close());Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(memory).Status);
            });
            suite.Run("VIVE participant recorder reports close flush failure despite structural close marker",()=>{
                var clock=new Clock{Now=1};var provider=new Provider();var memory=new MemoryStream();int count=0;
                var bridge=Create(clock,provider,memory,()=>{if(++count>1)throw new IOException("synthetic");});
                Check.True(!bridge.Close());Check.True(bridge.IsFaulted);Check.Equal(0L,bridge.LastFlushAcknowledgedSequence);
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(memory).Status);
            });
            suite.Run("VIVE recorder preserves authoritative task sequence fields and labels unknown phase honestly",()=>{
                var clock=new Clock{Now=1};var provider=new Provider();var memory=new MemoryStream();var bridge=Create(clock,provider,memory);
                Check.True(bridge.BeginAuthoritativeEvents(8,true,ViveDevelopmentTaskOrigin.SimulationDevelopment));
                var fields=new Dictionary<string,string>{{"item","value;three"},{"nullable",null},{"response","Green"}};
                var item=new JournalEvent(8,1,"participant-clock","session",1,2,"trial-1","rating",fields);
                fields["response"]="changed after event creation";
                Check.True(bridge.RecordAuthoritativeTaskEvent(item));
                var next=new JournalEvent(9,1,"participant-clock","session",1,2,null,"phase_changed",new Dictionary<string,string>{{"to","Rest"}});
                Check.True(bridge.RecordAuthoritativeTaskEvent(next));Check.True(bridge.Close());
                var rows=new List<ViveDevelopmentJournalRecord>();Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(memory,rows).Status);
                Check.Equal("AuthoritativeEvent",rows[1].Fields["event_kind"]);Check.Equal("UNRESOLVED",rows[1].Fields["phase"]);
                string detail=rows[1].Fields["detail"];ViveAuthoritativeTaskPayload payload;
                Check.True(ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(detail,out payload));
                Check.Equal(ViveDevelopmentTaskOrigin.SimulationDevelopment,payload.TaskOrigin);Check.Equal(8L,payload.Sequence);
                Check.Equal("rating",payload.Kind);Check.Equal(3,payload.Fields.Count);Check.Equal("value;three",payload.Fields["item"]);
                Check.Equal(null,payload.Fields["nullable"]);Check.Equal("Green",payload.Fields["response"]);
                Check.True(!ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(detail.Replace("SIMULATION_DEVELOPMENT","APPARATUS"),out payload));
                Check.True(!ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(detail.Replace("SIMULATION_DEVELOPMENT;", ""),out payload));
                Check.True(!ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(detail+";extra",out payload));
                Check.Equal("trial-1",rows[1].Fields["trial_id"]);Check.Equal(2L,bridge.TaskEventsWritten);
            });
            suite.Run("VIVE recorder latches event gaps wrong clock wrong segment and oversized details",()=>{
                foreach(int scenario in new[]{0,1,2,3})
                {
                    var clock=new Clock{Now=1};var memory=new MemoryStream();var bridge=Create(clock,new Provider(),memory);Check.True(bridge.BeginAuthoritativeEvents(8,true,ViveDevelopmentTaskOrigin.SimulationDevelopment));
                    var fields=new Dictionary<string,string>{{"detail",scenario==3?new string('x',1200):"ok"}};
                    var item=new JournalEvent(scenario==0?9:8,1,scenario==1?"other-clock":"participant-clock","session",scenario==2?0:1,2,null,"rating",fields);
                    Check.True(!bridge.RecordAuthoritativeTaskEvent(item));Check.True(bridge.IsFaulted&&!bridge.Close());Check.Equal(0L,bridge.TaskEventsWritten);
                    Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(memory).Status);
                }
            });
            suite.Run("VIVE recorder cannot silently mix manual and authoritative task events",()=>{
                var clock=new Clock{Now=1};var bridge=Create(clock,new Provider(),new MemoryStream());
                Check.True(bridge.BeginAuthoritativeEvents(0,true,ViveDevelopmentTaskOrigin.SimulationDevelopment));
                Check.True(!bridge.RecordTaskEvent(ViveDevelopmentTaskEventKind.Paused,1,1,2,"Paused",null,null));Check.True(bridge.IsFaulted);
            });
            suite.Run("VIVE real-eye recording requires the bound task and same clock instance before writing",()=>{
                var clock=new Clock{Now=1};var provider=new Provider();var memory=new MemoryStream();
                Check.Throws<ArgumentException>(()=>new ViveParticipantRecordingBridge(Source(clock,provider,0,0),clock,Identity(),ViveDevelopmentDataSource.RealVive,0,0,memory,()=>{},true,true));
                Check.Equal(0L,memory.Length);var otherClock=new Clock{Now=1};var task=TaskInFirstBlock(otherClock);
                Check.Throws<ArgumentException>(()=>new ViveParticipantRecordingBridge(Source(clock,provider,0,0),clock,Identity(),ViveDevelopmentDataSource.RealVive,0,0,memory,()=>{},true,true,task));
                Check.Equal(0L,memory.Length);
            });
            suite.Run("VIVE real-eye label preserves bound simulated task and detects unchained tampering",()=>{
                // No physical data: the fixture exercises the real-source label in memory only.
                var clock=new Clock{Now=1};var task=TaskInFirstBlock(clock);var provider=new Provider();var memory=new MemoryStream();
                var bridge=new ViveParticipantRecordingBridge(Source(clock,provider,0,0),clock,Identity(),ViveDevelopmentDataSource.RealVive,0,0,memory,()=>{},true,true,task);
                var initialCoverage=bridge.CoverageSnapshot();Check.Equal("ONE_BLOCK_SEGMENT_ONLY",initialCoverage.Scope);
                Check.True(!initialCoverage.CleanCloseAcknowledged&&!initialCoverage.CompleteSessionProven&&!initialCoverage.PracticeCovered);
                var events=FirstEpochEvents(task);Check.True(events.Count>0);
                Check.True(bridge.BeginAuthoritativeEvents(events[0].Sequence,true,ViveDevelopmentTaskOrigin.SimulationDevelopment));
                foreach(var item in events)Check.True(bridge.RecordAuthoritativeTaskEvent(item));
                task.EndTechnical(Actor.Owner,"synthetic end");var after=FirstEpochEvents(task);
                Check.True(!bridge.ContextOpen);Check.Equal(ViveParticipantRecordingPoll.ContextClosed,bridge.Poll());Check.Equal(0,provider.Calls);
                Check.True(!bridge.RecordGap(1,2,ViveDevelopmentGapReason.Missing));Check.True(!bridge.IsFaulted);
                for(int i=events.Count;i<after.Count;i++)Check.True(bridge.RecordAuthoritativeTaskEvent(after[i]));
                Check.True(bridge.Close());var coverage=bridge.CoverageSnapshot();
                Check.True(coverage.CleanCloseAcknowledged&&coverage.KnownSegmentEventsReconciledAtClose);
                Check.Equal(ViveDevelopmentDataSource.RealVive,coverage.EyeSource);
                Check.Equal((ViveDevelopmentTaskOrigin?)ViveDevelopmentTaskOrigin.SimulationDevelopment,coverage.TaskOrigin);
                Check.Equal((long?)events[0].Sequence,coverage.FirstTaskSequence);Check.Equal((long?)after[after.Count-1].Sequence,coverage.LastTaskSequence);
                Check.True(!coverage.PracticeCovered&&!coverage.EventsOutsideSegmentCovered&&!coverage.OtherSegmentsCovered&&
                    !coverage.ContinuousCoverageProven&&!coverage.CompleteSessionProven&&!coverage.ResearchExportAllowed&&!coverage.HardwareDurabilityProven);
                Check.True(!coverage.RecordsLeftRightRays&&!coverage.RecordsNativeOrigins&&!coverage.RecordsHeadPose);
                Check.True(!initialCoverage.CleanCloseAcknowledged);var rows=new List<ViveDevelopmentJournalRecord>();
                Check.Equal(ViveDevelopmentJournalReadStatus.CleanClose,Read(memory,rows).Status);Check.Equal("RealVive",rows[0].Fields["data_source"]);
                ViveAuthoritativeTaskPayload decoded;Check.True(ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(rows[1].Fields["detail"],out decoded));
                Check.Equal(ViveDevelopmentTaskOrigin.SimulationDevelopment,decoded.TaskOrigin);
                var tampered=memory.ToArray();string raw=Encoding.ASCII.GetString(tampered);int marker=raw.IndexOf("SIMULATION_DEVELOPMENT",StringComparison.Ordinal);
                Check.True(marker>=0);tampered[marker]=(byte)'X';Check.Equal(ViveDevelopmentJournalReadStatus.Invalid,ViveDevelopmentJournalReader.Read(new MemoryStream(tampered)).Status);
                var other=Create(clock,new Provider(),new MemoryStream());Check.True(!other.BeginAuthoritativeEvents(0,true,(ViveDevelopmentTaskOrigin)99));Check.True(other.IsFaulted);
                var freshTask=TaskInFirstBlock(clock);
                var realManual=new ViveParticipantRecordingBridge(Source(clock,provider,0,0),clock,Identity(),ViveDevelopmentDataSource.RealVive,0,0,new MemoryStream(),()=>{},true,true,freshTask);
                Check.True(!realManual.RecordTaskEvent(ViveDevelopmentTaskEventKind.Paused,clock.Now,0,0,"Paused",null,null));Check.True(realManual.IsFaulted);
            });
            suite.Run("VIVE real-eye segment rejects omitted start premature close and fabricated task event",()=>{
                foreach(int scenario in new[]{0,1,2,3})
                {
                    var clock=new Clock{Now=1};var task=TaskInFirstBlock(clock);var events=FirstEpochEvents(task);var memory=new MemoryStream();
                    var bridge=new ViveParticipantRecordingBridge(Source(clock,new Provider(),0,0),clock,Identity(),ViveDevelopmentDataSource.RealVive,0,0,memory,()=>{},true,true,task);
                    if(scenario==0){Check.True(!bridge.Close());}
                    else if(scenario==1){Check.True(!bridge.BeginAuthoritativeEvents(events[0].Sequence+1,true,ViveDevelopmentTaskOrigin.SimulationDevelopment));}
                    else
                    {
                        Check.True(bridge.BeginAuthoritativeEvents(events[0].Sequence,true,ViveDevelopmentTaskOrigin.SimulationDevelopment));
                        if(scenario==2){foreach(var item in events)Check.True(bridge.RecordAuthoritativeTaskEvent(item));Check.True(!bridge.Close());}
                        else
                        {
                            var item=events[0];var changed=new Dictionary<string,string>();foreach(var field in item.Fields)changed.Add(field.Key,field.Value);changed["fabricated"]="yes";
                            Check.True(!bridge.RecordAuthoritativeTaskEvent(new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,item.TrialId,item.Kind,changed)));
                        }
                    }
                    Check.True(bridge.IsFaulted);Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(memory).Status);
                }
            });
            suite.Run("VIVE real-eye segment rejects a queued observation from before the actual segment boundary",()=>{
                var clock=new Clock{Now=1};var task=TaskInFirstBlock(clock,false);clock.Now=1.01;
                var provider=new Provider{Next=Frame(1,.99,new Vec3(0,0,1))};var memory=new MemoryStream();
                var bridge=new ViveParticipantRecordingBridge(Source(clock,provider,0,0),clock,Identity(),ViveDevelopmentDataSource.RealVive,0,0,memory,()=>{},true,true,task);
                Check.Equal(ViveParticipantRecordingPoll.Faulted,bridge.Poll());Check.True(bridge.IsFaulted);Check.Equal(0L,bridge.ObservationsWritten);
                Check.True(!bridge.Close());Check.Equal(ViveDevelopmentJournalReadStatus.UncleanEnd,Read(memory).Status);
            });
        }
    }
}
