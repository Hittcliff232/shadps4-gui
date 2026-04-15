using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShadPS4Launcher.Models;

public sealed class LauncherSettings
{
    [JsonPropertyName("emulatorPath")]
    public string EmulatorPath { get; set; } = "";

    [JsonPropertyName("gameInstallDirs")]
    public List<string> GameInstallDirs { get; set; } = new();

    [JsonPropertyName("addonDir")]
    public string AddonDir { get; set; } = "";

    [JsonPropertyName("userDir")]
    public string UserDir { get; set; } = "";

    [JsonPropertyName("lastGamePath")]
    public string LastGamePath { get; set; } = "";

    [JsonPropertyName("windowWidth")]
    public int WindowWidth { get; set; } = 1280;

    [JsonPropertyName("windowHeight")]
    public int WindowHeight { get; set; } = 720;

    [JsonPropertyName("fullscreen")]
    public bool Fullscreen { get; set; }

    [JsonPropertyName("showFps")]
    public bool ShowFps { get; set; }

    [JsonPropertyName("ignoreGamePatch")]
    public bool IgnoreGamePatch { get; set; }

    [JsonPropertyName("patchFile")]
    public string PatchFile { get; set; } = "";

    [JsonPropertyName("overrideRoot")]
    public string OverrideRoot { get; set; } = "";

    [JsonPropertyName("waitForDebugger")]
    public bool WaitForDebugger { get; set; }

    [JsonPropertyName("waitForPid")]
    public int? WaitForPid { get; set; }

    /// <summary>Additional CLI args appended after `--`.</summary>
    [JsonPropertyName("customLaunchArgs")]
    public string CustomLaunchArgs { get; set; } = "";

    [JsonPropertyName("configClean")]
    public bool ConfigClean { get; set; }

    [JsonPropertyName("configGlobal")]
    public bool ConfigGlobal { get; set; }

    [JsonPropertyName("logAppend")]
    public bool LogAppend { get; set; }

    /// <summary>Custom display names: game path -> title.</summary>
    [JsonPropertyName("customGameNames")]
    public Dictionary<string, string> CustomGameNames { get; set; } = new();

    /// <summary>UI / interface sound volume (0-100).</summary>
    [JsonPropertyName("uiSoundVolume")]
    public int UiSoundVolume { get; set; } = 100;

    /// <summary>Background menu music volume (0-100).</summary>
    [JsonPropertyName("menuMusicVolume")]
    public int MenuMusicVolume { get; set; } = 35;

    /// <summary>UI language code, e.g. "ru", "en".</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "ru";

    /// <summary>Selected launcher theme id (folder name or manifest id).</summary>
    [JsonPropertyName("themeId")]
    public string ThemeId { get; set; } = "default";

    /// <summary>Show log lines on loading overlay.</summary>
    [JsonPropertyName("showLoadingLogs")]
    public bool ShowLoadingLogs { get; set; } = true;

    /// <summary>Store/update backend URL (Node.js server).</summary>
    [JsonPropertyName("storeServerUrl")]
    public string StoreServerUrl { get; set; } = "http://127.0.0.1:7070";

    /// <summary>Where emulator builds from store are installed. Empty => BaseDir\emulators.</summary>
    [JsonPropertyName("emulatorInstallDir")]
    public string EmulatorInstallDir { get; set; } = "";

    /// <summary>Where imported PKG games are unpacked. Empty => first GameInstallDir or BaseDir\Games.</summary>
    [JsonPropertyName("pkgGamesDir")]
    public string PkgGamesDir { get; set; } = "";

    /// <summary>Map: game eboot path -> original PKG path.</summary>
    [JsonPropertyName("pkgSources")]
    public Dictionary<string, string> PkgSources { get; set; } = new();
}
