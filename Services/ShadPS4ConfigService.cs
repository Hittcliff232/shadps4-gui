using System;
using System.Collections.Generic;
using System.IO;
using Tomlyn;
using Tomlyn.Model;

namespace ShadPS4Launcher.Services;

/// <summary>
/// Reads/writes ShadPS4 config.toml (GPU, Audio, Input, etc.).
/// User dir: %APPDATA%\shadPS4 or portable: ./user
/// </summary>
public sealed class ShadPS4ConfigService
{
    private string GetConfigPath(string? userDir)
    {
        if (!string.IsNullOrWhiteSpace(userDir) && Directory.Exists(userDir))
            return Path.Combine(userDir, "config.toml");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var defaultPath = Path.Combine(appData, "shadPS4", "config.toml");
        return defaultPath;
    }

    public string GetDefaultUserDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "shadPS4");
    }

    public bool ConfigExists(string? userDir) => File.Exists(GetConfigPath(userDir));

    public TomlTable? Load(string? userDir)
    {
        var path = GetConfigPath(userDir);
        if (!File.Exists(path))
            return null;

        try
        {
            var text = File.ReadAllText(path);
            return Toml.ToModel(text);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string? userDir, TomlTable model)
    {
        var path = GetConfigPath(userDir);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var text = Toml.FromModel(model);
        File.WriteAllText(path, text);
    }

    public T GetValue<T>(TomlTable? model, string section, string key, T defaultValue)
    {
        if (model == null) return defaultValue;
        if (model.TryGetValue(section, out var sectionObj) && sectionObj is TomlTable sectionTable &&
            sectionTable.TryGetValue(key, out var value))
        {
            try
            {
                if (typeof(T) == typeof(int) && value is long l) return (T)(object)(int)l;
                if (typeof(T) == typeof(uint) && value is long l2) return (T)(object)(uint)l2;
                if (typeof(T) == typeof(bool) && value is bool b) return (T)(object)b;
                if (typeof(T) == typeof(double) && value is double d) return (T)(object)d;
                if (typeof(T) == typeof(string) && value is string s) return (T)(object)s;
            }
            catch { }
        }
        return defaultValue;
    }

    public void SetValue(TomlTable model, string section, string key, object value)
    {
        if (!model.ContainsKey(section))
            model[section] = new TomlTable();
        var sectionTable = (TomlTable)model[section];
        sectionTable[key] = value switch
        {
            int i => (long)i,
            uint u => (long)u,
            _ => value
        };
    }

    /// <summary>Ensure config has all sections; create from defaults if missing.</summary>
    public TomlTable GetOrCreateModel(string? userDir)
    {
        var existing = Load(userDir);
        if (existing != null) return existing;

        var model = new TomlTable();
        foreach (var section in new[] { "General", "Input", "Audio", "GPU", "Vulkan", "Debug", "GUI", "Keys", "Settings" })
            model[section] = new TomlTable();

        SetValue(model, "GPU", "screenWidth", 1280);
        SetValue(model, "GPU", "screenHeight", 720);
        SetValue(model, "GPU", "Fullscreen", false);
        SetValue(model, "GPU", "FullscreenMode", "Windowed");
        SetValue(model, "GPU", "presentMode", "Mailbox");
        SetValue(model, "GPU", "allowHDR", false);
        SetValue(model, "GPU", "fsrEnabled", false);
        SetValue(model, "GPU", "rcasEnabled", true);
        SetValue(model, "GPU", "rcasAttenuation", 250);
        SetValue(model, "GPU", "vblankFrequency", 60);
        SetValue(model, "Vulkan", "gpuId", -1);
        SetValue(model, "General", "volumeSlider", 100);
        return model;
    }

    public void UpdateFromLauncher(TomlTable model, LauncherConfigSnapshot snapshot)
    {
        SetValue(model, "GPU", "screenWidth", snapshot.WindowWidth);
        SetValue(model, "GPU", "screenHeight", snapshot.WindowHeight);
        SetValue(model, "GPU", "Fullscreen", snapshot.Fullscreen);
        SetValue(model, "GPU", "fsrEnabled", snapshot.FsrEnabled);
        SetValue(model, "GPU", "rcasEnabled", snapshot.RcasEnabled);
        SetValue(model, "GPU", "rcasAttenuation", snapshot.RcasAttenuation);
        SetValue(model, "GPU", "internalScreenWidth", snapshot.InternalWidth);
        SetValue(model, "GPU", "internalScreenHeight", snapshot.InternalHeight);
        SetValue(model, "Vulkan", "gpuId", snapshot.GpuId);
        SetValue(model, "General", "volumeSlider", snapshot.Volume);
        SetValue(model, "Debug", "showFpsCounter", snapshot.ShowFps);
        SetValue(model, "Input", "isMotionControlsEnabled", snapshot.MotionControls);
        SetValue(model, "Input", "backgroundControllerInput", snapshot.BackgroundControllerInput);
        SetValue(model, "General", "extraDmemInMbytes", snapshot.ExtraDmemMb);
        if (snapshot.GameInstallDirs.Count > 0)
        {
            var arr = new TomlArray();
            foreach (var d in snapshot.GameInstallDirs) arr.Add(d);
            model["GUI"] ??= new TomlTable();
            ((TomlTable)model["GUI"])["installDirs"] = arr;
        }
        if (!string.IsNullOrEmpty(snapshot.AddonDir))
        {
            model["GUI"] ??= new TomlTable();
            ((TomlTable)model["GUI"])["addonInstallDir"] = snapshot.AddonDir;
        }
    }

    public LauncherConfigSnapshot GetSnapshot(TomlTable? model)
    {
        var s = new LauncherConfigSnapshot();
        if (model == null) return s;
        s.WindowWidth = GetValue(model, "GPU", "screenWidth", 1280);
        s.WindowHeight = GetValue(model, "GPU", "screenHeight", 720);
        s.Fullscreen = GetValue(model, "GPU", "Fullscreen", false);
        s.FsrEnabled = GetValue(model, "GPU", "fsrEnabled", false);
        s.RcasEnabled = GetValue(model, "GPU", "rcasEnabled", true);
        s.RcasAttenuation = GetValue(model, "GPU", "rcasAttenuation", 250);
        s.InternalWidth = GetValue(model, "GPU", "internalScreenWidth", 1280);
        s.InternalHeight = GetValue(model, "GPU", "internalScreenHeight", 720);
        s.GpuId = GetValue(model, "Vulkan", "gpuId", -1);
        s.Volume = GetValue(model, "General", "volumeSlider", 100);
        s.ShowFps = GetValue(model, "Debug", "showFpsCounter", false);
        s.MotionControls = GetValue(model, "Input", "isMotionControlsEnabled", true);
        s.BackgroundControllerInput = GetValue(model, "Input", "backgroundControllerInput", false);
        s.ExtraDmemMb = GetValue(model, "General", "extraDmemInMbytes", 0);
        if (model.TryGetValue("GUI", out var gui) && gui is TomlTable guiTable)
        {
            if (guiTable.TryGetValue("installDirs", out var dirs) && dirs is TomlArray dirArr)
            {
                foreach (var item in dirArr)
                    if (item is string d) s.GameInstallDirs.Add(d);
            }
            if (guiTable.TryGetValue("addonInstallDir", out var addon) && addon is string addonStr)
                s.AddonDir = addonStr;
        }
        return s;
    }
}

public sealed class LauncherConfigSnapshot
{
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public bool Fullscreen { get; set; }
    public bool FsrEnabled { get; set; }
    public bool RcasEnabled { get; set; } = true;
    public int RcasAttenuation { get; set; } = 250;
    public int InternalWidth { get; set; } = 1280;
    public int InternalHeight { get; set; } = 720;
    public int GpuId { get; set; } = -1;
    public int Volume { get; set; } = 100;
    public bool ShowFps { get; set; }
    public bool MotionControls { get; set; } = true;
    public bool BackgroundControllerInput { get; set; }
    public int ExtraDmemMb { get; set; }
    public List<string> GameInstallDirs { get; set; } = new();
    public string AddonDir { get; set; } = "";
}
