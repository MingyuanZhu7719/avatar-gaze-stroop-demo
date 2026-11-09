using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic mirror contract only. No genuine Experiment provenance,
    // physical input, network, experiment admission or disk I/O is implied.
    public static class VivePersistentTrialMirrorCandidateTests
    {
        const string Session="synthetic-mirror-session";
        static VivePersistentTrialMirrorCandidate New(int maxItems=128,long budget=16L*1024*1024)
        {return VivePersistentTrialMirrorCandidate.Create(Session,maxItems,budget);}
        static TrialRecord Trial(object key,string trial="trial-0",string session=Session,string list="list-0",bool practice=false,
            int attempt=0,int block=0,int planned=0,int segment=0,Condition? condition=Condition.A,Colour word=Colour.Red,Colour ink=Colour.Blue)
        {return new TrialRecord(key,trial,session,list,practice,attempt,block,planned,segment,condition,new Stimulus(word,ink));}
        static void Unavailable(VivePersistentTrialMirrorCandidate mirror)
        {
            VivePersistentTrialValue value;
            Check.True(mirror.Terminal&&!mirror.TryGet(0,out value));
            Check.True(!mirror.RealExperimentSourceBound&&!mirror.CanEnterCountdown&&!mirror.CanResume);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("persistent trial mirror accepts ordered append and each gradual response update",()=>{
                var mirror=New();var key=new object();var trial=Trial(key);VivePersistentTrialValue value;
                Check.True(!mirror.RealExperimentSourceBound&&!mirror.CanEnterCountdown&&!mirror.CanResume);
                Check.Equal(0L,mirror.Revision);Check.Equal(0,mirror.Count);Check.True(mirror.TryAppend(1,0,trial));
                Check.True(mirror.TryGet(0,out value));Check.Equal("trial-0",value.TrialId);Check.Equal(TrialOutcome.Pending,value.Outcome);
                trial.SetOnset(key,1.25);Check.True(mirror.TryReplace(2,0,trial));
                trial.SetResponseAt(key,1.5);Check.True(mirror.TryReplace(3,0,trial));
                trial.SetResponse(key,Colour.Blue);Check.True(mirror.TryReplace(4,0,trial));
                trial.SetCorrect(key,true);Check.True(mirror.TryReplace(5,0,trial));
                trial.SetTechnicalIssue(key);Check.True(mirror.TryReplace(6,0,trial));
                trial.SetResponded(key);Check.True(mirror.TryReplace(7,0,trial));
                Check.True(mirror.TryGet(0,out value));Check.Equal(7L,mirror.Revision);Check.Equal(1,mirror.Count);
                Check.Near(1.25,value.Onset.Value);Check.Near(1.5,value.ResponseAt.Value);Check.Equal(Colour.Blue,value.Response.Value);
                Check.Equal(true,value.Correct.Value);Check.Equal(TrialOutcome.Responded,value.Outcome);Check.True(value.TechnicalIssue);
                Check.True(!mirror.Terminal&&!mirror.CanEnterCountdown&&!mirror.CanResume);
            });
            suite.Run("persistent trial mirror preserves interruption and old copied values",()=>{
                var mirror=New();var key=new object();var trial=Trial(key);VivePersistentTrialValue before,after;
                Check.True(mirror.TryAppend(1,0,trial));Check.True(mirror.TryGet(0,out before));
                trial.SetOnset(key,2);trial.SetInterrupted(key);Check.True(mirror.TryReplace(2,0,trial));
                Check.True(mirror.TryGet(0,out after));Check.Equal(TrialOutcome.Pending,before.Outcome);Check.True(!before.Onset.HasValue);
                Check.Equal(TrialOutcome.Interrupted,after.Outcome);Check.Near(2,after.Onset.Value);
                trial.SetOnset(key,3);Check.True(mirror.TryGet(0,out after));Check.Near(2,after.Onset.Value);
            });
            suite.Run("persistent trial mirror compares 104 ordered synthetic trials against copied reference",()=>{
                var mirror=New();var records=new TrialRecord[104];var keys=new object[104];
                for(int i=0;i<records.Length;i++)
                {
                    bool practice=i<8;int main=i-8;int block=practice?-1:main/24;
                    keys[i]=new object();records[i]=Trial(keys[i],"trial-"+i,list:practice?"practice-list":"list-"+block,practice:practice,attempt:practice?1:0,
                        block:block,planned:practice?i:main%24,condition:practice?(Condition?)null:(Condition)(block%4),word:(Colour)(i%4),ink:(Colour)((i+1)%4));
                    Check.True(mirror.TryAppend(i+1,i,records[i]));
                }
                long revision=104;
                for(int i=0;i<records.Length;i++)
                {
                    records[i].SetOnset(keys[i],i+.25);Check.True(mirror.TryReplace(++revision,i,records[i]));
                    VivePersistentTrialValue actual;Check.True(mirror.TryGet(i,out actual));Check.True(actual.Equals(new VivePersistentTrialValue(records[i])));
                }
                Check.Equal(104,mirror.Count);Check.Equal(revision,mirror.Revision);Check.True(!mirror.Terminal);
            });
            suite.Run("persistent trial mirror locks on missing replayed and out-of-order revisions",()=>{
                foreach(long first in new[]{-1L,0L,2L,Int64.MaxValue})
                {var mirror=New();Check.True(!mirror.TryAppend(first,0,Trial(new object())));Unavailable(mirror);}
                var replay=New();var trial=Trial(new object());Check.True(replay.TryAppend(1,0,trial));
                Check.True(!replay.TryReplace(1,0,trial));Unavailable(replay);
                var gap=New();trial=Trial(new object());Check.True(gap.TryAppend(1,0,trial));
                Check.True(!gap.TryReplace(3,0,trial));Unavailable(gap);
            });
            suite.Run("persistent trial mirror locks on bad append order duplicate trial or foreign session",()=>{
                var wrongIndex=New();Check.True(!wrongIndex.TryAppend(1,1,Trial(new object())));Unavailable(wrongIndex);
                var negative=New();Check.True(!negative.TryAppend(1,-1,Trial(new object())));Unavailable(negative);
                var duplicate=New();Check.True(duplicate.TryAppend(1,0,Trial(new object())));
                Check.True(!duplicate.TryAppend(2,1,Trial(new object())));Unavailable(duplicate);
                var foreign=New();Check.True(!foreign.TryAppend(1,0,Trial(new object(),session:"other-session")));Unavailable(foreign);
            });
            suite.Run("persistent trial mirror rejects replacement of every immutable identity field",()=>{
                Func<TrialRecord>[] changed={
                    ()=>Trial(new object(),trial:"other"),()=>Trial(new object(),session:"other-session"),()=>Trial(new object(),list:"other-list"),
                    ()=>Trial(new object(),practice:true),()=>Trial(new object(),attempt:1),()=>Trial(new object(),block:1),
                    ()=>Trial(new object(),planned:1),()=>Trial(new object(),segment:1),()=>Trial(new object(),condition:Condition.B),
                    ()=>Trial(new object(),word:Colour.Green),()=>Trial(new object(),ink:Colour.Yellow)
                };
                foreach(var make in changed)
                {var mirror=New();Check.True(mirror.TryAppend(1,0,Trial(new object())));Check.True(!mirror.TryReplace(2,0,make()));Unavailable(mirror);}
            });
            suite.Run("persistent trial mirror latches existing onset and response-time rollback",()=>{
                var key=new object();var trial=Trial(key);trial.SetOnset(key,2);trial.SetResponseAt(key,2.5);
                foreach(var lower in new[]{1.0,3.0})
                {
                    var mirror=New();Check.True(mirror.TryAppend(1,0,trial));var alteredKey=new object();var altered=Trial(alteredKey);
                    altered.SetOnset(alteredKey,lower);altered.SetResponseAt(alteredKey,2.5);
                    Check.True(!mirror.TryReplace(2,0,altered));Unavailable(mirror);
                }
                var responseMirror=New();Check.True(responseMirror.TryAppend(1,0,trial));var otherKey=new object();var other=Trial(otherKey);
                other.SetOnset(otherKey,2);other.SetResponseAt(otherKey,2.4);Check.True(!responseMirror.TryReplace(2,0,other));Unavailable(responseMirror);
            });
            suite.Run("persistent trial mirror latches existing response correctness outcome and technical rollback",()=>{
                var key=new object();var trial=Trial(key);trial.SetOnset(key,1);trial.SetResponseAt(key,2);trial.SetResponse(key,Colour.Blue);trial.SetCorrect(key,true);trial.SetResponded(key);trial.SetTechnicalIssue(key);
                var missing=New();Check.True(missing.TryAppend(1,0,trial));Check.True(!missing.TryReplace(2,0,Trial(new object())));Unavailable(missing);
                var changed=New();Check.True(changed.TryAppend(1,0,trial));var otherKey=new object();var other=Trial(otherKey);other.SetOnset(otherKey,1);other.SetResponseAt(otherKey,2);
                other.SetResponse(otherKey,Colour.Red);other.SetCorrect(otherKey,false);other.SetResponded(otherKey);other.SetTechnicalIssue(otherKey);
                Check.True(!changed.TryReplace(2,0,other));Unavailable(changed);
                var interrupted=New();var pkey=new object();var prior=Trial(pkey);prior.SetInterrupted(pkey);Check.True(interrupted.TryAppend(1,0,prior));
                Check.True(!interrupted.TryReplace(2,0,Trial(new object())));Unavailable(interrupted);
            });
            suite.Run("persistent trial mirror independently latches each established response-field rollback",()=>{
                for(int omit=0;omit<5;omit++)
                {
                    var key=new object();var prior=Trial(key);prior.SetOnset(key,1);prior.SetResponseAt(key,2);prior.SetResponse(key,Colour.Blue);prior.SetCorrect(key,true);prior.SetTechnicalIssue(key);
                    var mirror=New();Check.True(mirror.TryAppend(1,0,prior));
                    var otherKey=new object();var changed=Trial(otherKey);
                    if(omit!=0)changed.SetOnset(otherKey,1);if(omit!=1)changed.SetResponseAt(otherKey,2);
                    if(omit!=2)changed.SetResponse(otherKey,Colour.Blue);if(omit!=3)changed.SetCorrect(otherKey,true);if(omit!=4)changed.SetTechnicalIssue(otherKey);
                    Check.True(!mirror.TryReplace(2,0,changed));Unavailable(mirror);
                }
            });
            suite.Run("persistent trial mirror rejects missing invalid and nonfinite observations",()=>{
                var nullRecord=New();Check.True(!nullRecord.TryAppend(1,0,null));Unavailable(nullRecord);
                var detached=New();Check.True(!detached.TryAppend(1,0,new TrialRecord()));Unavailable(detached);
                var badTime=New();var key=new object();var bad=Trial(key);bad.SetOnset(key,Double.NaN);
                Check.True(!badTime.TryAppend(1,0,bad));Unavailable(badTime);
                var reverseTime=New();key=new object();bad=Trial(key);bad.SetOnset(key,2);bad.SetResponseAt(key,1);
                Check.True(!reverseTime.TryAppend(1,0,bad));Unavailable(reverseTime);
                var negativeTime=New();key=new object();bad=Trial(key);bad.SetOnset(key,-1);
                Check.True(!negativeTime.TryAppend(1,0,bad));Unavailable(negativeTime);
                var infiniteTime=New();key=new object();bad=Trial(key);bad.SetOnset(key,1);bad.SetResponseAt(key,Double.PositiveInfinity);
                Check.True(!infiniteTime.TryAppend(1,0,bad));Unavailable(infiniteTime);
            });
            suite.Run("persistent trial mirror terminalizes invalid access capacity and cumulative budget",()=>{
                var badRead=New();VivePersistentTrialValue value;Check.True(!badRead.TryGet(0,out value));Unavailable(badRead);
                var badReplace=New();Check.True(!badReplace.TryReplace(1,0,Trial(new object())));Unavailable(badReplace);
                var capacity=New(1);Check.True(capacity.TryAppend(1,0,Trial(new object())));
                Check.True(!capacity.TryAppend(2,1,Trial(new object(),trial:"trial-1")));Unavailable(capacity);
                var budget=New(32,256);Check.True(!budget.TryAppend(1,0,Trial(new object())));Unavailable(budget);
            });
            suite.Run("persistent trial mirror fails closed on foreign thread",()=>{
                var mirror=New();bool result=true;var thread=new Thread(()=>result=mirror.TryAppend(1,0,Trial(new object())));
                thread.Start();thread.Join();Check.True(!result);Unavailable(mirror);
            });
            suite.Run("persistent trial mirror serializes owner and foreign-thread terminalization",()=>{
                var mirror=New();var trial=Trial(new object());Check.True(mirror.TryAppend(1,0,trial));
                var field=typeof(VivePersistentTrialMirrorCandidate).GetField("sync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                Check.True(field!=null);object gate=field.GetValue(mirror);bool foreignResult=true;
                using(var attempted=new ManualResetEvent(false))
                {
                    var thread=new Thread(()=>{attempted.Set();VivePersistentTrialValue ignored;foreignResult=mirror.TryGet(0,out ignored);});
                    Monitor.Enter(gate);
                    try{thread.Start();Check.True(attempted.WaitOne(5000));Check.True(mirror.TryReplace(2,0,trial));}
                    finally{Monitor.Exit(gate);}
                    Check.True(thread.Join(5000));Check.True(!foreignResult&&mirror.Terminal);Unavailable(mirror);
                    Check.True(!mirror.TryReplace(3,0,trial));
                }
            });
            suite.Run("persistent trial mirror requires complete internally correct responded records while permitting pending steps",()=>{
                for(int missing=0;missing<4;missing++)
                {
                    var mirror=New();var key=new object();var trial=Trial(key);
                    if(missing!=0)trial.SetOnset(key,1);if(missing!=1)trial.SetResponseAt(key,2);
                    if(missing!=2)trial.SetResponse(key,Colour.Blue);if(missing!=3)trial.SetCorrect(key,true);
                    trial.SetResponded(key);Check.True(!mirror.TryAppend(1,0,trial));Unavailable(mirror);
                    var replacement=New();var replacementKey=new object();var changed=Trial(replacementKey);Check.True(replacement.TryAppend(1,0,changed));
                    if(missing!=0)changed.SetOnset(replacementKey,1);if(missing!=1)changed.SetResponseAt(replacementKey,2);
                    if(missing!=2)changed.SetResponse(replacementKey,Colour.Blue);if(missing!=3)changed.SetCorrect(replacementKey,true);
                    changed.SetResponded(replacementKey);Check.True(!replacement.TryReplace(2,0,changed));Unavailable(replacement);
                }
                foreach(var response in new[]{Colour.Blue,Colour.Red})
                {
                    var mirror=New();var key=new object();var trial=Trial(key);trial.SetOnset(key,1);trial.SetResponseAt(key,2);
                    trial.SetResponse(key,response);trial.SetCorrect(key,response!=trial.Stimulus.Ink);trial.SetResponded(key);
                    Check.True(!mirror.TryAppend(1,0,trial));Unavailable(mirror);
                    var replacement=New();var replacementKey=new object();var changed=Trial(replacementKey);Check.True(replacement.TryAppend(1,0,changed));
                    changed.SetOnset(replacementKey,1);changed.SetResponseAt(replacementKey,2);changed.SetResponse(replacementKey,response);
                    changed.SetCorrect(replacementKey,response!=changed.Stimulus.Ink);changed.SetResponded(replacementKey);
                    Check.True(!replacement.TryReplace(2,0,changed));Unavailable(replacement);
                }
                var partial=New();var partialKey=new object();var pending=Trial(partialKey);VivePersistentTrialValue value;
                Check.True(partial.TryAppend(1,0,pending));pending.SetResponseAt(partialKey,2);Check.True(partial.TryReplace(2,0,pending));
                pending.SetOnset(partialKey,1);Check.True(partial.TryReplace(3,0,pending));pending.SetResponse(partialKey,Colour.Blue);
                Check.True(partial.TryReplace(4,0,pending));pending.SetCorrect(partialKey,true);Check.True(partial.TryReplace(5,0,pending));
                pending.SetResponded(partialKey);Check.True(partial.TryReplace(6,0,pending));
                Check.True(partial.TryGet(0,out value)&&value.Outcome==TrialOutcome.Responded&&!partial.Terminal);
            });
            suite.Run("persistent trial mirror rejects later onset that would follow an existing response",()=>{
                var mirror=New();var key=new object();var pending=Trial(key);pending.SetResponseAt(key,1);
                Check.True(mirror.TryAppend(1,0,pending));pending.SetOnset(key,2);
                Check.True(!mirror.TryReplace(2,0,pending));Unavailable(mirror);
            });
        }
    }
}
