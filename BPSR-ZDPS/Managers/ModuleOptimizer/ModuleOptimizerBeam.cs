using BPSR_ZDPS.DataTypes.Modules;
using BPSR_ZDPS.Managers.Modules;
using Serilog;
using System.Diagnostics;
using System.Numerics;
using ZLinq;

namespace BPSR_ZDPS.Managers
{
    public class ModuleOptimizerBeam : ModuleOptimizerBase
    {
        public int BeamWidth = 25000;
        private int[] StatScoreLookup;
        private byte[] RequirementMetLookup;
        private ushort[] StatProgressLookup;
        private byte[] StatIndexes;
        private int[] LinkTotalFightLookup = [];
        private int MaxLinkTotal;
        private static readonly int[] EnhanceTiers = [1, 4, 8, 12, 16, 20];

        private bool CombatMode => Config.ScoreMode == ScoreMode.CombatPower;

        public ModuleOptimizerBeam(SolverConfig config, PlayerModDataSave playerMods, Stopwatch sw, List<long> filtered, CancellationToken cancelToken) : base(config, playerMods, sw, filtered, cancelToken)
        {
            var numElements = Vector<byte>.Count * (MAX_STAT_VALUE_TOTAL + 1);
            StatScoreLookup = new int[numElements];
            RequirementMetLookup = new byte[numElements];
            StatProgressLookup = new ushort[numElements];
        }

        public override ModComboResult[] InnerSolve(Vector<byte>[] mods)
        {
            StatIndexes = NormalizedStatPrios.Select(x => (byte)x.Id).ToArray();
            BuildStatScoreLookup(NormalizedStatPrios);
            if (CombatMode)
            {
                BuildLinkTotalFightLookup();
            }

            var beam = new List<BeamNode>();
            beam.Add(new BeamNode() { Score = 0, CurrentSet = new ModuleSetIndices() });

            // Clamp like the GPU path does; ModArr is a fixed buffer of MaxModules entries.
            var numModules = Math.Clamp(Config.NumModules, 1, ModuleSet.MaxModules);
            for (int i = 0; i < numModules; i++)
            {
                var candidates = new List<BeamNode>();

                Log.Information($"Depth={i}, Beam={beam.Count}, EstimatedCandidates={beam.Count * mods.Length:N0}");

                Parallel.ForEach(beam,
                    () => new TopK(BeamWidth),
                    (node, _, local) =>
                    {
                        for (int modIdx = 0; modIdx < mods.Length; modIdx++)
                        {
                            Vector<byte> mod = mods[modIdx];
                            var skip = false;
                            unsafe
                            {
                                for (int modIdx2 = 0; modIdx2 < numModules; modIdx2++)
                                {
                                    if (node.CurrentSet.ModArr[modIdx2] == modIdx)
                                    {
                                        skip = true;
                                        break;
                                    }
                                }
                            }

                            if (skip)
                                continue;

                            var newCandidate = new BeamNode()
                            {
                                CurrentSet = new ModuleSetIndices()
                            };

                            newCandidate.CurrentSet = node.CurrentSet;
                            unsafe
                            {
                                newCandidate.CurrentSet.ModArr[node.Depth] = (short)modIdx;
                            }

                            newCandidate.Totals = Vector.Add(node.Totals, mod);
                            newCandidate.Depth = (byte)(i + 1);

                            ScoreBeamNode(ref newCandidate);

                            local.Add(newCandidate);
                            if (CancellationToken.IsCancellationRequested)
                                return local;
                        }

                        return local;
                    },
                    local =>
                    {
                        lock (candidates)
                        {
                            candidates.AddRange(local.ToList());
                        }
                    });

                if (CancellationToken.IsCancellationRequested)
                    return [];

                var sw = Stopwatch.StartNew();
                beam = GetTopK(candidates, BeamWidth);

                sw.Stop();

                Log.Information($"Candidate order took: {sw.Elapsed}, depth: {i}");
            }

            var meetsRequirements = beam.Where(x => x.RequirementsMet == NormalizedStatPrios.Count);
            var distinctTop = GetDistinctTopResults(meetsRequirements.DistinctBy(x => x.GetHash()), 10, 10);
            var bestX = distinctTop.AsValueEnumerable()
                .Select(x => BeamToResult(x))
                .ToArray();

            return bestX;
        }

