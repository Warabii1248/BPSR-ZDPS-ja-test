using BPSR_ZDPS.DataTypes;
using BPSR_ZDPS.DataTypes.Modules;
using BPSR_ZDPS.Managers;
using BPSR_ZDPS.Windows;
using Newtonsoft.Json;
using Serilog;
using System.Collections.Frozen;
using System.Diagnostics;

namespace BPSR_ZDPS.Tools
{
    /// <summary>
    /// Headless accuracy/perf harness: runs the GPU (exhaustive/exact) and CPU beam
    /// (approximate) backends over the SAME real inventory and reports the score gap
    /// and timings. Invoked from Program.Main via the hidden "--verify-solver" argument:
    ///
    ///   BPSR-ZDPS.exe --verify-solver &lt;dataRoot&gt; [--truth file.json] [--mode both|cpu|gpu]
    ///                 [--repeat N] [--json out.json] [--cases a,b,c]
    ///
    /// The GPU pool cache is force-disabled so a GPU run is always a full enumeration
    /// (= the true optimum). With --truth, GPU ground truth is computed once and cached
    /// to the file; later runs (e.g. --mode cpu while iterating on the beam) compare
    /// against the cached truth without re-running the GPU.
    /// </summary>
    internal static class SolverVerification
    {
        private class TruthEntry
        {
            public string ConfigSig = "";
            public long InvHash;
            public int BestScore;
            public List<int> Top10 = new();
            public string BestSetKey = "";
            public double GpuSeconds;
        }

        private class CaseResult
        {
            public string Name = "";
            public int GpuBest, BeamBest, Gap;
            public double GapPct;
            public double GpuSeconds, BeamSeconds;
            public int Top10Overlap = -1;
            public bool BeamBestInGpuTop10;
            public bool GpuMatchesTruth = true;
        }

