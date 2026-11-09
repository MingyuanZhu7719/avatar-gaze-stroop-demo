using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Actual Experiment planning and VRAW3 codec on synthetic in-memory data.
    public static class ViveRawPreparedPlanTests
    {
        sealed class Clock:IClock {public double Now {get;set;}public string ClockId {get{return "synthetic-clock";}}internal void Advance(double n){Now+=n;}}
        static byte[] Fixture(Func<JournalEvent,JournalEvent> alter=null,bool retry=false)
        {
            var a=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,2,"synthetic-geometry");
            var n=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-plan",a,n,a,n);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-intent",true,true);
            var clock=new Clock();var e=new Experiment(SessionConfiguration.NamedRecordingDevelopment("synthetic-session",37,plan,intent),clock);
            e.BeginFamiliarization(Actor.Owner);var first=e.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
            Check.True(Object.ReferenceEquals(first,e.PrepareNamedRecordingBlockPlan(Actor.Owner,true)));e.BeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {e.MarkPresented(e.CurrentTrial.TrialId);clock.Advance(.2);var ink=e.CurrentTrial.Stimulus.Ink;var answer=retry?(Colour)(((int)ink+1)%4):ink;
                Check.True(e.ButtonChanged(answer,true));e.ButtonChanged(answer,false);if(e.Phase==Phase.PracticeCorrection)e.ContinuePractice();}
            if(retry)
            {Check.Equal(Phase.PracticeRetry,e.Phase);var second=e.PrepareNamedRecordingBlockPlan(Actor.Owner,true);Check.True(!Object.ReferenceEquals(first,second));e.BeginPractice(Actor.Owner);
                for(int i=0;i<8;i++){e.MarkPresented(e.CurrentTrial.TrialId);clock.Advance(.2);var answer=e.CurrentTrial.Stimulus.Ink;Check.True(e.ButtonChanged(answer,true));e.ButtonChanged(answer,false);}}
            e.ConfirmNamedPlan(Actor.Owner,"synthetic-owner");
            for(int block=0;block<4;block++)
            {
                Check.Equal(Phase.BlockReady,e.Phase);var prepared=e.PrepareNamedRecordingBlockPlan(Actor.Owner,false);
                Check.True(Object.ReferenceEquals(prepared,e.PrepareNamedRecordingBlockPlan(Actor.Owner,false)));
                if(block==0)e.BeginCountdown(Actor.Owner,true);else Check.True(e.TryBeginRetainedBlockCountdown(true));clock.Advance(3);e.Tick();
                if(block==1){e.MarkPresented(e.CurrentTrial.TrialId);clock.Advance(.2);e.Pause(Actor.Participant);clock.Advance(.3);e.Resume(true);clock.Advance(3);e.Tick();}
                while(e.Phase==Phase.MainTask){e.MarkPresented(e.CurrentTrial.TrialId);clock.Advance(.2);var answer=e.CurrentTrial.Stimulus.Ink;Check.True(e.ButtonChanged(answer,true));e.ButtonChanged(answer,false);}
                Check.Equal(Phase.Questionnaire,e.Phase);Check.True(e.AcknowledgeQuestionnaireInstruction());for(int q=0;q<Questionnaire.Count;q++)e.Rate(q+1);
                if(block<3)Check.True(e.Ready());
            }
            var manifest=new ViveNamedSessionFrameAdmission.Manifest("synthetic-session",clock.ClockId,plan,intent,true);manifest.SealChoiceTime(0,0);
            using(var output=new MemoryStream())
            {
                byte[] previous=new byte[32];long ordinal=0,tasks=0;
                Action<byte[],bool> append=(payload,magic)=>{byte[] digest,frame=ViveRawSessionJournalCodec.FrameV3(payload,previous,magic,out digest);output.Write(frame,0,frame.Length);previous=digest;};
                append(ViveRawSessionJournalCodec.ManifestPayloadV3(manifest),true);
                for(int i=1;i<e.Events.Count;i++){var item=alter==null?e.Events[i]:alter(e.Events[i]);ordinal++;append(ViveRawSessionJournalCodec.RecordPayload(ordinal,item,manifest),false);tasks++;}
                append(ViveRawSessionJournalCodec.TerminalPayloadV3(ordinal+1,e.Events[e.Events.Count-1].Sequence,tasks,0,0,tasks,clock.Now),false);return output.ToArray();
            }
        }
        static ViveRawSessionReadResult Project(byte[] bytes)
        {using(var stream=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.ProjectTrials(stream,true,true,4194304,5000,CancellationToken.None);}
        static byte[] ThroughFirstPlan(byte[] input,bool incomplete)
        {
            byte[] needle=Encoding.UTF8.GetBytes("block_plan_prepared");int position=8;
            while(position<input.Length)
            {
                int length=ContinuousJournal.Int(input,position),payload=position+8;bool found=false;
                for(int i=payload;i<=payload+length-needle.Length&&!found;i++)
                {bool same=true;for(int j=0;j<needle.Length;j++)if(input[i+j]!=needle[j]){same=false;break;}found=same;}
                int end=payload+length+32;
                if(found){int target=incomplete?end-1:end;byte[] prefix=new byte[target];Buffer.BlockCopy(input,0,prefix,0,target);return prefix;}
                position=end;
            }
            throw new InvalidOperationException("Synthetic plan frame missing.");
        }
        static JournalEvent Rewrite(JournalEvent item,Action<Dictionary<string,string>> edit)
        {
            var fields=CopyFields(item);edit(fields);
            return new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,item.TrialId,item.Kind,fields,item.NamedOption,item.NamedPlan,item.NamedBlock,item.NamedRecordingIntent);
        }
        static Dictionary<string,string> CopyFields(JournalEvent item)
        {var result=new Dictionary<string,string>();foreach(var pair in item.Fields)result.Add(pair.Key,pair.Value);return result;}
        public static void Register(TestSuite suite)
        {
            suite.Run("VRAW block plans project exact preparation declarations without inventing execution or confirmation",()=>{
                var r=Project(Fixture());Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,r.Status);Check.True(r.CaptureTerminalObserved);
                var p=r.TrialProjection;Check.Equal(5,p.PreparedPlans.Count);Check.Equal(0,p.UnsupportedPreparedPlanEvents);Check.Equal("UNKNOWN",p.WriterConfirmation);
                int items=0,practice=0,main=0;foreach(var plan in p.PreparedPlans)
                {Check.Equal("UNKNOWN",plan.WriterConfirmation);Check.True(!plan.SourceAuthenticated);if(plan.Scope=="PRACTICE"){practice++;Check.Equal(1,plan.Attempt);Check.Equal(8,plan.Items.Count);}
                    else{main++;Check.Equal(0,plan.Attempt);Check.Equal(24,plan.Items.Count);if(plan.Block==1)Check.Equal(0,plan.SegmentAtDeclaration);}
                    for(int i=0;i<plan.Items.Count;i++){Check.Equal(i,plan.Items[i].Index);Check.Equal("UNKNOWN",plan.Items[i].Execution);items++;}}
                Check.Equal(1,practice);Check.Equal(4,main);Check.Equal(104,items);Check.Equal(104,p.Trials.Count);Check.Equal(20,p.Ratings.Count);
                ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(r,true,out report));Check.Equal(105,report.PreparedStimuliCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries).Length);
                Check.True(report.ManifestCsv.Contains("writer_confirmation,state:unknown\n")&&report.ManifestCsv.Contains("tail_coverage,state:unknown\n"));
            });
            suite.Run("VRAW practice retry has separate declared plan; resumed segment is not invented",()=>{
                var r=Project(Fixture(retry:true));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,r.Status);Check.Equal(6,r.TrialProjection.PreparedPlans.Count);
                Check.Equal(1,r.TrialProjection.PreparedPlans[0].Attempt);Check.Equal(2,r.TrialProjection.PreparedPlans[1].Attempt);
                Check.True(r.TrialProjection.PreparedPlans[0].List!=r.TrialProjection.PreparedPlans[1].List);
                foreach(var plan in r.TrialProjection.PreparedPlans)if(plan.Scope=="MAIN"&&plan.Block==1)Check.Equal(0,plan.SegmentAtDeclaration);
                bool actualSegment=false;foreach(var trial in r.TrialProjection.Trials)if(trial.Block==1&&trial.Segment==1)actualSegment=true;
                Check.True(actualSegment);Check.True(!r.TrialProjection.CompleteSessionProven);
            });
            suite.Run("VRAW prior practice with missing list leaves later attempt ordering UNKNOWN",()=>{
                bool changed=false;var bytes=Fixture(item=>{if(!changed&&item.BlockIndex==-1&&item.Kind=="trial_planned")
                    {changed=true;return Rewrite(item,f=>f.Remove("list"));}return item;},retry:true);
                Check.True(changed);var r=Project(bytes);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,r.Status);
                Check.Equal(6,r.TrialProjection.PreparedPlans.Count);Check.Equal("UNKNOWN",r.TrialProjection.PreparedPlans[1].PriorOrderState);
                Check.Equal("UNKNOWN",r.TrialProjection.PreparedPlans[1].WriterConfirmation);Check.True(!r.TrialProjection.CompleteSessionProven);
                ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(r,true,out report));bool found=false;
                foreach(string line in report.PreparedStimuliCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries))
                    if(line.StartsWith("b64:UFJBQ1RJQ0U=,i32:-1,i32:0,i32:2,",StringComparison.Ordinal))
                    {found=true;Check.True(line.EndsWith(",state:unknown,state:unknown,state:unknown",StringComparison.Ordinal));}
                Check.True(found);
            });
            suite.Run("VRAW unidentifiable prior practice remains UNKNOWN even if list looks canonical",()=>{
                foreach(bool missingId in new[]{true,false})
                {
                    bool changed=false;var bytes=Fixture(item=>{
                        if(!changed&&item.BlockIndex==-1&&item.Kind=="trial_planned")
                        {
                            changed=true;var fields=CopyFields(item);if(!missingId)fields.Remove("planned_index");
                            return new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,
                                missingId?null:"not-a-canonical-trial-id",item.Kind,fields,item.NamedOption,item.NamedPlan,item.NamedBlock,item.NamedRecordingIntent);
                        }
                        return item;
                    },retry:true);
                    Check.True(changed);var result=Project(bytes);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                    Check.Equal("UNKNOWN",result.TrialProjection.PreparedPlans[1].PriorOrderState);
                    Check.Equal("UNKNOWN",result.TrialProjection.PreparedPlans[1].WriterConfirmation);Check.True(!result.TrialProjection.CompleteSessionProven);
                }
            });
            suite.Run("VRAW known practice trial ID is bound to its own attempt plan and list",()=>{
                bool changed=false;var mismatch=Fixture(item=>{if(!changed&&item.BlockIndex==-1&&item.Kind=="trial_planned"&&item.TrialId.Contains("/p2/"))
                    {changed=true;return Rewrite(item,f=>{var list=f["list"];f["list"]=list.Substring(0,list.Length-1)+"1";});}return item;},retry:true);
                Check.True(changed);Check.Equal(ViveRawSessionReadStatus.Invalid,Project(mismatch).Status);
                JournalEvent saved=null;changed=false;
                var beforePlan=Fixture(item=>{
                    string scope,attempt;
                    if(saved==null&&item.Kind=="block_plan_prepared"&&item.Fields.TryGetValue("plan_scope",out scope)&&scope=="PRACTICE"&&
                        item.Fields.TryGetValue("attempt",out attempt)&&attempt=="2")
                    {
                        saved=item;var fields=CopyFields(item);fields.Remove("list");fields["planned_index"]="0";
                        fields["planned_stimulus_schema"]="1";fields["planned_word"]=fields["item_00_word"];fields["planned_ink"]=fields["item_00_ink"];
                        return new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,item.SessionId+"/p2/t0","trial_planned",fields,item.NamedOption,item.NamedPlan,item.NamedBlock,item.NamedRecordingIntent);
                    }
                    if(saved!=null&&!changed&&item.BlockIndex==-1&&item.Kind=="trial_planned"&&item.TrialId.Contains("/p2/"))
                    {changed=true;return new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,null,"block_plan_prepared",CopyFields(saved),item.NamedOption,item.NamedPlan,item.NamedBlock,item.NamedRecordingIntent);}
                    return item;
                },retry:true);
                Check.True(changed);Check.Equal(ViveRawSessionReadStatus.Invalid,Project(beforePlan).Status);
            });
            suite.Run("VRAW prepared plan rejects malformed or inconsistent declarations even when hashes are rebuilt",()=>{
                bool changed=false;var missing=Fixture(item=>{string scope;if(!changed&&item.Kind=="block_plan_prepared"&&item.Fields.TryGetValue("plan_scope",out scope)&&scope=="MAIN")
                    {changed=true;return Rewrite(item,f=>f.Remove("item_00_word"));}return item;});
                Check.Equal(ViveRawSessionReadStatus.Invalid,Project(missing).Status);
                changed=false;var mismatch=Fixture(item=>{string scope;if(!changed&&item.Kind=="block_plan_prepared"&&item.Fields.TryGetValue("plan_scope",out scope)&&scope=="MAIN")
                    {changed=true;return Rewrite(item,f=>f["item_00_word"]=f["item_00_word"]=="Red"?"Blue":"Red");}return item;});
                Check.Equal(ViveRawSessionReadStatus.Invalid,Project(mismatch).Status);
            });
            suite.Run("VRAW plan rejects wrong list binding segment and trial identity with rebuilt hashes",()=>{
                var badList=Fixture(item=>item.BlockIndex==0&&(item.Kind=="block_plan_prepared"||item.Kind=="trial_planned")?
                    Rewrite(item,f=>f["list"]=f["list"].Replace("main-v1-2-","main-v1-1-")):item);
                Check.Equal(ViveRawSessionReadStatus.Invalid,Project(badList).Status);
                bool changed=false;var badSegment=Fixture(item=>{if(!changed&&item.BlockIndex==0&&item.Kind=="block_plan_prepared")
                    {changed=true;return new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,1,item.TrialId,item.Kind,CopyFields(item),item.NamedOption,item.NamedPlan,item.NamedBlock,item.NamedRecordingIntent);}return item;});
                Check.Equal(ViveRawSessionReadStatus.Invalid,Project(badSegment).Status);
                changed=false;var badId=Fixture(item=>{if(!changed&&item.BlockIndex==0&&item.Kind=="trial_planned")
                    {changed=true;return new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,item.SessionId+"/b0/t23",item.Kind,CopyFields(item),item.NamedOption,item.NamedPlan,item.NamedBlock,item.NamedRecordingIntent);}return item;});
                Check.Equal(ViveRawSessionReadStatus.Invalid,Project(badId).Status);
                changed=false;var extra=Fixture(item=>{if(!changed&&item.Kind=="block_plan_prepared")
                    {changed=true;return Rewrite(item,f=>f.Add("item_99_word","Red"));}return item;});
                Check.Equal(ViveRawSessionReadStatus.Invalid,Project(extra).Status);
            });
            suite.Run("VRAW late plan in the same prefix is not misclassified as a prior plan",()=>{
                JournalEvent saved=null;bool changed=false;
                var bytes=Fixture(item=>{
                    if(saved==null&&item.BlockIndex==0&&item.Kind=="block_plan_prepared")
                    {
                        saved=item;var fields=CopyFields(item);
                        fields["planned_index"]="0";fields["planned_stimulus_schema"]="1";fields["planned_word"]=fields["item_00_word"];fields["planned_ink"]=fields["item_00_ink"];
                        return new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,item.SessionId+"/b0/t0","trial_planned",fields,item.NamedOption,item.NamedPlan,item.NamedBlock,item.NamedRecordingIntent);
                    }
                    if(saved!=null&&!changed&&item.BlockIndex==0&&item.Kind=="trial_planned")
                    {changed=true;return new JournalEvent(item.Sequence,item.Time,item.ClockId,item.SessionId,item.BlockIndex,item.Segment,null,"block_plan_prepared",CopyFields(saved),item.NamedOption,item.NamedPlan,item.NamedBlock,item.NamedRecordingIntent);}
                    return item;
                });
                Check.True(changed);Check.Equal(ViveRawSessionReadStatus.Invalid,Project(bytes).Status);
            });
            suite.Run("VRAW unsupported plan schema remains unknown without decoding items",()=>{
                bool changed=false;var future=Fixture(item=>{if(!changed&&item.Kind=="block_plan_prepared"){changed=true;return Rewrite(item,f=>f["recording_plan_schema"]="2");}return item;});
                var result=Project(future);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);Check.Equal(1,result.TrialProjection.UnsupportedPreparedPlanEvents);
                Check.Equal(4,result.TrialProjection.PreparedPlans.Count);Check.True(!result.TrialProjection.CompleteSessionProven);
            });
            suite.Run("VRAW planned prefix never invents trials; incomplete plan frame leaves all outcomes unknown",()=>{
                byte[] full=Fixture();var planned=Project(ThroughFirstPlan(full,false));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,planned.Status);
                Check.True(!planned.TrailingFrameTruncated&&!planned.CaptureTerminalObserved&&!planned.CompleteSessionProven);
                Check.Equal(1,planned.TrialProjection.PreparedPlans.Count);Check.Equal(8,planned.TrialProjection.PreparedPlans[0].Items.Count);Check.Equal(0,planned.TrialProjection.Trials.Count);
                foreach(var item in planned.TrialProjection.PreparedPlans[0].Items)Check.Equal("UNKNOWN",item.Execution);
                var partial=Project(ThroughFirstPlan(full,true));Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,partial.Status);Check.True(partial.TrailingFrameTruncated);
                Check.Equal(0,partial.TrialProjection.PreparedPlans.Count);Check.Equal(0,partial.TrialProjection.Trials.Count);Check.Equal("UNKNOWN",partial.TrialProjection.WriterConfirmation);
            });
        }
    }
}
