using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Preserve discrete renderer/input transitions; only steady-state pose samples
    // are rate-limited. This does not claim complete frame or photon-onset evidence.
    public sealed class PresentationAuditSampler
    {
        public const double IntervalSeconds=.1;
        bool sampled,headValid,eyeValid,headHeld,eyesHeld,observed,visibility,avatarVisible,hasPose;
        int block,segment;
        string trial,mapping,geometry,stream,clock;
        Phase phase;
        ControlSource selected;
        ControlSource? actual;
        GazeOutputStatus status;
        ReferencePhase? referencePhase;
        GazeTarget? from,to;
        double nextAt;

        public bool ShouldRecord(double now,AuditContext context,Phase currentPhase,AvatarOutput output,
            bool rendererObserved,bool rendererHeadHeld,bool rendererEyesHeld,bool visibilityRequested)
        {
            if(context==null||output==null)throw new ArgumentNullException(context==null?"context":"output");
            if(Double.IsNaN(now)||Double.IsInfinity(now)||now<0)throw new ArgumentOutOfRangeException("now");
            var reference=output.Reference;var input=output.InputSample;
            string nextGeometry=reference==null?null:reference.GeometryVersion;
            string nextStream=input==null?null:input.StreamId,nextClock=input==null?null:input.ClockId;
            ReferencePhase? nextReference=reference==null?(ReferencePhase?)null:reference.Phase;
            GazeTarget? nextFrom=reference==null?null:reference.FromTarget,nextTo=reference==null?null:reference.ToTarget;
            bool changed=!sampled||block!=context.BlockIndex||segment!=context.Segment||trial!=context.TrialId||phase!=currentPhase||
                headValid!=output.HeadValid||eyeValid!=output.EyeValid||headHeld!=rendererHeadHeld||eyesHeld!=rendererEyesHeld||
                observed!=rendererObserved||visibility!=visibilityRequested||avatarVisible!=output.AvatarVisible||hasPose!=output.HasActualPose||
                selected!=output.SelectedSource||actual!=output.ActualSource||status!=output.Status||mapping!=output.MappingVersion||
                geometry!=nextGeometry||stream!=nextStream||clock!=nextClock||referencePhase!=nextReference||from!=nextFrom||to!=nextTo;
            if(!changed&&now<nextAt)return false;
            sampled=true;nextAt=now+IntervalSeconds;
            block=context.BlockIndex;segment=context.Segment;trial=context.TrialId;phase=currentPhase;
            headValid=output.HeadValid;eyeValid=output.EyeValid;headHeld=rendererHeadHeld;eyesHeld=rendererEyesHeld;
            observed=rendererObserved;visibility=visibilityRequested;avatarVisible=output.AvatarVisible;hasPose=output.HasActualPose;
            selected=output.SelectedSource;actual=output.ActualSource;status=output.Status;mapping=output.MappingVersion;
            geometry=nextGeometry;stream=nextStream;clock=nextClock;referencePhase=nextReference;from=nextFrom;to=nextTo;
            return true;
        }
    }
}
