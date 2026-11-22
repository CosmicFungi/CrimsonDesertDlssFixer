using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CrimsonDesertDlssFixer.Models;

public enum IssueSeverity
{
    Critical,
    Warning,
    Info,
    Ok
}

public enum IssueCategory
{
    NvidiaDrsGlobal,
    NvidiaDrsProfile,
    PearlAbyssConfig,
    GameDirectory,
    RhiCache,
    SystemAndLogs
}

public sealed class DiagnosticIssue : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _fixStatus = string.Empty;

    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public IssueCategory Category { get; init; }
    public IssueSeverity Severity { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string TechnicalDetails { get; init; } = string.Empty;
    public string FixDescription { get; init; } = string.Empty;
    public bool IsFixable { get; init; }
    public bool DefaultSelected { get; init; }
    public string FixActionKey { get; init; } = string.Empty;
    public Dictionary<string, string> Metadata { get; init; } = new();

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public string FixStatus
    {
        get => _fixStatus;
        set
        {
            if (_fixStatus != value)
            {
                _fixStatus = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public string SeverityLabel => Severity switch
    {
        IssueSeverity.Critical => "CRITICAL",
        IssueSeverity.Warning => "WARNING",
        IssueSeverity.Info => "INFO",
        IssueSeverity.Ok => "OK",
        _ => "INFO"
    };

    [JsonIgnore]
    public string CategoryLabel => Category switch
    {
        IssueCategory.NvidiaDrsGlobal => "NVIDIA Driver (Global)",
        IssueCategory.NvidiaDrsProfile => "NVIDIA Driver (Crimson Desert)",
        IssueCategory.PearlAbyssConfig => "Pearl Abyss AppData XML",
        IssueCategory.GameDirectory => "Game Directory (bin64)",
        IssueCategory.RhiCache => "RHI Mod Manager Cache",
        IssueCategory.SystemAndLogs => "System & Streamline Logs",
        _ => Category.ToString()
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SystemSummary
{
    public string GpuName { get; set; } = "Unknown";
    public string DriverVersion { get; set; } = "Unknown";
    public string HagsStatus { get; set; } = "Unknown";
    public string GameInstallPath { get; set; } = string.Empty;
    public string Bin64Path { get; set; } = string.Empty;
    public string PearlAbyssPath { get; set; } = string.Empty;
    public string RhiPath { get; set; } = string.Empty;
    public bool IsRhiRunning { get; set; }
    public string LatestLogFile { get; set; } = string.Empty;
    public string StreamlineLogSummary { get; set; } = "No log analyzed";
    public DateTime ScannedAt { get; set; } = DateTime.Now;
}

public sealed class ScanResult
{
    public SystemSummary Summary { get; init; } = new();
    public List<DiagnosticIssue> Issues { get; init; } = new();
}

public sealed class DrsBackupEntry
{
    public string ProfileName { get; set; } = string.Empty;
    public bool IsBaseProfile { get; set; }
    public uint SettingId { get; set; }
    public string SettingName { get; set; } = string.Empty;
    public uint SettingType { get; set; }
    public uint U32Value { get; set; }
    public string? StringValue { get; set; }
    public bool WasAbsentBeforeFix { get; set; }
}

public sealed class FileBackupEntry
{
    public string OriginalPath { get; set; } = string.Empty;
    public string BackupRelativePath { get; set; } = string.Empty;
    public bool WasDirectory { get; set; }
    public bool WasQuarantinedMove { get; set; }
}

public sealed class BackupManifest
{
    public string Timestamp { get; set; } = string.Empty;
    public string BackupDirectory { get; set; } = string.Empty;
    public List<DrsBackupEntry> DrsEntries { get; set; } = new();
    public List<FileBackupEntry> Files { get; set; } = new();
}

public sealed class FixExecutionResult
{
    public bool Success { get; set; }
    public string BackupDirectory { get; set; } = string.Empty;
    public int FixedCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> LogLines { get; } = new();
}
