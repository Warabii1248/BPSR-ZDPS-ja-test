using BPSR_ZDPS.DataTypes;
using BPSR_ZDPS.DataTypes.Modules;
using BPSR_ZDPS.Managers;
using Hexa.NET.ImGui;
using Newtonsoft.Json;
using Serilog;
using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using ZLinq;
using Zproto;

namespace BPSR_ZDPS
{
    public class ModuleSolver
    {
        private static bool IsOpen = false;

        public static FrozenDictionary<string, int> StatCombatScores;
        public static int[] LegendaryStats = [2104, 2105, 2204, 2205, 2404, 2405, 2406, 2304];
        private static PlayerModDataSave PlayerModData = new PlayerModDataSave();
        private static PlayerModDataSave ResultsPlayerModData = new PlayerModDataSave();
        private static FrozenDictionary<int, ModStatInfo> ModStatInfos;
        private static FrozenDictionary<int, ModuleType> ModTypeMapping;
        private static List<int> ModStatIds = new List<int>();
        private static string ModuleImgBasePath;
        private static int NumTotalModules = 0;
        private static int NumAttackModules = 0;
        private static int NumSupportModules = 0;
        private static int NumGuardModules = 0;
        private static string ModSavePath => Path.Combine(Utils.DATA_DIR_NAME, "ModulesSaveData.json");

        private static SolverConfig SolverConfig = new SolverConfig();
        private static ModStatInfo? PendingStatToAdd = null;
        private static List<ModComboResult>? BestModResults = null;
        private static bool ShouldBlockMainUI = false;
        private static bool IsCalculating = false;
        private static CancellationTokenSource ModuleCalcCancelTokenSource = new();
        private static Task ModuleCalcTask;
        private static DateTime ModuleCalcStartTime = DateTime.Now;
        private static string CurrentPresetString = "";

        // Widest settings-row label seen so far. The label column is sized to this so
        // longer localized labels (JP) don't get clipped at the original fixed 200px.
        private static float SettingsLabelColWidth = 200f;
        static int RunOnceDelayed = 0;
        private static bool ShouldTrackOpenState;
        private static bool LastSolveUsedCpuFallback;
        // Solve progress (0..1), written from the solver's worker threads.
        private static volatile float CalcProgress;
        // Non-null when the last solve failed; shown in the results panel.
        private static string? LastSolveError;
        // Last solve came from the brute-force pool cache (and whether it was exact).
        private static bool LastSolveFromCache;
        private static bool LastSolveCacheExact;

        public static List<long> FilteredModules = [];

        // Live estimate of the exhaustive combination count for the current settings, shown
        // next to the Calculate button (GPU/exhaustive backend only). Recomputed only when a
        // lightweight signature of the inputs changes (see RefreshComboCountEstimate).
        private static string ComboCountText = "";
        private static string ComboCountSig = "";

