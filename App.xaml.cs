using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using CrimsonDesertDlssFixer.Models;
using CrimsonDesertDlssFixer.Services;

namespace CrimsonDesertDlssFixer;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        if (args.Any(a => a.Equals("--scan", StringComparison.OrdinalIgnoreCase)
                       || a.Equals("--fix", StringComparison.OrdinalIgnoreCase)
                       || a.Equals("--restore", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            int code = RunCli(args);
            Shutdown(code);
            return;
        }

        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    private static int RunCli(string[] args)
    {
        bool asJson = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        bool doFix = args.Any(a => a.Equals("--fix", StringComparison.OrdinalIgnoreCase));
        bool doRestore = args.Any(a => a.Equals("--restore", StringComparison.OrdinalIgnoreCase));

        string? customGamePath = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--game-path", StringComparison.OrdinalIgnoreCase))
            {
                customGamePath = args[i + 1];
            }
        }

        var engine = new DiagnosticEngine();

        if (doRestore)
        {
            string? manifest = engine.GetLatestBackupManifestPath();
            if (manifest is null)
            {
                Console.WriteLine("No backup found to restore.");
                return 1;
            }
            var (ok, logs) = engine.RestoreBackup(manifest);
            foreach (var l in logs) Console.WriteLine(l);
            return ok ? 0 : 1;
        }

        var scan = engine.RunFullScan(customGamePath);

        if (asJson && !doFix)
        {
            Console.WriteLine(JsonSerializer.Serialize(scan, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine("=====================================================================");
        Console.WriteLine("  CRIMSON DESERT DLSS & MFG DIAGNOSTIC REPORT");
        Console.WriteLine("=====================================================================");
        Console.WriteLine($"GPU:          {scan.Summary.GpuName}");
        Console.WriteLine($"Driver:       {scan.Summary.DriverVersion}");
        Console.WriteLine($"HAGS:         {scan.Summary.HagsStatus}");
        Console.WriteLine($"Game Path:    {scan.Summary.GameInstallPath}");
        Console.WriteLine($"Pearl Abyss:  {scan.Summary.PearlAbyssPath}");
        Console.WriteLine($"RHI Running:  {scan.Summary.IsRhiRunning}");
        Console.WriteLine($"Log Summary:  {scan.Summary.StreamlineLogSummary}");
        Console.WriteLine();

        foreach (var issue in scan.Issues)
        {
            Console.WriteLine($"[{issue.SeverityLabel,-8}] ({issue.CategoryLabel}) {issue.Title}");
            Console.WriteLine($"           {issue.Summary}");
            if (!string.IsNullOrWhiteSpace(issue.TechnicalDetails))
            {
                foreach (var line in issue.TechnicalDetails.Split('\n'))
                {
                    Console.WriteLine($"             {line.TrimEnd()}");
                }
            }
            if (issue.IsFixable)
            {
                Console.WriteLine($"           -> Auto-Fix: {issue.FixDescription}");
            }
            Console.WriteLine();
        }

        if (doFix)
        {
            Console.WriteLine("Applying all recommended fixes (with automatic backup)...");
            var fixResult = engine.ApplyFixes(scan.Issues, scan.Summary);
            foreach (var l in fixResult.LogLines) Console.WriteLine("  " + l);
            Console.WriteLine($"\nDone: {fixResult.FixedCount} fixed, {fixResult.FailedCount} failed.");
            Console.WriteLine($"Backup: {fixResult.BackupDirectory}");
            return fixResult.Success ? 0 : 1;
        }

        return 0;
    }
}
