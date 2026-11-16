#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.IO;
using System.Linq;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Real task state/events and the production VRAW4 codec; synthetic clock,
    // no device, no real recording admission and no physical timing claim.
    internal static class StudyVisualSearchRecordingFixture
    {
        sealed class Clock:IClock {public double Now {get;set;}public string ClockId {get{return "search-fixture-clock";}}}
        internal static byte[] FourBlocks(bool corruptPlan=false)
        {
            var allocation=new Allocation("search-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var configuration=SessionConfiguration.WithTaskKind(new SessionConfiguration("search-fixture-session",71,allocation,RunKind.Simulation,"en",5,0,true,0,true,true,true),TaskKind.VisualSearch);
            configuration=SessionConfiguration.LegacyRecordingDevelopment(configuration,
                ViveLegacyRecordingDesign.Select(Actor.Owner,"search-design",allocation,"search-geometry",ViveFixedTrackpadMapping.Version),
                ViveLegacyRecordingIntent.Select(Actor.Owner,"search-intent",true,true));
            var clock=new Clock{Now=3};var experiment=new Experiment(configuration,clock);
            experiment.RecordRawSoftwareChoices(clock.ClockId,1,2);
            experiment.BeginFamiliarization(Actor.Owner);experiment.PrepareRawRecordingBlockPlan(Actor.Owner,true);experiment.BeginPractice(Actor.Owner);
            while(experiment.Phase==Phase.Practice)
            {
                var trial=experiment.CurrentTrial;experiment.MarkPresented(trial.TrialId);
                Check.True(experiment.RecordVisualSearchCueEvidence(trial.TrialId,clock.Now-VisualSearchTrial.TargetPreviewSeconds,VisualSearchTrial.TargetPreviewSeconds));clock.Now+=.25;
                Check.True(experiment.RecordVisualSearchPointer(trial.TrialId,trial.VisualSearch.TargetId,"hover"));
                Check.True(experiment.SelectVisualSearchItem(trial.TrialId,trial.VisualSearch.TargetId));
            }
            experiment.LockBlock(Actor.Owner,ControlSelection.Automatic,"search-owner");
            for(int block=0;block<4;block++)
            {
                experiment.PrepareRawRecordingBlockPlan(Actor.Owner,false);
                if(block==0)experiment.BeginCountdown(Actor.Owner,true);else Check.True(experiment.TryBeginRetainedBlockCountdown(true));
                clock.Now+=5;experiment.Tick();
                while(experiment.Phase==Phase.MainTask)
                {
                    var trial=experiment.CurrentTrial;experiment.MarkPresented(trial.TrialId);
                    Check.True(experiment.RecordVisualSearchCueEvidence(trial.TrialId,clock.Now-VisualSearchTrial.TargetPreviewSeconds,VisualSearchTrial.TargetPreviewSeconds));clock.Now+=.25;
                    Check.True(experiment.RecordVisualSearchPointer(trial.TrialId,trial.VisualSearch.TargetId,"hover"));
                    if(block==0&&trial.PlannedIndex==0){clock.Now+=30;experiment.Tick();}
                    else
                    {
                        string selected=block==0&&trial.PlannedIndex==1?trial.VisualSearch.Items.First(x=>x.Id!=trial.VisualSearch.TargetId).Id:trial.VisualSearch.TargetId;
                        Check.True(experiment.SelectVisualSearchItem(trial.TrialId,selected));
                    }
                }
                Check.Equal(Phase.Questionnaire,experiment.Phase);Check.True(experiment.AcknowledgeQuestionnaireInstruction());
                for(int item=0;item<5;item++)experiment.Rate(3);
                if(block<3)Check.True(experiment.Ready());
            }
            Check.Equal(EndReason.Completed,experiment.EndReason.Value);
            var manifest=new ViveNamedSessionFrameAdmission.Manifest(ViveRawSessionContext.FromConfiguration(configuration),clock.ClockId,true,1,2);
            manifest.SealChoiceTime(3,-1);
            using(var output=new MemoryStream())
            {
                byte[] previous=new byte[32];long ordinal=0,tasks=0;
                Action<byte[],bool> append=(payload,first)=>{byte[] digest;var frame=ViveRawSessionJournalCodec.FrameV4(payload,previous,first,out digest);output.Write(frame,0,frame.Length);previous=digest;};
                append(ViveRawSessionJournalCodec.ManifestPayloadV4(manifest),true);
                foreach(var original in experiment.Events)
                {
                    var item=original;
                    if(corruptPlan&&item.Kind=="block_plan_prepared")
                    {
                        var fields=item.Fields.ToDictionary(x=>x.Key,x=>x.Value);
                        fields["item_00_search"]="forged:"+new string('0',64);
                        item=new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,item.TrialId,item.Kind,fields,null,
                            legacyRecordingDesign:item.LegacyRecordingDesign,legacyRecordingIntent:item.LegacyRecordingIntent);
                        corruptPlan=false;
                    }
                    append(ViveRawSessionJournalCodec.RecordPayload(++ordinal,item,manifest),false);tasks++;
                }
                append(ViveRawSessionJournalCodec.TerminalPayloadV3(ordinal+1,experiment.Events.Last().Sequence,tasks,0,0,tasks,clock.Now),false);
                return output.ToArray();
            }
        }
    }
}
#endif