        protected void BuildStatScoreLookup(List<StatPrio> statPrios)
        {
            // normalized index -> original stat id (needed for legendary lookup and combat FightValues)
            var origByNorm = PossibleStats.ToDictionary(kv => kv.Value, kv => kv.Key);

            for (int i = 0; i < Vector<byte>.Count; i++)
            {
                origByNorm.TryGetValue(i, out var origStatId); // 0 when this lane maps to no stat

                for (int x = 0; x <= MAX_STAT_VALUE_TOTAL; x++)
                {
                    var idx = i * (MAX_STAT_VALUE_TOTAL + 1) + x;
                    var statPrio = statPrios.FirstOrDefault(stat => stat.Id == i);

                    if (statPrio != null)
                    {
                        var prioPos = Config.StatPriorities.FindIndex(p => p.Id == origStatId);
                        var reqLevel = Math.Max((byte)0, statPrio.ReqLevel);

                        if (statPrio.StatMode == StatMode.Exactly)
                        {
                            // The requirement gate pins this stat to ReqLevel; score it at that value.
                            var score = ScoreStatValue(origStatId, statPrio.ReqLevel, prioPos);

                            if (x == statPrio.ReqLevel)
                            {
                                StatScoreLookup[idx] = score;
                                RequirementMetLookup[idx] = 1;

                                var pct = statPrio.ReqLevel > 0 ? Math.Min(x, statPrio.ReqLevel) * 100 / statPrio.ReqLevel : 100;
                                StatProgressLookup[idx] = (ushort)pct;
                            }
                            else
                            {
                                var progress = reqLevel > 0 ? x / (double)reqLevel : 0;
                                StatScoreLookup[idx] = (int)(score * progress);
                            }
                        }
                        else
                        {
                            var score = ScoreStatValue(origStatId, x, prioPos);

                            if (x >= reqLevel)
                            {
                                StatScoreLookup[idx] = score;
                                RequirementMetLookup[idx] = 1;
                            }
                            else
                            {
                                var progress = reqLevel > 0 ? x / (double)reqLevel : 0;
                                StatScoreLookup[idx] = (int)(score * progress);
                            }

                            var pct = statPrio.ReqLevel > 0 ? Math.Min(x, statPrio.ReqLevel) * 100 / statPrio.ReqLevel : 100;
                            StatProgressLookup[idx] = (ushort)pct;
                        }
                    }
                    else if (CombatMode)
                    {
                        // Combat power counts every stat regardless of ValueAllStats
                        // (mirrors CalcCombosCombatScore / the GPU CombatPower branch).
                        StatScoreLookup[idx] = CalcCombatStatScore(origStatId, x);
                        RequirementMetLookup[idx] = 0;
                        StatProgressLookup[idx] = 0;
                    }
                    else if (Config.ValueAllStats)
                    {
                        var statMul = GetStatMul(-1);
                        var score = CalcScore(x, -1, -1, statMul);
                        StatScoreLookup[idx] = score;
                        RequirementMetLookup[idx] = 0;
                        StatProgressLookup[idx] = 0;
                    }
                }
            }
        }

        /// <summary>Scores one priority stat's total under the active ScoreMode.</summary>
        private int ScoreStatValue(int origStatId, int statValue, int prioPos)
        {
            if (CombatMode)
            {
                return CalcCombatStatScore(origStatId, statValue);
            }

            return CalcScore(statValue, prioPos, Config.StatPriorities.Count, GetStatMul(origStatId));
        }

        /// <summary>Per-stat FightValue at the enhancement tier reached by <paramref name="statValue"/>.</summary>
        private int CalcCombatStatScore(int origStatId, int statValue)
        {
            if (origStatId <= 0 || statValue < 1)
            {
                return 0;
            }

            int enhanceLevel = 0;
            foreach (var tier in EnhanceTiers)
            {
                if (statValue >= tier)
                {
                    enhanceLevel = tier;
                }
                else
                {
                    break;
                }
            }

            return ModuleSolver.StatCombatScores.TryGetValue($"{origStatId}_{enhanceLevel}", out var v) ? v : 0;
        }

