using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShadPS4Launcher.Models;

namespace ShadPS4Launcher.Services;

/// <summary>
/// Scans game install dirs for PS4 games (eboot.bin; param.sfo optional).
/// Supports: GameFolder/eboot.bin + sce_sys/param.sfo, or any subfolder with eboot.bin.
/// </summary>
public sealed class GameDiscoveryService
{
    private const int MaxDepth = 8;

    public IReadOnlyList<GameEntry> DiscoverGames(IEnumerable<string> installDirs)
    {
        var list = new List<GameEntry>();
        foreach (var dir in installDirs ?? Array.Empty<string>())
        {
            var normalized = dir?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                continue;
            try
            {
                var path = Path.GetFullPath(normalized);
                if (!Directory.Exists(path))
                    continue;
                DiscoverRecursive(path, path, 0, list);
            }
            catch
            {
                // skip bad dir
            }
        }
        return list.OrderBy(g => g.DisplayTitle).ToList();
    }

    private void DiscoverRecursive(string baseDir, string currentDir, int depth, List<GameEntry> list)
    {
        if (depth > MaxDepth) return;

        var eboot = Path.Combine(currentDir, "eboot.bin");
        if (!File.Exists(eboot))
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(currentDir))
                    DiscoverRecursive(baseDir, sub, depth + 1, list);
            }
            catch { }
            return;
        }

        var dataDir = GetSiblingDataDir(currentDir);
        var paramSfo = Path.Combine(currentDir, "sce_sys", "param.sfo");
        if (!File.Exists(paramSfo) && dataDir != null)
            paramSfo = Path.Combine(dataDir, "param.sfo");
        if (!File.Exists(paramSfo) && dataDir != null)
            paramSfo = Path.Combine(dataDir, "sce_sys", "param.sfo");

        var title = File.Exists(paramSfo)
            ? ParamSfoReader.GetTitle(paramSfo)
            : null;
        var titleId = File.Exists(paramSfo)
            ? ParamSfoReader.GetTitleId(paramSfo)
            : null;
        var gameId = !string.IsNullOrWhiteSpace(titleId)
            ? titleId
            : Path.GetFileName(currentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "unknown";
        if (string.IsNullOrWhiteSpace(title))
            title = GetFallbackTitle(currentDir, baseDir, gameId);

        list.Add(new GameEntry
        {
            Title = title,
            Path = eboot,
            GameId = gameId,
            IconPath = FindIcon(currentDir, baseDir, dataDir)
        });
    }

    private static string? GetSiblingDataDir(string gameDir)
    {
        var parent = Path.GetDirectoryName(gameDir);
        if (string.IsNullOrEmpty(parent)) return null;
        var dataDir = Path.Combine(parent, "data");
        return Directory.Exists(dataDir) ? dataDir : null;
    }

    private static string GetFallbackTitle(string currentDir, string? baseDir, string gameId)
    {
        var folderName = Path.GetFileName(currentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(folderName) && folderName != "data" && folderName != "app")
            return folderName;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            var baseName = Path.GetFileName(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(baseName)) return baseName;
        }
        return gameId;
    }

    private static string? FindIcon(string gameDir, string? baseDir, string? dataDir)
    {
        var icon = Path.Combine(gameDir, "icon0.png");
        if (File.Exists(icon)) return icon;
        icon = Path.Combine(gameDir, "icon0.jpg");
        if (File.Exists(icon)) return icon;
        icon = Path.Combine(gameDir, "sce_sys", "icon0.png");
        if (File.Exists(icon)) return icon;
        icon = Path.Combine(gameDir, "sce_sys", "icon0.jpg");
        if (File.Exists(icon)) return icon;
        if (dataDir != null)
        {
            icon = Path.Combine(dataDir, "icon0.png");
            if (File.Exists(icon)) return icon;
            icon = Path.Combine(dataDir, "icon0.jpg");
            if (File.Exists(icon)) return icon;
            icon = Path.Combine(dataDir, "sce_sys", "icon0.png");
            if (File.Exists(icon)) return icon;
            icon = Path.Combine(dataDir, "sce_sys", "icon0.jpg");
            if (File.Exists(icon)) return icon;
        }
        if (!string.IsNullOrEmpty(baseDir))
        {
            icon = Path.Combine(baseDir, "data", "icon0.png");
            if (File.Exists(icon)) return icon;
            icon = Path.Combine(baseDir, "data", "icon0.jpg");
            if (File.Exists(icon)) return icon;
        }
        return null;
    }
}
