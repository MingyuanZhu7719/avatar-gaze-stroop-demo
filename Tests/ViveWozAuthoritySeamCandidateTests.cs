using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic immutable mutation commits and source packets only; no actual
    // user choice, network, headset, task admission or production binding.
    public static class ViveWozAuthoritySeamCandidateTests
    {
        static ExperimentMutationSnapshot Snapshot(int block,Phase phase,ControlSelection? selection=ControlSelection.LiveWoz,string owner="owner",long choice=12)
        {
            var task=new TaskSnapshot("session",block,0,null,null,null,phase,Condition.B);
            return new ExperimentMutationSnapshot(task,selection,owner,choice,null,new object[]{block,phase,selection,owner,choice});
        }
        static ExperimentMutationCommit Commit(long revision,params ExperimentMutationSnapshot[] states)
        {
            var steps=new List<ExperimentMutationStep>();
            for(int i=1;i<states.Length;i++)steps.Add(new ExperimentMutationStep(i-1,states[i-1],states[i]));
            return new ExperimentMutationCommit(revision,"synthetic",steps.AsReadOnly(),states[states.Length-1],0);
        }
        sealed class Clock:IClock {public string ClockId {get;set;}public double Now {get;set;}}
        sealed class BrokenSteps:IReadOnlyList<ExperimentMutationStep>
        {
            public int Count {get{return 1;}} public ExperimentMutationStep this[int index] {get{throw new InvalidOperationException();}}
            public IEnumerator<ExperimentMutationStep> GetEnumerator(){throw new InvalidOperationException();}
            IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
        }
        sealed class Reader:IViveWozSourceStateReader
        {
            public TaskSnapshot Task=new TaskSnapshot("session",0,0,null,null,null,Phase.BlockReady,Condition.B);
            public Action BeforeRead;
            public ViveWozSourceLocalState ReadCurrent(){if(BeforeRead!=null)BeforeRead();return new ViveWozSourceLocalState("session","owner",Task,ControlSelection.LiveWoz,true,true,0);}
        }
        sealed class RemoteFixture
        {
            public readonly Clock Sender=new Clock{ClockId="sender",Now=3.6},Receiver=new Clock{ClockId="receiver",Now=3};
            public readonly ViveWozSourceOwner Owner;public readonly ViveWozSourceReceiver Gate;public readonly ViveWozRemoteDeclarationCandidate Remote;
            public readonly IPEndPoint Expected=new IPEndPoint(IPAddress.Loopback,23456);
            public readonly Reader Reader=new Reader();
            public RemoteFixture()
            {
                string stream=ViveDevelopmentTransport.OperatorStream("map","epoch");
                var context=new ViveWozSourceContext("session","owner",stream,"map","epoch","challenge","sender","receiver",0,0,Condition.B);
                var map=new ClockSynchronizer("receiver","sender",new DevelopmentClockConfiguration(3,.2,.2,4,200));
                var peer=new PeerIdentity("session","remote",PeerRole.Operator,stream,ControlSource.LiveOperator,"sender");
                for(int i=0;i<3;i++){string id="clock-"+i;double t=i;Check.True(map.RegisterRequest(id,t));
                    Check.True(map.ObserveReply(WirePacket.TimeReply(peer,i,t+.5,id,"receiver",t,t+.5),t));}
                Owner=new ViveWozSourceOwner(context,Sender,Reader);
                Gate=new ViveWozSourceReceiver(context,Receiver,map,Reader,.5,.1,true);
                Remote=new ViveWozRemoteDeclarationCandidate(Gate,Expected,true);
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("WOZ authority candidate requires first committed owner choice",()=>{
                var first=Snapshot(0,Phase.BlockReady);var local=new ViveWozLocalChoiceCandidate("session","owner",first,true);
                Check.Throws<InvalidOperationException>(()=>local.ConfirmOwnerChoice(Actor.Participant));
                Check.True(!local.LocalChoiceCandidate&&!local.RealUiBound&&!local.CanEnterCountdown&&!local.CanResume);
                local.ConfirmOwnerChoice(Actor.Owner);Check.True(local.LocalChoiceCandidate);Check.Equal((long?)12,local.ConfirmationSequence);
                Check.Throws<InvalidOperationException>(()=>local.ConfirmOwnerChoice(Actor.Owner));
            });
            suite.Run("WOZ local candidate retains one source choice across four blocks",()=>{
                var previous=Snapshot(0,Phase.BlockReady);var local=new ViveWozLocalChoiceCandidate("session","owner",previous,true);local.ConfirmOwnerChoice(Actor.Owner);
                long revision=0;
                for(int block=0;block<4;block++){
                    foreach(var phase in new[]{Phase.Countdown,Phase.MainTask,Phase.Questionnaire}){
                        var next=Snapshot(block,phase);revision++;Check.True(local.ObserveCommit(Commit(revision,previous,next),revision));
                        Check.True(local.LocalChoiceCandidate);Check.Equal((long?)12,local.ConfirmationSequence);previous=next;
                    }
                    if(block<3){
                        var rest=Snapshot(block,Phase.Rest);revision++;Check.True(local.ObserveCommit(Commit(revision,previous,rest),revision));
                        var next=Snapshot(block+1,Phase.BlockReady);revision++;Check.True(local.ObserveCommit(Commit(revision,rest,next),revision));previous=next;
                    }
                }
                var ended=Snapshot(3,Phase.Ended);revision++;Check.True(!local.ObserveCommit(Commit(revision,previous,ended),revision));
                Check.Equal(ViveLocalChoiceState.Ended,local.State);Check.True(!local.LocalChoiceCandidate);
            });
            suite.Run("WOZ local candidate with backlog cannot claim fresh choice",()=>{
                var a=Snapshot(0,Phase.BlockReady);var b=Snapshot(0,Phase.Countdown);var c=Snapshot(0,Phase.MainTask);
                var local=new ViveWozLocalChoiceCandidate("session","owner",a,true);local.ConfirmOwnerChoice(Actor.Owner);
                Check.True(!local.ObserveHead(2)&&!local.LocalChoiceCandidate);Check.Equal("pending_backlog",local.LastIssue);
                Check.True(!local.ObserveCommit(Commit(1,a,b),2)&&!local.LocalChoiceCandidate);
                Check.True(local.ObserveCommit(Commit(2,b,c),2)&&local.LocalChoiceCandidate);
            });
            suite.Run("WOZ local candidate catches a transient source loss inside one commit",()=>{
                var a=Snapshot(0,Phase.BlockReady);var lost=Snapshot(0,Phase.BlockReady,ControlSelection.Automatic);var local=new ViveWozLocalChoiceCandidate("session","owner",a,true);
                local.ConfirmOwnerChoice(Actor.Owner);Check.True(!local.ObserveCommit(Commit(1,a,lost,a),1));
                Check.Equal(ViveLocalChoiceState.Faulted,local.State);Check.Equal("intermediate_revocation_or_context_change",local.LastIssue);
            });
            suite.Run("WOZ owner revocation is terminal even with pending backlog",()=>{
                var a=Snapshot(0,Phase.BlockReady);var local=new ViveWozLocalChoiceCandidate("session","owner",a,true);local.ConfirmOwnerChoice(Actor.Owner);
                local.ObserveHead(1);local.RevokeOwnerChoice(Actor.Owner);Check.Equal(ViveLocalChoiceState.Revoked,local.State);
                Check.True(!local.LocalChoiceCandidate&&!local.ObserveCommit(Commit(1,a,Snapshot(0,Phase.Countdown)),1));
                local=new ViveWozLocalChoiceCandidate("session","owner",a,true);local.ObserveHead(1);local.RevokeOwnerChoice(Actor.Owner);
                Check.Equal(ViveLocalChoiceState.Revoked,local.State);Check.Throws<InvalidOperationException>(()=>local.ConfirmOwnerChoice(Actor.Owner));
            });
            suite.Run("WOZ local candidate rejects revision gaps replay and journal loss",()=>{
                var a=Snapshot(0,Phase.BlockReady);var b=Snapshot(0,Phase.Countdown);var local=new ViveWozLocalChoiceCandidate("session","owner",a,true);local.ConfirmOwnerChoice(Actor.Owner);
                Check.True(!local.ObserveCommit(Commit(2,a,b),2));Check.Equal(ViveLocalChoiceState.Faulted,local.State);
                local=new ViveWozLocalChoiceCandidate("session","owner",a,true);local.ConfirmOwnerChoice(Actor.Owner);local.SignalJournalLoss("backlog_full");
                Check.Equal(ViveLocalChoiceState.Faulted,local.State);Check.True(!local.LocalChoiceCandidate);
            });
            suite.Run("WOZ local candidate compares public fields as well as snapshot payload",()=>{
                var a=Snapshot(0,Phase.BlockReady);var conflictingTask=new TaskSnapshot("session",0,0,null,null,null,Phase.Countdown,Condition.B);
                var imposter=new ExperimentMutationSnapshot(conflictingTask,ControlSelection.LiveWoz,"owner",12,null,
                    new object[]{0,Phase.BlockReady,(ControlSelection?)ControlSelection.LiveWoz,"owner",12L});
                var local=new ViveWozLocalChoiceCandidate("session","owner",a,true);local.ConfirmOwnerChoice(Actor.Owner);
                Check.True(!local.ObserveCommit(Commit(1,imposter,Snapshot(0,Phase.MainTask)),1));
                Check.Equal("inconsistent_mutation_step",local.LastIssue);
            });
            suite.Run("WOZ local candidate latches mutation feed exceptions",()=>{
                var a=Snapshot(0,Phase.BlockReady);var local=new ViveWozLocalChoiceCandidate("session","owner",a,true);local.ConfirmOwnerChoice(Actor.Owner);
                var broken=new ExperimentMutationCommit(1,"synthetic",new BrokenSteps(),a,0);
                Check.True(!local.ObserveCommit(broken,1));Check.Equal("mutation_feed_exception",local.LastIssue);Check.True(!local.LocalChoiceCandidate);
            });
            suite.Run("WOZ remote declaration cannot be local authority or authentication",()=>{
                var f=new RemoteFixture();var packet=f.Owner.Confirm(true);f.Receiver.Now=3.2;
                Check.True(f.Remote.Observe(packet,f.Expected)&&f.Remote.RemoteDeclarationAvailable);
                Check.True(!typeof(ViveWozLocalChoiceCandidate).IsInstanceOfType(f.Remote));
                Check.True(!f.Remote.CryptographicallyAuthenticated&&!f.Remote.CanEnterCountdown&&!f.Remote.CanResume);
            });
            suite.Run("WOZ remote declaration rejects wrong endpoint replay and expiry",()=>{
                var f=new RemoteFixture();var packet=f.Owner.Confirm(true);f.Receiver.Now=3.2;
                Check.True(!f.Remote.Observe(packet,new IPEndPoint(IPAddress.Loopback,23457)));Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Remote.State);
                f=new RemoteFixture();packet=f.Owner.Confirm(true);f.Receiver.Now=3.2;Check.True(f.Remote.Observe(packet,f.Expected));
                Check.True(!f.Remote.Observe(packet,f.Expected));Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Remote.State);
                f=new RemoteFixture();packet=f.Owner.Confirm(true);f.Receiver.Now=3.2;Check.True(f.Remote.Observe(packet,f.Expected));
                f.Receiver.Now=3.7;Check.True(!f.Remote.RemoteDeclarationAvailable);Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Remote.State);
            });
            suite.Run("WOZ local and remote candidates latch cross-thread misuse",()=>{
                var a=Snapshot(0,Phase.BlockReady);var local=new ViveWozLocalChoiceCandidate("session","owner",a,true);local.ConfirmOwnerChoice(Actor.Owner);
                Exception error=null;var thread=new Thread(()=>{try{local.ObserveHead(0);}catch(Exception e){error=e;}});thread.Start();Check.True(thread.Join(3000));
                Check.True(error is InvalidOperationException);Check.Equal(ViveLocalChoiceState.Faulted,local.State);Check.True(!local.LocalChoiceCandidate);
                local=new ViveWozLocalChoiceCandidate("session","owner",a,true);local.ConfirmOwnerChoice(Actor.Owner);
                error=null;thread=new Thread(()=>{try{var unused=local.LocalChoiceCandidate;}catch(Exception e){error=e;}});thread.Start();Check.True(thread.Join(3000));
                Check.True(error is InvalidOperationException);Check.Equal(ViveLocalChoiceState.Faulted,local.State);Check.True(!local.LocalChoiceCandidate);
                var f=new RemoteFixture();var packet=f.Owner.Confirm(true);f.Receiver.Now=3.2;Check.True(f.Remote.Observe(packet,f.Expected));
                error=null;thread=new Thread(()=>{try{f.Remote.Observe(packet,f.Expected);}catch(Exception e){error=e;}});thread.Start();Check.True(thread.Join(3000));
                Check.True(error is InvalidOperationException);Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Remote.State);Check.True(!f.Remote.RemoteDeclarationAvailable);
                Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);Check.True(f.Gate.MustClearLivePose&&f.Gate.MustPauseLiveTask);
            });
            suite.Run("WOZ local and remote loss remains terminal when reason is missing",()=>{
                foreach(string reason in new string[]{null,""," "})
                {
                    var initial=Snapshot(0,Phase.BlockReady);var local=new ViveWozLocalChoiceCandidate("session","owner",initial,true);
                    local.ConfirmOwnerChoice(Actor.Owner);Check.True(local.LocalChoiceCandidate);
                    Check.Throws<ArgumentException>(()=>local.SignalJournalLoss(reason));
                    Check.Equal(ViveLocalChoiceState.Faulted,local.State);Check.True(!local.LocalChoiceCandidate);
                    var f=new RemoteFixture();var packet=f.Owner.Confirm(true);f.Receiver.Now=3.2;
                    Check.True(f.Remote.Observe(packet,f.Expected)&&f.Remote.RemoteDeclarationAvailable);
                    Check.Throws<ArgumentException>(()=>f.Remote.SignalLoss(reason));
                    Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Remote.State);Check.True(!f.Remote.RemoteDeclarationAvailable);
                    Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);Check.True(f.Gate.MustClearLivePose&&f.Gate.MustPauseLiveTask&&!f.Gate.Candidate());
                    Check.Equal("receiver_external_loss",f.Gate.LastIssue);f.Remote.SignalLoss("later-loss");Check.Equal("receiver_external_loss",f.Gate.LastIssue);
                }
                var normal=new RemoteFixture();var normalPacket=normal.Owner.Confirm(true);normal.Receiver.Now=3.2;
                Check.True(normal.Remote.Observe(normalPacket,normal.Expected));normal.Remote.SignalLoss("synthetic-disconnect");
                Check.Equal(ViveWozSourceEvidenceState.Faulted,normal.Remote.State);
                Check.Equal(ViveWozSourceEvidenceState.Faulted,normal.Gate.State);Check.True(normal.Gate.MustClearLivePose&&normal.Gate.MustPauseLiveTask);
                Check.Equal("loss:synthetic-disconnect",normal.Gate.LastIssue);
                normal.Remote.SignalLoss("later-loss");Check.Equal("loss:synthetic-disconnect",normal.Gate.LastIssue);
                var prior=new RemoteFixture();prior.Gate.SignalLoss("first-loss");prior.Remote.SignalLoss("later-loss");
                Check.Equal("loss:first-loss",prior.Gate.LastIssue);
            });
            suite.Run("WOZ remote declaration rechecks loss signalled during source callbacks",()=>{
                var f=new RemoteFixture();var packet=f.Owner.Confirm(true);Exception error=null;
                f.Reader.BeforeRead=()=>{var thread=new Thread(()=>{try{f.Remote.SignalLoss("synthetic");}catch(Exception e){error=e;}});
                    thread.Start();Check.True(thread.Join(3000));};
                f.Receiver.Now=3.2;Check.True(!f.Remote.Observe(packet,f.Expected));Check.True(error is InvalidOperationException);
                Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Remote.State);Check.True(!f.Remote.RemoteDeclarationAvailable);
                Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);Check.True(f.Gate.MustClearLivePose&&f.Gate.MustPauseLiveTask);
                f=new RemoteFixture();packet=f.Owner.Confirm(true);f.Receiver.Now=3.2;Check.True(f.Remote.Observe(packet,f.Expected));error=null;
                f.Reader.BeforeRead=()=>{var thread=new Thread(()=>{try{f.Remote.SignalLoss("synthetic");}catch(Exception e){error=e;}});
                    thread.Start();Check.True(thread.Join(3000));};
                Check.True(!f.Remote.RemoteDeclarationAvailable);Check.True(error is InvalidOperationException);
                Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Remote.State);
                Check.Equal(ViveWozSourceEvidenceState.Faulted,f.Gate.State);Check.True(f.Gate.MustClearLivePose&&f.Gate.MustPauseLiveTask);
            });
        }
    }
}

