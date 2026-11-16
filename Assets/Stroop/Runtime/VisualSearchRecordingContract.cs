using System;
using System.Collections.Generic;
using System.Globalization;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Compare the actual immutable stimulus at each recording boundary. A
    // search display is never represented by invented Stroop word/ink values.
    internal static class VisualSearchRecordingContract
    {
        static bool Field(IReadOnlyDictionary<string,string> fields,string key,string value)
        {string actual;return fields.TryGetValue(key,out actual)&&String.Equals(actual,value,StringComparison.Ordinal);}
        static bool SearchIdentity(IReadOnlyDictionary<string,string> fields,VisualSearchTrial search)
        {return Field(fields,"task_kind","VisualSearch")&&Field(fields,"search_schema",VisualSearchTrial.Schema)&&
            Field(fields,"layout_id",search.LayoutId)&&Field(fields,"set_size",search.SetSize.ToString(CultureInfo.InvariantCulture))&&
            Field(fields,"target_item",search.TargetId);}
        internal static bool MatchesPlanned(IReadOnlyDictionary<string,string> fields,Stimulus stimulus)
        {
            if(stimulus==null)return false;
            if(stimulus.IsVisualSearch)
                return SearchIdentity(fields,stimulus.VisualSearch)&&
                    Field(fields,"visual_search_layout",stimulus.VisualSearch.Serialize())&&
                    !fields.ContainsKey("planned_word")&&!fields.ContainsKey("planned_ink");
            return Field(fields,"planned_stimulus_schema","1")&&Field(fields,"planned_word",stimulus.Word.ToString())&&
                Field(fields,"planned_ink",stimulus.Ink.ToString());
        }
        internal static bool MatchesPresented(IReadOnlyDictionary<string,string> fields,Stimulus stimulus)
        {
            if(stimulus==null)return false;
            if(stimulus.IsVisualSearch)
                return SearchIdentity(fields,stimulus.VisualSearch)&&
                    !fields.ContainsKey("word")&&!fields.ContainsKey("ink");
            return Field(fields,"word",stimulus.Word.ToString())&&Field(fields,"ink",stimulus.Ink.ToString());
        }
    }
}
