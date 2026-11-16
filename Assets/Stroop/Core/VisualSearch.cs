using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AvatarStroop.Core
{
    public enum TaskKind { Stroop, VisualSearch }
    public enum VisualSearchPattern { Plain, Striped, Dotted, Cross }

    public sealed class VisualSearchItem
    {
        public string Id {get;private set;}
        public int Slot {get;private set;}
        public Colour Colour {get;private set;}
        public VisualSearchPattern Pattern {get;private set;}
        public VisualSearchItem(int slot,Colour colour,VisualSearchPattern pattern)
        {
            if(slot<0||slot>=24||!Enum.IsDefined(typeof(Colour),colour)||!Enum.IsDefined(typeof(VisualSearchPattern),pattern))
                throw new ArgumentOutOfRangeException("item");
            Slot=slot;Colour=colour;Pattern=pattern;Id="item-"+slot.ToString("D2",CultureInfo.InvariantCulture);
        }
    }

    // Immutable, explicitly serialized search layout. Slots are row-major on
    // one six-column/four-row board; physical geometry is a renderer concern.
    public sealed class VisualSearchTrial
    {
        public const string Schema="visual-search-v1";
        public const string GeometryVersion="visual-search-backpacks-board-v1";
        public const int Columns=6,Rows=4;
        public const double ColumnStepMetres=.205,RowStepMetres=.175,HitWidthMetres=.174,HitHeightMetres=.154;
        public const double TimeoutSeconds=30;
        public const double TargetPreviewSeconds=1.5;
        public string LayoutId {get;private set;}
        public uint LayoutSeed {get;private set;}
        public int SetSize {get{return Items.Count;}}
        public string TargetId {get;private set;}
        public Colour TargetColour {get;private set;}
        public VisualSearchPattern TargetPattern {get;private set;}
        public string TargetLabel {get{return TargetColour.ToString().ToLowerInvariant()+" "+TargetPattern.ToString().ToLowerInvariant()+" backpack";}}
        public IReadOnlyList<VisualSearchItem> Items {get;private set;}
        public string PlanIdentity {get;private set;}
        readonly string serialized;
        public VisualSearchTrial(string layoutId,uint seed,int targetSlot,IEnumerable<VisualSearchItem> items)
        {
            if(String.IsNullOrWhiteSpace(layoutId)||layoutId.Length>80||layoutId.IndexOfAny(new[]{'|',';',':','\r','\n'})>=0)
                throw new ArgumentException("Stable layout identity required");
            var copy=new List<VisualSearchItem>(items??throw new ArgumentNullException("items"));
            if(copy.Count!=12&&copy.Count!=24)throw new ArgumentException("Search layouts contain 12 or 24 objects");
            copy.Sort((a,b)=>a==null?-1:b==null?1:a.Slot.CompareTo(b.Slot));
            var slots=new HashSet<int>();VisualSearchItem target=null;
            foreach(var item in copy)
            {if(item==null||!slots.Add(item.Slot))throw new ArgumentException("Unique non-null slot items required");if(item.Slot==targetSlot)target=item;}
            if(target==null)throw new ArgumentException("Exactly one target must be present");
            int matching=0;foreach(var item in copy)if(item.Colour==target.Colour&&item.Pattern==target.Pattern)matching++;
            if(matching!=1)throw new ArgumentException("Target attributes must uniquely identify the target");
            LayoutId=layoutId;LayoutSeed=seed;TargetId=target.Id;TargetColour=target.Colour;TargetPattern=target.Pattern;
            Items=new ReadOnlyCollection<VisualSearchItem>(copy);
            var value=new StringBuilder("vs1|").Append(layoutId).Append('|').Append(seed.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(targetSlot.ToString(CultureInfo.InvariantCulture)).Append('|');
            for(int i=0;i<copy.Count;i++)
            {if(i>0)value.Append(';');var item=copy[i];value.Append(item.Slot.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(((int)item.Colour).ToString(CultureInfo.InvariantCulture)).Append(':').Append(((int)item.Pattern).ToString(CultureInfo.InvariantCulture));}
            serialized=value.ToString();
            using(var hash=SHA256.Create())PlanIdentity=layoutId+":"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(serialized))).Replace("-","").ToLowerInvariant();
        }
        public string Serialize(){return serialized;}
        public VisualSearchItem FindItem(string id)
        {if(id==null)return null;foreach(var item in Items)if(item.Id==id)return item;return null;}
        public static VisualSearchTrial Parse(string value)
        {
            if(value==null||value.Length>2048)throw new ArgumentException("Invalid bounded search layout");
            var parts=value.Split('|');uint seed;int target;
            if(parts.Length!=5||parts[0]!="vs1"||!UInt32.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out seed)||
                !Int32.TryParse(parts[3],NumberStyles.None,CultureInfo.InvariantCulture,out target))throw new ArgumentException("Invalid search layout header");
            var items=new List<VisualSearchItem>();
            foreach(var item in parts[4].Split(';'))
            {
                var fields=item.Split(':');int slot,colour,pattern;
                if(fields.Length!=3||!Int32.TryParse(fields[0],NumberStyles.None,CultureInfo.InvariantCulture,out slot)||
                    !Int32.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out colour)||!Int32.TryParse(fields[2],NumberStyles.None,CultureInfo.InvariantCulture,out pattern))
                    throw new ArgumentException("Invalid search object");
                items.Add(new VisualSearchItem(slot,(Colour)colour,(VisualSearchPattern)pattern));
            }
            var result=new VisualSearchTrial(parts[1],seed,target,items);
            if(result.Serialize()!=value)throw new ArgumentException("Noncanonical search layout");
            return result;
        }
    }

    public static class VisualSearchPlans
    {
        sealed class Random32
        {
            uint state;internal Random32(uint seed){state=seed;}
            internal uint Next(){unchecked{state+=0x9E3779B9u;uint v=state;v=(v^(v>>16))*0x85EBCA6Bu;v=(v^(v>>13))*0xC2B2AE35u;return v^(v>>16);}}
            internal int Next(int maximum){return (int)(Next()%(uint)maximum);}
        }
        static void Shuffle<T>(T[] values,Random32 random)
        {for(int i=values.Length-1;i>0;i--){int j=random.Next(i+1);T temp=values[i];values[i]=values[j];values[j]=temp;}}
        public static Stimulus[] MainList(uint seed,int listIndex)
        {
            if(listIndex<0||listIndex>3)throw new ArgumentOutOfRangeException("listIndex");
            return Build(seed,listIndex,0,24);
        }
        public static Stimulus[] Practice(uint seed,int attempt)
        {
            if(attempt<1||attempt>2)throw new ArgumentOutOfRangeException("attempt");
            return Build(seed,-1,attempt,8);
        }
        static Stimulus[] Build(uint seed,int list,int attempt,int count)
        {
            var random=new Random32(unchecked(seed+(uint)(list+2)*0xD1B54A35u+(uint)attempt*0x94D049BBu));
            var slots=new int[24];for(int i=0;i<24;i++)slots[i]=i;
            Shuffle(slots,random);
            var result=new Stimulus[count];int small=0,large=0;
            for(int i=0;i<count;i++)
            {
                int slot=slots[i];
                // Main plans visit every slot once. A checkerboard assignment
                // balances size within each row, column and left/right half.
                bool twelve=count==24?((slot/6+slot%6+list)%2==0):i%2==0;
                int within=twelve?small++:large++;
                var colour=(Colour)((within+(list<0?attempt:list))%4);
                var pattern=(VisualSearchPattern)((within/4+within%4+(list<0?attempt:list))%4);
                uint layoutSeed=random.Next();
                string id="vs1-"+seed.ToString("x8",CultureInfo.InvariantCulture)+(list<0?"-p"+attempt:"-l"+list)+"-t"+i;
                result[i]=new Stimulus(Layout(id,layoutSeed,slot,twelve?12:24,colour,pattern));
            }
            Shuffle(result,random);return result;
        }
        static VisualSearchTrial Layout(string id,uint seed,int targetSlot,int count,Colour targetColour,VisualSearchPattern targetPattern)
        {
            var random=new Random32(seed);var otherSlots=new int[23];int n=0;
            for(int i=0;i<24;i++)if(i!=targetSlot)otherSlots[n++]=i;
            Shuffle(otherSlots,random);
            var colours=new[]{0,1,2,3};var patterns=new[]{0,1,2,3};Shuffle(colours,random);Shuffle(patterns,random);
            var result=new List<VisualSearchItem>{new VisualSearchItem(targetSlot,targetColour,targetPattern)};
            int ci=0,pi=0;
            // Roughly half share the target colour, half its pattern. Every
            // distractor differs in at least one feature; neither feature alone
            // identifies the target. The extra item alternates by layout seed.
            for(int i=0;i<count-1;i++)
            {
                Colour colour;VisualSearchPattern pattern;
                if(((i+(int)(seed&1))&1)==0)
                {colour=targetColour;do{pattern=(VisualSearchPattern)patterns[pi++%4];}while(pattern==targetPattern);}
                else{pattern=targetPattern;do{colour=(Colour)colours[ci++%4];}while(colour==targetColour);}
                result.Add(new VisualSearchItem(otherSlots[i],colour,pattern));
            }
            return new VisualSearchTrial(id,seed,targetSlot,result);
        }
    }
}
