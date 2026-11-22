using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using CrimsonDesertDlssFixer.Models;
using CrimsonDesertDlssFixer.Services;
using Microsoft.Win32;

namespace CrimsonDesertDlssFixer;

public partial class MainWindow : Window
{
    private readonly DiagnosticEngine _engine = new();
    private readonly ObservableCollection<DiagnosticIssue> _issues = new();
    private ScanResult? _lastScan;

    public MainWindow()
    {
        InitializeComponent();
        IssuesList.ItemsSource = _issues;
        Loaded += async (_, _) => await RunScanAsync();
    }

    private async Task RunScanAsync(string? overrideGamePath = null)
    {
        SetBusy(true, "Scanning NVIDIA DRS, Pearl Abyss XMLs, Game Directory, and RHI...");
        try
        {
            var scan = await Task.Run(() => _engine.RunFullScan(overrideGamePath));
            _lastScan = scan;

            _issues.Clear();
            foreach (var issue in scan.Issues)
            {
                _issues.Add(issue);
            }

            GpuNameText.Text = scan.Summary.GpuName;
            DriverVersionText.Text = $"Driver: {scan.Summary.DriverVersion}";
            GamePathTextBox.Text = scan.Summary.GameInstallPath;
            HagsSummaryText.Text = $"HAGS: {scan.Summary.HagsStatus}";
            LogSummaryText.Text = scan.Summary.StreamlineLogSummary;
            RhiRunningBanner.Visibility = scan.Summary.IsRhiRunning ? Visibility.Visible : Visibility.Collapsed;

            int crit = scan.Issues.Count(i => i.Severity == IssueSeverity.Critical);
            int warn = scan.Issues.Count(i => i.Severity == IssueSeverity.Warning);
            int fixable = scan.Issues.Count(i => i.IsFixable);

            CriticalCountText.Text = crit.ToString();
            WarningCountText.Text = warn.ToString();
            FixableCountText.Text = fixable.ToString();

            RestoreButton.IsEnabled = _engine.GetLatestBackupManifestPath() is not null;

            AppendLog($"[{DateTime.Now:HH:mm:ss}] Scan completed: {crit} critical, {warn} warning(s), {fixable} auto-fixable item(s).");
            StatusText.Text = fixable > 0
                ? $"Found {crit} critical, {warn} warning ({fixable} auto-fixable)"
                : "No fixable issues detected — system looks clean!";
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Scan failed: {ex.Message}");
            StatusText.Text = "Scan error — check Activity Log";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        string? customPath = string.IsNullOrWhiteSpace(GamePathTextBox.Text) ? null : GamePathTextBox.Text.Trim();
        await RunScanAsync(customPath);
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Crimson Desert Installation Folder (containing bin64)"
        };
        if (Directory.Exists(GamePathTextBox.Text))
        {
            dlg.InitialDirectory = GamePathTextBox.Text;
        }

        if (dlg.ShowDialog() == true)
        {
            GamePathTextBox.Text = dlg.FolderName;
            await RunScanAsync(dlg.FolderName);
        }
    }

