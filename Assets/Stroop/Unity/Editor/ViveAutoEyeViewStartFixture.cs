#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using UnityEngine;

namespace AvatarStroop.Unity.Editor
{
    // Exercise only the production lifecycle gate. No Unity scene, native
    // SDK, loader, worker, participant, calibration or file API is called.
    public static class ViveAutoEyeViewStartFixture
    {
        static int count;
        static void Check(bool pass,string message)
        {if(!pass)throw new InvalidOperationException("Automatic eye start gate: "+message);count++;}
        sealed class Gate
        {
            const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
            readonly Type type=typeof(ViveStroopPreviewRig).Assembly.GetType("AvatarStroop.Unity.ViveAutoEyeViewStart",true);
            readonly object state;
            internal Gate(){state=Activator.CreateInstance(type,true);}
            internal bool Pending{get{return (bool)type.GetProperty("Pending",Private).GetValue(state);}}
            internal bool Begin(){return (bool)Call("BeginExplicitView");}
            internal bool Claim(params object[] enabled){return (bool)Call("TryClaim",enabled);}
            internal bool Claim(){return Claim(true,true,true,true,true);}
            internal void Suppress(){Call("Suppress");}
            internal void End(){Call("EndView");}
            object Call(string name,params object[] args){return type.GetMethod(name,Private).Invoke(state,args);}
        }
        public static void Validate()
        {
            count=0;var gate=new Gate();
            Check(!gate.Pending&&!gate.Claim(),"worker could start without explicit classroom start");
            Check(gate.Begin()&&gate.Pending,"explicit view did not arm");
            Check(!gate.Begin()&&gate.Pending,"duplicate explicit start disturbed the still-pending view");
            for(int i=0;i<5;i++)
            {
                var enabled=new object[]{true,true,true,true,true};enabled[i]=false;
                Check(!gate.Claim(enabled)&&gate.Pending,"claim ignored blocked loader/head/focus/clock/service prerequisite at "+i);
            }
            int physicalAttempts=0;
            Action simulatedFailure=()=>{physicalAttempts++;throw new InvalidOperationException("simulated SDK start failure");};
            if(gate.Claim())try{simulatedFailure();}catch(InvalidOperationException){}
            Check(physicalAttempts==1&&!gate.Pending&&!gate.Claim()&&!gate.Begin(),"failed service start re-armed the same view");
            gate.End();Check(gate.Begin()&&gate.Pending,"new explicit view could not re-arm after shutdown");
            gate.Suppress();
            Check(!gate.Pending&&!gate.Begin()&&!gate.Claim(),"Stop or disable before native start re-armed the current view");
            gate.End();Check(gate.Begin()&&gate.Claim(),"new explicit view did not make one attempt");
            gate.Suppress();
            Check(!gate.Pending&&!gate.Claim(),"stopping an active service silently restarted it");
            Debug.Log("VIVE_AUTO_EYE_VIEW_START_SYNTHETIC_FIXTURE_PASSED: "+count+
                " checks; no installed SDK, headset, participant or files.");
        }
    }
}
#endif
