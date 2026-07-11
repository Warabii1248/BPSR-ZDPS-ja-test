using BPSR_ZDPSLib;
using BPSR_ZDPS.DataTypes;
using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace BPSR_ZDPS.Windows
{
    public static class SettingsWindow
    {
        public const string LAYER = "SettingsWindowLayer";
        public static string TITLE_ID = "###SettingsWindow";

        static string Language;
        static int PreviousSelectedNetworkDeviceIdx = -1;
        static int SelectedNetworkDeviceIdx = -1;
        static bool normalizeMeterContributions;
        static bool useShortWidthNumberFormatting;
        static bool showClassIconsInMeters;
        static bool colorClassIconsByRole;
        static bool showSkillIconsInDetails;
        static bool onlyShowDamageContributorsInMeters;
        static bool onlyShowPartyMembersInMeters;
        static bool showAbilityScoreInMeters;
        static bool showSeasonStrengthInMeters;
        static bool showSubProfessionNameInMeters;
        static bool showPlayerSummonsInMeters;
        static bool showPlayerImaginesInMeters;
        static bool useAutomaticWipeDetection;
        static bool skipTeleportStateCheckInAutomaticWipeDetection;
        static bool disableWipeRecalculationOverwriting;
        static bool useLegacyWipeDetection;
        static bool splitEncountersOnNewPhases;
        static bool displayTruePerSecondValuesInMeters;
        static bool hideEncounterPerSecondValuesInMeters;
        static bool allowGamepadNavigationInputInZDPS;
        static bool keepPastEncounterInMeterUntilNextDamage;
        static bool showChannelLineNumberInStatus;
        static bool showCallWipeForEncounterOnMainWindow;
        static bool useDatabaseForEncounterHistory;
        static int databaseRetentionPolicyDays;
        static bool skipSavingEncountersWithNoCombatData;
        static bool limitEncounterBuffTrackingInOpenWorld;
        static bool skipSkillSnapshotSavingInOpenWorld;
        static bool allowEncounterSavingPausingInOpenWorld;
        static bool persistEncounterSavingPauseStateBetweenMaps;
        static bool minimalProcessingWhileEncounterSavingPaused;
        static bool includeHealEventsOutsideOfCombat;

        static bool meterSettingsTankingShowDeaths;
        static bool meterSettingsNpcTakenShowHpData;
        static bool meterSettingsNpcTakenHideMaxHp;
        static bool meterSettingsNpcTakenUseHpMeter;

        static bool playNotificationSoundOnMatchmake;
        static string matchmakeNotificationSoundPath;
        static bool loopNotificationSoundOnMatchmake;
        static float matchmakeNotificationVolume;
        static bool playNotificationSoundOnReadyCheck;
        static string readyCheckNotificationSoundPath;
        static bool loopNotificationSoundOnReadyCheck;
        static float readyCheckNotificationVolume;

        static bool logToFile;

        static bool IsBindingEncounterResetKey = false;
        static uint EncounterResetKey;
        static string EncounterResetKeyName = "";
        static bool IsBindingPinnedWindowClickthroughKey = false;
        static uint PinnedWindowClickthroughKey;
        static string PinnedWindowClickthroughKeyName = "";
        static bool IsBindingToggleWindowMinimizeKey = false;
        static uint ToggleWindowMinimizeKey;
        static string ToggleWindowMinimizeKeyName = "";

        static SharpPcap.LibPcap.LibPcapLiveDeviceList? NetworkDevices;
        static EGameCapturePreference GameCapturePreference;
        static string gameCaptureCustomExeName;

        static bool saveEncounterReportToFile;
        static int reportFileRetentionPolicyDays;
        static int minimumPlayerCountToCreateReport;
        static bool alwaysCreateReportAtDungeonEnd;

        static bool webhookReportsEnabled;
        static EWebhookReportsMode webhookReportsMode;
        static string webhookReportsDeduplicationServerUrl;
        static string webhookReportsDiscordUrl;
        static string webhookReportsCustomUrl;

        static bool checkForZDPSUpdatesOnStartup;
        static string latestZDPSVersionCheckURL;

        static bool lowPerformanceMode;
        static int fixedFramerate;

        static bool enableGDIBackBufferCopyCompatibility;

        static bool aggressiveExceptionDebugLogging;

        // External Settings
        static bool externalBPTimerEnabled;
        static bool externalBPTimerIncludeCharacterId;
        static bool externalBPTimerFieldBossHpReportsEnabled;

        static WindowSettings windowSettings;

        static bool IsDiscordWebhookUrlValid = true;

        static int RunOnceDelayed = 0;

        static bool IsElevated = false;

        static Dictionary<int, float> allowedSyncRates = new();
        static float fpsUpdateTracker = 0.0f;
        static double currentFps = 0.0;

        static Version npcapVersion = new();

        public static void Open()
        {
            RunOnceDelayed = 0;

            ImGuiP.PushOverrideID(ImGuiP.ImHashStr(LAYER));
            ImGui.OpenPopup(TITLE_ID);

            NetworkDevices = SharpPcap.LibPcap.LibPcapLiveDeviceList.Instance;

            Load();

            LoadHotkeys();

            // Set selection to matching device name (the index could have changed since last time we were here)
            if (!string.IsNullOrEmpty(Settings.Instance.NetCaptureDeviceName))
            {
                for (int i = 0; i < NetworkDevices.Count; i++)
                {
                    if (NetworkDevices[i].Name == Settings.Instance.NetCaptureDeviceName)
                    {
                        SelectedNetworkDeviceIdx = i;
                        if (PreviousSelectedNetworkDeviceIdx == -1)
                        {
                            // This is the first time we're opening the menu, so let's set the default previous value as well
                            // Doing so prevents the capture from being restarted on first save
                            PreviousSelectedNetworkDeviceIdx = i;
                        }
                    }
                }
            }

            // Default to first device in list as fallback, if there are any
            if (SelectedNetworkDeviceIdx == -1 && NetworkDevices?.Count > 0)
            {
                SelectedNetworkDeviceIdx = 0;
            }

            // Disable all HotKeys while we're in the Settings menu to prevent unexpected behavior when rebinding
            HotKeyManager.UnregisterAllHotKeys();

            RecalculateRefreshRates();

            npcapVersion = User32.GetNpcapVersion();

            ImGui.PopID();
        }

        public static void Draw(MainWindow mainWindow)
        {
            var io = ImGui.GetIO();
            var main_viewport = ImGui.GetMainViewport();

            // TODO: Open window at center of current active monitor
            // Will need to use GLFW to figure out monitors/sizes/positions/etc

            //ImGui.SetNextWindowPos(new Vector2(main_viewport.WorkPos.X + 200, main_viewport.WorkPos.Y + 120), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(550, 350), new Vector2(ImGui.GETFLTMAX()));
            //ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X, io.DisplaySize.Y), ImGuiCond.Appearing);

            ImGui.SetNextWindowSize(new Vector2(700, 700), ImGuiCond.FirstUseEver);
            ImGuiP.PushOverrideID(ImGuiP.ImHashStr(LAYER));

            if (ImGui.BeginPopupModal($"{AppStrings.GetLocalized("Settings_WindowTitle")}{TITLE_ID}"))
            {
                if (RunOnceDelayed == 0)
                {
                    RunOnceDelayed++;
                    using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    {
                        IsElevated = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    }
                }
                else if (RunOnceDelayed == 2)
                {
                    RunOnceDelayed++;
                    Utils.SetCurrentWindowIcon();
                    Utils.BringWindowToFront();
                }
                else if (RunOnceDelayed < 3)
                {
                    RunOnceDelayed++;
                }

                ImGuiTabBarFlags tabBarFlags = ImGuiTabBarFlags.FittingPolicyScroll | ImGuiTabBarFlags.NoTooltip | ImGuiTabBarFlags.NoCloseWithMiddleMouseButton;
                if (ImGui.BeginTabBar("##SettingsTabs", tabBarFlags))
                {
                    if (ImGui.BeginTabItem($"{AppStrings.GetLocalized("Settings_Tab_General")}###GeneralTab"))
                    {
                        var contentRegionAvail = ImGui.GetContentRegionAvail();
                        ImGui.BeginChild("##GeneralTabContent", new Vector2(contentRegionAvail.X, contentRegionAvail.Y - 56), ImGuiChildFlags.Borders);

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_Localization"));

                        //ImGui.BeginDisabled();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(AppStrings.GetLocalized("Settings_Language_Label"));
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(150);
                        if (ImGui.BeginCombo("##LanguageCombo", System.Globalization.CultureInfo.GetCultureInfo(Language).EnglishName))
                        {
                            if (ImGui.Selectable("English (EN)"))
                            {
                                Language = "en";
                            }

                            if (ImGui.Selectable("Chinese (ZH)"))
                            {
                                Language = "zh";
                            }

                            if (ImGui.Selectable("Japanese (JA)"))
                            {
                                Language = "ja";
                            }

                            ImGui.EndCombo();
                        }

                        //ImGui.EndDisabled();

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_NetworkDevice"));

                        if (npcapVersion == new Version())
                        {
                            ImGui.PushStyleColor(ImGuiCol.ChildBg, Colors.Red_Transparent);
                            ImGui.BeginChild($"##VeryOutOfDateNpcapVersion", new Vector2(0, 0), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.Borders);
                            ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], ImGui.GetFontSize());
                            ImGui.TextUnformatted(AppStrings.GetLocalized("Settings_ErrorLabel"));
                            ImGui.PopFont();
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_NpcapVeryOutOfDate_Desc"));
                            ImGui.EndChild();
                            ImGui.PopStyleColor();
                        }
                        else if (npcapVersion < new Version(1, 86))
                        {
                            ImGui.PushStyleColor(ImGuiCol.ChildBg, Colors.Goldenrod_Transparent);
                            ImGui.BeginChild($"##OutOfDateNpcapVersion", new Vector2(0, 0), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.Borders);
                            ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], ImGui.GetFontSize());
                            ImGui.TextUnformatted(AppStrings.GetLocalized("Settings_WarningLabel"));
                            ImGui.PopFont();
                            ImGui.TextWrapped(string.Format(AppStrings.GetLocalized("Settings_NpcapOutOfDate_Desc_Format"), npcapVersion));
                            ImGui.EndChild();
                            ImGui.PopStyleColor();
                        }

                        ImGui.Text(AppStrings.GetLocalized("Settings_NetworkDevice_Text"));

                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

                        string network_device_preview = "";
                        if (SelectedNetworkDeviceIdx > -1 && NetworkDevices?.Count > 0)
                        {
                            network_device_preview = NetworkDevices[SelectedNetworkDeviceIdx].Description;
                        }

                        if (ImGui.BeginCombo("##NetworkDeviceCombo", network_device_preview, ImGuiComboFlags.HeightLarge))
                        {
                            for (int i = 0; i < NetworkDevices?.Count; i++)
                            {
                                bool isSelected = (SelectedNetworkDeviceIdx == i);
                                var device = NetworkDevices[i];

                                string friendlyName = "";
                                if (!string.IsNullOrEmpty(device.Interface?.FriendlyName))
                                {
                                    friendlyName = $"{device.Interface?.FriendlyName}\n";
                                }

                                if (ImGui.Selectable($"{friendlyName}{device.Description}\n{device.Name}", isSelected))
                                {
                                    SelectedNetworkDeviceIdx = i;
                                }

                                if (isSelected)
                                {
                                    ImGui.SetItemDefaultFocus();
                                }

                                ImGui.Separator();
                            }

                            if (NetworkDevices == null || NetworkDevices?.Count == 0)
                            {
                                ImGui.Selectable(AppStrings.GetLocalized("Settings_NoNetworkDevicesFound"));
                            }

                            ImGui.EndCombo();
                        }

                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(AppStrings.GetLocalized("Settings_GameCapturePreference"));
                        ImGui.SameLine();

                        var gamePrefName = Utils.GameCapturePreferenceToName(GameCapturePreference);
                        ImGui.SetNextItemWidth(150);
                        if (ImGui.BeginCombo("##EGameCapturePreference", gamePrefName, ImGuiComboFlags.HeightLarge))
                        {
                            if (ImGui.Selectable(AppStrings.GetLocalized("Settings_GameCapturePreference_Auto")))
                            {
                                GameCapturePreference = EGameCapturePreference.Auto;
                            }
                            else if (ImGui.Selectable(AppStrings.GetLocalized("Settings_GameCapturePreference_Standalone")))
                            {
                                GameCapturePreference = EGameCapturePreference.Standalone;
                            }
                            else if (ImGui.Selectable("Steam"))
                            {
                                GameCapturePreference = EGameCapturePreference.Steam;
                            }
                            else if (ImGui.Selectable("Epic"))
                            {
                                GameCapturePreference = EGameCapturePreference.Epic;
                            }
                            else if (ImGui.Selectable("HaoPlay SEA"))
                            {
                                GameCapturePreference = EGameCapturePreference.HaoPlaySea;
                            }
                            else if (ImGui.Selectable("XDG"))
                            {
                                GameCapturePreference = EGameCapturePreference.XDG;
                            }
                            else if (ImGui.Selectable("HaoPlay SEA Steam"))
                            {
                                GameCapturePreference = EGameCapturePreference.HaoPlaySeaSteam;
                            }
                            else if (ImGui.Selectable("XDG Steam"))
                            {
                                GameCapturePreference = EGameCapturePreference.XDGSteam;
                            }
                            else if (ImGui.Selectable("WeGame"))
                            {
                                GameCapturePreference = EGameCapturePreference.WeGame;
                            }
                            else if (ImGui.Selectable(AppStrings.GetLocalized("Settings_GameCapturePreference_Custom")))
                            {
                                GameCapturePreference = EGameCapturePreference.Custom;
                            }
                            ImGui.SetItemTooltip(AppStrings.GetLocalized("Settings_GameCapturePreference_Custom_Tooltip"));

                            ImGui.EndCombo();
                        }

                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_GameCapturePreference_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        if (GameCapturePreference == EGameCapturePreference.Custom)
                        {
                            ImGui.Indent();
                            
                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_CustomBPSRExecutableName"));
                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(-1);
                            if (ImGui.InputText("##GameCaptureCustomExeName", ref gameCaptureCustomExeName, 512))
                            {
                                gameCaptureCustomExeName = Path.GetFileNameWithoutExtension(gameCaptureCustomExeName);
                            }
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_CustomBPSRExecutableName_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.Unindent();
                        }

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_Keybinds"));

                        if (IsElevated == false)
                        {
                            ImGui.PushStyleColor(ImGuiCol.ChildBg, Colors.Red_Transparent);
                            ImGui.BeginChild("##KeybindsNotice", new Vector2(0, 0), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.Borders);
                            ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], ImGui.GetFontSize());
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ImportantNoteLabel"));
                            ImGui.PopFont();
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_Keybinds_Notice"));
                            ImGui.EndChild();
                            ImGui.PopStyleColor();
                        }

                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_Keybinds_Text"));
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_PressEscapeToCancelRebinding"));

                        ImGui.Indent();

                        RebindKeyButton(AppStrings.GetLocalized("Settings_Keybinds_EncounterReset_Label"), ref EncounterResetKey, ref EncounterResetKeyName, ref IsBindingEncounterResetKey);
                        if (splitEncountersOnNewPhases)
                        {
                            ImGui.Indent();
                            ImGui.PushStyleColor(ImGuiCol.Text, Colors.Red_Transparent);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_Keybinds_EncounterReset_Warning"));
                            ImGui.PopStyleColor();
                            ImGui.Unindent();
                        }
                        RebindKeyButton(AppStrings.GetLocalized("Settings_Keybinds_PinnedWindowClickthrough_Label"), ref PinnedWindowClickthroughKey, ref PinnedWindowClickthroughKeyName, ref IsBindingPinnedWindowClickthroughKey);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_Keybinds_PinnedWindowClickthrough_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        RebindKeyButton(AppStrings.GetLocalized("Settings_Keybinds_ToggleWindowMinimize_Label"), ref ToggleWindowMinimizeKey, ref ToggleWindowMinimizeKeyName, ref IsBindingToggleWindowMinimizeKey);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_Keybinds_ToggleWindowMinimize_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.Unindent();

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_ZDPSUpdateChecking"));

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_CheckForZDPSUpdatesOnStartup"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##CheckForZDPSUpdatesOnStartup", ref checkForZDPSUpdatesOnStartup);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_CheckForZDPSUpdatesOnStartup_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_LatestZDPSVersionCheckURL"));
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputText("##LatestZDPSVersionCheckURL", ref latestZDPSVersionCheckURL, 512))
                        {
                            // If the value was empty, revert back to the default URL
                            if (string.IsNullOrEmpty(latestZDPSVersionCheckURL))
                            {
                                latestZDPSVersionCheckURL = "https://raw.githubusercontent.com/Blue-Protocol-Source/BPSR-ZDPS-Metadata/master/LatestVersion.txt";
                            }
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_LatestZDPSVersionCheckURL_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_Database"));

                        ShowRestartRequiredNotice(Settings.Instance.UseDatabaseForEncounterHistory != useDatabaseForEncounterHistory, AppStrings.GetLocalized("Settings_UseDatabaseForEncounterHistory").TrimEnd(':', '：', ' '));

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_UseDatabaseForEncounterHistory"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##UseDatabaseForEncounterHistory", ref useDatabaseForEncounterHistory);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_UseDatabaseForEncounterHistory_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.BeginDisabled(!useDatabaseForEncounterHistory);
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_DatabaseEncounterHistoryRetentionPolicy"));
                        ImGui.SameLine();
                        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                        ImGui.SetNextItemWidth(-1);
                        ImGui.SliderInt("##DatabaseRetentionPolicyDays", ref databaseRetentionPolicyDays, 0, 30, databaseRetentionPolicyDays == 0 ? AppStrings.GetLocalized("Settings_KeepForever") : string.Format(AppStrings.GetLocalized("Settings_DaysFormat"), databaseRetentionPolicyDays));
                        ImGui.PopStyleColor(2);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_DatabaseEncounterHistoryRetentionPolicy_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();
                        ImGui.EndDisabled();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_SkipSavingEncountersWithNoCombatData"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##SkipSavingEncountersWithNoCombatData", ref skipSavingEncountersWithNoCombatData);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_SkipSavingEncountersWithNoCombatData_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_LimitEncounterBuffTrackingInOpenWorld"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##LimitEncounterBuffTrackingInOpenWorld", ref limitEncounterBuffTrackingInOpenWorld);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_LimitEncounterBuffTrackingInOpenWorld_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_SkipSkillSnapshotSavingInOpenWorld"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##SkipSkillSnapshotSavingInOpenWorld", ref skipSkillSnapshotSavingInOpenWorld);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_SkipSkillSnapshotSavingInOpenWorld_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_AllowEncounterSavingPausingInOpenWorld"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##AllowEncounterSavingPausingInOpenWorld", ref allowEncounterSavingPausingInOpenWorld);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_AllowEncounterSavingPausingInOpenWorld_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.BeginDisabled(!allowEncounterSavingPausingInOpenWorld);

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_PersistEncounterSavingPauseStateBetweenMaps"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##PersistEncounterSavingPauseStateBetweenMaps", ref persistEncounterSavingPauseStateBetweenMaps);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_PersistEncounterSavingPauseStateBetweenMaps_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_MinimalProcessingWhileEncounterSavingPaused"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##MinimalProcessingWhileEncounterSavingPaused", ref minimalProcessingWhileEncounterSavingPaused);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_MinimalProcessingWhileEncounterSavingPaused_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.EndDisabled();

                        ImGui.EndChild();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem($"{AppStrings.GetLocalized("Settings_Tab_Combat")}###CombatTab"))
                    {
                        var contentRegionAvail = ImGui.GetContentRegionAvail();
                        ImGui.BeginChild("##CombatTabContent", new Vector2(contentRegionAvail.X, contentRegionAvail.Y - 56), ImGuiChildFlags.Borders);

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_Combat"));

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_NormalizeMeterContributionBars"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##NormalizeMeterContributions", ref normalizeMeterContributions);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_NormalizeMeterContributionBars_Desc"));
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_NormalizeMeterContributionBars_Desc2"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_UseShortWidthNumberFormatting"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##UseShortWidthNumberFormatting", ref useShortWidthNumberFormatting);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_UseShortWidthNumberFormatting_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_UseAutomaticWipeDetection"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##UseAutomaticWipeDetection", ref useAutomaticWipeDetection);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_UseAutomaticWipeDetection_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.BeginDisabled(!useAutomaticWipeDetection);

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_UseLegacyWipeDetection"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##UseLegacyWipeDetection", ref useLegacyWipeDetection);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_UseLegacyWipeDetection_Desc"));
                        if (useLegacyWipeDetection)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, Colors.Red);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_UseLegacyWipeDetection_Warning"));
                            ImGui.PopStyleColor();
                        }
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.BeginDisabled(!useLegacyWipeDetection);
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_SkipTeleportStateCheckInAutomaticWipeDetection"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##SkipTeleportStateCheckInAutomaticWipeDetection", ref skipTeleportStateCheckInAutomaticWipeDetection);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_SkipTeleportStateCheckInAutomaticWipeDetection_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_DisableWipeRecalculationOverwriting"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##DisableWipeRecalculationOverwriting", ref disableWipeRecalculationOverwriting);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        //ImGui.TextWrapped("When enabled, the internal process of checking the Dead status of all players in the Encounter is allowed to overwrite the detected wipe status from the normal automatic detector.\nAllowing this to overturn results is experimental so only enable it if you run into incorrect wipe reporting.");
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_DisableWipeRecalculationOverwriting_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();
                        ImGui.EndDisabled();

                        ImGui.EndDisabled();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_SplitEncountersOnNewPhases"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##SplitEncountersOnNewPhases", ref splitEncountersOnNewPhases);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_SplitEncountersOnNewPhases_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_DisplayActivePerSecondValuesInMeters"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##DisplayTruePerSecondValuesInMeters", ref displayTruePerSecondValuesInMeters);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_DisplayActivePerSecondValuesInMeters_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.Indent();
                        ImGui.BeginDisabled(!displayTruePerSecondValuesInMeters);
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_HideEncounterPerSecondValuesInMeters"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##HideEncounterPerSecondValuesInMeters", ref hideEncounterPerSecondValuesInMeters);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_HideEncounterPerSecondValuesInMeters_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_IncludeHealEventsOutsideOfCombat"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##IncludeHealEventsOutsideOfCombat", ref includeHealEventsOutsideOfCombat);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_IncludeHealEventsOutsideOfCombat_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.EndChild();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem($"{AppStrings.GetLocalized("Settings_Tab_UserInterface")}###UserInterfaceTab"))
                    {
                        var contentRegionAvail = ImGui.GetContentRegionAvail();
                        ImGui.BeginChild("##UserInterfaceTabContent", new Vector2(contentRegionAvail.X, contentRegionAvail.Y - 56), ImGuiChildFlags.Borders);

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_UserInterface"));

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ShowClassIconsInMeters"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##ShowClassIconsInMeters", ref showClassIconsInMeters);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ShowClassIconsInMeters_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ColorClassIconsByRoleType"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##ColorClassIconsByRole", ref colorClassIconsByRole);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ColorClassIconsByRoleType_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ShowSkillIconsInDetails"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##ShowSkillIconsInDetails", ref showSkillIconsInDetails);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ShowSkillIconsInDetails_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_OnlyShowDamageContributorsInMeters"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##OnlyShowContributorsInMeters", ref onlyShowDamageContributorsInMeters);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_OnlyShowDamageContributorsInMeters_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_OnlyShowPartyMembersInMeters"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##OnlyShowPartyMembersInMeters", ref onlyShowPartyMembersInMeters);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_OnlyShowPartyMembersInMeters_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ShowAbilityScoreInMeters"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##ShowAbilityScoreInMeters", ref showAbilityScoreInMeters);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ShowAbilityScoreInMeters_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ShowSeasonStrengthInMeters"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##ShowSeasonStrengthInMeters", ref showSeasonStrengthInMeters);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ShowSeasonStrengthInMeters_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ShowSubProfessionNameInMeters"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##ShowSubProfessionNameInMeters", ref showSubProfessionNameInMeters);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ShowSubProfessionNameInMeters_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ShowPlayerSummonsInNPCTakenMeter"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##ShowPlayerSummonsInMeters", ref showPlayerSummonsInMeters);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ShowPlayerSummonsInNPCTakenMeter_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ShowPlayerImaginesInMeters"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##ShowPlayerImaginesInMeters", ref showPlayerImaginesInMeters);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ShowPlayerImaginesInMeters_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_AllowGamepadNavigationInputInZDPS"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##AllowGamepadNavigationInputInZDPS", ref allowGamepadNavigationInputInZDPS);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_AllowGamepadNavigationInputInZDPS_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_KeepPastEncounterInMeterUIUntilNextDamage"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##KeepPastEncounterInMeterUntilNextDamage", ref keepPastEncounterInMeterUntilNextDamage);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_KeepPastEncounterInMeterUIUntilNextDamage_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ShowChannelLineNumberInStatus"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##ShowChannelLineNumberInStatus", ref showChannelLineNumberInStatus);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ShowChannelLineNumberInStatus_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ShowCallWipeForEncounterOnMainWindow"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##ShowCallWipeForEncounterOnMainWindow", ref showCallWipeForEncounterOnMainWindow);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ShowCallWipeForEncounterOnMainWindow_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        if (ImGui.CollapsingHeader($"{AppStrings.GetLocalized("Settings_Section_PinnedWindowOpacities")}###PinnedWindowOpacitiesSection"))
                        {
                            ImGui.Indent();

                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_MainWindowOpacity_Label"));
                            ImGui.SetNextItemWidth(-1);
                            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                            if (ImGui.SliderInt("##MainWindowOpacity", ref windowSettings.MainWindow.Opacity, 20, 100, $"{windowSettings.MainWindow.Opacity}%%", ImGuiSliderFlags.ClampOnInput))
                            {
                                windowSettings.MainWindow.Opacity = windowSettings.MainWindow.Opacity;
                            }
                            ImGui.PopStyleColor(2);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_MainWindowOpacity_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.SetNextItemWidth(-1);
                            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                            if (ImGui.SliderInt("##MainWindowBackgroundOpacity", ref windowSettings.MainWindow.BackgroundOpacity, 0, 100, $"{windowSettings.MainWindow.BackgroundOpacity}%%", ImGuiSliderFlags.ClampOnInput))
                            {
                                windowSettings.MainWindow.BackgroundOpacity = windowSettings.MainWindow.BackgroundOpacity;
                            }
                            ImGui.PopStyleColor(2);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_MainWindowBackgroundOpacity_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_CooldownPriorityTrackerWindowOpacity_Label"));
                            ImGui.SetNextItemWidth(-1);
                            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                            if (ImGui.SliderInt("##CooldownPriorityTrackerWindowOpacity", ref windowSettings.RaidManagerCooldowns.Opacity, 20, 100, $"{windowSettings.RaidManagerCooldowns.Opacity}%%", ImGuiSliderFlags.ClampOnInput))
                            {
                                windowSettings.RaidManagerCooldowns.Opacity = windowSettings.RaidManagerCooldowns.Opacity;
                            }
                            ImGui.PopStyleColor(2);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_CooldownPriorityTrackerWindowOpacity_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_EntityCacheViewerWindowOpacity_Label"));
                            ImGui.SetNextItemWidth(-1);
                            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                            if (ImGui.SliderInt("##EntityCacheViewerWindowOpacity", ref windowSettings.EntityCacheViewer.Opacity, 20, 100, $"{windowSettings.EntityCacheViewer.Opacity}%%", ImGuiSliderFlags.ClampOnInput))
                            {
                                windowSettings.EntityCacheViewer.Opacity = windowSettings.EntityCacheViewer.Opacity;
                            }
                            ImGui.PopStyleColor(2);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_EntityCacheViewerWindowOpacity_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_Integrations"));

                            if (ImGui.CollapsingHeader("BPTimer##BPTimerOpacitySection", ImGuiTreeNodeFlags.DefaultOpen))
                            {
                                ImGui.Indent();

                                ImGui.AlignTextToFramePadding();
                                ImGui.Text(AppStrings.GetLocalized("Settings_SpawnTrackerWindowOpacity_Label"));
                                ImGui.SetNextItemWidth(-1);
                                ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                                ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                                if (ImGui.SliderInt("##BPTimerSpawnTrackerWindowOpacity", ref windowSettings.SpawnTracker.Opacity, 20, 100, $"{windowSettings.SpawnTracker.Opacity}%%"))
                                {
                                    windowSettings.SpawnTracker.Opacity = windowSettings.SpawnTracker.Opacity;
                                }
                                ImGui.PopStyleColor(2);
                                ImGui.Indent();
                                ImGui.BeginDisabled(true);
                                ImGui.TextWrapped(AppStrings.GetLocalized("Settings_SpawnTrackerWindowOpacity_Desc"));
                                ImGui.EndDisabled();
                                ImGui.Unindent();

                                ImGui.Unindent();
                            }

                            ImGui.Unindent();
                        }

                        if (ImGui.CollapsingHeader($"{AppStrings.GetLocalized("Settings_Section_WindowScales")}###WindowScalesSection"))
                        {
                            ImGui.Indent();

                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_MeterBarScale_Label"));
                            ImGui.SetNextItemWidth(-1);
                            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                            if (ImGui.SliderFloat("##MeterBarScale", ref windowSettings.MainWindow.MeterBarScale, 0.80f, 2.0f, $"{(int)(windowSettings.MainWindow.MeterBarScale * 100)}%%"))
                            {
                                windowSettings.MainWindow.MeterBarScale = MathF.Round(windowSettings.MainWindow.MeterBarScale, 2);
                            }
                            ImGui.PopStyleColor(2);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_MeterBarScale_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_Integrations"));

                            if (ImGui.CollapsingHeader("BPTimer##BPTimerScaleSection", ImGuiTreeNodeFlags.DefaultOpen))
                            {
                                ImGui.Indent();

                                ImGui.AlignTextToFramePadding();
                                ImGui.Text(AppStrings.GetLocalized("Settings_SpawnTrackerTextScale_Label"));
                                ImGui.SetNextItemWidth(-1);
                                ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                                ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                                if (ImGui.SliderFloat("##BPTimerSpawnTrackerTextScale", ref windowSettings.SpawnTracker.TextScale, 0.80f, 3.0f, $"{(int)(windowSettings.SpawnTracker.TextScale * 100)}%%"))
                                {
                                    windowSettings.SpawnTracker.TextScale = MathF.Round(windowSettings.SpawnTracker.TextScale, 2);
                                }
                                ImGui.PopStyleColor(2);
                                ImGui.Indent();
                                ImGui.BeginDisabled(true);
                                ImGui.TextWrapped(AppStrings.GetLocalized("Settings_SpawnTrackerTextScale_Desc"));
                                ImGui.EndDisabled();
                                ImGui.Unindent();

                                ImGui.AlignTextToFramePadding();
                                ImGui.Text(AppStrings.GetLocalized("Settings_SpawnTrackerLineScale_Label"));
                                ImGui.SetNextItemWidth(-1);
                                ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                                ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                                if (ImGui.SliderFloat("##BPTimerSpawnTrackerLineScale", ref windowSettings.SpawnTracker.LineScale, 0.80f, 3.0f, $"{(int)(windowSettings.SpawnTracker.LineScale * 100)}%%"))
                                {
                                    windowSettings.SpawnTracker.LineScale = MathF.Round(windowSettings.SpawnTracker.LineScale, 2);
                                }
                                ImGui.PopStyleColor(2);
                                ImGui.Indent();
                                ImGui.BeginDisabled(true);
                                ImGui.TextWrapped(AppStrings.GetLocalized("Settings_SpawnTrackerLineScale_Desc"));
                                ImGui.EndDisabled();
                                ImGui.Unindent();

                                ImGui.Unindent();
                            }

                            ImGui.Unindent();
                        }

                        if(ImGui.CollapsingHeader($"{AppStrings.GetLocalized("Settings_Section_MeterSettings")}###MeterSettingsSection"))
                        {
                            ImGui.Indent();

                            ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_Tanking"));

                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_MeterSettingsTankingShowDeaths_Label"));
                            ImGui.SameLine();
                            ImGui.Checkbox("##MeterSettingsTankingShowDeaths", ref meterSettingsTankingShowDeaths);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_MeterSettingsTankingShowDeaths_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_NpcTaken"));

                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_MeterSettingsNpcTakenShowHpData_Label"));
                            ImGui.SameLine();
                            ImGui.Checkbox("##MeterSettingsNpcTakenShowHpData", ref meterSettingsNpcTakenShowHpData);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_MeterSettingsNpcTakenShowHpData_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_MeterSettingsNpcTakenHideMaxHp_Label"));
                            ImGui.SameLine();
                            ImGui.Checkbox("##MeterSettingsNpcTakenHideMaxHp", ref meterSettingsNpcTakenHideMaxHp);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_MeterSettingsNpcTakenHideMaxHp_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_MeterSettingsNpcTakenUseHpMeter_Label"));
                            ImGui.SameLine();
                            ImGui.Checkbox("##MeterSettingsNpcTakenUseHpMeter", ref meterSettingsNpcTakenUseHpMeter);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_MeterSettingsNpcTakenUseHpMeter_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.Unindent();
                        }

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_WindowPropertyResets"));

                        if (ImGui.Button($"{AppStrings.GetLocalized("Settings_ResetMainWindowPositionBtn")}###ResetMainWindowPositionBtn"))
                        {
                            var glfwMonitor = Hexa.NET.GLFW.GLFW.GetPrimaryMonitor();
                            var glfwVidMode = Hexa.NET.GLFW.GLFW.GetVideoMode(glfwMonitor);
                            mainWindow.NextWindowPosition = new Vector2(glfwVidMode.Width, glfwVidMode.Height);
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ResetMainWindowPositionBtn_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        if (ImGui.Button($"{AppStrings.GetLocalized("Settings_ResetMainWindowSizeBtn")}###ResetMainWindowSizeBtn"))
                        {
                            mainWindow.NextWindowSize = mainWindow.DefaultWindowSize;
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ResetMainWindowSizeBtn_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        if (ImGui.Button($"{AppStrings.GetLocalized("Settings_ResetRaidManagerCooldownTrackerSizeBtn")}###ResetRaidManagerCooldownTrackerSizeBtn"))
                        {
                            RaidManagerCooldownsWindow.ResetWindowSize = true;
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ResetRaidManagerCooldownTrackerSizeBtn_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        if (ImGui.Button($"{AppStrings.GetLocalized("Settings_ResetEntityCacheViewerSizeBtn")}###ResetEntityCacheViewerSizeBtn"))
                        {
                            EntityCacheViewerWindow.ResetWindowSize = true;
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ResetEntityCacheViewerSizeBtn_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        if (ImGui.Button($"{AppStrings.GetLocalized("Settings_ResetBPTimerSpawnTrackerSizeBtn")}###ResetBPTimerSpawnTrackerSizeBtn"))
                        {
                            SpawnTrackerWindow.ResetWindowSize = true;
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ResetBPTimerSpawnTrackerSizeBtn_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_LowPerformanceMode"));

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_LowPerformanceMode_Label"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##LowPerformanceMode", ref lowPerformanceMode);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_LowPerformanceMode_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.BeginDisabled(lowPerformanceMode);
                        int maxSyncRate = 1;
                        if (allowedSyncRates.Count > 0)
                        {
                            maxSyncRate = allowedSyncRates.Last().Key;
                        }
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_FixedFramerate_Label"));
                        ImGui.SetNextItemWidth(-1);
                        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                        if (ImGui.SliderInt("##FixedFramerate", ref fixedFramerate, 1, maxSyncRate, $"{fixedFramerate} ({allowedSyncRates[fixedFramerate]}hz)", ImGuiSliderFlags.ClampOnInput))
                        {
                            Settings.Instance.FixedFramerateScale = (uint)fixedFramerate;
                        }
                        ImGui.PopStyleColor(2);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_FixedFramerate_Desc"));
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_FixedFramerate_Note"));
                        if (fpsUpdateTracker >= 0.5)
                        {
                            currentFps = Math.Round(1 / io.DeltaTime, 1);
                            fpsUpdateTracker = 0;
                        }
                        else
                        {
                            fpsUpdateTracker += io.DeltaTime;
                        }
                        ImGui.TextUnformatted(string.Format(AppStrings.GetLocalized("Settings_EstimatedCurrentFps_Format"), currentFps));
                        ImGui.EndDisabled();
                        ImGui.Unindent();
                        ImGui.EndDisabled();

                        ShowRestartRequiredNotice(Settings.Instance.EnableGDIBackBufferCopyCompatibility != enableGDIBackBufferCopyCompatibility, AppStrings.GetLocalized("Settings_EnableGDIBackBufferCopyCompatibility_Label").TrimEnd(':', '：', ' '));
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_EnableGDIBackBufferCopyCompatibility_Label"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##EnableGDIBackBufferCopyCompatibility", ref enableGDIBackBufferCopyCompatibility);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextUnformatted(AppStrings.GetLocalized("Settings_GDIBackBufferCopyCompatibility_SubLabel"));
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_EnableGDIBackBufferCopyCompatibility_Desc"));
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_EnableGDIBackBufferCopyCompatibility_Note1"));
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_EnableGDIBackBufferCopyCompatibility_Note2"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.EndChild();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem($"{AppStrings.GetLocalized("Settings_Tab_Matchmaking")}###MatchmakingTab"))
                    {
                        var contentRegionAvail = ImGui.GetContentRegionAvail();
                        ImGui.BeginChild("##MatchmakingTabContent", new Vector2(contentRegionAvail.X, contentRegionAvail.Y - 56), ImGuiChildFlags.Borders);

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_Matchmaking"));
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_PlayNotificationSoundOnMatchmake"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##PlayNotificationSoundOnMatchmake", ref playNotificationSoundOnMatchmake);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_PlayNotificationSoundOnMatchmake_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.BeginDisabled(!playNotificationSoundOnMatchmake);
                        ImGui.Indent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_MatchmakeNotificationSoundPath"));
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 140 - ImGui.GetStyle().ItemSpacing.X);
                        ImGui.InputText("##MatchmakeNotificationSoundPath", ref matchmakeNotificationSoundPath, 1024);
                        ImGui.SameLine();
                        if (ImGui.Button(AppStrings.GetLocalized("Settings_Browse") + "##MatchmakeSoundPathBrowseBtn", new Vector2(140, 0)))
                        {
                            string defaultDir = File.Exists(matchmakeNotificationSoundPath) ? Path.GetDirectoryName(matchmakeNotificationSoundPath) : "";

                            ImFileBrowser.OpenFile((selectedFilePath)=>
                            {
                                System.Diagnostics.Debug.WriteLine($"MatchmakeNotificationSoundPath = {selectedFilePath}");
                                matchmakeNotificationSoundPath = selectedFilePath;
                            },
                            AppStrings.GetLocalized("Settings_SelectSoundFile"), defaultDir, "MP3 (*.mp3)|*.mp3|WAV (*.wav)|*.wav|All Files (*.*)|*.*", 0);
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_MatchmakeNotificationSoundPath_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_LoopNotificationSoundOnMatchmake"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##loopNotificationSoundOnMatchmake", ref loopNotificationSoundOnMatchmake);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_LoopNotificationSoundOnMatchmake_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_MatchmakeNotificationVolumeLevel"));
                        ImGui.SetNextItemWidth(-1);
                        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                        if (ImGui.SliderFloat("##MatchmakeNotificationVolume", ref matchmakeNotificationVolume, 0.02f, 3.0f, $"{(int)(matchmakeNotificationVolume * 100)}%%"))
                        {
                            matchmakeNotificationVolume = MathF.Round(matchmakeNotificationVolume, 2);
                        }
                        ImGui.PopStyleColor(2);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_MatchmakeNotificationVolumeLevel_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.Unindent();
                        ImGui.EndDisabled();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_PlayNotificationSoundOnReadyCheck"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##PlayNotificationSoundOnReadyCheck", ref playNotificationSoundOnReadyCheck);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_PlayNotificationSoundOnReadyCheck_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.BeginDisabled(!playNotificationSoundOnReadyCheck);
                        ImGui.Indent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ReadyCheckNotificationSoundPath"));
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 140 - ImGui.GetStyle().ItemSpacing.X);
                        ImGui.InputText("##ReadyCheckNotificationSoundPath", ref readyCheckNotificationSoundPath, 1024);
                        ImGui.SameLine();
                        if (ImGui.Button(AppStrings.GetLocalized("Settings_Browse") + "##ReadyCheckSoundPathBrowseBtn", new Vector2(140, 0)))
                        {
                            string defaultDir = File.Exists(readyCheckNotificationSoundPath) ? Path.GetDirectoryName(readyCheckNotificationSoundPath) : "";

                            ImFileBrowser.OpenFile((selectedFilePath) =>
                            {
                                System.Diagnostics.Debug.WriteLine($"ReadyCheckNotificationSoundPath = {selectedFilePath}");
                                readyCheckNotificationSoundPath = selectedFilePath;
                            },
                            AppStrings.GetLocalized("Settings_SelectSoundFile"), defaultDir, "MP3 (*.mp3)|*.mp3|WAV (*.wav)|*.wav|All Files (*.*)|*.*", 0);
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ReadyCheckNotificationSoundPath_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_LoopNotificationSoundOnReadyCheck"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##loopNotificationSoundOnReadyCheck", ref loopNotificationSoundOnReadyCheck);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_LoopNotificationSoundOnReadyCheck_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ReadyCheckNotificationVolumeLevel"));
                        ImGui.SetNextItemWidth(-1);
                        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                        if (ImGui.SliderFloat("##ReadyCheckNotificationVolume", ref readyCheckNotificationVolume, 0.02f, 3.0f, $"{(int)(readyCheckNotificationVolume * 100)}%%"))
                        {
                            readyCheckNotificationVolume = MathF.Round(readyCheckNotificationVolume, 2);
                        }
                        ImGui.PopStyleColor(2);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ReadyCheckNotificationVolumeLevel_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.Unindent();
                        ImGui.EndDisabled();

                        ImGui.EndChild();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem($"{AppStrings.GetLocalized("Settings_Tab_Integrations")}###IntegrationsTab"))
                    {
                        var contentRegionAvail = ImGui.GetContentRegionAvail();
                        ImGui.BeginChild("##IntegrationsTabContent", new Vector2(contentRegionAvail.X, contentRegionAvail.Y - 56), ImGuiChildFlags.Borders);

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_Integrations"));

                        ShowGenericImportantNotice(!useAutomaticWipeDetection, "AutoWipeDetectionDisabled", string.Format(AppStrings.GetLocalized("Settings_Notice_CurrentlyDisabledWarning_Format"), AppStrings.GetLocalized("Settings_UseAutomaticWipeDetection").TrimEnd(':', '：', ' ')));
                        ShowGenericImportantNotice(skipTeleportStateCheckInAutomaticWipeDetection, "SkipTeleportStateCheckInAutomaticWipeDetectionEnabled", string.Format(AppStrings.GetLocalized("Settings_Notice_CurrentlyEnabledWarning_Format"), AppStrings.GetLocalized("Settings_SkipTeleportStateCheckInAutomaticWipeDetection").TrimEnd(':', '：', ' ')));
                        ShowGenericImportantNotice(!splitEncountersOnNewPhases, "SplitEncountersOnNewPhasesDisabled", string.Format(AppStrings.GetLocalized("Settings_Notice_CurrentlyDisabledWarning_Format"), AppStrings.GetLocalized("Settings_SplitEncountersOnNewPhases").TrimEnd(':', '：', ' ')));

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_SaveEncounterReportToFile"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##SaveEncounterReportToFile", ref saveEncounterReportToFile);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_SaveEncounterReportToFile_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.BeginDisabled(!saveEncounterReportToFile);
                        ImGui.Indent();
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_ReportFileRetentionPolicy"));
                        ImGui.SameLine();
                        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                        ImGui.SetNextItemWidth(-1);
                        ImGui.SliderInt("##ReportFileRetentionPolicyDays", ref reportFileRetentionPolicyDays, 0, 30, reportFileRetentionPolicyDays == 0 ? AppStrings.GetLocalized("Settings_KeepForever") : string.Format(AppStrings.GetLocalized("Settings_DaysFormat"), reportFileRetentionPolicyDays));
                        ImGui.PopStyleColor(2);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ReportFileRetentionPolicy_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();
                        ImGui.Unindent();
                        ImGui.EndDisabled();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_MinimumPlayerCountToCreateReport"));
                        ImGui.SameLine();
                        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.55f));
                        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive, 0.55f));
                        ImGui.SetNextItemWidth(-1);
                        ImGui.SliderInt("##MinimumPlayerCountToCreateReport", ref minimumPlayerCountToCreateReport, 0, 20, minimumPlayerCountToCreateReport == 0 ? AppStrings.GetLocalized("Settings_Any") : string.Format(AppStrings.GetLocalized("Settings_PlayersFormat"), minimumPlayerCountToCreateReport));
                        ImGui.PopStyleColor(2);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_MinimumPlayerCountToCreateReport_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_AlwaysCreateReportAtDungeonEnd"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##AlwaysCreateReportAtDungeonEnd", ref alwaysCreateReportAtDungeonEnd);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_AlwaysCreateReportAtDungeonEnd_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_ZDPSReportWebhooks"));

                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(AppStrings.GetLocalized("Settings_WebhookMode"));
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(-1);

                        string reportsModeName = "";
                        switch (webhookReportsMode)
                        {
                            case EWebhookReportsMode.DiscordDeduplication:
                                reportsModeName = AppStrings.GetLocalized("Settings_WebhookMode_DiscordDeduplication");
                                break;
                            case EWebhookReportsMode.Discord:
                                reportsModeName = AppStrings.GetLocalized("Settings_WebhookMode_DiscordWebhook");
                                break;
                            case EWebhookReportsMode.Custom:
                                reportsModeName = AppStrings.GetLocalized("Settings_WebhookMode_CustomUrl");
                                break;
                            case EWebhookReportsMode.FallbackDiscordDeduplication:
                                reportsModeName = AppStrings.GetLocalized("Settings_WebhookMode_FallbackDiscordDeduplication");
                                break;
                        }

                        if (ImGui.BeginCombo("##WebhookMode", $"{reportsModeName}", ImGuiComboFlags.None))
                        {
                            if (ImGui.Selectable(AppStrings.GetLocalized("Settings_WebhookMode_DiscordDeduplication")))
                            {
                                webhookReportsMode = EWebhookReportsMode.DiscordDeduplication;
                            }
                            ImGui.SetItemTooltip(AppStrings.GetLocalized("Settings_WebhookMode_DiscordDeduplication_Tooltip"));
                            if (ImGui.Selectable(AppStrings.GetLocalized("Settings_WebhookMode_DiscordWebhook")))
                            {
                                webhookReportsMode = EWebhookReportsMode.Discord;
                            }
                            ImGui.SetItemTooltip(AppStrings.GetLocalized("Settings_WebhookMode_DiscordWebhook_Tooltip"));
                            if (ImGui.Selectable(AppStrings.GetLocalized("Settings_WebhookMode_CustomUrl")))
                            {
                                webhookReportsMode = EWebhookReportsMode.Custom;
                            }
                            ImGui.SetItemTooltip(AppStrings.GetLocalized("Settings_WebhookMode_CustomUrl_Tooltip"));
                            if (ImGui.Selectable(AppStrings.GetLocalized("Settings_WebhookMode_FallbackDiscordDeduplication")))
                            {
                                webhookReportsMode = EWebhookReportsMode.FallbackDiscordDeduplication;
                            }
                            ImGui.SetItemTooltip(AppStrings.GetLocalized("Settings_WebhookMode_FallbackDiscordDeduplication_Tooltip"));
                            ImGui.EndCombo();
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_WebhookMode_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        // TODO: Maybe allow adding multiple Webhooks and toggling the enabled state of each one (should allow entering a friendly name next to them too)

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(string.Format(AppStrings.GetLocalized("Settings_SendEncounterReportsTo_Format"), reportsModeName));
                        ImGui.SameLine();
                        ImGui.Checkbox("##WebhookReportsEnabled", ref webhookReportsEnabled);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(string.Format(AppStrings.GetLocalized("Settings_WebhookReportsEnabled_Desc_Format"), reportsModeName));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.BeginDisabled(!webhookReportsEnabled);
                        ImGui.Indent();

                        switch (webhookReportsMode)
                        {
                            case EWebhookReportsMode.DiscordDeduplication:
                            case EWebhookReportsMode.Discord:
                            case EWebhookReportsMode.FallbackDiscordDeduplication:
                                if (webhookReportsMode == EWebhookReportsMode.DiscordDeduplication || webhookReportsMode == EWebhookReportsMode.FallbackDiscordDeduplication)
                                {
                                    ImGui.AlignTextToFramePadding();
                                    ImGui.Text(AppStrings.GetLocalized("Settings_DeduplicationServerURL"));
                                    ImGui.SameLine();
                                    ImGui.SetNextItemWidth(-1);
                                    ImGui.InputText("##WebhookReportsDeduplicationServerHost", ref webhookReportsDeduplicationServerUrl, 512);
                                    ImGui.Indent();
                                    ImGui.BeginDisabled(true);
                                    ImGui.TextWrapped(AppStrings.GetLocalized("Settings_DeduplicationServerURL_Desc"));
                                    if (webhookReportsMode == EWebhookReportsMode.FallbackDiscordDeduplication)
                                    {
                                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_DeduplicationServerURL_FallbackNote"));
                                    }
                                    ImGui.EndDisabled();
                                    ImGui.Unindent();
                                }

                                ImGui.AlignTextToFramePadding();
                                ImGui.Text(AppStrings.GetLocalized("Settings_WebhookURL"));
                                ImGui.SameLine();
                                ImGui.SetNextItemWidth(-1);
                                if (ImGui.InputText("##WebhookReportsDiscordUrl", ref webhookReportsDiscordUrl, 512))
                                {
                                    if (Utils.SplitAndValidateDiscordWebhook(webhookReportsDiscordUrl) != null)
                                    {
                                        IsDiscordWebhookUrlValid = true;
                                    }
                                    else
                                    {
                                        IsDiscordWebhookUrlValid = false;
                                    }
                                }

                                if (!IsDiscordWebhookUrlValid)
                                {
                                    ImGui.Indent();
                                    ImGui.BeginDisabled(true);
                                    ImGui.PushStyleColor(ImGuiCol.Text, Colors.Red);
                                    ImGui.TextWrapped(AppStrings.GetLocalized("Settings_WebhookURL_InvalidNotice"));
                                    ImGui.PopStyleColor();
                                    ImGui.EndDisabled();
                                    ImGui.Unindent();
                                }

                                ImGui.Indent();
                                ImGui.BeginDisabled(true);
                                ImGui.TextWrapped(AppStrings.GetLocalized("Settings_WebhookURL_Desc"));
                                ImGui.EndDisabled();
                                ImGui.Unindent();
                                break;
                            case EWebhookReportsMode.Custom:
                                ImGui.AlignTextToFramePadding();
                                ImGui.Text(AppStrings.GetLocalized("Settings_WebhookURL"));
                                ImGui.SameLine();
                                ImGui.SetNextItemWidth(-1);
                                ImGui.InputText("##WebhookReportsCustomUrl", ref webhookReportsCustomUrl, 512);
                                ImGui.Indent();
                                ImGui.BeginDisabled(true);
                                ImGui.TextWrapped(AppStrings.GetLocalized("Settings_WebhookURL_Custom_Desc"));
                                ImGui.EndDisabled();
                                ImGui.Unindent();
                                break;
                        }

                        ImGui.Unindent();
                        ImGui.EndDisabled();

                        if (ImGui.CollapsingHeader("BPTimer", ImGuiTreeNodeFlags.DefaultOpen))
                        {
                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_BPTimer_Enabled"));
                            ImGui.SameLine();
                            ImGui.Checkbox("##ExternalBPTimerEnabled", ref externalBPTimerEnabled);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_BPTimer_Enabled_Desc"));
                            bool hasBPTimerReports = externalBPTimerFieldBossHpReportsEnabled;
                            if (!hasBPTimerReports)
                            {
                                ImGui.PushStyleColor(ImGuiCol.Text, Colors.Red);
                            }
                            else
                            {
                                ImGui.PushStyleColor(ImGuiCol.Text, Colors.Green);
                            }
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_BPTimer_Enabled_Note"));
                            ImGui.PopStyleColor();

                            ImGui.EndDisabled();
                            if (ImGui.CollapsingHeader($"{AppStrings.GetLocalized("Settings_Section_DataCollection")}###BPTimerDataCollectionSection"))
                            {
                                ImGui.Indent();
                                ImGui.TextUnformatted(AppStrings.GetLocalized("Settings_BPTimerDataCollection_Intro"));
                                ImGui.BulletText(AppStrings.GetLocalized("Settings_BPTimerData_BossInfo"));
                                ImGui.BulletText(AppStrings.GetLocalized("Settings_BPTimerData_LineNumber"));
                                ImGui.BulletText(AppStrings.GetLocalized("Settings_BPTimerData_AccountId"));
                                ImGui.SetItemTooltip(AppStrings.GetLocalized("Settings_BPTimerData_AccountId_Tooltip"));
                                ImGui.BulletText(AppStrings.GetLocalized("Settings_BPTimerData_ServerAddress"));
                                ImGui.BulletText(AppStrings.GetLocalized("Settings_BPTimerData_CharacterUid"));
                                ImGui.BulletText(AppStrings.GetLocalized("Settings_BPTimerData_CharacterName"));
                                ImGui.BulletText(AppStrings.GetLocalized("Settings_BPTimerData_YourIpAddress"));
                                ImGui.Unindent();
                            }
                            ImGui.Unindent();

                            ImGui.BeginDisabled(!externalBPTimerEnabled);
                            ImGui.Indent();

                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_IncludeOwnCharacterDataInReport"));
                            ImGui.SameLine();
                            ImGui.Checkbox("##ExternalBPTimerIncludeCharacterId", ref externalBPTimerIncludeCharacterId);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_IncludeOwnCharacterDataInReport_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.AlignTextToFramePadding();
                            ImGui.Text(AppStrings.GetLocalized("Settings_BPTimerFieldBossHPReports"));
                            ImGui.SameLine();
                            ImGui.Checkbox("##ExternalBPTimerFieldBossHpReportsEnabled", ref externalBPTimerFieldBossHpReportsEnabled);
                            ImGui.Indent();
                            ImGui.BeginDisabled(true);
                            ImGui.TextWrapped(AppStrings.GetLocalized("Settings_BPTimerFieldBossHPReports_Desc"));
                            ImGui.EndDisabled();
                            ImGui.Unindent();

                            ImGui.Unindent();
                            ImGui.EndDisabled();
                        }

                        ImGui.EndChild();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem($"{AppStrings.GetLocalized("Settings_Tab_Development")}###DevelopmentTab"))
                    {
                        var contentRegionAvail = ImGui.GetContentRegionAvail();
                        ImGui.BeginChild("##DevelopmentTabContent", new Vector2(contentRegionAvail.X, contentRegionAvail.Y - 56), ImGuiChildFlags.Borders);

                        ImGui.SeparatorText(AppStrings.GetLocalized("Settings_Section_Development"));
                        if (ImGui.Button($"{AppStrings.GetLocalized("Settings_ReloadDataTablesBtn")}###ReloadDataTablesBtn"))
                        {
                            AppState.LoadDataTables();
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ReloadDataTablesBtn_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        if (ImGui.Button($"{AppStrings.GetLocalized("Settings_RestartCaptureBtn")}###RestartCaptureBtn"))
                        {
                            MessageManager.StopCapturing();
                            MessageManager.InitializeCapturing();
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_RestartCaptureBtn_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        if (ImGui.Button($"{AppStrings.GetLocalized("Settings_ReloadModuleSaveBtn")}###ReloadModuleSaveBtn"))
                        {
                            ModuleSolver.Init();
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_ReloadModuleSaveBtn_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ShowRestartRequiredNotice(Settings.Instance.LogToFile != logToFile, AppStrings.GetLocalized("Settings_LogToFile_Label").TrimEnd(':', '：', ' '));
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_LogToFile_Label"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##LogToFile", ref logToFile);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_LogToFile_Desc"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ShowRestartRequiredNotice(Settings.Instance.AggressiveExceptionDebugLogging != aggressiveExceptionDebugLogging, AppStrings.GetLocalized("Settings_AggressiveExceptionDebugLogging").TrimEnd(':', '：', ' '));
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(AppStrings.GetLocalized("Settings_AggressiveExceptionDebugLogging"));
                        ImGui.SameLine();
                        ImGui.Checkbox("##AggressiveExceptionDebugLogging", ref aggressiveExceptionDebugLogging);
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_AggressiveExceptionDebugLogging_Desc"));
                        ImGui.TextWrapped(AppStrings.GetLocalized("Settings_AggressiveExceptionDebugLogging_Note"));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        if (ImGui.Button($"{AppStrings.GetLocalized("Settings_OpenGitHubProjectPageBtn")}###OpenGitHubProjectPageBtn"))
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                            {
                                FileName = Settings.Instance.ZDPSWebsiteURL,
                                UseShellExecute = true,
                            });
                        }
                        ImGui.Indent();
                        ImGui.BeginDisabled(true);
                        ImGui.TextWrapped(string.Format(AppStrings.GetLocalized("Settings_OpenGitHubProjectPageBtn_Desc_Format"), Settings.Instance.ZDPSWebsiteURL));
                        ImGui.EndDisabled();
                        ImGui.Unindent();

                        ImGui.EndChild();
                        ImGui.EndTabItem();
                    }
                    
                    ImGui.EndTabBar();
                }

                ImGui.NewLine();
                float buttonWidth = 120;
                ImGui.PushStyleColor(ImGuiCol.Button, Colors.DarkGreen_Transparent);
                if (ImGui.Button(AppStrings.GetLocalized("Settings_SaveBtn"), new Vector2(buttonWidth, 0)))
                {
                    Save(mainWindow);

                    ImGui.CloseCurrentPopup();
                }
                ImGui.PopStyleColor();

                ImGui.SameLine();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - buttonWidth);
                ImGui.PushStyleColor(ImGuiCol.Button, Colors.DarkRed_Transparent);
                if (ImGui.Button(AppStrings.GetLocalized("Settings_CloseBtn"), new Vector2(buttonWidth, 0)))
                {
                    SelectedNetworkDeviceIdx = PreviousSelectedNetworkDeviceIdx;

                    Load();

                    EncounterResetKey = Settings.Instance.HotkeysEncounterReset;
                    if (EncounterResetKey == 0)
                    {
                        EncounterResetKeyName = "[UNBOUND]";
                    }
                    else
                    {
                        EncounterResetKeyName = ImGui.GetKeyNameS(HotKeyManager.VirtualKeyToImGuiKey((int)EncounterResetKey));
                    }

                    PinnedWindowClickthroughKey = Settings.Instance.HotkeysPinnedWindowClickthrough;
                    if (PinnedWindowClickthroughKey == 0)
                    {
                        PinnedWindowClickthroughKeyName = "[UNBOUND]";
                    }
                    else
                    {
                        PinnedWindowClickthroughKeyName = ImGui.GetKeyNameS(HotKeyManager.VirtualKeyToImGuiKey((int)PinnedWindowClickthroughKey));
                    }

                    ToggleWindowMinimizeKey = Settings.Instance.HotkeysToggleWindowMinimize;
                    if (ToggleWindowMinimizeKey == 0)
                    {
                        ToggleWindowMinimizeKeyName = "[UNBOUND]";
                    }
                    else
                    {
                        ToggleWindowMinimizeKeyName = ImGui.GetKeyNameS(HotKeyManager.VirtualKeyToImGuiKey((int)ToggleWindowMinimizeKey));
                    }

                    RegisterAllHotkeys(mainWindow);

                    ImGui.CloseCurrentPopup();
                }
                ImGui.PopStyleColor();

                ImFileBrowser.Draw();

                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        private static void Load()
        {
            Language = Settings.Instance.Language;

            normalizeMeterContributions = Settings.Instance.NormalizeMeterContributions;
            useShortWidthNumberFormatting = Settings.Instance.UseShortWidthNumberFormatting;
            showClassIconsInMeters = Settings.Instance.ShowClassIconsInMeters;
            colorClassIconsByRole = Settings.Instance.ColorClassIconsByRole;
            showSkillIconsInDetails = Settings.Instance.ShowSkillIconsInDetails;
            onlyShowDamageContributorsInMeters = Settings.Instance.OnlyShowDamageContributorsInMeters;
            onlyShowPartyMembersInMeters = Settings.Instance.OnlyShowPartyMembersInMeters;
            showAbilityScoreInMeters = Settings.Instance.ShowAbilityScoreInMeters;
            showSeasonStrengthInMeters = Settings.Instance.ShowSeasonStrengthInMeters;
            showSubProfessionNameInMeters = Settings.Instance.ShowSubProfessionNameInMeters;
            showPlayerSummonsInMeters = Settings.Instance.ShowPlayerSummonsInMeters;
            showPlayerImaginesInMeters = Settings.Instance.ShowPlayerImaginesInMeters;
            useAutomaticWipeDetection = Settings.Instance.UseAutomaticWipeDetection;
            skipTeleportStateCheckInAutomaticWipeDetection = Settings.Instance.SkipTeleportStateCheckInAutomaticWipeDetection;
            disableWipeRecalculationOverwriting = Settings.Instance.DisableWipeRecalculationOverwriting;
            useLegacyWipeDetection = Settings.Instance.UseLegacyWipeDetection;
            splitEncountersOnNewPhases = Settings.Instance.SplitEncountersOnNewPhases;
            displayTruePerSecondValuesInMeters = Settings.Instance.DisplayTruePerSecondValuesInMeters;
            hideEncounterPerSecondValuesInMeters = Settings.Instance.HideEncounterPerSecondValuesInMeters;
            allowGamepadNavigationInputInZDPS = Settings.Instance.AllowGamepadNavigationInputInZDPS;
            keepPastEncounterInMeterUntilNextDamage = Settings.Instance.KeepPastEncounterInMeterUntilNextDamage;
            showChannelLineNumberInStatus = Settings.Instance.ShowChannelLineNumberInStatus;
            showCallWipeForEncounterOnMainWindow = Settings.Instance.ShowCallWipeForEncounterOnMainWindow;

            useDatabaseForEncounterHistory = Settings.Instance.UseDatabaseForEncounterHistory;
            databaseRetentionPolicyDays = Settings.Instance.DatabaseRetentionPolicyDays;
            skipSavingEncountersWithNoCombatData = Settings.Instance.SkipSavingEncountersWithNoCombatData;
            limitEncounterBuffTrackingInOpenWorld = Settings.Instance.LimitEncounterBuffTrackingInOpenWorld;
            skipSkillSnapshotSavingInOpenWorld = Settings.Instance.SkipSkillSnapshotSavingInOpenWorld;
            allowEncounterSavingPausingInOpenWorld = Settings.Instance.AllowEncounterSavingPausingInOpenWorld;
            persistEncounterSavingPauseStateBetweenMaps = Settings.Instance.PersistEncounterSavingPauseStateBetweenMaps;
            minimalProcessingWhileEncounterSavingPaused = Settings.Instance.MinimalProcessingWhileEncounterSavingPaused;

            includeHealEventsOutsideOfCombat = Settings.Instance.IncludeHealEventsOutsideOfCombat;

            meterSettingsTankingShowDeaths = Settings.Instance.MeterSettingsTankingShowDeaths;
            meterSettingsNpcTakenShowHpData = Settings.Instance.MeterSettingsNpcTakenShowHpData;
            meterSettingsNpcTakenHideMaxHp = Settings.Instance.MeterSettingsNpcTakenHideMaxHp;
            meterSettingsNpcTakenUseHpMeter = Settings.Instance.MeterSettingsNpcTakenUseHpMeter;

            GameCapturePreference = Settings.Instance.GameCapturePreference;
            gameCaptureCustomExeName = Settings.Instance.GameCaptureCustomExeName;

            playNotificationSoundOnMatchmake = Settings.Instance.PlayNotificationSoundOnMatchmake;
            matchmakeNotificationSoundPath = Settings.Instance.MatchmakeNotificationSoundPath;
            loopNotificationSoundOnMatchmake = Settings.Instance.LoopNotificationSoundOnMatchmake;
            matchmakeNotificationVolume = Settings.Instance.MatchmakeNotificationVolume;

            playNotificationSoundOnReadyCheck = Settings.Instance.PlayNotificationSoundOnReadyCheck;
            readyCheckNotificationSoundPath = Settings.Instance.ReadyCheckNotificationSoundPath;
            loopNotificationSoundOnReadyCheck = Settings.Instance.LoopNotificationSoundOnReadyCheck;
            readyCheckNotificationVolume = Settings.Instance.ReadyCheckNotificationVolume;

            saveEncounterReportToFile = Settings.Instance.SaveEncounterReportToFile;
            reportFileRetentionPolicyDays = Settings.Instance.ReportFileRetentionPolicyDays;
            minimumPlayerCountToCreateReport = Settings.Instance.MinimumPlayerCountToCreateReport;
            alwaysCreateReportAtDungeonEnd = Settings.Instance.AlwaysCreateReportAtDungeonEnd;
            webhookReportsEnabled = Settings.Instance.WebhookReportsEnabled;
            webhookReportsMode = Settings.Instance.WebhookReportsMode;
            webhookReportsDeduplicationServerUrl = Settings.Instance.WebhookReportsDeduplicationServerHost;
            webhookReportsDiscordUrl = Settings.Instance.WebhookReportsDiscordUrl;
            webhookReportsCustomUrl = Settings.Instance.WebhookReportsCustomUrl;

            checkForZDPSUpdatesOnStartup = Settings.Instance.CheckForZDPSUpdatesOnStartup;
            latestZDPSVersionCheckURL = Settings.Instance.LatestZDPSVersionCheckURL;

            windowSettings = (WindowSettings)Settings.Instance.WindowSettings.Clone();

            logToFile = Settings.Instance.LogToFile;

            lowPerformanceMode = Settings.Instance.LowPerformanceMode;
            fixedFramerate = (int)Settings.Instance.FixedFramerateScale;

            enableGDIBackBufferCopyCompatibility = Settings.Instance.EnableGDIBackBufferCopyCompatibility;

            aggressiveExceptionDebugLogging = Settings.Instance.AggressiveExceptionDebugLogging;

            // External
            externalBPTimerEnabled = Settings.Instance.External.BPTimerSettings.ExternalBPTimerEnabled;
            externalBPTimerIncludeCharacterId = Settings.Instance.External.BPTimerSettings.ExternalBPTimerIncludeCharacterId;
            externalBPTimerFieldBossHpReportsEnabled = Settings.Instance.External.BPTimerSettings.ExternalBPTimerFieldBossHpReportsEnabled;
        }

        private static void Save(MainWindow mainWindow)
        {
            var io = ImGui.GetIO();
            if (SelectedNetworkDeviceIdx != PreviousSelectedNetworkDeviceIdx || GameCapturePreference != Settings.Instance.GameCapturePreference)
            {
                PreviousSelectedNetworkDeviceIdx = SelectedNetworkDeviceIdx;

                MessageManager.StopCapturing();

                Settings.Instance.NetCaptureDeviceName = NetworkDevices[SelectedNetworkDeviceIdx].Name;
                MessageManager.NetCaptureDeviceName = NetworkDevices[SelectedNetworkDeviceIdx].Name;

                Settings.Instance.GameCapturePreference = GameCapturePreference;
                Settings.Instance.GameCaptureCustomExeName = gameCaptureCustomExeName;

                MessageManager.InitializeCapturing();
            }
            if (allowGamepadNavigationInputInZDPS)
            {
                io.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
            }
            else
            {
                io.ConfigFlags &= ~ImGuiConfigFlags.NavEnableGamepad;
            }

            Settings.Instance.AllowEncounterSavingPausingInOpenWorld = allowEncounterSavingPausingInOpenWorld;
            if (!allowEncounterSavingPausingInOpenWorld)
            {
                AppState.IsEncounterSavingPaused = false;
                AppState.WasEncounterSavingPaused = false;
            }

            if (Settings.Instance.Language != Language)
            {
                Settings.Instance.Language = Language;
                AppState.LoadAppStringsTable();
                AppState.LoadSkillOverridesTable();
                AppState.LoadBuffOverridesTable();
            }

            Settings.Instance.NormalizeMeterContributions = normalizeMeterContributions;
            Settings.Instance.UseShortWidthNumberFormatting = useShortWidthNumberFormatting;
            Settings.Instance.ShowClassIconsInMeters = showClassIconsInMeters;
            Settings.Instance.ColorClassIconsByRole = colorClassIconsByRole;
            Settings.Instance.ShowSkillIconsInDetails = showSkillIconsInDetails;
            Settings.Instance.OnlyShowDamageContributorsInMeters = onlyShowDamageContributorsInMeters;
            Settings.Instance.OnlyShowPartyMembersInMeters = onlyShowPartyMembersInMeters;
            Settings.Instance.ShowAbilityScoreInMeters = showAbilityScoreInMeters;
            Settings.Instance.ShowSeasonStrengthInMeters = showSeasonStrengthInMeters;
            Settings.Instance.ShowSubProfessionNameInMeters = showSubProfessionNameInMeters;
            Settings.Instance.ShowPlayerSummonsInMeters = showPlayerSummonsInMeters;
            Settings.Instance.ShowPlayerImaginesInMeters = showPlayerImaginesInMeters;
            Settings.Instance.UseAutomaticWipeDetection = useAutomaticWipeDetection;
            Settings.Instance.SkipTeleportStateCheckInAutomaticWipeDetection = skipTeleportStateCheckInAutomaticWipeDetection;
            Settings.Instance.DisableWipeRecalculationOverwriting = disableWipeRecalculationOverwriting;
            Settings.Instance.UseLegacyWipeDetection = useLegacyWipeDetection;
            Settings.Instance.SplitEncountersOnNewPhases = splitEncountersOnNewPhases;
            Settings.Instance.DisplayTruePerSecondValuesInMeters = displayTruePerSecondValuesInMeters;
            Settings.Instance.HideEncounterPerSecondValuesInMeters = hideEncounterPerSecondValuesInMeters;
            Settings.Instance.AllowGamepadNavigationInputInZDPS = allowGamepadNavigationInputInZDPS;
            Settings.Instance.KeepPastEncounterInMeterUntilNextDamage = keepPastEncounterInMeterUntilNextDamage;
            Settings.Instance.ShowChannelLineNumberInStatus = showChannelLineNumberInStatus;
            Settings.Instance.ShowCallWipeForEncounterOnMainWindow = showCallWipeForEncounterOnMainWindow;

            Settings.Instance.UseDatabaseForEncounterHistory = useDatabaseForEncounterHistory;
            Settings.Instance.DatabaseRetentionPolicyDays = databaseRetentionPolicyDays;
            Settings.Instance.SkipSavingEncountersWithNoCombatData = skipSavingEncountersWithNoCombatData;
            Settings.Instance.LimitEncounterBuffTrackingInOpenWorld = limitEncounterBuffTrackingInOpenWorld;
            Settings.Instance.SkipSkillSnapshotSavingInOpenWorld = skipSkillSnapshotSavingInOpenWorld;
            Settings.Instance.PersistEncounterSavingPauseStateBetweenMaps = persistEncounterSavingPauseStateBetweenMaps;
            Settings.Instance.MinimalProcessingWhileEncounterSavingPaused = minimalProcessingWhileEncounterSavingPaused;

            Settings.Instance.IncludeHealEventsOutsideOfCombat = includeHealEventsOutsideOfCombat;

            Settings.Instance.MeterSettingsTankingShowDeaths = meterSettingsTankingShowDeaths;
            Settings.Instance.MeterSettingsNpcTakenShowHpData = meterSettingsNpcTakenShowHpData;
            Settings.Instance.MeterSettingsNpcTakenHideMaxHp = meterSettingsNpcTakenHideMaxHp;
            Settings.Instance.MeterSettingsNpcTakenUseHpMeter = meterSettingsNpcTakenUseHpMeter;

            Settings.Instance.PlayNotificationSoundOnMatchmake = playNotificationSoundOnMatchmake;
            Settings.Instance.MatchmakeNotificationSoundPath = matchmakeNotificationSoundPath;
            Settings.Instance.LoopNotificationSoundOnMatchmake = loopNotificationSoundOnMatchmake;
            Settings.Instance.MatchmakeNotificationVolume = matchmakeNotificationVolume;

            Settings.Instance.PlayNotificationSoundOnReadyCheck = playNotificationSoundOnReadyCheck;
            Settings.Instance.ReadyCheckNotificationSoundPath = readyCheckNotificationSoundPath;
            Settings.Instance.LoopNotificationSoundOnReadyCheck = loopNotificationSoundOnReadyCheck;
            Settings.Instance.ReadyCheckNotificationVolume = readyCheckNotificationVolume;

            Settings.Instance.SaveEncounterReportToFile = saveEncounterReportToFile;
            Settings.Instance.ReportFileRetentionPolicyDays = reportFileRetentionPolicyDays;
            Settings.Instance.MinimumPlayerCountToCreateReport = minimumPlayerCountToCreateReport;
            Settings.Instance.AlwaysCreateReportAtDungeonEnd = alwaysCreateReportAtDungeonEnd;
            Settings.Instance.WebhookReportsEnabled = webhookReportsEnabled;
            Settings.Instance.WebhookReportsMode = webhookReportsMode;
            Settings.Instance.WebhookReportsDeduplicationServerHost = webhookReportsDeduplicationServerUrl;
            Settings.Instance.WebhookReportsDiscordUrl = webhookReportsDiscordUrl;
            Settings.Instance.WebhookReportsCustomUrl = webhookReportsCustomUrl;

            Settings.Instance.CheckForZDPSUpdatesOnStartup = checkForZDPSUpdatesOnStartup;
            Settings.Instance.LatestZDPSVersionCheckURL = latestZDPSVersionCheckURL;

            Settings.Instance.WindowSettings = (WindowSettings)windowSettings.Clone();

            Settings.Instance.LogToFile = logToFile;

            Settings.Instance.LowPerformanceMode = lowPerformanceMode;
            Settings.Instance.FixedFramerateScale = (uint)fixedFramerate;

            Settings.Instance.EnableGDIBackBufferCopyCompatibility = enableGDIBackBufferCopyCompatibility;
            RendererImpl.EnableGDIBackBufferCopyCompatibility = enableGDIBackBufferCopyCompatibility;

            Settings.Instance.AggressiveExceptionDebugLogging = aggressiveExceptionDebugLogging;

            // External
            Settings.Instance.External.BPTimerSettings.ExternalBPTimerEnabled = externalBPTimerEnabled;
            Settings.Instance.External.BPTimerSettings.ExternalBPTimerIncludeCharacterId = externalBPTimerIncludeCharacterId;
            Settings.Instance.External.BPTimerSettings.ExternalBPTimerFieldBossHpReportsEnabled = externalBPTimerFieldBossHpReportsEnabled;

            RegisterAllHotkeys(mainWindow);

            DB.Init();

            // Write out the new settings to file now that they've been applied
            Settings.Save();

            if (externalBPTimerEnabled && externalBPTimerFieldBossHpReportsEnabled)
            {
                // Attempt to update our supported mob list with data from the BPTimer server
                Managers.External.BPTimerManager.FetchSupportedMobList();
            }
        }

        static void ShowRestartRequiredNotice(bool showCondition, string settingName)
        {
            if (showCondition)
            {
                ImGui.PushStyleColor(ImGuiCol.ChildBg, Colors.Red_Transparent);
                ImGui.BeginChild($"##RestartRequiredNotice_{settingName}", new Vector2(0, 0), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.Borders);
                ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], ImGui.GetFontSize());
                ImGui.TextUnformatted(AppStrings.GetLocalized("Settings_ImportantNoteLabel"));
                ImGui.PopFont();
                ImGui.TextWrapped(string.Format(AppStrings.GetLocalized("Settings_RestartRequiredNotice_Format"), settingName));
                ImGui.EndChild();
                ImGui.PopStyleColor();
            }
        }

        static void ShowGenericImportantNotice(bool showCondition, string uniqueName, string text)
        {
            if (showCondition)
            {
                ImGui.PushStyleColor(ImGuiCol.ChildBg, Colors.Red_Transparent);
                ImGui.BeginChild($"##GenericImportantNotice_{uniqueName}", new Vector2(0, 0), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.Borders);
                ImGui.PushFont(HelperMethods.Fonts["Segoe-Bold"], ImGui.GetFontSize());
                ImGui.TextUnformatted(AppStrings.GetLocalized("Settings_ImportantNoteLabel"));
                ImGui.PopFont();
                ImGui.TextWrapped($"{text}");
                ImGui.EndChild();
                ImGui.PopStyleColor();
            }
        }

        static void LoadHotkeys()
        {
            EncounterResetKey = Settings.Instance.HotkeysEncounterReset;
            if (EncounterResetKey == 0)
            {
                EncounterResetKeyName = "[UNBOUND]";
            }
            else
            {
                EncounterResetKeyName = ImGui.GetKeyNameS(HotKeyManager.VirtualKeyToImGuiKey((int)EncounterResetKey));
            }

            PinnedWindowClickthroughKey = Settings.Instance.HotkeysPinnedWindowClickthrough;
            if (PinnedWindowClickthroughKey == 0)
            {
                PinnedWindowClickthroughKeyName = "[UNBOUND]";
            }
            else
            {
                PinnedWindowClickthroughKeyName = ImGui.GetKeyNameS(HotKeyManager.VirtualKeyToImGuiKey((int)PinnedWindowClickthroughKey));
            }

            ToggleWindowMinimizeKey = Settings.Instance.HotkeysToggleWindowMinimize;
            if (ToggleWindowMinimizeKey == 0)
            {
                ToggleWindowMinimizeKeyName = "[UNBOUND]";
            }
            else
            {
                ToggleWindowMinimizeKeyName = ImGui.GetKeyNameS(HotKeyManager.VirtualKeyToImGuiKey((int)ToggleWindowMinimizeKey));
            }
        }

        static void RegisterAllHotkeys(MainWindow mainWindow)
        {
            if (EncounterResetKey != 0)// && EncounterResetKey != Settings.Instance.HotkeysEncounterReset)
            {
                HotKeyManager.RegisterKey("EncounterReset", mainWindow.CreateNewEncounter, EncounterResetKey);
            }
            Settings.Instance.HotkeysEncounterReset = EncounterResetKey;

            if (PinnedWindowClickthroughKey != 0)
            {
                HotKeyManager.RegisterKey("PinnedWindowClickthrough", mainWindow.ToggleMouseClickthrough, PinnedWindowClickthroughKey);
            }
            Settings.Instance.HotkeysPinnedWindowClickthrough = PinnedWindowClickthroughKey;

            if (ToggleWindowMinimizeKey != 0)
            {
                HotKeyManager.RegisterKey("ToggleWindowMinimize", mainWindow.ToggleWindowMinimize, ToggleWindowMinimizeKey);
            }
            Settings.Instance.HotkeysToggleWindowMinimize = ToggleWindowMinimizeKey;
        }

        public static void RebindKeyButton(string bindingName, ref uint bindingVariable, ref string bindingVariableName, ref bool bindingState)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"{bindingName}:");

            string bindDisplay = "[UNBOUND]";

            if (bindingState == true)
            {
                for (uint key = (uint)ImGuiKey.NamedKeyBegin; key < (uint)ImGuiKey.NamedKeyEnd; key++)
                {
                    if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                    {
                        bindingState = false;
                    }
                    else if (ImGui.IsKeyPressed((ImGuiKey)key))
                    {
                        ImGuiKey[] blacklistedKeys =
                            [
                            ImGuiKey.ModAlt, ImGuiKey.LeftAlt, ImGuiKey.RightAlt, ImGuiKey.ReservedForModAlt,
                            ImGuiKey.ModCtrl, ImGuiKey.LeftCtrl, ImGuiKey.RightCtrl, ImGuiKey.ReservedForModCtrl,
                            ImGuiKey.ModShift, ImGuiKey.LeftShift, ImGuiKey.RightShift, ImGuiKey.ReservedForModShift,
                            ImGuiKey.ModMask, ImGuiKey.ModSuper, ImGuiKey.LeftSuper, ImGuiKey.RightSuper, ImGuiKey.ReservedForModSuper,
                            ImGuiKey.MouseLeft, ImGuiKey.MouseMiddle, ImGuiKey.MouseRight, ImGuiKey.MouseWheelX, ImGuiKey.MouseWheelY,
                            ImGuiKey.Escape, ImGuiKey.F12
                            ];
                        
                        if (!blacklistedKeys.Contains((ImGuiKey)key))
                        {
                            string keyName = ImGui.GetKeyNameS((ImGuiKey)key);
                            bindingVariable = (uint)HotKeyManager.ImGuiKeyToVirtualKey((ImGuiKey)key);
                            bindingVariableName = keyName;
                            bindingState = false;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(bindingVariableName))
            {
                bindDisplay = bindingVariableName;
            }
            ImGui.SameLine();
            bool isInBindingState = bindingState;

            if (isInBindingState)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered]);
            }
            if (ImGui.Button($"{bindDisplay}##BindBtn_{bindingName}", new Vector2(120, 0)))
            {
                bindingState = true;
            }
            if (isInBindingState)
            {
                ImGui.PopStyleColor();
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(bindingVariable == 0);
            if (ImGui.Button($"X##ClearBindingBtn_{bindingName}"))
            {
                bindingVariable = 0;
                bindingVariableName = "";
                bindingState = false;
            }
            ImGui.EndDisabled();
            ImGui.SetItemTooltip(AppStrings.GetLocalized("Settings_ClearKeybindingBtn_Tooltip"));
        }

        public static void RecalculateRefreshRates()
        {
            allowedSyncRates.Clear();
            var glfwMonitor = Hexa.NET.GLFW.GLFW.GetPrimaryMonitor();
            var glfwVidMode = Hexa.NET.GLFW.GLFW.GetVideoMode(glfwMonitor);
            for (int i = 1; i < 5; i++)
            {
                float syncRate = (float)glfwVidMode.RefreshRate / (float)i;
                if (syncRate >= 35.0f)
                {
                    allowedSyncRates.Add(i, MathF.Round(syncRate, 2));
                }
            }

            if (allowedSyncRates.Count == 0)
            {
                allowedSyncRates.Add(1, glfwVidMode.RefreshRate);
            }
        }
    }
}
