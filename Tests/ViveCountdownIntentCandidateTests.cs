using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveCountdownIntentCandidateTests
    {
        const string Session="intent-session", Epoch="epoch-new", Previous="epoch-old", Map="mapping-v1";
        static TaskSnapshot Task(Phase phase=Phase.BlockReady,int block=0,int segment=0,Condition condition=Condition.A,string session=Session)
        {return new TaskSnapshot(session,block,segment,null,null,null,phase,condition);}
        static ViveCountdownIntentContext Context(TaskSnapshot task=null,ViveCountdownIntent intent=ViveCountdownIntent.BeginBlock,Phase? pausedFrom=null,
            int expectedSegment=0,string epoch=Epoch,string previous=null,long confirmation=3,string owner="owner",bool source=true,bool host=true,bool resume=false,bool hasNext=false)
        {return new ViveCountdownIntentContext(task??Task(),intent,pausedFrom,expectedSegment,confirmation,owner,epoch,previous,
            ViveDevelopmentTransport.OperatorStream(Map,epoch),Map,"participant-clock","operator-clock",source,host,resume,hasNext);}
        static ViveCountdownIntentCandidate Gate(ViveCountdownIntentContext context=null){return new ViveCountdownIntentCandidate(context??Context(),10,.5);}
        static ViveCountdownIntentEvidence Evidence(ViveCountdownIntentCandidate gate,ViveCountdownIntentContext context=null,long sequence=1,long head=1,long eye=1,
            double headMin=10.1,double headMax=10.11,double eyeMin=10.12,double eyeMax=10.13,bool headValid=true,bool eyeValid=true,string challenge=null)
        {return new ViveCountdownIntentEvidence(context??Context(),challenge??gate.Challenge,sequence,head,eye,headMin,headMax,eyeMin,eyeMax,headValid,eyeValid);}

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE countdown intent is a one-shot candidate and cannot enter an experiment",()=>{
                var c=Context();var g=Gate(c);
                Check.True(!g.CandidateReady(c,10.01)&&!g.CanEnterCountdown&&!g.CanResume&&!g.CryptographicallyAuthenticated&&!g.HardwareCaptureTimeVerified);
                Check.True(g.Observe(c,Evidence(g,c),10.2));Check.True(g.CandidateReady(c,10.21));
                Check.True(g.ConsumeCandidate(c,10.22));Check.True(!g.ConsumeCandidate(c,10.23)&&!g.CandidateReady(c,10.24));
                Check.True(!g.CanEnterCountdown&&!g.CanResume);
            });
            suite.Run("VIVE intent uses real BlockReady not a fabricated Countdown and validates confirmation",()=>{
                Check.Throws<ArgumentException>(()=>Context(Task(Phase.Countdown)));
                Check.Throws<ArgumentException>(()=>Context(Task(segment:1),expectedSegment:1));
                Check.Throws<ArgumentException>(()=>Context(source:false));Check.Throws<ArgumentException>(()=>Context(host:false));
                Check.Throws<ArgumentException>(()=>Context(confirmation:-1));Check.Throws<ArgumentException>(()=>Context(owner:""));
                Check.Throws<ArgumentException>(()=>Context(previous:Epoch));
                Check.Throws<ArgumentException>(()=>new ViveCountdownIntentContext(Task(),ViveCountdownIntent.BeginBlock,null,0,3,"owner",Epoch,null,
                    "incorrect-stream",Map,"participant-clock","operator-clock",true,true,false,false));
            });
            suite.Run("VIVE recovery intent requires actual paused state new epoch origin and exact next segment",()=>{
                var paused=Task(Phase.Paused,segment:4);
                var fromCountdown=Context(paused,ViveCountdownIntent.ResumeCountdown,Phase.Countdown,4,previous:Previous,resume:true);
                var fromMain=Context(paused,ViveCountdownIntent.ResumeCountdown,Phase.MainTask,5,previous:Previous,resume:true,hasNext:true);
                var countdownGate=Gate(fromCountdown);Check.True(countdownGate.Observe(fromCountdown,Evidence(countdownGate,fromCountdown),10.2));
                var g=Gate(fromMain);Check.True(g.Observe(fromMain,Evidence(g,fromMain),10.2));Check.True(g.CandidateReady(fromMain,10.21));
                Check.Throws<ArgumentException>(()=>Context(paused,ViveCountdownIntent.ResumeCountdown,Phase.MainTask,4,previous:Previous,resume:true,hasNext:true));
                Check.Throws<ArgumentException>(()=>Context(paused,ViveCountdownIntent.ResumeCountdown,Phase.MainTask,5,previous:Previous,resume:true,hasNext:false));
                Check.Throws<ArgumentException>(()=>Context(paused,ViveCountdownIntent.ResumeCountdown,Phase.Countdown,5,previous:Previous,resume:true));
                Check.Throws<ArgumentException>(()=>Context(paused,ViveCountdownIntent.ResumeCountdown,Phase.Countdown,4,resume:true));
                Check.Throws<ArgumentException>(()=>Context(Task(Phase.Countdown,segment:4),ViveCountdownIntent.ResumeCountdown,Phase.Countdown,4,previous:Previous,resume:true));
            });
            suite.Run("VIVE intent rejects other session epoch context and old challenges without refreshing proof",()=>{
                var c=Context();var g=Gate(c);var foreign=Context(Task(session:"different-session"));
                Check.True(!g.Observe(c,Evidence(g,foreign),10.2));Check.True(!g.Observe(c,Evidence(g,c,challenge:"different-challenge"),10.2));
                Check.True(g.Observe(c,Evidence(g,c),10.2));
                Check.True(!g.Observe(c,Evidence(g,c),10.4));Check.True(g.CandidateReady(c,10.49));
                Check.True(!g.CandidateReady(c,10.61)&&g.RecoveryDecisionRequired);
            });
            suite.Run("VIVE intent latches changed authoritative context or source selection",()=>{
                var c=Context();var g=Gate(c);Check.True(g.Observe(c,Evidence(g,c),10.2));
                Check.True(!g.CandidateReady(Context(confirmation:4),10.21)&&g.RecoveryDecisionRequired);
                Check.True(!g.CandidateReady(c,10.22));
                var h=Gate(c);var paused=Context(Task(Phase.Paused),ViveCountdownIntent.ResumeCountdown,Phase.Countdown,0,previous:Previous,resume:true);
                Check.True(!h.CandidateReady(paused,10.1)&&h.RecoveryDecisionRequired);
            });
            suite.Run("VIVE intent requires post-challenge bounded fresh and usable head and eye observations",()=>{
                var c=Context();var before=Gate(c);Check.True(!before.Observe(c,Evidence(before,c,headMin:10),10.2)&&before.RecoveryDecisionRequired);
                var future=Gate(c);Check.True(!future.Observe(c,Evidence(future,c,eyeMax:10.3),10.2)&&future.RecoveryDecisionRequired);
                var g=Gate(c);Check.True(g.Observe(c,Evidence(g,c),10.2));
                Check.True(!g.Observe(c,Evidence(g,c,sequence:2,head:2,eye:2,eyeValid:false),10.21)&&g.RecoveryDecisionRequired);
                Check.Throws<ArgumentException>(()=>Evidence(g,c,eyeMin:Double.NaN));
            });
            suite.Run("VIVE intent detects local and observation rollback and latches loss",()=>{
                var c=Context();var g=Gate(c);Check.True(g.Observe(c,Evidence(g,c),10.2));
                Check.True(!g.Observe(c,Evidence(g,c,sequence:2,head:2,eye:2,headMin:10.09),10.21)&&g.RecoveryDecisionRequired);
                var h=Gate(c);Check.True(!h.CandidateReady(c,9.9)&&h.RecoveryDecisionRequired);
                var i=Gate(c);i.SignalLoss("transport");Check.True(i.RecoveryDecisionRequired&&!i.Observe(c,Evidence(i,c),10.2));
                var j=Gate(c);Check.True(j.Observe(c,Evidence(j,c),10.2)&&j.ConsumeCandidate(c,10.21));j.SignalLoss("paused");Check.True(j.RecoveryDecisionRequired);
            });
            suite.Run("VIVE newer invalid proof invalidates even without a fresh native sample; old proof cannot",()=>{
                var c=Context();var g=Gate(c);Check.True(g.Observe(c,Evidence(g,c,sequence:3,head:3,eye:3),10.2));
                Check.True(!g.Observe(c,Evidence(g,c,sequence:2,head:2,eye:2,eyeValid:false),10.21));
                Check.True(g.CandidateReady(c,10.22));
                Check.True(!g.Observe(c,Evidence(g,c,sequence:4,head:3,eye:3,eyeValid:false),10.23));
                Check.True(g.RecoveryDecisionRequired&&!g.CandidateReady(c,10.24));
            });
            suite.Run("VIVE rejected current proof numbers cannot be replayed or superseded by older valid proofs",()=>{
                var c=Context();var g=Gate(c);
                Check.True(!g.Observe(c,Evidence(g,c,sequence:3,head:3,eye:3,eyeValid:false),10.2));
                Check.True(!g.Observe(c,Evidence(g,c,sequence:2,head:2,eye:2),10.21));
                Check.True(g.RecoveryDecisionRequired&&!g.CandidateReady(c,10.22));
                var h=Gate(c);Check.True(h.Observe(c,Evidence(h,c,sequence:1,head:1,eye:1),10.2));
                Check.True(!h.Observe(c,Evidence(h,c,sequence:3,head:1,eye:1),10.3));
                Check.True(!h.Observe(c,Evidence(h,c,sequence:2,head:2,eye:2,headMin:10.3,headMax:10.31,eyeMin:10.3,eyeMax:10.31),10.4));
                Check.True(!h.CandidateReady(c,10.61)&&h.RecoveryDecisionRequired);
            });
            suite.Run("VIVE newer proof with duplicate samples still latches future or rolled-back timing",()=>{
                var c=Context();var future=Gate(c);Check.True(future.Observe(c,Evidence(future,c),10.2));
                Check.True(!future.Observe(c,Evidence(future,c,sequence:2,head:1,eye:1,headMax:10.4),10.21));
                Check.True(future.RecoveryDecisionRequired);
                var rollback=Gate(c);Check.True(rollback.Observe(c,Evidence(rollback,c),10.2));
                Check.True(!rollback.Observe(c,Evidence(rollback,c,sequence:2,head:1,eye:1,headMin:10.09),10.21));
                Check.True(rollback.RecoveryDecisionRequired);
            });
            suite.Run("VIVE intent rejects foreign-thread calls and latches failure",()=>{
                var c=Context();var g=Gate(c);bool threw=false;
                var thread=new System.Threading.Thread(()=>{try{g.CandidateReady(c,10.1);}catch(InvalidOperationException){threw=true;}});
                thread.Start();thread.Join();Check.True(threw&&g.RecoveryDecisionRequired&&!g.CandidateReady(c,10.1));
            });
            suite.Run("VIVE intent never bridges an initial or established proof gap",()=>{
                var c=Context();var g=Gate(c);Check.True(!g.Observe(c,Evidence(g,c,headMin:10.51,headMax:10.52,eyeMin:10.51,eyeMax:10.52),10.55));
                Check.True(g.RecoveryDecisionRequired);
                var h=Gate(c);Check.True(h.Observe(c,Evidence(h,c),10.2));
                Check.True(!h.Observe(c,Evidence(h,c,sequence:2,head:2,eye:2,headMin:10.65,headMax:10.66,eyeMin:10.65,eyeMax:10.66),10.7));
                Check.True(h.RecoveryDecisionRequired);
            });
        }
    }
}
