using System;
using System.Collections.Generic;

namespace AvatarStroop.Core
{
    public sealed partial class Experiment
    {
        // Assembly-owned evidence only. This is never a network input API.
        // The runtime has already accepted the exact owned packet/scene proof.
        internal void RecordLegacyWozEvidence(IReadOnlyDictionary<string,string> fields)
        {
            if(Configuration.LegacyRecordingDesign==null||!Configuration.LegacyRecordingDesign.OperatorWozRequested||
                !Configuration.RawTaskRequested||!Configuration.RawEyeRequested||Phase==Phase.Ended)
                throw new InvalidOperationException("Explicit current local WoZ recording design required.");
            ViveLegacyWozRecordingSchema.Validate("woz_network_evidence",fields,clock.ClockId,Time(),true);
            var values=new List<string>();foreach(var field in fields){values.Add(field.Key);values.Add(field.Value);}
            Log("woz_network_evidence",values.ToArray());
        }
    }
}
