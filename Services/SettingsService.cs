using System;
using System.IO;
using System.Text.Json;
using ShadPS4Launcher.Models;

namespace ShadPS4Launcher.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShadPS4Launcher",
            "launcher_settings.json");

    public LauncherSettings Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(SettingsPath))
                return new LauncherSettings();

            var opts = new JsonSerializerOptions(JsonOptions) { PropertyNameCaseInsensitive = true };
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<LauncherSettings>(json, opts) ?? new LauncherSettings();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings)
    {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json, System.Text.Encoding.UTF8);
            if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
            File.Move(tempPath, SettingsPath);
        }
        catch
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(SettingsPath, json, System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
