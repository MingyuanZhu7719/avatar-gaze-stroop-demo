using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class VisualSearchWozTaskTests
    {
        static VisualSearchTrial Layout(int size)
        {return VisualSearchPlans.MainList(77,0).First(x=>x.VisualSearch.SetSize==size).VisualSearch;}
        static TaskSnapshot Search(VisualSearchTrial layout,Condition condition=Condition.A,bool presented=true,int block=0,int segment=0)
        {return new TaskSnapshot("search-wire",block,segment,"trial",null,null,Phase.MainTask,condition,TaskKind.VisualSearch,ResponseRule.InkColor,layout,presented);}
        static PeerIdentity Peer(PeerRole role)
        {return new PeerIdentity("search-wire",role==PeerRole.Participant?"participant":"operator",role,
            role==PeerRole.Participant?"task-host":"operator-stream",ControlSource.LiveOperator,role==PeerRole.Participant?"p-clock":"o-clock");}
        static WozLanContext Context()
        {return new WozLanContext(Peer(PeerRole.Participant),Peer(PeerRole.Operator),new byte[16],"mapping","operator-device");}
        static WozAuthenticatedLanEndpoint Endpoint(PeerRole role)
        {return new WozAuthenticatedLanEndpoint(Context(),role,new byte[32],1);}
        static byte[] TaskPacket(TaskSnapshot task,long revision=1,long sequence=1)
        {
            using(var endpoint=Endpoint(PeerRole.Participant))
            {
                byte[] bytes;string error;var context=new WozLanTaskContext(task,revision,sequence,5);
                Check.True(endpoint.TryProtect(WirePacket.Task(Peer(PeerRole.Participant),sequence,5,task),context,null,out bytes,out error),error);return bytes;
            }
        }
        static byte[] PosePacket(TaskSnapshot task)
        {
            var sample=new HeadEyeSample("search-wire","operator-stream","operator-device","o-clock",task.BlockIndex,task.Segment,1,11.5,13,
                ControlSource.LiveOperator,StreamRole.OperatorControl,Rotation.Identity,new Vec3(0,0,1),true,true);
            using(var endpoint=Endpoint(PeerRole.Operator))
            {
                byte[] bytes;string error;
                Check.True(endpoint.TryProtect(WirePacket.Tracking(Peer(PeerRole.Operator),1,sample),new WozLanTaskContext(task,1,1,5),
                    new WozLanPoseObservation(11,12,12.1),out bytes,out error),error);return bytes;
            }
        }
        static VisualSearchTrial ChangedSameId(VisualSearchTrial layout)
        {
            var items=new List<VisualSearchItem>(layout.Items);int index=items.FindIndex(x=>x.Id!=layout.TargetId);
            var original=items[index];Colour different=original.Colour;
            for(int i=0;i<4;i++)if((Colour)i!=original.Colour&&(Colour)i!=layout.TargetColour){different=(Colour)i;break;}
            items[index]=new VisualSearchItem(original.Slot,different,original.Pattern);
            return new VisualSearchTrial(layout.LayoutId,layout.LayoutSeed,layout.FindItem(layout.TargetId).Slot,items);
        }
        static void InvalidToken(byte[] source,int field,string value)
        {
            var parts=Encoding.ASCII.GetString(source).Split('|');parts[field]=value;WirePacket decoded;string error;
            Check.True(!WireCodec.TryDecode(Encoding.ASCII.GetBytes(String.Join("|",parts)),out decoded,out error));Check.True(decoded==null);
        }
        sealed class Provider:IViveProEyeProvider {public ViveProEyePoll Poll(){return null;}}
        public static void Register(TestSuite suite)
        {
            suite.Run("WoZ wire v3 preserves frozen 12/24 layouts, cue stage and all A/B/C/D conditions",()=>{
                foreach(int size in new[]{12,24})foreach(Condition condition in Enum.GetValues(typeof(Condition)))foreach(bool presented in new[]{false,true})
                {
                    var task=Search(Layout(size),condition,presented,(int)condition);WirePacket decoded;string error;
                    var bytes=WireCodec.Encode(WirePacket.Task(Peer(PeerRole.Participant),1,5,task));
                    Check.True(bytes.Length<=WireCodec.MaxPacketBytes);Check.True(WireCodec.TryDecode(bytes,out decoded,out error),error);
                    Check.True(TaskSnapshot.Same(task,decoded.Snapshot));Check.Equal(size,decoded.Snapshot.VisualSearch.SetSize);
                    Check.Equal(task.VisualSearch.Serialize(),decoded.Snapshot.VisualSearch.Serialize());
                    var view=ViveWozOperatorTaskView.FromFreshTask(decoded.Snapshot);
                    Check.Equal(TaskKind.VisualSearch,view.TaskKind);Check.True(view.Stimulus.IsVisualSearch);
                    Check.Equal(task.VisualSearch.PlanIdentity,view.VisualSearch.PlanIdentity);Check.Equal(presented,view.VisualSearchArrayPresented);
                    Check.True(view.TutorialCorrect==null&&view.QuestionnaireItem==null&&view.TutorialFeedback==null);
                }
            });
            suite.Run("WoZ task projection carries Word meaning rule and clears search on phase exit",()=>{
                var meaning=new TaskSnapshot("search-wire",0,0,"trial",Colour.Red,Colour.Green,Phase.MainTask,Condition.B,TaskKind.Stroop,ResponseRule.WordMeaning);
                WirePacket decoded;string error;Check.True(WireCodec.TryDecode(WireCodec.Encode(WirePacket.Task(Peer(PeerRole.Participant),1,5,meaning)),out decoded,out error),error);
                Check.Equal(ResponseRule.WordMeaning,ViveWozOperatorTaskView.FromFreshTask(decoded.Snapshot).ResponseRule);
                foreach(var phase in new[]{Phase.BlockReady,Phase.Countdown,Phase.Questionnaire,Phase.Rest,Phase.Ended})
                {
                    var hidden=new TaskSnapshot("search-wire",0,0,null,null,null,phase,Condition.A,TaskKind.VisualSearch);
                    var view=ViveWozOperatorTaskView.FromFreshTask(hidden);
                    Check.True(!hidden.HasStimulus&&view.Stimulus==null&&view.VisualSearch==null&&!view.VisualSearchArrayPresented);
                }
                var clear=ViveWozOperatorTaskView.FromFreshTask(null);Check.True(clear.Stimulus==null&&clear.VisualSearch==null);
            });
            suite.Run("WoZ wire rejects old peers, wrong task tags and malformed frozen layouts",()=>{
                var bytes=WireCodec.Encode(WirePacket.Task(Peer(PeerRole.Participant),1,5,Search(Layout(24))));
                InvalidToken(bytes,1,"2");InvalidToken(bytes,18,"Stroop");InvalidToken(bytes,18,"Unknown");
                InvalidToken(bytes,19,"WordMeaning");InvalidToken(bytes,14,"Red");InvalidToken(bytes,20,"!!!!");InvalidToken(bytes,21,"true");
                InvalidToken(bytes,20,Convert.ToBase64String(Encoding.UTF8.GetBytes("vs1|bad|0|0|0:0:0")));
                InvalidToken(bytes,20,"~");InvalidToken(bytes,16,"Questionnaire");
                Check.Throws<ArgumentException>(()=>new TaskSnapshot("search-wire",0,0,null,null,null,Phase.MainTask,Condition.A,TaskKind.VisualSearch,ResponseRule.InkColor,Layout(12)));
            });
            suite.Run("WoZ authenticated task packets retain full search layout and reject replay and stale revision",()=>{
                using(var receiver=Endpoint(PeerRole.Operator))
                {
                    WozAuthenticatedLanEndpoint.VerifiedMessage message;string error;var task=Search(Layout(24));var bytes=TaskPacket(task,2,1);
                    Check.True(receiver.TryReceive(bytes,20,20.1,null,out message,out error),error);
                    Check.True(TaskSnapshot.Same(task,message.Packet.Snapshot));
                    Check.True(!receiver.TryReceive(bytes,20.1,20.2,null,out message,out error));Check.Equal("replay_or_remote_clock",error);
                    Check.True(!receiver.TryReceive(TaskPacket(task,1,2),20.2,20.3,null,out message,out error));Check.Equal("task_revision",error);
                    Check.True(!receiver.TryReceive(TaskPacket(Search(ChangedSameId(task.VisualSearch)),2,2),20.3,20.4,null,out message,out error));Check.Equal("task_revision",error);
                }
            });
            suite.Run("WoZ search pose proof binds layout content, cue stage, block and segment",()=>{
                var task=Search(Layout(24));var bytes=PosePacket(task);
                foreach(var changed in new[]{Search(ChangedSameId(task.VisualSearch)),Search(task.VisualSearch,presented:false),
                    Search(task.VisualSearch,block:1),Search(task.VisualSearch,segment:1)})
                    using(var receiver=Endpoint(PeerRole.Participant))
                    {
                        WozAuthenticatedLanEndpoint.VerifiedMessage message;string error;
                        Check.True(!TaskSnapshot.Same(task,changed));
                        Check.True(!receiver.TryReceive(bytes,20,20.1,new WozLanTaskContext(changed,1,1,5),out message,out error));
                        Check.Equal("unexpected_pose_task",error);Check.True(message==null);
                        Check.True(receiver.TryReceive(bytes,20,20.1,new WozLanTaskContext(task,1,1,5),out message,out error),error);
                    }
            });
            suite.Run("WoZ search onset advances display stage without replacing authority or allowing regression",()=>{
                var layout=Layout(24);var cue=Search(layout,presented:false);var array=Search(layout);
                Check.True(TaskSnapshot.SameAuthority(cue,array)&&!TaskSnapshot.Same(cue,array));
                using(var sender=Endpoint(PeerRole.Participant))using(var receiver=Endpoint(PeerRole.Operator))
                {
                    byte[] bytes;string error;WozAuthenticatedLanEndpoint.VerifiedMessage message;
                    foreach(var entry in new[]{new{Task=cue,Sequence=1L},new{Task=array,Sequence=2L}})
                    {
                        var context=new WozLanTaskContext(entry.Task,7,entry.Sequence,5);
                        Check.True(sender.TryProtect(WirePacket.Task(Peer(PeerRole.Participant),entry.Sequence,5,entry.Task),context,null,out bytes,out error),error);
                        Check.True(receiver.TryReceive(bytes,20,20.1,null,out message,out error),error);
                        Check.Equal(entry.Task.VisualSearchArrayPresented,message.Packet.Snapshot.VisualSearchArrayPresented);
                    }
                    foreach(long revision in new[]{7L,8L})
                    {
                        Check.True(!sender.TryProtect(WirePacket.Task(Peer(PeerRole.Participant),3,5,cue),new WozLanTaskContext(cue,revision,3,5),null,out bytes,out error));
                        Check.Equal("task_presentation_regressed",error);
                        // Independently-created sender gives valid authenticated
                        // bytes; receiver must enforce its own high-water stage.
                        Check.True(!receiver.TryReceive(TaskPacket(cue,revision,3),20.2,20.3,null,out message,out error));
                        Check.Equal("task_presentation_regressed",error);Check.True(message==null);
                    }
                    var changed=Search(ChangedSameId(layout));
                    Check.True(!sender.TryProtect(WirePacket.Task(Peer(PeerRole.Participant),3,5,changed),new WozLanTaskContext(changed,7,3,5),null,out bytes,out error));
                    Check.Equal("task_revision",error);
                }
            });
            suite.Run("WoZ search admits live source for A/B/C and rejects D or absent stimulus",()=>{
                foreach(var condition in new[]{Condition.A,Condition.B,Condition.C})
                {
                    var factory=new ViveWozOperatorSourceFactory("search-wire","epoch",new ManualClock(),new Provider(),new ViveProEyeRehearsalCoordinates(),.15,true);
                    Check.True(factory.Create(Search(Layout(12),condition,presented:false))!=null);
                    Check.True(factory.Create(Search(Layout(24),condition))!=null);
                    Check.True(PosePacket(Search(Layout(24),condition)).Length>0);
                }
                var other=new ViveWozOperatorSourceFactory("search-wire","epoch",new ManualClock(),new Provider(),new ViveProEyeRehearsalCoordinates(),.15,true);
                Check.Throws<InvalidOperationException>(()=>other.Create(Search(Layout(12),Condition.D)));
                Check.Throws<InvalidOperationException>(()=>other.Create(new TaskSnapshot("search-wire",0,0,"trial",null,null,Phase.MainTask,Condition.A,TaskKind.VisualSearch)));
            });
        }
    }
}
