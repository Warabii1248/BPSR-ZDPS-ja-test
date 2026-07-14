using BPSR_ZDPS.DataTypes;
using BPSR_ZDPS.DataTypes.Modules;
using BPSR_ZDPS.Managers.Gpu;
using Serilog;
using System.Text;

namespace BPSR_ZDPS.Managers
{
    // Brute-force candidate cache + exact dominance pre-filter for the module solver.
    //
    // Cache design: a full GPU solve additionally collects every combination whose
    // (ungated) score is at least "XX% below the displayed 10th-best" into a pool.
    // While the module inventory / candidate set / set size stay unchanged, later
    // solves re-score that pool on the CPU instead of re-enumerating C(N,K) combos:
    //   * requirement-level (gate) only changes  -> provably exact when the pool still
    //     yields 10 distinct results (every combo above the threshold is in the pool);
    //   * scoring-weight changes (priorities order, score mode, boosts) -> approximate
    //     (flagged in the UI); anything structural (inventory, K, candidate set) misses
    //     the cache and triggers a full solve, which rebuilds the pool.
    public partial class ModuleOptimizer
    {
        // Pool entries are 12 bytes; 2M ≈ 24 MB GPU + managed copy.
        private const int PoolCapacity = 2_000_000;
        private const int PoolCollectRetries = 2;

        private static BruteForcePoolCache? _poolCache;
        private static FullSolveMemo? _lastFullSolve;
        private static readonly object _poolCacheLock = new();

        /// <summary>Entries / build time / effective cut percent of the current pool cache (null = none).</summary>
        public static (int Entries, DateTime BuiltAt, int EffectivePct)? PoolCacheStatus
        {
            get
            {
                var c = _poolCache;
                return c == null ? null : (c.Pool.Length, c.BuiltAt, c.EffectivePct);
            }
        }

        public static void ClearPoolCache()
        {
            lock (_poolCacheLock)
            {
                _poolCache = null;
                _lastFullSolve = null;
            }
        }

        private sealed class BruteForcePoolCache
        {
            public required long InventoryHash;
            public required long[] FilteredIds;
            public required int K;
            public required int NumStats;           // real S (normalized stat count)
            public required byte[] StatMatrix;      // [n * S] per-module normalized stat levels
            public required uint[] Binom;
            public required GpuCandidate[] Pool;
            public required int ThresholdScore;
            public required int EffectivePct;
            public required ScoringSnapshot Scoring;
            public DateTime BuiltAt = DateTime.Now;
        }

        private sealed class FullSolveMemo
        {
            public required long InventoryHash;
            public required string ConfigHash;
            public required int K;
            public required SolverResult Result;
        }

        /// <summary>
        /// The parts of a config that change raw (ungated) combo scores. Requirement
        /// levels / stat modes are gates, not weights, so they are deliberately absent.
        /// </summary>
        private sealed class ScoringSnapshot
        {
            public required ScoreMode ScoreMode;
            public required ScoringModel ScoringModel;
            public required float OrderBoostStrength;
            public required float LegendaryStatMultiplier;
            public required byte[] LinkLevelBonus;
            public required bool ValueAllStats;
            public required bool BruteForceAllModules;
            public required int[] PriorityIds;      // order matters (order boost)

            public static ScoringSnapshot From(SolverConfig c) => new()
            {
                ScoreMode = c.ScoreMode,
                ScoringModel = c.ScoringModel,
                OrderBoostStrength = c.OrderBoostStrength,
                LegendaryStatMultiplier = c.LegendaryStatMultiplier,
                LinkLevelBonus = c.LinkLevelBonus.ToArray(),
                ValueAllStats = c.ValueAllStats,
                BruteForceAllModules = c.BruteForceAllModules,
                PriorityIds = c.StatPriorities.Select(p => p.Id).ToArray()
            };

