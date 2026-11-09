using System.Collections.Generic;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // The VIVE preview uses one explicitly agreed mapping. Return a new
    // dictionary so neither the input adapter nor the view can change it for
    // another configuration or a later session.
    public static class ViveFixedTrackpadMapping
    {
        // Shared by VIVE task input, configuration validation and questionnaire navigation.
        public const double SectorThreshold = 0.5;
        public const string Version = "vive-trackpad-up-red-right-green-down-blue-left-yellow-v1";

        internal static bool Matches(IDictionary<ViveTrackpadDirection,Colour> value)
        {
            if(value==null||value.Count!=4)return false;
            foreach(var item in Create())
            {Colour found;if(!value.TryGetValue(item.Key,out found)||found!=item.Value)return false;}
            return true;
        }

        static readonly string label=CreateLabel();

        public static Dictionary<ViveTrackpadDirection,Colour> Create()
        {
            return new Dictionary<ViveTrackpadDirection,Colour>
            {
                {ViveTrackpadDirection.Up,Colour.Red},
                {ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},
                {ViveTrackpadDirection.Left,Colour.Yellow}
            };
        }

        public static string Label {get{return label;}}

        static string CreateLabel()
        {
            var map=Create();
            return "Trackpad: Up "+map[ViveTrackpadDirection.Up]+" | Right "+map[ViveTrackpadDirection.Right]+
                " | Down "+map[ViveTrackpadDirection.Down]+" | Left "+map[ViveTrackpadDirection.Left];
        }
    }
}
