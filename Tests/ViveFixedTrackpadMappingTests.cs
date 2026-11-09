using System;
using System.Linq;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveFixedTrackpadMappingTests
    {
        static VivePreviewTrackpadSample Sample(bool pressed=false,double x=0,double y=0,bool ready=true)
        {return new VivePreviewTrackpadSample(ready,pressed,x,y);}

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE fixed mapping supplies the approved four colours and independent copies",()=>{
                var first=ViveFixedTrackpadMapping.Create();
                var second=ViveFixedTrackpadMapping.Create();
                Check.Equal(4,first.Count);
                Check.Equal(Colour.Red,first[ViveTrackpadDirection.Up]);
                Check.Equal(Colour.Green,first[ViveTrackpadDirection.Right]);
                Check.Equal(Colour.Blue,first[ViveTrackpadDirection.Down]);
                Check.Equal(Colour.Yellow,first[ViveTrackpadDirection.Left]);
                Check.Equal("Trackpad: Up Red | Right Green | Down Blue | Left Yellow",ViveFixedTrackpadMapping.Label);
                first[ViveTrackpadDirection.Up]=Colour.Blue;
                Check.Equal(Colour.Red,second[ViveTrackpadDirection.Up]);
                Check.Equal(Colour.Red,ViveFixedTrackpadMapping.Create()[ViveTrackpadDirection.Up]);
            });
            suite.Run("VIVE fixed mapping emits and releases the approved colour in practice and task",()=>{
                foreach(var phase in new[]{Phase.Practice,Phase.MainTask})
                {
                    var input=new VivePreviewTrackpadInput(ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold);
                    input.Update(phase,Sample());
                    foreach(var test in new[]{Tuple.Create(0d,1d,Colour.Red),Tuple.Create(1d,0d,Colour.Green),
                        Tuple.Create(0d,-1d,Colour.Blue),Tuple.Create(-1d,0d,Colour.Yellow)})
                    {
                        var down=input.Update(phase,Sample(true,test.Item1,test.Item2));
                        Check.Equal((Colour?)test.Item3,down.PressedColour);
                        Check.Equal(0,down.ReleasedColours.Count);
                        var up=input.Update(phase,Sample());
                        Check.Equal(test.Item3,up.ReleasedColours.Single());
                        Check.Equal(0,input.Update(phase,Sample()).ReleasedColours.Count);
                    }
                }
            });
            suite.Run("VIVE fixed mapping keeps initial and phase-boundary held presses inert",()=>{
                var input=new VivePreviewTrackpadInput(ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold);
                Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,0,1)).PressedColour);
                Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,0,1)).PressedColour);
                input.Update(Phase.Practice,Sample());
                Check.Equal((Colour?)Colour.Red,input.Update(Phase.Practice,Sample(true,0,1)).PressedColour);
                Check.Equal<Colour?>(null,input.Update(Phase.MainTask,Sample(true,1,0)).PressedColour);
                Check.Equal(Colour.Red,input.Update(Phase.MainTask,Sample()).ReleasedColours.Single());
                Check.Equal((Colour?)Colour.Green,input.Update(Phase.MainTask,Sample(true,1,0)).PressedColour);
            });
            suite.Run("VIVE fixed threshold classifies each 0.5 boundary and ignores centre and diagonals",()=>{
                Check.Equal(0.5,ViveFixedTrackpadMapping.SectorThreshold);
                foreach(var test in new[]{Tuple.Create(0d,.5,Colour.Red),Tuple.Create(.5,0d,Colour.Green),
                    Tuple.Create(0d,-.5,Colour.Blue),Tuple.Create(-.5,0d,Colour.Yellow)})
                {
                    var input=new VivePreviewTrackpadInput(ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold);
                    input.Update(Phase.Practice,Sample());
                    Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,test.Item1*.98,test.Item2*.98)).PressedColour);
                    Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,test.Item1,test.Item2)).PressedColour);
                    input.Update(Phase.Practice,Sample());
                    Check.Equal((Colour?)test.Item3,input.Update(Phase.Practice,Sample(true,test.Item1,test.Item2)).PressedColour);
                    input.Update(Phase.Practice,Sample());
                    Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,.5,.5)).PressedColour);
                    input.Update(Phase.Practice,Sample());
                    Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,0,0)).PressedColour);
                }
            });
            suite.Run("VIVE fixed threshold questionnaire navigation consumes one physical click",()=>{
                var gate=new ViveQuestionnaireTrackpadGate(ViveFixedTrackpadMapping.SectorThreshold);
                gate.Observe(true,true,true,Sample());
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,0,.49)));
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,0,.5)));
                gate.Observe(true,true,true,Sample());
                Check.Equal(-1,gate.Observe(true,true,true,Sample(true,0,.5)));
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,0,-.5)));
                gate.Observe(true,true,true,Sample());
                Check.Equal(1,gate.Observe(true,true,true,Sample(true,0,-.5)));
                gate.Observe(true,true,true,Sample());
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,.5,.5)));
                gate.Observe(true,true,true,Sample());
                Check.Equal(0,gate.Observe(true,true,true,Sample(true,.5,0)));
            });
            suite.Run("VIVE fixed mapping preserves selected threshold and held-sector behaviour",()=>{
                var input=new VivePreviewTrackpadInput(ViveFixedTrackpadMapping.Create(),.75);
                input.Update(Phase.Practice,Sample());
                Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,0,.74)).PressedColour);
                Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,0,1)).PressedColour);
                input.Update(Phase.Practice,Sample());
                Check.Equal((Colour?)Colour.Yellow,input.Update(Phase.Practice,Sample(true,-.75,0)).PressedColour);
                Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,1,0)).PressedColour);
                Check.Equal(Colour.Yellow,input.Update(Phase.Practice,Sample()).ReleasedColours.Single());
                input.Update(Phase.Practice,Sample(true,0,-1));
                var lost=input.Update(Phase.Practice,Sample(false,0,0,false));
                Check.True(!lost.DeviceReady);
                Check.Equal(Colour.Blue,lost.ReleasedColours.Single());
                Check.Equal<Colour?>(null,input.Update(Phase.Practice,Sample(true,0,-1)).PressedColour);
            });
        }
    }
}