            public bool ScoringEquals(SolverConfig c) =>
                ScoreMode == c.ScoreMode
                && ScoringModel == c.ScoringModel
                && OrderBoostStrength == c.OrderBoostStrength
                && LegendaryStatMultiplier == c.LegendaryStatMultiplier
                && ValueAllStats == c.ValueAllStats
                && BruteForceAllModules == c.BruteForceAllModules
                && LinkLevelBonus.SequenceEqual(c.LinkLevelBonus)
                && PriorityIds.SequenceEqual(c.StatPriorities.Select(p => p.Id));
        }

        // ------------------------------------------------------------------
        // Candidate preparation (exact search-space reduction)
        // ------------------------------------------------------------------

        /// <summary>
        /// Shrinks the candidate list without changing results: caps interchangeable
        /// duplicates at K per type, then removes modules dominated (per-stat &lt;=) by at
        /// least K others. Dominance is provably lossless because every score mode is
        /// monotone in each stat total (verified at runtime) and Atleast gates only ever
        /// benefit from higher totals; it is skipped for Exactly gates or non-monotone
        /// custom link bonuses.
        /// </summary>
        private static List<long> PrepareCandidates(SolverConfig config, PlayerModDataSave playerMods, List<long> filtered, bool forPoolCache = false)
        {
            int k = Math.Clamp(config.NumModules, 1, ModuleSet.MaxModules);
            var limited = Modules.ModuleOptimizerBase.LimitDuplicates(playerMods, filtered, k);
            var reduced = FilterDominated(config, playerMods, limited, k, forPoolCache);
            Log.Information("Candidate prep: {Raw} raw -> {Limited} deduped -> {Final} after dominance filter",
                filtered.Count, limited.Count, reduced.Count);
            return reduced;
        }

        private static List<long> FilterDominated(SolverConfig config, PlayerModDataSave playerMods, List<long> filtered, int k, bool forPoolCache)
        {
            if (filtered.Count <= k)
            {
                return filtered;
            }

            // With the pool cache active the candidate list must not depend on the gate
            // config: dominance runs for plain configs but is skipped for cap/Exactly ones,
            // so the lists would differ and every gate tweak would miss on FilteredIds.
            // Skipping it up front keeps one stable list (and a pool that also covers the
            // dominated modules a later cap/Exactly query may need) at the cost of a
            // slightly larger one-time full solve.
            if (forPoolCache)
            {
                return filtered;
            }

            // "Exactly this link value" and upper-bound cap gates break monotonicity (more
            // can invalidate), so dominance (which assumes higher stats are never worse) is
            // unsafe: a capped combo may need the lower-stat module the filter would remove.
            if (config.StatPriorities.Any(p => p.HasCap || p.StatMode == StatMode.Exactly))
            {
                return filtered;
            }

            var possibleStats = NormalizeStatsLookup(playerMods, filtered);
            int s = possibleStats.Count;

            if (!ScoreIsMonotone(config, possibleStats, k))
            {
                return filtered;
            }

            int n = filtered.Count;
            var matrix = BuildStatMatrix(playerMods, filtered, possibleStats, s);
            var removed = new bool[n];

            Parallel.For(0, n, x =>
            {
                int dominators = 0;
                for (int y = 0; y < n && dominators < k; y++)
                {
                    if (y == x)
                    {
                        continue;
                    }

                    bool geAll = true;
                    bool strict = false;
                    for (int st = 0; st < s; st++)
                    {
                        byte vy = matrix[y * s + st];
                        byte vx = matrix[x * s + st];
                        if (vy < vx)
                        {
                            geAll = false;
                            break;
                        }
                        if (vy > vx)
                        {
                            strict = true;
                        }
                    }

                    // Equal-stat modules tie-break by list position so dominance stays a
                    // strict order (no two modules ever remove each other).
                    if (geAll && (strict || y < x))
                    {
                        dominators++;
                    }
                }

                // With >= K dominators, any combo using this module can swap it for a
                // dominator not already in the combo (a combo has only K-1 other slots),
                // never lowering any stat total => never lowering the score or a gate.
                removed[x] = dominators >= k;
            });

            var result = new List<long>(n);
            for (int idx = 0; idx < n; idx++)
            {
                if (!removed[idx])
                {
                    result.Add(filtered[idx]);
                }
            }

            return result;
        }

