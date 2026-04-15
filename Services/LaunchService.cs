using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ShadPS4Launcher.Models;

namespace ShadPS4Launcher.Services;

public sealed class LaunchService
{
    /// <summary>
    /// Build CLI arguments for shadPS4: -g path [options]
    /// </summary>
    public string BuildArguments(LauncherSettings settings, string? gamePathOrId, IReadOnlyList<string>? extraArgs = null)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(gamePathOrId))
            args.Add($"-g \"{gamePathOrId}\"");

        if (settings.Fullscreen)
            args.Add("-f true");
        else
            args.Add("-f false");

        if (settings.ShowFps)
            args.Add("--show-fps");
        if (settings.IgnoreGamePatch)
            args.Add("-i");
        if (!string.IsNullOrWhiteSpace(settings.PatchFile))
            args.Add($"-p \"{settings.PatchFile}\"");
        if (!string.IsNullOrWhiteSpace(settings.OverrideRoot))
            args.Add($"--override-root \"{settings.OverrideRoot}\"");
        if (settings.WaitForDebugger)
            args.Add("--wait-for-debugger");
        if (settings.WaitForPid.HasValue && settings.WaitForPid.Value > 0)
            args.Add($"--wait-for-pid {settings.WaitForPid.Value}");
        if (settings.ConfigClean)
            args.Add("--config-clean");
        if (settings.ConfigGlobal)
            args.Add("--config-global");
        if (settings.LogAppend)
            args.Add("--log-append");

        if (!string.IsNullOrWhiteSpace(settings.CustomLaunchArgs))
            args.Add("-- " + settings.CustomLaunchArgs.Trim());

        if (extraArgs != null)
            foreach (var a in extraArgs)
                if (!string.IsNullOrWhiteSpace(a))
                    args.Add(a);

        return string.Join(" ", args);
    }

    /// <summary>
    /// Start shadPS4 process. Returns null if exe not found or start fails.
    /// </summary>
    public Process? LaunchProcess(LauncherSettings settings, string? gamePathOrId, string? workingDir = null)
    {
        var exe = settings.EmulatorPath?.Trim();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return null;

        var args = BuildArguments(settings, gamePathOrId);
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(exe) ?? "",
            WindowStyle = ProcessWindowStyle.Normal,
            CreateNoWindow = false
        };
        try
        {
            return Process.Start(startInfo);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Start shadPS4 process. Returns false if exe not found or start fails.
    /// </summary>
    public bool Launch(LauncherSettings settings, string? gamePathOrId, string? workingDir = null)
    {
        return LaunchProcess(settings, gamePathOrId, workingDir) != null;
    }

    /// <summary>
    /// Add game folder to ShadPS4 config by running: shadps4.exe --add-game-folder "path"
    /// </summary>
    public bool AddGameFolder(string emulatorPath, string folderPath)
    {
        if (string.IsNullOrEmpty(emulatorPath) || !File.Exists(emulatorPath) ||
            string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return false;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = emulatorPath,
                Arguments = $"--add-game-folder \"{folderPath}\"",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(emulatorPath) ?? "",
                CreateNoWindow = true
            };
            using var p = Process.Start(startInfo);
            p?.WaitForExit(10000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Set addon folder: shadps4.exe --set-addon-folder "path"
    /// </summary>
    public bool SetAddonFolder(string emulatorPath, string folderPath)
    {
        if (string.IsNullOrEmpty(emulatorPath) || !File.Exists(emulatorPath))
            return false;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = emulatorPath,
                Arguments = $"--set-addon-folder \"{folderPath}\"",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(emulatorPath) ?? "",
                CreateNoWindow = true
            };
            using var p = Process.Start(startInfo);
            p?.WaitForExit(10000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