        public static void Init()
        {
            ModuleImgBasePath = Path.Combine(Utils.DATA_DIR_NAME, "Images", "Modules");
            var effectStatTypes = HelperMethods.DataTables.ModEffects.Data.DistinctBy(x => x.Value.EffectID).Select(x => new ModStatInfo()
            {
                Name = x.Value.EffectName,
                Icon = x.Value.EffectConfigIcon,
                StatId = x.Value.EffectID,
                IconRef = ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, $"{x.Value.EffectConfigIcon.Split('/').Last()}.png")) ??
                    ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "Missing.png"))
            });

            StatCombatScores = HelperMethods.DataTables.ModEffects.Data
                .ToFrozenDictionary(x => $"{x.Value.EffectID}_{x.Value.EnhancementNum}",
                y => y.Value.FightValue);

            ModStatInfos = effectStatTypes.ToFrozenDictionary(x => x.StatId, y => y);
            ModTypeMapping = HelperMethods.DataTables.Modules.Data.ToFrozenDictionary(x => x.Value.Id, y => (ModuleType)y.Value.SimilarId);

            ModStatIds = effectStatTypes.Select(x => x.StatId).ToList();

            PendingStatToAdd = effectStatTypes.FirstOrDefault();

            LoadSavedModData(ModSavePath);

            SolverConfig = Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config;

            CurrentPresetString = SolverConfig.SaveToString();
        }

        public static void Open()
        {
            RunOnceDelayed = 0;
            IsOpen = true;
        }

        public static void SetPlayerInv(CharSerialize data)
        {
            lock (PlayerModData)
            {
                PlayerModData = new PlayerModDataSave()
                {
                    ModulesPackage = data.ItemPackage?.Packages[5] ?? null,
                    Mod = data?.Mod ?? null
                };

                SaveModData(ModSavePath);
                ModuleInvUpdated();
            }
        }

        private static void ModuleInvUpdated()
        {
            if (PlayerModData.ModulesPackage != null)
            {
                NumTotalModules = PlayerModData.ModulesPackage.Items.Count();
                NumAttackModules = PlayerModData.ModulesPackage.Items.Count(x => IsModuleOfType(x.Value.ConfigId, ModuleType.Attack));
                NumSupportModules = PlayerModData.ModulesPackage.Items.Count(x => IsModuleOfType(x.Value.ConfigId, ModuleType.Support));
                NumGuardModules = PlayerModData.ModulesPackage.Items.Count(x => IsModuleOfType(x.Value.ConfigId, ModuleType.Guard));
            }
        }

        public static void Draw()
        {
            if (!IsOpen && IsCalculating)
            {
                IsOpen = true;
            }

            if (!IsOpen) return;

            var windowSize = new Vector2(800, 500);
            float leftWidth = 320;
            ImGui.SetNextWindowSize(windowSize, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(1270, 700), new Vector2(float.PositiveInfinity, float.PositiveInfinity));

            if (Settings.Instance.WindowSettings.ModuleWindow.WindowPosition != new Vector2())
            {
                ImGui.SetNextWindowPos(Settings.Instance.WindowSettings.ModuleWindow.WindowPosition, ImGuiCond.FirstUseEver);
            }

            if (ImGui.Begin($"{AppStrings.GetLocalized("Module_Title")}###ModuleOptimizer", ref IsOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking))
            {
                ShouldTrackOpenState = true;

                if (RunOnceDelayed == 0)
                {
                    RunOnceDelayed++;
                }
                else if (RunOnceDelayed == 1)
                {
                    RunOnceDelayed++;
                    Utils.SetCurrentWindowIcon();
                    Utils.BringWindowToFront();
                }

                var shouldBlock = CheckAndDrawNoModulesBanner();
                if (ModuleCalcTask?.Status == TaskStatus.Running)
                {
                    var timeTaken = DateTime.Now - ModuleCalcStartTime;
                    var progressPct = (int)(Math.Clamp(CalcProgress, 0f, 1f) * 100);
                    DrawBanner(string.Format(AppStrings.GetLocalized("Module_Calculating"), $"{timeTaken:mm\\:ss}", progressPct), 0xFF005DD9, "Thinking.png", true,
                        (drawList, txtPos, txtSize, bannerHeight) =>
                        {
                            var cancelButtonStart = txtPos + new Vector2(0, 100);
                            var cancelButtonEnd = cancelButtonStart + new Vector2(txtSize.X, 30);
                            var isHovered = ImGui.IsMouseHoveringRect(cancelButtonStart, cancelButtonEnd);
                            drawList.AddRectFilled(cancelButtonStart, cancelButtonEnd, ImGui.ColorConvertFloat4ToU32(isHovered ? Colors.Gray : Colors.DimGray));
                            ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], 25.0f);
                            drawList.AddText(cancelButtonStart + new Vector2((txtSize.X / 2) - 25, 0), ImGui.ColorConvertFloat4ToU32(Colors.White), AppStrings.GetLocalized("Module_Cancel"));
                            ImGui.PopFont();

                            if (isHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                            {
                                ModuleCalcCancelTokenSource.Cancel();
                            }
                        });
                }

                ImGui.BeginDisabled(ShouldBlockMainUI || shouldBlock);

                if (ImGui.BeginTabBar("MainTabBar", ImGuiTabBarFlags.None))
                {
                    if (ImGui.BeginTabItem($"{AppStrings.GetLocalized("Module_Tab_Optimizer")}###OptimizerTab"))
                    {
                        DrawSolverTab(ImGui.GetContentRegionAvail(), leftWidth);
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem($"{AppStrings.GetLocalized("Module_Tab_Inventory")}###InventoryTab"))
                    {
                        DrawModuleInv();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem($"{AppStrings.GetLocalized("Module_Tab_Settings")}###SettingsTab"))
                    {
                        if (ImGui.BeginTable("settings_table", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.BordersInnerH))
                        {
                            ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, SettingsLabelColWidth);
                            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

                            AddSettingRow(AppStrings.GetLocalized("Module_Settings_PresetShareCode"), () => {
                                ImGui.SetNextItemWidth(400);
                                if (ImGui.InputText("##PresetCode", ref CurrentPresetString, 1024, ImGuiInputTextFlags.AutoSelectAll))
                                {

                                }
                                ImGui.SameLine();
                                if (ImGui.Button(AppStrings.GetLocalized("Module_Apply") + "##ApplyPreset"))
                                {
                                    var solverConfig = new SolverConfig();
                                    solverConfig.FromString(CurrentPresetString);
                                    if (solverConfig.Verify(ModStatInfos))
                                    {
                                        solverConfig.LinkLevelBonus = SolverConfig.LinkLevelBonus;
                                        solverConfig.QualitiesV2 = SolverConfig.QualitiesV2;
                                        solverConfig.NumModules = SolverConfig.NumModules;
                                        solverConfig.ValueAllStats = SolverConfig.ValueAllStats;
                                        solverConfig.BruteForceAllModules = SolverConfig.BruteForceAllModules;
                                        solverConfig.ModuleTotalCutoff = SolverConfig.ModuleTotalCutoff;
                                        solverConfig.ScoreMode = SolverConfig.ScoreMode;
                                        solverConfig.ScoringModel = SolverConfig.ScoringModel;
                                        solverConfig.OrderBoostStrength = SolverConfig.OrderBoostStrength;
                                        solverConfig.LegendaryStatMultiplier = SolverConfig.LegendaryStatMultiplier;
                                        SolverConfig = solverConfig;
                                        // Re-link the saved settings to the new instance; otherwise later
                                        // edits (ScoreMode etc.) mutate a detached object and never persist.
                                        Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config = solverConfig;
                                    }
                                }
                                ImGui.SameLine();
                                if (ImGui.Button(AppStrings.GetLocalized("Module_Copy") + "##CopyPreset"))
                                {
                                    ImGui.SetClipboardText(CurrentPresetString);
                                }
                            });

                            /*
                            //if (!(Vector.IsHardwareAccelerated && Avx2.IsSupported))
                            {
                                AddSettingRow("Solver Mode:", () =>
                                {
                                    string[] solverNames = ["Legacy", "Fallback", "Normal", "NormalV2"];
                                    int selectedSolver = (int)Settings.Instance.WindowSettings.ModuleWindow.SolverMode;
                                    ImGui.SetNextItemWidth(300);
                                    ImGui.Combo("##SolverMode", ref selectedSolver, solverNames, solverNames.Length);
                                    Settings.Instance.WindowSettings.ModuleWindow.SolverMode = (SolverModes)selectedSolver;
                                });
                            }*/

                            AddSettingRow(AppStrings.GetLocalized("Module_Settings_IncludeAllStats"), () =>
                            {
                                var val = Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config.ValueAllStats;
                                ImGui.Checkbox("##ValueAllStats", ref val);
                                Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config.ValueAllStats = val;
                            });

                            AddSettingRow(AppStrings.GetLocalized("Module_Settings_NumModules"), () =>
                            {
                                ImGui.SetNextItemWidth(300);
                                // Disable the hover/active highlight so it doesn't paint over the slider.
                                var frameBg = ImGui.GetColorU32(ImGuiCol.FrameBg);
                                ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, frameBg);
                                ImGui.PushStyleColor(ImGuiCol.FrameBgActive, frameBg);
                                ImGui.SliderInt("##NumModules", ref SolverConfig.NumModules, 1, ModuleSet.MaxModules);
                                ImGui.PopStyleColor(2);
                            });

                            AddSettingRow(AppStrings.GetLocalized("Module_Settings_BruteForce"), () =>
                            {
                                var val = SolverConfig.BruteForceAllModules;
                                if (ImGui.Checkbox("##BruteForceAllModules", ref val))
                                {
                                    SolverConfig.BruteForceAllModules = val;
                                }
                                ModuleTooltip(AppStrings.GetLocalized("Module_Settings_BruteForce_Tooltip"));
                            });

                            AddSettingRow(AppStrings.GetLocalized("Module_Settings_ModuleTotalCutoff"), () =>
                            {
                                ImGui.SetNextItemWidth(300);
                                int cutoff = SolverConfig.ModuleTotalCutoff;
                                if (ImGui.InputInt("##ModuleTotalCutoff", ref cutoff, 1))
                                {
                                    SolverConfig.ModuleTotalCutoff = Math.Clamp(cutoff, 0, 60);
                                }
                                ModuleTooltip(AppStrings.GetLocalized("Module_Settings_ModuleTotalCutoff_Tooltip"));
                            });

                            AddSettingRow(AppStrings.GetLocalized("Module_Settings_ComputeBackend"), () =>
                            {
                                // User-selected backend. There is no automatic fallback: a GPU
                                // failure is reported in the results panel and the solve stops.
                                string[] backendNames = [AppStrings.GetLocalized("Module_Backend_Gpu"), AppStrings.GetLocalized("Module_Backend_Cpu")];
                                int selected = (int)Settings.Instance.WindowSettings.ModuleWindow.ComputeBackend;
                                ImGui.SetNextItemWidth(300);
                                if (ImGui.Combo("##ComputeBackend", ref selected, backendNames, backendNames.Length))
                                {
                                    Settings.Instance.WindowSettings.ModuleWindow.ComputeBackend = (ComputeBackend)Math.Clamp(selected, 0, 1);
                                }
                                ModuleTooltip(AppStrings.GetLocalized("Module_Settings_ComputeBackend_Tooltip"));

                                if (Settings.Instance.WindowSettings.ModuleWindow.ComputeBackend == ComputeBackend.Gpu)
                                {
                                    var adapter = Managers.ModuleOptimizer.GpuAdapterName;
                                    if (Managers.ModuleOptimizer.GpuUnavailable)
                                    {
                                        ImGui.PushStyleColor(ImGuiCol.Text, Colors.Red_Transparent);
                                        ImGui.TextUnformatted(AppStrings.GetLocalized("Module_Settings_GpuUnavailable"));
                                        ImGui.PopStyleColor();
                                    }
                                    else if (!string.IsNullOrEmpty(adapter))
                                    {
                                        ImGui.TextDisabled(adapter);
                                    }
                                    else
                                    {
                                        ImGui.TextDisabled(AppStrings.GetLocalized("Module_Settings_GpuPending"));
                                    }
                                }
                            });

                            AddSettingRow(AppStrings.GetLocalized("Module_Settings_ResultCache"), () =>
                            {
                                var modWin = Settings.Instance.WindowSettings.ModuleWindow;

                                var useCache = modWin.UseBruteForceCache;
                                if (ImGui.Checkbox("##UseBruteForceCache", ref useCache))
                                {
                                    modWin.UseBruteForceCache = useCache;
                                }
                                ModuleTooltip(AppStrings.GetLocalized("Module_Settings_ResultCache_Tooltip"));

                                ImGui.SameLine();
                                ImGui.BeginDisabled(!useCache);
                                ImGui.SetNextItemWidth(120);
                                int pct = modWin.CacheThresholdPct;
                                if (ImGui.InputInt("%##CacheThresholdPct", ref pct, 5))
                                {
                                    modWin.CacheThresholdPct = Math.Clamp(pct, 1, 90);
                                }
                                ModuleTooltip(AppStrings.GetLocalized("Module_Settings_CachePct_Tooltip"));
                                ImGui.EndDisabled();

                                var status = Managers.ModuleOptimizer.PoolCacheStatus;
                                if (status.HasValue)
                                {
                                    ImGui.TextDisabled(string.Format(AppStrings.GetLocalized("Module_Cache_Status"),
                                        status.Value.Entries.ToString("N0"), status.Value.BuiltAt.ToString("HH:mm:ss"), status.Value.EffectivePct));
                                    ImGui.SameLine();
                                    if (ImGui.SmallButton(AppStrings.GetLocalized("Module_Cache_Clear") + "##ClearPoolCache"))
                                    {
                                        Managers.ModuleOptimizer.ClearPoolCache();
                                    }
                                }
                                else
                                {
                                    ImGui.TextDisabled(AppStrings.GetLocalized("Module_Cache_None"));
                                }
                            });

                            AddSettingRow(AppStrings.GetLocalized("Module_Settings_ScoreMode"), () =>
                            {
                                string[] scoreNames = [AppStrings.GetLocalized("Module_ScoreMode_ZScore"), AppStrings.GetLocalized("Module_ScoreMode_CombatPower")];
                                int selected = (int)SolverConfig.ScoreMode;
                                ImGui.SetNextItemWidth(300);
                                if (ImGui.Combo("##ScoreMode", ref selected, scoreNames, scoreNames.Length))
                                {
                                    SolverConfig.ScoreMode = (ScoreMode)Math.Clamp(selected, 0, 1);
                                }
                            });

                            // Heuristic weights only affect the ZScore mode.
                            ImGui.BeginDisabled(SolverConfig.ScoreMode == ScoreMode.CombatPower);

                            AddSettingRow(AppStrings.GetLocalized("Module_Settings_ScoringModel"), () =>
                            {
                                string[] modelNames = [AppStrings.GetLocalized("Module_ScoringModel_Enhanced"), AppStrings.GetLocalized("Module_ScoringModel_Original")];
                                int selected = (int)SolverConfig.ScoringModel;
                                ImGui.SetNextItemWidth(300);
                                if (ImGui.Combo("##ScoringModel", ref selected, modelNames, modelNames.Length))
                                {
                                    SolverConfig.ScoringModel = (ScoringModel)Math.Clamp(selected, 0, 1);
                                }
                                ModuleTooltip(AppStrings.GetLocalized("Module_Settings_ScoringModel_Tooltip"));
                            });

                            AddSettingRow(AppStrings.GetLocalized("Module_Settings_OrderBoostStrength"), () =>
                            {
                                ImGui.SetNextItemWidth(300);
                                float strength = SolverConfig.OrderBoostStrength;
                                if (ImGui.SliderFloat("##OrderBoostStrength", ref strength, 0f, 3f, "%.2f"))
                                {
                                    SolverConfig.OrderBoostStrength = strength;
                                }
                                ModuleTooltip(AppStrings.GetLocalized("Module_Settings_OrderBoostStrength_Tooltip"));
                            });

                            AddSettingRow(AppStrings.GetLocalized("Module_Settings_LegendaryMul"), () =>
                            {
                                ImGui.SetNextItemWidth(300);
                                float mul = SolverConfig.LegendaryStatMultiplier;
                                if (ImGui.SliderFloat("##LegendaryMul", ref mul, 1f, 4f, "%.2f"))
                                {
                                    SolverConfig.LegendaryStatMultiplier = mul;
                                }
                                ModuleTooltip(AppStrings.GetLocalized("Module_Settings_LegendaryMul_Tooltip"));
                            });

                            ImGui.EndDisabled();

                            ImGui.EndTable();
                        }

                        if (ImGui.CollapsingHeader(AppStrings.GetLocalized("Module_Settings_LinkLevelBoosts")))
                        {
                            var linkLevelSettingsWidth = 300;
                            //ImGui.PushClipRect(ImGui.GetCursorScreenPos(), ImGui.GetCursorScreenPos() + new Vector2(linkLevelSettingsWidth, 100000), false);
                            if (ImGui.BeginTable("LinkLevelBoosts", 2))
                            {
                                ImGui.TableSetupColumn("1", ImGuiTableColumnFlags.WidthFixed, 300f);
                                ImGui.TableSetupColumn("2", ImGuiTableColumnFlags.WidthStretch);

                                ImGui.TableNextColumn();
                                if (ImGui.BeginTable("LinkLevelBoostsValues", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.BordersInnerH))
                                {
                                    ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 80f);
                                    ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);


                                    if (Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config.LinkLevelBonus.Length != 6)
                                    {
                                        Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config.LinkLevelBonus = new byte[6];
                                    }

                                    for (int i = 0; i < 6; i++)
                                    {
                                        ImGui.TableNextColumn();
                                        ImGui.TextUnformatted(string.Format(AppStrings.GetLocalized("Module_Settings_Level"), i + 1));
                                        ImGui.TableNextColumn();
                                        int val = Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config.LinkLevelBonus[i];
                                        //ImGui.SetNextItemWidth(100);
                                        ImGui.InputInt($"##LinkLevelBoost{i}", ref val, 0);
                                        val = Math.Clamp(val, 0, 250);
                                        Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config.LinkLevelBonus[i] = (byte)val;
                                    }

                                    ImGui.EndTable();

                                    if (ImGui.Button(AppStrings.GetLocalized("Module_Settings_ResetDefaults"), new Vector2(-1, 0)))
                                    {
                                        Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config.LinkLevelBonus = SolverConfig.DefaultLinkLevels;
                                    }
                                }
                                ImGui.TableNextColumn();
                                ImGui.SeparatorText(AppStrings.GetLocalized("Module_Settings_LinkBoostDesc_Title"));
                                ImGui.TextWrapped(string.Format(AppStrings.GetLocalized("Module_Settings_LinkBoostDesc"),
                                    Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config.LinkLevelBonus[4]));

                                ImGui.EndTable();
                            }
                            //ImGui.PopClipRect();
                        }

                        ImGui.SetCursorPos(ImGui.GetWindowSize() - new Vector2(300, 62));
                        //ImGui.SeparatorText("Debug Info");
                        ImGui.TextUnformatted($"HW Accel: {Vector.IsHardwareAccelerated}");
                        ImGui.SetCursorPos(ImGui.GetWindowSize() - new Vector2(300, 42));
                        ImGui.TextUnformatted($"AVX2: {Avx2.IsSupported}");
                        ImGui.SetCursorPos(ImGui.GetWindowSize() - new Vector2(300, 22));
                        ImGui.TextUnformatted($"Size: {Vector<byte>.Count}");

                        var tina = ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "Missing.png"));
                        if (tina.HasValue)
                        {
                            var size = new Vector2(200, 200);
                            var start = ImGui.GetWindowPos() + ImGui.GetWindowSize() - size - new Vector2(0, -5);
                            ImGui.GetForegroundDrawList().AddImage(tina.Value, start, start + size);
                        }

                        ImGui.EndTabItem();
                    }

