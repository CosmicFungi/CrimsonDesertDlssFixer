using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CrimsonDesertDlssFixer.Models;

namespace CrimsonDesertDlssFixer.Services;

public sealed class DiagnosticEngine
{
    private readonly NvApiDrsService _nvapi = new();
    private readonly PearlAbyssConfigService _pearlAbyss = new();
    private readonly GameDirectoryService _gameDir = new();
    private readonly RhiAndSystemService _rhiSystem = new();

    public static string BackupsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrimsonDesertDlssFixer", "Backups");

    public ScanResult RunFullScan(string? overrideGamePath = null)
    {
        var issues = new List<DiagnosticIssue>();
        string pearlAbyssPath = PearlAbyssConfigService.GetDefaultPearlAbyssPath();
        string rhiPath = RhiAndSystemService.GetDefaultRhiPath();

        // 1. System & Hardware checks
        var sys = _rhiSystem.ScanSystem();

        // 2. Pearl Abyss Launcher Logs
        var logInfo = _pearlAbyss.AnalyzeLatestLauncherLog(pearlAbyssPath);

        // 3. Locate Crimson Desert game folder
        string gamePath = !string.IsNullOrWhiteSpace(overrideGamePath)
            ? overrideGamePath
            : _gameDir.FindGameInstallPath(logInfo?.DetectedGamePath, rhiPath);

        var gameScan = _gameDir.ScanGameDirectory(gamePath);
        var rhiScan = _rhiSystem.ScanRhi(rhiPath);

        string slSummary = "No Launcher log found";
        if (logInfo is not null)
        {
            slSummary = $"Log: DLSS={logInfo.StreamlineDlssVersion ?? "?"}, RR={logInfo.StreamlineDlssRrVersion ?? "?"}, " +
                        $"DLSS-G={logInfo.StreamlineDlssGVersion ?? "not logged"}, Reflex={logInfo.StreamlineReflexVersion ?? "?"}, " +
                        $"SL Errors={logInfo.TotalStreamlineErrors}";
        }

        var summary = new SystemSummary
        {
            GpuName = sys.GpuName,
            DriverVersion = sys.DriverVersion,
            HagsStatus = sys.HagsStatus,
            GameInstallPath = gamePath,
            Bin64Path = gameScan.Bin64Path,
            PearlAbyssPath = pearlAbyssPath,
            RhiPath = rhiPath,
            IsRhiRunning = rhiScan.IsRhiRunning,
            LatestLogFile = logInfo?.LatestLogPath ?? string.Empty,
            StreamlineLogSummary = slSummary,
            ScannedAt = DateTime.Now
        };

        // ------------------------------------------------------------------
        // CHECK A: NVIDIA DRS Global (BASE) & Crimson Desert Profile Overrides
        // ------------------------------------------------------------------
        var (globalDrs, profileDrs, profileNameFound, nvapiErr) = _nvapi.ScanDrsOverrides();
        if (nvapiErr is not null)
        {
            issues.Add(new DiagnosticIssue
            {
                Id = "nvapi_unavailable",
                Title = "NVIDIA Driver Profile (NVAPI DRS) Unavailable",
                Category = IssueCategory.NvidiaDrsGlobal,
                Severity = IssueSeverity.Warning,
                Summary = nvapiErr,
                TechnicalDetails = "Could not query nvapi64.dll for DLSS/MFG/Streamline overrides.",
                IsFixable = false
            });
        }
        else
        {
            // Global BASE overrides (e.g. 0x10E41E06 Enable Streamline override)
            if (globalDrs.Count > 0)
            {
                var activeGlobal = globalDrs.Where(s => s.CurrentU32 != 0 || !string.IsNullOrEmpty(s.CurrentString)).ToList();
                var sb = new StringBuilder();
                foreach (var s in globalDrs)
                {
                    sb.AppendLine($"• 0x{s.SettingId:X8} — {s.FriendlyName}: {s.DecodedCurrentValue} (default: 0x{s.PredefinedU32:X})");
                }

                bool hasStreamlineOverride = globalDrs.Any(s => s.SettingId == 0x10E41E06 && s.CurrentU32 != 0);
                issues.Add(new DiagnosticIssue
                {
                    Id = "drs_global_overrides",
                    Title = hasStreamlineOverride
                        ? "Global NVIDIA Driver 'Streamline Override' Is Forced ON (Breaks Clean Install!)"
                        : $"Global NVIDIA Driver DLSS/MFG Overrides Active ({globalDrs.Count})",
                    Category = IssueCategory.NvidiaDrsGlobal,
                    Severity = activeGlobal.Count > 0 ? IssueSeverity.Critical : IssueSeverity.Warning,
                    Summary = hasStreamlineOverride
                        ? "The Global NVIDIA DRS profile has 'Enable Streamline override' (0x10E41E06=1) enabled. This injects NVIDIA OTA Streamline DLLs over Crimson Desert's clean-installed bin64 Streamline files and causes DLSS/MFG & buffer-tag errors even after a clean reinstall."
                        : "User-modified DLSS/MFG/Streamline overrides are set on the Global NVIDIA BASE profile and inherited by Crimson Desert.",
                    TechnicalDetails = sb.ToString().TrimEnd(),
                    FixDescription = "Delete the DLSS/MFG/Streamline override entries from the Global NVIDIA DRS BASE profile and save cleanly via NVAPI (with automatic backup).",
                    IsFixable = true,
                    DefaultSelected = true,
                    IsSelected = true,
                    FixActionKey = "FIX_DRS_GLOBAL",
                    Metadata = new Dictionary<string, string>
                    {
                        ["SettingIds"] = string.Join(",", globalDrs.Select(s => $"0x{s.SettingId:X8}"))
                    }
                });
            }
            else
            {
                issues.Add(new DiagnosticIssue
                {
                    Id = "drs_global_clean",
                    Title = "Global NVIDIA DRS Profile Clean",
                    Category = IssueCategory.NvidiaDrsGlobal,
                    Severity = IssueSeverity.Ok,
                    Summary = "No global DLSS, MFG, or Streamline overrides found in the NVIDIA BASE profile.",
                    IsFixable = false
                });
            }

            // Crimson Desert Profile overrides (MFG mode, MFG count, DLSS-SR/RR quality overrides, dirty RHI entries)
            if (profileDrs.Count > 0)
            {
                var mfgOverrides = profileDrs.Where(s =>
                    s.SettingId is 0x10308298 or 0x104D6667 or 0x10562D0F or 0x10CF4125 or 0x10E41E03 or 0x10E41DF7 or 0x10E41DFB).ToList();
                var dlssOverrides = profileDrs.Except(mfgOverrides).ToList();

                var activeMfg = mfgOverrides.Where(s => s.CurrentU32 != 0).ToList();
                var activeDlss = dlssOverrides.Where(s => s.CurrentU32 != 0).ToList();

                if (mfgOverrides.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var s in mfgOverrides)
                    {
                        sb.AppendLine($"• 0x{s.SettingId:X8} — {s.FriendlyName}: {s.DecodedCurrentValue} (default: 0x{s.PredefinedU32:X})");
                    }

                    issues.Add(new DiagnosticIssue
                    {
                        Id = "drs_cd_mfg_overrides",
                        Title = activeMfg.Count > 0
                            ? $"Crimson Desert NVIDIA Profile Has Active MFG / DLSS-FG Overrides ({mfgOverrides.Count} entries)"
                            : $"Crimson Desert NVIDIA Profile Has Stale/Dirty MFG / DLSS-FG Entries ({mfgOverrides.Count})",
                        Category = IssueCategory.NvidiaDrsProfile,
                        Severity = activeMfg.Count > 0 ? IssueSeverity.Critical : IssueSeverity.Warning,
                        Summary = activeMfg.Count > 0
                            ? "RHI / driver profile has forced DLSS Frame Generation / Multi-Frame Generation overrides (e.g. 'Override DLSSG mode' = 0x2 while multi-frame count = 0x0). This directly breaks MFG & Frame Generation even after a clean game reinstall."
                            : "RHI left dirty zeroed user-override DRS entries for DLSS-FG/MFG instead of deleting them back to driver defaults.",
                        TechnicalDetails = sb.ToString().TrimEnd(),
                        FixDescription = $"Delete all {mfgOverrides.Count} DLSS-FG / MFG override entries from the '{profileNameFound}' NVIDIA DRS profile via NvAPI_DRS_DeleteProfileSetting.",
                        IsFixable = true,
                        DefaultSelected = true,
                        IsSelected = true,
                        FixActionKey = "FIX_DRS_PROFILE_MFG",
                        Metadata = new Dictionary<string, string>
                        {
                            ["ProfileName"] = profileNameFound,
                            ["SettingIds"] = string.Join(",", mfgOverrides.Select(s => $"0x{s.SettingId:X8}"))
                        }
                    });
                }

                if (dlssOverrides.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var s in dlssOverrides)
                    {
                        sb.AppendLine($"• 0x{s.SettingId:X8} — {s.FriendlyName}: {s.DecodedCurrentValue} (default: 0x{s.PredefinedU32:X})");
                    }

                    issues.Add(new DiagnosticIssue
                    {
                        Id = "drs_cd_dlss_overrides",
                        Title = activeDlss.Count > 0
                            ? $"Crimson Desert NVIDIA Profile Has Active DLSS-SR / DLSS-RR Overrides ({dlssOverrides.Count} entries)"
                            : $"Crimson Desert NVIDIA Profile Has Stale/Dirty DLSS Entries ({dlssOverrides.Count})",
                        Category = IssueCategory.NvidiaDrsProfile,
                        Severity = activeDlss.Count > 0 ? IssueSeverity.Critical : IssueSeverity.Warning,
                        Summary = activeDlss.Count > 0
                            ? "The Crimson Desert driver profile forces DLSS-SR / DLSS-RR performance mode overrides at the driver level, overriding the game's own DLSS menu settings."
                            : "RHI left dirty user-override DRS entries for DLSS-SR/RR/NR instead of restoring driver defaults.",
                        TechnicalDetails = sb.ToString().TrimEnd(),
                        FixDescription = $"Delete all {dlssOverrides.Count} DLSS-SR/RR/NR override entries from the '{profileNameFound}' NVIDIA DRS profile via NvAPI_DRS_DeleteProfileSetting.",
                        IsFixable = true,
                        DefaultSelected = true,
                        IsSelected = true,
                        FixActionKey = "FIX_DRS_PROFILE_DLSS",
                        Metadata = new Dictionary<string, string>
                        {
                            ["ProfileName"] = profileNameFound,
                            ["SettingIds"] = string.Join(",", dlssOverrides.Select(s => $"0x{s.SettingId:X8}"))
                        }
                    });
                }
            }
            else
            {
                issues.Add(new DiagnosticIssue
                {
                    Id = "drs_cd_clean",
                    Title = $"NVIDIA '{profileNameFound}' Profile Clean",
                    Category = IssueCategory.NvidiaDrsProfile,
                    Severity = IssueSeverity.Ok,
                    Summary = "No user-modified DLSS or MFG overrides found in the Crimson Desert NVIDIA profile.",
                    IsFixable = false
                });
            }
        }

        // ------------------------------------------------------------------
        // CHECK A2: Private NVIDIA DRS Settings (Smooth Motion, RTX HDR, Forced V-Sync)
        // Public NVAPI hides 0xB0D384C0 (Smooth Motion) & 0x00634291/0x00598928 (RTX HDR),
        // so we inspect the active nvdrsdb0/1.bin records directly!
        // ------------------------------------------------------------------
        var xmlAnalyses = _pearlAbyss.AnalyzeXmlConfigs(pearlAbyssPath);
        bool nativeHdrOn = xmlAnalyses.Any(x => x.IsNativeHdrEnabled);
        bool gameVsyncOff = xmlAnalyses.Any(x => x.IsVsyncOff);
        bool isRtx50Series = sys.GpuName.Contains("RTX 50", StringComparison.OrdinalIgnoreCase);
        bool isRtx40OrOlder = sys.GpuName.Contains("RTX 40", StringComparison.OrdinalIgnoreCase)
            || sys.GpuName.Contains("RTX 30", StringComparison.OrdinalIgnoreCase)
            || sys.GpuName.Contains("RTX 20", StringComparison.OrdinalIgnoreCase)
            || sys.GpuName.Contains("GTX ", StringComparison.OrdinalIgnoreCase);

        var baseBin = _nvapi.ReadProfileRecordsFromActiveDrsBin("Base Profile");
        var cdBin = _nvapi.ReadProfileRecordsFromActiveDrsBin(profileNameFound);

        // 1. NVIDIA Smooth Motion (DriverFGX, 0xB0D384C0)
        bool baseSmoothMotionOn = baseBin.TryGetValue(NvApiDrsService.DRS_SMOOTH_MOTION, out var bmSm) && bmSm.IsUserModified && bmSm.Value != 0;
        bool cdHasSmoothMotionOverride = cdBin.TryGetValue(NvApiDrsService.DRS_SMOOTH_MOTION, out var cdSm) && cdSm.IsUserModified;
        bool cdSmoothMotionEffectiveOn = cdHasSmoothMotionOverride ? (cdSm!.Value != 0) : baseSmoothMotionOn;

        if (cdSmoothMotionEffectiveOn)
        {
            string source = cdHasSmoothMotionOverride
                ? $"directly on the '{profileNameFound}' profile (0xB0D384C0=0x{cdSm!.Value:X})"
                : "on the Global 'Base Profile' (0xB0D384C0=0x1) and inherited by Crimson Desert";

            bool baseLowLatUltra = baseBin.TryGetValue(NvApiDrsService.DRS_LOW_LATENCY_MODE, out var bmLl) && bmLl.IsUserModified && bmLl.Value != 0;
            var details = new StringBuilder();
            details.AppendLine($"• 0xB0D384C0 (NVIDIA Smooth Motion / DriverFGX): ENABLED {source}");
            if (baseLowLatUltra)
                details.AppendLine($"• 0x0005F543 (Low Latency Mode): 0x{bmLl!.Value:X} (forced by Smooth Motion; conflicts with in-game NVIDIA Reflex)");
            if (logInfo is not null && logInfo.ProxyBufferNullErrors > 0)
                details.AppendLine($"• Confirmed in latest game log: {logInfo.ProxyBufferNullErrors}x Streamline DLSS-G swapchain error(s) ('resizeProxyBuffers: createTexture2D failed' / 'slHookGetBuffer: proxyBuffer is NULL')!");

            issues.Add(new DiagnosticIssue
            {
                Id = "drs_smooth_motion_active",
                Title = "NVIDIA Smooth Motion (Driver Frame Gen) Is Active on Crimson Desert — Causes Inverted MFG & Screen Blinking!",
                Category = IssueCategory.NvidiaDrsProfile,
                Severity = IssueSeverity.Critical,
                Summary = "NVIDIA Smooth Motion (0xB0D384C0) is active on Crimson Desert. When in-game FG/MFG is OFF, Smooth Motion injects driver-level frame generation (making FG look ON when OFF). When in-game FG/MFG is ON, Smooth Motion collides with Streamline's DLSS-G swapchain hook, crashing DLSS-G (making FG look OFF when ON) and causing severe graphics blinking/flashing!",
                TechnicalDetails = details.ToString().TrimEnd(),
                FixDescription = $"Explicitly disable Smooth Motion (0xB0D384C0=0) and driver Low Latency override (0x0005F543=0) on the '{profileNameFound}' profile via NVAPI (keeps your Global setting untouched for other games).",
                IsFixable = true,
                DefaultSelected = true,
                IsSelected = true,
                FixActionKey = "FIX_DISABLE_SMOOTH_MOTION_CD",
                Metadata = new Dictionary<string, string>
                {
                    ["ProfileName"] = profileNameFound
                }
            });
        }
        else
        {
            issues.Add(new DiagnosticIssue
            {
                Id = "drs_smooth_motion_clean",
                Title = "NVIDIA Smooth Motion (DriverFGX) Disabled for Crimson Desert",
                Category = IssueCategory.NvidiaDrsProfile,
                Severity = IssueSeverity.Ok,
                Summary = "Smooth Motion (0xB0D384C0) is not active on Crimson Desert, so it will not collide with native DLSS Frame Generation.",
                IsFixable = false
            });
        }

        // 2. Global RTX HDR (0x00DD48FB = Enable TrueHDR Feature) colliding with Crimson Desert native HDR / DX12 swapchain
        bool cdRtxHdrSet = cdBin.TryGetValue(NvApiDrsService.DRS_RTX_HDR_ENABLE, out var cdHdrRec) && cdHdrRec.IsUserModified;
        bool baseRtxHdrOn = baseBin.TryGetValue(NvApiDrsService.DRS_RTX_HDR_ENABLE, out var bHdrRec) && bHdrRec.IsUserModified && bHdrRec.Value != 0;
        bool effectiveRtxHdrOn = cdRtxHdrSet ? (cdHdrRec!.Value != 0) : baseRtxHdrOn;

        if (effectiveRtxHdrOn)
        {
            var sb = new StringBuilder();
            uint val = cdRtxHdrSet ? cdHdrRec!.Value : bHdrRec!.Value;
            sb.AppendLine($"• 0x00DD48FB — Enable TrueHDR Feature (RTX HDR): 0x{val:X} ({(cdRtxHdrSet ? "set on Crimson Desert" : "inherited from Global Base Profile")})");
            if (nativeHdrOn)
                sb.AppendLine("• Crimson Desert XML has native in-game HDR enabled (_enableHDR=\"True\") — double-HDR conflict!");

            issues.Add(new DiagnosticIssue
            {
                Id = "drs_rtx_hdr_conflict",
                Title = nativeHdrOn
                    ? "NVIDIA RTX HDR (TrueHDR) Is Active Globally While Crimson Desert Uses Native HDR"
                    : "NVIDIA RTX HDR (TrueHDR) Is Active on Crimson Desert",
                Category = IssueCategory.NvidiaDrsProfile,
                Severity = nativeHdrOn ? IssueSeverity.Critical : IssueSeverity.Warning,
                Summary = "Global NVIDIA RTX HDR (0x00DD48FB=1) is inherited by Crimson Desert. Running RTX HDR on top of native in-game HDR and DX12 DLSS Frame Generation triggers screen flashing/blinking and swapchain texture creation failures.",
                TechnicalDetails = sb.ToString().TrimEnd(),
                FixDescription = $"Explicitly disable RTX HDR (0x00DD48FB=0) on the '{profileNameFound}' profile only via NVAPI v2 (preserves Global RTX HDR for other games).",
                IsFixable = true,
                DefaultSelected = true,
                IsSelected = true,
                FixActionKey = "FIX_DISABLE_RTX_HDR_CD",
                Metadata = new Dictionary<string, string>
                {
                    ["ProfileName"] = profileNameFound
                }
            });
        }

        // 2b. Global DLSS Model Preset Profile (0x00634291) inherited from Base Profile
        bool cdDlssPresetSet = cdBin.TryGetValue(NvApiDrsService.DRS_DLSS_MODEL_PRESET_PROFILE, out var cdDpRec) && cdDpRec.IsUserModified;
        bool baseDlssPresetOn = baseBin.TryGetValue(NvApiDrsService.DRS_DLSS_MODEL_PRESET_PROFILE, out var bDpRec) && bDpRec.IsUserModified && bDpRec.Value != 0;
        bool effectiveDlssPresetOn = cdDlssPresetSet ? (cdDpRec!.Value != 0) : baseDlssPresetOn;

        if (effectiveDlssPresetOn)
        {
            uint val = cdDlssPresetSet ? cdDpRec!.Value : bDpRec!.Value;
            issues.Add(new DiagnosticIssue
            {
                Id = "drs_dlss_model_preset_profile",
                Title = "Global NVIDIA 'DLSS Model Preset Profile' Override (0x00634291) Inherited by Crimson Desert",
                Category = IssueCategory.NvidiaDrsProfile,
                Severity = IssueSeverity.Critical,
                Summary = $"The Global Base Profile has 'DLSS Model Preset Profile' (0x00634291=0x{val:X}) active, overriding Crimson Desert's native DLSS model preset selection at the driver level.",
                TechnicalDetails = $"• 0x00634291 — DLSS Model Preset Profile: 0x{val:X} ({(cdDlssPresetSet ? "set on Crimson Desert" : "inherited from Global Base Profile")})",
                FixDescription = $"Explicitly set DLSS Model Preset Profile to Off/Default (0x00634291=0) on the '{profileNameFound}' profile only via NVAPI v2.",
                IsFixable = true,
                DefaultSelected = true,
                IsSelected = true,
                FixActionKey = "FIX_DISABLE_DLSS_PRESET_CD",
                Metadata = new Dictionary<string, string>
                {
                    ["ProfileName"] = profileNameFound
                }
            });
        }

        // 3. Global Forced V-Sync (0x00A879CF = 0x47814940) inherited by Crimson Desert
        bool cdVsyncSet = cdBin.TryGetValue(NvApiDrsService.DRS_VSYNC, out var cdVs) && cdVs.IsUserModified;
        bool baseVsyncForcedOn = baseBin.TryGetValue(NvApiDrsService.DRS_VSYNC, out var bVs) && bVs.IsUserModified && bVs.Value == NvApiDrsService.VSYNC_FORCE_ON;
        bool effectiveVsyncForcedOn = cdVsyncSet ? (cdVs!.Value == NvApiDrsService.VSYNC_FORCE_ON) : baseVsyncForcedOn;

        if (effectiveVsyncForcedOn)
        {
            issues.Add(new DiagnosticIssue
            {
                Id = "drs_vsync_forced_on",
                Title = "NVIDIA Driver Forces Vertical Sync ON Globally for Crimson Desert",
                Category = IssueCategory.NvidiaDrsProfile,
                Severity = IssueSeverity.Warning,
                Summary = gameVsyncOff
                    ? "Your Global NVIDIA Base Profile forces V-Sync ON (0x00A879CF=0x47814940) while Crimson Desert has in-game V-Sync OFF. Forced driver V-Sync can interfere with DLSS Frame Generation / Reflex swapchain pacing."
                    : "Your Global NVIDIA Base Profile forces V-Sync ON (0x00A879CF=0x47814940), which can conflict with DLSS Frame Generation.",
                TechnicalDetails = "• 0x00A879CF (Vertical Sync): 0x47814940 (VSYNCMODE_FORCEON) inherited from Base Profile",
                FixDescription = $"Set Vertical Sync to 'Use the 3D application setting' (0x08416747) on the '{profileNameFound}' profile only.",
                IsFixable = true,
                DefaultSelected = true,
                IsSelected = true,
                FixActionKey = "FIX_RESET_VSYNC_CD",
                Metadata = new Dictionary<string, string>
                {
                    ["ProfileName"] = profileNameFound
                }
            });
        }

        // ------------------------------------------------------------------
        // CHECK B: Pearl Abyss AppData XML Configs (DLSS & MFG corruption)
        // ------------------------------------------------------------------
        int xmlIndex = 0;
        bool anyXmlIssue = false;
        foreach (var xml in xmlAnalyses)
        {
            xmlIndex++;
            if (!xml.HasAnyIssue(isRtx50Series, isRtx40OrOlder)) continue;
            anyXmlIssue = true;

            var reasons = new List<string>();
            if (isRtx50Series && xml.HasCorruptedNumFramesEnableGroup)
                reasons.Add($"• Corrupted '_numFramesToGenerate' override block (isOverrided=\"true\", removedElements count={xml.RemovedMfgElementCount}, locked _value=\"{xml.NumFramesValue}\") — strips MFG multiplier options from Crimson Desert!");
            if (isRtx40OrOlder && !string.IsNullOrWhiteSpace(xml.NumFramesValue) && int.TryParse(xml.NumFramesValue, out int nf) && nf > 2)
                reasons.Add($"• Invalid '_numFramesToGenerate' = \"{xml.NumFramesValue}\" for this GPU (max native is 2)");
            if (XmlAnalysisResult.IsSuspiciousUpscaleMode(xml.UpscaleModeSelect))
                reasons.Add($"• Stale/modded '_upscaleModeSelect' = \"{xml.UpscaleModeSelect}\"");
            if (xml.IsReadOnly)
                reasons.Add("• File is marked Read-Only (prevents Crimson Desert from saving updated DLSS/MFG settings)");
            if (xml.IsMalformedXml)
                reasons.Add("• XML syntax is malformed / corrupted");

            string shortName = Path.GetRelativePath(pearlAbyssPath, xml.FilePath);
            issues.Add(new DiagnosticIssue
            {
                Id = $"xml_corrupt_{xmlIndex}",
                Title = $"Corrupted DLSS / MFG Settings in Pearl Abyss\\{shortName}",
                Category = IssueCategory.PearlAbyssConfig,
                Severity = (isRtx50Series && xml.HasCorruptedNumFramesEnableGroup) || xml.IsMalformedXml || XmlAnalysisResult.IsSuspiciousUpscaleMode(xml.UpscaleModeSelect)
                    ? IssueSeverity.Critical
                    : IssueSeverity.Warning,
                Summary = $"'{shortName}' contains stale or conflicting DLSS/Frame Generation settings.",
                TechnicalDetails = $"Path: {xml.FilePath}\n" + string.Join("\n", reasons),
                FixDescription = "Back up the XML, remove Read-Only, surgically strip the corrupted '_numFramesToGenerate' & stale DLSS/FG nodes while preserving your controls, graphics, HDR, and save slots.",
                IsFixable = true,
                DefaultSelected = true,
                IsSelected = true,
                FixActionKey = "FIX_PEARL_ABYSS_XML",
                Metadata = new Dictionary<string, string> { ["FilePath"] = xml.FilePath }
            });
        }

        if (!anyXmlIssue)
        {
            issues.Add(new DiagnosticIssue
            {
                Id = "xml_clean",
                Title = "Pearl Abyss Engine Option XMLs Clean",
                Category = IssueCategory.PearlAbyssConfig,
                Severity = IssueSeverity.Ok,
                Summary = xmlAnalyses.Count > 0
                    ? $"Checked {xmlAnalyses.Count} user_engine_option_save.xml file(s) — no corrupted MFG/DLSS overrides found."
                    : "No user_engine_option_save.xml files found yet (game will generate clean defaults on launch).",
                IsFixable = false
            });
        }

        // ------------------------------------------------------------------
        // CHECK C: Game Directory (Leftover RHI / MFG / DLSS5 Mod Files & DLLs)
        // ------------------------------------------------------------------
        if (!gameScan.Exists)
        {
            issues.Add(new DiagnosticIssue
            {
                Id = "game_not_found",
                Title = "Crimson Desert Installation Folder Not Found",
                Category = IssueCategory.GameDirectory,
                Severity = IssueSeverity.Warning,
                Summary = "Could not automatically locate Crimson Desert. Use the Browse button above to select your game folder.",
                IsFixable = false
            });
        }
        else
        {
            if (gameScan.HasNonAsciiPath)
            {
                issues.Add(new DiagnosticIssue
                {
                    Id = "game_non_ascii_path",
                    Title = "Non-ASCII Characters in Game Install Path",
                    Category = IssueCategory.GameDirectory,
                    Severity = IssueSeverity.Critical,
                    Summary = "NVIDIA Streamline in Crimson Desert fails to load DLSS DLLs when the install path contains accented or non-ASCII characters.",
                    TechnicalDetails = $"InstallPath: {gameScan.InstallPath}",
                    FixDescription = "Move your Steam library folder to an ASCII-only path (e.g. C:\\Games\\SteamLibrary).",
                    IsFixable = false
                });
            }

            if (gameScan.LeftoverArtifacts.Count > 0)
            {
                var sb = new StringBuilder();
                long totalBytes = 0;
                foreach (var art in gameScan.LeftoverArtifacts)
                {
                    totalBytes += art.SizeBytes;
                    string sizeMb = $"{art.SizeBytes / (1024.0 * 1024.0):0.0} MB";
                    sb.AppendLine($"• {(art.IsDirectory ? "[DIR] " : "")}{art.RelativePath} ({sizeMb}) — {art.Reason}");
                }

                issues.Add(new DiagnosticIssue
                {
                    Id = "game_leftover_mods",
                    Title = $"Leftover RHI / DLSS / MFG Mod Files in Game Folder ({gameScan.LeftoverArtifacts.Count} item(s), {totalBytes / (1024.0 * 1024.0):0.0} MB)",
                    Category = IssueCategory.GameDirectory,
                    Severity = gameScan.LeftoverArtifacts.Any(a => !a.IsDirectory) ? IssueSeverity.Critical : IssueSeverity.Warning,
                    Summary = "Steam clean installs do NOT remove extra mod files/folders left behind by RHI, DLSS 5 / Neural Rendering, OptiScaler, or MFG unlockers.",
                    TechnicalDetails = sb.ToString().TrimEnd(),
                    FixDescription = "Safely quarantine (move, not permanently delete) these leftover mod files/folders into the backup folder.",
                    IsFixable = true,
                    DefaultSelected = true,
                    IsSelected = true,
                    FixActionKey = "FIX_QUARANTINE_GAME_MODS",
                    Metadata = new Dictionary<string, string>
                    {
                        ["Paths"] = string.Join("|", gameScan.LeftoverArtifacts.Select(a => $"{(a.IsDirectory ? "D" : "F")}:{a.FullPath}"))
                    }
                });
            }
            else
            {
                issues.Add(new DiagnosticIssue
                {
                    Id = "game_mods_clean",
                    Title = "No Leftover Mod DLLs or Backup Folders in Game Directory",
                    Category = IssueCategory.GameDirectory,
                    Severity = IssueSeverity.Ok,
                    Summary = $"Scanned '{gameScan.Bin64Path}' — no proxy DLLs, MFG unlockers, OptiScaler, ReShade, or _BACKUPNV leftovers detected.",
                    IsFixable = false
                });
            }

            // Core NVIDIA DLSS & Streamline DLL integrity
            var missingDlls = gameScan.CoreDlls.Where(d => !d.Exists).ToList();
            var unsignedDlls = gameScan.CoreDlls.Where(d => d.Exists && !d.IsNvidiaSigned).ToList();
            if (missingDlls.Count > 0 || unsignedDlls.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var d in missingDlls) sb.AppendLine($"• MISSING: {d.FileName}");
                foreach (var d in unsignedDlls) sb.AppendLine($"• NON-NVIDIA SIGNATURE: {d.FileName} (v{d.FileVersion}, signer: {d.SignerName})");
                issues.Add(new DiagnosticIssue
                {
                    Id = "game_core_dlls_bad",
                    Title = $"Core NVIDIA DLSS / Streamline DLL Integrity Warning ({missingDlls.Count} missing, {unsignedDlls.Count} unsigned)",
                    Category = IssueCategory.GameDirectory,
                    Severity = IssueSeverity.Critical,
                    Summary = "One or more official DLSS or Streamline DLLs in bin64 are missing or replaced with an unsigned/modded binary. Run Steam 'Verify integrity of game files' after applying fixes.",
                    TechnicalDetails = sb.ToString().TrimEnd(),
                    IsFixable = false
                });
            }
            else if (gameScan.CoreDlls.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var d in gameScan.CoreDlls)
                    sb.AppendLine($"• {d.FileName}: v{d.FileVersion} (NVIDIA Signed)");
                issues.Add(new DiagnosticIssue
                {
                    Id = "game_core_dlls_ok",
                    Title = $"All {gameScan.CoreDlls.Count} Official NVIDIA DLSS & Streamline DLLs Verified",
                    Category = IssueCategory.GameDirectory,
                    Severity = IssueSeverity.Ok,
                    Summary = $"All core nvngx_dlss* and sl.* DLLs in bin64 are present and validly signed by NVIDIA (Streamline disk v{gameScan.StreamlineDiskVersion}).",
                    TechnicalDetails = sb.ToString().TrimEnd(),
                    IsFixable = false
                });
            }
        }

        // ------------------------------------------------------------------
        // CHECK D: RHI Local Cache & Auto-Redeploy Config
        // ------------------------------------------------------------------
        if (rhiScan.Exists && rhiScan.StaleFindings.Count > 0)
        {
            issues.Add(new DiagnosticIssue
            {
                Id = "rhi_stale_cache",
                Title = $"RHI Mod Manager Retains Stale Crimson Desert DLSS/MFG Config ({rhiScan.StaleFindings.Count} finding(s))",
                Category = IssueCategory.RhiCache,
                Severity = IssueSeverity.Warning,
                Summary = rhiScan.IsRhiRunning
                    ? "RHI.exe is currently RUNNING and has stale Crimson Desert DLSS-NR/ShortFuse/FG entries cached. Close RHI before fixing so it doesn't overwrite changes."
                    : "RHI (%LOCALAPPDATA%\\RHI) still caches leftover DLSS-NR paths (_BACKUPNV) and ShortFuse/FG overrides for Crimson Desert.",
                TechnicalDetails = string.Join("\n", rhiScan.StaleFindings.Select(f => $"• {f}")),
                FixDescription = "Back up RHI's JSON files and remove Crimson Desert's stale DLSS/FG/ShortFuse entries so RHI doesn't re-inject or re-flag them.",
                IsFixable = true,
                DefaultSelected = true,
                IsSelected = true,
                FixActionKey = "FIX_RHI_CACHE",
                Metadata = new Dictionary<string, string>
                {
                    ["RhiPath"] = rhiScan.RhiPath,
                    ["AffectedFiles"] = string.Join("|", rhiScan.AffectedFiles)
                }
            });
        }
        else if (rhiScan.Exists)
        {
            issues.Add(new DiagnosticIssue
            {
                Id = "rhi_clean",
                Title = "RHI Mod Manager Cache Clean for Crimson Desert",
                Category = IssueCategory.RhiCache,
                Severity = IssueSeverity.Ok,
                Summary = "No stale Crimson Desert DLSS/MFG/DLSS-NR entries found in %LOCALAPPDATA%\\RHI.",
                IsFixable = false
            });
        }

        // ------------------------------------------------------------------
        // CHECK E: Streamline Log Evidence, HAGS, Registry & Shader Caches
        // ------------------------------------------------------------------
        if (logInfo is not null)
        {
            bool versionMismatch = !string.IsNullOrWhiteSpace(gameScan.StreamlineDiskVersion)
                && !string.IsNullOrWhiteSpace(logInfo.StreamlineDlssVersion)
                && !gameScan.StreamlineDiskVersion.StartsWith(logInfo.StreamlineDlssVersion + ".", StringComparison.OrdinalIgnoreCase)
                && !gameScan.StreamlineDiskVersion.Equals(logInfo.StreamlineDlssVersion, StringComparison.OrdinalIgnoreCase);

            if (versionMismatch || logInfo.TotalStreamlineErrors > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Latest Log: {logInfo.LatestLogPath}");
                sb.AppendLine($"• Disk sl.dlss.dll version: {gameScan.StreamlineDiskVersion ?? "Unknown"}");
                sb.AppendLine($"• Runtime loaded Streamline DLSS: {logInfo.StreamlineDlssVersion ?? "Unknown"}");
                sb.AppendLine($"• Runtime loaded Streamline DLSS-RR: {logInfo.StreamlineDlssRrVersion ?? "Unknown"}");
                sb.AppendLine($"• Runtime loaded Streamline DLSS-G (FG/MFG): {logInfo.StreamlineDlssGVersion ?? "NOT INITIALIZED / NOT LOGGED"}");
                sb.AppendLine($"• Runtime loaded Streamline Reflex: {logInfo.StreamlineReflexVersion ?? "Unknown"}");
                if (logInfo.TotalStreamlineErrors > 0)
                {
                    sb.AppendLine($"• Streamline runtime errors: {logInfo.TotalStreamlineErrors} total ({logInfo.MissingDepthTagErrors}x missing kBufferTypeDepth, {logInfo.MissingMotionVectorErrors}x missing kBufferTypeMotionVectors)");
                }
                if (logInfo.ProxyBufferNullErrors > 0)
                {
                    sb.AppendLine($"• DLSS-G Swapchain Hook Failures: {logInfo.ProxyBufferNullErrors}x ('resizeProxyBuffers: createTexture2D failed' / 'slHookGetBuffer: proxyBuffer is NULL') — classic symptom of NVIDIA Smooth Motion / RTX HDR colliding with DLSS Frame Generation!");
                }

                issues.Add(new DiagnosticIssue
                {
                    Id = "log_streamline_mismatch",
                    Title = versionMismatch
                        ? $"Latest Game Log Confirms Streamline Override Mismatch (Loaded {logInfo.StreamlineDlssVersion} vs Disk {gameScan.StreamlineDiskVersion})"
                        : logInfo.ProxyBufferNullErrors > 0
                            ? $"Latest Game Log Shows DLSS-G Swapchain Proxy Buffer Failure ({logInfo.ProxyBufferNullErrors}x)"
                            : $"Latest Game Log Shows {logInfo.TotalStreamlineErrors} Streamline Runtime Errors",
                    Category = IssueCategory.SystemAndLogs,
                    Severity = IssueSeverity.Info,
                    Summary = versionMismatch
                        ? "This confirms the NVIDIA Driver Global Streamline Override (0x10E41E06) injected mismatched OTA Streamline DLLs into Crimson Desert, breaking DLSS/MFG buffer tagging. Fixing the NVIDIA DRS overrides above resolves this!"
                        : logInfo.ProxyBufferNullErrors > 0
                            ? "Streamline DLSS-G failed to allocate swapchain proxy buffers (proxyBuffer is NULL). Disabling NVIDIA Smooth Motion and RTX HDR on Crimson Desert resolves this swapchain collision!"
                            : "Streamline logged missing Depth/MotionVectors buffer tags during your last launch. Fixing the NVIDIA DRS overrides and Pearl Abyss XML resolves this.",
                    TechnicalDetails = sb.ToString().TrimEnd(),
                    IsFixable = false
                });
            }
        }

        if (sys.IsHagsExplicitlyDisabled)
        {
            issues.Add(new DiagnosticIssue
            {
                Id = "sys_hags_disabled",
                Title = "Hardware-Accelerated GPU Scheduling (HAGS) Is Disabled",
                Category = IssueCategory.SystemAndLogs,
                Severity = IssueSeverity.Critical,
                Summary = "DLSS Frame Generation and Multi-Frame Generation (MFG) require Windows HAGS to be enabled.",
                TechnicalDetails = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers\\HwSchMode = 1 (Disabled). Enable HAGS in Windows Settings -> System -> Display -> Graphics -> Default graphics settings, then reboot.",
                IsFixable = false
            });
        }

        if (sys.NgxCoreAnomalies.Count > 0 || sys.SuspiciousEnvVars.Count > 0 || sys.SuspiciousVulkanLayers.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var a in sys.NgxCoreAnomalies) sb.AppendLine($"• Registry: {a}");
            foreach (var e in sys.SuspiciousEnvVars) sb.AppendLine($"• EnvVar: {e}");
            foreach (var v in sys.SuspiciousVulkanLayers) sb.AppendLine($"• Vulkan Layer: {v}");
            issues.Add(new DiagnosticIssue
            {
                Id = "sys_global_hooks",
                Title = "Global NGX Registry / Environment / Vulkan Layer Overrides Detected",
                Category = IssueCategory.SystemAndLogs,
                Severity = IssueSeverity.Warning,
                Summary = "Global system overrides (NGXCore registry flags, DLSS/SL environment variables, or ReShade Vulkan implicit layers) were detected.",
                TechnicalDetails = sb.ToString().TrimEnd(),
                IsFixable = false
            });
        }

        // Only warn about recent shader cache when > 50 MB AND actual DRS/mod/Streamline issues were detected
        // (avoids nagging over freshly compiled clean shaders after playing with zero errors!)
        bool hasActiveRuntimeOrModProblems = issues.Any(i =>
            i.Severity is IssueSeverity.Critical or IssueSeverity.Warning
            || (logInfo is not null && logInfo.TotalStreamlineErrors > 0));

        if (sys.ShaderCacheDirectories.Count > 0 && sys.RecentShaderCacheSizeBytes > 50L * 1024 * 1024 && hasActiveRuntimeOrModProblems)
        {
            issues.Add(new DiagnosticIssue
            {
                Id = "sys_recent_shader_cache",
                Title = $"Clear Recent NVIDIA Shader & Compute Cache (Last 72h: {sys.RecentShaderCacheFileCount} files, {sys.RecentShaderCacheSizeBytes / (1024.0 * 1024.0):0.1} MB)",
                Category = IssueCategory.SystemAndLogs,
                Severity = IssueSeverity.Warning,
                Summary = "Recommended: clears only recently written DXCache / ComputeCache / D3DSCache files (compiled while Streamline override, DLSS5, Smooth Motion, or RTX HDR were active) while keeping older shader caches from your other games intact.",
                TechnicalDetails = string.Join("\n", sys.ShaderCacheDirectories.Select(d => $"• {d}")),
                FixDescription = "Delete unlocked cache files modified within the last 72 hours inside NVIDIA DXCache, ComputeCache, and D3DSCache.",
                IsFixable = true,
                DefaultSelected = true,
                IsSelected = true,
                FixActionKey = "FIX_CLEAR_RECENT_SHADER_CACHE",
                Metadata = new Dictionary<string, string>
                {
                    ["Dirs"] = string.Join("|", sys.ShaderCacheDirectories)
                }
            });
        }

        if (sys.ShaderCacheDirectories.Count > 0 && sys.ShaderCacheSizeBytes > 0)
        {
            issues.Add(new DiagnosticIssue
            {
                Id = "sys_shader_cache",
                Title = $"Clear ALL NVIDIA & DirectX Shader Caches ({sys.ShaderCacheSizeBytes / (1024.0 * 1024.0):0.1} MB)",
                Category = IssueCategory.SystemAndLogs,
                Severity = IssueSeverity.Info,
                Summary = "Optional full wipe: clears all cached shaders across all games if recent-only cache cleanup isn't enough.",
                TechnicalDetails = string.Join("\n", sys.ShaderCacheDirectories.Select(d => $"• {d}")),
                FixDescription = "Delete all unlocked cache files inside NVIDIA DXCache, ComputeCache, and D3DSCache. Unchecked by default to preserve older games' shader caches.",
                IsFixable = true,
                DefaultSelected = false,
                IsSelected = false,
                FixActionKey = "FIX_CLEAR_SHADER_CACHE",
                Metadata = new Dictionary<string, string>
                {
                    ["Dirs"] = string.Join("|", sys.ShaderCacheDirectories)
                }
            });
        }

        // Order: Critical -> Warning -> Info -> Ok
        issues = issues.OrderBy(i => i.Severity).ToList();
        return new ScanResult { Summary = summary, Issues = issues };
    }

    public FixExecutionResult ApplyFixes(IEnumerable<DiagnosticIssue> selectedIssues, SystemSummary summary)
    {
        var result = new FixExecutionResult();
        var list = selectedIssues.Where(i => i.IsFixable && i.IsSelected).ToList();
        if (list.Count == 0)
        {
            result.Success = true;
            result.LogLines.Add("No fixable issues were selected.");
            return result;
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string backupDir = Path.Combine(BackupsRoot, $"backup_{stamp}");
        Directory.CreateDirectory(backupDir);
        result.BackupDirectory = backupDir;
        result.LogLines.Add($"Created backup directory: {backupDir}");

        var manifest = new BackupManifest
        {
            Timestamp = DateTime.Now.ToString("O"),
            BackupDirectory = backupDir
        };

        int fileSeq = 0;

        foreach (var issue in list)
        {
            result.LogLines.Add($"--- Processing: {issue.Title} ---");
            try
            {
                switch (issue.FixActionKey)
                {
                    case "FIX_DRS_GLOBAL":
                    {
                        var ids = ParseHexIds(issue.Metadata.GetValueOrDefault("SettingIds", ""));
                        var (ok, backedUp, logs) = _nvapi.ResetDrsSettings(isBaseProfile: true, "BASE", ids);
                        manifest.DrsEntries.AddRange(backedUp);
                        result.LogLines.AddRange(logs);
                        MarkIssue(issue, ok, result);
                        break;
                    }

                    case "FIX_DRS_PROFILE_MFG":
                    case "FIX_DRS_PROFILE_DLSS":
                    {
                        string profName = issue.Metadata.GetValueOrDefault("ProfileName", "Crimson Desert");
                        var ids = ParseHexIds(issue.Metadata.GetValueOrDefault("SettingIds", ""));
                        var (ok, backedUp, logs) = _nvapi.ResetDrsSettings(isBaseProfile: false, profName, ids);
                        manifest.DrsEntries.AddRange(backedUp);
                        result.LogLines.AddRange(logs);
                        MarkIssue(issue, ok, result);
                        break;
                    }

                    case "FIX_DISABLE_SMOOTH_MOTION_CD":
                    {
                        string profName = issue.Metadata.GetValueOrDefault("ProfileName", "Crimson Desert");
                        var updates = new (uint, uint)[]
                        {
                            (NvApiDrsService.DRS_SMOOTH_MOTION, 0u),
                            (NvApiDrsService.DRS_LOW_LATENCY_MODE, 0u),
                        };
                        var (ok, backedUp, logs) = _nvapi.SetDrsSettingsU32(isBaseProfile: false, profName, updates);
                        manifest.DrsEntries.AddRange(backedUp);
                        result.LogLines.AddRange(logs);
                        MarkIssue(issue, ok, result);
                        break;
                    }

                    case "FIX_DISABLE_RTX_HDR_CD":
                    {
                        string profName = issue.Metadata.GetValueOrDefault("ProfileName", "Crimson Desert");
                        var updates = new (uint, uint)[]
                        {
                            (NvApiDrsService.DRS_RTX_HDR_ENABLE, 0u),
                        };
                        var (ok, backedUp, logs) = _nvapi.SetDrsSettingsU32(isBaseProfile: false, profName, updates);
                        manifest.DrsEntries.AddRange(backedUp);
                        result.LogLines.AddRange(logs);
                        MarkIssue(issue, ok, result);
                        break;
                    }

                    case "FIX_DISABLE_DLSS_PRESET_CD":
                    {
                        string profName = issue.Metadata.GetValueOrDefault("ProfileName", "Crimson Desert");
                        var updates = new (uint, uint)[]
                        {
                            (NvApiDrsService.DRS_DLSS_MODEL_PRESET_PROFILE, 0u),
                        };
                        var (ok, backedUp, logs) = _nvapi.SetDrsSettingsU32(isBaseProfile: false, profName, updates);
                        manifest.DrsEntries.AddRange(backedUp);
                        result.LogLines.AddRange(logs);
                        MarkIssue(issue, ok, result);
                        break;
                    }

                    case "FIX_RESET_VSYNC_CD":
                    {
                        string profName = issue.Metadata.GetValueOrDefault("ProfileName", "Crimson Desert");
                        var updates = new (uint, uint)[]
                        {
                            (NvApiDrsService.DRS_VSYNC, NvApiDrsService.VSYNC_USE_APP),
                        };
                        var (ok, backedUp, logs) = _nvapi.SetDrsSettingsU32(isBaseProfile: false, profName, updates);
                        manifest.DrsEntries.AddRange(backedUp);
                        result.LogLines.AddRange(logs);
                        MarkIssue(issue, ok, result);
                        break;
                    }

                    case "FIX_CLEAR_RECENT_SHADER_CACHE":
                    {
                        var dirs = issue.Metadata.GetValueOrDefault("Dirs", "")
                            .Split('|', StringSplitOptions.RemoveEmptyEntries);
                        var (deleted, skipped) = RhiAndSystemService.ClearShaderCaches(dirs, TimeSpan.FromHours(72));
                        result.LogLines.Add($"Cleared {deleted} recent shader/compute cache file(s) ({skipped} locked file(s) safely skipped).");
                        MarkIssue(issue, true, result);
                        break;
                    }

                    case "FIX_PEARL_ABYSS_XML":
                    {
                        string xmlPath = issue.Metadata.GetValueOrDefault("FilePath", "");
                        if (File.Exists(xmlPath))
                        {
                            fileSeq++;
                            string rel = Path.Combine("xml", $"{fileSeq}_{Path.GetFileName(xmlPath)}");
                            string dst = Path.Combine(backupDir, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                            File.Copy(xmlPath, dst, overwrite: true);
                            manifest.Files.Add(new FileBackupEntry
                            {
                                OriginalPath = xmlPath,
                                BackupRelativePath = rel,
                                WasDirectory = false,
                                WasQuarantinedMove = false
                            });
                            result.LogLines.Add($"Backed up XML to {rel}");

                            var (ok, changes) = PearlAbyssConfigService.RepairXmlConfig(xmlPath);
                            result.LogLines.AddRange(changes);
                            MarkIssue(issue, ok, result);
                        }
                        else
                        {
                            result.LogLines.Add($"File not found: {xmlPath}");
                            MarkIssue(issue, false, result);
                        }
                        break;
                    }

                    case "FIX_QUARANTINE_GAME_MODS":
                    {
                        string raw = issue.Metadata.GetValueOrDefault("Paths", "");
                        bool allOk = true;
                        foreach (string entry in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (entry.Length < 3) continue;
                            bool isDir = entry[0] == 'D';
                            string src = entry[2..];
                            fileSeq++;
                            string safeName = $"{fileSeq}_{Path.GetFileName(src.TrimEnd('\\'))}";
                            string rel = Path.Combine("quarantine", safeName);
                            string dst = Path.Combine(backupDir, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                            try
                            {
                                if (isDir && Directory.Exists(src))
                                {
                                    MoveDirectoryCrossVolumeSafe(src, dst);
                                    manifest.Files.Add(new FileBackupEntry
                                    {
                                        OriginalPath = src,
                                        BackupRelativePath = rel,
                                        WasDirectory = true,
                                        WasQuarantinedMove = true
                                    });
                                    result.LogLines.Add($"Quarantined directory '{src}' -> '{rel}'");
                                }
                                else if (!isDir && File.Exists(src))
                                {
                                    File.Move(src, dst, overwrite: true);
                                    manifest.Files.Add(new FileBackupEntry
                                    {
                                        OriginalPath = src,
                                        BackupRelativePath = rel,
                                        WasDirectory = false,
                                        WasQuarantinedMove = true
                                    });
                                    result.LogLines.Add($"Quarantined file '{src}' -> '{rel}'");
                                }
                            }
                            catch (Exception ex)
                            {
                                allOk = false;
                                result.LogLines.Add($"ERROR quarantining '{src}': {ex.Message}");
                            }
                        }
                        MarkIssue(issue, allOk, result);
                        break;
                    }

                    case "FIX_RHI_CACHE":
                    {
                        string rhiPath = issue.Metadata.GetValueOrDefault("RhiPath", "");
                        string affected = issue.Metadata.GetValueOrDefault("AffectedFiles", "");
                        foreach (string f in affected.Split('|', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!File.Exists(f)) continue;
                            fileSeq++;
                            string rel = Path.Combine("rhi", $"{fileSeq}_{Path.GetFileName(f)}");
                            string dst = Path.Combine(backupDir, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                            File.Copy(f, dst, overwrite: true);
                            manifest.Files.Add(new FileBackupEntry
                            {
                                OriginalPath = f,
                                BackupRelativePath = rel,
                                WasDirectory = false,
                                WasQuarantinedMove = false
                            });
                            result.LogLines.Add($"Backed up RHI config '{Path.GetFileName(f)}' -> '{rel}'");
                        }

                        var (ok, logs) = RhiAndSystemService.CleanRhiCrimsonDesertEntries(rhiPath);
                        result.LogLines.AddRange(logs);
                        MarkIssue(issue, ok, result);
                        break;
                    }

                    case "FIX_CLEAR_SHADER_CACHE":
                    {
                        var dirs = issue.Metadata.GetValueOrDefault("Dirs", "")
                            .Split('|', StringSplitOptions.RemoveEmptyEntries);
                        var (deleted, skipped) = RhiAndSystemService.ClearShaderCaches(dirs);
                        result.LogLines.Add($"Cleared {deleted} shader cache file(s) ({skipped} locked file(s) safely skipped).");
                        MarkIssue(issue, true, result);
                        break;
                    }

                    default:
                        result.LogLines.Add($"Unknown fix action: {issue.FixActionKey}");
                        MarkIssue(issue, false, result);
                        break;
                }
            }
            catch (Exception ex)
            {
                result.LogLines.Add($"ERROR executing {issue.FixActionKey}: {ex.Message}");
                MarkIssue(issue, false, result);
            }
        }

        // Persist backup manifest
        string manifestPath = Path.Combine(backupDir, "backup_manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        result.LogLines.Add($"Saved rollback manifest: {manifestPath}");
        result.Success = result.FailedCount == 0;
        return result;
    }

    public string? GetLatestBackupManifestPath()
    {
        if (!Directory.Exists(BackupsRoot)) return null;
        return new DirectoryInfo(BackupsRoot)
            .EnumerateDirectories("backup_*")
            .OrderByDescending(d => d.Name)
            .Select(d => Path.Combine(d.FullName, "backup_manifest.json"))
            .FirstOrDefault(File.Exists);
    }

    public (bool Success, List<string> Logs) RestoreBackup(string manifestPath)
    {
        var logs = new List<string>();
        if (!File.Exists(manifestPath))
        {
            logs.Add($"Backup manifest not found: {manifestPath}");
            return (false, logs);
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath));
        if (manifest is null)
        {
            logs.Add("Failed to deserialize backup manifest.");
            return (false, logs);
        }

        bool allOk = true;
        string backupDir = Path.GetDirectoryName(manifestPath)!;

        // 1. Restore DRS settings
        if (manifest.DrsEntries.Count > 0)
        {
            var (ok, drsLogs) = _nvapi.RestoreDrsBackup(manifest.DrsEntries);
            logs.AddRange(drsLogs);
            if (!ok) allOk = false;
        }

        // 2. Restore files & quarantined items
        foreach (var entry in manifest.Files)
        {
            string src = Path.Combine(backupDir, entry.BackupRelativePath);
            try
            {
                if (entry.WasDirectory)
                {
                    if (Directory.Exists(src))
                    {
                        if (Directory.Exists(entry.OriginalPath))
                            Directory.Delete(entry.OriginalPath, recursive: true);
                        CopyDirectoryRecursive(src, entry.OriginalPath);
                        logs.Add($"Restored directory: {entry.OriginalPath}");
                    }
                }
                else if (File.Exists(src))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
                    File.Copy(src, entry.OriginalPath, overwrite: true);
                    logs.Add($"Restored file: {entry.OriginalPath}");
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                logs.Add($"ERROR restoring '{entry.OriginalPath}': {ex.Message}");
            }
        }

        return (allOk, logs);
    }

    private static void MarkIssue(DiagnosticIssue issue, bool ok, FixExecutionResult result)
    {
        if (ok)
        {
            result.FixedCount++;
            issue.FixStatus = "FIXED";
        }
        else
        {
            result.FailedCount++;
            issue.FixStatus = "FAILED";
        }
    }

    private static List<uint> ParseHexIds(string csv)
    {
        var ids = new List<uint>();
        foreach (string token in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string clean = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
            if (uint.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint val))
            {
                ids.Add(val);
            }
        }
        return ids;
    }

    private static void MoveDirectoryCrossVolumeSafe(string sourceDir, string destDir)
    {
        try
        {
            Directory.Move(sourceDir, destDir);
        }
        catch (IOException)
        {
            CopyDirectoryRecursive(sourceDir, destDir);
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string sub in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectoryRecursive(sub, Path.Combine(destDir, Path.GetFileName(sub)));
        }
    }
}
