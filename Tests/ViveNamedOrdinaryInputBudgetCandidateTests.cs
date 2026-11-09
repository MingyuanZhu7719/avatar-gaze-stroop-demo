using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveNamedOrdinaryInputBudgetCandidateTests
    {
        sealed class Clock:IClock {public double Now {get{return 0;}}public string ClockId {get{return "budget-clock";}}}
        sealed class ThrowingInputs:IReadOnlyList<string>
        {
            public int Count {get{throw new InvalidOperationException("synthetic count failure");}}
            public string this[int index] {get{return null;}}
            public IEnumerator<string> GetEnumerator(){throw new InvalidOperationException("synthetic enumeration failure");}
            IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
        }
        static Experiment Task(string session="budget-session",string intentId="budget-intent",string geometry="budget-geometry")
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,geometry);
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"budget-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,intentId,true,true);
            return new Experiment(SessionConfiguration.NamedRecordingDevelopment(session,11,plan,intent),new Clock());
        }
        static ViveNamedOrdinaryInputBudgetCandidate.Result Inspect(Experiment task,string[] values,bool safety=false,string format="VRAW0003")
        {return ViveNamedOrdinaryInputBudgetCandidate.Inspect(task,format,ViveNamedRecordingIntent.EventSchemaVersion,ViveNamedSimulationPlan.SchemaVersion,values,safety);}
        public static void Register(TestSuite suite)
        {
            suite.Run("ordinary input fit never grants a reservation or product authority",()=>{
                var task=Task();var before=task.Events.Count;
                var result=Inspect(task,new[]{"ordinary-input",new String('\u754c',10922)});
                Check.Equal(ViveNamedOrdinaryInputBudgetCandidate.Status.Unknown,result.State);
                Check.Equal("FUTURE_EVENT_COUNT_AND_PAYLOAD_NOT_PROVEN",result.Reason);
                Check.True(!result.AuthorizesAction&&!result.CapacityProven);Check.Equal(before,task.Events.Count);Check.Equal(Phase.Preparation,task.Phase);
            });
            suite.Run("ordinary input rejects oversize utf8 and malformed surrogate without truncating",()=>{
                var task=Task();int before=task.Events.Count;
                foreach(string text in new[]{new String('\u754c',10923),"\ud800"})
                {var result=Inspect(task,new[]{text});Check.Equal(ViveNamedOrdinaryInputBudgetCandidate.Status.RejectedInput,result.State);Check.Equal("ORDINARY_INPUT_TEXT_INVALID",result.Reason);Check.True(!result.AuthorizesAction);}
                Check.Equal(before,task.Events.Count);
            });
            suite.Run("ordinary input accepts exact byte boundary and null only as unproven",()=>{
                var task=Task();var builder=new StringBuilder();
                for(int i=0;i<8192;i++)builder.Append(char.ConvertFromUtf32(0x1F642));
                string exact=builder.ToString();Check.Equal(32768,Encoding.UTF8.GetByteCount(exact));
                var fit=Inspect(task,new[]{exact,null});Check.Equal(ViveNamedOrdinaryInputBudgetCandidate.Status.Unknown,fit.State);
                Check.Equal("FUTURE_EVENT_COUNT_AND_PAYLOAD_NOT_PROVEN",fit.Reason);Check.True(!fit.CapacityProven&&!fit.AuthorizesAction);
                var tooLarge=Inspect(task,new[]{exact+char.ConvertFromUtf32(0x1F642)});
                Check.Equal(ViveNamedOrdinaryInputBudgetCandidate.Status.RejectedInput,tooLarge.State);
            });
            suite.Run("ordinary input checks actual named configuration and latest event",()=>{
                foreach(var task in new[]{Task(session:new String('s',32769)),Task(intentId:new String('i',32769)),Task(geometry:new String('g',32769))})
                {var result=Inspect(task,new string[0]);Check.Equal(ViveNamedOrdinaryInputBudgetCandidate.Status.RejectedInput,result.State);Check.True(!result.AuthorizesAction);}
                var existing=Task();existing.BeginFamiliarization(Actor.Owner);existing.RecordPreparation(Actor.Owner,"field",new String('x',32769));
                var badEvent=Inspect(existing,new string[0]);Check.Equal(ViveNamedOrdinaryInputBudgetCandidate.Status.RejectedInput,badEvent.State);
                Check.Equal("CURRENT_EVENT_FIELDS_INVALID",badEvent.Reason);
            });
            suite.Run("ordinary input unsupported versions and missing inputs stay unknown",()=>{
                var task=Task();var unsupported=Inspect(task,new string[0],format:"VRAW0004");
                Check.Equal(ViveNamedOrdinaryInputBudgetCandidate.Status.Unknown,unsupported.State);Check.Equal("UNSUPPORTED_SCHEMA",unsupported.Reason);
                var missing=Inspect(task,null);Check.Equal(ViveNamedOrdinaryInputBudgetCandidate.Status.Unknown,missing.State);
                Check.True(!unsupported.CapacityProven&&!missing.AuthorizesAction);
            });
            suite.Run("safety action is not inspected or gated even with invalid inputs",()=>{
                var result=Inspect(null,new[]{new String('x',32769)},true,"unsupported");
                Check.Equal(ViveNamedOrdinaryInputBudgetCandidate.Status.SafetyNotGated,result.State);
                Check.Equal("SAFETY_NOT_INSPECTED_OR_BLOCKED",result.Reason);Check.True(!result.AuthorizesAction);
            });
            suite.Run("throwing prospective input list fails unknown without mutating task",()=>{
                var task=Task();int before=task.Events.Count;
                var result=ViveNamedOrdinaryInputBudgetCandidate.Inspect(task,"VRAW0003",ViveNamedRecordingIntent.EventSchemaVersion,
                    ViveNamedSimulationPlan.SchemaVersion,new ThrowingInputs(),false);
                Check.Equal(ViveNamedOrdinaryInputBudgetCandidate.Status.Unknown,result.State);
                Check.Equal("SNAPSHOT_OR_ENCODING_UNAVAILABLE",result.Reason);Check.True(!result.AuthorizesAction&&!result.CapacityProven);
                Check.Equal(before,task.Events.Count);Check.Equal(Phase.Preparation,task.Phase);
            });
        }
    }
}
