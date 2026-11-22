using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CrimsonDesertDlssFixer.Models;

namespace CrimsonDesertDlssFixer.Services;

public sealed class XmlAnalysisResult
{
    public string FilePath { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public bool IsReadOnly { get; init; }
    public bool HasCorruptedNumFramesEnableGroup { get; init; }
    public int RemovedMfgElementCount { get; init; }
    public string? NumFramesValue { get; init; }
    public string? UpscaleModeSelect { get; init; }
    public string? UpscaleResolution { get; init; }
    public string? FrameGenerationModeSelect { get; init; }
    public bool IsNativeHdrEnabled { get; init; }
    public bool IsVsyncOff { get; init; }
    public bool IsMalformedXml { get; init; }

    public bool HasAnyIssue(bool isRtx50Series, bool isRtx40OrOlder)
    {
        bool badNumFrames = IsMalformedXml;
        if (!string.IsNullOrWhiteSpace(NumFramesValue))
        {
            if (!int.TryParse(NumFramesValue, out int n) || n < 1 || n > 6)
                badNumFrames = true;
            else if (isRtx40OrOlder && n > 2)
                badNumFrames = true;
        }
        // Only RTX 50-series (Blackwell) natively supports 3x/4x/6x MFG, where removedElements strips native options.
        // On RTX 20/30/40 GPUs, Crimson Desert itself writes removedElements to hide unsupported Blackwell multipliers.
        if (isRtx50Series && HasCorruptedNumFramesEnableGroup)
            badNumFrames = true;

        return IsReadOnly
            || badNumFrames
            || IsSuspiciousUpscaleMode(UpscaleModeSelect);
    }

    public static bool IsSuspiciousUpscaleMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return false;
        // Native Crimson Desert supports "NVIDIA DLSS 4.5", "NVIDIA DLSS 4.5 L", "NVIDIA DLSS 4.5 M", etc.
        // Only flag genuinely modded/non-native strings (DLSS 5 Neural Rendering / OptiScaler).
        return mode.Contains("DLSS 5", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("DLSS5", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("Neural", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSuspiciousFrameGenMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return false;
        return mode.Contains("FSR", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("XeSS", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class LogAnalysisResult
{
    public string LatestLogPath { get; init; } = string.Empty;
    public string? DetectedGamePath { get; init; }
    public string? StreamlineDlssVersion { get; init; }
    public string? StreamlineDlssRrVersion { get; init; }
    public string? StreamlineDlssGVersion { get; init; }
    public string? StreamlineReflexVersion { get; init; }
    public int MissingDepthTagErrors { get; init; }
    public int MissingMotionVectorErrors { get; init; }
    public int ProxyBufferNullErrors { get; init; }
    public int TotalStreamlineErrors { get; init; }
}

public sealed class PearlAbyssConfigService
{
    public static string GetDefaultPearlAbyssPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pearl Abyss");

    public List<XmlAnalysisResult> AnalyzeXmlConfigs(string pearlAbyssRoot)
    {
        var results = new List<XmlAnalysisResult>();
        var candidates = new[]
        {
            Path.Combine(pearlAbyssRoot, "CD", "user_engine_option_save.xml"),
            Path.Combine(pearlAbyssRoot, "CD", "save", "user_engine_option_save.xml"),
        };

        foreach (string path in candidates)
        {
            if (!File.Exists(path)) continue;
            results.Add(AnalyzeSingleXml(path));
        }

        return results;
    }

    public static XmlAnalysisResult AnalyzeSingleXml(string path)
    {
        bool isReadOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        bool hasCorruptedEnableGroup = false;
        bool isMalformed = false;
        bool isHdrEnabled = false;
        bool isVsyncOff = false;
        int removedCount = 0;
        string? numFramesVal = null;
        string? upscaleMode = null;
        string? upscaleRes = null;
        string? fgMode = null;

        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is not null)
            {
                var numFramesEl = root.Descendants("OptionStringVector")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "_numFramesToGenerate");
                if (numFramesEl is not null)
                {
                    numFramesVal = (string?)numFramesEl.Attribute("_value");
                    var enableGroup = numFramesEl.Elements("Vector")
                        .FirstOrDefault(v => (string?)v.Attribute("Name") == "_enableGroup");
                    if (enableGroup is not null)
                    {
                        bool isOverrided = string.Equals((string?)enableGroup.Attribute("isOverrided"), "true", StringComparison.OrdinalIgnoreCase);
                        var removed = enableGroup.Element("removedElements");
                        int.TryParse((string?)removed?.Attribute("count"), out removedCount);
                        if (isOverrided || removedCount > 0)
                        {
                            hasCorruptedEnableGroup = true;
                        }
                    }
                }

                var upscaleEl = root.Descendants("OptionStringVector")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "_upscaleModeSelect");
                upscaleMode = (string?)upscaleEl?.Attribute("_value");

                var fgEl = root.Descendants("OptionStringVector")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "_frameGenerationModeSelect");
                fgMode = (string?)fgEl?.Attribute("_value");

                var resEl = root.Descendants("EnumSelectResolutionScale")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "_upscaleResolution");
                upscaleRes = (string?)resEl?.Attribute("_select");

                var hdrEl = root.Descendants("OptionBool")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "_enableHDR");
                isHdrEnabled = string.Equals((string?)hdrEl?.Attribute("_value"), "True", StringComparison.OrdinalIgnoreCase);

                var vsyncEl = root.Descendants("OptionStringVector")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "_enableVsync");
                isVsyncOff = string.Equals((string?)vsyncEl?.Attribute("_value"), "Off", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            isMalformed = true;
            hasCorruptedEnableGroup = true;
        }

        return new XmlAnalysisResult
        {
            FilePath = path,
            Exists = true,
            IsReadOnly = isReadOnly,
            HasCorruptedNumFramesEnableGroup = hasCorruptedEnableGroup,
            RemovedMfgElementCount = removedCount,
            NumFramesValue = numFramesVal,
            UpscaleModeSelect = upscaleMode,
            UpscaleResolution = upscaleRes,
            FrameGenerationModeSelect = fgMode,
            IsNativeHdrEnabled = isHdrEnabled,
            IsVsyncOff = isVsyncOff,
            IsMalformedXml = isMalformed
        };
    }

    /// <summary>
    /// Surgically repairs user_engine_option_save.xml so Crimson Desert restores clean DLSS & MFG options
    /// while preserving controls, audio, graphics, HDR, and never touching game save slots.
    /// </summary>
    public static (bool Success, List<string> Changes) RepairXmlConfig(string xmlPath)
    {
        var changes = new List<string>();
        if (!File.Exists(xmlPath))
        {
            changes.Add($"File not found: {xmlPath}");
            return (false, changes);
        }

        // Clear Read-Only attribute if present
        var attrs = File.GetAttributes(xmlPath);
        if ((attrs & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(xmlPath, attrs & ~FileAttributes.ReadOnly);
            changes.Add("Removed Read-Only attribute.");
        }

        var doc = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
        var root = doc.Root;
        if (root is null)
        {
            changes.Add("XML root element missing.");
            return (false, changes);
        }

        // 1. Remove corrupted/overridden _numFramesToGenerate (which strips MFG options via removedElements)
        var numFramesNodes = root.Descendants("OptionStringVector")
            .Where(e => (string?)e.Attribute("Name") == "_numFramesToGenerate")
            .ToList();
        foreach (var node in numFramesNodes)
        {
            node.Remove();
            changes.Add("Removed corrupted/overridden '_numFramesToGenerate' node (restores native MFG/FG multiplier options).");
        }

        // 2. Remove stale/conflicting _frameGenerationModeSelect so engine re-detects NVIDIA DLSS FG/MFG cleanly
        var fgNodes = root.Descendants("OptionStringVector")
            .Where(e => (string?)e.Attribute("Name") == "_frameGenerationModeSelect")
            .ToList();
        foreach (var node in fgNodes)
        {
            string oldVal = (string?)node.Attribute("_value") ?? "";
            node.Remove();
            changes.Add($"Cleared '_frameGenerationModeSelect' (was '{oldVal}') so NVIDIA DLSS Frame Generation / MFG can re-initialize.");
        }

        // 3. Remove stale/modded _upscaleModeSelect (e.g. "NVIDIA DLSS 4.5 L")
        var upscaleNodes = root.Descendants("OptionStringVector")
            .Where(e => (string?)e.Attribute("Name") == "_upscaleModeSelect")
            .ToList();
        foreach (var node in upscaleNodes)
        {
            string oldVal = (string?)node.Attribute("_value") ?? "";
            if (XmlAnalysisResult.IsSuspiciousUpscaleMode(oldVal))
            {
                node.Remove();
                changes.Add($"Cleared stale modded '_upscaleModeSelect' (was '{oldVal}').");
            }
        }

        // 4. Reset forced 33% Ultra Performance resolution scale if stuck
        var resNodes = root.Descendants("EnumSelectResolutionScale")
            .Where(e => (string?)e.Attribute("Name") == "_upscaleResolution")
            .ToList();
        foreach (var node in resNodes)
        {
            string oldVal = (string?)node.Attribute("_select") ?? "";
            if (string.Equals(oldVal, "33%", StringComparison.OrdinalIgnoreCase))
            {
                node.SetAttributeValue("_select", "67%");
                changes.Add("Reset '_upscaleResolution' from forced '33%' (Ultra Performance) to '67%' (Quality).");
            }
        }

        // Clean empty _resolutionOption container if all its children were removed
        var emptyResOptions = root.Descendants("EngineOptionResolution")
            .Where(e => !e.HasElements)
            .ToList();
        foreach (var empty in emptyResOptions)
        {
            empty.Remove();
        }

        if (changes.Count > 0)
        {
            doc.Save(xmlPath);
        }

        return (true, changes);
    }

    public LogAnalysisResult? AnalyzeLatestLauncherLog(string pearlAbyssRoot)
    {
        string logDir = Path.Combine(pearlAbyssRoot, "log");
        if (!Directory.Exists(logDir)) return null;

        var latest = new DirectoryInfo(logDir)
            .EnumerateFiles("Launcher_*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latest is null) return null;

        string? gamePath = null;
        string? slDlss = null;
        string? slDlssRr = null;
        string? slDlssG = null;
        string? slReflex = null;
        int depthErrs = 0;
        int mvErrs = 0;
        int proxyNullErrs = 0;
        int totalSlErrs = 0;

        try
        {
            using var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (gamePath is null && line.Contains("added_path:", StringComparison.OrdinalIgnoreCase))
                {
                    var m = Regex.Match(line, @"added_path:\s*([^\]\r\n]+?)/dlc/", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        gamePath = m.Groups[1].Value.Replace('/', '\\').Trim();
                    }
                }

                if (line.Contains("NVIDIA Streamline", StringComparison.OrdinalIgnoreCase))
                {
                    var m = Regex.Match(line, @"NVIDIA Streamline (DLSS-RR|DLSS-G|DLSS|Reflex) version:\s*([0-9\.]+)", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string comp = m.Groups[1].Value.ToUpperInvariant();
                        string ver = m.Groups[2].Value;
                        switch (comp)
                        {
                            case "DLSS": slDlss = ver; break;
                            case "DLSS-RR": slDlssRr = ver; break;
                            case "DLSS-G": slDlssG = ver; break;
                            case "REFLEX": slReflex = ver; break;
                        }
                    }
                }

                if (line.Contains("[streamline][error]", StringComparison.OrdinalIgnoreCase))
                {
                    totalSlErrs++;
                    if (line.Contains("kBufferTypeDepth", StringComparison.OrdinalIgnoreCase)) depthErrs++;
                    if (line.Contains("kBufferTypeMotionVectors", StringComparison.OrdinalIgnoreCase)) mvErrs++;
                    if (line.Contains("proxyBuffer is NULL", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("resizeProxyBuffers", StringComparison.OrdinalIgnoreCase)) proxyNullErrs++;
                }
            }
        }
        catch
        {
            // Ignore read errors on locked files
        }

        return new LogAnalysisResult
        {
            LatestLogPath = latest.FullName,
            DetectedGamePath = gamePath,
            StreamlineDlssVersion = slDlss,
            StreamlineDlssRrVersion = slDlssRr,
            StreamlineDlssGVersion = slDlssG,
            StreamlineReflexVersion = slReflex,
            MissingDepthTagErrors = depthErrs,
            MissingMotionVectorErrors = mvErrs,
            ProxyBufferNullErrors = proxyNullErrs,
            TotalStreamlineErrors = totalSlErrs
        };
    }
}
