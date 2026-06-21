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

        private static GpuComputeContext? _gpuContext;
        private static bool _gpuInitFailed;
        private static readonly object _gpuLock = new();

        /// <summary>Name of the GPU bound for compute, or null until the first GPU solve runs.</summary>
        public static string? GpuAdapterName => _gpuContext?.AdapterName;

        /// <summary>True if GPU compute init failed and the solver fell back to CPU.</summary>
        public static bool GpuUnavailable => _gpuInitFailed;

        /// <summary>
        /// Lazily creates the shared compute context. Throws (so the caller falls back to CPU)
        /// if the GPU/DirectCompute is unavailable.
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
                    Log.Warning(ex, "GPU module solver unavailable; falling back to CPU.");
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

        private SolverResult GpuSolve(SolverConfig config, PlayerModDataSave playerMods, Stopwatch sw, List<long> filtered, CancellationToken cancelToken)
        {
            int n = filtered.Count;
            int k = Math.Clamp(config.NumModules, 1, ModuleSet.MaxModules);

            if (n < k)
            {
                sw.Stop();
                return new SolverResult { FilteredModules = filtered };
            }

            var gpu = EnsureGpuContext();

            // ---- normalized stat indexing (shared with the CPU vector path) ----
            var possibleStats = NormalizeStatsLookup(playerMods, filtered); // origStatId -> normIndex
            int s = possibleStats.Count;
            if (s > GpuMaxStats)
            {
                throw new NotSupportedException($"Too many distinct stats ({s}) for the GPU solver.");
            }

            var input = new GpuSolveInput
            {
                NumModules = n,
                NumModulesInSet = k,
                NumStats = s,
                ScoreMode = config.ScoreMode == ScoreMode.CombatPower ? 1 : 0,
                HasExact = config.StatPriorities.Any(p => p.StatMode == StatMode.Exactly),
                MaxTotal = k * 20,
            };

            input.ModuleStats = BuildModuleStats(playerMods, filtered, possibleStats, s);
            BuildStatConfig(config, possibleStats, s, out input.StatMul, out input.StatReq, out input.StatMin, out input.StatExact);
            input.Binom = BuildBinomial(n, k);
            input.LinkBonus = BuildLinkBonus(config);
            input.StatCombat = BuildStatCombat(possibleStats, s);
            input.LinkTotalFight = BuildLinkTotalFight(input.MaxTotal);

            var candidates = gpu.Dispatch(input);
            sw.Stop();

            var top10 = MergeTop10(candidates, k, n, input.Binom);

            var results = new List<ModComboResult>(top10.Count);
            foreach (var (score, localIndices) in top10)
            {
                if (cancelToken.IsCancellationRequested)
                {
                    break;
                }

                results.Add(BuildResult(playerMods, config, filtered, localIndices, score, k));
            }

            return new SolverResult
            {
                BestModResults = results,
                FilteredModules = filtered
            };
        }

        private static uint[] BuildModuleStats(PlayerModDataSave playerMods, List<long> filtered, Dictionary<int, int> possibleStats, int s)
        {
            var arr = new uint[filtered.Count * s];
            for (int i = 0; i < filtered.Count; i++)
            {
                long modId = filtered[i];
                var modParts = playerMods.ModulesPackage.Items[modId].ModNewAttr.ModParts;
                var linkNums = playerMods.Mod.ModInfos[modId].InitLinkNums;
                for (int p = 0; p < modParts.Count; p++)
                {
                    int normIdx = possibleStats[modParts[p]];
                    arr[i * s + normIdx] = (uint)linkNums[p];
                }
            }
            return arr;
        }

        private void BuildStatConfig(SolverConfig config, Dictionary<int, int> possibleStats, int s,
            out int[] mul, out int[] req, out int[] min, out int[] exact)
        {
            mul = new int[s];
            req = new int[s];
            min = new int[s];
            exact = new int[s];

            foreach (var (origId, normIdx) in possibleStats)
            {
                mul[normIdx] = GetStatMultiplier(config, origId);
                var prio = config.StatPriorities.FirstOrDefault(p => p.Id == origId);
                if (prio != null)
                {
                    req[normIdx] = prio.ReqLevel;
                    min[normIdx] = prio.MinLevel;
                    exact[normIdx] = prio.StatMode == StatMode.Exactly ? 1 : 0;
                }
            }
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
                        throw new NotSupportedException("Combination count exceeds uint32 range; using CPU solver.");
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
            var arr = new int[s * 6];
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

        private static List<(int Score, int[] LocalIndices)> MergeTop10(GpuCandidate[] candidates, int k, int n, uint[] binom)
        {
            var seen = new HashSet<string>();
            var merged = new List<(int Score, int[] LocalIndices)>();

            foreach (var ordered in candidates
                .Where(c => c.Score > GpuScoreInvalid)
                .OrderByDescending(c => c.Score))
            {
                var indices = DecodeCombo(ordered, k, n, binom);
                if (indices == null)
                {
                    continue;
                }

                var key = string.Join('_', indices);
                if (!seen.Add(key))
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
