namespace ShadPS4Launcher.Bridge;

/// <summary>
/// Bridge interface for WebView2: C# implements this, LauncherHost calls it from JS.
/// </summary>
public interface IWebLauncherBridge
{
    string GetGamesJson();
    string GetSettingsJson();
    void SaveSettingsFromJson(string json);
    void SetSelectedGame(string path);
    bool LaunchGame();
    string GetLastLaunchStatusJson();
    string GetRecentLaunchLogLinesJson();
    string GetRecentLaunchErrorLinesJson();
    bool CreateShortcut();
    void OpenConfigFolder();
    void OpenGeneralSettings();
    void OpenPathsWindow();
    void OpenGraphicsWindow();
    void OpenAudioWindow();
    void OpenInputWindow();
    void OpenLogsWindow();
    void ExitLauncher();
    void NotifyRefreshGames();
    void ShowMessage(string message, string kind);
    void SetGameDisplayName(string path, string displayTitle);
    void RemoveGameDisplayName(string path);
    string OpenFolderDialog(string title);
    string OpenFileDialog(string title, string filter);
    string GetConfigJson();
    void SaveConfigJson(string json);
    string GetGameInstallDirsJson();
    void AddGameFolder(string path);
    void RemoveGameFolder(string path);
    string GetLogContent();
    bool ClearEmulatorLogs();
    string GetDirectoryEntries(string path);
    string GetAssetsBaseUrl();
    string GetEmulatorInputConfigPath();
    void OpenFolder(string path);
    string ReadFileContent(string path);
    void WriteFileContent(string path, string content);
    string GetLangJson(string langCode);
    string GetEmulatorLogDirPath();
    string GetGpuDevicesJson();
    string GetThemesJson();
    string GetLauncherVersion();
    string GetStoreCatalogJson();
    string InstallStorePackage(string requestJson);
    string CheckLauncherUpdateJson();
    string InstallLauncherUpdate(string requestJson);
    void OpenInAppBrowser(string url);
    void CloseInAppBrowser();
    void NavigateInAppBrowser(string url);
    void BrowserBack();
    void BrowserForward();
    void BrowserReload();
    string GetInAppBrowserUrl();
    bool IsInAppBrowserOpen();
    void SetInAppBrowserBounds(double x, double y, double width, double height, double viewportWidth, double viewportHeight);
    string GetThemeCustomCssPath(string themeId);
    string AddPkgGame(string pkgPath);
    string GetPkgOperationStatusJson();
    string GetSelectedGameInfoJson();
    string DeleteSelectedGameFromDisk();
}