        public static void Run(string[] args)
        {
            string root = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Directory.GetCurrentDirectory();
            string? truthPath = GetOpt(args, "--truth");
            string mode = GetOpt(args, "--mode") ?? "both";      // both | cpu | gpu
            int repeat = int.TryParse(GetOpt(args, "--repeat"), out var r) ? Math.Max(1, r) : 1;
            string? jsonOut = GetOpt(args, "--json");
            var onlyCases = (GetOpt(args, "--cases") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

            Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().CreateLogger();

            Directory.SetCurrentDirectory(root);
            Console.WriteLine($"[verify] data root = {root}   mode={mode} repeat={repeat}");

            Settings.Load();
            // Force exhaustive GPU (no pool cache) so GPU == true optimum.
            Settings.Instance.WindowSettings.ModuleWindow.UseBruteForceCache = false;

            LoadSolverTables();
            ModuleSolver.StatCombatScores = HelperMethods.DataTables.ModEffects.Data
                .ToFrozenDictionary(x => $"{x.Value.EffectID}_{x.Value.EnhancementNum}", y => y.Value.FightValue);

            var mods = JsonConvert.DeserializeObject<PlayerModDataSave>(
                File.ReadAllText(Path.Combine(Utils.DATA_DIR_NAME, "ModulesSaveData.json")))!;
            long invHash = InventoryHash(mods);
            Console.WriteLine($"[verify] owned modules = {mods.ModulesPackage?.Items?.Count ?? 0}   invHash={invHash:X}");

            var truth = LoadTruth(truthPath);
            var real = Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config;

            if (mode == "cache")
            {
                RunCacheChecks(mods, real);
                return;
            }

            var cases = BuildCases(real);

            var results = new List<CaseResult>();
            foreach (var (name, cfg) in cases)
            {
                if (onlyCases.Count > 0 && !onlyCases.Contains(name)) continue;
                var res = RunOne(name, cfg, mods, invHash, truth, truthPath, mode, repeat);
                if (res != null) results.Add(res);
            }

            Console.WriteLine(new string('=', 100));
            Console.WriteLine($"{"CASE",-28} {"GPUbest",8} {"BEAMbest",8} {"GAP",6} {"GAP%",8} {"ovl",5} {"GPU s",8} {"BEAM s",8}");
            foreach (var s in results)
            {
                Console.WriteLine($"{s.Name,-28} {s.GpuBest,8} {s.BeamBest,8} {s.Gap,6} {s.GapPct,7:F3}% {(s.Top10Overlap >= 0 ? s.Top10Overlap + "/10" : "-"),5} {s.GpuSeconds,8:F2} {s.BeamSeconds,8:F2}{(s.GpuMatchesTruth ? "" : "  ** GPU!=TRUTH **")}");
            }
            int misses = results.Count(x => x.Gap > 0);
            Console.WriteLine($"[verify] beam misses: {misses}/{results.Count}   total gap: {results.Sum(x => x.Gap)}   GPU exactness: {(results.All(x => x.GpuMatchesTruth) ? "OK" : "FAILED")}");

            if (jsonOut != null)
            {
                File.WriteAllText(jsonOut, JsonConvert.SerializeObject(results, Formatting.Indented));
                Console.WriteLine($"[verify] json -> {jsonOut}");
            }
            Console.WriteLine("[verify] done.");
        }

        private static CaseResult? RunOne(string name, SolverConfig cfg, PlayerModDataSave mods, long invHash,
            Dictionary<string, TruthEntry> truth, string? truthPath, string mode, int repeat)
        {
            string sig = ConfigSig(cfg);
            double estCombos = ModuleOptimizer.EstimateComboCount(cfg, mods);

            Console.WriteLine(new string('=', 100));
            Console.WriteLine($"CASE: {name}   K={cfg.NumModules}  ~{estCombos:E2} combos  [{DescribeGates(cfg)}]");

            var solver = new ModuleOptimizer();
            var res = new CaseResult { Name = name };

            // ---- GPU (exact) or cached truth ----
            TruthEntry? te = truth.TryGetValue(name, out var t) && t.ConfigSig == sig && t.InvHash == invHash ? t : null;
            SolverResult? gpu = null;

            if (mode != "cpu" || te == null)
            {
                if (estCombos > 2.0e11)
                {
                    Console.WriteLine($"  SKIP: {estCombos:E2} combos too large for exhaustive ground truth.");
                    return null;
                }
                var swG = Stopwatch.StartNew();
                try { gpu = solver.Solve(cfg, mods, SolverModes.Gpu, CancellationToken.None); }
                catch (Exception ex) { Console.WriteLine($"  GPU FAILED: {ex.Message}"); return null; }
                swG.Stop();
                res.GpuSeconds = swG.Elapsed.TotalSeconds;

                var gl = gpu.BestModResults.OrderByDescending(x => x.Score).ToList();
                if (gl.Count == 0) { Console.WriteLine("  GPU: no results"); return null; }

                var freshTruth = new TruthEntry
                {
                    ConfigSig = sig,
                    InvHash = invHash,
                    BestScore = gl[0].Score,
                    Top10 = gl.Take(10).Select(x => x.Score).ToList(),
                    BestSetKey = SetKey(gl[0], gpu.FilteredModules),
                    GpuSeconds = res.GpuSeconds,
                };

                if (te != null)
                {
                    // Truth existed: verify the (possibly modified) GPU backend is still exact.
                    res.GpuMatchesTruth = te.BestScore == freshTruth.BestScore && te.Top10.SequenceEqual(freshTruth.Top10);
                    if (!res.GpuMatchesTruth)
                        Console.WriteLine($"  !!! GPU DIVERGED FROM TRUTH: best {freshTruth.BestScore} vs truth {te.BestScore}");
                }
                te = res.GpuMatchesTruth ? (truth.TryGetValue(name, out var keep) && keep.ConfigSig == sig ? keep : freshTruth) : freshTruth;
                truth[name] = te;
                SaveTruth(truthPath, truth);
            }
            else
            {
                res.GpuSeconds = te.GpuSeconds;
                Console.WriteLine($"  GPU: using cached truth (best={te.BestScore})");
            }

            res.GpuBest = te!.BestScore;

            if (mode == "gpu")
            {
                Console.WriteLine($"  GPU best={res.GpuBest}  time={res.GpuSeconds:F2}s  exact={(res.GpuMatchesTruth ? "OK" : "DIVERGED")}");
                return res;
            }

            // ---- CPU beam (median of N repeats) ----
            SolverResult? beam = null;
            var times = new List<double>();
            for (int i = 0; i < repeat; i++)
            {
                var swB = Stopwatch.StartNew();
                try { beam = solver.Solve(cfg, mods, SolverModes.NormalV2, CancellationToken.None); }
                catch (Exception ex) { Console.WriteLine($"  BEAM FAILED: {ex.Message}"); return null; }
                swB.Stop();
                times.Add(swB.Elapsed.TotalSeconds);
            }
            times.Sort();
            res.BeamSeconds = times[times.Count / 2];

            var bl = beam!.BestModResults.OrderByDescending(x => x.Score).ToList();
            if (bl.Count == 0) { Console.WriteLine("  BEAM: no results"); res.BeamBest = 0; res.Gap = res.GpuBest; res.GapPct = 100; return res; }

            res.BeamBest = bl[0].Score;
            res.Gap = res.GpuBest - res.BeamBest;
            res.GapPct = res.GpuBest != 0 ? 100.0 * res.Gap / Math.Abs(res.GpuBest) : 0;

            if (gpu != null)
            {
                var gl = gpu.BestModResults.OrderByDescending(x => x.Score).ToList();
                var gpuTop = gl.Take(10).Select(x => SetKey(x, gpu.FilteredModules)).ToHashSet();
                var beamKeys = bl.Take(10).Select(x => SetKey(x, beam.FilteredModules)).ToList();
                res.Top10Overlap = beamKeys.Count(gpuTop.Contains);
                res.BeamBestInGpuTop10 = gpuTop.Contains(beamKeys[0]);
            }

            Console.WriteLine($"  GPU best={res.GpuBest} ({res.GpuSeconds:F2}s)   BEAM best={res.BeamBest} ({res.BeamSeconds:F2}s median of {repeat})");
            Console.WriteLine($"  GAP = {res.Gap} ({res.GapPct:F3}%)  {(res.Gap == 0 ? "OK: beam optimal" : res.Gap > 0 ? "beam SUBOPTIMAL" : "beam>gpu ?! CHECK")}   beam top10=[{string.Join(",", bl.Take(10).Select(x => x.Score))}]");
            return res;
        }

        // ---- cache checks (--mode cache) ----

        /// <summary>
        /// Verifies pool-cache extraction under cap gates: a BASE config full-solves with
        /// the cache enabled (building the pool), then a VARIATION (cap added/changed) is
        /// solved twice - once answered by the cache, once cache-disabled (exact ground
        /// truth) - and the top-10 score lists must match exactly whenever the cache path
        /// claims exactness. Also reports whether the pool was actually used vs fell back.
        /// </summary>
        private static void RunCacheChecks(PlayerModDataSave mods, SolverConfig real)
        {
            // Pool store/overflow outcomes log at Information; surface them here so a
            // "FromCache=False" line can be told apart (overflowed vs threshold miss).
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console().CreateLogger();
            Settings.Instance.WindowSettings.ModuleWindow.CacheThresholdPct = 20;
            var solver = new ModuleOptimizer();

            // Fresh StatPrio instances everywhere: configs share nothing mutable.
            SolverConfig ZBase() => Mut(real, c =>
            {
                c.BruteForceAllModules = false; c.ScoreMode = ScoreMode.ZScore; c.ScoringModel = ScoringModel.Enhanced; c.NumModules = 5;
                c.StatPriorities = new List<StatPrio>
                {
                    new StatPrio(2104, 0, 12, StatMode.Atleast),
                    new StatPrio(1112, 0, 8, StatMode.Atleast),
                    new StatPrio(1407, 0, 8, StatMode.Atleast),
                };
            });
            SolverConfig CBase() => Mut(real, c =>
            {
                c.BruteForceAllModules = true; c.ScoreMode = ScoreMode.CombatPower; c.NumModules = 6; c.ModuleTotalCutoff = 14;
                c.StatPriorities = new List<StatPrio>
                {
                    new StatPrio(2104, 0, 20, StatMode.Exactly),
                    new StatPrio(1112, 0, 16, StatMode.Atleast),
                    new StatPrio(1110, 0, 0, StatMode.Atleast),
                };
            });

            var checks = new List<(string name, SolverConfig baseCfg, SolverConfig varCfg)>
            {
                ("zscore: cap -3 on 1407",      ZBase(), Mut(ZBase(), c => c.StatPriorities[2] = new StatPrio(1407, 0, -3, StatMode.Atleast))),
                ("zscore: cap -2 on 1407",      ZBase(), Mut(ZBase(), c => c.StatPriorities[2] = new StatPrio(1407, 0, -2, StatMode.Atleast))),
                ("combat k6: cap -2 on 1110",   CBase(), Mut(CBase(), c => c.StatPriorities[2] = new StatPrio(1110, 0, -2, StatMode.Atleast))),
                ("zscore: exclude -6 on 1110",  ZBase(), Mut(ZBase(), c => c.StatPriorities.Add(new StatPrio(1110, 0, -6, StatMode.Atleast)))),
            };

            int failures = 0;
            foreach (var (name, baseCfg, varCfg) in checks)
            {
                Console.WriteLine(new string('=', 100));
                Console.WriteLine($"CACHE CHECK: {name}");

                // Exact ground truth for the variation (cache off).
                Settings.Instance.WindowSettings.ModuleWindow.UseBruteForceCache = false;
                ModuleOptimizer.ClearPoolCache();
                var truth = solver.Solve(varCfg, mods, SolverModes.Gpu, CancellationToken.None);
                var truthScores = truth.BestModResults.Select(x => x.Score).OrderByDescending(x => x).ToList();

                // Cache on: base full solve builds the pool, then the variation queries it.
                Settings.Instance.WindowSettings.ModuleWindow.UseBruteForceCache = true;
                ModuleOptimizer.ClearPoolCache();
                var swBase = Stopwatch.StartNew();
                solver.Solve(baseCfg, mods, SolverModes.Gpu, CancellationToken.None);
                swBase.Stop();
                var swVar = Stopwatch.StartNew();
                var cached = solver.Solve(varCfg, mods, SolverModes.Gpu, CancellationToken.None);
                swVar.Stop();
                var cachedScores = cached.BestModResults.Select(x => x.Score).OrderByDescending(x => x).ToList();

                bool match = truthScores.SequenceEqual(cachedScores);
                if (!match) failures++;

                Console.WriteLine($"  base solve {swBase.Elapsed.TotalSeconds:F2}s -> variation {swVar.Elapsed.TotalSeconds:F2}s  " +
                    $"FromCache={cached.FromCache} CacheExact={cached.CacheExact}");
                Console.WriteLine($"  truth  top10: [{string.Join(",", truthScores)}]");
                Console.WriteLine($"  cached top10: [{string.Join(",", cachedScores)}]");
                Console.WriteLine($"  {(match ? "OK: identical" : "!!! MISMATCH !!!")}");
            }

            Settings.Instance.WindowSettings.ModuleWindow.UseBruteForceCache = false;
            Console.WriteLine(new string('=', 100));
            Console.WriteLine($"[verify] cache checks: {checks.Count - failures}/{checks.Count} passed");
        }

        // ---- case definitions ----

        private static List<(string, SolverConfig)> BuildCases(SolverConfig real)
        {
            var list = new List<(string, SolverConfig)>();

            list.Add(("real-preset", real.Clone()));

            list.Add(("exactly2-combat", Mut(real, c =>
            {
                c.BruteForceAllModules = true; c.ScoreMode = ScoreMode.CombatPower; c.NumModules = 5;
                c.StatPriorities = new List<StatPrio>
                {
                    new StatPrio(2104, 0, 20, StatMode.Exactly),
                    new StatPrio(1112, 0, 16, StatMode.Exactly),
                    new StatPrio(1410, 0, 12, StatMode.Atleast),
                };
            })));

            list.Add(("caps-zscore", Mut(real, c =>
            {
                c.BruteForceAllModules = false; c.ScoreMode = ScoreMode.ZScore; c.ScoringModel = ScoringModel.Enhanced; c.NumModules = 5;
                c.StatPriorities = new List<StatPrio>
                {
                    new StatPrio(1112, 0, 16, StatMode.Atleast),
                    new StatPrio(1110, 0, -2, StatMode.Atleast),
                    new StatPrio(1407, 0, -3, StatMode.Atleast),
                };
            })));

            list.Add(("exclude-zscore", Mut(real, c =>
            {
                c.BruteForceAllModules = false; c.ScoreMode = ScoreMode.ZScore; c.ScoringModel = ScoringModel.Enhanced; c.NumModules = 5;
                c.StatPriorities = new List<StatPrio>
                {
                    new StatPrio(2104, 0, 12, StatMode.Atleast),
                    new StatPrio(1112, 0, 8, StatMode.Atleast),
                    new StatPrio(1110, 0, -6, StatMode.Atleast),
                };
            })));

            list.Add(("nogate-combat-k5", Mut(real, c =>
            {
                c.BruteForceAllModules = true; c.ScoreMode = ScoreMode.CombatPower; c.NumModules = 5;
                c.StatPriorities = new List<StatPrio>();
            })));

            list.Add(("zscore-prios-k5", Mut(real, c =>
            {
                c.BruteForceAllModules = false; c.ScoreMode = ScoreMode.ZScore; c.ScoringModel = ScoringModel.Enhanced; c.NumModules = 5;
                c.StatPriorities = new List<StatPrio>
                {
                    new StatPrio(2104, 0, 12, StatMode.Atleast),
                    new StatPrio(1112, 0, 8, StatMode.Atleast),
                    new StatPrio(1407, 0, 8, StatMode.Atleast),
                };
            })));

            list.Add(("zscore-original-k5", Mut(real, c =>
            {
                c.BruteForceAllModules = false; c.ScoreMode = ScoreMode.ZScore; c.ScoringModel = ScoringModel.Original; c.NumModules = 5;
                c.StatPriorities = new List<StatPrio>
                {
                    new StatPrio(2104, 0, 12, StatMode.Atleast),
                    new StatPrio(1112, 0, 8, StatMode.Atleast),
                    new StatPrio(1407, 0, 8, StatMode.Atleast),
                };
            })));

            // Higher K with a tight cutoff to keep exhaustive ground truth feasible.
            list.Add(("gates-combat-k6-cut14", Mut(real, c =>
            {
                c.BruteForceAllModules = true; c.ScoreMode = ScoreMode.CombatPower; c.NumModules = 6; c.ModuleTotalCutoff = 14;
                c.StatPriorities = new List<StatPrio>
                {
                    new StatPrio(2104, 0, 20, StatMode.Exactly),
                    new StatPrio(1112, 0, 16, StatMode.Atleast),
                    new StatPrio(1110, 0, -2, StatMode.Atleast),
                };
            })));

            return list;
        }

        private static SolverConfig Mut(SolverConfig baseCfg, Action<SolverConfig> mutate)
        {
            var c = baseCfg.Clone();
            // Clone() shares the StatPriorities list reference; replace it before mutating.
            c.StatPriorities = baseCfg.StatPriorities.ToList();
            mutate(c);
            return c;
        }

        // ---- helpers ----

        private static string? GetOpt(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        private static string DescribeGates(SolverConfig c) =>
            $"{c.ScoreMode}/{c.ScoringModel}/brute={c.BruteForceAllModules}/cut={c.ModuleTotalCutoff} " +
            string.Join(", ", c.StatPriorities.Select(p => $"{p.Id}:{(p.HasCap ? $"<= {p.GetCap()}" : $"{(p.StatMode == StatMode.Exactly ? "=" : ">=")}{p.ReqLevel}")}"));

        private static string ConfigSig(SolverConfig c) =>
            JsonConvert.SerializeObject(new
            {
                c.NumModules, c.ScoreMode, c.ScoringModel, c.BruteForceAllModules, c.ModuleTotalCutoff,
                c.ValueAllStats, c.OrderBoostStrength, c.LegendaryStatMultiplier,
                Q = c.QualitiesV2.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"),
                P = c.StatPriorities.Select(p => $"{p.Id}/{p.ReqLevel}/{(int)p.StatMode}"),
                L = Convert.ToBase64String(c.LinkLevelBonus),
            });

        private static long InventoryHash(PlayerModDataSave mods)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                foreach (var kv in mods.ModulesPackage!.Items.OrderBy(x => x.Key))
                {
                    h = (h ^ kv.Key) * 1099511628211L;
                    h = (h ^ kv.Value.ConfigId) * 1099511628211L;
                    if (mods.Mod!.ModInfos.TryGetValue(kv.Key, out var info))
                        foreach (var l in info.InitLinkNums) h = (h ^ l) * 1099511628211L;
                }
                return h;
            }
        }

