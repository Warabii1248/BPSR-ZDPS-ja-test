using BPSR_ZDPS.DataTypes;
using BPSR_ZDPS.DataTypes.Modules;
using BPSR_ZDPS.Managers.Gpu;
using Serilog;
using System.Diagnostics;
using System.Reflection;

namespace BPSR_ZDPS.Managers
{
    public partial class ModuleOptimizer
    {
        private const int GpuScoreInvalid = -2147483647; // matches SCORE_INVALID in ModuleSolver.hlsl
        private const int GpuMaxStats = 32;              // matches MAX_STATS in ModuleSolver.hlsl
        private static readonly int[] GpuEnhanceTiers = [1, 4, 8, 12, 16, 20];

        // Debug-only knobs (surfaced in the ModuleSolver window's Debug tab, DEBUG builds only).
        // They restore the legacy automatic-fallback conditions; in normal use a GPU failure
        // is reported to the user and the solve stops instead of silently degrading.
        /// <summary>Debug: when &gt; 0, a GPU solve whose C(N,K) exceeds this throws (legacy combo budget).</summary>
        public static long DebugMaxGpuCombos;
        /// <summary>Debug: when true, a failed GPU solve falls back to the CPU beam search (legacy).</summary>
        public static bool DebugAllowCpuFallback;

        private static GpuComputeContext? _gpuContext;
        private static bool _gpuInitFailed;
        private static readonly object _gpuLock = new();

        /// <summary>Name of the GPU bound for compute, or null until the first GPU solve runs.</summary>
        public static string? GpuAdapterName => _gpuContext?.AdapterName;

        /// <summary>True if GPU compute init failed (resets on app restart).</summary>
        public static bool GpuUnavailable => _gpuInitFailed;

        /// <summary>
        /// Lazily creates the shared compute context. Throws when the GPU/DirectCompute is
        /// unavailable; the caller surfaces the error to the user (no automatic fallback).
        /// </summary>
        private static GpuComputeContext EnsureGpuContext()
        {
            lock (_gpuLock)
            {
                if (_gpuContext != null)
                {
                    return _gpuContext;
                }

                if (_gpuInitFailed)
                {
                    throw new InvalidOperationException("GPU compute context previously failed to initialize.");
                }

                try
                {
                    var shaderBytes = LoadShaderSource();
                    _gpuContext = new GpuComputeContext(shaderBytes);
                    Log.Information("GPU module solver initialized on adapter: {Adapter}", _gpuContext.AdapterName);
                    return _gpuContext;
                }
                catch (Exception ex)
                {
                    _gpuInitFailed = true;
                    Log.Warning(ex, "GPU module solver unavailable.");
                    throw;
                }
            }
        }

