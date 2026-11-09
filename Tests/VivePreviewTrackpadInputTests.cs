using System;
using System.Collections.Generic;
using System.Linq;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class VivePreviewTrackpadInputTests
    {
        static Dictionary<ViveTrackpadDirection,Colour> Map()
        {
            return new Dictionary<ViveTrackpadDirection,Colour> {
                {ViveTrackpadDirection.Up,Colour.Yellow}, {ViveTrackpadDirection.Right,Colour.Red},
                {ViveTrackpadDirection.Down,Colour.Blue}, {ViveTrackpadDirection.Left,Colour.Green} };
        }
        static VivePreviewTrackpadSample Sample(bool pressed=false,double x=0,double y=0,bool ready=true)
        { return new VivePreviewTrackpadSample(ready,pressed,x,y); }
        public static void Register(TestSuite suite)
        {
            suite.Run("Vive questionnaire navigation consumes a physical click through sector changes",()=>{
                var gate=new ViveQuestionnaireTrackpadGate(.5);
                Check.Equal(0,gate.Observe(true,true,true,Sample()));
                Check.Equal(-1,gate.Observe(true,true,true,Sample(true,0,1)));
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,1,0)));
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,0,1)));
                Check.Equal(0,gate.Observe(true,true,true,Sample()));
                Check.Equal(1,gate.Observe(true,true,true,Sample(true,0,-1)));
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,0,1)));
                gate.Observe(true,true,true,Sample());
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,1,0)));
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,0,-1)));
                gate.Observe(true,true,true,Sample());
                Check.Equal(-1,gate.Observe(true,true,true,Sample(true,0,.5)));
            });
            suite.Run("Vive questionnaire navigation requires release after context or focus loss",()=>{
                var gate=new ViveQuestionnaireTrackpadGate(.5);
                Check.Equal(0,gate.Observe(true,false,true,Sample(true,0,1)));
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,0,1)));
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,0,-1)));
                gate.Observe(true,true,true,Sample());Check.Equal(1,gate.Observe(true,true,true,Sample(true,0,-1)));
                Check.Equal(0,gate.Observe(false,true,true,Sample(true,0,-1)));
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,0,-1)));
                gate.Observe(true,true,false,Sample());
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,0,1)));
                gate.Observe(true,true,true,Sample());Check.Equal(-1,gate.Observe(true,true,true,Sample(true,0,1)));
                gate.Reset();Check.Equal(0,gate.Observe(true,true,true,Sample(true,0,1)));
            });
            suite.Run("Vive trackpad requires explicit complete bijective mapping and valid threshold",()=>{
                Check.Throws<ArgumentNullException>(()=>new VivePreviewTrackpadInput(null,.5));
                var missing=Map(); missing.Remove(ViveTrackpadDirection.Up);
                Check.Throws<ArgumentException>(()=>new VivePreviewTrackpadInput(missing,.5));
                var duplicate=Map(); duplicate[ViveTrackpadDirection.Up]=Colour.Red;
                Check.Throws<ArgumentException>(()=>new VivePreviewTrackpadInput(duplicate,.5));
                var invalidDirection=Map(); invalidDirection.Remove(ViveTrackpadDirection.Up); invalidDirection[(ViveTrackpadDirection)99]=Colour.Yellow;
                Check.Throws<ArgumentException>(()=>new VivePreviewTrackpadInput(invalidDirection,.5));
                var invalidColour=Map(); invalidColour[ViveTrackpadDirection.Up]=(Colour)99;
                Check.Throws<ArgumentException>(()=>new VivePreviewTrackpadInput(invalidColour,.5));
                foreach(double value in new[]{0d,-.1,1.01,Double.NaN,Double.PositiveInfinity})
                    Check.Throws<ArgumentOutOfRangeException>(()=>new VivePreviewTrackpadInput(Map(),value));
            });
            suite.Run("Vive trackpad maps each sector from a defensive copy and releases its own down",()=>{
                var map=Map(); var input=new VivePreviewTrackpadInput(map,.5); map[ViveTrackpadDirection.Up]=Colour.Red;
                input.Update(Phase.MainTask,Sample());
                foreach(var item in new[]{Tuple.Create(0d,1d,Colour.Yellow),Tuple.Create(1d,0d,Colour.Red),Tuple.Create(0d,-1d,Colour.Blue),Tuple.Create(-1d,0d,Colour.Green)})
                {
                    var down=input.Update(Phase.MainTask,Sample(true,item.Item1,item.Item2));
                    Check.Equal((Colour?)item.Item3,down.PressedColour); Check.Equal(0,down.ReleasedColours.Count);
                    var up=input.Update(Phase.MainTask,Sample()); Check.Equal(item.Item3,up.ReleasedColours.Single());
                    Check.Equal(0,input.Update(Phase.MainTask,Sample()).ReleasedColours.Count);
                }
            });
            suite.Run("Vive trackpad holds one colour while moving and consumes ambiguous clicks",()=>{
                var input=new VivePreviewTrackpadInput(Map(),.5); input.Update(Phase.Practice,Sample());
                Check.Equal((Colour?)Colour.Yellow,input.Update(Phase.Practice,Sample(true,0,1)).PressedColour);
                Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,1,0)).PressedColour);
                Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,0,0)).PressedColour);
                Check.Equal(Colour.Yellow,input.Update(Phase.Practice,Sample()).ReleasedColours.Single());
                foreach(var position in new[]{Tuple.Create(.1,.1),Tuple.Create(.8,.8),Tuple.Create(.49,0d)})
                {
                    Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,position.Item1,position.Item2)).PressedColour);
                    Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,1,0)).PressedColour);
                    Check.Equal(0,input.Update(Phase.Practice,Sample()).ReleasedColours.Count);
                }
                Check.Equal((Colour?)Colour.Red,input.Update(Phase.Practice,Sample(true,.5,0)).PressedColour);
            });
            suite.Run("Vive trackpad observes initial and phase boundary samples and releases across phases",()=>{
                var input=new VivePreviewTrackpadInput(Map(),.5);
                Check.Equal<Colour?>(null,input.Update(Phase.MainTask,Sample(true,0,1)).PressedColour);
                Check.Equal<Colour?>(null,input.Update(Phase.MainTask,Sample(true,0,1)).PressedColour);
                input.Update(Phase.MainTask,Sample());
                Check.Equal((Colour?)Colour.Green,input.Update(Phase.MainTask,Sample(true,-1,0)).PressedColour);
                Check.Equal<Colour?>(null,input.Update(Phase.Questionnaire,Sample(true,1,0)).PressedColour);
                Check.Equal(Colour.Green,input.Update(Phase.Questionnaire,Sample()).ReleasedColours.Single());
                Check.Equal<Colour?>(null,input.Update(Phase.Questionnaire,Sample(true,1,0)).PressedColour);
                Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,1,0)).PressedColour);
                input.Update(Phase.Practice,Sample());
                Check.Equal((Colour?)Colour.Red,input.Update(Phase.Practice,Sample(true,1,0)).PressedColour);
                Check.Throws<ArgumentOutOfRangeException>(()=>input.Update((Phase)99,Sample()));
                Check.Throws<ArgumentNullException>(()=>input.Update(Phase.Practice,null));
            });
            suite.Run("Vive trackpad invalid samples and reset release exactly once and reconnect safely",()=>{
                foreach(double value in new[]{Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,1.01,-1.01})
                {
                    foreach(bool invalidX in new[]{true,false})
                    {
                        var input=new VivePreviewTrackpadInput(Map(),.5); input.Update(Phase.MainTask,Sample());
                        input.Update(Phase.MainTask,Sample(true,0,-1));
                        var bad=input.Update(Phase.MainTask,Sample(true,invalidX?value:0,invalidX?0:value));
                        Check.True(!bad.DeviceReady); Check.Equal<Colour?>(null,bad.PressedColour); Check.Equal(Colour.Blue,bad.ReleasedColours.Single());
                        Check.Equal(0,input.Update(Phase.MainTask,Sample(false,0,0,false)).ReleasedColours.Count);
                        Check.Equal<Colour?>(null,input.Update(Phase.MainTask,Sample(true,0,-1)).PressedColour);
                        input.Update(Phase.MainTask,Sample()); input.Update(Phase.MainTask,Sample(true,0,-1));
                        Check.Equal(Colour.Blue,input.Reset().Single()); Check.Equal(0,input.Reset().Count);
                        Check.Equal<Colour?>(null,input.Update(Phase.MainTask,Sample(true,0,-1)).PressedColour);
                    }
                }
                var loss=new VivePreviewTrackpadInput(Map(),.5); loss.Update(Phase.MainTask,Sample());
                loss.Update(Phase.MainTask,Sample(true,1,0));
                var disconnected=loss.Update(Phase.MainTask,Sample(true,1,0,false));
                Check.True(!disconnected.DeviceReady); Check.Equal(Colour.Red,disconnected.ReleasedColours.Single());
                Check.Equal<Colour?>(null,loss.Update(Phase.MainTask,Sample(true,1,0)).PressedColour);
            });
            suite.Run("Vive trackpad intents preserve the core response and held release contract",()=>{
                var clock=new ManualClock(); var experiment=Fixtures.New(clock);
                experiment.BeginFamiliarization(Actor.Owner); experiment.BeginPractice(Actor.Owner);
                experiment.MarkPresented(experiment.CurrentTrial.TrialId);
                var input=new VivePreviewTrackpadInput(Map(),.5); input.Update(experiment.Phase,Sample());
                var down=input.Update(experiment.Phase,Sample(true,0,1));
                Check.True(experiment.ButtonChanged(down.PressedColour.Value,true));
                var held=input.Update(experiment.Phase,Sample(true,1,0));
                Check.Equal<Colour?>(null,held.PressedColour);
                var released=input.Update(experiment.Phase,Sample());
                foreach(var colour in released.ReleasedColours) experiment.ButtonChanged(colour,false);
                Check.Equal(Colour.Yellow,released.ReleasedColours.Single());
            });
        }
    }
}
