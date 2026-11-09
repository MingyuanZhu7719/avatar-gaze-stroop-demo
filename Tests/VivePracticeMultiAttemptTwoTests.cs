using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Declared in-memory records only; no consent, participant, device, or storage proof.
    public static class VivePracticeMultiAttemptTwoTests
    {
        const int Limit=262144, Batches=64;
        sealed class Clock:IClock {public string ClockId{get{return "clock";}}public double Now{get{return 10;}}}
        sealed class Fixture
        {
            internal readonly VivePracticeContextV3 Context;
            internal readonly VivePracticeStartEnvelope Start;
            internal Fixture(int attempt=2,VivePracticeEpochReasonV3 reason=VivePracticeEpochReasonV3.PracticeRetry,string previous="epoch-1",string from="PracticeRetry")
            {
                Context=new VivePracticeContextV3("session","participant","stream","device","clock","map",attempt,"epoch",0,previous,reason,10);
                var events=new[]{Event(0,"practice_started",-1,null,new Dictionary<string,string>{{"attempt",attempt.ToString()}}),
                    Event(1,"phase_changed",-1,null,new Dictionary<string,string>{{"from",from},{"to","Practice"}}),
                    Event(2,"trial_planned",-1,"session/p"+attempt+"/t0",new Dictionary<string,string>{{"list","practice"},{"planned_index","0"}})};
                var batch=new VivePracticeStartBatch("session","clock",attempt,1,events);
                Start=VivePracticeStartEnvelopeCodec.Encode(Context,batch,Limit);
            }
            internal VivePracticeMultiWriter Writer(){return new VivePracticeMultiWriter(Start,Limit,Batches);}
        }
        static JournalEvent Event(int seq,string kind,int block,string trial,IDictionary<string,string> fields)
        {return new JournalEvent(seq,10,"clock","session",block,0,trial,kind,fields);}
        static JournalEvent Done(string score="5",string attempt="2")
        {return Event(3,"practice_completed",-1,"session/p2/t7",new Dictionary<string,string>{{"attempt",attempt},{"correct_first",score},{"planned","8"}});}
        static JournalEvent End(string reason="PracticeFailed",string detail="two_practice_attempts_below_6_of_8",string trial=null)
        {return Event(4,"session_ended",-1,trial,new Dictionary<string,string>{{"reason",reason},{"detail",detail}});}
        static JournalEvent Phase(int seq,string to,int block,string from="Practice",string trial=null)
        {return Event(seq,"phase_changed",block,trial,new Dictionary<string,string>{{"from",from},{"to",to}});}
        static byte[] Prefix(byte[] bytes,int length){var a=new byte[length];Buffer.BlockCopy(bytes,0,a,0,length);return a;}
        static byte[] Append(byte[] a,byte[] b){var c=new byte[a.Length+b.Length];Buffer.BlockCopy(a,0,c,0,a.Length);Buffer.BlockCopy(b,0,c,a.Length,b.Length);return c;}
        static VivePracticeMultiReadResult Read(byte[] b,bool recovery=true){return VivePracticeMultiReader.Read(b,recovery,Limit,Batches);}
        static byte[] Forged(Fixture f,params JournalEvent[] events)
        {
            var start=f.Writer().Snapshot();var previous=new byte[32];Buffer.BlockCopy(start,start.Length-32,previous,0,32);
            var payload=VivePracticeMultiCodec.EncodeTransaction(f.Context,events,Limit);byte[] ignored;
            return Append(start,VivePracticeMultiCodec.Frame(1,2,previous,payload,out ignored));
        }
        static void Reject(Fixture f,params JournalEvent[] events)
        {
            var w=f.Writer();var before=w.Snapshot();Check.True(!w.AppendTaskTransaction(f.Context,events));Check.True(w.Faulted);
            Check.Equal(before.Length,w.Snapshot().Length);var r=Read(Forged(f,events));Check.Equal(VivePracticeMultiStatus.Invalid,r.Status);
            Check.Equal(3,r.Events.Count);Check.Equal(before.Length,r.CommittedBytes);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("multi attempt two accepts actual Experiment correct response before last trial",()=>{
                var clock=new Clock();var allocation=new Allocation("allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var task=new Experiment(new SessionConfiguration("session",42,allocation,RunKind.Simulation,"en",1),clock);
                task.BeginFamiliarization(Actor.Owner);task.BeginPractice(Actor.Owner);
                for(int i=0;i<8;i++)
                {
                    task.MarkPresented(task.CurrentTrial.TrialId);var ink=task.CurrentTrial.Stimulus.Ink;
                    var wrong=ink==Colour.Red?Colour.Green:Colour.Red;Check.True(task.ButtonChanged(wrong,true));task.ButtonChanged(wrong,false);task.ContinuePractice();
                }
                Check.Equal(AvatarStroop.Core.Phase.PracticeRetry,task.Phase);int start=task.Events.Count;task.BeginPractice(Actor.Owner);
                var startEvents=new List<JournalEvent>();for(int i=start;i<task.Events.Count;i++)startEvents.Add(task.Events[i]);
                var context=new VivePracticeContextV3("session","participant","stream","device","clock","map",2,"epoch-2",0,"epoch-1",VivePracticeEpochReasonV3.PracticeRetry,10);
                var batch=new VivePracticeStartBatch("session","clock",2,1,startEvents);
                var w=new VivePracticeMultiWriter(VivePracticeStartEnvelopeCodec.Encode(context,batch,Limit),Limit,Batches);
                int before=task.Events.Count;task.MarkPresented(task.CurrentTrial.TrialId);
                var onset=new List<JournalEvent>();for(int i=before;i<task.Events.Count;i++)onset.Add(task.Events[i]);
                Check.True(w.AppendTaskTransaction(context,onset.ToArray()));before=task.Events.Count;
                Check.True(task.ButtonChanged(task.CurrentTrial.Stimulus.Ink,true));var response=new List<JournalEvent>();
                for(int i=before;i<task.Events.Count;i++)response.Add(task.Events[i]);
                Check.Equal(3,response.Count);Check.Equal("Practice",response[1].Fields["from"]);Check.Equal("Practice",response[1].Fields["to"]);
                Check.True(w.AppendTaskTransaction(context,response.ToArray()));Check.Equal(7,Read(w.Snapshot()).Events.Count);
            });
            suite.Run("multi attempt two successful completion accepts whole boundary",()=>{
                var f=new Fixture();var w=f.Writer();Check.True(w.AppendTaskTransaction(f.Context,new[]{Done("6"),Phase(4,"BlockPreparation",0)}));
                Check.True(w.Close());var r=Read(w.Snapshot(),false);Check.Equal(VivePracticeMultiStatus.Complete,r.Status);
                Check.Equal(2,r.Context.PracticeAttempt);Check.Equal(5,r.Events.Count);Check.True(!r.FullPracticeCoverageProven);
            });
            suite.Run("multi attempt two failure accepts exact three-event boundary",()=>{
                var f=new Fixture();var w=f.Writer();Check.True(w.AppendTaskTransaction(f.Context,new[]{Done(),End(),Phase(5,"Ended",-1,"PracticeCorrection")}));
                var prefix=Read(w.Snapshot());Check.Equal(VivePracticeMultiStatus.Unclean,prefix.Status);Check.Equal(6,prefix.Events.Count);
                Check.True(w.Close());Check.Equal(VivePracticeMultiStatus.Complete,Read(w.Snapshot(),false).Status);
            });
            suite.Run("multi attempt two failure truncation never reveals half terminal transaction",()=>{
                var f=new Fixture();var w=f.Writer();var start=w.Snapshot();Check.True(w.AppendTaskTransaction(f.Context,new[]{Done(),End(),Phase(5,"Ended",-1)}));
                var finish=w.Snapshot();for(int i=start.Length;i<finish.Length;i++){var r=Read(Prefix(finish,i));Check.Equal(3,r.Events.Count);Check.Equal(start.Length,r.CommittedBytes);}
                Check.Equal(6,Read(finish).Events.Count);
            });
            suite.Run("multi attempt two rejects split missing duplicate and reordered tail",()=>{
                var f=new Fixture();Reject(f,Done());Reject(f,Done(),End());Reject(f,Done(),Phase(4,"PracticeRetry",-1));
                Reject(f,Done(),End(),End());Reject(f,End(),Done(),Phase(5,"Ended",-1));
                Reject(f,Event(3,"session_ended",-1,null,new Dictionary<string,string>{{"reason","PracticeFailed"},{"detail","two_practice_attempts_below_6_of_8"}}),Phase(4,"Ended",-1));
                Reject(f,Phase(3,"Ended",-1));Reject(f,Phase(3,"BlockPreparation",-1));Reject(f,Phase(3,"PracticeRetry",-1));
            });
            suite.Run("multi attempt two rejects wrong failure reason detail and context",()=>{
                var f=new Fixture();Reject(f,Done(),End("Completed"),Phase(5,"Ended",-1));
                Reject(f,Done(),End(detail:"wrong"),Phase(5,"Ended",-1));Reject(f,Done(),End(trial:"wrong"),Phase(5,"Ended",-1));
                Reject(f,Done(attempt:"1"),End(),Phase(5,"Ended",-1));Reject(f,Done("9"),End(),Phase(5,"Ended",-1));
                Reject(f,Done(),End(),Phase(5,"Ended",-1,"Paused"));Reject(f,Done(),End(),Phase(5,"Ended",0));
            });
            suite.Run("multi attempt two rejects retry context mismatches",()=>{
                var bad=new Fixture(from:"Familiarization");Check.Throws<ArgumentException>(()=>bad.Writer());
                var initial=new Fixture(attempt:2,reason:VivePracticeEpochReasonV3.Initial,previous:null);Check.Throws<ArgumentException>(()=>initial.Writer());
            });
            suite.Run("multi attempt two boundary rejects later events and trailing bytes",()=>{
                var f=new Fixture();var w=f.Writer();Check.True(w.AppendTaskTransaction(f.Context,new[]{Done(),End(),Phase(5,"Ended",-1)}));
                Check.True(!w.AppendTaskTransaction(f.Context,new[]{Event(6,"stimulus_presented",-1,null,new Dictionary<string,string>())}));
                var w2=f.Writer();Check.True(w2.AppendTaskTransaction(f.Context,new[]{Done(),End(),Phase(5,"Ended",-1)}));Check.True(w2.Close());
                Check.Equal(VivePracticeMultiStatus.Invalid,Read(Append(w2.Snapshot(),new byte[]{1})).Status);
            });
            suite.Run("multi attempt two pause is a boundary and never continues old epoch",()=>{
                var f=new Fixture();var w=f.Writer();var pause=Event(3,"explicit_pause",-1,"session/p2/t0",new Dictionary<string,string>{{"actor","Participant"},{"active_seconds","0"}});
                Check.True(w.AppendTaskTransaction(f.Context,new[]{pause,Phase(4,"Paused",-1,trial:"session/p2/t0")}));
                Check.True(!w.AppendTaskTransaction(f.Context,new[]{Event(5,"explicit_resume",-1,null,new Dictionary<string,string>())}));
                var direct=f.Writer();Check.True(!direct.AppendTaskTransaction(f.Context,new[]{Event(3,"explicit_resume",-1,null,new Dictionary<string,string>())}));
                Reject(f,pause);Reject(f,Phase(3,"Paused",-1));Reject(f,Phase(3,"Practice",-1,"Ended"));Reject(f,Phase(3,"MainTask",-1));
            });
        }
    }
}
