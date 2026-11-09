#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace AvatarStroop.Unity.Editor
{
    // Inspects the real compiled owner-panel binding without constructing a
    // MonoBehaviour, opening a scene, or invoking any task/device/file method.
    public static class ViveEvidencePanelIsolationFixture
    {
        static MethodInfo Method(Type type,string name)
        {
            var method=type.GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            if(method==null)throw new InvalidOperationException("Missing panel method: "+name);
            return method;
        }
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Evidence panel isolation: "+message);}
        static List<MemberInfo> Members(MethodInfo method)
        {
            var codes=new Dictionary<short,OpCode>();
            foreach(var field in typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static))
                if(field.FieldType==typeof(OpCode)){var code=(OpCode)field.GetValue(null);codes.Add(code.Value,code);}
            var body=method.GetMethodBody();Require(body!=null,"method body required");
            var bytes=body.GetILAsByteArray();var members=new List<MemberInfo>();
            for(int at=0;at<bytes.Length;)
            {
                short value=bytes[at++];
                if(value==0xfe)value=unchecked((short)(0xfe00|bytes[at++]));
                OpCode code;Require(codes.TryGetValue(value,out code),"unknown IL operation");
                int size;
                switch(code.OperandType)
                {
                    case OperandType.InlineNone:size=0;break;
                    case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:case OperandType.ShortInlineVar:size=1;break;
                    case OperandType.InlineVar:size=2;break;
                    case OperandType.InlineI8:case OperandType.InlineR:size=8;break;
                    case OperandType.InlineSwitch:size=checked(4+4*BitConverter.ToInt32(bytes,at));break;
                    default:size=4;break;
                }
                Require(size>=0&&at<=bytes.Length-size,"invalid IL operand");
                if(code.OperandType==OperandType.InlineMethod)
                    members.Add(method.Module.ResolveMethod(BitConverter.ToInt32(bytes,at)));
                if(code==OpCodes.Stfld||code==OpCodes.Stsfld)
                    members.Add(method.Module.ResolveField(BitConverter.ToInt32(bytes,at)));
                at+=size;
            }
            return members;
        }
        static int Calls(List<MemberInfo> members,Type owner,string name)
        {int count=0;foreach(var member in members)if(member is MethodBase&&member.DeclaringType==owner&&member.Name==name)count++;return count;}
        public static void ValidateBindings(Type bootstrap)
        {
            var draw=Members(Method(bootstrap,"DrawOwnerControls"));
            var journal=Members(Method(bootstrap,"ClearJournalEvidenceSummary"));
            var privacy=Members(Method(bootstrap,"ClearSensitiveEvidence"));
            Require(Calls(draw,bootstrap,"ClearJournalEvidenceSummary")==2,"both journal-summary branches must clear only journal evidence");
            Require(Calls(draw,bootstrap,"ClearSensitiveEvidence")==0,"journal UI must not clear independent task memory");
            Require(Calls(journal,bootstrap,"ClearNamedTaskView")==0,"journal-only clear must not call task clear");
            foreach(var member in journal)
                Require(!(member is FieldInfo&&member.DeclaringType==bootstrap&&member.Name.StartsWith("namedTask",StringComparison.Ordinal)),"journal-only clear must not overwrite task fields");
            Require(Calls(privacy,bootstrap,"ClearJournalEvidenceSummary")==1&&Calls(privacy,bootstrap,"ClearNamedTaskView")==1,"full privacy clear must still clear both panels");
            Console.WriteLine("PASS compiled DrawOwnerControls and privacy-clear isolation bindings.");
        }
        public static void Validate()
        {
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type=assembly.GetType("AvatarStroop.Unity.ViveStroopDevelopmentBootstrap",false);
                if(type!=null){ValidateBindings(type);return;}
            }
            throw new InvalidOperationException("Real VIVE bootstrap assembly is not loaded.");
        }
    }
}
#endif