        /// <summary>
        /// Verifies every active score mode is non-decreasing in each stat total, which the
        /// dominance filter's exactness proof requires. Custom link bonuses (ZScore) or
        /// unusual data tables (CombatPower) can break this; then the filter is skipped.
        /// </summary>
        private static bool ScoreIsMonotone(SolverConfig config, Dictionary<int, int> possibleStats, int k)
        {
            if (config.ScoreMode == ScoreMode.CombatPower)
            {
                foreach (var origId in possibleStats.Keys)
                {
                    int prev = 0;
                    foreach (var tier in GpuEnhanceTiers)
                    {
                        int v = ModuleSolver.StatCombatScores.TryGetValue($"{origId}_{tier}", out var tmp) ? tmp : 0;
                        if (v < prev || v < 0)
                        {
                            return false;
                        }
                        prev = v;
                    }
                }

                var linkTotal = BuildLinkTotalFight(k * 20);
                for (int t = 1; t < linkTotal.Length; t++)
                {
                    if (linkTotal[t] < linkTotal[t - 1])
                    {
                        return false;
                    }
                }

                return true;
            }

            // ZScore: per-stat score is alpha * g(v) + h(v) where g(v) = SnapBp(v) * bonus,
            // h(v) = overcap (Original only) and alpha is that stat's positive weight.
            // Monotone for every weight in [alphaMin, inf) iff dg >= 0 and
            // alphaMin * dg + dh >= 0 at every step.
            var linkBonus = BuildLinkBonus(config);
            bool original = config.ScoringModel == ScoringModel.Original;

            float alphaMin = float.MaxValue;
            if (config.ValueAllStats || config.BruteForceAllModules)
            {
                alphaMin = 0.95f;
            }
            int prioCount = config.StatPriorities.Count;
            for (int pos = 0; pos < prioCount; pos++)
            {
                var legendary = ModuleSolver.LegendaryStats.Contains(config.StatPriorities[pos].Id)
                    ? config.LegendaryStatMultiplier : 1f;
                var w = legendary * ModuleOptimizerBeam.GetOrderBoost(config.OrderBoostStrength, pos, prioCount);
                alphaMin = Math.Min(alphaMin, w);
            }

            if (alphaMin == float.MaxValue)
            {
                // No scored stats at all: every combo scores 0, trivially monotone.
                return true;
            }
            if (alphaMin <= 0f)
            {
                return false;
            }

            int GVal(int v) => CpuSnapBp(v) * linkBonus[CpuLinkTier(v)];
            int HVal(int v) => original ? v - CpuSnapBp(v) : 0;

            for (int v = 1; v <= 50; v++)
            {
                int dg = GVal(v) - GVal(v - 1);
                int dh = HVal(v) - HVal(v - 1);
                if (dg < 0 || alphaMin * dg + dh < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] BuildStatMatrix(PlayerModDataSave playerMods, List<long> filtered, Dictionary<int, int> possibleStats, int s)
        {
            var matrix = new byte[filtered.Count * s];
            for (int i = 0; i < filtered.Count; i++)
            {
                long modId = filtered[i];
                var modParts = playerMods.ModulesPackage.Items[modId].ModNewAttr.ModParts;
                var linkNums = playerMods.Mod.ModInfos[modId].InitLinkNums;
                for (int p = 0; p < modParts.Count; p++)
                {
                    matrix[i * s + possibleStats[modParts[p]]] = (byte)Math.Clamp(linkNums[p], 0, 255);
                }
            }
            return matrix;
        }

        // ------------------------------------------------------------------
        // Cache keys
        // ------------------------------------------------------------------

        private static long ComputeInventoryHash(PlayerModDataSave playerMods, List<long> filtered)
        {
            ulong hash = 14695981039346656037UL;
            void Mix(ulong v) { hash ^= v; hash *= 1099511628211UL; }

            foreach (var id in filtered)
            {
                Mix((ulong)id);
                var modParts = playerMods.ModulesPackage.Items[id].ModNewAttr.ModParts;
                var linkNums = playerMods.Mod.ModInfos[id].InitLinkNums;
                for (int p = 0; p < modParts.Count; p++)
                {
                    Mix((ulong)modParts[p]);
                    Mix((ulong)linkNums[p]);
                }
            }

            return (long)hash;
        }

        private static string ComputeConfigHash(SolverConfig config, int k)
        {
            var sb = new StringBuilder();
            sb.Append(k).Append('|')
              .Append((int)config.ScoreMode).Append('|')
              .Append((int)config.ScoringModel).Append('|')
              .Append(config.OrderBoostStrength.ToString("R")).Append('|')
              .Append(config.LegendaryStatMultiplier.ToString("R")).Append('|')
              .Append(config.ValueAllStats).Append('|')
              .Append(config.BruteForceAllModules).Append('|')
              .Append(string.Join(',', config.LinkLevelBonus)).Append('|');
            foreach (var p in config.StatPriorities)
            {
                sb.Append(p.Id).Append(':').Append(p.ReqLevel).Append(':').Append((int)p.StatMode).Append(':').Append(p.MinLevel).Append(';');
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Cache query (CPU re-scoring of the pooled combos)
        // ------------------------------------------------------------------

        private SolverResult? TryQueryPoolCache(SolverConfig config, PlayerModDataSave playerMods, List<long> filtered, int k, long invHash)
        {
            BruteForcePoolCache? cache;
            FullSolveMemo? memo;
            lock (_poolCacheLock)
            {
                cache = _poolCache;
                memo = _lastFullSolve;
            }

            var cfgHash = ComputeConfigHash(config, k);
            if (memo != null && memo.InventoryHash == invHash && memo.K == k && memo.ConfigHash == cfgHash)
            {
                // Identical full solve already ran: return its (exact) results directly.
                return CloneResult(memo.Result, exact: true);
            }

            // Upper-bound caps (negative ReqLevel) ride the same guarantees as lower-bound
            // gate changes: they only FILTER (compliant combos score identically), GatesPass
            // re-applies them during extraction, and the <10-results fallback below catches
            // pools whose threshold cut away the capped optimum. A -6 exclusion shrinks the
            // candidate set, so it naturally misses on FilteredIds and full-solves once per
            // exclusion combination (like any candidate-set change).
            if (cache == null || cache.K != k || cache.InventoryHash != invHash
                || cache.FilteredIds.Length != filtered.Count)
            {
                return null;
            }
            for (int i = 0; i < filtered.Count; i++)
            {
                if (cache.FilteredIds[i] != filtered[i])
                {
                    return null;
                }
            }

            int n = filtered.Count;
            int s = cache.NumStats;

            var possibleStats = NormalizeStatsLookup(playerMods, filtered);
            if (possibleStats.Count != s)
            {
                return null;
            }

            BuildStatConfig(config, possibleStats, s, out var mul, out var req, out var exact, out var cap);
            var linkBonus = BuildLinkBonus(config);
            var statCombat = BuildStatCombat(possibleStats, s);
            int maxTotal = k * 20;
            var linkTotalFight = BuildLinkTotalFight(maxTotal);
            bool combat = config.ScoreMode == ScoreMode.CombatPower;
            bool original = config.ScoringModel == ScoringModel.Original;

            var pool = cache.Pool;
            var scores = new int[pool.Length];
            var order = new int[pool.Length];

            Parallel.For(0, pool.Length, p =>
            {
                order[p] = p;
                var indices = DecodeCombo(pool[p], k, n, cache.Binom);
                if (indices == null)
                {
                    scores[p] = int.MinValue;
                    return;
                }

                Span<int> totals = stackalloc int[MaxCacheStats];
                SumTotals(cache.StatMatrix, s, indices, totals);

                if (!GatesPass(totals, s, req, exact, cap))
                {
                    scores[p] = int.MinValue;
                    return;
                }

                scores[p] = CpuScoreCombo(totals, s, mul, req, linkBonus, statCombat, linkTotalFight, maxTotal, combat, original);
            });

            // Descending by re-scored value (invalid entries sink to the end).
            var keys = new int[pool.Length];
            for (int p = 0; p < pool.Length; p++)
            {
                keys[p] = scores[p] == int.MinValue ? int.MaxValue : -scores[p];
            }
            Array.Sort(keys, order);

            var seenProfiles = new HashSet<ulong>();
            var top = new List<ModComboResult>(10);
            Span<int> walkTotals = stackalloc int[MaxCacheStats];
            foreach (var p in order)
            {
                if (scores[p] == int.MinValue)
                {
                    break;
                }

                var indices = DecodeCombo(pool[p], k, n, cache.Binom)!;
                SumTotals(cache.StatMatrix, s, indices, walkTotals);

                if (!seenProfiles.Add(TotalsProfileHash(walkTotals, s)))
                {
                    continue;
                }

                top.Add(BuildResult(playerMods, config, filtered, indices, scores[p], k));
                if (top.Count >= 10)
                {
                    break;
                }
            }

            // Fewer than 10 distinct results can hide out-of-pool combos (they were cut by
            // the threshold): run a full solve instead. Its memo then serves repeats.
            if (top.Count < 10)
            {
                return null;
            }

            bool scoringEqual = cache.Scoring.ScoringEquals(config);
            return new SolverResult
            {
                BestModResults = top,
                FilteredModules = new List<long>(filtered),
                FromCache = true,
                CacheExact = scoringEqual
            };
        }

        // ------------------------------------------------------------------
        // Cache build (pool collection after a full solve)
        // ------------------------------------------------------------------

        private void StorePoolCache(GpuComputeContext gpu, GpuSolveInput input, SolverConfig config,
            PlayerModDataSave playerMods, List<long> filtered, int k, long invHash, int score10,
            CancellationToken cancelToken, Action<float>? progress)
        {
            int pct = Math.Clamp(Settings.Instance.WindowSettings.ModuleWindow.CacheThresholdPct, 1, 90);

            int maxAttempts = PoolCollectRetries + 1;
            for (int attempt = 0; attempt <= PoolCollectRetries; attempt++)
            {
                // Keep combos scoring at least (100 - pct)% of the displayed 10th-best.
                int threshold = (int)Math.Ceiling(score10 * (1.0 - pct / 100.0));

                // Each attempt re-enumerates the whole space; map its 0..1 progress into this
                // attempt's slice of the phase so a retry keeps the bar moving forward instead
                // of snapping back to the phase start (was seen as 100% -> 50%).
                int attemptIdx = attempt;
                Action<float>? attemptProgress = progress == null
                    ? null
                    : p => progress((attemptIdx + Math.Clamp(p, 0f, 1f)) / maxAttempts);

                var (entries, total) = gpu.DispatchCollect(input, threshold, PoolCapacity, cancelToken, attemptProgress);
                if (cancelToken.IsCancellationRequested)
                {
                    return; // partial pool would silently miss combos; don't cache it
                }

                if (total <= (uint)entries.Length)
                {
                    var possibleStats = NormalizeStatsLookup(playerMods, filtered);
                    var cache = new BruteForcePoolCache
                    {
                        InventoryHash = invHash,
                        FilteredIds = filtered.ToArray(),
                        K = k,
                        NumStats = possibleStats.Count,
                        StatMatrix = BuildStatMatrix(playerMods, filtered, possibleStats, possibleStats.Count),
                        Binom = input.Binom,
                        Pool = entries,
                        ThresholdScore = threshold,
                        EffectivePct = pct,
                        Scoring = ScoringSnapshot.From(config)
                    };

                    lock (_poolCacheLock)
                    {
                        _poolCache = cache;
                    }

                    Log.Information("Brute-force pool cached: {Count:N0} combos within {Pct}% of the 10th-best score {Score}.",
                        entries.Length, pct, score10);
                    progress?.Invoke(1f); // finish the phase (may snap forward if it succeeded early)
                    return;
                }

                Log.Information("Pool overflow ({Total:N0} > {Cap:N0}) at {Pct}%; tightening threshold.", total, PoolCapacity, pct);
                pct = Math.Max(1, pct / 2);
            }

            progress?.Invoke(1f); // all attempts exhausted; still complete the bar
            Log.Information("Pool cache skipped: too many candidate combos near the top score.");
        }

        private void MemoizeFullSolve(SolverConfig config, int k, long invHash, SolverResult result)
        {
            var memo = new FullSolveMemo
            {
                InventoryHash = invHash,
                ConfigHash = ComputeConfigHash(config, k),
                K = k,
                Result = CloneResult(result, exact: true)
            };

            lock (_poolCacheLock)
            {
                _lastFullSolve = memo;
            }
        }

        private static SolverResult CloneResult(SolverResult src, bool exact)
        {
            // Defensive copies: the UI clears/mutates the returned lists on window close.
            return new SolverResult
            {
                BestModResults = new List<ModComboResult>(src.BestModResults),
                FilteredModules = new List<long>(src.FilteredModules),
                FromCache = true,
                CacheExact = exact
            };
        }

        // ------------------------------------------------------------------
        // CPU scoring (mirrors ModuleSolver.hlsl ScoreCombo exactly)
        // ------------------------------------------------------------------

        private const int MaxCacheStats = 32; // matches MAX_STATS in ModuleSolver.hlsl

        private static void SumTotals(byte[] statMatrix, int s, int[] indices, Span<int> totals)
        {
            totals.Clear();
            foreach (var idx in indices)
            {
                int row = idx * s;
                for (int st = 0; st < s; st++)
                {
                    totals[st] += statMatrix[row + st];
                }
            }
        }

        private static bool GatesPass(Span<int> totals, int s, int[] req, int[] exact, int[] cap)
        {
            for (int st = 0; st < s; st++)
            {
                int tv = totals[st];
                // Exactly gates apply per stat: EVERY exact stat must hit its target.
                // (A former any-one-matches check let a combo violate a second Exactly
                // stat.) Raw equality on the total, same as the shader and the beam.
                if (exact[st] != 0)
                {
                    if (tv != req[st])
                    {
                        return false;
                    }
                }
                else if (Math.Min(tv, 20) < req[st])
                {
                    return false;
                }
                // Upper-bound cap (StatPrio.NoCap = none); cap 0 excludes the stat entirely.
                if (tv > cap[st])
                {
                    return false;
                }
            }

            return true;
        }

        private static int CpuScoreCombo(Span<int> totals, int s, float[] mul, int[] req,
            int[] linkBonus, int[] statCombat, int[] linkTotalFight, int maxTotal, bool combat, bool original)
        {
            int score = 0;
            int totalSum = 0;

            for (int st = 0; st < s; st++)
            {
                int tv = totals[st];
                if (!combat)
                {
                    float w = mul[st];
                    if (w > 0)
                    {
                        int tvc = Math.Min(tv, 50);
                        int bp = CpuSnapBp(tvc);
                        int statScore = (int)((bp * linkBonus[CpuLinkTier(tvc)]) * w);
                        if (original)
                        {
                            statScore += tvc - bp;
                        }
                        score += statScore;
                    }
                }
                else
                {
                    totalSum += tv;
                    if (tv >= 1)
                    {
                        score += statCombat[st * 6 + CpuLinkTier(tv)];
                    }
                }
            }

            if (combat)
            {
                totalSum = Math.Clamp(totalSum, 0, maxTotal);
                score += linkTotalFight[totalSum];
            }

            return score;
        }

        private static ulong TotalsProfileHash(Span<int> totals, int s)
        {
            ulong hash = 14695981039346656037UL;
            for (int st = 0; st < s; st++)
            {
                hash ^= (ulong)Math.Min(totals[st], 20);
                hash *= 1099511628211UL;
            }
            return hash;
        }

        private static int CpuLinkTier(int v)
        {
            if (v >= 20) return 5;
            if (v >= 16) return 4;
            if (v >= 12) return 3;
            if (v >= 8) return 2;
            if (v >= 4) return 1;
            return 0;
        }

        private static int CpuSnapBp(int v)
        {
            if (v >= 20) return 20;
            if (v >= 16) return 16;
            if (v >= 12) return 12;
            if (v >= 8) return 8;
            if (v >= 4) return 4;
            if (v >= 1) return 1;
            return 0;
        }
    }
}
