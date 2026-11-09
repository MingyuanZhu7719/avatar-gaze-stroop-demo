#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;

namespace AvatarStroop.Unity.Editor
{
    // Pure text/selection checks. No scene, GameObject, task, file, XR loader,
    // eye SDK or native recording factory is created or called. Reflection
    // preserves the normal Runtime/Editor assembly boundary.
    public static class ViveLegacyRecordingPresentationFixture
    {
        static int assertions;
        static Type ScopeType()
        {
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type=assembly.GetType("AvatarStroop.Unity.ViveStroopRecordingScopePresentation",false);
                if(type!=null)return type;
            }
            throw new InvalidOperationException("Legacy presentation helper is missing.");
        }
        static object Call(string method,params object[] arguments)
        {
            var member=ScopeType().GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic);
            if(member==null)throw new InvalidOperationException("Missing presentation method: "+method);
            return member.Invoke(null,arguments);
        }
        static string Text(string method,params object[] arguments){return (string)Call(method,arguments);}
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Legacy presentation check: "+message);assertions++;}
        public static void Validate()
        {
            assertions=0;
            foreach(bool controlled in new[]{false,true})
            foreach(bool legacy in new[]{false,true})
            for(int design=-1;design<=4;design++)
                Require((bool)Call("DesignAvailable",design,controlled,legacy)==(controlled&&(design==3||design==0&&legacy)),"both build gates and explicit supported design required");
            for(int selected=-1;selected<=4;selected++)
            for(int setup=-1;setup<=4;setup++)
                Require((bool)Call("SetupMatches",selected,setup)==((selected==0||selected==3)&&selected==setup),"setup is bound to exact supported design");
            foreach(int design in new[]{0,3})
            foreach(bool available in new[]{false,true})
            foreach(bool notice in new[]{false,true})
            foreach(bool task in new[]{false,true})
            foreach(bool eye in new[]{false,true})
                Require((bool)Call("SetupEligible",design,design,available,notice,task,eye)==(available&&notice&&task&&eye),"no choice, one choice, notice off or closed gate rejects configuration");
            Require(!(bool)Call("SetupEligible",0,3,true,true,true,true),"named choices cannot configure legacy");
            Require(!(bool)Call("SetupEligible",3,0,true,true,true,true),"legacy choices cannot configure named");
            var context=new object();var replacement=new object();
            Require((bool)Call("ExactContextMatches",context,context),"exact pending context retained");
            Require(!(bool)Call("ExactContextMatches",context,replacement),"replacement context rejected even with null named plan/intent");
            Require(!(bool)Call("ExactContextMatches",null,null),"two missing contexts never match");
            Require(!(bool)Call("ExactContextMatches",context,null)&&!(bool)Call("ExactContextMatches",null,context),"missing pending or current context rejected");
            foreach(bool legacy in new[]{false,true})
            foreach(bool namedRequested in new[]{false,true})
            foreach(bool legacyRequested in new[]{false,true})
                Require((bool)Call("KindMatches",legacy,namedRequested,legacyRequested)==(legacy?legacyRequested&&!namedRequested:namedRequested&&!legacyRequested),"entrypoint cannot cross or combine kinds");
            Require(Text("LegacySelectionNotice",false).Contains("recording/export unavailable"),"closed legacy gate stays visibly unavailable");
            Require(Text("LegacySelectionNotice",true).Contains("optional controlled recording")&&Text("LegacySelectionNotice",true).Contains("No recording has started"),"future capability does not claim active recording");
            Require(Text("BuildDesignScope",false,true)==Text("BuildScope",false),"legacy flag alone never advertises recording");
            Require(Text("BuildDesignScope",true,false)==Text("BuildScope",true),"closed legacy gate preserves named-only capability");
            Require(Text("BuildDesignScope",true,true).Contains("Preview configuration never records"),"open future gate keeps preview distinct");
            Require(Text("OwnerDesignHeading",true,false,false,true,true).Contains("OPTIONS"),"unconfigured available design offers a choice");
            Require(Text("OwnerDesignHeading",true,false,true,true,true).Contains("NO RECORDING / EXPORT"),"configured preview stays memory-only with future gate open");
            Require(Text("OwnerDesignHeading",true,true,true,true,true).Contains("LEGACY CONTROLLED RECORDING SELF-TEST"),"requested recording is not labelled no-files preview");
            for(int design=-1;design<=4;design++)
            {
                Require((bool)Call("IsLegacy",design,false,false)==(design==0),"explicit legacy selection only");
                Require((bool)Call("IsLegacy",design,true,false),"configured legacy survives stale selection");
                Require(!(bool)Call("IsLegacy",design,true,true),"named participant is never labelled legacy");
            }
            foreach(bool controlled in new[]{false,true})
            {
                string heading=Text("OwnerHeading",true,controlled);
                Require(heading.Contains("LEGACY PREVIEW")&&heading.Contains("NO RECORDING / EXPORT"),"legacy heading ignores build capability");
                Require(!Text("OwnerHeading",false,controlled).Contains("LEGACY PREVIEW"),"unselected heading is not an active legacy task");
                Require(Text("BuildScope",controlled).Contains("Legacy A/B/C/D has no recording or export"),"build scope discloses legacy limit");
            }
            string capability=Text("BuildScope",true);
            Require(capability.Contains("limited to Named explicit multi-block simulations")&&capability.Contains("separate task/eye choices and an explicit start"),"controlled scope is named and conditional");
            string unavailable=Text("BuildScope",false);
            Require(unavailable.Contains("Simulated development task")&&unavailable.Contains("Local VIVE recording is unavailable")&&unavailable.Contains("No two-PC network, formal study"),"ordinary build limitations retained");
            foreach(string phase in new[]{"Preparation","PracticeRetry","PracticeCorrection","Countdown","Questionnaire","Rest","Paused","NeedsProtocolDecision","Ended"})
            {
                string original="Safety and controls for "+phase;
                string controls=Text("Controls",true,original);
                Require(controls.StartsWith(original+"\n",StringComparison.Ordinal),"phase controls retained: "+phase);
                Require(controls.Contains("No eye/task files.")&&controls.Contains("memory only until close"),"memory limit in phase footer: "+phase);
                Require(Text("Controls",false,original)==original,"other designs retain controls: "+phase);
                Require(Text("Banner",true,original)=="LEGACY PREVIEW | RECORDING/EXPORT UNAVAILABLE","phase banner: "+phase);
                Require(Text("Banner",false,original)==original,"other designs retain banner: "+phase);
            }
            Require(Text("Controls",true,"")==Text("Controls",true,(object)null),"missing controls still show memory notice");
            Require(Text("Controls",false,(object)null)==null,"non-legacy null controls preserved");
            foreach(bool completed in new[]{false,true})
            {
                string ended=Text("LegacyEndMessage",completed);
                Require(ended.StartsWith(completed?"Legacy preview complete.":"Legacy preview ended.",StringComparison.Ordinal),"completion and other end states remain distinct");
                Require(ended.Contains("No eye or task files were saved.")&&ended.Contains("in memory only until close"),"end result makes no saved-result claim");
            }
            Console.WriteLine("PASS "+assertions+" pure legacy recording presentation assertions. No Unity/player/eye SDK/native execution.");
        }
    }
}
#endif