        public static float GetOrderBoost(float strength, int itemPos, int numItems)
        {
            // Non-linear falloff: the top priority (pos 0) is worth ~numItems,
            // dropping as 1/(pos+1)^strength down the list, never below 1.
            var weight = 1.0 / Math.Pow(itemPos + 1, strength);
            var boost = numItems * weight;

            return (float)Math.Max(1, boost);
        }

        protected int CalcScore(int statValue, int statIdx, int numStats, float statMul)
        {
            // Restored upstream formula: (breakpoint level x link bonus) weighted by
            // legendary multiplier and priority-order boost, with raw points above the
            // snapped breakpoint ("overcap") added unweighted.
            var breakPointBonus = GetLinkLevelBoost(statValue);
            var orderBoost = statIdx >= 0 ? GetOrderBoost(Config.OrderBoostStrength, statIdx, numStats) : 1f;
            var bpLevel = SnapToBreakPointLevel(statValue);
            var leftOverPoints = statValue - bpLevel;

            return (int)((bpLevel * breakPointBonus) * statMul * orderBoost + leftOverPoints);
        }

        protected void ScoreBeamNode(ref BeamNode beamNode)
        {
            beamNode.Score = 0;
            beamNode.RequirementsMet = 0;
            beamNode.RequirementProgress = 0;

            int totalLinks = 0;
            for (int statIdx = 0; statIdx < Vector<byte>.Count; statIdx++)
            {
                //var statIdx = StatIndexes[i];
                totalLinks += beamNode.Totals[statIdx];
                var totalVal = Math.Min((byte)MAX_STAT_VALUE_TOTAL, beamNode.Totals[statIdx]);
                var lookupIdx = statIdx * (MAX_STAT_VALUE_TOTAL + 1) + totalVal;
                var statScore = StatScoreLookup[lookupIdx];
                beamNode.Score += statScore;
                beamNode.RequirementsMet += RequirementMetLookup[lookupIdx];
                beamNode.RequirementProgress += StatProgressLookup[lookupIdx];
            }

            if (CombatMode)
            {
                // Global enhancement bonus from the total link count (mirrors CalcCombosCombatScore).
                beamNode.Score += LinkTotalFightLookup[Math.Min(totalLinks, MaxLinkTotal)];
            }
        }

        private void BuildLinkTotalFightLookup()
        {
            MaxLinkTotal = Math.Max(1, Config.NumModules * 20);
            LinkTotalFightLookup = new int[MaxLinkTotal + 1];
            for (int t = 0; t <= MaxLinkTotal; t++)
            {
                LinkTotalFightLookup[t] = HelperMethods.DataTables.ModLinkEffects.Data.TryGetValue(t + 1, out var entry)
                    ? entry?.FightValue ?? 0
                    : 0;
            }
        }

        protected ModComboResult BeamToResult(BeamNode beam)
        {
            var result = new ModComboResult();
            var vals = new int[ModuleSet.MaxModules];
            unsafe
            {
                for (int i = 0; i < ModuleSet.MaxModules; i++)
                {
                    vals[i] = beam.CurrentSet.ModArr[i];
                }
            }
            result.ModuleSet = ModuleSet.FromValues(vals);

            result.Score = (int)beam.Score;
            return result;
        }

        protected List<BeamNode> GetTopK(IEnumerable<BeamNode> candidates, int size)
        {
            var heap = new PriorityQueue<BeamNode, double>();

            foreach (var candidate in candidates)
            {
                if (heap.Count < size)
                {
                    heap.Enqueue(candidate, candidate.Score);
                    continue;
                }

                heap.TryPeek(out var worst, out var worstScore);

                if (!IsBetter(candidate, worst))
                    continue;

                heap.Dequeue();
                heap.Enqueue(candidate, CreatePriority(candidate));
            }

            var result = new List<BeamNode>(heap.Count);

            while (heap.Count > 0)
            {
                result.Add(heap.Dequeue());
            }

            result.Sort((a, b) => b.Score.CompareTo(a.Score));

            return result;
        }

        protected static bool IsBetter(BeamNode a, BeamNode b)
        {
            if (a.RequirementProgress != b.RequirementProgress)
                return a.RequirementProgress > b.RequirementProgress;

            if (a.RequirementsMet != b.RequirementsMet)
                return a.RequirementsMet > b.RequirementsMet;

            return a.Score > b.Score;
        }

