using System;

namespace AvatarStroop.Core
{
    /// <summary>
    /// Reproducible planning for the accepted 24-trial, four-condition protocol.
    /// These schedules have no response, accuracy, or gaze inputs.
    /// </summary>
    public static class TrialPlans
    {
        private static readonly Condition[][] Williams =
        {
            new[] { Condition.A, Condition.B, Condition.D, Condition.C },
            new[] { Condition.B, Condition.C, Condition.A, Condition.D },
            new[] { Condition.C, Condition.D, Condition.B, Condition.A },
            new[] { Condition.D, Condition.A, Condition.C, Condition.B }
        };

        public static Stimulus[] Main(uint seed)
        {
            var types = new Stimulus[16];
            var counts = new int[16];
            for (int word = 0; word < 4; word++)
            {
                for (int ink = 0; ink < 4; ink++)
                {
                    int index = word * 4 + ink;
                    types[index] = new Stimulus((Colour)word, (Colour)ink);
                    counts[index] = word == ink ? 3 : 1;
                }
            }

            var random = new Random32(seed);
            var result = new Stimulus[24];
            int previous = -1;
            for (int position = 0; position < result.Length; position++)
            {
                int remaining = result.Length - position - 1;
                int totalWeight = 0;
                var weights = new int[types.Length];
                for (int candidate = 0; candidate < types.Length; candidate++)
                {
                    if (candidate == previous || counts[candidate] == 0) continue;
                    counts[candidate]--;
                    bool feasible = true;
                    // The next item cannot match this candidate. The remaining
                    // count of it therefore has one fewer available slot when
                    // the remainder is odd. This prevents a dead end without
                    // retries, backtracking, or a limit-dependent fallback.
                    for (int other = 0; other < counts.Length; other++)
                    {
                        int maximum = other == candidate ? remaining / 2 : (remaining + 1) / 2;
                        if (counts[other] > maximum) { feasible = false; break; }
                    }
                    counts[candidate]++;
                    if (feasible)
                    {
                        weights[candidate] = counts[candidate];
                        totalWeight += weights[candidate];
                    }
                }

                if (totalWeight == 0) throw new InvalidOperationException("No valid stimulus continuation.");
                int draw = random.Next(totalWeight);
                int selected = 0;
                for (; selected < weights.Length; selected++)
                {
                    draw -= weights[selected];
                    if (draw < 0) break;
                }
                result[position] = types[selected];
                counts[selected]--;
                previous = selected;
            }
            return result;
        }

        public static uint MainListSeed(uint sessionSeed, int listIndex)
        {
            ValidateListIndex(listIndex);
            return DeriveSeed(sessionSeed, unchecked(0xA341316Cu + (uint)listIndex * 0x9E3779B9u));
        }

        public static string MainListId(uint sessionSeed, int listIndex)
        {
            return "main-v1-" + listIndex + "-" + MainListSeed(sessionSeed, listIndex).ToString("x8");
        }

        public static Stimulus[] MainList(uint sessionSeed, int listIndex)
        {
            ValidateListIndex(listIndex);
            var earlier = new Stimulus[listIndex + 1][];
            for (int index = 0; index <= listIndex; index++)
            {
                Stimulus[] candidate = Main(MainListSeed(sessionSeed, index));
                if (MatchesEarlier(candidate, earlier, index))
                {
                    // Seeded shuffles can in principle coincide. A bounded
                    // pair-swap search guarantees a distinct constrained order
                    // while preserving the exact stimulus multiset.
                    bool repaired = false;
                    for (int left = 0; left < candidate.Length - 1 && !repaired; left++)
                    {
                        for (int right = left + 1; right < candidate.Length; right++)
                        {
                            Stimulus value = candidate[left];
                            candidate[left] = candidate[right];
                            candidate[right] = value;
                            if (NoAdjacentRepeat(candidate) && !MatchesEarlier(candidate, earlier, index))
                            {
                                repaired = true;
                                break;
                            }
                            value = candidate[left];
                            candidate[left] = candidate[right];
                            candidate[right] = value;
                        }
                    }
                    if (!repaired) throw new InvalidOperationException("Could not make a distinct constrained list.");
                }
                earlier[index] = candidate;
            }
            return earlier[listIndex];
        }

