using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class VivePersistentTrialVectorCandidateTests
    {
        static TrialRecord Trial(object key,int index,int revision)
        {
            var trial=new TrialRecord(key,"trial-"+index,"synthetic-session","list-"+(index/24),false,0,index/24,index%24,revision%3,
                (Condition)(index%4),new Stimulus((Colour)(index%4),(Colour)((index+1)%4)));
            if(revision%4!=0)trial.SetOnset(key,index+revision*.01);
            if(revision%4==2){trial.SetResponseAt(key,index+revision*.01+.2);trial.SetResponse(key,trial.Stimulus.Ink);trial.SetCorrect(key,true);trial.SetResponded(key);}
            if(revision%4==3)trial.SetInterrupted(key);
            if(revision%2==1)trial.SetTechnicalIssue(key);
            return trial;
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("persistent trial vector randomized versions match complete copied reference",()=>{
                var random=new Random(34751);var vector=VivePersistentTrialVectorCandidate.Create(128,128L*1024*1024);
                var expected=new List<VivePersistentTrialValue>();var saved=new List<Tuple<VivePersistentTrialVectorCandidate,VivePersistentTrialValue[]>>();
                for(int i=0;i<104;i++)
                {
                    var key=new object();var record=Trial(key,i,0);VivePersistentTrialVectorCandidate next;
                    Check.True(vector.TryAppend(record,out next));vector=next;expected.Add(new VivePersistentTrialValue(record));
                }
                for(int iteration=0;iteration<3000;iteration++)
                {
                    int index=random.Next(expected.Count),version=random.Next(1,100000);var key=new object();var record=Trial(key,index,version);
                    VivePersistentTrialVectorCandidate next;Check.True(vector.TryReplace(index,record,out next));vector=next;expected[index]=new VivePersistentTrialValue(record);
                    if(iteration%250==0)saved.Add(Tuple.Create(vector,expected.ToArray()));
                    for(int probe=0;probe<4;probe++){int k=random.Next(expected.Count);Check.True(expected[k].Equals(vector.Get(k)));}
                }
                foreach(var version in saved)for(int i=0;i<version.Item2.Length;i++)Check.True(version.Item2[i].Equals(version.Item1.Get(i)));
                Check.Equal(104,vector.Count);Check.True(!vector.Terminal&&!vector.RealExperimentSourceBound&&!vector.CanEnterCountdown&&!vector.CanResume);
            });
            suite.Run("persistent trial vector shares unchanged chunks and copies the live trial",()=>{
                var vector=VivePersistentTrialVectorCandidate.Create(128,8L*1024*1024);var key=new object();var original=Trial(key,35,0);
                for(int i=0;i<70;i++){VivePersistentTrialVectorCandidate next;Check.True(vector.TryAppend(i==35?original:Trial(new object(),i,0),out next));vector=next;}
                var before=vector;var prior=before.Get(35);original.SetOnset(key,123);VivePersistentTrialVectorCandidate updated;
                Check.True(vector.TryReplace(35,original,out updated));Check.True(before.SharesChunk(updated,0)&&!before.SharesChunk(updated,1)&&before.SharesChunk(updated,2));
                Check.True(prior.Equals(before.Get(35))&&!before.Get(35).Onset.HasValue);Check.Near(123,updated.Get(35).Onset.Value);
                original.SetOnset(key,456);Check.Near(123,updated.Get(35).Onset.Value);
            });
            suite.Run("persistent trial vector cumulative budget is terminal without corrupting old versions",()=>{
                var empty=VivePersistentTrialVectorCandidate.Create(32,256);long prior=empty.EstimatedBytesUsed;VivePersistentTrialVectorCandidate failed;
                Check.True(!empty.TryAppend(Trial(new object(),0,0),out failed)&&failed==null&&empty.Terminal);
                Check.Equal(prior,empty.EstimatedBytesUsed);Check.Equal("estimated_budget_exhausted",empty.LastIssue);Check.Equal(0,empty.Count);
                Check.True(!empty.TryAppend(Trial(new object(),0,0),out failed));
            });
            suite.Run("persistent trial vector rejects invalid indices and capacity without consuming budget",()=>{
                var vector=VivePersistentTrialVectorCandidate.Create(1,100000);var trial=Trial(new object(),0,0);VivePersistentTrialVectorCandidate next;
                long before=vector.EstimatedBytesUsed;Check.Throws<ArgumentOutOfRangeException>(()=>vector.Get(0));
                Check.Throws<ArgumentOutOfRangeException>(()=>vector.TryReplace(0,trial,out next));Check.Equal(before,vector.EstimatedBytesUsed);
                Check.True(vector.TryAppend(trial,out next));vector=next;long after=vector.EstimatedBytesUsed;
                Check.Throws<InvalidOperationException>(()=>vector.TryAppend(trial,out next));Check.Equal(after,vector.EstimatedBytesUsed);Check.True(!vector.Terminal);
            });
            suite.Run("persistent trial vector foreign-thread writer ends its development epoch",()=>{
                var vector=VivePersistentTrialVectorCandidate.Create(32,100000);bool ok=true;
                var thread=new Thread(()=>{VivePersistentTrialVectorCandidate ignored;ok=vector.TryAppend(Trial(new object(),0,0),out ignored);});thread.Start();thread.Join();
                Check.True(!ok&&vector.Terminal&&vector.LastIssue=="owner_thread_violation"&&vector.Count==0);
            });
            suite.Run("persistent trial value preserves timestamp bits without validating them",()=>{
                var key=new object();var trial=Trial(key,0,0);trial.SetOnset(key,Double.NaN);var nan=new VivePersistentTrialValue(trial);
                Check.True(nan.Equals(nan));trial.SetOnset(key,0.0);var positive=new VivePersistentTrialValue(trial);
                trial.SetOnset(key,BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL)));var negative=new VivePersistentTrialValue(trial);
                Check.True(!positive.Equals(negative)&&!nan.Equals(positive));
            });
        }
    }
}