#if DEBUG
                    if (ImGui.BeginTabItem("Debug"))
                    {
                        DrawDebug();
                        ImGui.EndTabItem();
                    }
#endif

                    ImGui.EndTabBar();
                }

                ImGui.EndDisabled();
            }

            if (!IsOpen && ShouldTrackOpenState && !IsCalculating)
            {
                Settings.Instance.WindowSettings.ModuleWindow.WindowPosition = ImGui.GetWindowPos();

                ShouldTrackOpenState = false;

                ResultsPlayerModData = new PlayerModDataSave();
                BestModResults = null;
                FilteredModules.Clear();

                GC.Collect();
            }

            ImGui.End();
        }

        private static void AddSettingRow(string label, Action valueWidget)
        {
            // Grow the label column to fit the widest label so nothing gets clipped.
            // Applied on the next frame via TableSetupColumn (monotonic, so no flicker).
            float needed = ImGui.CalcTextSize(label).X + 16f;
            if (needed > SettingsLabelColWidth)
            {
                SettingsLabelColWidth = needed;
            }

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(label);
            ImGui.TableNextColumn();
            //ImGui.PushItemWidth(-1);
            valueWidget?.Invoke();
            //ImGui.PopItemWidth();
        }

        private static void DrawSolverTab(Vector2 windowSize, float leftWidth)
        {
            var contentRegion = ImGui.GetContentRegionAvail();
            ImGui.SetCursorPosY(58);

            var clipStart = ImGui.GetCursorScreenPos();
            ImGui.PushClipRect(clipStart, clipStart + new Vector2(leftWidth, 20), true);
            ImGui.SeparatorText(AppStrings.GetLocalized("Module_Section_Config"));
            ImGui.PopClipRect();
            ImGui.SetCursorPosY(85);

            var configChanged = false;
            ImGui.BeginChild("LeftSection", new Vector2(leftWidth, contentRegion.Y - 55), ImGuiChildFlags.Borders);
            ImGui.SeparatorText(AppStrings.GetLocalized("Module_Section_Quality"));

            // One quality per line with the checkboxes aligned to a column past the widest
            // label: the old single-line layout clipped the wider localized labels (e.g. the
            // Japanese "エクセレント") and their checkboxes off the edge of the left panel.
            var basicLabel = AppStrings.GetLocalized("Module_Quality_Basic");
            var advancedLabel = AppStrings.GetLocalized("Module_Quality_Advanced");
            var excellentLabel = AppStrings.GetLocalized("Module_Quality_Excellent");
            float qualityCheckX = Math.Max(ImGui.CalcTextSize(basicLabel).X,
                Math.Max(ImGui.CalcTextSize(advancedLabel).X, ImGui.CalcTextSize(excellentLabel).X)) + 16;

            bool basicQuality = SolverConfig.QualitiesV2.TryGetValue(2, out var temp) ? temp : false;
            ImGui.AlignTextToFramePadding();
            ImGui.PushStyleColor(ImGuiCol.Text, Colors.QualityBasic);
            ImGui.TextUnformatted(basicLabel);
            ImGui.PopStyleColor();
            ImGui.SameLine(qualityCheckX);
            if (ImGui.Checkbox("##Basic", ref basicQuality))
            {
                SolverConfig.QualitiesV2[2] = basicQuality;
            }

            bool advancedQuality = SolverConfig.QualitiesV2.TryGetValue(3, out var temp2) ? temp2 : false;
            ImGui.AlignTextToFramePadding();
            ImGui.PushStyleColor(ImGuiCol.Text, Colors.QualityAdvanced);
            ImGui.TextUnformatted(advancedLabel);
            ImGui.PopStyleColor();
            ImGui.SameLine(qualityCheckX);
            if (ImGui.Checkbox("##Advanced", ref advancedQuality))
            {
                SolverConfig.QualitiesV2[3] = advancedQuality;
            }

            bool excellentQuality = SolverConfig.QualitiesV2.TryGetValue(4, out var temp3) ? temp3 : false;
            ImGui.AlignTextToFramePadding();
            ImGui.PushStyleColor(ImGuiCol.Text, Colors.QualityExcellent);
            ImGui.TextUnformatted(excellentLabel);
            ImGui.PopStyleColor();
            ImGui.SameLine(qualityCheckX);
            if (ImGui.Checkbox("##Excellent", ref excellentQuality))
            {
                SolverConfig.QualitiesV2[4] = excellentQuality;
            }
            ImGui.Spacing();

            ImGui.SeparatorText(AppStrings.GetLocalized("Module_Section_StatPriority"));
            ImGui.Spacing();

            if (SolverConfig.BruteForceAllModules)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Colors.LightBlue_Transparent);
                ImGui.TextWrapped(AppStrings.GetLocalized("Module_BruteForce_PriorityNote"));
                ImGui.PopStyleColor();
                ImGui.Spacing();
            }

            int idToRemove = -1;
            for (int i = 0; i < SolverConfig.StatPriorities.Count; i++)
            {
                var result = DrawStatFilter(i);
                if (result.Item1)
                {
                    idToRemove = i;
                    configChanged = true;
                }

                if (result.Item2)
                {
                    configChanged = true;
                }
            }

            if (idToRemove != -1)
            {
                SolverConfig.StatPriorities.RemoveAt(idToRemove);
            }

            ImGui.EndChild();

            var pos = ImGui.GetCursorPos();
            ImGui.SetNextItemWidth(leftWidth - 55);
            if (ImGui.BeginCombo("##Stat", $"       {PendingStatToAdd.Name}"))
            {
                foreach (var item in ModStatInfos.AsValueEnumerable().Where(x => !SolverConfig.StatPriorities.Any(y => y.Id == x.Key)))
                {
                    //ImGui.BeginGroup();
                    ImGui.Image(item.Value.IconRef.Value, new Vector2(22, 22));
                    ImGui.SameLine();
                    ImGui.AlignTextToFramePadding();
                    if (ImGui.Selectable($"{item.Value.Name}##StatToSelect", item.Key == PendingStatToAdd.StatId))
                    {
                        PendingStatToAdd = item.Value;
                    }
                    //ImGui.EndGroup();
                }
                ImGui.EndCombo();
            }

            ImGui.SetCursorPos(pos + new Vector2(2, 2));
            ImGui.Image(PendingStatToAdd.IconRef.Value, new Vector2(22, 22));

            ImGui.SetCursorPos(pos + new Vector2(leftWidth - 50, 0));
            var isAlreadyAdded = SolverConfig.StatPriorities.Any(x => x.Id == PendingStatToAdd.StatId);
            ImGui.BeginDisabled(isAlreadyAdded || SolverConfig.StatPriorities.Count >= 12);
            if (ImGui.Button(AppStrings.GetLocalized("Module_Add") + "##AddStat", new Vector2(50, 0)))
            {
                SolverConfig.StatPriorities.Add(new StatPrio()
                {
                    Id = PendingStatToAdd.StatId,
                    MinLevel = 0
                });

                configChanged = true;
            }

            if (isAlreadyAdded)
            {
                ModuleTooltip(AppStrings.GetLocalized("Module_StatAlreadyAdded"));
            }
            ImGui.EndDisabled();

            if (configChanged)
            {
                CurrentPresetString = SolverConfig.SaveToString();
            }

            ImGui.SetCursorPosX(leftWidth + 8);
            ImGui.SetCursorPosY(58);

            ImGui.SeparatorText(AppStrings.GetLocalized("Module_Section_Results"));
            ImGui.SetCursorPosX(leftWidth + 8);
            ImGui.SetCursorPosY(85);

            ImGui.BeginChild("RightSection", new Vector2(windowSize.X - leftWidth - 5, contentRegion.Y - 55), ImGuiChildFlags.Borders);
            ImGui.Spacing();

            if (LastSolveUsedCpuFallback && BestModResults != null && !IsCalculating)
            {
                ImGui.TextDisabled(AppStrings.GetLocalized("Module_Result_CpuFallback"));
            }

            if (LastSolveFromCache && BestModResults != null && !IsCalculating)
            {
                ImGui.TextDisabled(AppStrings.GetLocalized(
                    LastSolveCacheExact ? "Module_Result_FromCache_Exact" : "Module_Result_FromCache_Approx"));
            }

            if (LastSolveError != null && !IsCalculating)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Colors.Red_Transparent);
                ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], 22f);
                ImGui.TextWrapped(AppStrings.GetLocalized("Module_Error_SolveFailed"));
                ImGui.PopFont();
                ImGui.TextWrapped(LastSolveError);
                ImGui.PopStyleColor();
                ImGui.Spacing();
                ImGui.TextWrapped(AppStrings.GetLocalized("Module_Error_SolveFailed_Hint"));
            }
            else if (BestModResults?.Count > 0)
            {
                lock (ResultsPlayerModData)
                {
                    lock (BestModResults)
                    {
                        lock (FilteredModules)
                        {
                            bool[] resultsOpenStates = new bool[BestModResults.Count];
                            for (int i = 0; i < BestModResults.Count; i++)
                            {
                                resultsOpenStates[i] = i <= 1;

                                ModComboResult modsResult = BestModResults[i];
                                var resultHeader = string.Format(AppStrings.GetLocalized("Module_Result"), i + 1, modsResult.CombatScore.ToString("#,##"), modsResult.Score.ToString("#,##")) + $"###ModResult{i}";
                                if (ImGui.CollapsingHeader(resultHeader, (resultsOpenStates[i] ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None)))
                                {
                                    var perLine = 3;
                                    var statPos = ImGui.GetCursorPos();
                                    for (int i1 = 0; i1 < modsResult.Stats.Length; i1++)
                                    {
                                        PowerCore stat = modsResult.Stats[i1];
                                        ImGui.SetCursorPos(statPos + new Vector2(i1 * 100, 0));
                                        var isAPrioStat = SolverConfig.StatPriorities.FirstOrDefault(x => x.Id == stat.Id) != null;
                                        DrawModuleStat(stat.Id, stat.Value, isAPrioStat);
                                    }

                                    bool needsToNewLine = false;
                                    bool isCtrlPressed = ImGui.IsKeyDown(ImGuiKey.LeftCtrl);
                                    var mods = modsResult.ModuleSet.Mods;
                                    for (int i1 = 0; i1 < mods.Length; i1++)
                                    {
                                        if (mods[i1] == -1)
                                        {
                                            break;
                                        }

                                        needsToNewLine = false;
                                        var modId = FilteredModules[mods[i1]];
                                        var modItem = ResultsPlayerModData.ModulesPackage.Items[modId];
                                        DrawModule(ResultsPlayerModData, modId, modItem, isCtrlPressed);
                                        if ((i1 % 2) == 0)
                                        {
                                            ImGui.SameLine();
                                            ImGui.Dummy(new Vector2(20, 0));
                                            ImGui.SameLine();
                                            needsToNewLine = true;
                                        }
                                    }

                                    if (needsToNewLine)
                                    {
                                        ImGui.NewLine();
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else if (!IsCalculating && BestModResults != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Colors.Red_Transparent);
                ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], 22f);
                ImGui.TextWrapped(AppStrings.GetLocalized("Module_NoValidCombo"));
                ImGui.PopFont();
                ImGui.PopStyleColor();

                var sad = ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "Tired.png"));
                if (sad != null)
                {
                    ImGui.Image(sad.Value, new Vector2(200, 200));
                }
            }

            ImGui.EndChild();
            ImGui.SetCursorPosX(leftWidth + 8);
            RefreshComboCountEstimate();
            var calcLabel = string.Format(AppStrings.GetLocalized("Module_CalculateButton"), SolverConfig.NumModules);
            if (ComboCountText.Length > 0)
            {
                calcLabel += " " + ComboCountText;
            }
            if (ImGui.Button(calcLabel + "###CalculateButton", new Vector2(contentRegion.X - leftWidth, 0)))
            {
                // Settings normally persist only on clean exit; save now so the solver config
                // (score mode etc.) survives even if the app dies mid-solve (e.g. a GPU reset).
                Settings.Save();

                ModuleCalcCancelTokenSource = new CancellationTokenSource();
                ModuleCalcTask = Task.Factory.StartNew(() =>
                {
                    ShouldBlockMainUI = true;
                    ModuleCalcStartTime = DateTime.Now;
                    CalculateBestModules();
                    ShouldBlockMainUI = false;
                }, ModuleCalcCancelTokenSource.Token);
            }
            ModuleTooltip(AppStrings.GetLocalized("Module_CalculateButton_Tooltip"));
        }

        // Recomputes the combination-count estimate shown on the Calculate button, but only
        // when a lightweight signature of its inputs changes (so we don't filter the whole
        // inventory every frame). Only the GPU (exhaustive) backend enumerates every
        // combination; the CPU beam search does not, so a "total combinations" figure would
        // be misleading there and is hidden.
        private static void RefreshComboCountEstimate()
        {
            var backend = Settings.Instance.WindowSettings.ModuleWindow.ComputeBackend;
            var cfg = SolverConfig;
            var lang = Settings.Instance.Language ?? "en";
            var qualities = string.Join(',', cfg.QualitiesV2.Where(q => q.Value).Select(q => q.Key));
            var prios = string.Join(',', cfg.StatPriorities.Select(p => p.Id));
            var sig = $"{backend}|{cfg.NumModules}|{cfg.BruteForceAllModules}|{cfg.ModuleTotalCutoff}|{qualities}|{prios}|{NumTotalModules}|{lang}";
            if (sig == ComboCountSig)
            {
                return;
            }
            ComboCountSig = sig;

            if (backend != ComputeBackend.Gpu || PlayerModData?.ModulesPackage?.Items == null || PlayerModData.Mod == null)
            {
                ComboCountText = "";
                return;
            }

            double combos = Managers.ModuleOptimizer.EstimateComboCount(cfg, PlayerModData);
            ComboCountText = FormatComboCount(combos, lang);
        }

        // Compact, locale-aware combination count. CJK locales group by 4 digits (万/億/兆);
        // others use SI suffixes (K/M/B/T). Counts too large to state usefully (e.g. 10 modules
        // over a big inventory) collapse to a localized "huge" warning.
        private static string FormatComboCount(double c, string lang)
        {
            if (c < 1d)
            {
                return "";
            }

            bool cjk = lang.StartsWith("ja") || lang.StartsWith("zh");
            string num;
            if (cjk)
            {
                if (c >= 1e16) return AppStrings.GetLocalized("Module_ComboCount_Huge");
                if (c >= 1e12) num = (c / 1e12).ToString("0.#") + "兆";
                else if (c >= 1e8) num = (c / 1e8).ToString("0.#") + "億";
                else if (c >= 1e4) num = (c / 1e4).ToString("0.#") + "万";
                else num = ((long)c).ToString("N0");
            }
            else
            {
                if (c >= 1e15) return AppStrings.GetLocalized("Module_ComboCount_Huge");
                if (c >= 1e9) num = (c / 1e9).ToString("0.#") + "B";
                else if (c >= 1e6) num = (c / 1e6).ToString("0.#") + "M";
                else if (c >= 1e3) num = (c / 1e3).ToString("0.#") + "K";
                else num = ((long)c).ToString("N0");
            }

            return string.Format(AppStrings.GetLocalized("Module_ComboCount"), num);
        }

        // Tooltip that wraps long lines at a fixed width instead of letting the tooltip
        // window stretch arbitrarily wide (the default SetItemTooltip does not wrap, so a
        // long unbroken localized string produced an oversized box). Used for every Module
        // Optimizer tooltip.
        private static void ModuleTooltip(string text)
        {
            if (ImGui.BeginItemTooltip())
            {
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32f);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }

        private static (bool, bool) DrawStatFilter(int i)
        {
            var pos = ImGui.GetCursorPos();
            var statInfo = SolverConfig.StatPriorities[i];
            bool shouldRemove = false;
            bool wasChanged = false;

            ImGui.SetCursorPos(pos + new Vector2(0, 2));
            ImGui.PushFont(HelperMethods.Fonts["FASIcons"], 13.0f);
            ImGui.BeginDisabled(i == 0);
            if (ImGui.Button($"{FASIcons.AngleUp}##{i}", new Vector2(32, 16)))
            {
                var toMoveTo = i - 1;
                var tempStat = SolverConfig.StatPriorities[toMoveTo];
                SolverConfig.StatPriorities[toMoveTo] = SolverConfig.StatPriorities[i];
                SolverConfig.StatPriorities[i] = tempStat;
                wasChanged = true;
            }
            ImGui.EndDisabled();

            ImGui.BeginDisabled(i == SolverConfig.StatPriorities.Count() - 1);
            if (ImGui.Button($"{FASIcons.AngleDown}##{i}", new Vector2(32, 16)))
            {
                var toMoveTo = i + 1;
                var tempStat = SolverConfig.StatPriorities[toMoveTo];
                SolverConfig.StatPriorities[toMoveTo] = SolverConfig.StatPriorities[i];
                SolverConfig.StatPriorities[i] = tempStat;
                wasChanged = true;
            }
            ImGui.EndDisabled();
            ImGui.PopFont();

            var stat = ModStatInfos[SolverConfig.StatPriorities[i].Id];
            ImGui.SetCursorPos(pos + new Vector2(40, 4));
            ImGui.Image(stat.IconRef.Value, new Vector2(32, 32));

            ImGui.SetCursorPos(pos + new Vector2(80, 9));
            ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], ImGui.GetFontSize());
            ImGui.TextUnformatted(stat.Name);
            ImGui.PopFont();

            var availSize = ImGui.GetContentRegionAvail();
            ImGui.SetCursorPos(pos + new Vector2(availSize.X - 90, 5));
            ImGui.SetNextItemWidth(40);

            /*
            if (ImGui.InputInt($"##MinLevel{i}", ref SolverConfig.StatPriorities[i].MinLevel, 0, ImGuiInputTextFlags.CharsDecimal))
            {
                wasChanged = true;
            }
            ModuleTooltip("The minimum Link value needed for this stat to be considered.\nLeave 0 to use any Link.");
            */

            if (ImGui.InputInt($"##ReqLevel{i}", ref SolverConfig.StatPriorities[i].ReqLevel, 0, ImGuiInputTextFlags.CharsDecimal))
            {
                // >= 0: lower-bound requirement. -1..-6: upper-bound cap (see StatPrio).
                SolverConfig.StatPriorities[i].ReqLevel = Math.Clamp(SolverConfig.StatPriorities[i].ReqLevel, -6, 20);
                wasChanged = true;
            }
            ModuleTooltip(AppStrings.GetLocalized("Module_StatFilter_ReqLevel_Tooltip"));

            ImGui.SetCursorPos(pos + new Vector2(availSize.X - 50, 5));
            ImGui.Dummy(new Vector2(-4, 0));
            ImGui.SameLine();
            // A negative ReqLevel is a cap; the A/E (Atleast/Exactly) mode has no meaning
            // there, so show a cap indicator and disable the toggle.
            bool capMode = SolverConfig.StatPriorities[i].ReqLevel < 0;
            bool isAtleastMode = SolverConfig.StatPriorities[i].StatMode == StatMode.Atleast;
            ImGui.BeginDisabled(capMode);
            // Use only Latin-1 glyphs here: the default Segoe UI atlas has no U+2264.
            string modeLabel = capMode
                ? (SolverConfig.StatPriorities[i].ReqLevel == -6 ? "×" : "<")
                : (isAtleastMode ? "A" : "E");
            if (ImGui.Button($"{modeLabel}##StatMode{i}"))
            {
                SolverConfig.StatPriorities[i].StatMode = isAtleastMode ? StatMode.Exactly : StatMode.Atleast;
                wasChanged = true;
            }
            ImGui.EndDisabled();
            if (!capMode)
            {
                ModuleTooltip(isAtleastMode ?
                    AppStrings.GetLocalized("Module_StatFilter_Atleast_Tooltip") :
                    AppStrings.GetLocalized("Module_StatFilter_Exactly_Tooltip"));
            }

            ImGui.SetCursorPos(pos + new Vector2(availSize.X - 25, 0));
            ImGui.PushFont(HelperMethods.Fonts["FASIcons"], 13.0f);
            ImGui.PushStyleColor(ImGuiCol.Button, Colors.DarkRed);
            if (ImGui.Button($"{FASIcons.TrashCan}##{i}", new Vector2(25, 40)))
            {
                shouldRemove = true;
            }
            ImGui.PopStyleColor();
            ImGui.PopFont();

            ImGui.SetCursorPos(pos + new Vector2(0, 40));
            ImGui.Separator();

            return (shouldRemove, wasChanged);
        }

        private static void DrawModuleInv()
        {
            var contentSize = ImGui.GetContentRegionAvail();
            ImGui.BeginChild("##ModuleInv", new Vector2(-1, contentSize.Y - 25));
            var numPerLine = Math.Floor(ImGui.GetContentRegionAvail().X / MOD_DISPLAY_SIZE.X);
            int i = 0;
            bool isCtrlPressed = ImGui.IsKeyDown(ImGuiKey.LeftCtrl);
            foreach (var item in PlayerModData.ModulesPackage?.Items ?? [])
            {
                DrawModule(PlayerModData, item.Key, item.Value, isCtrlPressed);
                if ((++i % numPerLine) != 0)
                {
                    ImGui.SameLine();
                }
            }
            ImGui.EndChild();

            ImGui.Separator();
            ImGui.TextUnformatted(string.Format(AppStrings.GetLocalized("Module_Inventory_Counts"), NumTotalModules, NumAttackModules, NumSupportModules, NumGuardModules));
        }

        static Vector2 MOD_ICON_SIZE = new Vector2(80, 80);
        static Vector2 MOD_DISPLAY_SIZE = new Vector2(410, 105);
        public static void DrawModule(PlayerModDataSave modInv, long id, Zproto.Item item, bool showId = false)
        {
            var modTypeData = HelperMethods.DataTables.Modules.Data[item.ConfigId];
            var modInfo = modInv.Mod.ModInfos[id];
            var startPos = ImGui.GetCursorPos();
            ImGui.PushClipRect(ImGui.GetCursorScreenPos(), ImGui.GetCursorScreenPos() + MOD_DISPLAY_SIZE, true);
            ImGui.BeginGroup();
            var qualityBg = GetItemQualityBg(item.Quality);
            var modIcon = GetModuleIcon(item.ConfigId);

            ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], ImGui.GetFontSize());
            ImGui.PushStyleVarX(ImGuiStyleVar.SeparatorTextPadding, 80);
            ImGui.SeparatorText(modTypeData.Name);
            ImGui.PopStyleVar();
            ImGui.PopFont();

            var pos = ImGui.GetCursorPos();
            ImGui.Image(qualityBg, MOD_ICON_SIZE);
            ImGui.SetCursorPos(pos);
            if (modIcon != null)
            {
                ImGui.Image(modIcon.Value, MOD_ICON_SIZE);
            }
            else
            {
                ImGui.Dummy(MOD_ICON_SIZE);
            }

            if (showId)
            {
                ImGui.SetCursorPos(pos);
                ImGui.TextUnformatted($"ID: {id}");
                ImGui.SetCursorPos(pos + new Vector2(0, MOD_ICON_SIZE.Y));
            }

            pos = ImGui.GetCursorPos();
            ImGui.SetCursorPos(pos + new Vector2(100, -MOD_ICON_SIZE.Y));
            var numStats = item.ModNewAttr.ModParts.Count();
            for (int i = 0; i < numStats; i++)
            {
                var partId = item.ModNewAttr.ModParts[i];
                var level = modInfo.InitLinkNums[i];
                ImGui.SetCursorPos(pos + new Vector2(90 + (i * 102), -(MOD_ICON_SIZE.Y + 4)));
                DrawModuleStat(partId, level);
            }

            ImGui.EndGroup();
            ImGui.PopClipRect();

            ImGui.SetCursorPos(startPos);
            ImGui.Dummy(MOD_DISPLAY_SIZE);
        }

        private static void DrawModuleStat(int partId, int level, bool underline = false)
        {
            Vector2 iconSize = new Vector2(32, 32);
            Vector2 size = new Vector2(100, 100);

            if (ModStatInfos.TryGetValue(partId, out var statInfo))
            {
                var pos = ImGui.GetCursorPos();
                var titleWidth = ImGui.CalcTextSize(statInfo.Name).X;
                var icon = statInfo.IconRef.Value;
                ImGui.SetCursorPos(pos + new Vector2((size.X / 2) - (titleWidth / 2), 0));
                ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], 17);
                ImGui.TextUnformatted(statInfo.Name);
                ImGui.SetCursorPos(pos + new Vector2((size.X / 2) - (iconSize.X / 2), 25));
                ImGui.Image(icon, iconSize);
                ImGui.SetCursorPos(pos + new Vector2((size.X / 2) - 10, 60));
                ImGui.TextUnformatted($"+{level}");
                ImGui.PopFont();

                if (underline)
                {
                    var textWidth = ImGui.CalcTextSize(statInfo.Name);
                    var freeSpace = (size.X - textWidth.X) / 2 - 5;
                    var underlinePos = ImGui.GetWindowPos() - new Vector2(0, ImGui.GetScrollY()) + pos + new Vector2(0, 18);
                    uint col = ImGui.ColorConvertFloat4ToU32(Colors.LightBlue_Transparent);
                    ImGui.GetWindowDrawList().AddLine(underlinePos + new Vector2(freeSpace, 0), underlinePos + new Vector2(size.X - freeSpace, 0), col, 2);
                }
            }
        }

        public static void DrawBoxOutline(Vector2 position, float width, float height, Vector4 color, float thickness = 1.0f, float rounding = 0.0f)
        {
            Vector2 min = ImGui.GetWindowPos() + position;
            Vector2 max = new(min.X + width, min.Y + height);

            uint col = ImGui.ColorConvertFloat4ToU32(color);

            ImGui.GetWindowDrawList().AddRect(min, max, col, rounding, ImDrawFlags.None, thickness);
        }

        private static bool CheckAndDrawNoModulesBanner()
        {
            if (PlayerModData == null || PlayerModData.ModulesPackage?.Items == null || PlayerModData.Mod == null)
            {
                DrawBanner(AppStrings.GetLocalized("Module_Banner_LoadData"), 0xFFAD5E15, "Looking.png");
                return true;
            }

            return false;
        }

        static float BannerImgPulseTimer = 0f;
        private static void DrawBanner(string msg, uint bgColor, string img = null, bool animate = false, Action<ImDrawListPtr, Vector2, Vector2, float> customDraw = null)
        {
            float bannerheight = 200;
            var pos = ImGui.GetCursorScreenPos();
            var size = ImGui.GetContentRegionAvail();
            var start = pos + new Vector2(0, (size.Y / 2) - (bannerheight / 2));
            var drawList = ImGui.GetForegroundDrawList();

            drawList.AddRectFilled(
                start,
                start + new Vector2(size.X, bannerheight),
                bgColor
            );

            ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], 30.0f);
            var txtSize = ImGui.CalcTextSize(msg);
            var txtStartPos = start + new Vector2(size.X / 2 - txtSize.X / 2, (bannerheight / 2) - (txtSize.Y / 2));
            drawList.AddText(txtStartPos, ImGui.ColorConvertFloat4ToU32(Colors.White), msg);
            ImGui.PopFont();

            if (customDraw != null)
            {
                customDraw(drawList, txtStartPos, txtSize, bannerheight);
            }

            var imgRef = ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, img));
            if (imgRef.HasValue)
            {
                var offset = new Vector2(5, 5);
                if (animate)
                {
                    BannerImgPulseTimer += ImGui.GetIO().DeltaTime;
                    Vector2 pulseAmount = new Vector2(5, 5);
                    float pulseSpeed = 1;
                    offset = new Vector2(
                        pulseAmount.X * MathF.Sin(BannerImgPulseTimer * MathF.PI * pulseSpeed),
                        pulseAmount.Y * MathF.Sin(BannerImgPulseTimer * MathF.PI * pulseSpeed)
                    );
                }

                var imgSize = new Vector2(bannerheight, bannerheight) + offset;
                var imgStart = txtStartPos - new Vector2(imgSize.X + 40, (imgSize.Y / 2) - txtSize.Y / 2);
                drawList.AddImage(imgRef.Value, imgStart, imgStart + imgSize);
            }
        }