        public static Stimulus[] Practice(uint seed, int attempt)
        {
            if (attempt < 1 || attempt > 2) throw new ArgumentOutOfRangeException("attempt", "Practice has two total attempts, numbered 1 and 2.");
            var random = new Random32(DeriveSeed(seed, unchecked(0xC8013EA4u + (uint)attempt)));
            var result = new Stimulus[8];
            var cycle = new[] { 0, 1, 2, 3 };
            Shuffle(cycle, random);
            int shift = 1 + random.Next(3);
            for (int index = 0; index < 4; index++)
            {
                result[index] = new Stimulus((Colour)index, (Colour)index);
                result[index + 4] = new Stimulus((Colour)cycle[index], (Colour)cycle[(index + shift) % 4]);
            }
            Shuffle(result, random);
            // Distinct seeds usually suffice, but the retry arrangement must
            // differ even if both independently generated arrangements match.
            if (attempt == 2 && SameOrder(result, Practice(seed, 1)))
            {
                Stimulus first = result[0];
                result[0] = result[1];
                result[1] = first;
            }
            return result;
        }

        /// <summary>
        /// Pre-generates one frozen allocation roster. Retain this roster and
        /// record assignments/completions; do not regenerate it after withdrawals
        /// or select replacement allocations from participant outcomes. A changed
        /// count creates a different identified roster, not an extension of one.
        /// </summary>
        public static Allocation[] Allocate(int count, uint seed)
        {
            if (count < 0) throw new ArgumentOutOfRangeException("count");
            var sequenceRandom = new Random32(DeriveSeed(seed, 0xAD90777Du));
            var targetRandom = new Random32(DeriveSeed(seed, 0x7E95761Eu));
            var listRandom = new Random32(DeriveSeed(seed, 0x4CF5AD43u));
            var targets = new GazeTarget[count];
            int start = targetRandom.Next(2);
            for (int index = 0; index < count; index++)
                targets[index] = (index + start) % 2 == 0 ? GazeTarget.Participant : GazeTarget.Task;
            Shuffle(targets, targetRandom);

            var result = new Allocation[count];
            var sequenceIndices = new[] { 0, 1, 2, 3 };
            for (int index = 0; index < count; index++)
            {
                if (index % 4 == 0)
                {
                    for (int sequence = 0; sequence < 4; sequence++) sequenceIndices[sequence] = sequence;
                    Shuffle(sequenceIndices, sequenceRandom);
                }
                var listByBlock = new[] { 0, 1, 2, 3 };
                Shuffle(listByBlock, listRandom);
                result[index] = new Allocation(
                    "allocation-v1-roster" + count + "-" + seed.ToString("x8") + "-" + (index + 1).ToString("D4"),
                    Williams[sequenceIndices[index % 4]], targets[index], listByBlock);
            }
            return result;
        }

        private static bool SameOrder(Stimulus[] left, Stimulus[] right)
        {
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++)
                if (left[index].Word != right[index].Word || left[index].Ink != right[index].Ink) return false;
            return true;
        }

        private static bool MatchesEarlier(Stimulus[] candidate, Stimulus[][] earlier, int count)
        {
            for (int index = 0; index < count; index++)
                if (SameOrder(candidate, earlier[index])) return true;
            return false;
        }

        private static bool NoAdjacentRepeat(Stimulus[] items)
        {
            for (int index = 1; index < items.Length; index++)
                if (items[index].Word == items[index - 1].Word && items[index].Ink == items[index - 1].Ink) return false;
            return true;
        }

        private static void ValidateListIndex(int listIndex)
        {
            if (listIndex < 0 || listIndex >= 4) throw new ArgumentOutOfRangeException("listIndex", "There are four main stimulus lists, indexed 0 through 3.");
        }

        private static uint DeriveSeed(uint seed, uint stream)
        {
            unchecked
            {
                uint value = seed + stream;
                value = (value ^ (value >> 16)) * 0x85EBCA6Bu;
                value = (value ^ (value >> 13)) * 0xC2B2AE35u;
                return value ^ (value >> 16);
            }
        }

        private static void Shuffle<T>(T[] items, Random32 random)
        {
            for (int index = items.Length - 1; index > 0; index--)
            {
                int other = random.Next(index + 1);
                T value = items[index];
                items[index] = items[other];
                items[other] = value;
            }
        }

        // Stable across Unity/.NET versions; never relies on System.Random's
        // implementation. A bounded modulo draw is adequate for this pilot
        // planning helper; it is not a cryptographic randomization service.
        private sealed class Random32
        {
            private uint state;
            public Random32(uint seed) { state = seed; }
            public int Next(int exclusiveMaximum)
            {
                if (exclusiveMaximum <= 0) throw new ArgumentOutOfRangeException("exclusiveMaximum");
                unchecked
                {
                    state += 0x9E3779B9u;
                    uint value = state;
                    value = (value ^ (value >> 16)) * 0x85EBCA6Bu;
                    value = (value ^ (value >> 13)) * 0xC2B2AE35u;
                    value ^= value >> 16;
                    return (int)(value % (uint)exclusiveMaximum);
                }
            }
        }
    }
}
