using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A sealed, exact in-process binding, never a caller-created source or I/O
    // success certificate. Only the participant creates one from its immutable
    // configuration. Named and legacy branches are disjoint by construction.
    internal sealed class ViveRawSessionContext
    {
        internal readonly SessionConfiguration Configuration;
        internal readonly ViveNamedSimulationPlan Plan;
        internal readonly ViveNamedRecordingIntent Intent;
        internal readonly ViveLegacyRecordingDesign LegacyDesign;
        internal readonly ViveLegacyRecordingIntent LegacyIntent;
        internal bool IsLegacy {get{return LegacyDesign!=null;}}
        internal bool IsLegacyWoz {get{return IsLegacy&&LegacyDesign.OperatorWozRequested;}}
        internal bool TaskRequested {get{return IsLegacy?LegacyIntent.TaskRequested:Intent.TaskRequested;}}
        internal bool EyeRequested {get{return IsLegacy?LegacyIntent.EyeRequested:Intent.EyeRequested;}}
        internal string DesignId {get{return IsLegacy?LegacyDesign.DesignId:Plan.PlanId;}}
        internal string IntentId {get{return IsLegacy?LegacyIntent.IntentId:Intent.IntentId;}}
        ViveRawSessionContext(SessionConfiguration configuration)
        {
            Configuration=configuration;Plan=configuration.NamedPlan;Intent=configuration.NamedRecordingIntent;
            LegacyDesign=configuration.LegacyRecordingDesign;LegacyIntent=configuration.LegacyRecordingIntent;
        }
        internal static ViveRawSessionContext FromConfiguration(SessionConfiguration configuration)
        {
            if(configuration==null||configuration.RunKind!=RunKind.Simulation)throw new InvalidOperationException("Raw development configuration required.");
            bool named=configuration.NamedRecordingIntent!=null,legacy=configuration.LegacyRecordingIntent!=null;
            if(named==legacy)throw new InvalidOperationException("Exactly one recording design is required.");
            if(named&&(configuration.NamedPlan==null||configuration.LegacyRecordingDesign!=null))
                throw new InvalidOperationException("Named recording context is inconsistent.");
            if(legacy&&(configuration.IsNamedDesign||configuration.NamedPlan!=null||configuration.NamedRecordingIntent!=null||
                configuration.LegacyRecordingDesign==null||!Object.ReferenceEquals(configuration.Allocation,configuration.LegacyRecordingDesign.Allocation)))
                throw new InvalidOperationException("Legacy recording context is inconsistent.");
            return new ViveRawSessionContext(configuration);
        }
        internal bool MatchesEvent(JournalEvent item)
        {
            if(item==null||item.SessionId!=Configuration.SessionId)return false;
            if(IsLegacy)return item.NamedPlan==null&&item.NamedOption==null&&item.NamedBlock==null&&item.NamedRecordingIntent==null&&
                Object.ReferenceEquals(item.LegacyRecordingDesign,LegacyDesign)&&Object.ReferenceEquals(item.LegacyRecordingIntent,LegacyIntent);
            return item.LegacyRecordingDesign==null&&item.LegacyRecordingIntent==null&&
                Object.ReferenceEquals(item.NamedPlan,Plan)&&Object.ReferenceEquals(item.NamedRecordingIntent,Intent);
        }
    }
    internal static class ViveLegacyRawRecordingBuildPolicy
    {
        // Separate explicit build opt-in. The existing strict Windows/XR/
        // development policy is still necessary; no runtime override exists.
        internal static bool Available
        {
            get
            {
#if STROOP_LEGACY_RAW_RECORDING_DEVELOPMENT
                return ViveControlledRecordingBuildPolicy.Available;
#else
                return false;
#endif
            }
        }
    }
}
