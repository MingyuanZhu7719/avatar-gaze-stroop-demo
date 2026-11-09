using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    // Canonical byte representation of source event values, not a wire or
    // export format. Golden hashes are from the saved pre-v3 Unity source.
    internal static class ViveHistoricalEventBytesFixture
    {
        sealed class Clock:IClock {internal double Value;public double Now {get{return Value;}}public string ClockId {get{return "historical-synthetic-clock";}}}
        static void Answer(Experiment e,Clock c)
        {e.MarkPresented(e.CurrentTrial.TrialId);c.Value+=.25;var ink=e.CurrentTrial.Stimulus.Ink;e.ButtonChanged(ink,true);e.ButtonChanged(ink,false);}
        static void Text(BinaryWriter writer,string value)
        {writer.Write(value!=null);if(value!=null)writer.Write(value);}
        static byte[] CanonicalBytes(IReadOnlyList<JournalEvent> events)
        {
            using(var bytes=new MemoryStream())
            {
                using(var writer=new BinaryWriter(bytes,new UTF8Encoding(false,true),true))
                {
                    writer.Write(events.Count);
                    foreach(var e in events)
                    {
                        writer.Write(e.Sequence);writer.Write(e.Time);Text(writer,e.ClockId);Text(writer,e.SessionId);
                        writer.Write(e.BlockIndex);writer.Write(e.Segment);Text(writer,e.TrialId);Text(writer,e.Kind);
                        Text(writer,e.NamedOption==null?null:e.NamedOption.Option.ToString());
                        Text(writer,e.NamedPlan==null?null:e.NamedPlan.PlanId);writer.Write(e.NamedBlock==null?-1:e.NamedBlock.Index);
                        var keys=new List<string>(e.Fields.Keys);keys.Sort(StringComparer.Ordinal);writer.Write(keys.Count);
                        foreach(var key in keys){Text(writer,key);Text(writer,e.Fields[key]);}
                    }
                }
                return bytes.ToArray();
            }
        }
        internal static string Digest(int fixture)
        {var bytes=Snapshot(fixture);using(var sha=SHA256.Create())return BitConverter.ToInt32(bytes,0).ToString(CultureInfo.InvariantCulture)+":"+
            BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        internal static byte[] Snapshot(int fixture)
        {
            var clock=new Clock();SessionConfiguration config;
            if(fixture==0)
            {
                var allocation=new Allocation("synthetic-williams-bcad",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                config=new SessionConfiguration("synthetic-quest",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            }
            else if(fixture==1)
                config=SessionConfiguration.NamedAutomaticDevelopment("synthetic-named-v1",42,
                    ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,2,"synthetic-geometry"));
            else if(fixture==2)
            {
                var n=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);
                var a=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,3,"synthetic-geometry");
                config=SessionConfiguration.NamedPlanDevelopment("synthetic-named-v2",42,ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-plan",n,a));
            }
            else throw new ArgumentOutOfRangeException("fixture");
            var experiment=new Experiment(config,clock);experiment.BeginFamiliarization(Actor.Owner);experiment.BeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)Answer(experiment,clock);
            if(fixture==2)experiment.ConfirmNamedPlan(Actor.Owner,"synthetic-owner");else experiment.LockBlock(Actor.Owner,ControlSelection.Automatic,"synthetic-owner");
            experiment.BeginCountdown(Actor.Owner,true);clock.Value+=3;experiment.Tick();
            int blocks=fixture==0?4:fixture==1?1:2;
            for(int block=0;block<blocks;block++)
            {
                if(block==0)
                {experiment.MarkPresented(experiment.CurrentTrial.TrialId);clock.Value+=.5;experiment.Pause(Actor.Participant);clock.Value+=2;experiment.Resume(true);clock.Value+=3;experiment.Tick();}
                while(experiment.Phase==Phase.MainTask)Answer(experiment,clock);
                experiment.AcknowledgeQuestionnaireInstruction();for(int i=0;i<Questionnaire.Count;i++)experiment.Rate(3);
                if(block+1<blocks){experiment.Ready();experiment.TryBeginRetainedBlockCountdown(true);clock.Value+=3;experiment.Tick();}
            }
            if(experiment.Phase!=Phase.Ended||experiment.EndReason!=EndReason.Completed)throw new InvalidOperationException("Synthetic fixture did not complete");
            return CanonicalBytes(experiment.Events);
        }
    }
}
