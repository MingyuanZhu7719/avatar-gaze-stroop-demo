using System;
using System.Collections.Generic;
using System.Text;

namespace AvatarStroop.Runtime
{
    // A selected-source declaration audit, never an authoritative session
    // receipt. Fingerprints are for comparing retained declared fields only;
    // they neither authenticate the source nor prove all fields are equal.
    internal static class ViveSelectedSessionIntegrity
    {
        internal static readonly string[] NamedKeys={"named_schema","named_option","named_schedule","named_geometry",
            "named_source","named_avatar_aoi","named_participant_eye","named_scope"};
        internal static string Render(IReadOnlyList<ViveCoverageSource> sources)
        {
            var output=new StringBuilder();
            output.AppendLine("SELECTED-SOURCE DECLARATION AUDIT (not an authoritative manifest)");
            output.AppendLine("Only selected, explicitly read verified prefixes are represented. Source identity, inventory, consent, disk durability, device timing and scientific validity are not authenticated.");
            output.AppendLine("Marker absence is unobserved, not proof that an event did not happen. Withdrawal may be absent from an unclean prefix.");
            var first=new Dictionary<long,ViveCoverageTaskMarker>();int same=0,different=0,indeterminate=0,closed=0,prefix=0,created=0,createdAtZero=0;
            int complete=0,withdrawn=0,technical=0,practiceFailed=0,unknownTerminal=0;
            for(int sourceIndex=0;sourceIndex<sources.Count;sourceIndex++)
            {
                var source=sources[sourceIndex];if(source.Status==ViveDevelopmentJournalReadStatus.CleanClose)closed++;else prefix++;
                output.Append("Source ").Append(sourceIndex+1).Append(" decoded markers: ").Append(source.Markers.Count)
                    .Append("; structural status ").Append(source.Status).Append("; verified bytes ").Append(source.VerifiedBytes).AppendLine(".");
                output.AppendLine("  Named fields among decoded markers: nonempty/empty/null/missing counts (not value or version validation):");
                for(int key=0;key<NamedKeys.Length;key++)
                    output.Append("  ").Append(NamedKeys[key]).Append(": ").Append(source.NamedPresent[key]).Append('/')
                        .Append(source.NamedEmpty[key]).Append('/').Append(source.NamedNull[key]).Append('/').Append(source.NamedMissing[key]).AppendLine();
                foreach(var marker in source.Markers)
                {
                    ViveCoverageTaskMarker previous;
                    if(!first.TryGetValue(marker.Sequence,out previous))first.Add(marker.Sequence,marker);
                    else if(!previous.ComparedFieldsPresent||!marker.ComparedFieldsPresent)indeterminate++;
                    else if(previous.RetainedDeclarationFingerprint==marker.RetainedDeclarationFingerprint)same++;else different++;
                    if(marker.Kind=="session_created"){created++;if(marker.Sequence==0)createdAtZero++;}
                    if(marker.TerminalCategory=="Completed")complete++;
                    else if(marker.TerminalCategory=="Withdrawn")withdrawn++;
                    else if(marker.TerminalCategory=="TechnicalEnd")technical++;
                    else if(marker.TerminalCategory=="PracticeFailed")practiceFailed++;
                    else if(marker.TerminalCategory=="Unknown")unknownTerminal++;
                }
            }
            output.Append("Selected source structural statuses: clean closes ").Append(closed).Append("; incomplete/invalid prefixes ").Append(prefix).AppendLine(". A clean close is not durable storage or session completion.");
            output.Append("Additional repeated task sequences with matching compared fingerprints: ").Append(same)
                .Append("; with differing compared fingerprints: ").Append(different)
                .Append("; indeterminate because compared fields are missing: ").Append(indeterminate).AppendLine(".");
            output.AppendLine("Each additional occurrence is compared only to the first selected occurrence of that sequence; this is not a pairwise or distinct-value count.");
            output.AppendLine("Comparison covers only AE2 payload, declared block, segment, trial ID and local observation time; other fields are not compared. Matching hashes do not authenticate identity or prove every field equal.");
            output.Append("session_created markers observed: ").Append(created).Append("; at sequence zero: ").Append(createdAtZero).AppendLine(". Counts include repeated selected sources.");
            output.Append("session_ended reason markers observed: Completed ").Append(complete).Append("; Withdrawn ").Append(withdrawn)
                .Append("; TechnicalEnd ").Append(technical).Append("; PracticeFailed ").Append(practiceFailed).Append("; other/missing ").Append(unknownTerminal).AppendLine(". Counts do not prove actual completion or absence of withdrawal.");
            output.AppendLine("Complete session, all trials/responses, practice coverage, continuous gaze, cross-device alignment and durable/authenticated publication remain UNKNOWN.");
            return output.ToString();
        }
    }
}
