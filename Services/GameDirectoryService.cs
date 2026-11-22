using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CrimsonDesertDlssFixer.Services;

public sealed class ModArtifactItem
{
    public string FullPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class CoreDllInfo
{
    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public string FileVersion { get; init; } = string.Empty;
    public bool IsNvidiaSigned { get; init; }
    public string SignerName { get; init; } = string.Empty;
}

public sealed class GameDirectoryScanResult
{
    public string InstallPath { get; init; } = string.Empty;
    public string Bin64Path { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public bool HasNonAsciiPath { get; init; }
    public List<ModArtifactItem> LeftoverArtifacts { get; init; } = new();
    public List<CoreDllInfo> CoreDlls { get; init; } = new();
    public string? StreamlineDiskVersion { get; init; }
}

public sealed class GameDirectoryService
{
    private static readonly string[] KnownModDirectories =
    {
        "_BACKUPNV",
        "OptiScaler",
        "reshade-shaders",
        "rhi_backup",
        "plugins",
        "scripts"
    };

    private static readonly (string Pattern, string Reason)[] KnownModFilePatterns =
    {
        (@"^nvngx_dlssnr\.dll$", "RHI / DLSS 5 Neural Rendering DLL (not shipped with vanilla Crimson Desert)"),
        (@"^nvngx_dlss_edit\.dll$", "Modified / patched DLSS DLL leftover"),
        (@"^nvngx\.dll$", "Injected NVNGX proxy DLL"),
        (@"^nvngx_dlssg_orig\.dll$", "Backup of original DLSS-G DLL left by FG/MFG mod"),
        (@"^nvngx_dlss_orig\.dll$", "Backup of original DLSS DLL left by mod"),
        (@"^dxgi\.dll$", "Injected DXGI proxy DLL (ReShade / OptiScaler / SpecialK / MFG)"),
        (@"^version\.dll$", "Injected version.dll proxy (RTX40MFG / Universal MFG / UAL64 / OptiScaler)"),
        (@"^winmm\.dll$", "Injected winmm.dll proxy (UAL64 / DLSS Enabler / MFG)"),
        (@"^d3d12\.dll$", "Injected d3d12.dll proxy wrapper"),
        (@"^d3d11\.dll$", "Injected d3d11.dll proxy wrapper"),
        (@"^dbghelp\.dll$", "Injected dbghelp.dll proxy wrapper"),
        (@"^wininet\.dll$", "Injected wininet.dll proxy wrapper"),
        (@"^xinput1_[34]\.dll$", "Injected xinput proxy wrapper"),
        (@"^mfgunlocker.*\.dll$", "Universal MFG Unlocker DLL"),
        (@"^rtx40?mfg.*", "RTX40MFG / Universal MFG Unlocker file"),
        (@"^optiscaler.*", "OptiScaler mod file"),
        (@"^optipatcher.*", "OptiPatcher mod file"),
        (@"^d3d12_optiscaler.*", "OptiScaler D3D12 hook"),
        (@"^dlssg_to_fsr3.*", "dlssg-to-fsr3 Frame Generation mod"),
        (@"^dlss[-_]?enabler.*", "DLSS Enabler mod file"),
        (@"^reshade.*\.(dll|ini|log|txt)$", "ReShade file"),
        (@"^renodx.*", "RenoDX HDR mod file"),
        (@"^hsf.*|^sloppy.*", "HSF (Hunter's Sloppy Frames) MFG mod file"),
        (@"^rtx[-_]?encore.*", "RTX Encore mod file"),
        (@"\.addon64$|\.addon$", "ReShade / RenoDX / ShortFuse / MFG addon"),
        (@"\.asi$", "ASI plugin (OptiPatcher / UAL64)"),
        (@"\.rhi_sentinel$", "RHI sentinel marker file"),
        (@"^dlss5.*", "DLSS 5 / Neural Rendering mod file"),
        (@"^shortfuse.*", "ShortFuse DLSS tool file"),
    };

    private static readonly string[] RequiredCoreDlls =
    {
        "nvngx_dlss.dll",
        "nvngx_dlssd.dll",
        "nvngx_dlssg.dll",
        "sl.interposer.dll",
        "sl.common.dll",
        "sl.dlss.dll",
        "sl.dlss_d.dll",
        "sl.dlss_g.dll",
        "sl.reflex.dll"
    };

    public string FindGameInstallPath(string? logDetectedPath, string rhiPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(logDetectedPath))
            candidates.Add(logDetectedPath);

