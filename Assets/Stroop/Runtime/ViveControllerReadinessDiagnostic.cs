using System;
using System.Text;

namespace AvatarStroop.Runtime
{
    // A display-only snapshot; it does not decide whether input is accepted.
    // Control counts come from Unity bindings, not a physical-device inventory.
    internal struct ViveControllerActionDiagnostic
    {
        internal readonly bool Enabled, RequiresActive, IsActive, QueryFailed;
        internal readonly int ControlCount;
        internal ViveControllerActionDiagnostic(bool enabled,int count,bool requiresActive,bool isActive,bool queryFailed=false)
        { Enabled=enabled;ControlCount=count;RequiresActive=requiresActive;IsActive=isActive;QueryFailed=queryFailed; }
        internal bool Ready { get { return !QueryFailed&&Enabled&&ControlCount==1&&(!RequiresActive||IsActive); } }
        internal string Display
        {
            get
            {
                if(QueryFailed)return "query error";
                if(!Enabled)return "disabled";
                if(ControlCount!=1)return ControlCount+" bindings";
                return RequiresActive&&!IsActive?"inactive":"ok";
            }
        }
    }

    internal static class ViveControllerReadinessDiagnostic
    {
        static readonly string[] Names={"position","rotation","tracked","tracking state","trackpad","trackpad click","trigger","menu"};
        internal static string Bindings(string hand,ViveControllerActionDiagnostic[] states)
        {
            if(states==null||states.Length!=Names.Length)throw new ArgumentException("Eight action states are required.","states");
            var text=new StringBuilder();text.Append(hand).Append(" VIVE input | ");
            for(int i=0;i<states.Length;i++)
            {
                if(i!=0)text.Append("; ");
                text.Append(Names[i]).Append(": ").Append(states[i].Display);
            }
            return text.ToString();
        }
    }
}
