using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace CrimsonDesertDlssFixer.Services;

public sealed class RhiScanResult
{
    public string RhiPath { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public bool IsRhiRunning { get; init; }
    public List<string> StaleFindings { get; init; } = new();
    public List<string> AffectedFiles { get; init; } = new();
}

public sealed class SystemChecksResult
{
    public string GpuName { get; init; } = "Unknown";
    public string DriverVersion { get; init; } = "Unknown";
    public string HagsStatus { get; init; } = "Windows Default";
    public bool IsHagsExplicitlyDisabled { get; init; }
    public List<string> NgxCoreAnomalies { get; init; } = new();
    public List<string> SuspiciousEnvVars { get; init; } = new();
    public List<string> SuspiciousVulkanLayers { get; init; } = new();
    public long ShaderCacheSizeBytes { get; init; }
    public long RecentShaderCacheSizeBytes { get; init; }
    public int RecentShaderCacheFileCount { get; init; }
    public List<string> ShaderCacheDirectories { get; init; } = new();
}

public sealed class RhiAndSystemService
{
    public static string GetDefaultRhiPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RHI");

    public RhiScanResult ScanRhi(string rhiPath)
    {
        bool running = Process.GetProcessesByName("RHI").Length > 0;
        if (!Directory.Exists(rhiPath))
        {
            return new RhiScanResult { RhiPath = rhiPath, Exists = false, IsRhiRunning = running };
        }

        var findings = new List<string>();
        var files = new List<string>();

        // 1. dlss_trusted_paths.json
        string trustedPath = Path.Combine(rhiPath, "dlss_trusted_paths.json");
        if (File.Exists(trustedPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(trustedPath))?.AsObject();
                if (root is not null)
                {
                    foreach (var kvp in root)
                    {
                        if (kvp.Key.Contains("Crimson Desert", StringComparison.OrdinalIgnoreCase))
                        {
                            string json = kvp.Value?.ToJsonString() ?? "";
                            if (json.Contains("_BACKUPNV", StringComparison.OrdinalIgnoreCase) ||
                                json.Contains("dlssnr", StringComparison.OrdinalIgnoreCase))
                            {
                                findings.Add($"dlss_trusted_paths.json: '{kvp.Key}' still references leftover DLSS-NR / _BACKUPNV paths.");
                            }
                            else
                            {
                                findings.Add($"dlss_trusted_paths.json: cached DLSS path entry for '{kvp.Key}'.");
                            }
                            if (!files.Contains(trustedPath)) files.Add(trustedPath);
                        }
                    }
                }
            }
            catch { }
        }

