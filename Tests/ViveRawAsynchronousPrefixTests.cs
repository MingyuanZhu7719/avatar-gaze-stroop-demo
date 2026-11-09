using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic task events and in-memory codec only. These tests do not
    // establish that the writer, a headset, or a research session ran.
    public static class ViveRawAsynchronousPrefixTests
    {
        sealed class Clock : IClock
        {
            public double Now {get;set;}
            public string ClockId {get{return "prefix-clock";}}
        }

        static byte[][] ActualPracticePrefixes()
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"synthetic-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"prefix-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"prefix-intent",true,true);
            var clock=new Clock();
            var experiment=new Experiment(SessionConfiguration.NamedRecordingDevelopment("prefix-session",37,plan,intent),clock);
            experiment.BeginFamiliarization(Actor.Owner);
            experiment.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
            experiment.BeginPractice(Actor.Owner);
            string first=experiment.CurrentTrial.TrialId;
            experiment.MarkPresented(first);
            clock.Now=.2;
            Check.True(experiment.ButtonChanged(experiment.CurrentTrial.Stimulus.Ink,true));
            string second=experiment.CurrentTrial.TrialId;
            Check.True(first!=second);
            var manifest=new ViveNamedSessionFrameAdmission.Manifest("prefix-session",clock.ClockId,plan,intent,true);
            manifest.SealChoiceTime(0,-1);
            var snapshots=new byte[5][];
            using(var memory=new MemoryStream())
            {
                byte[] previous=new byte[32];long ordinal=0;
                Action<byte[],bool> add=(payload,magic)=>{byte[] digest,frame=ViveRawSessionJournalCodec.FrameV3(payload,previous,magic,out digest);memory.Write(frame,0,frame.Length);previous=digest;};
                add(ViveRawSessionJournalCodec.ManifestPayloadV3(manifest),true);
                foreach(var item in experiment.Events)
                {
                    add(ViveRawSessionJournalCodec.RecordPayload(++ordinal,item,manifest),false);
                    if(item.Kind=="block_plan_prepared")snapshots[0]=memory.ToArray();
                    if(item.TrialId==first&&item.Kind=="trial_planned")snapshots[1]=memory.ToArray();
                    if(item.TrialId==first&&item.Kind=="stimulus_presented")snapshots[2]=memory.ToArray();
                    if(item.TrialId==first&&item.Kind=="first_response")snapshots[3]=memory.ToArray();
                    if(item.TrialId==second&&item.Kind=="trial_planned")snapshots[4]=memory.ToArray();
                }
            }
            foreach(var snapshot in snapshots)Check.True(snapshot!=null,"missing synthetic boundary");
            return snapshots;
        }

        static ViveRawSessionReadResult Project(byte[] bytes)
        {using(var input=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,5000,CancellationToken.None);}
        static void Unknown(ViveRawTrialProjection projection)
        {
            Check.Equal("UNKNOWN",projection.WriterConfirmation);
            Check.Equal("UNKNOWN",projection.Coverage);
            Check.Equal("UNKNOWN",projection.CurrentWithdrawalStatus);
            Check.True(!projection.CompleteSessionProven&&!projection.SourceAuthenticated&&!projection.CurrentAccessAuthorized);
            foreach(var trial in projection.Trials)
            {Check.Equal("UNKNOWN",trial.Validity);Check.Equal("UNKNOWN",trial.OpenFlushEvidence);Check.Equal("UNKNOWN",trial.RenderProxyEvidence);Check.Equal("UNKNOWN",trial.ResponseCommitEvidence);}
        }

        public static void Register(TestSuite suite)
        {
            suite.Run("async VRAW exact event boundaries preserve only declarations and UNKNOWN",()=>{
                var snapshots=ActualPracticePrefixes();
                for(int i=0;i<snapshots.Length;i++)
                {
                    var result=Project(snapshots[i]);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.True(!result.CaptureTerminalObserved);
                    var projection=result.TrialProjection;Unknown(projection);Check.Equal(1,projection.PreparedPlans.Count);
                    foreach(var item in projection.PreparedPlans[0].Items)Check.Equal("UNKNOWN",item.Execution);
                    Check.Equal(i==0?0:i==4?2:1,projection.Trials.Count);
                    if(i>0)
                    {
                        var first=projection.Trials[0];Check.Equal(1,first.PlannedEvents);
                        Check.Equal(i>=2?1:0,first.PresentationEvents);Check.Equal(i>=3?1:0,first.ResponseEvents);
                        Check.Equal(i<3,first.IncompleteEvidence);Check.True(!first.EventConflict);
                    }
                    if(i==4){var second=projection.Trials[1];Check.Equal(1,second.PlannedEvents);Check.Equal(0,second.PresentationEvents);Check.True(second.IncompleteEvidence);}
                }
            });
            suite.Run("async VRAW truncated event frame does not invent the next declaration",()=>{
                var snapshots=ActualPracticePrefixes();
                for(int i=0;i<snapshots.Length-1;i++)
                {
                    // Keep all but the last byte of the next complete event.
                    var partial=new byte[snapshots[i+1].Length-1];Array.Copy(snapshots[i+1],partial,partial.Length);
                    var result=Project(partial);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                    Check.True(result.TrailingFrameTruncated&&!result.CaptureTerminalObserved);Unknown(result.TrialProjection);
                    var prior=Project(snapshots[i]).TrialProjection;
                    Check.Equal(prior.Trials.Count,result.TrialProjection.Trials.Count);
                    if(prior.Trials.Count>0){Check.Equal(prior.Trials[0].PresentationEvents,result.TrialProjection.Trials[0].PresentationEvents);Check.Equal(prior.Trials[0].ResponseEvents,result.TrialProjection.Trials[0].ResponseEvents);}
                }
            });
            suite.Run("async projection marks response and presentation reordering as conflict",()=>{
                var planned=new Dictionary<string,string>{{"planned_stimulus_schema","1"},{"planned_word","Red"},{"planned_ink","Green"}};
                var presented=new Dictionary<string,string>{{"word","Red"},{"ink","Green"}};
                var response=new Dictionary<string,string>{{"response","Green"},{"correct","True"}};
                var kinds=new[]{new[]{"stimulus_presented","trial_planned","first_response"},new[]{"first_response","trial_planned","stimulus_presented"},new[]{"trial_planned","first_response","stimulus_presented"}};
                foreach(var order in kinds)
                {
                    var accumulator=new ViveRawTrialAccumulator();
                    for(int i=0;i<order.Length;i++)accumulator.Observe(order[i],"trial",0,0,i,i,order[i]=="trial_planned"?planned:order[i]=="stimulus_presented"?presented:response);
                    var projection=accumulator.Finish();Unknown(projection);Check.Equal(1,projection.Trials.Count);Check.True(projection.Trials[0].EventConflict);
                }
            });
        }
    }
}
