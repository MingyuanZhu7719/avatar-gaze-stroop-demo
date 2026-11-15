using System;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveControllerReadinessDiagnosticTests
    {
        static ViveControllerActionDiagnostic Pose(int count=1,bool enabled=true)
        {return new ViveControllerActionDiagnostic(enabled,count,false,false);}
        static ViveControllerActionDiagnostic Action(int count=1,bool enabled=true,bool active=true)
        {return new ViveControllerActionDiagnostic(enabled,count,true,active);}
        static ViveControllerActionDiagnostic[] Healthy()
        {return new[]{Pose(),Pose(),Pose(),Pose(),Action(),Action(),Action(),Action()};}
        public static void Register(TestSuite suite)
        {
            suite.Run("focus recovery distinguishes inactive actions from binding and query faults",()=>{
                var states=Healthy();Check.True(!ViveControllerReadinessDiagnostic.OnlyActivationPending(states));
                states[4]=Action(1,true,false);Check.True(ViveControllerReadinessDiagnostic.OnlyActivationPending(states));
                foreach(int slot in new[]{0,3,5,7})
                {
                    var invalid=(ViveControllerActionDiagnostic[])states.Clone();
                    invalid[slot]=slot<4?Pose(0):Action(0);
                    Check.True(!ViveControllerReadinessDiagnostic.OnlyActivationPending(invalid));
                    invalid[slot]=new ViveControllerActionDiagnostic(false,0,slot>=4,false,true);
                    Check.True(!ViveControllerReadinessDiagnostic.OnlyActivationPending(invalid));
                }
                Check.True(!ViveControllerReadinessDiagnostic.OnlyActivationPending(null));
            });
            suite.Run("VIVE readiness mirrors exact binding and active requirements",()=>{
                Check.True(Pose().Ready);Check.True(Action().Ready);
                Check.True(!Pose(0).Ready);Check.True(!Pose(2).Ready);Check.True(!Pose(1,false).Ready);
                Check.True(!Action(0).Ready);Check.True(!Action(2).Ready);Check.True(!Action(1,false).Ready);Check.True(!Action(1,true,false).Ready);
                Check.True(!new ViveControllerActionDiagnostic(true,1,true,true,true).Ready);
            });
            suite.Run("VIVE readiness exposes all action failures for the selected hand",()=>{
                var states=Healthy();states[0]=Pose(0);states[1]=Pose(2);states[4]=Action(1,true,false);states[5]=Action(0);states[6]=Action(1,false);
                string text=ViveControllerReadinessDiagnostic.Bindings("Left",states);
                Check.True(text.Contains("Left VIVE input"));Check.True(text.Contains("position: 0 bindings"));
                Check.True(text.Contains("rotation: 2 bindings"));Check.True(text.Contains("trackpad: inactive"));
                Check.True(text.Contains("trackpad click: 0 bindings"));Check.True(text.Contains("trigger: disabled"));
                Check.True(text.Contains("tracked: ok"));Check.True(text.Contains("menu: ok"));
            });
            suite.Run("VIVE diagnostics do not assert physical disconnection or action activity for pose controls",()=>{
                var states=Healthy();states[3]=Pose(3);string text=ViveControllerReadinessDiagnostic.Bindings("Right",states);
                Check.True(text.Contains("Right VIVE input"));Check.True(text.Contains("tracking state: 3 bindings"));
                Check.True(!text.Contains("disconnected"));Check.True(!text.Contains("inactive"));
                Check.Throws<ArgumentException>(()=>ViveControllerReadinessDiagnostic.Bindings("Right",new ViveControllerActionDiagnostic[7]));
            });
            suite.Run("VIVE diagnostic preserves other action statuses when one query fails",()=>{
                var states=Healthy();states[4]=new ViveControllerActionDiagnostic(false,0,true,false,true);
                string text=ViveControllerReadinessDiagnostic.Bindings("Right",states);
                Check.True(text.Contains("trackpad: query error"));Check.True(text.Contains("trackpad click: ok"));Check.True(text.Contains("position: ok"));
            });
        }
    }
}