        // 1. Check RHI game_library.json
        string rhiLib = Path.Combine(rhiPath, "game_library.json");
        if (File.Exists(rhiLib))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(rhiLib));
                if (doc.RootElement.TryGetProperty("Games", out var games) && games.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in games.EnumerateArray())
                    {
                        string name = g.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                        string path = g.TryGetProperty("InstallPath", out var p) ? p.GetString() ?? "" : "";
                        if (name.Contains("Crimson Desert", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(path))
                        {
                            candidates.Add(path);
                        }
                    }
                }
            }
            catch { }
        }

        // 2. Check Steam registry & libraryfolders.vdf
        foreach (string steamRoot in GetSteamRoots())
        {
            candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "Crimson Desert"));
            string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    string text = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string lib = m.Groups[1].Value.Replace(@"\\", @"\");
                        candidates.Add(Path.Combine(lib, "steamapps", "common", "Crimson Desert"));
                    }
                }
                catch { }
            }
        }

        // 3. Common defaults
        candidates.Add(@"C:\Program Files (x86)\Steam\steamapps\common\Crimson Desert");
        candidates.Add(@"D:\SteamLibrary\steamapps\common\Crimson Desert");

        foreach (string c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "bin64", "CrimsonDesert.exe")))
                return Path.GetFullPath(c);
        }

        foreach (string c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(c))
                return Path.GetFullPath(c);
        }

        return string.Empty;
    }

    private static IEnumerable<string> GetSteamRoots()
    {
        var roots = new List<string>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string p && Directory.Exists(p))
                roots.Add(p.Replace('/', '\\'));
        }
        catch { }

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (k?.GetValue("InstallPath") is string p && Directory.Exists(p))
                roots.Add(p);
        }
        catch { }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public GameDirectoryScanResult ScanGameDirectory(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return new GameDirectoryScanResult { InstallPath = installPath, Exists = false };
        }

        string bin64 = Path.Combine(installPath, "bin64");
        bool hasBin64 = Directory.Exists(bin64);
        bool hasNonAscii = installPath.Any(ch => ch > 127);

        var artifacts = new List<ModArtifactItem>();
        var coreDlls = new List<CoreDllInfo>();

        var dirsToInspect = new List<string> { installPath };
        if (hasBin64) dirsToInspect.Add(bin64);

        foreach (string dir in dirsToInspect)
        {
            // Check known mod subdirectories
            foreach (string subName in KnownModDirectories)
            {
                string subPath = Path.Combine(dir, subName);
                if (Directory.Exists(subPath))
                {
                    long totalBytes = 0;
                    try
                    {
                        totalBytes = Directory.EnumerateFiles(subPath, "*", SearchOption.AllDirectories)
                            .Sum(f => new FileInfo(f).Length);
                    }
                    catch { }

                    string reason = subName.Equals("_BACKUPNV", StringComparison.OrdinalIgnoreCase)
                        ? "Leftover DLSS 5 / Neural Rendering / RHI backup folder containing modded NVNGX DLLs"
                        : $"Leftover mod framework directory ({subName})";

                    artifacts.Add(new ModArtifactItem
                    {
                        FullPath = subPath,
                        RelativePath = Path.GetRelativePath(installPath, subPath),
                        IsDirectory = true,
                        SizeBytes = totalBytes,
                        Reason = reason
                    });
                }
            }

            // Check files in this directory
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    string name = Path.GetFileName(file);
                    foreach (var (pattern, reason) in KnownModFilePatterns)
                    {
                        if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase))
                        {
                            artifacts.Add(new ModArtifactItem
                            {
                                FullPath = file,
                                RelativePath = Path.GetRelativePath(installPath, file),
                                IsDirectory = false,
                                SizeBytes = new FileInfo(file).Length,
                                Reason = reason
                            });
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        string? slDiskVersion = null;
        if (hasBin64)
        {
            foreach (string dllName in RequiredCoreDlls)
            {
                string full = Path.Combine(bin64, dllName);
                if (!File.Exists(full))
                {
                    coreDlls.Add(new CoreDllInfo { FileName = dllName, FullPath = full, Exists = false });
                    continue;
                }

                string ver = string.Empty;
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(full);
                    ver = $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}.{vi.FilePrivatePart}";
                }
                catch { }

                if (dllName.Equals("sl.dlss.dll", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ver))
                {
                    slDiskVersion = ver;
                }

                var (isNvSigned, signer) = CheckNvidiaSignature(full);
                coreDlls.Add(new CoreDllInfo
                {
                    FileName = dllName,
                    FullPath = full,
                    Exists = true,
                    FileVersion = ver,
                    IsNvidiaSigned = isNvSigned,
                    SignerName = signer
                });
            }
        }

        return new GameDirectoryScanResult
        {
            InstallPath = installPath,
            Bin64Path = bin64,
            Exists = true,
            HasNonAsciiPath = hasNonAscii,
            LeftoverArtifacts = artifacts,
            CoreDlls = coreDlls,
            StreamlineDiskVersion = slDiskVersion
        };
    }

    private static (bool IsNvidiaSigned, string Signer) CheckNvidiaSignature(string filePath)
    {
        try
        {
            #pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            #pragma warning restore SYSLIB0057
            string subject = cert.Subject ?? string.Empty;
            bool isNv = subject.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            return (isNv, subject);
        }
        catch
        {
            return (false, "Unsigned or invalid signature");
        }
    }
}
