using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A declaration in untrusted source bytes, not a flush receipt, proof of
    // participant consent, actual trial creation, presentation or response.
    internal sealed class ViveRawPreparedPlanItem
    {
        internal readonly int Index;internal readonly string Word,Ink;
        internal string Execution {get{return "UNKNOWN";}}
        internal ViveRawPreparedPlanItem(int index,string word,string ink){Index=index;Word=word;Ink=ink;}
    }
    internal sealed class ViveRawPreparedPlan
    {
        readonly ReadOnlyCollection<ViveRawPreparedPlanItem> items;
        internal readonly string Scope,List,PriorOrderState;internal readonly int Block,SegmentAtDeclaration,Attempt;internal readonly long EventSequence;internal readonly double EventLocalSeconds;
        internal IReadOnlyList<ViveRawPreparedPlanItem> Items {get{return items;}}
        internal string WriterConfirmation {get{return "UNKNOWN";}}
        internal bool SourceAuthenticated {get{return false;}}
        internal ViveRawPreparedPlan(string scope,string list,int block,int segment,int attempt,long sequence,double time,List<ViveRawPreparedPlanItem> values,string priorOrder)
        {Scope=scope;List=list;Block=block;SegmentAtDeclaration=segment;Attempt=attempt;EventSequence=sequence;EventLocalSeconds=time;items=new ReadOnlyCollection<ViveRawPreparedPlanItem>(values);PriorOrderState=priorOrder;}
    }
    // Reader-owned semantic checks. Missing plans in old or truncated data are
    // never filled in from a seed. Unknown schema remains unknown, not v1.
    internal sealed class ViveRawPreparedPlanAudit
    {
        internal sealed class InvalidPlan:Exception {}
        readonly Dictionary<string,ViveRawPreparedPlan> plans=new Dictionary<string,ViveRawPreparedPlan>(StringComparer.Ordinal);
        readonly HashSet<string> priorPracticeLists=new HashSet<string>(StringComparer.Ordinal);
        readonly bool[] priorMainTrials=new bool[4],priorPracticeAttempts=new bool[2];bool untrackedPriorPractice;
        internal int UnsupportedEvents {get;private set;}
        static bool ColourName(string value)
        {Colour c;return value!=null&&Enum.TryParse<Colour>(value,false,out c)&&Enum.IsDefined(typeof(Colour),c)&&String.Equals(c.ToString(),value,StringComparison.Ordinal);}
        static string Required(IReadOnlyDictionary<string,string> fields,string key)
        {string value;if(!fields.TryGetValue(key,out value)||String.IsNullOrWhiteSpace(value)||value.Length>256)throw new InvalidPlan();return value;}
        static string Key(string scope,int block,int attempt){return scope=="MAIN"?"M"+block.ToString(CultureInfo.InvariantCulture):"P"+attempt.ToString(CultureInfo.InvariantCulture);}
        static bool MainListMatches(string list,string declaredIndex)
        {
            int index;if(!Int32.TryParse(declaredIndex,NumberStyles.None,CultureInfo.InvariantCulture,out index)||index<0||index>3||
                declaredIndex!=index.ToString(CultureInfo.InvariantCulture))return false;
            string prefix="main-v1-"+declaredIndex+"-";if(!list.StartsWith(prefix,StringComparison.Ordinal)||list.Length!=prefix.Length+8)return false;
            for(int i=prefix.Length;i<list.Length;i++)if(!((list[i]>='0'&&list[i]<='9')||(list[i]>='a'&&list[i]<='f')))return false;
            return true;
        }
        static bool PracticeListMatches(string list,int attempt)
        {
            const string prefix="practice-";string suffix="-"+attempt.ToString(CultureInfo.InvariantCulture);
            if(!list.StartsWith(prefix,StringComparison.Ordinal)||!list.EndsWith(suffix,StringComparison.Ordinal)||list.Length<=prefix.Length+suffix.Length)return false;
            string seed=list.Substring(prefix.Length,list.Length-prefix.Length-suffix.Length);uint value;
            return UInt32.TryParse(seed,NumberStyles.None,CultureInfo.InvariantCulture,out value)&&seed==value.ToString(CultureInfo.InvariantCulture);
        }
        static bool TryPracticeId(string session,string trialId,out int attempt)
        {
            attempt=0;if(String.IsNullOrEmpty(session)||String.IsNullOrEmpty(trialId))return false;
            string prefix=session+"/p";if(!trialId.StartsWith(prefix,StringComparison.Ordinal))return false;
            int separator=trialId.IndexOf("/t",prefix.Length,StringComparison.Ordinal);if(separator<0)return false;
            string a=trialId.Substring(prefix.Length,separator-prefix.Length),i=trialId.Substring(separator+2);int index;
            return Int32.TryParse(a,NumberStyles.None,CultureInfo.InvariantCulture,out attempt)&&(attempt==1||attempt==2)&&a==attempt.ToString(CultureInfo.InvariantCulture)&&
                Int32.TryParse(i,NumberStyles.None,CultureInfo.InvariantCulture,out index)&&index>=0&&index<8&&i==index.ToString(CultureInfo.InvariantCulture);
        }
        internal ViveRawPreparedPlan Add(IReadOnlyDictionary<string,string> fields,string trial,int block,int segment,long sequence,double time,bool legacy=false)
        {
            string schema;
            if(!fields.TryGetValue("recording_plan_schema",out schema)||schema!="1")
            {if(UnsupportedEvents==Int32.MaxValue)throw new InvalidPlan();UnsupportedEvents++;return null;}
            string designPrefix=legacy?"legacy_":"named_";
            string scope=Required(fields,"plan_scope"),attemptText=Required(fields,"attempt"),list=Required(fields,"list"),countText=Required(fields,"item_count"),phase=Required(fields,designPrefix+"phase");
            int attempt,count;
            if(!Int32.TryParse(attemptText,NumberStyles.None,CultureInfo.InvariantCulture,out attempt)||attemptText!=attempt.ToString(CultureInfo.InvariantCulture)||
                !Int32.TryParse(countText,NumberStyles.None,CultureInfo.InvariantCulture,out count)||countText!=count.ToString(CultureInfo.InvariantCulture)||trial!=null||segment!=0)throw new InvalidPlan();
            if(scope=="PRACTICE")
            {if(block!=-1||(attempt!=1&&attempt!=2)||count!=8||(phase!="Preparation"&&phase!="Familiarization"&&phase!="PracticeRetry")||
                !PracticeListMatches(list,attempt)||priorPracticeAttempts[attempt-1]||priorPracticeLists.Contains(list))throw new InvalidPlan();}
            else if(scope=="MAIN")
            {
                string started,index;if(block<0||block>=4||attempt!=0||count!=24||phase!="BlockReady"||priorMainTrials[block]||
                    !fields.TryGetValue(designPrefix+"block_started",out started)||started!="False"||
                    !fields.TryGetValue(designPrefix+"list_index",out index)||!MainListMatches(list,index))throw new InvalidPlan();
            }
            else throw new InvalidPlan();
            string key=Key(scope,block,attempt);if(plans.ContainsKey(key))throw new InvalidPlan();
            if(scope=="PRACTICE")foreach(var existing in plans.Values)
                if(existing.Scope=="PRACTICE"&&String.Equals(existing.List,list,StringComparison.Ordinal))throw new InvalidPlan();
            var expected=new HashSet<string>(StringComparer.Ordinal){"item_count"};var values=new List<ViveRawPreparedPlanItem>(count);
            for(int i=0;i<count;i++)
            {
                string prefix="item_"+i.ToString("D2",CultureInfo.InvariantCulture),wordKey=prefix+"_word",inkKey=prefix+"_ink";
                expected.Add(wordKey);expected.Add(inkKey);string word=Required(fields,wordKey),ink=Required(fields,inkKey);
                if(!ColourName(word)||!ColourName(ink))throw new InvalidPlan();values.Add(new ViveRawPreparedPlanItem(i,word,ink));
            }
            foreach(string field in fields.Keys)if(field.StartsWith("item_",StringComparison.Ordinal)&&!expected.Contains(field))throw new InvalidPlan();
            string order=scope=="PRACTICE"&&untrackedPriorPractice?"UNKNOWN":"NO_CONTRADICTION_OBSERVED_IN_PREFIX";
            var result=new ViveRawPreparedPlan(scope,list,block,segment,attempt,sequence,time,values,order);plans.Add(key,result);return result;
        }
        internal void ObserveTrial(IReadOnlyDictionary<string,string> fields,int block,string session,string trialId)
        {
            string list,indexText;
            bool hasList=fields.TryGetValue("list",out list)&&!String.IsNullOrEmpty(list);
            ViveRawPreparedPlan selected=null;
            if(block<0)
            {
                int attempt;ViveRawPreparedPlan byAttempt=null;
                bool knownAttempt=TryPracticeId(session,trialId,out attempt);
                // A list declaration alone cannot establish which attempt an
                // unidentifiable prior trial actually belonged to.
                if(!knownAttempt)untrackedPriorPractice=true;
                if(knownAttempt&&!plans.TryGetValue(Key("PRACTICE",-1,attempt),out byAttempt))priorPracticeAttempts[attempt-1]=true;
                if(byAttempt!=null&&hasList&&!String.Equals(byAttempt.List,list,StringComparison.Ordinal))throw new InvalidPlan();
                if(hasList)foreach(var p in plans.Values)if(p.Scope=="PRACTICE"&&String.Equals(p.List,list,StringComparison.Ordinal))
                    {if(selected!=null)throw new InvalidPlan();selected=p;}
                if(selected==null)
                {
                    // Keep bounded evidence only for ordering checks if a v1
                    // plan appears later. Existing streams without a plan
                    // remain readable even if this prior history is unknown.
                    if(!hasList||list.Length>256||(!priorPracticeLists.Contains(list)&&priorPracticeLists.Count>=512))untrackedPriorPractice=true;
                    else priorPracticeLists.Add(list);
                }
            }
            else if(block<4)
            {plans.TryGetValue(Key("MAIN",block,0),out selected);if(selected==null)priorMainTrials[block]=true;
                else if(hasList&&!String.Equals(selected.List,list,StringComparison.Ordinal))throw new InvalidPlan();}
            if(selected==null)return;
            if(!hasList)return; // missing declaration remains unknown
            if(!fields.TryGetValue("planned_index",out indexText))return; // missing is not backfilled
            int index;if(!Int32.TryParse(indexText,NumberStyles.None,CultureInfo.InvariantCulture,out index)||indexText!=index.ToString(CultureInfo.InvariantCulture)||index<0||index>=selected.Items.Count)throw new InvalidPlan();
            // Verify an observed ID only when the plan, list and index are all
            // available. Never synthesize an ID for absent/partial trials.
            if(!String.IsNullOrEmpty(trialId))
            {
                string expected=session+(selected.Scope=="PRACTICE"?"/p"+selected.Attempt.ToString(CultureInfo.InvariantCulture):"/b"+block.ToString(CultureInfo.InvariantCulture))+"/t"+index.ToString(CultureInfo.InvariantCulture);
                if(!String.Equals(trialId,expected,StringComparison.Ordinal))throw new InvalidPlan();
            }
            string schema,word,ink;fields.TryGetValue("planned_stimulus_schema",out schema);fields.TryGetValue("planned_word",out word);fields.TryGetValue("planned_ink",out ink);
            string state=ViveRawTrialAccumulator.StimulusState(schema,word,ink);
            if(state=="CONFLICT")throw new InvalidPlan();
            if(state=="DECLARED_V1"&&(!String.Equals(word,selected.Items[index].Word,StringComparison.Ordinal)||!String.Equals(ink,selected.Items[index].Ink,StringComparison.Ordinal)))throw new InvalidPlan();
        }
    }
}
