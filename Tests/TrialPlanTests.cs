using System;
using System.Collections.Generic;
using System.Globalization;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class TrialPlanTests
    {
        public static void Register(TestSuite t)
        {
            t.Run("main lists preserve exact 24-trial composition without adjacent repeats", MainComposition);
            t.Run("four main lists are reproducible and retain distinct identities", MainLists);
            t.Run("practice balances each category and gives a different retry arrangement", PracticeComposition);
            t.Run("Williams allocation balances positions and all directed adjacent pairs", WilliamsBalance);
            t.Run("partial Williams sets stay even and B initial targets balance independently", PartialAndTargetBalance);
            t.Run("allocation uses every list and varies its condition mapping", ListAllocation);
            t.Run("allocation identity never changes factors across roster sizes", AllocationIdentity);
            t.Run("participant IDs link tasks and bind only their assigned apparatus configuration", ParticipantIdentity);
            t.Run("participant roster balances orders, lists and B starts separately for each task", ParticipantBalance);
            t.Run("planning rejects unsupported list and practice indices", InvalidInputs);
        }

        private static void MainComposition()
        {
            for (uint seed = 0; seed < 512; seed++) AssertMain(TrialPlans.Main(seed));
            AssertMain(TrialPlans.Main(uint.MaxValue));
        }

        private static void AssertMain(Stimulus[] trials)
        {
            Check.Equal(24, trials.Length);
            int[,] pairs = new int[4, 4];
            int congruent = 0;
            for (int index = 0; index < trials.Length; index++)
            {
                Stimulus item = trials[index];
                pairs[(int)item.Word, (int)item.Ink]++;
                if (item.Congruent) congruent++;
                if (index > 0) Check.True(item.Key != trials[index - 1].Key);
            }
            Check.Equal(12, congruent);
            for (int word = 0; word < 4; word++)
                for (int ink = 0; ink < 4; ink++) Check.Equal(word == ink ? 3 : 1, pairs[word, ink]);
        }

        private static void MainLists()
        {
            for (uint seed = 0; seed < 64; seed++)
            {
                var ids = new HashSet<string>();
                var orders = new HashSet<string>();
                for (int list = 0; list < 4; list++)
                {
                    Stimulus[] first = TrialPlans.MainList(seed, list);
                    Stimulus[] again = TrialPlans.MainList(seed, list);
                    AssertMain(first);
                    Check.Equal(OrderKey(first), OrderKey(again));
                    Check.True(ids.Add(TrialPlans.MainListId(seed, list)));
                    Check.True(orders.Add(OrderKey(first)));
                }
            }
        }

        private static void PracticeComposition()
        {
            for (uint seed = 0; seed < 256; seed++)
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    Stimulus[] trials = TrialPlans.Practice(seed, attempt);
                    Check.Equal(8, trials.Length);
                    int[,] words = new int[2, 4];
                    int[,] inks = new int[2, 4];
                    foreach (Stimulus item in trials)
                    {
                        int category = item.Congruent ? 0 : 1;
                        words[category, (int)item.Word]++;
                        inks[category, (int)item.Ink]++;
                    }
                    for (int category = 0; category < 2; category++)
                        for (int colour = 0; colour < 4; colour++)
                        {
                            Check.Equal(1, words[category, colour]);
                            Check.Equal(1, inks[category, colour]);
                        }
                    Check.Equal(OrderKey(trials), OrderKey(TrialPlans.Practice(seed, attempt)));
                }
                Check.True(OrderKey(TrialPlans.Practice(seed, 1)) != OrderKey(TrialPlans.Practice(seed, 2)));
            }
        }

        private static void WilliamsBalance()
        {
            Allocation[] allocations = TrialPlans.Allocate(40, 0);
            var accepted = new HashSet<string> { "ABDC", "BCAD", "CDBA", "DACB" };
            for (int start = 0; start < allocations.Length; start += 4)
            {
                var seen = new HashSet<string>();
                int[,] positions = new int[4, 4];
                int[,] adjacency = new int[4, 4];
                for (int participant = start; participant < start + 4; participant++)
                {
                    Condition[] order = allocations[participant].Order;
                    Check.True(accepted.Contains(ConditionKey(order)));
                    Check.True(seen.Add(ConditionKey(order)));
                    for (int position = 0; position < 4; position++)
                    {
                        positions[(int)order[position], position]++;
                        if (position > 0) adjacency[(int)order[position - 1], (int)order[position]]++;
                    }
                }
                for (int from = 0; from < 4; from++)
                    for (int to = 0; to < 4; to++)
                    {
                        Check.Equal(1, positions[from, to]);
                        Check.Equal(from == to ? 0 : 1, adjacency[from, to]);
                    }
            }
        }

        private static void PartialAndTargetBalance()
        {
            var targetsBySequence = new Dictionary<string, HashSet<GazeTarget>>();
            for (uint seed = 0; seed < 64; seed++)
            {
                for (int count = 0; count <= 17; count++)
                {
                    Allocation[] allocations = TrialPlans.Allocate(count, seed);
                    Check.Equal(count, allocations.Length);
                    int participantStarts = 0;
                    var sequenceCounts = new Dictionary<string, int>();
                    foreach (Allocation allocation in allocations)
                    {
                        if (allocation.BInitialTarget == GazeTarget.Participant) participantStarts++;
                        string sequence = ConditionKey(allocation.Order);
                        if (!sequenceCounts.ContainsKey(sequence)) sequenceCounts[sequence] = 0;
                        sequenceCounts[sequence]++;
                        if (!targetsBySequence.ContainsKey(sequence)) targetsBySequence[sequence] = new HashSet<GazeTarget>();
                        targetsBySequence[sequence].Add(allocation.BInitialTarget);
                    }
                    Check.True(Math.Abs(participantStarts * 2 - count) <= 1);
                    foreach (int number in sequenceCounts.Values)
                        Check.True(number == count / 4 || number == (count + 3) / 4);
                }
            }
            Check.Equal(4, targetsBySequence.Count);
            foreach (HashSet<GazeTarget> targets in targetsBySequence.Values) Check.Equal(2, targets.Count);
        }

        private static void ListAllocation()
        {
            Allocation[] allocations = TrialPlans.Allocate(64, 99);
            Allocation[] repeated = TrialPlans.Allocate(64, 99);
            var mappings = new HashSet<string>();
            for (int index = 0; index < allocations.Length; index++)
            {
                Allocation allocation = allocations[index];
                var lists = new HashSet<int>(allocation.ListByBlock);
                Check.Equal(4, lists.Count);
                for (int list = 0; list < 4; list++) Check.True(lists.Contains(list));
                string mapping = "";
                Condition[] order = allocation.Order;
                int[] listByBlock = allocation.ListByBlock;
                for (int condition = 0; condition < 4; condition++)
                    for (int block = 0; block < 4; block++)
                        if ((int)order[block] == condition) mapping += listByBlock[block].ToString();
                mappings.Add(mapping);
                Check.Equal(ConditionKey(allocation.Order), ConditionKey(repeated[index].Order));
                Check.Equal(allocation.BInitialTarget, repeated[index].BInitialTarget);
                Check.Equal(string.Join(",", allocation.ListByBlock), string.Join(",", repeated[index].ListByBlock));
            }
            Check.True(mappings.Count > 1);
        }

        private static void InvalidInputs()
        {
            Check.Throws<ArgumentOutOfRangeException>(() => TrialPlans.Practice(0, 0));
            Check.Throws<ArgumentOutOfRangeException>(() => TrialPlans.Practice(0, 3));
            Check.Throws<ArgumentOutOfRangeException>(() => TrialPlans.MainList(0, -1));
            Check.Throws<ArgumentOutOfRangeException>(() => TrialPlans.MainListId(0, 4));
            Check.Throws<ArgumentOutOfRangeException>(() => TrialPlans.Allocate(-1, 0));
        }

        static ParticipantAssignment Enroll(int number,int task)
        {
            ParticipantAssignment assignment;
            string id="p"+number.ToString("D3",CultureInfo.InvariantCulture);
            Check.True(ParticipantAssignment.TryCreate(id,task==2?TaskKind.VisualSearch:TaskKind.Stroop,
                task==0?ResponseRule.InkColor:ResponseRule.WordMeaning,out assignment));
            return assignment;
        }

        static void ParticipantIdentity()
        {
            foreach(string invalid in new[]{null,"","P000","P0","P1","P1.2","P0012","P+12","P١٢٣","Alice","P12A"})
            {
                ParticipantAssignment assignment;
                Check.True(!ParticipantAssignment.TryCreate(invalid,TaskKind.Stroop,ResponseRule.InkColor,out assignment));
                Check.True(assignment==null);
            }
            var differentOrders=new HashSet<string>();
            for(int task=0;task<3;task++)
            {
                var a=Enroll(1,task);
                Check.Equal("P001",a.ParticipantId);
                Check.Equal(new[]{"InkColor","WordMeaning","VisualSearch"}[task],a.TaskKey);
                Check.Equal("participant-balance-v1-P001-T"+(task+1),a.Allocation.AllocationId);
                Check.True(differentOrders.Add(ConditionKey(a.Allocation.Order)));
                Check.Equal(new[]{"CDBA","DACB","ABDC"}[task],ConditionKey(a.Allocation.Order));
                Check.Equal(new[]{"0,1,3,2","3,0,2,1","2,3,1,0"}[task],String.Join(",",a.Allocation.ListByBlock));
                var design=ViveLegacyRecordingDesign.SelectParticipant(Actor.Owner,"design",a,"geometry","mapping");
                var intent=ViveLegacyRecordingIntent.Select(Actor.Owner,"intent",true,true);
                var config=new SessionConfiguration("run",ParticipantAssignment.StimulusSeed,a.Allocation,RunKind.Apparatus,"en",5,responseRule:a.ResponseRule);
                config=SessionConfiguration.WithTaskKind(config,a.TaskKind);
                Check.Throws<InvalidOperationException>(()=>new Experiment(config,new ManualClock()));
                Check.Throws<ArgumentException>(()=>SessionConfiguration.LegacyRecordingDevelopment(config,design,intent));
                var session=SessionConfiguration.ParticipantRecording(config,design,intent);
                Check.Equal(a,session.ParticipantAssignment);
                var e=new Experiment(session,new ManualClock());
                Check.Equal(a.ParticipantId,e.Events[0].Fields["participant_id"]);
                Check.Equal("Apparatus",e.Events[0].Fields["run_kind"]);
                Check.Throws<InvalidOperationException>(()=>SessionConfiguration.WithTaskKind(session,
                    session.TaskKind==TaskKind.Stroop?TaskKind.VisualSearch:TaskKind.Stroop));
            }
        }

        static void ParticipantBalance()
        {
            for(int task=0;task<3;task++)
            for(int set=0;set<30;set++)
            {
                var crossed=new HashSet<string>();
                var orderAndLists=new HashSet<string>();
                var listPositions=new int[4,4];
                for(int cohort=0;cohort<8;cohort++)
                {
                    var orders=new HashSet<string>();
                    int[,] conditionLists=new int[4,4];
                    int participantStarts=0;
                    for(int slot=0;slot<4;slot++)
                    {
                        int number=1+32*set+4*cohort+slot;
                        var a=Enroll(number,task).Allocation;
                        string order=ConditionKey(a.Order);
                        string lists=String.Join(",",a.ListByBlock);
                        Check.True(orders.Add(order));
                        Check.True(orderAndLists.Add(order+"/"+lists));
                        Check.True(crossed.Add(order+"/"+lists+"/"+a.BInitialTarget));
                        if(a.BInitialTarget==GazeTarget.Participant)participantStarts++;
                        for(int block=0;block<4;block++)
                        {
                            conditionLists[(int)a.Order[block],a.ListByBlock[block]]++;
                            listPositions[block,a.ListByBlock[block]]++;
                        }
                        var repeated=Enroll(number,task).Allocation;
                        Check.Equal(a.BInitialTarget,repeated.BInitialTarget);
                        Check.Equal(order,ConditionKey(repeated.Order));
                        Check.Equal(lists,String.Join(",",repeated.ListByBlock));
                    }
                    Check.Equal(2,participantStarts);
                    foreach(int count in conditionLists)Check.Equal(1,count);
                    if(cohort%4==3)
                    {
                        Check.Equal(16,orderAndLists.Count);
                        foreach(int count in listPositions)Check.Equal(4,count);
                        orderAndLists.Clear();
                        Array.Clear(listPositions,0,listPositions.Length);
                    }
                }
                Check.Equal(32,crossed.Count);
            }
        }

        private static void AllocationIdentity()
        {
            var factorsById = new Dictionary<string, string>();
            for (uint seed = 0; seed < 16; seed++)
            {
                for (int count = 1; count <= 17; count++)
                {
                    // Revisit identical plans as well as different roster sizes.
                    for (int replay = 0; replay < 2; replay++)
                    {
                        foreach (Allocation allocation in TrialPlans.Allocate(count, seed))
                        {
                            string factors = ConditionKey(allocation.Order) + "/" + allocation.BInitialTarget + "/" + string.Join(",", allocation.ListByBlock);
                            if (factorsById.ContainsKey(allocation.AllocationId))
                                Check.Equal(factorsById[allocation.AllocationId], factors);
                            else factorsById.Add(allocation.AllocationId, factors);
                        }
                    }
                }
            }
        }

        private static string OrderKey(Stimulus[] trials)
        {
            var keys = new string[trials.Length];
            for (int index = 0; index < trials.Length; index++) keys[index] = trials[index].Key;
            return string.Join(";", keys);
        }

        private static string ConditionKey(Condition[] order)
        {
            string result = "";
            foreach (Condition condition in order) result += condition.ToString();
            return result;
        }
    }
}
