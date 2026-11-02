using System;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class PreviewRestartWorkflowTests
    {
        static OwnerCommand Command(int id=1,OwnerCommandKind kind=OwnerCommandKind.RestartPreview)
        {return new OwnerCommand(id.ToString("x32"),kind);}
        static OwnerAcknowledgement Acknowledgement(OwnerCommand command,bool accepted=true,string reason="technical_end_recorded")
        {return new OwnerAcknowledgement(command.RequestId,accepted,reason,OwnerCommandCodec.Hash(command));}
        static PreviewRestartWorkflow Begun()
        {
            var workflow=new PreviewRestartWorkflow();
            Check.True(workflow.TryBegin(Command(),true,false,0));
            return workflow;
        }
        static PreviewRestartWorkflow Closing()
        {
            var workflow=Begun();
            Check.True(workflow.ObserveAcknowledgement(Acknowledgement(Command()),1));
            Check.True(workflow.TryRequestClose(2));
            return workflow;
        }
        static PreviewRestartWorkflow Verifying()
        {
            var workflow=Closing();
            workflow.ObserveExit(true,true,true,true,3);
            Check.Equal(PreviewRestartStage.Verifying,workflow.Stage);
            return workflow;
        }

        public static void Register(TestSuite suite)
        {
            suite.Run("Preview restart requires a fresh participant and no pending owner command",()=>{
                var workflow=new PreviewRestartWorkflow();var command=Command();
                Check.Equal(PreviewRestartStage.Idle,workflow.Stage);Check.True(!workflow.BlocksActions);
                Check.True(!workflow.TryBegin(command,false,false,0));
                Check.True(!workflow.TryBegin(command,true,true,0));
                Check.True(!workflow.TryBegin(command,false,true,0));
                Check.Equal(PreviewRestartStage.Idle,workflow.Stage);Check.True(!workflow.BlocksActions);
                Check.True(workflow.TryBegin(command,true,false,0));
                Check.Equal(PreviewRestartStage.AwaitingAcknowledgement,workflow.Stage);Check.True(workflow.BlocksActions);
                Check.Equal(command.RequestId,workflow.RequestId);Check.Equal(OwnerCommandCodec.Hash(command),workflow.CommandHash);
                Check.True(!workflow.TryBegin(Command(2),true,false,1));
                Check.Equal(command.RequestId,workflow.RequestId);
            });
            suite.Run("Preview restart refuses an unrelated owner command",()=>{
                var workflow=new PreviewRestartWorkflow();
                Check.Throws<ArgumentException>(()=>workflow.TryBegin(Command(1,OwnerCommandKind.Practice),true,false,0));
                Check.Equal(PreviewRestartStage.Idle,workflow.Stage);Check.True(!workflow.BlocksActions);
            });
            suite.Run("Preview restart ignores null unrelated and conflicting acknowledgements",()=>{
                var workflow=Begun();var command=Command();var other=Command(2);
                Check.True(!workflow.ObserveAcknowledgement(null,1));
                Check.True(!workflow.ObserveAcknowledgement(new OwnerAcknowledgement(null,false,"unidentified_rejection",null),2));
                Check.True(!workflow.ObserveAcknowledgement(new OwnerAcknowledgement(other.RequestId,true,"wrong_request",OwnerCommandCodec.Hash(command)),3));
                Check.True(!workflow.ObserveAcknowledgement(new OwnerAcknowledgement(command.RequestId,true,"wrong_hash",OwnerCommandCodec.Hash(other)),4));
                Check.True(!workflow.ObserveAcknowledgement(Acknowledgement(other,false,"unrelated_rejection"),5));
                Check.Equal(PreviewRestartStage.AwaitingAcknowledgement,workflow.Stage);Check.True(workflow.BlocksActions);
                Check.True(workflow.ObserveAcknowledgement(Acknowledgement(command),6));
                Check.Equal(PreviewRestartStage.CloseReady,workflow.Stage);
            });
            suite.Run("Preview restart cannot close verify or launch before the exact positive acknowledgement",()=>{
                var workflow=Begun();
                Check.True(!workflow.TryRequestClose(1));
                workflow.ObserveExit(true,true,true,true,2);
                workflow.ObserveVerification(true,null,3);
                Check.True(!workflow.TryClaimLaunch(4));
                Check.Throws<InvalidOperationException>(()=>workflow.CompleteLaunch());
                Check.Equal(PreviewRestartStage.AwaitingAcknowledgement,workflow.Stage);
            });
            suite.Run("Preview restart matching rejection blocks close and retains its request identity",()=>{
                var workflow=Begun();var command=Command();
                workflow.ObserveAcknowledgement(Acknowledgement(command,false,"technical_end_failed"),1);
                Check.Equal(PreviewRestartStage.Blocked,workflow.Stage);Check.True(workflow.BlocksActions);
                Check.True(!String.IsNullOrWhiteSpace(workflow.FailureReason));
                string failure=workflow.FailureReason;
                Check.True(!workflow.ObserveAcknowledgement(Acknowledgement(command),2));
                Check.True(!workflow.TryRequestClose(3));Check.True(!workflow.TryClaimLaunch(4));
                Check.Equal(command.RequestId,workflow.RequestId);Check.Equal(OwnerCommandCodec.Hash(command),workflow.CommandHash);
                Check.Equal(failure,workflow.FailureReason);
            });
            suite.Run("Preview restart lost acknowledgement times out at the deadline and late success cannot revive it",()=>{
                var workflow=Begun();
                workflow.Poll(14.999);Check.Equal(PreviewRestartStage.AwaitingAcknowledgement,workflow.Stage);
                workflow.Poll(15);Check.Equal(PreviewRestartStage.Blocked,workflow.Stage);
                Check.True(!String.IsNullOrWhiteSpace(workflow.FailureReason));string failure=workflow.FailureReason;
                workflow.Poll(15);workflow.Poll(16);
                Check.True(!workflow.ObserveAcknowledgement(Acknowledgement(Command()),17));
                Check.True(!workflow.TryRequestClose(18));Check.True(!workflow.TryBegin(Command(2),true,false,19));
                Check.Equal(PreviewRestartStage.Blocked,workflow.Stage);Check.Equal(failure,workflow.FailureReason);
                Check.Equal(Command().RequestId,workflow.RequestId);
            });
            suite.Run("Preview restart late acknowledgement checks its deadline even without an earlier poll",()=>{
                var workflow=Begun();
                Check.True(!workflow.ObserveAcknowledgement(Acknowledgement(Command()),15));
                Check.Equal(PreviewRestartStage.Blocked,workflow.Stage);Check.True(!workflow.TryRequestClose(16));
            });
            suite.Run("Preview restart close is claimed once and has its own timeout interval",()=>{
                var workflow=new PreviewRestartWorkflow(2,4);var command=Command();
                Check.True(workflow.TryBegin(command,true,false,10));
                Check.True(workflow.ObserveAcknowledgement(Acknowledgement(command),11.5));
                Check.True(workflow.TryRequestClose(11.5));Check.True(!workflow.TryRequestClose(11.5));
                Check.Equal(PreviewRestartStage.AwaitingExit,workflow.Stage);Check.True(workflow.BlocksActions);
                workflow.Poll(12);Check.Equal(PreviewRestartStage.AwaitingExit,workflow.Stage);
                workflow.Poll(15.499);Check.Equal(PreviewRestartStage.AwaitingExit,workflow.Stage);
                workflow.Poll(15.5);Check.Equal(PreviewRestartStage.Blocked,workflow.Stage);
            });
            suite.Run("Preview restart requires both process exits to be known and complete",()=>{
                var observations=new[]{
                    new[]{false,true,true,true},new[]{true,true,false,true},
                    new[]{true,false,true,true},new[]{true,true,true,false},
                    new[]{false,false,false,false},new[]{true,false,true,false}};
                foreach(var observation in observations)
                {
                    var workflow=Closing();
                    workflow.ObserveExit(observation[0],observation[1],observation[2],observation[3],3);
                    Check.Equal(PreviewRestartStage.AwaitingExit,workflow.Stage);
                    workflow.ObserveVerification(true,null,4);Check.True(!workflow.TryClaimLaunch(5));
                    Check.Equal(PreviewRestartStage.AwaitingExit,workflow.Stage);
                    workflow.ObserveExit(true,true,true,true,6);
                    Check.Equal(PreviewRestartStage.Verifying,workflow.Stage);Check.True(workflow.BlocksActions);
                    Check.True(!workflow.TryClaimLaunch(7));
                }
            });
            suite.Run("Preview restart late process exits cannot bypass the close timeout",()=>{
                var workflow=Closing();
                workflow.ObserveExit(true,true,true,false,16.999);
                Check.Equal(PreviewRestartStage.AwaitingExit,workflow.Stage);
                workflow.ObserveExit(true,true,true,true,17);
                Check.Equal(PreviewRestartStage.Blocked,workflow.Stage);string failure=workflow.FailureReason;
                workflow.ObserveExit(true,true,true,true,18);workflow.ObserveVerification(true,null,19);
                Check.True(!workflow.TryClaimLaunch(20));Check.Equal(failure,workflow.FailureReason);
            });
            suite.Run("Preview restart journal verification failure blocks launch permanently",()=>{
                var workflow=Verifying();
                workflow.ObserveVerification(false,"participant_journal_not_cleanly_closed",4);
                Check.Equal(PreviewRestartStage.Blocked,workflow.Stage);Check.True(workflow.BlocksActions);
                Check.True(!String.IsNullOrWhiteSpace(workflow.FailureReason));string failure=workflow.FailureReason;
                workflow.ObserveVerification(true,null,5);Check.True(!workflow.TryClaimLaunch(6));
                Check.Throws<InvalidOperationException>(()=>workflow.CompleteLaunch());
                Check.Equal(failure,workflow.FailureReason);
            });
            suite.Run("Preview restart launches once only after acknowledged end known exits and verified journals",()=>{
                var workflow=Verifying();
                Check.Throws<InvalidOperationException>(()=>workflow.CompleteLaunch());
                workflow.ObserveVerification(true,null,4);
                Check.Equal(PreviewRestartStage.LaunchReady,workflow.Stage);Check.True(workflow.BlocksActions);
                Check.True(workflow.TryClaimLaunch(5));Check.Equal(PreviewRestartStage.LaunchClaimed,workflow.Stage);
                Check.True(workflow.BlocksActions);Check.True(!workflow.TryClaimLaunch(5));
                workflow.CompleteLaunch();
                Check.Equal(PreviewRestartStage.Completed,workflow.Stage);Check.True(!workflow.BlocksActions);
                Check.True(!workflow.TryClaimLaunch(6));Check.True(!workflow.TryBegin(Command(2),true,false,7));
                Check.Throws<InvalidOperationException>(()=>workflow.CompleteLaunch());
                Check.Equal(Command().RequestId,workflow.RequestId);
            });
            suite.Run("Preview restart uncertain command write stops without retry and preserves the first reason",()=>{
                var workflow=Begun();workflow.Stop("command_write_outcome_uncertain");
                Check.Equal(PreviewRestartStage.Blocked,workflow.Stage);Check.True(workflow.BlocksActions);
                Check.Equal("command_write_outcome_uncertain",workflow.FailureReason);
                workflow.Stop("later_failure");workflow.Poll(20);
                Check.Equal("command_write_outcome_uncertain",workflow.FailureReason);
                Check.True(!workflow.ObserveAcknowledgement(Acknowledgement(Command()),21));
                Check.True(!workflow.TryRequestClose(22));Check.True(!workflow.TryBegin(Command(2),true,false,23));
                Check.Equal(Command().RequestId,workflow.RequestId);
            });
            suite.Run("Preview restart close failure stays blocked after later exit and verification reports",()=>{
                var workflow=Closing();workflow.Stop("operator_close_failed");
                workflow.ObserveExit(true,true,true,true,3);workflow.ObserveVerification(true,null,4);
                Check.True(!workflow.TryClaimLaunch(5));Check.Equal(PreviewRestartStage.Blocked,workflow.Stage);
                Check.Equal("operator_close_failed",workflow.FailureReason);Check.Equal(Command().RequestId,workflow.RequestId);
            });
            suite.Run("Preview restart stop does not block an idle or completed workflow",()=>{
                var idle=new PreviewRestartWorkflow();idle.Stop("nothing_started");
                Check.Equal(PreviewRestartStage.Idle,idle.Stage);Check.True(!idle.BlocksActions);
                var completed=Verifying();completed.ObserveVerification(true,null,4);
                Check.True(completed.TryClaimLaunch(5));completed.CompleteLaunch();completed.Stop("already_completed");
                Check.Equal(PreviewRestartStage.Completed,completed.Stage);Check.True(!completed.BlocksActions);
            });
            suite.Run("Preview restart rejects nonfinite or backward time without advancing state",()=>{
                var workflow=Begun();workflow.Poll(2);
                foreach(double invalid in new[]{Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,1d})
                    Check.Throws<ArgumentException>(()=>workflow.Poll(invalid));
                Check.Equal(PreviewRestartStage.AwaitingAcknowledgement,workflow.Stage);
                workflow.Poll(2);
                Check.True(workflow.ObserveAcknowledgement(Acknowledgement(Command()),3));
                Check.Equal(PreviewRestartStage.CloseReady,workflow.Stage);
            });
        }
    }
}
