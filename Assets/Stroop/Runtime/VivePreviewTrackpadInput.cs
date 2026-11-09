using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Abstract, normalized trackpad sectors for development preview. This does
    // not read an XR device or prescribe the study's colour mapping.
    public enum ViveTrackpadDirection { Up, Right, Down, Left }

    public sealed class VivePreviewTrackpadSample
    {
        public bool DeviceReady { get; private set; }
        public bool Pressed { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }

        // X increases right and Y increases up, both normalized to [-1, 1].
        public VivePreviewTrackpadSample(bool deviceReady, bool pressed, double x, double y)
        { DeviceReady=deviceReady; Pressed=pressed; X=x; Y=y; }
        internal bool Usable { get { return DeviceReady && WireCodec.Finite(X) && WireCodec.Finite(Y) && X>=-1 && X<=1 && Y>=-1 && Y<=1; } }
    }

    public sealed class VivePreviewTrackpadFrame
    {
        public bool DeviceReady { get; private set; }
        public Colour? PressedColour { get; private set; }
        public IReadOnlyList<Colour> ReleasedColours { get; private set; }
        internal VivePreviewTrackpadFrame(bool ready, Colour? pressed, IReadOnlyList<Colour> released)
        { DeviceReady=ready; PressedColour=pressed; ReleasedColours=released; }
    }

    // Questionnaire ratings respond once per physical click. Moving a held
    // click through the centre or into another sector cannot re-arm it.
    public sealed class ViveQuestionnaireTrackpadGate
    {
        readonly double threshold;
        bool active,armed;
        public ViveQuestionnaireTrackpadGate(double threshold)
        {
            if(!WireCodec.Finite(threshold)||threshold<=0||threshold>1)throw new ArgumentOutOfRangeException("threshold");
            this.threshold=threshold;
        }
        public void Reset(){active=false;armed=false;}
        public int Observe(bool inputReady,bool questionnaire,bool displayReady,VivePreviewTrackpadSample sample)
        {
            if(!inputReady||!questionnaire||!displayReady||sample==null||!sample.Usable)
            {Reset();return 0;}
            if(!active){active=true;armed=!sample.Pressed;return 0;}
            if(!sample.Pressed){armed=true;return 0;}
            if(!armed)return 0;
            armed=false;
            double x=Math.Abs(sample.X),y=Math.Abs(sample.Y);
            if(y<threshold||y<=x)return 0;
            return sample.Y>0?-1:1;
        }
    }

    // Intent only: forward releases before the optional down to the core. This
    // adapter has no hardware bindings and does not advance or configure a run.
    public sealed class VivePreviewTrackpadInput
    {
        readonly Dictionary<ViveTrackpadDirection, Colour> mapping;
        readonly double threshold;
        bool sampled, previousPressed;
        Phase previousPhase;
        Colour? emitted;
        // Read-only input-edge state for the development retry safety check.
        internal bool WasPressed {get{return sampled&&previousPressed;}}

        // Caller must choose all four directions and four distinct colours, and
        // explicitly choose the minimum absolute dominant axis for a sector.
        public VivePreviewTrackpadInput(IDictionary<ViveTrackpadDirection, Colour> mapping, double threshold)
        {
            if(mapping==null) throw new ArgumentNullException("mapping");
            if(!WireCodec.Finite(threshold) || threshold<=0 || threshold>1) throw new ArgumentOutOfRangeException("threshold");
            if(mapping.Count!=4) throw new ArgumentException("A complete four-direction mapping is required.", "mapping");
            this.mapping=new Dictionary<ViveTrackpadDirection, Colour>();
            var colours=new HashSet<Colour>();
            foreach(var pair in mapping)
            {
                if(!Enum.IsDefined(typeof(ViveTrackpadDirection),pair.Key) || !Enum.IsDefined(typeof(Colour),pair.Value) || !colours.Add(pair.Value))
                    throw new ArgumentException("The mapping must be a bijection of directions to colours.", "mapping");
                this.mapping.Add(pair.Key,pair.Value);
            }
            foreach(ViveTrackpadDirection direction in Enum.GetValues(typeof(ViveTrackpadDirection)))
                if(!this.mapping.ContainsKey(direction)) throw new ArgumentException("A complete four-direction mapping is required.", "mapping");
            this.threshold=threshold;
        }

        public IReadOnlyList<Colour> Reset()
        {
            var releases=Release();
            sampled=false; previousPressed=false;
            return releases;
        }

        public VivePreviewTrackpadFrame Update(Phase phase, VivePreviewTrackpadSample sample)
        {
            if(!Enum.IsDefined(typeof(Phase),phase)) throw new ArgumentOutOfRangeException("phase");
            if(sample==null) throw new ArgumentNullException("sample");
            if(!sample.Usable) return new VivePreviewTrackpadFrame(false,null,Reset());

            var releases=!sample.Pressed ? Release() : Empty();
            bool boundary=!sampled || phase!=previousPhase;
            bool pressed=sample.Pressed && !previousPressed;
            sampled=true; previousPhase=phase; previousPressed=sample.Pressed;
            // Each phase/reconnection observes its first sample. A press remains
            // latched to its original colour until physical release, even if the
            // contact moves or the phase changes.
            if(boundary || !pressed || (phase!=Phase.Practice && phase!=Phase.MainTask))
                return new VivePreviewTrackpadFrame(true,null,releases);

            double ax=Math.Abs(sample.X), ay=Math.Abs(sample.Y);
            // Centre and exact diagonals are deliberately unclassified. The
            // whole click is consumed; movement while held cannot create an edge.
            if(Math.Max(ax,ay)<threshold || ax==ay) return new VivePreviewTrackpadFrame(true,null,releases);
            var direction=ax>ay ? (sample.X>0?ViveTrackpadDirection.Right:ViveTrackpadDirection.Left) :
                (sample.Y>0?ViveTrackpadDirection.Up:ViveTrackpadDirection.Down);
            emitted=mapping[direction];
            return new VivePreviewTrackpadFrame(true,emitted,releases);
        }

        IReadOnlyList<Colour> Release()
        {
            var result=new List<Colour>();
            if(emitted.HasValue) result.Add(emitted.Value);
            emitted=null;
            return new ReadOnlyCollection<Colour>(result);
        }
        static IReadOnlyList<Colour> Empty() { return new ReadOnlyCollection<Colour>(new List<Colour>()); }
    }
}
