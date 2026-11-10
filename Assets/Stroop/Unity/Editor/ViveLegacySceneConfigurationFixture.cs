#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Core;

namespace AvatarStroop.Unity.Editor
{
    // Exercises the actual pure scene configuration helper, not a duplicate
    // configuration. No installed gate, task, device, file or source is opened.
    public static class ViveLegacySceneConfigurationFixture
    {
        static int assertions;
        static SessionConfiguration Call(string method,params object[] arguments)
        {
            var type=typeof(SessionConfiguration).Assembly.GetType("AvatarStroop.Unity.ViveLegacySceneConfiguration",true);
            var member=type.GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic);
            if(member==null)throw new InvalidOperationException("Missing scene configuration helper: "+method);
            return (SessionConfiguration)member.Invoke(null,arguments);
        }
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Legacy scene configuration: "+message);assertions++;}
        static void Reject(Action action,string message)
        {
            try{action();}
            catch(TargetInvocationException error)
            {if(error.InnerException is ArgumentException||error.InnerException is InvalidOperationException){assertions++;return;}throw;}
            throw new InvalidOperationException("Legacy scene configuration accepted "+message);
        }
        public static void Validate()
        {
            assertions=0;
            var preview=Call("CreatePreview","synthetic-legacy-scene");
            Require(preview.SessionId=="synthetic-legacy-scene"&&preview.Seed==20261028,"original session identity and seed");
            Require(preview.RunKind==RunKind.Simulation&&preview.QuestionnaireLanguage=="en","original simulation and language");
            Require(preview.Allocation.AllocationId=="development-fixture-BCAD","original allocation identity");
            var order=preview.Allocation.Order;var lists=preview.Allocation.ListByBlock;
            var expected=new[]{Condition.B,Condition.C,Condition.A,Condition.D};
            for(int i=0;i<4;i++){Require(order[i]==expected[i],"BCAD at block "+i);Require(lists[i]==i,"stimulus list at block "+i);}
            Require(preview.Allocation.BInitialTarget==GazeTarget.Participant,"B initially faces Participant");
            Require(preview.CountdownSeconds==3&&preview.MinimumRestSeconds==30&&preview.SkipFinalRest,"original countdown and rest timing");
            Require(preview.QuestionnaireInstructionSeconds==0&&preview.QuestionnaireInstructionRequiresAcknowledgement,"original questionnaire timing and acknowledgement");
            Require(preview.ParticipantPacedRest&&preview.RetainControlAcrossBlocks,"participant-paced rest and retained source");
            Require(!preview.IsNamedDesign&&preview.NamedPlan==null&&preview.NamedOption==null&&preview.NamedRecordingIntent==null,"preview is not a named alias");
            Require(preview.LegacyRecordingDesign==null&&preview.LegacyRecordingIntent==null,"preview has no recording intent");
            var recorded=Call("SelectRecording",preview,"synthetic-geometry","synthetic-mapping","synthetic-design","synthetic-intent",false);
            Require(!Object.ReferenceEquals(preview,recorded),"recording configuration is a copy");
            Require(!recorded.LegacyRecordingDesign.OperatorWozRequested,"original local mode remains Automatic");
            Reject(()=>Call("SelectRecording",preview,"synthetic-geometry","synthetic-mapping","synthetic-design","synthetic-intent",true),"WoZ recording gate remains closed in Editor");
            Require(Object.ReferenceEquals(preview.Allocation,recorded.Allocation),"same original immutable Allocation object");
            Require(Object.ReferenceEquals(recorded.Allocation,recorded.LegacyRecordingDesign.Allocation),"design binds that exact Allocation object");
            Require(recorded.LegacyRecordingDesign.DesignId=="synthetic-design"&&recorded.LegacyRecordingDesign.GeometryVersion=="synthetic-geometry"&&recorded.LegacyRecordingDesign.MappingVersion=="synthetic-mapping","explicit design identity and version binding");
            Require(recorded.LegacyRecordingIntent.IntentId=="synthetic-intent"&&recorded.LegacyRecordingIntent.TaskRequested&&recorded.LegacyRecordingIntent.EyeRequested,"explicit separate legacy task and eye intent");
            Require(!recorded.IsNamedDesign&&recorded.NamedPlan==null&&recorded.NamedOption==null&&recorded.NamedRecordingIntent==null,"recording is never a named alias");
            Require(recorded.SessionId==preview.SessionId&&recorded.Seed==preview.Seed&&recorded.RunKind==preview.RunKind&&recorded.QuestionnaireLanguage==preview.QuestionnaireLanguage,"identity and mode unchanged by recording wrapper");
            Require(recorded.CountdownSeconds==preview.CountdownSeconds&&recorded.MinimumRestSeconds==preview.MinimumRestSeconds&&recorded.SkipFinalRest==preview.SkipFinalRest,"all rest/countdown settings preserved");
            Require(recorded.QuestionnaireInstructionSeconds==preview.QuestionnaireInstructionSeconds&&recorded.QuestionnaireInstructionRequiresAcknowledgement==preview.QuestionnaireInstructionRequiresAcknowledgement,"all questionnaire timing preserved");
            Require(recorded.ParticipantPacedRest==preview.ParticipantPacedRest&&recorded.RetainControlAcrossBlocks==preview.RetainControlAcrossBlocks,"retained source and paced rest preserved");
            Require(preview.LegacyRecordingDesign==null&&preview.LegacyRecordingIntent==null,"original preview remains non-recording");
            order[0]=Condition.A;lists[0]=3;
            Require(recorded.Allocation.Order[0]==Condition.B&&recorded.Allocation.ListByBlock[0]==0,"public array copies cannot mutate bound allocation");
            Reject(()=>Call("SelectRecording",preview,"synthetic-geometry","synthetic-mapping","","intent",false),"missing design identity");
            Reject(()=>Call("SelectRecording",preview,"synthetic-geometry","synthetic-mapping","design","",false),"missing intent identity");
            Reject(()=>Call("SelectRecording",recorded,"synthetic-geometry","synthetic-mapping","design-2","intent-2",false),"already-recording configuration");
            var named=SessionConfiguration.NamedNoAvatarDevelopment("synthetic-named",1,ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,0,null));
            Reject(()=>Call("SelectRecording",named,"synthetic-geometry","synthetic-mapping","design","intent",false),"named configuration alias");
            Console.WriteLine("PASS "+assertions+" pure legacy scene configuration assertions. No task/device/recording or native execution.");
        }
    }
}
#endif