        protected static long CreatePriority(BeamNode x)
        {
            return ((long)x.RequirementsMet << 48) | ((long)x.RequirementProgress << 32) | (uint)Math.Max(0, (int)(x.Score * 1000));
        }

        protected List<BeamNode> GetDistinctTopResults(IEnumerable<BeamNode> candidates, int maxResults = 10, int minStatDifference = 10)
        {
            var results = new List<BeamNode>();

            foreach (var candidate in candidates.OrderBy(x => x, Comparer<BeamNode>.Create(CompareBeamStates)))
            {
                bool distinct = true;

                foreach (var existing in results)
                {
                    if (StatDifference(existing, candidate, StatIndexes) < minStatDifference)
                    {
                        distinct = false;
                        break;
                    }
                }

                if (!distinct)
                    continue;

                results.Add(candidate);

                if (results.Count == maxResults)
                    break;
            }

            return results;
        }

        protected int StatDifference(BeamNode a, BeamNode b, byte[] statIndexes)
        {
            int diff = 0;

            foreach (var statIdx in statIndexes)
            {
                var statA = Math.Min(a.Totals[statIdx], (byte)MAX_STAT_VALUE_TOTAL);
                var statB = Math.Min(b.Totals[statIdx], (byte)MAX_STAT_VALUE_TOTAL);
                diff += Math.Abs(statA - statB);
            }

            return diff;
        }

        private int CompareBeamStates(BeamNode a, BeamNode b)
        {
            if (a.RequirementsMet != b.RequirementsMet)
                return b.RequirementsMet.CompareTo(a.RequirementsMet);

            if (a.RequirementProgress != b.RequirementProgress)
                return b.RequirementProgress.CompareTo(a.RequirementProgress);

            if (a.Score != b.Score)
                return b.Score.CompareTo(a.Score);

            var numBetterStatsA = 0;
            var numBetterStatsB = 0;
            foreach (var stat in NormalizedStatPrios)
            {
                var statTotalA = Math.Min((byte)MAX_STAT_VALUE, a.Totals[stat.Id]);
                var statTotalB = Math.Min((byte)MAX_STAT_VALUE, b.Totals[stat.Id]);
                if (statTotalA > statTotalB)
                    numBetterStatsA++;
                else if (statTotalA < statTotalB)
                    numBetterStatsB++;
            }

            if (numBetterStatsA < numBetterStatsB)
                return -1;
            else if (numBetterStatsA > numBetterStatsB)
                return 1;

            var combatScoreA = ResloveResults([BeamToResult(a)])[0].CombatScore;
            var combatScoreB = ResloveResults([BeamToResult(b)])[0].CombatScore;

            if (combatScoreA < combatScoreB)
                return -1;
            else if (combatScoreA > combatScoreB)
                return 1;

            var aTotalStats = Vector.Sum(a.Totals);
            var bTotalStats = Vector.Sum(b.Totals);

            if (aTotalStats < bTotalStats)
                return -1;
            else if (aTotalStats > bTotalStats)
                return 1;

            return 0;
        }

        public sealed class TopK
        {
            private int Size;
            private PriorityQueue<BeamNode, double> Heap = new();

            public TopK(int size)
            {
                Size = size;
            }

            public void Add(BeamNode candidate)
            {
                if (Heap.Count < Size)
                {
                    Heap.Enqueue(candidate, candidate.Score);
                    return;
                }

                Heap.TryPeek(out var worst, out var worstScore);

                if (!IsBetter(candidate, worst))
                    return;

                Heap.Dequeue();
                Heap.Enqueue(candidate, CreatePriority(candidate));
            }

            public List<BeamNode> ToList()
            {
                var result = new List<BeamNode>(Heap.Count);

                while (Heap.Count > 0)
                {
                    result.Add(Heap.Dequeue());
                }

                return result;
            }
        }

        public struct BeamNode
        {
            public ModuleSetIndices CurrentSet;
            public byte Depth;
            public int Score;
            public Vector<byte> Totals;
            public byte RequirementsMet;
            public ushort RequirementProgress;

            public ulong GetHash()
            {
                ulong hash = 14695981039346656037UL;

                for (int i = 0; i < Vector<byte>.Count; i++)
                {
                    hash ^= Math.Min(Totals[i], (byte)20);
                    hash *= 1099511628211UL;
                }

                return hash;
            }
        }
    }
}
