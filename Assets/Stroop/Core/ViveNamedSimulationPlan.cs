using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AvatarStroop.Core
{
    // An explicitly selected development plan, not a protocol allocation or
    // evidence that any block has actually happened. Each block has its own
    // identity even if the owner deliberately selects the same option twice.
    public sealed class ViveNamedSimulationPlan
    {
        public const string SchemaVersion="vive-named-simulation-plan-v2";
        readonly ReadOnlyCollection<ViveNamedSimulationBlock> blocks;
        public string PlanId {get;private set;}
        public IReadOnlyList<ViveNamedSimulationBlock> Blocks {get{return blocks;}}
        ViveNamedSimulationPlan(string id,ViveNamedOptionProvenance[] selections)
        {
            PlanId=id;
            var copy=new ViveNamedSimulationBlock[selections.Length];
            for(int i=0;i<copy.Length;i++)copy[i]=new ViveNamedSimulationBlock(this,i,selections[i]);
            blocks=Array.AsReadOnly(copy);
        }
        public static ViveNamedSimulationPlan Select(Actor actor,string explicitPlanId,params ViveNamedOptionProvenance[] selections)
        {
            if(actor!=Actor.Owner)throw new UnauthorizedAccessException("Owner selection is required");
            if(String.IsNullOrWhiteSpace(explicitPlanId))throw new ArgumentException("Explicit plan identity is required", "explicitPlanId");
            if(selections==null||selections.Length<1||selections.Length>4)
                throw new ArgumentException("Explicitly select one to four blocks", "selections");
            var copy=(ViveNamedOptionProvenance[])selections.Clone();
            foreach(var selection in copy)
                if(selection==null||(selection.Option!=ViveNamedOption.Automatic&&selection.Option!=ViveNamedOption.NoAvatar))
                    throw new InvalidOperationException("Each block must use an available named simulation option");
            return new ViveNamedSimulationPlan(explicitPlanId,copy);
        }
    }

    // Plan + zero-based index is the identity. It does not mean the block ran.
    public sealed class ViveNamedSimulationBlock
    {
        public ViveNamedSimulationPlan Plan {get;private set;}
        public int Index {get;private set;}
        public ViveNamedOptionProvenance Selection {get;private set;}
        internal ViveNamedSimulationBlock(ViveNamedSimulationPlan plan,int index,ViveNamedOptionProvenance selection)
        {Plan=plan;Index=index;Selection=selection;}
    }
}
