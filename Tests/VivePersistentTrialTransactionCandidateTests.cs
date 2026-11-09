using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic candidate only: caller-declared tokens and records do not prove
    // that observations came from an actual Experiment or physical devices.
    public static class VivePersistentTrialTransactionCandidateTests
    {
        const string Session="synthetic-transaction-session";
        sealed class EqualToken
        {public override bool Equals(object other){return other is EqualToken;}public override int GetHashCode(){return 0;}}
        static VivePersistentTrialTransactionCandidate New(object token,int maxItems=128,long budget=16L*1024*1024)
        {return VivePersistentTrialTransactionCandidate.Create(Session,maxItems,budget,token);}
        static TrialRecord Trial(object key,string id="trial-0",string session=Session,string list="list-0",bool practice=false,
            int attempt=0,int block=0,int planned=0,int segment=0,Condition? condition=Condition.A,Colour word=Colour.Red,Colour ink=Colour.Blue)
        {return new TrialRecord(key,id,session,list,practice,attempt,block,planned,segment,condition,new Stimulus(word,ink));}
        static void Unavailable(VivePersistentTrialTransactionCandidate candidate)
        {
            VivePersistentTrialValue ignored;
            Check.True(candidate.Terminal&&!candidate.TryGet(0,out ignored));
            Check.True(!candidate.RealExperimentSourceBound&&!candidate.CanEnterCountdown&&!candidate.CanResume);
        }
        static void Seed(VivePersistentTrialTransactionCandidate candidate,object token,TrialRecord trial)
        {Check.True(candidate.TryBegin(1,1,token));Check.True(candidate.TryAppend(1,0,0,trial,token));Check.True(candidate.TryCommit(1,token));}
        public static void Register(TestSuite suite)
        {
            suite.Run("persistent trial transaction stages multiple steps and publishes only complete commit",()=>{
                var token=new object();var candidate=New(token);var key=new object();var trial=Trial(key);VivePersistentTrialValue value;
                Check.True(!candidate.RealExperimentSourceBound&&!candidate.CanEnterCountdown&&!candidate.CanResume);
                Check.Equal(0L,candidate.Revision);Check.Equal(0,candidate.Count);Check.True(candidate.TryBegin(1,4,token));
                Check.True(candidate.TryAppend(1,0,0,trial,token));Check.True(!candidate.TryGet(0,out value)&&!candidate.Terminal);Check.Equal(null,value.TrialId);
                Check.Equal(0L,candidate.Revision);Check.Equal(0,candidate.Count);
                trial.SetOnset(key,1.0);Check.True(candidate.TryReplace(1,1,0,trial,token));
                trial.SetResponseAt(key,1.2);trial.SetResponse(key,Colour.Blue);trial.SetCorrect(key,true);
                Check.True(candidate.TryReplace(1,2,0,trial,token));trial.SetResponded(key);Check.True(candidate.TryReplace(1,3,0,trial,token));
                Check.True(!candidate.TryGet(0,out value)&&!candidate.Terminal);Check.Equal(null,value.TrialId);Check.Equal(0L,candidate.Revision);Check.Equal(0,candidate.Count);
                Check.True(candidate.TryCommit(1,token));Check.Equal(1L,candidate.Revision);Check.Equal(1,candidate.Count);
                Check.True(candidate.TryGet(0,out value));Check.Equal(TrialOutcome.Responded,value.Outcome);Check.Near(1.0,value.Onset.Value);Check.Near(1.2,value.ResponseAt.Value);
            });
            suite.Run("persistent trial transaction does not expose old or staged data while active",()=>{
                var token=new object();var candidate=New(token);var key=new object();var trial=Trial(key);VivePersistentTrialValue value;Seed(candidate,token,trial);
                Check.True(candidate.TryGet(0,out value));Check.True(candidate.TryBegin(2,1,token));trial.SetOnset(key,2);
                Check.True(candidate.TryReplace(2,0,0,trial,token));Check.Equal(1L,candidate.Revision);Check.Equal(1,candidate.Count);
                Check.True(!candidate.TryGet(0,out value)&&!candidate.Terminal);Check.True(candidate.TryCommit(2,token));
                Check.True(candidate.TryGet(0,out value));Check.Near(2,value.Onset.Value);
            });
            suite.Run("persistent trial transaction accepts an empty no-trial commit",()=>{
                var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,0,token));
                Check.True(candidate.TryCommit(1,token));Check.Equal(1L,candidate.Revision);Check.Equal(0,candidate.Count);Check.True(!candidate.Terminal);
            });
            suite.Run("persistent trial transaction abort does not publish staged changes",()=>{
                var token=new object();var candidate=New(token);var trial=Trial(new object());Seed(candidate,token,trial);
                Check.True(candidate.TryBegin(2,1,token));Check.True(candidate.TryAppend(2,0,1,Trial(new object(),id:"trial-1"),token));
                Check.True(candidate.TryAbort(token));Check.Equal(1L,candidate.Revision);Check.Equal(1,candidate.Count);Unavailable(candidate);
            });
            suite.Run("persistent trial transaction rejects begin and commit revision gaps or replay",()=>{
                foreach(long invalid in new[]{-1L,0L,2L,Int64.MaxValue})
                {var token=new object();var candidate=New(token);Check.True(!candidate.TryBegin(invalid,1,token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,0,token));Check.True(!candidate.TryCommit(2,token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Seed(candidate,token,Trial(new object()));Check.True(!candidate.TryBegin(1,0,token));Unavailable(candidate);}
            });
            suite.Run("persistent trial transaction binds source token by identity rather than equality",()=>{
                var token=new EqualToken();var other=new EqualToken();Check.True(token.Equals(other));
                {var candidate=New(token);Check.True(!candidate.TryBegin(1,0,other));Unavailable(candidate);}
                {var candidate=New(token);Check.True(candidate.TryBegin(1,1,token));Check.True(!candidate.TryAppend(1,0,0,Trial(new object()),other));Unavailable(candidate);}
                {var candidate=New(token);var trial=Trial(new object());Seed(candidate,token,trial);Check.True(candidate.TryBegin(2,1,token));Check.True(!candidate.TryReplace(2,0,0,trial,other));Unavailable(candidate);}
                {var candidate=New(token);Check.True(candidate.TryBegin(1,0,token));Check.True(!candidate.TryCommit(1,other));Unavailable(candidate);}
                {var candidate=New(token);Check.True(candidate.TryBegin(1,0,token));Check.True(!candidate.TryAbort(other));Unavailable(candidate);}
            });
            suite.Run("persistent trial transaction rejects operations outside a transaction and nesting",()=>{
                {var token=new object();var candidate=New(token);Check.True(!candidate.TryCommit(1,token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(!candidate.TryAppend(1,0,0,Trial(new object()),token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(!candidate.TryAbort(token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(!candidate.TryBegin(1,-1,token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(!candidate.TryBegin(1,1025,token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,0,token));Check.True(!candidate.TryBegin(1,0,token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,0,token));Check.True(!candidate.TryAppend(1,0,0,Trial(new object()),token));Unavailable(candidate);}
            });
            suite.Run("persistent trial transaction locks on skipped duplicate or out-of-order steps",()=>{
                foreach(int wrong in new[]{-1,1,2})
                {var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,2,token));Check.True(!candidate.TryAppend(1,wrong,0,Trial(new object()),token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,2,token));Check.True(candidate.TryAppend(1,0,0,Trial(new object()),token));
                 Check.True(!candidate.TryAppend(1,0,1,Trial(new object(),id:"trial-1"),token));Check.Equal(0,candidate.Count);Check.Equal(0L,candidate.Revision);Unavailable(candidate);}
            });
            suite.Run("persistent trial transaction rejects partial commit and wrong stage revision",()=>{
                {var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,2,token));Check.True(candidate.TryAppend(1,0,0,Trial(new object()),token));
                 Check.True(!candidate.TryCommit(1,token));Check.Equal(0,candidate.Count);Check.Equal(0L,candidate.Revision);Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,1,token));Check.True(!candidate.TryAppend(2,0,0,Trial(new object()),token));Unavailable(candidate);}
            });
            suite.Run("persistent trial transaction rejects duplicate foreign and invalid append",()=>{
                {var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,2,token));Check.True(candidate.TryAppend(1,0,0,Trial(new object()),token));
                 Check.True(!candidate.TryAppend(1,1,1,Trial(new object()),token));Check.Equal(0,candidate.Count);Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,1,token));Check.True(!candidate.TryAppend(1,0,0,Trial(new object(),session:"other"),token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,1,token));Check.True(!candidate.TryAppend(1,0,1,Trial(new object()),token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token);Check.True(candidate.TryBegin(1,1,token));Check.True(!candidate.TryAppend(1,0,0,null,token));Unavailable(candidate);}
            });
            suite.Run("persistent trial transaction rejects every immutable trial metadata replacement",()=>{
                Func<TrialRecord>[] changes={
                    ()=>Trial(new object(),id:"other"),()=>Trial(new object(),session:"other"),()=>Trial(new object(),list:"other"),
                    ()=>Trial(new object(),practice:true),()=>Trial(new object(),attempt:1),()=>Trial(new object(),block:1),()=>Trial(new object(),planned:1),
                    ()=>Trial(new object(),segment:1),()=>Trial(new object(),condition:Condition.B),()=>Trial(new object(),word:Colour.Green),()=>Trial(new object(),ink:Colour.Yellow)};
                foreach(var make in changes)
                {var token=new object();var candidate=New(token);Seed(candidate,token,Trial(new object()));Check.True(candidate.TryBegin(2,1,token));
                 Check.True(!candidate.TryReplace(2,0,0,make(),token));Check.Equal(1L,candidate.Revision);Check.Equal(1,candidate.Count);Unavailable(candidate);}
            });
            suite.Run("persistent trial transaction rejects nonfinite reversed time and incomplete responded state",()=>{
                for(int scenario=0;scenario<5;scenario++)
                {
                    var token=new object();var candidate=New(token);var key=new object();var trial=Trial(key);
                    if(scenario==0)trial.SetOnset(key,Double.NaN);
                    if(scenario==1){trial.SetOnset(key,2);trial.SetResponseAt(key,1);}
                    if(scenario==2)trial.SetResponded(key);
                    if(scenario==3){trial.SetOnset(key,1);trial.SetResponseAt(key,2);trial.SetResponse(key,Colour.Blue);trial.SetCorrect(key,false);trial.SetResponded(key);}
                    if(scenario==4)trial.SetResponseAt(key,Double.PositiveInfinity);
                    Check.True(candidate.TryBegin(1,1,token));Check.True(!candidate.TryAppend(1,0,0,trial,token));Unavailable(candidate);
                }
            });
            suite.Run("persistent trial transaction rejects rollback without publishing earlier stage",()=>{
                var token=new object();var candidate=New(token);var key=new object();var trial=Trial(key);trial.SetOnset(key,1);Seed(candidate,token,trial);
                Check.True(candidate.TryBegin(2,2,token));var next=Trial(new object(),id:"trial-1");Check.True(candidate.TryAppend(2,0,1,next,token));
                var alteredKey=new object();var altered=Trial(alteredKey);altered.SetOnset(alteredKey,.5);
                Check.True(!candidate.TryReplace(2,1,0,altered,token));Check.Equal(1L,candidate.Revision);Check.Equal(1,candidate.Count);Unavailable(candidate);
            });
            suite.Run("persistent trial transaction terminalizes invalid capacity and exhausted budget",()=>{
                {var token=new object();var candidate=New(token,1);Check.True(candidate.TryBegin(1,2,token));Check.True(candidate.TryAppend(1,0,0,Trial(new object()),token));
                 Check.True(!candidate.TryAppend(1,1,1,Trial(new object(),id:"trial-1"),token));Unavailable(candidate);}
                {var token=new object();var candidate=New(token,32,256);Check.True(candidate.TryBegin(1,1,token));Check.True(!candidate.TryAppend(1,0,0,Trial(new object()),token));Unavailable(candidate);}
            });
            suite.Run("persistent trial transaction foreign thread locks the candidate",()=>{
                var token=new object();var candidate=New(token);bool result=true;var thread=new Thread(()=>result=candidate.TryBegin(1,0,token));
                thread.Start();thread.Join();Check.True(!result);Unavailable(candidate);
            });
            suite.Run("persistent trial transaction status exposes only an atomic inactive committed pair",()=>{
                var token=new object();var candidate=New(token);long revision;int count;
                Check.True(candidate.TryGetStatus(out revision,out count));Check.Equal(0L,revision);Check.Equal(0,count);
                Check.True(candidate.TryBegin(1,1,token));Check.True(!candidate.TryGetStatus(out revision,out count));
                Check.Equal(-1L,revision);Check.Equal(-1,count);Check.True(!candidate.Terminal);
                Check.True(candidate.TryAppend(1,0,0,Trial(new object()),token));Check.True(!candidate.TryGetStatus(out revision,out count));
                Check.Equal(-1L,revision);Check.Equal(-1,count);Check.True(candidate.TryCommit(1,token));
                Check.True(candidate.TryGetStatus(out revision,out count));Check.Equal(1L,revision);Check.Equal(1,count);
                Check.True(candidate.TryBegin(2,0,token));Check.True(!candidate.TryGetStatus(out revision,out count));Check.Equal(-1L,revision);Check.Equal(-1,count);
                Check.True(candidate.TryCommit(2,token));Check.True(candidate.TryGetStatus(out revision,out count));Check.Equal(2L,revision);Check.Equal(1,count);
                bool foreignResult=true;long foreignRevision=99;int foreignCount=99;
                var thread=new Thread(()=>foreignResult=candidate.TryGetStatus(out foreignRevision,out foreignCount));thread.Start();thread.Join();
                Check.True(!foreignResult&&candidate.Terminal);Check.Equal(-1L,foreignRevision);Check.Equal(-1,foreignCount);
                Check.True(!candidate.TryGetStatus(out revision,out count));Check.Equal(-1L,revision);Check.Equal(-1,count);
            });
            suite.Run("persistent trial transaction compares 104 records over multi-step commits",()=>{
                var token=new object();var candidate=New(token);var records=new TrialRecord[104];var keys=new object[104];long revision=0;
                for(int first=0;first<104;first+=4)
                {
                    Check.True(candidate.TryBegin(++revision,4,token));
                    for(int i=0;i<4;i++){int index=first+i;bool practice=index<8;int main=index-8,block=practice?-1:main/24;keys[index]=new object();records[index]=Trial(keys[index],id:"trial-"+index,
                        list:practice?"practice-list":"list-"+block,practice:practice,attempt:practice?1:0,block:block,planned:practice?index:main%24,condition:practice?(Condition?)null:(Condition)(block%4));
                        Check.True(candidate.TryAppend(revision,i,index,records[index],token));}
                    Check.True(candidate.TryCommit(revision,token));
                }
                for(int first=0;first<104;first+=4)
                {
                    Check.True(candidate.TryBegin(++revision,4,token));
                    for(int i=0;i<4;i++){int index=first+i;records[index].SetOnset(keys[index],index+.25);Check.True(candidate.TryReplace(revision,i,index,records[index],token));}
                    Check.True(candidate.TryCommit(revision,token));
                }
                for(int i=0;i<104;i++){VivePersistentTrialValue actual;Check.True(candidate.TryGet(i,out actual));Check.True(actual.Equals(new VivePersistentTrialValue(records[i])));}
                Check.Equal(104,candidate.Count);Check.Equal(revision,candidate.Revision);Check.True(!candidate.Terminal&&!candidate.RealExperimentSourceBound);
            });
            suite.Run("persistent trial transaction randomized multi-step commits match complete reference after each commit",()=>{
                var token=new object();var candidate=New(token);var random=new Random(91743);var records=new TrialRecord[104];var keys=new object[104];
                var expected=new VivePersistentTrialValue[104];long revision=0;int count=0;
                while(count<104)
                {
                    int steps=Math.Min(104-count,random.Next(1,5));Check.True(candidate.TryBegin(++revision,steps,token));
                    var pending=(VivePersistentTrialValue[])expected.Clone();int starting=count;
                    for(int step=0;step<steps;step++)
                    {
                        int index=starting+step;bool practice=index<8;int main=index-8,block=practice?-1:main/24;keys[index]=new object();
                        records[index]=Trial(keys[index],id:"trial-"+index,list:practice?"practice-list":"list-"+block,practice:practice,
                            attempt:practice?1:0,block:block,planned:practice?index:main%24,condition:practice?(Condition?)null:(Condition)(block%4));
                        Check.True(candidate.TryAppend(revision,step,index,records[index],token));pending[index]=new VivePersistentTrialValue(records[index]);
                        VivePersistentTrialValue hidden;Check.True(!candidate.TryGet(index,out hidden));Check.Equal(null,hidden.TrialId);
                    }
                    Check.Equal(starting,candidate.Count);Check.True(candidate.TryCommit(revision,token));count+=steps;expected=pending;
                    for(int i=0;i<count;i++){VivePersistentTrialValue actual;Check.True(candidate.TryGet(i,out actual));Check.True(expected[i].Equals(actual));}
                }
                int remaining=300;
                while(remaining>0)
                {
                    int steps=Math.Min(remaining,random.Next(1,6));Check.True(candidate.TryBegin(++revision,steps,token));
                    var pending=(VivePersistentTrialValue[])expected.Clone();
                    for(int step=0;step<steps;step++)
                    {
                        int i=random.Next(records.Length);var trial=records[i];var key=keys[i];
                        if(!trial.Onset.HasValue)trial.SetOnset(key,i+1);
                        else if(!trial.ResponseAt.HasValue)trial.SetResponseAt(key,i+1.25);
                        else if(!trial.Response.HasValue)trial.SetResponse(key,trial.Stimulus.Ink);
                        else if(!trial.Correct.HasValue)trial.SetCorrect(key,true);
                        else if(trial.Outcome==TrialOutcome.Pending)trial.SetResponded(key);
                        else if(!trial.TechnicalIssue)trial.SetTechnicalIssue(key);
                        Check.True(candidate.TryReplace(revision,step,i,trial,token));pending[i]=new VivePersistentTrialValue(trial);
                    }
                    Check.True(candidate.TryCommit(revision,token));expected=pending;remaining-=steps;
                    for(int i=0;i<count;i++){VivePersistentTrialValue actual;Check.True(candidate.TryGet(i,out actual));Check.True(expected[i].Equals(actual));}
                }
                Check.Equal(revision,candidate.Revision);Check.Equal(104,candidate.Count);Check.True(!candidate.Terminal&&!candidate.CanEnterCountdown);
            });
        }
    }
}