#if DEBUG
        private static void DrawDebug()
        {
            if (ImGui.Button("Reload Inventory"))
            {
                LoadSavedModData(ModSavePath);
            }

            if (ImGui.CollapsingHeader("Solver Fallback Conditions (legacy)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                // Debug-only: restores the legacy automatic-fallback behavior that release
                // builds no longer have (GPU failures stop the solve and show an error).
                var allowFallback = Managers.ModuleOptimizer.DebugAllowCpuFallback;
                if (ImGui.Checkbox("Auto CPU (beam) fallback on GPU failure", ref allowFallback))
                {
                    Managers.ModuleOptimizer.DebugAllowCpuFallback = allowFallback;
                }

                int budgetMillions = (int)(Managers.ModuleOptimizer.DebugMaxGpuCombos / 1_000_000);
                ImGui.SetNextItemWidth(200);
                if (ImGui.InputInt("GPU combo budget (millions, 0 = unlimited)", ref budgetMillions, 10))
                {
                    Managers.ModuleOptimizer.DebugMaxGpuCombos = Math.Max(0, budgetMillions) * 1_000_000L;
                }
                ImGui.TextDisabled("Above the budget the GPU solve throws NotSupportedException (legacy trigger was 50M).");
            }

            if (ImGui.CollapsingHeader("Presets", ImGuiTreeNodeFlags.DefaultOpen))
            {
                DrawDebugPreset("Dmg Stack 20 E, Crit 15 A",
                    "ZMO:2104-1-20,1409-0-15|");
                DrawDebugPreset("Crit 20 A, Atk Spd 15 E",
                    "ZMO:1409-0-20,1408-1-15|");
                DrawDebugPreset("Frostbeam",
                    "ZMO:2104-0-0,2404-0-0,1112-0-0,1409-0-0,1114-0-0,1407-0-0|", true);
            }

            if (ImGui.CollapsingHeader("Module Inventory", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (ImGui.BeginTable("ModuleInventory", 2))
                {
                    foreach (var testInv in DebugPlayerModInventories)
                    {
                        ImGui.TableNextColumn();
                        DrawDebugLoadout(testInv.Key, testInv.Value);
                    }


                    ImGui.EndTable();
                }
            }
        }

        private static void DrawDebugPreset(string name, string presetCode, bool newLine = false)
        {
            ImGui.PushID(presetCode);

            if (ImGui.Button(name))
            {
                var solverConfig = new SolverConfig();
                solverConfig.FromString(presetCode);
                if (solverConfig.Verify(ModStatInfos))
                {
                    SolverConfig = solverConfig;
                    // Keep the saved settings pointing at the active config (see Apply preset).
                    Settings.Instance.WindowSettings.ModuleWindow.LastUsedPreset.Config = solverConfig;
                }
            }

            ImGui.PopID();

            if (!newLine)
            {
                ImGui.SameLine();
            }
        }

        private static void DrawDebugLoadout(string name, PlayerModDataSave modInv)
        {
            ImGui.PushID(name);
            ImGui.SeparatorText(name);
            if (ImGui.CollapsingHeader("Modules"))
            {
                foreach (var mod in modInv.ModulesPackage.Items)
                {
                    DrawModule(modInv, mod.Key, mod.Value);
                }
            }

            var width = ImGui.GetContentRegionAvail().X / 3;
            if (ImGui.Button("Test Solve", new Vector2(width, 0)))
            {
                CalculateBestModules(modInv);
            }

            ImGui.SameLine();

            if (ImGui.Button("Set As Inventory", new Vector2(width, 0)))
            {
                PlayerModData = modInv;
            }

            ImGui.SameLine();

            if (ImGui.Button("Add To Inventory", new Vector2(width, 0)))
            {
                foreach (var mod in modInv.ModulesPackage.Items)
                {
                    PlayerModData.ModulesPackage.Items.TryAdd(mod.Key, mod.Value);
                }

                foreach (var modInfo in modInv.Mod.ModInfos)
                {
                    PlayerModData.Mod.ModInfos.TryAdd(modInfo.Key, modInfo.Value);
                }
            }

            ImGui.PopID();
        }

        private static long DebugTestModCurrentUUID = 0;
        private static (Zproto.Item mod, int[] statLevels) DebugMakeMod(int quality, int configId, int[] statIds, int[] statLevels)
        {
            var item = new Zproto.Item();
            item.Uuid = DebugTestModCurrentUUID++;
            item.Quality = quality;
            item.ConfigId = configId;
            item.ModNewAttr = new ModNewAttr();
            item.ModNewAttr.ModParts.AddRange(statIds);

            return (item, statLevels);
        }

        private static PlayerModDataSave DebugMakeModInv((Zproto.Item mod, int[] statLevels)[] mods)
        {
            var inv = new PlayerModDataSave();
            inv.ModulesPackage = new Package();
            inv.Mod = new Mod();

            foreach (var mod in mods)
            {
                inv.ModulesPackage.Items.Add(mod.mod.Uuid, mod.mod);

                var modInfo = new ModInfo();
                modInfo.InitLinkNums.AddRange(mod.statLevels);
                inv.Mod.ModInfos.TryAdd(mod.mod.Uuid, modInfo);
            }

            return inv;
        }

        private static void DebugDrawModuleScore(byte[][] mods, StatPrio[] prios)
        {
            var modVecs = new Vector<byte>[mods.Length];
            for (int i = 0; i < mods.Length; i++)
            {
                var arr = new byte[Vector<byte>.Count];
                for (int j = 0; j < mods[i].Length; j++)
                {
                    arr[j] = mods[i][j];
                }

                modVecs[i] = new Vector<byte>(arr);
            }

            for (int i = 0; i < Vector<byte>.Count; i++)
            {
                ImGui.Text($"{i:00} ");

                if (i != Vector<byte>.Count - 1)
                    ImGui.SameLine();
            }

            ImGui.Separator();

            foreach (var mod in modVecs)
            {
                for (int i = 0; i < Vector<byte>.Count; i++)
                {
                    ImGui.Text($"{mod[i]:00} ");

                    if (i != Vector<byte>.Count -1)
                        ImGui.SameLine();
                }
            }

            foreach (var prio in prios)
            {
                ImGui.Text($"ID: {prio.Id}, M Lvl: {prio.MinLevel}, R Lvl: {prio.ReqLevel}, Stat Mode: {prio.StatMode}");
            }
        }
#endif

        public static void LoadSavedModData(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Log.Information("No modules data saved at {Path}", path);
                    return;
                }

                var txt = File.ReadAllText(path);
                var modData = JsonConvert.DeserializeObject<PlayerModDataSave>(txt);
                if (modData != null)
                {
                    PlayerModData = modData;
                    ModuleInvUpdated();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading Mod data from {Path}", path);
            }
        }

        private static void SaveModData(string path)
        {
            try
            {
                var jsonTxt = JsonConvert.SerializeObject(PlayerModData, Formatting.Indented);
                File.WriteAllText(path, jsonTxt);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error saving Mod Data to {Path}", path);
            }
        }

        private static ImTextureRef? GetModuleIcon(int id)
        {
            var icon = id switch
            {
                5500101 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_mod_device_attack2.png")),
                5500102 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_mod_device_attack3.png")),
                5500103 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_mod_device_attack4.png")),
                5500104 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_icons_mod_device_attack5.png")),

                5500201 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_mod_device_2.png")),
                5500202 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_mod_device_3.png")),
                5500203 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_mod_device_4.png")),
                5500204 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_icons_mod_device_5.png")),

                5500301 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_mod_device_protect2.png")),
                5500302 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_mod_device_protect3.png")),
                5500303 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_mod_device_protect4.png")),
                5500304 => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "item_icons_device_protect5.png")),

                _ => ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "Missing.png"))
            };

            return icon;
        }

        private static ImTextureRef GetItemQualityBg(int quality)
        {
            var icon = ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, $"item_quality_{quality}.png")) ?? ImageHelper.LoadTexture(Path.Combine(ModuleImgBasePath, "Missing.png"));

            return icon.Value;
        }

        private static bool IsModuleOfType(int id, ModuleType modType) => ModTypeMapping.TryGetValue(id, out var info) ? info == modType : false;

        private static void CalculateBestModules(PlayerModDataSave invToUse = null)
        {
            FilteredModules = [];
            BestModResults = [];
            IsCalculating = true;
            CalcProgress = 0f;
            LastSolveError = null;

            var modWindowSettings = Settings.Instance.WindowSettings.ModuleWindow;
            var solver = new ModuleOptimizer();
            ResultsPlayerModData = invToUse ?? PlayerModData;

            // User-selected backend: exhaustive GPU search or the CPU beam search (approximate).
            // A failure (GPU init/limits) stops the solve and is shown in the results panel.
            var mode = modWindowSettings.ComputeBackend == ComputeBackend.Cpu ? SolverModes.NormalV2 : SolverModes.Gpu;

            try
            {
                var results = solver.Solve(SolverConfig, ResultsPlayerModData, mode, ModuleCalcCancelTokenSource.Token, p => CalcProgress = p);

                FilteredModules = results.FilteredModules;
                BestModResults = results.BestModResults;
                LastSolveUsedCpuFallback = results.UsedCpuFallback;
                LastSolveFromCache = results.FromCache;
                LastSolveCacheExact = results.CacheExact;
            }
            catch (OperationCanceledException)
            {
                BestModResults = null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Module solve failed.");
                LastSolveError = ex.Message;
                BestModResults = null;
            }

            IsCalculating = false;
        }

        public static long CountCombinations4(int n)
        {
            if (n < 4) return 0;
            return (long)n * (n - 1) * (n - 2) * (n - 3) / 24;
        }