        private static string SetKey(ModComboResult r, List<long> filtered)
        {
            var ids = r.ModuleSet.Mods.Where(x => x >= 0 && x < filtered.Count)
                .Select(x => filtered[x]).OrderBy(x => x);
            return string.Join(",", ids);
        }

        private static Dictionary<string, TruthEntry> LoadTruth(string? path)
        {
            if (path == null || !File.Exists(path)) return new();
            try { return JsonConvert.DeserializeObject<Dictionary<string, TruthEntry>>(File.ReadAllText(path)) ?? new(); }
            catch { return new(); }
        }

        private static void SaveTruth(string? path, Dictionary<string, TruthEntry> truth)
        {
            if (path == null) return;
            File.WriteAllText(path, JsonConvert.SerializeObject(truth, Formatting.Indented));
        }

        private static void LoadSolverTables()
        {
            string dir = Utils.DATA_DIR_NAME;
            HelperMethods.DataTables.Modules.Data =
                JsonConvert.DeserializeObject<Dictionary<int, ModuleData>>(File.ReadAllText(Path.Combine(dir, "ModTable.json")))!;
            HelperMethods.DataTables.ModEffects.Data =
                JsonConvert.DeserializeObject<Dictionary<int, EffectData>>(File.ReadAllText(Path.Combine(dir, "ModEffectTable.json")))!;
            HelperMethods.DataTables.ModLinkEffects.Data =
                JsonConvert.DeserializeObject<Dictionary<int, ModLinkEffect>>(File.ReadAllText(Path.Combine(dir, "ModLinkEffectTable.json")))!;
        }
    }
}
