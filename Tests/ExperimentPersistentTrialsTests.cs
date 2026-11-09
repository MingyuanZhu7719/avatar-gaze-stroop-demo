using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class ExperimentPersistentTrialsTests
    {
        const string Session="persistent-synthetic";
        static ExperimentMutationTrialSnapshot Trial(int index,int version=0,string session=Session,string trial=null,Colour ink=Colour.Blue)
        {
            object key=new object();var record=new TrialRecord(key,trial??"trial-"+index,session,"list-"+(index/24),false,0,index/24,index%24,0,
                (Condition)(index%4),new Stimulus(Colour.Red,ink));
            if(version>0)record.SetOnset(key,index+version*.01);
            if(version%3==2){record.SetResponseAt(key,index+version*.01+.2);record.SetResponse(key,ink);record.SetCorrect(key,true);record.SetResponded(key);}
            if(version%3==1)record.SetTechnicalIssue(key);
            return new ExperimentMutationTrialSnapshot(record);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("experiment persistent trials match copied reference through randomized immutable versions",()=>{
                var budget=new ExperimentPersistentMutationBudget(64L*1024*1024);var vector=ExperimentPersistentTrials.Create(Session,128,budget);
                var expected=new List<ExperimentMutationTrialSnapshot>();var saved=new List<Tuple<ExperimentPersistentTrials,ExperimentMutationTrialSnapshot[]>>();
                for(int i=0;i<104;i++){var value=Trial(i);ExperimentPersistentTrials next;Check.True(vector.TryAppend(value,out next));vector=next;expected.Add(value);}
                var random=new Random(36251);
                for(int i=0;i<1500;i++)
                {
                    int index=random.Next(expected.Count);var value=Trial(index,random.Next(1,100000));ExperimentPersistentTrials next;
                    Check.True(vector.TryReplace(index,value,out next));vector=next;expected[index]=value;
                    if(i%250==0)saved.Add(Tuple.Create(vector,expected.ToArray()));
                    for(int n=0;n<4;n++){int probe=random.Next(expected.Count);ExperimentMutationTrialSnapshot actual;Check.True(vector.TryGet(probe,out actual)&&actual.Equals(expected[probe]));}
                }
                foreach(var version in saved)for(int i=0;i<version.Item2.Length;i++){ExperimentMutationTrialSnapshot actual;Check.True(version.Item1.TryGet(i,out actual)&&actual.Equals(version.Item2[i]));}
                Check.Equal(104,vector.Count);Check.True(!vector.Terminal&&!vector.CanEnterCountdown&&!vector.CanResume);
            });
            suite.Run("experiment persistent trials share untouched chunks and keep old values",()=>{
                var vector=ExperimentPersistentTrials.Create(Session,128,new ExperimentPersistentMutationBudget(4L*1024*1024));
                for(int i=0;i<70;i++){ExperimentPersistentTrials next;Check.True(vector.TryAppend(Trial(i),out next));vector=next;}
                var old=vector;ExperimentPersistentTrials updated;Check.True(vector.TryReplace(35,Trial(35,1),out updated));
                Check.True(old.SharesChunk(updated,0)&&!old.SharesChunk(updated,1)&&old.SharesChunk(updated,2));
                ExperimentMutationTrialSnapshot before=default(ExperimentMutationTrialSnapshot),after=default(ExperimentMutationTrialSnapshot);Check.True(old.TryGet(35,out before)&&updated.TryGet(35,out after));
                Check.True(!before.Onset.HasValue&&after.Onset.HasValue);
            });
            suite.Run("experiment persistent trials budget is cumulative and shared across branches",()=>{
                var budget=new ExperimentPersistentMutationBudget(1024*1024);var empty=ExperimentPersistentTrials.Create(Session,64,budget);
                ExperimentPersistentTrials baseline,a,b;Check.True(empty.TryAppend(Trial(0),out baseline));long prior=budget.EstimatedBytesUsed;
                Check.True(baseline.TryReplace(0,Trial(0,1),out a));long first=budget.EstimatedBytesUsed;
                Check.True(baseline.TryReplace(0,Trial(0,2),out b));Check.True(first>prior&&budget.EstimatedBytesUsed>first);
                ExperimentMutationTrialSnapshot oldValue=default(ExperimentMutationTrialSnapshot),newA=default(ExperimentMutationTrialSnapshot),newB=default(ExperimentMutationTrialSnapshot);Check.True(baseline.TryGet(0,out oldValue)&&a.TryGet(0,out newA)&&b.TryGet(0,out newB));
                Check.True(!oldValue.Onset.HasValue&&newA.Onset.HasValue&&newB.Onset.HasValue&&!newA.Equals(newB));
            });
            suite.Run("experiment persistent trials terminalize budget without revealing old head",()=>{
                var budget=new ExperimentPersistentMutationBudget(256);var vector=ExperimentPersistentTrials.Create(Session,32,budget);ExperimentPersistentTrials next;
                Check.True(!vector.TryAppend(Trial(0),out next)&&next==null&&budget.Terminal);Check.Equal(256L,budget.EstimatedBytesUsed);
                Check.True(!vector.TryAppend(Trial(0),out next));ExperimentMutationTrialSnapshot ignored;Check.True(!vector.TryGet(0,out ignored));
            });
            suite.Run("experiment persistent trials reject invalid source identity and index",()=>{
                var duplicate=ExperimentPersistentTrials.Create(Session,32,new ExperimentPersistentMutationBudget(100000));ExperimentPersistentTrials next;
                Check.True(duplicate.TryAppend(Trial(0),out next));Check.True(!next.TryAppend(Trial(0),out duplicate)&&next.Terminal);
                var foreign=ExperimentPersistentTrials.Create(Session,32,new ExperimentPersistentMutationBudget(100000));
                Check.True(!foreign.TryAppend(Trial(0,session:"foreign"),out next)&&foreign.Terminal);
                var mismatch=ExperimentPersistentTrials.Create(Session,32,new ExperimentPersistentMutationBudget(100000));
                Check.True(mismatch.TryAppend(Trial(0),out next));Check.True(!next.TryReplace(0,Trial(0,ink:Colour.Yellow),out mismatch)&&next.Terminal);
                var invalid=ExperimentPersistentTrials.Create(Session,32,new ExperimentPersistentMutationBudget(100000));
                Check.True(!invalid.TryReplace(0,Trial(0),out next)&&invalid.Terminal);
            });
            suite.Run("experiment persistent trials foreign-thread access ends the shared epoch",()=>{
                var budget=new ExperimentPersistentMutationBudget(100000);var vector=ExperimentPersistentTrials.Create(Session,32,budget);bool result=true;
                var thread=new Thread(()=>{ExperimentPersistentTrials ignored;result=vector.TryAppend(Trial(0),out ignored);});thread.Start();thread.Join();
                Check.True(!result&&budget.Terminal&&budget.LastIssue=="owner_thread_violation");
            });
            suite.Run("experiment persistent trials serialize owner update with foreign terminalization",()=>{
                var budget=new ExperimentPersistentMutationBudget(100000);var empty=ExperimentPersistentTrials.Create(Session,32,budget);
                ExperimentPersistentTrials first;Check.True(empty.TryAppend(Trial(0),out first));bool foreign=true;
                using(var attempted=new ManualResetEvent(false))
                {
                    var thread=new Thread(()=>{attempted.Set();ExperimentMutationTrialSnapshot ignored;foreign=first.TryGet(0,out ignored);});
                    Monitor.Enter(budget.Sync);
                    try{thread.Start();Check.True(attempted.WaitOne(5000));ExperimentPersistentTrials second;Check.True(first.TryReplace(0,Trial(0,1),out second));}
                    finally{Monitor.Exit(budget.Sync);}
                    Check.True(thread.Join(5000));Check.True(!foreign&&budget.Terminal);ExperimentMutationTrialSnapshot value;Check.True(!first.TryGet(0,out value));
                }
            });
            suite.Run("experiment persistent trials configuration failure terminalizes supplied budget",()=>{
                var budget=new ExperimentPersistentMutationBudget(100000);
                Check.Throws<ArgumentException>(()=>ExperimentPersistentTrials.Create("",32,budget));
                Check.True(budget.Terminal&&budget.LastIssue=="vector_configuration_invalid");
            });
        }
    }
}