#if DEBUG
        private static Dictionary<string, PlayerModDataSave> DebugPlayerModInventories = new Dictionary<string, PlayerModDataSave>
        {
            {
                "Test Mod Set One",
                DebugMakeModInv([
                    DebugMakeMod(4, 5500102, [1408, 1409], [4, 10]),
                    DebugMakeMod(2, 5500102, [1408, 1409], [4, 10]),
                    DebugMakeMod(4, 5500102, [2105], [5]),
                    DebugMakeMod(4, 5500102, [1408, 2105], [5, 8]),
                    DebugMakeMod(4, 5500102, [1408, 1409], [15, 10])
                ])
            },
            {
                "Test Mod Frostbeam One",
                DebugMakeModInv([
                    DebugMakeMod(4, 5500102, [2104, 1409], [4, 10]),
                    DebugMakeMod(2, 5500102, [2104, 1409], [6, 10]),
                    DebugMakeMod(4, 5500102, [2104], [10]),
                    DebugMakeMod(4, 5500102, [2104, 2105], [2, 8]),
                    DebugMakeMod(4, 5500102, [1408, 1409], [15, 10]),

                    DebugMakeMod(4, 5500102, [1408, 1409], [4, 10]),
                    DebugMakeMod(2, 5500102, [1408, 1409], [4, 10]),
                    DebugMakeMod(4, 5500102, [2105], [5]),
                    DebugMakeMod(4, 5500102, [1408, 2105], [5, 8]),
                    DebugMakeMod(4, 5500102, [1408, 1409], [15, 10])
                ])
            }
        };
