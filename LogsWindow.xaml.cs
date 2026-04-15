using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace ShadPS4Launcher;

public partial class LogsWindow
{
    private readonly string _userDir;

    public LogsWindow(string userDir)
    {
        InitializeComponent();
        _userDir = userDir ?? "";
        TryLoadLog();
    }

    private void TryLoadLog()
    {
        if (string.IsNullOrWhiteSpace(_userDir) || !Directory.Exists(_userDir))
        {
            TbLog.Text = "User folder not set or missing. Set it in Settings and run the emulator at least once.";
            return;
        }
        var logPath = FindLogFile();
        if (logPath != null)
        {
            try
            {
                TbLog.Text = File.ReadAllText(logPath);
                return;
            }
            catch (Exception ex)
            {
                TbLog.Text = $"Could not read log: {ex.Message}";
                return;
            }
        }
        TbLog.Text = "No log file found in user folder. Run the emulator to generate logs.";
    }

    private string? FindLogFile()
    {
        var candidates = new[]
        {
            Path.Combine(_userDir, "log.txt"),
            Path.Combine(_userDir, "shadps4_log.txt"),
            Path.Combine(_userDir, "log", "log.txt")
        };
        foreach (var p in candidates)
            if (File.Exists(p)) return p;
        try
        {
            var latest = new DirectoryInfo(_userDir)
                .GetFiles("*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return latest?.FullName;
        }
        catch { }
        return null;
    }

    private void BtnLoad_OnClick(object sender, RoutedEventArgs e) => TryLoadLog();

    private void BtnOpenFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var dir = _userDir;
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        try { Process.Start("explorer.exe", $"\"{dir}\""); } catch { }
    }

    private void BtnClose_OnClick(object sender, RoutedEventArgs e) => Close();
}
