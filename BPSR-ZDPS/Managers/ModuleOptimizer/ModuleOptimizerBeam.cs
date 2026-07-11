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
        // Score applied to a stat total that exceeds its upper-bound cap, so beam nodes
        // that violate a cap sink far below any valid (capped) node.
        private const int CapExceededPenalty = -1_000_000;

        private bool CombatMode => Config.ScoreMode == ScoreMode.CombatPower;
        private bool OriginalScoring => Config.ScoringModel == ScoringModel.Original;

        public ModuleOptimizerBeam(SolverConfig config, PlayerModDataSave playerMods, Stopwatch sw, List<long> filtered, CancellationToken cancelToken) : base(config, playerMods, sw, filtered, cancelToken)
        {
            var numElements = Vector<byte>.Count * (MAX_STAT_VALUE_TOTAL + 1);
            StatScoreLookup = new int[numElements];
            RequirementMetLookup = new byte[numElements];
            StatProgressLookup = new ushort[numElements];
        }

        public override ModComboResult[] InnerSolve(Vector<byte>[] mods)
        {
            // Distinctness is measured across every present stat lane. Restricting it to the
            // priority lanes collapses all requirement-meeting results into one (their
            // priority totals are identical), leaving a single result in the UI.
            StatIndexes = PossibleStats.Values.Select(v => (byte)v).ToArray();
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

                int depth = i;
                int beamSize = beam.Count;
                int processedNodes = 0;

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

                        // Depth-weighted progress: each depth advances 1/numModules, filled by
                        // the fraction of beam nodes expanded (throttled to every 64th node).
                        var done = Interlocked.Increment(ref processedNodes);
                        if ((done & 63) == 0 || done == beamSize)
                        {
                            ProgressCallback?.Invoke((depth + done / (float)beamSize) / numModules);
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

            // Local-search polish: the beam prunes on intermediate rankings, so ancestors
            // of the true optimum can be lost mid-search. Hill climbing from strong
            // finished sets recovers most of those misses at negligible cost.
            beam = RefineBeam(beam, mods, numModules);

            var meetsRequirements = beam.Where(x => x.RequirementsMet == NormalizedStatPrios.Count);
            // Threshold 1: only stat-profile duplicates collapse, so up to 10 results survive
            // (a threshold of 10 used to merge every requirement-meeting set into one entry).
            var distinctTop = GetDistinctTopResults(meetsRequirements.DistinctBy(x => x.GetHash()), 10, 1);
            var bestX = distinctTop.AsValueEnumerable()
                .Select(x => BeamToResult(x))
                .ToArray();

            return bestX;
        }

        /// <summary>
        /// Post-beam local search: steepest-ascent 1-swap hill climbing from a diverse set
        /// of finished beam nodes. Each sweep tries replacing every chosen module with every
        /// unused candidate and applies the best improvement (by the canonical priority)
        /// until a local optimum. Cost is ~K*N evaluations per sweep per seed - negligible
        /// next to the beam itself - and it recovers most sets the beam pruned mid-search.
        /// </summary>
        private List<BeamNode> RefineBeam(List<BeamNode> beam, Vector<byte>[] mods, int numModules)
        {
            if (beam.Count == 0 || mods.Length <= numModules)
                return beam;

            const int TopSeeds = 128, SpreadSeeds = 64, MaxSweeps = 10;

            // Seeds: the strongest nodes by canonical priority plus an even sample of the
            // rest, so climbing starts from several basins rather than near-duplicates.
            var byPrio = beam.OrderByDescending(CreatePriority).ToList();
            var seeds = new List<BeamNode>(TopSeeds + SpreadSeeds);
            seeds.AddRange(byPrio.Take(TopSeeds));
            if (byPrio.Count > TopSeeds)
            {
                int step = Math.Max(1, (byPrio.Count - TopSeeds) / SpreadSeeds);
                for (int i = TopSeeds; i < byPrio.Count; i += step)
                    seeds.Add(byPrio[i]);
            }

            var refined = new BeamNode[seeds.Count];
            Parallel.For(0, seeds.Count, si =>
            {
                refined[si] = OneSwapClimb(seeds[si], mods, numModules, MaxSweeps);
            });

            // Second phase: 2-swap on the strongest climbed nodes. Exactly/cap gates form
            // equality ridges a single swap cannot cross (two stats must change at once
            // while every gate stays satisfied), so module PAIRS are tried on a few seeds,
            // re-polishing with 1-swaps after each accepted pair move.
            const int PairSeeds = 12, PairRounds = 3;
            var pairTop = refined.OrderByDescending(CreatePriority)
                .Take(PairSeeds).ToArray();
            var pairRefined = new BeamNode[pairTop.Length];
            Parallel.For(0, pairTop.Length, ti =>
            {
                var node = pairTop[ti];
                for (int round = 0; round < PairRounds && !CancellationToken.IsCancellationRequested; round++)
                {
                    if (!TwoSwapStep(ref node, mods, numModules))
                        break;
                    node = OneSwapClimb(node, mods, numModules, MaxSweeps);
                }
                pairRefined[ti] = node;
            });

            // Refined nodes join the pool; duplicates collapse later via DistinctBy(GetHash).
            var merged = new List<BeamNode>(beam.Count + refined.Length + pairRefined.Length);
            merged.AddRange(pairRefined);
            merged.AddRange(refined);
            merged.AddRange(beam);
            return merged;
        }

        /// <summary>Steepest-ascent 1-swap hill climbing to a local optimum (or maxSweeps).</summary>
        private BeamNode OneSwapClimb(BeamNode node, Vector<byte>[] mods, int numModules, int maxSweeps)
        {
            for (int sweep = 0; sweep < maxSweeps && !CancellationToken.IsCancellationRequested; sweep++)
            {
                long bestPrio = CreatePriority(node);
                bool improved = false;
                BeamNode bestNode = default;

                for (int p = 0; p < numModules; p++)
                {
                    short oldIdx;
                    unsafe { oldIdx = node.CurrentSet.ModArr[p]; }
                    if (oldIdx < 0)
                        continue;

                    var baseTotals = Vector.Subtract(node.Totals, mods[oldIdx]);

                    for (int m = 0; m < mods.Length; m++)
                    {
                        if (IsUsed(ref node, m, numModules))
                            continue;

                        var cand = node;
                        unsafe { cand.CurrentSet.ModArr[p] = (short)m; }
                        cand.Totals = Vector.Add(baseTotals, mods[m]);
                        ScoreBeamNode(ref cand);

                        long prio = CreatePriority(cand);
                        if (prio > bestPrio)
                        {
                            bestPrio = prio;
                            bestNode = cand;
                            improved = true;
                        }
                    }
                }

                if (!improved)
                    break;
                node = bestNode;
            }

            return node;
        }

        /// <summary>
        /// One steepest 2-swap move: replaces the best-improving PAIR of chosen modules
        /// with a pair of unused candidates. Returns false at a 2-swap local optimum.
        /// </summary>
        private bool TwoSwapStep(ref BeamNode node, Vector<byte>[] mods, int numModules)
        {
            long bestPrio = CreatePriority(node);
            bool improved = false;
            BeamNode bestNode = default;

            for (int p1 = 0; p1 < numModules - 1; p1++)
            {
                short o1;
                unsafe { o1 = node.CurrentSet.ModArr[p1]; }
                if (o1 < 0)
                    continue;

                for (int p2 = p1 + 1; p2 < numModules; p2++)
                {
                    short o2;
                    unsafe { o2 = node.CurrentSet.ModArr[p2]; }
                    if (o2 < 0)
                        continue;

                    var baseTotals = Vector.Subtract(Vector.Subtract(node.Totals, mods[o1]), mods[o2]);

                    for (int m1 = 0; m1 < mods.Length; m1++)
                    {
                        if (m1 != o1 && m1 != o2 && IsUsed(ref node, m1, numModules))
                            continue;

                        var partTotals = Vector.Add(baseTotals, mods[m1]);

                        for (int m2 = m1 + 1; m2 < mods.Length; m2++)
                        {
                            if (m2 != o1 && m2 != o2 && IsUsed(ref node, m2, numModules))
                                continue;
                            if ((m1 == o1 && m2 == o2) || (m1 == o2 && m2 == o1))
                                continue; // no-op

                            var cand = node;
                            unsafe
                            {
                                cand.CurrentSet.ModArr[p1] = (short)m1;
                                cand.CurrentSet.ModArr[p2] = (short)m2;
                            }
                            cand.Totals = Vector.Add(partTotals, mods[m2]);
                            ScoreBeamNode(ref cand);

                            long prio = CreatePriority(cand);
                            if (prio > bestPrio)
                            {
                                bestPrio = prio;
                                bestNode = cand;
                                improved = true;
                            }
                        }
                    }
                }
            }

            if (improved)
                node = bestNode;
            return improved;
        }

        /// <summary>True when module index <paramref name="m"/> is already in the node's set.</summary>
        private static bool IsUsed(ref BeamNode node, int m, int numModules)
        {
            unsafe
            {
                for (int q = 0; q < numModules; q++)
                {
                    if (node.CurrentSet.ModArr[q] == m)
                        return true;
                }
            }
            return false;
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

                        if (statPrio.HasCap)
                        {
                            // Upper-bound cap (negative ReqLevel; A/E ignored): score the stat
                            // normally up to the cap, but once the total exceeds it mark the
                            // requirement unmet and penalize so the beam steers toward capped
                            // combos. Approximate — the GPU backend enforces the cap exactly.
                            int cap = statPrio.GetCap();
                            if (x <= cap)
                            {
                                StatScoreLookup[idx] = ScoreStatValue(origStatId, x, prioPos);
                                RequirementMetLookup[idx] = 1;
                                StatProgressLookup[idx] = 100;
                            }
                            else
                            {
                                StatScoreLookup[idx] = CapExceededPenalty;
                                RequirementMetLookup[idx] = 0;
                                StatProgressLookup[idx] = 0;
                            }

                            continue;
                        }

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
                                // Guide the beam toward the exact target from BOTH sides:
                                // rising credit below it, decaying credit above it. The old
                                // x/req multiplier kept growing past the target (rewarding
                                // overshoot the gate then rejects) and left progress at 0
                                // until the exact value (no gradient), both causing misses.
                                var progress = reqLevel > 0
                                    ? (x < reqLevel ? x / (double)reqLevel : reqLevel / (double)x)
                                    : 0;
                                StatScoreLookup[idx] = (int)(score * progress);
                                // Strictly below the met lane's 100 so exact still wins.
                                StatProgressLookup[idx] = (ushort)(progress * 99);
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
                    else if (Config.ValueAllStats || Config.BruteForceAllModules)
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
            // (breakpoint level x link bonus) weighted by legendary multiplier and priority-order boost.
            var breakPointBonus = GetLinkLevelBoost(statValue);
            var orderBoost = statIdx >= 0 ? GetOrderBoost(Config.OrderBoostStrength, statIdx, numStats) : 1f;
            var bpLevel = SnapToBreakPointLevel(statValue);

            if (OriginalScoring)
            {
                // Upstream: raw points above the snapped breakpoint ("overcap") added unweighted.
                return (int)((bpLevel * breakPointBonus) * statMul * orderBoost + (statValue - bpLevel));
            }

            // Enhanced: points past the level-20 breakpoint have no in-game effect, so overcap
            // is not rewarded: this favors reaching 20 on more stats over piling into one
            // (e.g. 20/20/16 > 30/30).
            return (int)((bpLevel * breakPointBonus) * statMul * orderBoost);
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
            result.Score = beam.Score;
            return result;
        }

        /// <summary>
        /// Order-independent identity of the node's chosen module set. The expansion loop
        /// builds the same set in every order (up to Depth! permutations), so beam slots
        /// must be deduplicated on this or the effective width collapses.
        /// </summary>
        protected static ulong SetHash(in BeamNode node)
        {
            Span<short> ids = stackalloc short[ModuleSet.MaxModules];
            int count = Math.Min((int)node.Depth, ModuleSet.MaxModules);
            unsafe
            {
                for (int i = 0; i < count; i++)
                {
                    fixed (short* arr = node.CurrentSet.ModArr)
                    {
                        ids[i] = arr[i];
                    }
                }
            }

            // Insertion sort (tiny count) then FNV-1a.
            for (int i = 1; i < count; i++)
            {
                var v = ids[i];
                int j = i - 1;
                while (j >= 0 && ids[j] > v)
                {
                    ids[j + 1] = ids[j];
                    j--;
                }
                ids[j + 1] = v;
            }

            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < count; i++)
            {
                hash ^= (ushort)ids[i];
                hash *= 1099511628211UL;
            }

            return hash;
        }

        protected List<BeamNode> GetTopK(IEnumerable<BeamNode> candidates, int size)
        {
            // Min-heap on the canonical priority. Every enqueue MUST use CreatePriority:
            // mixing raw Score and the packed key made TryPeek return an arbitrary node
            // instead of the true worst, so good nodes were evicted (search misses).
            // Permutation duplicates of an already-held set are skipped (identical Totals,
            // identical priority) so every slot holds a DISTINCT set.
            var heap = new PriorityQueue<BeamNode, long>();
            var seen = new HashSet<ulong>(size);

            foreach (var candidate in candidates)
            {
                var prio = CreatePriority(candidate);
                if (heap.Count >= size)
                {
                    heap.TryPeek(out _, out var worstPrio);
                    if (prio <= worstPrio)
                        continue;
                }

                if (!seen.Add(SetHash(candidate)))
                    continue;

                if (heap.Count >= size)
                {
                    var evicted = heap.Dequeue();
                    seen.Remove(SetHash(evicted));
                }

                heap.Enqueue(candidate, prio);
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
            // Single canonical ordering (same key the heaps use) so ranking and eviction
            // can never disagree.
            return CreatePriority(a) > CreatePriority(b);
        }

        protected static long CreatePriority(BeamNode x)
        {
            // Canonical beam ranking: requirement progress (finer gradient) first, then
            // fully-met count, then score. The score is offset, not clamped at zero, so
            // negative scores (e.g. the cap-exceeded penalty) still order among themselves.
            // progress <= 12 prios * 100 fits 16 bits; met <= 12 fits 8 bits.
            var scoreKey = (uint)Math.Clamp((long)x.Score + 2_000_000L, 0L, uint.MaxValue);
            return ((long)x.RequirementProgress << 48) | ((long)x.RequirementsMet << 40) | scoreKey;
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
            private PriorityQueue<BeamNode, long> Heap = new();
            private HashSet<ulong> Seen = new();

            public TopK(int size)
            {
                Size = size;
            }

            public void Add(BeamNode candidate)
            {
                // Same canonical priority + set-dedup as GetTopK; see the comments there.
                var prio = CreatePriority(candidate);
                if (Heap.Count >= Size)
                {
                    Heap.TryPeek(out _, out var worstPrio);
                    if (prio <= worstPrio)
                        return;
                }

                if (!Seen.Add(SetHash(candidate)))
                    return;

                if (Heap.Count >= Size)
                {
                    var evicted = Heap.Dequeue();
                    Seen.Remove(SetHash(evicted));
                }

                Heap.Enqueue(candidate, prio);
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
