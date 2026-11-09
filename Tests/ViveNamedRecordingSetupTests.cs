using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveNamedRecordingSetupTests
    {
        static readonly MonotonicClock SelectionClock=new MonotonicClock("synthetic-selection-clock");
        static ViveNamedRecordingSetup Prepare(string directory=@"C:\Existing",string budget="128")
        {string error;var value=ViveNamedRecordingSetup.TryPrepare(true,directory,budget,_=>true,out error);Check.True(value!=null&&error==null);return value;}
        static void Input(ViveNamedRecordingSetup setup,bool ready=true,bool visible=true,bool trigger=false,bool menu=false,bool trackpad=false,bool changed=false)
        {setup.Observe(ready,visible,trigger,menu,trackpad,changed,SelectionClock);}
        public static void Register(TestSuite suite)
        {
            suite.Run("recording setup closed gate avoids directory inspection",()=>{
                bool inspected=false;string error;
                var setup=ViveNamedRecordingSetup.TryPrepare(false,@"C:\Existing","128",_=>{inspected=true;return true;},out error);
                Check.True(setup==null&&!inspected&&!String.IsNullOrEmpty(error));
            });
            suite.Run("recording setup requires existing absolute local folder and explicit bounded capacity",()=>{
                string error;foreach(var path in new[]{"",@"C:relative",@"\\server\share",@"\rooted"})
                    Check.True(ViveNamedRecordingSetup.TryPrepare(true,path,"128",_=>true,out error)==null);
                Check.True(ViveNamedRecordingSetup.TryPrepare(true,@"C:\Missing","128",_=>false,out error)==null);
                foreach(var limit in new[]{"", "0", "-1", "8193", "999999999999999999999999"})
                    Check.True(ViveNamedRecordingSetup.TryPrepare(true,@"C:\Existing",limit,_=>true,out error)==null);
                Check.True(Prepare().Budget.MaximumBytes==128L*1048576L);
            });
            suite.Run("recording choices require two separate visible neutral participant inputs",()=>{
                var setup=Prepare();Input(setup,trigger:true);Input(setup,trigger:true);Check.Equal(ViveNamedRecordingSetup.Step.Task,setup.Current);
                Input(setup,visible:false);Input(setup,visible:false,trigger:true);Input(setup,trigger:true);Check.Equal(ViveNamedRecordingSetup.Step.Task,setup.Current);
                Input(setup);Input(setup,trigger:true);Check.Equal(ViveNamedRecordingSetup.Step.Eye,setup.Current);
                Input(setup,trigger:true);Check.True(!setup.BothSelected);Input(setup);Input(setup,trigger:true);Check.True(setup.BothSelected);
                double task,eye;Check.True(setup.TryGetObservedTimes(SelectionClock,out task,out eye)&&task>=0&&eye>=task);
                Check.True(!setup.TryGetObservedTimes(new MonotonicClock("other-selection-clock"),out task,out eye));
            });
            suite.Run("recording choices decline cancel tracking and device change clear selection",()=>{
                foreach(int mode in new[]{0,1,2})
                {
                    var setup=Prepare();Input(setup);Input(setup,trigger:true);
                    if(mode==0)Input(setup,ready:false);else if(mode==1)Input(setup,changed:true);else {Input(setup);Input(setup,trigger:true,menu:true);}
                    Check.True(!setup.BothSelected);
                    Check.Equal(mode==2?ViveNamedRecordingSetup.Step.Declined:ViveNamedRecordingSetup.Step.Task,setup.Current);
                }
                var confirmed=Prepare();Input(confirmed);Input(confirmed,trigger:true);Input(confirmed);Input(confirmed,trigger:true);Check.True(confirmed.BothSelected);
                confirmed.Cancel();Check.True(!confirmed.BothSelected&&confirmed.DirectoryPath==null&&confirmed.Budget==null);
                double task,eye;Check.True(!confirmed.TryGetObservedTimes(SelectionClock,out task,out eye));
                Input(confirmed);Input(confirmed,trigger:true);Check.Equal(ViveNamedRecordingSetup.Step.Cancelled,confirmed.Current);
                var lost=Prepare();Input(lost);Input(lost,trigger:true);Input(lost);Input(lost,trigger:true);Input(lost,ready:false);
                Check.True(!lost.BothSelected&&lost.Current==ViveNamedRecordingSetup.Step.Cancelled);
            });
            suite.Run("recording choice timing is cleared if the view clock changes",()=>{
                var setup=Prepare();Input(setup);Input(setup,trigger:true);Check.Equal(ViveNamedRecordingSetup.Step.Eye,setup.Current);
                var other=new MonotonicClock("other-selection-clock");
                setup.Observe(true,true,false,false,false,false,other);
                double task,eye;Check.Equal(ViveNamedRecordingSetup.Step.Task,setup.Current);
                Check.True(!setup.TryGetObservedTimes(SelectionClock,out task,out eye));
                setup.Observe(true,true,false,false,false,false,other);setup.Observe(true,true,true,false,false,false,other);
                setup.Observe(true,true,false,false,false,false,other);setup.Observe(true,true,true,false,false,false,other);
                Check.True(setup.TryGetObservedTimes(other,out task,out eye)&&eye>=task);
            });
            suite.Run("confirmed recording choices are invalidated by clock or device changes",()=>{
                foreach(int mode in new[]{0,1,2,3})
                {
                    var setup=Prepare();Input(setup);Input(setup,trigger:true);Input(setup);Input(setup,trigger:true);
                    var clock=mode==0?null:mode==1?new MonotonicClock("replaced"):SelectionClock;
                    setup.Observe(mode!=2,true,false,false,false,mode==3,clock);
                    double task,eye;Check.Equal(ViveNamedRecordingSetup.Step.Cancelled,setup.Current);
                    Check.True(!setup.TryGetObservedTimes(SelectionClock,out task,out eye));
                    Input(setup);Input(setup,trigger:true);Check.Equal(ViveNamedRecordingSetup.Step.Cancelled,setup.Current);
                }
            });
            suite.Run("recording start requires same-frame focused tracking and worker",()=>{
                var setup=Prepare();Input(setup);Input(setup,trigger:true);Input(setup);Input(setup,trigger:true);
                Check.True(setup.CanStart(true,true,true,true,21,21));
                Check.True(!setup.CanStart(false,true,true,true,21,21));
                Check.True(!setup.CanStart(true,false,true,true,21,21));
                Check.True(!setup.CanStart(true,true,false,true,21,21));
                Check.True(!setup.CanStart(true,true,true,false,21,21));
                Check.True(!setup.CanStart(true,true,true,true,20,21));
                setup.Cancel();Check.True(!setup.CanStart(true,true,true,true,21,21));
            });
        }
    }
}
