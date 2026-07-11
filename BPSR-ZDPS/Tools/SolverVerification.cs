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
    /// Headless accuracy harness: runs the GPU (exhaustive/exact) and CPU beam (approximate)
    /// backends over the SAME real inventory and configs, then reports the score gap.
    /// Invoked from Program.Main via the hidden "--verify-solver &lt;dataRoot&gt;" argument.
    /// The GPU pool cache is force-disabled so the GPU result is always a full enumeration
    /// (= the true optimum) and thus a valid ground truth to score the beam against.
    /// </summary>
    internal static class SolverVerification
    {
        public static void Run(string[] args)
        {
            string root = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(Path.Combine(root, "solver_verify_log.txt"))
                .CreateLogger();

            Directory.SetCurrentDirectory(root);
            Console.WriteLine($"[verify] data root = {root}");

            Settings.Load();
            // Force exhaustive GPU (no pool cache) so GPU == true optimum.
            Settings.Instance.WindowSettings.ModuleWindow.UseBruteForceCache = false;

            LoadSolverTables();

            ModuleSolver.StatCombatScores = HelperMethods.DataTables.ModEffects.Data
                .ToFrozenDictionary(x => $"{x.Value.EffectID}_{x.Value.EnhancementNum}", y => y.Value.FightValue);

            var modJson = File.ReadAllText(Path.Combine(Utils.DATA_DIR_NAME, "ModulesSaveData.json"));
            var mods = JsonConvert.DeserializeObject<PlayerModDataSave>(modJson);
            int owned = mods?.ModulesPackage?.Items?.Count ?? 0;
            Console.WriteLine($"[verify] owned modules = {owned}");
            Console.WriteLine($"[verify] beam width = {new ModuleOptimizerBeam(new SolverConfig(), mods, new Stopwatch(), new List<long>(), default).BeamWidth}");
            Console.WriteLine();

            var real = Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config;

            var cases = new List<(string name, SolverConfig cfg, int k)>
            {
                ("Real preset (as saved)",                          real,                    real.NumModules),
                ("Combat / brute-force-all / no priorities, K=5",   CombatBrute(real),       5),
                ("Combat / brute-force-all / no priorities, K=4",   CombatBrute(real),       4),
                ("ZScore(Enhanced) / 3 priorities Atleast, K=5",    ZScorePrio(real),        5),
                ("ZScore(Enhanced) / priorities + cap(-3), K=5",    ZScoreCap(real),         5),
            };

            foreach (var (name, cfg, k) in cases)
            {
                RunOne(name, cfg, k, mods);
            }

            Console.WriteLine("[verify] done.");
        }

        private static void RunOne(string name, SolverConfig baseCfg, int k, PlayerModDataSave mods)
        {
            var cfg = baseCfg.Clone();
            cfg.NumModules = k;

            Console.WriteLine(new string('=', 78));
            Console.WriteLine($"CASE: {name}");
            Console.WriteLine($"  ScoreMode={cfg.ScoreMode}  ScoringModel={cfg.ScoringModel}  BruteForceAll={cfg.BruteForceAllModules}  Cutoff={cfg.ModuleTotalCutoff}  K={cfg.NumModules}");
            Console.WriteLine($"  Priorities=[{string.Join(", ", cfg.StatPriorities.Select(p => $"{p.Id}:{(p.HasCap ? $"cap{p.ReqLevel}(<={p.GetCap()})" : $"{(p.StatMode == StatMode.Exactly ? "=" : ">=")}{p.ReqLevel}")}"))}]");

            var solver = new ModuleOptimizer();

            SolverResult gpu, beam;
            var swG = Stopwatch.StartNew();
            try { gpu = solver.Solve(cfg, mods, SolverModes.Gpu, CancellationToken.None); }
            catch (Exception ex) { Console.WriteLine($"  GPU FAILED: {ex.Message}\n"); return; }
            swG.Stop();

            var swB = Stopwatch.StartNew();
            try { beam = solver.Solve(cfg, mods, SolverModes.NormalV2, CancellationToken.None); }
            catch (Exception ex) { Console.WriteLine($"  BEAM FAILED: {ex.Message}\n"); return; }
            swB.Stop();

            var gpuList = (gpu.BestModResults ?? new()).OrderByDescending(r => r.Score).ToList();
            var beamList = (beam.BestModResults ?? new()).OrderByDescending(r => r.Score).ToList();

            int nGpu = gpu.FilteredModules?.Count ?? 0;
            int nBeam = beam.FilteredModules?.Count ?? 0;

            Console.WriteLine($"  Candidates: GPU N={nGpu}  Beam N={nBeam}   (C(N,K) exhaustive on GPU)");
            Console.WriteLine($"  Time: GPU {swG.Elapsed.TotalSeconds:F1}s   Beam {swB.Elapsed.TotalSeconds:F2}s");

            if (gpuList.Count == 0 || beamList.Count == 0)
            {
                Console.WriteLine($"  RESULTS: GPU={gpuList.Count} beam={beamList.Count} (no comparable results)\n");
                return;
            }

            int gpuBest = gpuList[0].Score;
            int beamBest = beamList[0].Score;
            int gap = gpuBest - beamBest;
            double gapPct = gpuBest != 0 ? 100.0 * gap / Math.Abs(gpuBest) : 0;

            // Canonical, backend-independent combat power of each backend's chosen best set.
            int gpuBestCombat = gpuList[0].CombatScore;
            int beamBestCombat = beamList[0].CombatScore;

            Console.WriteLine($"  --- Score (optimized metric, identical formula both backends) ---");
            Console.WriteLine($"    GPU  best = {gpuBest}   top10 = [{string.Join(", ", gpuList.Take(10).Select(r => r.Score))}]");
            Console.WriteLine($"    Beam best = {beamBest}   top10 = [{string.Join(", ", beamList.Take(10).Select(r => r.Score))}]");
            Console.WriteLine($"    GAP = {gap}  ({gapPct:F3}% below optimum)  {(gap == 0 ? "<-- beam found the optimum" : gap > 0 ? "<-- beam SUBOPTIMAL" : "<-- beam ABOVE gpu?! (check)")}");
            Console.WriteLine($"  --- Canonical CombatScore of each best set ---");
            Console.WriteLine($"    GPU={gpuBestCombat}  Beam={beamBestCombat}  gap={gpuBestCombat - beamBestCombat}");

            // Set-level overlap via global module ids.
            var gpuKeys = gpuList.Select(r => SetKey(r, gpu.FilteredModules)).ToList();
            var beamKeys = beamList.Select(r => SetKey(r, beam.FilteredModules)).ToList();
            var gpuTop10Set = gpuKeys.Take(10).ToHashSet();
            int overlap = beamKeys.Take(10).Count(gpuTop10Set.Contains);
            bool beamBestInGpuTop10 = gpuTop10Set.Contains(beamKeys[0]);

            Console.WriteLine($"  --- Set identity (global module ids) ---");
            Console.WriteLine($"    beam best set == a GPU top-10 set: {beamBestInGpuTop10}");
            Console.WriteLine($"    top-10 set overlap: {overlap}/10");
            Console.WriteLine();
        }

        /// <summary>Sorted global-module-id signature of a result's chosen set (local idx -> filtered[idx]).</summary>
        private static string SetKey(ModComboResult r, List<long> filtered)
        {
            var ids = r.ModuleSet.Mods.Where(x => x >= 0 && x < filtered.Count)
                .Select(x => filtered[x]).OrderBy(x => x);
            return string.Join(",", ids);
        }

        // ---- config builders (do not mutate the caller's config) ----

        private static SolverConfig CombatBrute(SolverConfig real)
        {
            var c = real.Clone();
            c.StatPriorities = new List<StatPrio>();
            c.BruteForceAllModules = true;
            c.ScoreMode = ScoreMode.CombatPower;
            return c;
        }

        private static SolverConfig ZScorePrio(SolverConfig real)
        {
            var c = real.Clone();
            c.BruteForceAllModules = false;
            c.ScoreMode = ScoreMode.ZScore;
            c.ScoringModel = ScoringModel.Enhanced;
            c.ValueAllStats = true;
            c.StatPriorities = new List<StatPrio>
            {
                new StatPrio(2104, 0, 12, StatMode.Atleast),
                new StatPrio(1112, 0, 8,  StatMode.Atleast),
                new StatPrio(1407, 0, 8,  StatMode.Atleast),
            };
            return c;
        }

        private static SolverConfig ZScoreCap(SolverConfig real)
        {
            var c = ZScorePrio(real);
            // Cap the third priority at tier 8 (ReqLevel -3): GPU enforces exactly, beam approximates.
            c.StatPriorities = new List<StatPrio>
            {
                new StatPrio(2104, 0, 12, StatMode.Atleast),
                new StatPrio(1112, 0, 8,  StatMode.Atleast),
                new StatPrio(1407, 0, -3, StatMode.Atleast),
            };
            return c;
        }

        private static void LoadSolverTables()
        {
            string dir = Utils.DATA_DIR_NAME;
            HelperMethods.DataTables.Modules.Data =
                JsonConvert.DeserializeObject<Dictionary<int, ModuleData>>(File.ReadAllText(Path.Combine(dir, "ModTable.json")));
            HelperMethods.DataTables.ModEffects.Data =
                JsonConvert.DeserializeObject<Dictionary<int, EffectData>>(File.ReadAllText(Path.Combine(dir, "ModEffectTable.json")));
            HelperMethods.DataTables.ModLinkEffects.Data =
                JsonConvert.DeserializeObject<Dictionary<int, ModLinkEffect>>(File.ReadAllText(Path.Combine(dir, "ModLinkEffectTable.json")));
            Console.WriteLine($"[verify] tables: Modules={HelperMethods.DataTables.Modules.Data.Count} ModEffects={HelperMethods.DataTables.ModEffects.Data.Count} ModLinkEffects={HelperMethods.DataTables.ModLinkEffects.Data.Count}");
        }
    }
}