        private static byte[] LoadShaderSource()
        {
            var asm = typeof(ModuleOptimizer).Assembly;
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("ModuleSolver.hlsl", StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("Embedded resource ModuleSolver.hlsl not found.");

            using var stream = asm.GetManifestResourceStream(resName)
                ?? throw new FileNotFoundException($"Unable to open embedded resource {resName}.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private SolverResult GpuSolve(SolverConfig config, PlayerModDataSave playerMods, Stopwatch sw, List<long> filtered, CancellationToken cancelToken, Action<float>? progress = null)
        {
            int k = Math.Clamp(config.NumModules, 1, ModuleSet.MaxModules);
            int n = filtered.Count;

            if (n < k)
            {
                sw.Stop();
                return new SolverResult { FilteredModules = filtered };
            }

            // Debug-only legacy budget: refuse solves above the configured combination count.
            if (DebugMaxGpuCombos > 0 && EstimateCombinations(n, k) > DebugMaxGpuCombos)
            {
                throw new NotSupportedException(
                    $"Combination count for {n} modules choose {k} exceeds the debug GPU budget ({DebugMaxGpuCombos:N0}).");
            }

            bool cacheEnabled = Settings.Instance.WindowSettings.ModuleWindow.UseBruteForceCache;
            long invHash = ComputeInventoryHash(playerMods, filtered);

            if (cacheEnabled)
            {
                var cached = TryQueryPoolCache(config, playerMods, filtered, k, invHash);
                if (cached != null)
                {
                    sw.Stop();
                    progress?.Invoke(1f);
                    return cached;
                }
            }

            var gpu = EnsureGpuContext();

            // ---- normalized stat indexing (shared with the CPU vector path) ----
            var possibleStats = NormalizeStatsLookup(playerMods, filtered); // origStatId -> normIndex
            int s = possibleStats.Count;
            if (s > GpuMaxStats)
            {
                throw new NotSupportedException(
                    $"Too many distinct stats ({s} > {GpuMaxStats}) for the GPU solver; reduce the candidate modules or switch to the CPU backend.");
            }

            var input = new GpuSolveInput
            {
                NumModules = n,
                NumModulesInSet = k,
                NumStats = s,
                ScoreMode = config.ScoreMode == ScoreMode.CombatPower ? 1 : 0,
                // Cap-mode priorities (negative ReqLevel) ignore A/E, so they never count as Exactly.
                HasExact = config.StatPriorities.Any(p => !p.HasCap && p.StatMode == StatMode.Exactly),
                MaxTotal = k * 20,
                OriginalScoring = config.ScoringModel == ScoringModel.Original ? 1 : 0,
            };

            input.ModuleStats = BuildModuleStats(playerMods, filtered, possibleStats, s);
            BuildStatConfig(config, possibleStats, s, out input.StatMul, out input.StatReq, out input.StatExact, out input.StatCap);
            input.Binom = BuildBinomial(n, k);
            input.LinkBonus = BuildLinkBonus(config);
            input.StatCombat = BuildStatCombat(possibleStats, s);
            input.LinkTotalFight = BuildLinkTotalFight(input.MaxTotal);

            // Sliced, TDR-safe dispatch; cancelling returns the candidates found so far.
            // Building the cache pool costs a second enumeration, so the solve pass maps
            // to 0-50% of the progress bar and the collect pass to 50-100%.
            Action<float>? solveProgress = cacheEnabled ? p => progress?.Invoke(p * 0.5f) : progress;
            var candidates = gpu.Dispatch(input, cancelToken, solveProgress);

            var top10 = MergeTop10(candidates, k, n, input);

            var results = new List<ModComboResult>(top10.Count);
            foreach (var (score, localIndices) in top10)
            {
                if (cancelToken.IsCancellationRequested)
                {
                    break;
                }

                results.Add(BuildResult(playerMods, config, filtered, localIndices, score, k));
            }

            var result = new SolverResult
            {
                BestModResults = results,
                FilteredModules = filtered
            };

            if (!cancelToken.IsCancellationRequested && results.Count > 0)
            {
                if (cacheEnabled)
                {
                    StorePoolCache(gpu, input, config, playerMods, filtered, k, invHash,
                        top10[^1].Score, cancelToken, p => progress?.Invoke(0.5f + p * 0.5f));
                }

                // Identical (inventory, config) repeats answer straight from this memo.
                MemoizeFullSolve(config, k, invHash, result);
            }

            sw.Stop();
            return result;
        }

        /// <summary>Number of padded stat lanes: kernel loops run word-wise over ceil4(S) lanes.</summary>
        private static int PaddedStats(int s) => (s + 3) / 4 * 4;

        private static uint[] BuildModuleStats(PlayerModDataSave playerMods, List<long> filtered, Dictionary<int, int> possibleStats, int s)
        {
            // Four 8-bit stat values per uint (link levels are far below 255), so the
            // kernel reads a quarter of the words per module.
            int sPacked = (s + 3) / 4;
            var arr = new uint[filtered.Count * sPacked];
            for (int i = 0; i < filtered.Count; i++)
            {
                long modId = filtered[i];
                var modParts = playerMods.ModulesPackage.Items[modId].ModNewAttr.ModParts;
                var linkNums = playerMods.Mod.ModInfos[modId].InitLinkNums;
                for (int p = 0; p < modParts.Count; p++)
                {
                    int normIdx = possibleStats[modParts[p]];
                    uint value = (uint)Math.Clamp(linkNums[p], 0, 255);
                    arr[i * sPacked + normIdx / 4] |= value << ((normIdx % 4) * 8);
                }
            }
            return arr;
        }

        private void BuildStatConfig(SolverConfig config, Dictionary<int, int> possibleStats, int s,
            out float[] mul, out int[] req, out int[] exact, out int[] cap)
        {
            // Zero-padded to a 4-multiple: padded lanes have weight 0 / req 0, so the
            // kernel's word-wise loops need no per-lane bounds checks.
            int padded = PaddedStats(s);
            mul = new float[padded];
            req = new int[padded];
            exact = new int[padded];
            cap = new int[padded];
            // Default every lane to "no cap"; only capped priorities lower it below.
            Array.Fill(cap, StatPrio.NoCap);

            int prioCount = config.StatPriorities.Count;
            foreach (var (origId, normIdx) in possibleStats)
            {
                var prioPos = config.StatPriorities.FindIndex(p => p.Id == origId);
                if (prioPos >= 0)
                {
                    var prio = config.StatPriorities[prioPos];

                    // Combined ZScore weight: legendary multiplier x non-linear priority-order boost
                    // (same factors ModuleOptimizerBeam.CalcScore applies on the CPU path).
                    // A ReqLevel of 0 just means "no requirement gate"; the stat is still a
                    // scored priority, so sets that carry it rank above sets that don't.
                    var legendaryMul = ModuleSolver.LegendaryStats.Contains(origId) ? config.LegendaryStatMultiplier : 1f;
                    mul[normIdx] = legendaryMul * ModuleOptimizerBeam.GetOrderBoost(config.OrderBoostStrength, prioPos, prioCount);

                    // Negative ReqLevel is an upper-bound cap (GetLowerReq -> 0, A/E ignored),
                    // otherwise a lower-bound requirement. The stat is still scored up to the cap.
                    req[normIdx] = prio.GetLowerReq();
                    exact[normIdx] = (!prio.HasCap && prio.StatMode == StatMode.Exactly) ? 1 : 0;
                    cap[normIdx] = prio.GetCap();
                }
                else
                {
                    // Brute force ranks by the whole build, so value every stat even if
                    // "include all stats" is off; otherwise honor that toggle.
                    mul[normIdx] = (config.ValueAllStats || config.BruteForceAllModules) ? 0.95f : 0f;
                }
            }
        }

        /// <summary>Approximate C(n, k) as a double (saturates to +Inf) for workload budgeting.</summary>
        private static double EstimateCombinations(int n, int k)
        {
            if (k < 0 || k > n) return 0d;
            if (k > n - k) k = n - k;
            double result = 1d;
            for (int i = 1; i <= k; i++)
            {
                result = result * (n - i + 1) / i;
                if (double.IsInfinity(result)) break;
            }
            return result;
        }

        private static uint[] BuildBinomial(int n, int k)
        {
            int rows = n + 1;
            int cols = k + 1;
            // Only columns 0..k-1 are ever read (sub-count uses C(m, k-1); unranking uses
            // C(., t-j-1) with t-j-1 <= k-2). Leaving column k unbuilt avoids spurious
            // uint32 overflow for large n at high k.
            int maxCol = Math.Max(0, k - 1);
            var binom = new uint[rows * cols];
            for (int a = 0; a < rows; a++)
            {
                binom[a * cols + 0] = 1;
                int maxB = Math.Min(a, maxCol);
                for (int b = 1; b <= maxB; b++)
                {
                    long value = (long)binom[(a - 1) * cols + (b - 1)] + binom[(a - 1) * cols + b];
                    if (value > uint.MaxValue)
                    {
                        throw new NotSupportedException(
                            "Combination count exceeds the GPU solver's uint32 range; reduce the candidate modules (total-link cutoff / qualities) or switch to the CPU backend.");
                    }
                    binom[a * cols + b] = (uint)value;
                }
            }
            return binom;
        }

        private static int[] BuildLinkBonus(SolverConfig config)
        {
            var bonus = new int[6];
            for (int i = 0; i < 6; i++)
            {
                bonus[i] = i < config.LinkLevelBonus.Length ? config.LinkLevelBonus[i] : 0;
            }
            return bonus;
        }

        private static int[] BuildStatCombat(Dictionary<int, int> possibleStats, int s)
        {
            var arr = new int[PaddedStats(s) * 6];
            foreach (var (origId, normIdx) in possibleStats)
            {
                for (int tier = 0; tier < 6; tier++)
                {
                    int level = GpuEnhanceTiers[tier];
                    arr[normIdx * 6 + tier] = ModuleSolver.StatCombatScores.TryGetValue($"{origId}_{level}", out var v) ? v : 0;
                }
            }
            return arr;
        }

        private static int[] BuildLinkTotalFight(int maxTotal)
        {
            var arr = new int[maxTotal + 1];
            for (int t = 0; t <= maxTotal; t++)
            {
                arr[t] = HelperMethods.DataTables.ModLinkEffects.Data.TryGetValue(t + 1, out var entry)
                    ? entry?.FightValue ?? 0
                    : 0;
            }
            return arr;
        }

        private static List<(int Score, int[] LocalIndices)> MergeTop10(GpuCandidate[] candidates, int k, int n, GpuSolveInput input)
        {
            var seenProfiles = new HashSet<ulong>();
            var merged = new List<(int Score, int[] LocalIndices)>();

            foreach (var ordered in candidates
                .Where(c => c.Score > GpuScoreInvalid)
                .OrderByDescending(c => c.Score))
            {
                var indices = DecodeCombo(ordered, k, n, input.Binom);
                if (indices == null)
                {
                    continue;
                }

                // Dedupe by the effective stat profile (capped at the in-game max of 20, like
                // the beam search's GetHash): swapping in an identical duplicate module must
                // not consume one of the 10 result slots.
                if (!seenProfiles.Add(StatProfileHash(indices, input)))
                {
                    continue;
                }

                merged.Add((ordered.Score, indices));
                if (merged.Count >= 10)
                {
                    break;
                }
            }

            return merged;
        }

        /// <summary>FNV-1a hash of a combo's per-stat totals, each capped at 20 (in-game max).</summary>
        private static ulong StatProfileHash(int[] localIndices, GpuSolveInput input)
        {
            int s = input.NumStats;
            int sPacked = (s + 3) / 4;
            ulong hash = 14695981039346656037UL;
            for (int st = 0; st < s; st++)
            {
                uint total = 0;
                foreach (var idx in localIndices)
                {
                    total += (input.ModuleStats[idx * sPacked + st / 4] >> ((st % 4) * 8)) & 0xFF;
                }

                hash ^= Math.Min(total, 20u);
                hash *= 1099511628211UL;
            }

            return hash;
        }

        /// <summary>
        /// Re-expands a (i, rank) candidate into sorted local module indices using the same
        /// lexicographic combinatorial unranking the shader uses to enumerate combos.
        /// </summary>
        private static int[]? DecodeCombo(GpuCandidate c, int k, int n, uint[] binom)
        {
            int cols = k + 1;
            int i = (int)c.I;
            int m = n - i - 1;   // modules after i
            int t = k - 1;       // offsets to choose

            if (i < 0 || i + k > n)
            {
                return null;
            }

            var indices = new int[k];
            indices[0] = i;

            long rank = c.Rank;
            int cur = 0;
            for (int j = 0; j < t; j++)
            {
                while (cur < m)
                {
                    uint cnt = binom[(m - cur - 1) * cols + (t - j - 1)];
                    if (rank < cnt) break;
                    rank -= cnt;
                    cur++;
                }
                int idx = i + 1 + cur;
                if (idx < 0 || idx >= n)
                {
                    return null;
                }
                indices[j + 1] = idx;
                cur++;
            }

            Array.Sort(indices);
            return indices;
        }

        private ModComboResult BuildResult(PlayerModDataSave playerMods, SolverConfig config, List<long> filtered, int[] localIndices, int score, int k)
        {
            var modSet = new ModComboResult { Score = score };
            modSet.ModuleSet = ModuleSet.FromValues(localIndices);

            var coreStats = new Dictionary<long, PowerCore>();
            foreach (var localIdx in localIndices)
            {
                var modId = filtered[localIdx];
                foreach (var powerCore in GetModPowerCores(playerMods, modId))
                {
                    if (coreStats.TryGetValue(powerCore.Id, out var existing))
                    {
                        existing.Value += powerCore.Value;
                        coreStats[powerCore.Id] = existing;
                    }
                    else
                    {
                        coreStats.Add(powerCore.Id, powerCore);
                    }
                }
            }

            modSet.Stats = OrderPowerCoresByPriorities(coreStats.Values.ToArray(), config.StatPriorities);

            var resolved = ModuleSet.FromValues(localIndices.Select(ix => (int)filtered[ix]).ToList());
            modSet.CombatScore = CalcCombosCombatScore(playerMods, resolved);

            return modSet;
        }
    }
}