#endif
    }

    public enum ModuleType
    {
        Attack = 5500100,
        Support = 5500200,
        Guard = 5500300
    }

    public class ModStatInfo
    {
        public string Name { get; set; }
        public string Icon { get; set; }
        public int StatId   { get; set; }
        public ImTextureRef? IconRef { get; set; }
    }

    public class ModTypeInfo
    {
        public string Name { get; set; }
        public string Icon {  set; get; }
    }

    public struct ModuleSet
    {
        public const int MaxModules = 10;

        public ModuleSet()
        {

        }

        public int Mod1 = -1;
        public int Mod2 = -1;
        public int Mod3 = -1;
        public int Mod4 = -1;
        public int Mod5 = -1;
        public int Mod6 = -1;
        public int Mod7 = -1;
        public int Mod8 = -1;
        public int Mod9 = -1;
        public int Mod10 = -1;

        public int[] Mods => [Mod1, Mod2, Mod3, Mod4, Mod5, Mod6, Mod7, Mod8, Mod9, Mod10];

        /// <summary>Builds a ModuleSet from up to 10 values (local indices or resolved ids); rest are -1.</summary>
        public static ModuleSet FromValues(IReadOnlyList<int> values)
        {
            var ms = new ModuleSet();
            ms.Mod1 = values.Count > 0 ? values[0] : -1;
            ms.Mod2 = values.Count > 1 ? values[1] : -1;
            ms.Mod3 = values.Count > 2 ? values[2] : -1;
            ms.Mod4 = values.Count > 3 ? values[3] : -1;
            ms.Mod5 = values.Count > 4 ? values[4] : -1;
            ms.Mod6 = values.Count > 5 ? values[5] : -1;
            ms.Mod7 = values.Count > 6 ? values[6] : -1;
            ms.Mod8 = values.Count > 7 ? values[7] : -1;
            ms.Mod9 = values.Count > 8 ? values[8] : -1;
            ms.Mod10 = values.Count > 9 ? values[9] : -1;
            return ms;
        }
    }

    public struct ModComboResult
    {
        public ModuleSet ModuleSet;
        public int Score;
        public PowerCore[] Stats;
        public int CombatScore;
    }

    public struct PowerCore
    {
        public int Id;
        public int Value;
    }

    public class StatPrio
    {
        // Sentinel returned by GetCap() when this priority has no upper-bound cap
        // (ReqLevel >= 0). Chosen so the solvers' "total > cap" test is never true.
        public const int NoCap = int.MaxValue;

        public StatPrio()
        {

        }

        public StatPrio(int id, int minLevel, int reqLevel, StatMode statMode)
        {
            Id = id;
            MinLevel = minLevel;
            ReqLevel = reqLevel;
            StatMode = statMode;
        }

        public int Id;
        public int MinLevel;
        // >= 0: lower-bound requirement (the stat's summed link total must be at least
        //       this, combined with StatMode Atleast/Exactly).
        // < 0 : upper-bound cap. -1..-6 map to the link tiers 16/12/8/4/1/0; combos whose
        //       total for this stat exceeds the cap are rejected (-6 => must not appear).
        //       A/E (StatMode) is ignored in this mode.
        public int ReqLevel;
        public StatMode StatMode = StatMode.Atleast;

        /// <summary>True when ReqLevel encodes an upper-bound cap (a negative value).</summary>
        public bool HasCap => ReqLevel < 0;

        /// <summary>
        /// Upper bound on this stat's summed link total. ReqLevel -1..-6 map to the link
        /// tiers 16/12/8/4/1/0; a cap of 0 (-6) means the stat must not appear at all.
        /// Returns <see cref="NoCap"/> when ReqLevel >= 0 (no cap).
        /// </summary>
        public int GetCap() => ReqLevel switch
        {
            -1 => 16,
            -2 => 12,
            -3 => 8,
            -4 => 4,
            -5 => 1,
            -6 => 0,
            _ => NoCap,
        };

        /// <summary>
        /// Lower-bound requirement level. A negative ReqLevel is a cap (upper bound) and so
        /// imposes no lower bound: 0 there, the raw ReqLevel otherwise.
        /// </summary>
        public int GetLowerReq() => ReqLevel < 0 ? 0 : ReqLevel;
    }

    public class Preset
    {
        public string Name;
        public SolverConfig Config = new SolverConfig();
    }

    public class ModuleWindowSettings : WindowSettingsBase
    {
        public List<Preset> Presets = [];
        public SolverModes SolverMode = SolverModes.Normal;
        public Preset LastUsedPreset = new Preset();
        // User-selected solver backend; there is no automatic fallback between them.
        public ComputeBackend ComputeBackend = ComputeBackend.Gpu;
        // GPU result cache: while the inventory / candidate set / set size are unchanged,
        // solves re-score the cached candidate pool instead of re-enumerating C(N,K).
        public bool UseBruteForceCache = true;
        // Combos scoring more than this percent below the 10th-best are not pooled.
        public int CacheThresholdPct = 20;
    }

    public enum SolverModes
    {
        Legacy,
        Fallback,
        Normal,
        NormalV2,
        Gpu
    }

    // Which hardware runs the module solver. Gpu = exhaustive DirectCompute search,
    // Cpu = lightweight beam search (approximate).
    public enum ComputeBackend
    {
        Gpu,
        Cpu
    }

    public enum ScoreMode
    {
        ZScore,
        CombatPower
    }

    // ZScore scoring heuristic selection (does not affect the CombatPower score).
    public enum ScoringModel
    {
        // This fork's tuned heuristic: no overcap reward (breakpoint-only, cap 20).
        Enhanced,
        // Upstream behavior: raw points past the snapped breakpoint are also added.
        Original
    }

    public class SolverResult
    {
        public List<ModComboResult> BestModResults = [];
        public List<long> FilteredModules = [];
        // True when the debug-only CPU fallback ran after a GPU failure and the
        // lightweight beam search produced these (approximate) results instead.
        public bool UsedCpuFallback;
        // True when these results were extracted from the brute-force pool cache
        // instead of a full enumeration.
        public bool FromCache;
        // With FromCache: true when the scoring config matches the cache-building one
        // (provably exact); false when only re-scored (may miss pruned combos).
        public bool CacheExact;
    }

    public enum StatMode
    {
        Atleast,
        Exactly
    }
}