    private void SelectRecommendedButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var issue in _issues)
        {
            if (issue.IsFixable)
            {
                issue.IsSelected = issue.DefaultSelected;
            }
        }
    }

    private async void FixSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastScan is null) return;

        var selected = _issues.Where(i => i.IsFixable && i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select at least one fixable issue first.", "Nothing Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Process.GetProcessesByName("CrimsonDesert").Length > 0)
        {
            MessageBox.Show("CrimsonDesert.exe appears to be running. Please close Crimson Desert before applying fixes.",
                "Game Running", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Process.GetProcessesByName("RHI").Length > 0)
        {
            var rhiChoice = MessageBox.Show(
                "RHI.exe is currently running and may overwrite cleaned settings. Continue anyway?",
                "RHI Is Running", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (rhiChoice != MessageBoxResult.Yes) return;
        }

        var confirm = MessageBox.Show(
            $"Apply {selected.Count} selected fix(es)?\n\nAll modified NVIDIA DRS settings, Pearl Abyss XML configs, RHI JSON files, and quarantined mod files will be automatically backed up first so you can restore at any time.\n\nYour game save slots (CD\\save\\<id>\\slot*) will NOT be touched.",
            "Confirm Repair", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true, "Backing up and applying fixes...");
        LogExpander.IsExpanded = true;

        try
        {
            var res = await Task.Run(() => _engine.ApplyFixes(selected, _lastScan.Summary));
            foreach (string line in res.LogLines)
            {
                AppendLog(line);
            }

            string msg = res.Success
                ? $"Successfully applied {res.FixedCount} fix(es)!\n\nBackup saved to:\n{res.BackupDirectory}\n\nNext steps:\n1. Launch Crimson Desert and re-select DLSS & Frame Generation / MFG in Graphics Settings.\n2. Keep RHI closed (or don't re-apply DLSS/Streamline overrides in RHI)."
                : $"Completed with {res.FixedCount} fixed and {res.FailedCount} failed. Check the Activity Log for details.\n\nBackup saved to:\n{res.BackupDirectory}";

            MessageBox.Show(msg, res.Success ? "Repairs Complete" : "Repairs Completed With Warnings",
                MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

            await RunScanAsync(string.IsNullOrWhiteSpace(GamePathTextBox.Text) ? null : GamePathTextBox.Text.Trim());
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Fix execution failed: {ex.Message}");
            MessageBox.Show($"Repair failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        string? manifestPath = _engine.GetLatestBackupManifestPath();
        if (manifestPath is null)
        {
            MessageBox.Show("No backups found yet.", "No Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Restore from latest backup?\n\n{Path.GetDirectoryName(manifestPath)}",
            "Confirm Restore", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true, "Restoring backup...");
        LogExpander.IsExpanded = true;
        try
        {
            var (ok, logs) = await Task.Run(() => _engine.RestoreBackup(manifestPath));
            foreach (var l in logs) AppendLog(l);
            MessageBox.Show(ok ? "Backup restored successfully." : "Restore finished with warnings — check Activity Log.",
                "Restore Complete", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

            await RunScanAsync(string.IsNullOrWhiteSpace(GamePathTextBox.Text) ? null : GamePathTextBox.Text.Trim());
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DiagnosticEngine.BackupsRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = DiagnosticEngine.BackupsRoot,
            UseShellExecute = true
        });
    }

    private void ExportReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastScan is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export Diagnostic Report",
            Filter = "Text Report (*.txt)|*.txt|All Files (*.*)|*.*",
            FileName = $"CrimsonDesert_DLSS_Report_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Crimson Desert DLSS & MFG Diagnostic Report");
        sb.AppendLine($"Generated: {DateTime.Now:O}");
        sb.AppendLine($"GPU: {_lastScan.Summary.GpuName}");
        sb.AppendLine($"Driver: {_lastScan.Summary.DriverVersion}");
        sb.AppendLine($"HAGS: {_lastScan.Summary.HagsStatus}");
        sb.AppendLine($"Game Path: {_lastScan.Summary.GameInstallPath}");
        sb.AppendLine($"Pearl Abyss Path: {_lastScan.Summary.PearlAbyssPath}");
        sb.AppendLine($"Streamline Log: {_lastScan.Summary.StreamlineLogSummary}");
        sb.AppendLine(new string('=', 72));
        foreach (var issue in _lastScan.Issues)
        {
            sb.AppendLine($"[{issue.SeverityLabel}] ({issue.CategoryLabel}) {issue.Title}");
            sb.AppendLine($"Summary: {issue.Summary}");
            if (!string.IsNullOrWhiteSpace(issue.TechnicalDetails))
                sb.AppendLine(issue.TechnicalDetails);
            if (!string.IsNullOrWhiteSpace(issue.FixDescription))
                sb.AppendLine($"Planned Fix: {issue.FixDescription}");
            sb.AppendLine();
        }
        File.WriteAllText(dlg.FileName, sb.ToString());
        AppendLog($"Exported report to: {dlg.FileName}");
    }

    private void SetBusy(bool busy, string? status = null)
    {
        RescanButton.IsEnabled = !busy;
        FixSelectedButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy && _engine.GetLatestBackupManifestPath() is not null;
        if (status is not null) StatusText.Text = status;
    }

    private void AppendLog(string line)
    {
        ActivityLogBox.AppendText(line + Environment.NewLine);
        ActivityLogBox.ScrollToEnd();
    }
}