        // 2. game_library.json (DlssPathsCache)
        string gameLibPath = Path.Combine(rhiPath, "game_library.json");
        if (File.Exists(gameLibPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(gameLibPath))?.AsObject();
                if (root?["DlssPathsCache"] is JsonObject cache)
                {
                    foreach (var kvp in cache)
                    {
                        if (kvp.Key.Contains("Crimson Desert", StringComparison.OrdinalIgnoreCase))
                        {
                            string json = kvp.Value?.ToJsonString() ?? "";
                            if (json.Contains("_BACKUPNV", StringComparison.OrdinalIgnoreCase) ||
                                json.Contains("nvngx_dlssnr", StringComparison.OrdinalIgnoreCase))
                            {
                                findings.Add($"game_library.json (DlssPathsCache): '{kvp.Key}' points DlssnrPath to _BACKUPNV\\nvngx_dlssnr.dll.");
                                if (!files.Contains(gameLibPath)) files.Add(gameLibPath);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // 3. settings.json (ShortFuse auto-config, OptiScaler FG hooks, HDR/Res overrides)
        string settingsPath = Path.Combine(rhiPath, "settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject();
                if (root is not null)
                {
                    string[] keysToCheck =
                    {
                        "SfAutoConfigEnabled",
                        "OsFgInput",
                        "OsFgOutput",
                        "OsFgNvngxReplacement",
                        "OsDeployDlssEnabler",
                        "NrMethodOverrides",
                        "OsVariantOverrides",
                        "DllOverrides"
                    };

                    foreach (string key in keysToCheck)
                    {
                        string raw = root[key]?.ToString() ?? "";
                        if (raw.Contains("Crimson Desert", StringComparison.OrdinalIgnoreCase))
                        {
                            findings.Add($"settings.json -> '{key}' contains an active/cached Crimson Desert override entry.");
                            if (!files.Contains(settingsPath)) files.Add(settingsPath);
                        }
                    }
                }
            }
            catch { }
        }

        return new RhiScanResult
        {
            RhiPath = rhiPath,
            Exists = true,
            IsRhiRunning = running,
            StaleFindings = findings,
            AffectedFiles = files
        };
    }

    public static (bool Success, List<string> Logs) CleanRhiCrimsonDesertEntries(string rhiPath)
    {
        var logs = new List<string>();
        bool anyError = false;

        // 1. Clean dlss_trusted_paths.json
        string trustedPath = Path.Combine(rhiPath, "dlss_trusted_paths.json");
        if (File.Exists(trustedPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(trustedPath))?.AsObject();
                if (root is not null)
                {
                    var keys = root.Select(k => k.Key)
                        .Where(k => k.Contains("Crimson Desert", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (string k in keys)
                    {
                        root.Remove(k);
                        logs.Add($"Removed '{k}' from RHI dlss_trusted_paths.json.");
                    }
                    if (keys.Count > 0)
                    {
                        File.WriteAllText(trustedPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
            }
            catch (Exception ex)
            {
                anyError = true;
                logs.Add($"ERROR cleaning dlss_trusted_paths.json: {ex.Message}");
            }
        }

        // 2. Clean game_library.json -> DlssPathsCache
        string gameLibPath = Path.Combine(rhiPath, "game_library.json");
        if (File.Exists(gameLibPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(gameLibPath))?.AsObject();
                if (root?["DlssPathsCache"] is JsonObject cache)
                {
                    var keys = cache.Select(k => k.Key)
                        .Where(k => k.Contains("Crimson Desert", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (string k in keys)
                    {
                        cache.Remove(k);
                        logs.Add($"Removed stale DlssPathsCache entry '{k}' from RHI game_library.json.");
                    }
                    if (keys.Count > 0)
                    {
                        File.WriteAllText(gameLibPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
            }
            catch (Exception ex)
            {
                anyError = true;
                logs.Add($"ERROR cleaning game_library.json: {ex.Message}");
            }
        }

        // 3. Clean settings.json (Note: RHI stores nested JSON strings as string values!)
        string settingsPath = Path.Combine(rhiPath, "settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject();
                if (root is not null)
                {
                    bool modified = false;
                    string[] keysToClean =
                    {
                        "SfAutoConfigEnabled",
                        "OsFgInput",
                        "OsFgOutput",
                        "OsFgNvngxReplacement",
                        "OsDeployDlssEnabler",
                        "NrMethodOverrides",
                        "OsVariantOverrides",
                        "DllOverrides"
                    };

                    foreach (string key in keysToClean)
                    {
                        if (!root.TryGetPropertyValue(key, out var valNode) || valNode is null) continue;
                        if (valNode is JsonValue jv && jv.TryGetValue<string>(out string? innerJson) && !string.IsNullOrWhiteSpace(innerJson))
                        {
                            if (!innerJson.Contains("Crimson Desert", StringComparison.OrdinalIgnoreCase)) continue;
                            try
                            {
                                var inner = JsonNode.Parse(innerJson);
                                if (inner is JsonArray arr)
                                {
                                    for (int i = arr.Count - 1; i >= 0; i--)
                                    {
                                        if ((arr[i]?.ToString() ?? "").Contains("Crimson Desert", StringComparison.OrdinalIgnoreCase))
                                        {
                                            arr.RemoveAt(i);
                                            modified = true;
                                            logs.Add($"Removed Crimson Desert from RHI settings.json -> {key}.");
                                        }
                                    }
                                    root[key] = arr.ToJsonString();
                                }
                                else if (inner is JsonObject obj)
                                {
                                    var rm = obj.Select(k => k.Key)
                                        .Where(k => k.Contains("Crimson Desert", StringComparison.OrdinalIgnoreCase))
                                        .ToList();
                                    foreach (string k in rm)
                                    {
                                        obj.Remove(k);
                                        modified = true;
                                        logs.Add($"Removed '{k}' from RHI settings.json -> {key}.");
                                    }
                                    root[key] = obj.ToJsonString();
                                }
                            }
                            catch { }
                        }
                    }

                    if (modified)
                    {
                        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
            }
            catch (Exception ex)
            {
                anyError = true;
                logs.Add($"ERROR cleaning settings.json: {ex.Message}");
            }
        }

        return (!anyError, logs);
    }

    public SystemChecksResult ScanSystem()
    {
        var (gpuName, driverVersion) = ReadNvidiaGpuInfo();
        var (hagsStatus, hagsDisabled) = ReadHagsStatus();

        var ngxAnomalies = new List<string>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\NVIDIA Corporation\Global\NGXCore");
            if (k is not null)
            {
                foreach (string valName in k.GetValueNames())
                {
                    if (valName.Equals("FullPath", StringComparison.OrdinalIgnoreCase) ||
                        valName.Equals("Installed", StringComparison.OrdinalIgnoreCase))
                        continue;

                    object? v = k.GetValue(valName);
                    if (valName.Contains("SignatureOverride", StringComparison.OrdinalIgnoreCase) ||
                        valName.Contains("Override", StringComparison.OrdinalIgnoreCase) ||
                        valName.Contains("ShowDlssIndicator", StringComparison.OrdinalIgnoreCase) ||
                        valName.Contains("EnableOther", StringComparison.OrdinalIgnoreCase))
                    {
                        ngxAnomalies.Add($"HKLM\\...\\NGXCore\\{valName} = {v}");
                    }
                }
            }
        }
        catch { }

        var envAnomalies = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = entry.Key?.ToString() ?? "";
            string val = entry.Value?.ToString() ?? "";
            if (name.StartsWith("NGX_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("SL_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("OPTISCALER_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("DXVK_NVAPI", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("DLSS", StringComparison.OrdinalIgnoreCase))
            {
                envAnomalies.Add($"{name}={val}");
            }
        }

        var vulkanAnomalies = new List<string>();
        foreach (var rootKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = rootKey.OpenSubKey(@"SOFTWARE\Khronos\Vulkan\ImplicitLayers");
                if (k is null) continue;
                foreach (string valName in k.GetValueNames())
                {
                    if (valName.Contains("ReShade", StringComparison.OrdinalIgnoreCase) ||
                        valName.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase))
                    {
                        object? v = k.GetValue(valName);
                        if (v is int iv && iv == 0)
                        {
                            vulkanAnomalies.Add($"{valName} (enabled)");
                        }
                    }
                }
            }
            catch { }
        }

        // Shader caches (DXCache, D3DSCache, and Roaming\NVIDIA\ComputeCache)
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var cacheDirs = new[]
        {
            Path.Combine(localAppData, "NVIDIA", "DXCache"),
            Path.Combine(roamingAppData, "NVIDIA", "ComputeCache"),
            Path.Combine(localAppData, "D3DSCache"),
        }.Where(Directory.Exists).ToList();

        long totalCacheBytes = 0;
        long recentCacheBytes = 0;
        int recentCacheCount = 0;
        DateTime recentCutoff = DateTime.UtcNow.AddHours(-72);
        foreach (string dir in cacheDirs)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        totalCacheBytes += fi.Length;
                        if (fi.LastWriteTimeUtc >= recentCutoff)
                        {
                            recentCacheBytes += fi.Length;
                            recentCacheCount++;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        return new SystemChecksResult
        {
            GpuName = gpuName,
            DriverVersion = driverVersion,
            HagsStatus = hagsStatus,
            IsHagsExplicitlyDisabled = hagsDisabled,
            NgxCoreAnomalies = ngxAnomalies,
            SuspiciousEnvVars = envAnomalies,
            SuspiciousVulkanLayers = vulkanAnomalies,
            ShaderCacheSizeBytes = totalCacheBytes,
            RecentShaderCacheSizeBytes = recentCacheBytes,
            RecentShaderCacheFileCount = recentCacheCount,
            ShaderCacheDirectories = cacheDirs
        };
    }

    public static (int DeletedFiles, int SkippedLockedFiles) ClearShaderCaches(IEnumerable<string> cacheDirs, TimeSpan? maxAge = null)
    {
        int deleted = 0;
        int skipped = 0;
        DateTime? cutoff = maxAge.HasValue ? DateTime.UtcNow - maxAge.Value : null;
        foreach (string dir in cacheDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (cutoff.HasValue && File.GetLastWriteTimeUtc(file) < cutoff.Value)
                        continue;
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    skipped++;
                }
            }
        }
        return (deleted, skipped);
    }

    private static (string GpuName, string DriverVersion) ReadNvidiaGpuInfo()
    {
        string bestName = "Unknown GPU";
        string bestDriver = "Unknown";
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is not null)
            {
                foreach (string sub in cls.GetSubKeyNames())
                {
                    if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                    using var dev = cls.OpenSubKey(sub);
                    if (dev is null) continue;
                    string desc = dev.GetValue("DriverDesc")?.ToString() ?? "";
                    string ver = dev.GetValue("DriverVersion")?.ToString() ?? "";
                    if (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    {
                        bestName = desc;
                        bestDriver = FormatNvidiaDriverVersion(ver);
                        break;
                    }
                    if (bestName == "Unknown GPU" && !string.IsNullOrWhiteSpace(desc))
                    {
                        bestName = desc;
                        bestDriver = ver;
                    }
                }
            }
        }
        catch { }
        return (bestName, bestDriver);
    }

    private static string FormatNvidiaDriverVersion(string wddmVersion)
    {
        // e.g. "32.0.16.2124" -> "621.24 (32.0.16.2124)"
        var parts = wddmVersion.Split('.');
        if (parts.Length == 4 && parts[2].Length >= 1 && parts[3].Length == 4)
        {
            string nvVer = $"{parts[2][^1]}{parts[3][..2]}.{parts[3][2..]}";
            return $"{nvVer} ({wddmVersion})";
        }
        return wddmVersion;
    }

    private static (string Status, bool ExplicitlyDisabled) ReadHagsStatus()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            object? val = k?.GetValue("HwSchMode");
            if (val is int iv)
            {
                return iv switch
                {
                    2 => ("Enabled (HwSchMode=2)", false),
                    1 => ("DISABLED (HwSchMode=1 — breaks DLSS FG / MFG!)", true),
                    _ => ($"Custom (HwSchMode={iv})", false)
                };
            }
            return ("Windows 11 Default (Enabled on RTX GPUs)", false);
        }
        catch
        {
            return ("Unknown", false);
        }
    }
}
