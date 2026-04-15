using System.Runtime.InteropServices;

namespace ShadPS4Launcher.Bridge;

/// <summary>
/// Host object injected into WebView2 for JS interop. JS uses window.chrome.webview.hostObjects.sync.launcherHost.
/// </summary>
[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public sealed class LauncherHost
{
    private readonly IWebLauncherBridge _bridge;

    public LauncherHost(IWebLauncherBridge bridge)
    {
        _bridge = bridge;
    }

    public string getGames() => _bridge.GetGamesJson();
    public string getSettings() => _bridge.GetSettingsJson();
    public void saveSettings(string json) => _bridge.SaveSettingsFromJson(json);
    public void setSelectedGame(string path) => _bridge.SetSelectedGame(path);
    public bool launch() => _bridge.LaunchGame();
    public string getLastLaunchStatus() => _bridge.GetLastLaunchStatusJson();
    public string getRecentLaunchLogLines() => _bridge.GetRecentLaunchLogLinesJson();
    public string getRecentLaunchErrorLines() => _bridge.GetRecentLaunchErrorLinesJson();
    public bool createShortcut() => _bridge.CreateShortcut();
    public void openConfigFolder() => _bridge.OpenConfigFolder();
    public void openGeneralSettings() => _bridge.OpenGeneralSettings();
    public void openPathsWindow() => _bridge.OpenPathsWindow();
    public void openGraphicsWindow() => _bridge.OpenGraphicsWindow();
    public void openAudioWindow() => _bridge.OpenAudioWindow();
    public void openInputWindow() => _bridge.OpenInputWindow();
    public void openLogsWindow() => _bridge.OpenLogsWindow();
    public void exitLauncher() => _bridge.ExitLauncher();
    public void showMessage(string message, string kind) => _bridge.ShowMessage(message, kind);
    public void setGameDisplayName(string path, string displayTitle) => _bridge.SetGameDisplayName(path, displayTitle);
    public void removeGameDisplayName(string path) => _bridge.RemoveGameDisplayName(path);
    public string openFolderDialog(string title) => _bridge.OpenFolderDialog(title);
    public string openFileDialog(string title, string filter) => _bridge.OpenFileDialog(title, filter);
    public string getConfig() => _bridge.GetConfigJson();
    public void saveConfig(string json) => _bridge.SaveConfigJson(json);
    public string getGameInstallDirs() => _bridge.GetGameInstallDirsJson();
    public void addGameFolder(string path) => _bridge.AddGameFolder(path);
    public void removeGameFolder(string path) => _bridge.RemoveGameFolder(path);
    public string getLogContent() => _bridge.GetLogContent();
    public bool clearLogs() => _bridge.ClearEmulatorLogs();
    public string getDirectoryEntries(string path) => _bridge.GetDirectoryEntries(path);
    public string getAssetsBaseUrl() => _bridge.GetAssetsBaseUrl();
    public string getEmulatorInputConfigPath() => _bridge.GetEmulatorInputConfigPath();
    public void openFolder(string path) => _bridge.OpenFolder(path);
    public string readFileContent(string path) => _bridge.ReadFileContent(path);
    public void writeFileContent(string path, string content) => _bridge.WriteFileContent(path, content);
    public string getLangJson(string langCode) => _bridge.GetLangJson(langCode);
    public string getEmulatorLogDirPath() => _bridge.GetEmulatorLogDirPath();
    public string getGpuDevices() => _bridge.GetGpuDevicesJson();
    public string getThemes() => _bridge.GetThemesJson();
    public string getLauncherVersion() => _bridge.GetLauncherVersion();
    public string getStoreCatalog() => _bridge.GetStoreCatalogJson();
    public string installStorePackage(string requestJson) => _bridge.InstallStorePackage(requestJson);
    public string checkLauncherUpdate() => _bridge.CheckLauncherUpdateJson();
    public string installLauncherUpdate(string requestJson) => _bridge.InstallLauncherUpdate(requestJson);
    public void openInAppBrowser(string url) => _bridge.OpenInAppBrowser(url);
    public void closeInAppBrowser() => _bridge.CloseInAppBrowser();
    public void navigateInAppBrowser(string url) => _bridge.NavigateInAppBrowser(url);
    public void browserBack() => _bridge.BrowserBack();
    public void browserForward() => _bridge.BrowserForward();
    public void browserReload() => _bridge.BrowserReload();
    public string getInAppBrowserUrl() => _bridge.GetInAppBrowserUrl();
    public bool isInAppBrowserOpen() => _bridge.IsInAppBrowserOpen();
    public void setInAppBrowserBounds(double x, double y, double width, double height, double viewportWidth, double viewportHeight)
        => _bridge.SetInAppBrowserBounds(x, y, width, height, viewportWidth, viewportHeight);
    public string getThemeCustomCssPath(string themeId) => _bridge.GetThemeCustomCssPath(themeId);
    public string addPkgGame(string pkgPath) => _bridge.AddPkgGame(pkgPath);
    public string getPkgOperationStatus() => _bridge.GetPkgOperationStatusJson();
    public string getSelectedGameInfo() => _bridge.GetSelectedGameInfoJson();
    public string deleteSelectedGameFromDisk() => _bridge.DeleteSelectedGameFromDisk();
}
