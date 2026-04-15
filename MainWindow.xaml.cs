using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using ShadPS4Launcher.Bridge;
using ShadPS4Launcher.Models;
using ShadPS4Launcher.Services;

namespace ShadPS4Launcher;

public sealed class CpuComboItem
{
    public int Id { get; }
    public string DisplayName { get; }
    public CpuComboItem(int id, string displayName) { Id = id; DisplayName = displayName; }
}

public partial class MainWindow : IWebLauncherBridge
{
    private const int DisplayDeviceMirroringDriver = 0x00000008;
    private System.Windows.Threading.DispatcherTimer? _splashTimer;
    private bool _isMainPageReady = false;
    private WebView2? _preloadWebView;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDevice lpDisplayDevice,
        uint dwFlags);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions JsonReadOptions = new(JsonOptions)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    private readonly SettingsService _settingsService = new();
    private readonly ShadPS4ConfigService _configService = new();
    private readonly LaunchService _launchService = new();
    private readonly ShortcutService _shortcutService = new();
    private readonly GameDiscoveryService _gameDiscovery = new();

    private LauncherSettings _settings = null!;
    private List<GameEntry> _games = new();
    private string? _selectedGamePath;
    private Process? _lastLaunchedProcess;
    private DateTimeOffset? _lastLaunchStartedAtUtc;
    private const string DefaultStoreServerUrl = "http://127.0.0.1:7070";

    private sealed class StoreInstallRequest
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Type { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string FileName { get; set; } = "";
        public bool Extract { get; set; } = true;
        public string TargetSubdir { get; set; } = "";
    }

    private sealed class LauncherVersionInfo
    {
        public string Version { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string Notes { get; set; } = "";
    }

    private sealed class PkgOperationStatus
    {
        public bool Active { get; set; }
        public string Kind { get; set; } = "none";
        public string Stage { get; set; } = "idle";
        public string Message { get; set; } = "";
        public string GamePath { get; set; } = "";
        public string PkgPath { get; set; } = "";
        public string CurrentFile { get; set; } = "";
        public int Current { get; set; }
        public int Total { get; set; }
        public int Percent { get; set; }
        public bool Success { get; set; }
        public bool Failed { get; set; }
        public string PreviewImagePath { get; set; } = "";
        public string PreviewTitle { get; set; } = "";
        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public List<string> StdOutLines { get; set; } = new();
        public List<string> StdErrLines { get; set; } = new();
    }

    private static readonly Regex ExtractProgressRegex = new(@"^\[(\d+)/(\d+)\]\s*(.*)$", RegexOptions.Compiled);
    private readonly object _pkgOpSync = new();
    private readonly object _installLogSync = new();
    private PkgOperationStatus _pkgOperation = new();
    private Task? _pkgOperationTask;
    private bool _inAppBrowserReady;
    private string _inAppBrowserCurrentUrl = "about:blank";
    private string _inAppBrowserPendingUrl = "about:blank";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += (_, _) => _settingsService.Save(_settings);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsService.Load();
        SyncSettingsFromConfigIfNeeded();
        RefreshGameList();
        _selectedGamePath = _settings.LastGamePath;

        try
        {
            await WebView.EnsureCoreWebView2Async(null);
            WebView.CoreWebView2.AddHostObjectToScript("launcherHost", new LauncherHost(this));

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var assetsBaseUrl = "file:///" + baseDir.Replace('\\', '/').TrimStart('/').Replace(" ", "%20") + "/";
            await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__assetsBaseUrl = " + JsonSerializer.Serialize(assetsBaseUrl) + ";");

            // Добавляем обработчик сообщений от WebView
            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // Загружаем preview.html
            await LoadPreviewPageAsync();

            // Начинаем предзагрузку главной страницы в фоне
            _ = PreloadMainPageAsync();

            // Настраиваем таймер для максимального времени показа заставки
            SetupSplashTimer();
        }
        catch (Exception ex)
        {
            MessageBox.Show("WebView2 failed: " + ex.Message, "ShadPS4 Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        var message = args.TryGetWebMessageAsString();
        
        if (message == "preview-loaded")
        {
            // Preview загружен, запускаем анимацию
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _ = WebView.CoreWebView2.ExecuteScriptAsync(@"
                    document.querySelector('.logo').style.animation = 'logoAnim 5s forwards';
                ");
            });
        }
        else if (message == "skip-splash")
        {
            SkipSplash();
        }
        else if (message == "main-page-ready")
        {
            _isMainPageReady = true;
            // Если прогресс-бар уже начал заполняться, проверяем готовность
            CheckAndCompleteTransition();
        }
        else if (message == "progress-complete")
        {
            // Прогресс-бар заполнился, проверяем готовность главной страницы
            CheckAndCompleteTransition();
        }
    }

private async Task LoadPreviewPageAsync()
{
    var previewPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.html");
    if (File.Exists(previewPath))
    {
        WebView.Source = new Uri("file:///" + previewPath.Replace('\\', '/').TrimStart('/'));
        
    }
    else
    {
        // Если preview.html не найден, сразу загружаем index.html (как обычную навигацию)
        await LoadMainPageAsync(isInitialLoad: false);
    }
}

    private async Task PreloadMainPageAsync()
    {
        try
        {
            _preloadWebView = new WebView2();
            await _preloadWebView.EnsureCoreWebView2Async();

            var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
            if (File.Exists(htmlPath))
            {
                _preloadWebView.Source = new Uri("file:///" + htmlPath.Replace('\\', '/').TrimStart('/'));

                // Ждем загрузки и добавляем скрипт уведомления
                await Task.Delay(1000); // Небольшая задержка для загрузки
                
                await _preloadWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    // Уведомляем о готовности главной страницы
                    window.addEventListener('load', function() {
                        window.chrome.webview.postMessage('main-page-ready');
                    });
                ");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Preload error: {ex.Message}");
        }
    }

    private void SetupSplashTimer()
    {
        _splashTimer = new System.Windows.Threading.DispatcherTimer();
        _splashTimer.Interval = TimeSpan.FromSeconds(8); // Максимальное время показа заставки
        _splashTimer.Tick += (s, e) =>
        {
            _splashTimer.Stop();
            // Если главная страница еще не готова, все равно переходим
            _ = TransitionToMainPage();
        };
        _splashTimer.Start();
    }

private async Task TransitionToMainPage()
{
    // Плавно скрываем preview
    await WebView.CoreWebView2.ExecuteScriptAsync(@"
        document.body.style.transition = 'opacity 0.8s ease';
        document.body.style.opacity = '0';
    ");

    await Task.Delay(800);

    // Загружаем главную страницу с флагом initial load
    await LoadMainPageAsync(isInitialLoad: true);
}

private async Task LoadMainPageAsync(bool isInitialLoad = false)
{
    var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
    if (File.Exists(htmlPath))
    {
        WebView.Source = new Uri("file:///" + htmlPath.Replace('\\', '/').TrimStart('/'));

        // Добавляем скрипт для плавного появления ТОЛЬКО при первом запуске
        if (isInitialLoad)
        {
            await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                // Сохраняем флаг, что это первая загрузка
                window._isInitialLoad = true;
                
                document.body.style.opacity = '0';
                document.body.style.transition = 'opacity 0.8s ease';
                setTimeout(() => {
                    document.body.style.opacity = '1';
                    // Удаляем transition после анимации
                    setTimeout(() => {
                        document.body.style.transition = 'none';
                    }, 1000);
                }, 100);
            ");
        }
        else
        {
            // Для обычной навигации - без анимации
            await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                document.body.style.opacity = '1';
                document.body.style.transition = 'none';
            ");
        }

        // Обновляем список игр после загрузки
        await Task.Delay(500);
        NotifyRefreshGames();
    }
    else
    {
        WebView.NavigateToString("<body style='background:#0b0b0b;color:#fff;font-family:sans-serif;padding:40px;'>index.html not found next to the executable.</body>");
    }

    _preloadWebView?.Dispose();
    _preloadWebView = null;
    _splashTimer?.Stop();
}

    private void CheckAndCompleteTransition()
    {
        if (_isMainPageReady)
        {
            // Прогресс-бар завершен и главная страница готова
            _ = TransitionToMainPage();
        }
    }

    public void SkipSplash()
    {
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            _splashTimer?.Stop();
            await TransitionToMainPage();
        });
    }

    private void SyncSettingsFromConfigIfNeeded()
    {
        _settings.GameInstallDirs ??= new List<string>();
        _settings.CustomGameNames ??= new Dictionary<string, string>();
        _settings.PkgSources = _settings.PkgSources == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(_settings.PkgSources, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(_settings.StoreServerUrl))
            _settings.StoreServerUrl = DefaultStoreServerUrl;

        var userDir = string.IsNullOrWhiteSpace(_settings.UserDir) ? null : _settings.UserDir;
        var model = _configService.Load(userDir);
        if (model == null) return;
        var snap = _configService.GetSnapshot(model);
        if (snap.GameInstallDirs.Count > 0 && _settings.GameInstallDirs.Count == 0)
            _settings.GameInstallDirs = new List<string>(snap.GameInstallDirs);
        if (!string.IsNullOrEmpty(snap.AddonDir) && string.IsNullOrEmpty(_settings.AddonDir))
            _settings.AddonDir = snap.AddonDir;
    }

    private void RefreshGameList()
    {
        var dirs = _settings.GameInstallDirs;
        if (dirs.Count == 0 && !string.IsNullOrWhiteSpace(_settings.UserDir))
        {
            var configModel = _configService.Load(_settings.UserDir);
            if (configModel != null)
                dirs = _configService.GetSnapshot(configModel).GameInstallDirs;
        }
        _games = _gameDiscovery.DiscoverGames(dirs).ToList();
    }

    private string? GetSelectedGamePathOrId()
    {
        return _selectedGamePath ?? (string.IsNullOrWhiteSpace(_settings.LastGamePath) ? null : _settings.LastGamePath);
    }

    private GameEntry? GetSelectedGameEntry()
    {
        var path = GetSelectedGamePathOrId();
        if (string.IsNullOrEmpty(path)) return null;
        return _games.FirstOrDefault(g => g.Path == path || g.GameId == path);
    }

    private string GetGameDisplayTitle(GameEntry entry)
    {
        if (_settings.CustomGameNames.TryGetValue(entry.Path, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom.Trim();
        return entry.DisplayTitle;
    }

    private static string NormalizeBrowserUrl(string? raw)
    {
        var v = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(v))
            return "https://duckduckgo.com";

        if (v.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return v;

        if (Uri.TryCreate(v, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        return "https://" + v;
    }

    private async Task EnsureInAppBrowserReadyAsync()
    {
        if (_inAppBrowserReady)
            return;

        await InAppBrowserView.EnsureCoreWebView2Async(null);
        _inAppBrowserReady = true;

        InAppBrowserView.NavigationCompleted += (_, _) =>
        {
            try
            {
                _inAppBrowserCurrentUrl = InAppBrowserView.Source?.ToString() ?? _inAppBrowserCurrentUrl;
            }
            catch
            {
                // ignore
            }
        };

        try
        {
            InAppBrowserView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            InAppBrowserView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            InAppBrowserView.CoreWebView2.Settings.IsZoomControlEnabled = true;
        }
        catch
        {
            // ignore optional settings
        }
    }

    private static string? ResolveGameDeleteRoot(GameEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
            return null;

        string gameDir;
        if (File.Exists(entry.Path))
        {
            gameDir = Path.GetDirectoryName(entry.Path) ?? "";
        }
        else if (Directory.Exists(entry.Path))
        {
            gameDir = entry.Path;
        }
        else
        {
            gameDir = Path.GetDirectoryName(entry.Path) ?? "";
        }

        if (string.IsNullOrWhiteSpace(gameDir))
            return null;

        gameDir = Path.GetFullPath(gameDir);
        var gameDirName = Path.GetFileName(gameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(gameDirName, "app", StringComparison.OrdinalIgnoreCase))
            return gameDir;

        var parent = Path.GetDirectoryName(gameDir);
        if (string.IsNullOrWhiteSpace(parent))
            return gameDir;

        var siblingData = Path.Combine(parent, "data");
        if (Directory.Exists(siblingData))
            return Path.GetFullPath(parent);

        return gameDir;
    }

    private static bool IsUnsafeDeletePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(full))
                return true;

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root))
                return true;

            var rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, rootTrimmed, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static long GetDirectorySizeSafe(string rootDir)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            return 0;

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(rootDir);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // ignore files that cannot be read
                    }
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                    pending.Push(sub);
            }
            catch
            {
                // ignore directories that cannot be read
            }
        }

        return total;
    }

    private void RemoveGameMetadata(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return;

        _settings.CustomGameNames.Remove(gamePath);
        _settings.PkgSources.Remove(gamePath);
    }

    private void NormalizeSelectedGameAfterRefresh()
    {
        var selectedPath = GetSelectedGamePathOrId();
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            var stillExists = _games.Any(g =>
                string.Equals(g.Path, selectedPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(g.GameId, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (stillExists)
            {
                _selectedGamePath = selectedPath;
                _settings.LastGamePath = selectedPath;
                return;
            }
        }

        var next = _games.FirstOrDefault();
        _selectedGamePath = next?.Path;
        _settings.LastGamePath = _selectedGamePath ?? "";
    }

    // --- IWebLauncherBridge ---

    private static string? FindBackgroundPath(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return null;
        foreach (var name in new[] { "pic0.png", "pic1.png", "pic0.jpg", "pic1.jpg" })
        {
            var p = Path.Combine(gameDir, name);
            if (File.Exists(p)) return p;
            p = Path.Combine(gameDir, "sce_sys", name);
            if (File.Exists(p)) return p;
        }
        var dataDir = Path.Combine(Path.GetDirectoryName(gameDir) ?? "", "data");
        if (Directory.Exists(dataDir))
            foreach (var name in new[] { "pic0.png", "pic1.png", "pic0.jpg", "pic1.jpg" })
            {
                var p = Path.Combine(dataDir, name);
                if (File.Exists(p)) return p;
                p = Path.Combine(dataDir, "sce_sys", name);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    private static string ToDataUrl(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null!;
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mime = ext == ".png" ? "image/png" : (ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : "image/png");
            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }
        catch { return null!; }
    }

    public string GetGamesJson()
    {
        var list = new List<object>();
        foreach (var g in _games)
        {
            var gameDir = Path.GetDirectoryName(g.Path) ?? "";
            var iconData = ToDataUrl(g.IconPath);
            var bgPath = FindBackgroundPath(gameDir);
            var backgroundDataUrl = ToDataUrl(bgPath);
            if (string.IsNullOrEmpty(backgroundDataUrl)) backgroundDataUrl = iconData;
            var title = GetGameDisplayTitle(g);
            _settings.PkgSources.TryGetValue(g.Path, out var pkgSourcePath);
            var pkgSourceExists = !string.IsNullOrWhiteSpace(pkgSourcePath) && File.Exists(pkgSourcePath);
            list.Add(new
            {
                title = title,
                path = g.Path,
                gameId = g.GameId,
                iconDataUrl = iconData,
                backgroundDataUrl = backgroundDataUrl,
                pkgSourcePath = pkgSourcePath ?? "",
                pkgSourceExists
            });
        }
        return JsonSerializer.Serialize(list, JsonOptions);
    }

    public string GetSelectedGameInfoJson()
    {
        try
        {
            var entry = GetSelectedGameEntry();
            if (entry == null)
            {
                return JsonSerializer.Serialize(new
                {
                    hasGame = false,
                    canDelete = false,
                    title = "",
                    gameId = "",
                    path = "",
                    deleteRoot = "",
                    sizeBytes = 0L
                }, JsonOptions);
            }

            var deleteRoot = ResolveGameDeleteRoot(entry) ?? "";
            var canDelete = !string.IsNullOrWhiteSpace(deleteRoot) &&
                            Directory.Exists(deleteRoot) &&
                            !IsUnsafeDeletePath(deleteRoot);

            var sizeBytes = canDelete ? GetDirectorySizeSafe(deleteRoot) : 0L;
            return JsonSerializer.Serialize(new
            {
                hasGame = true,
                canDelete,
                title = GetGameDisplayTitle(entry),
                gameId = entry.GameId ?? "",
                path = entry.Path ?? "",
                deleteRoot,
                sizeBytes
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                hasGame = false,
                canDelete = false,
                title = "",
                gameId = "",
                path = "",
                deleteRoot = "",
                sizeBytes = 0L,
                message = ex.Message
            }, JsonOptions);
        }
    }

    public string DeleteSelectedGameFromDisk()
    {
        try
        {
            var entry = GetSelectedGameEntry();
            if (entry == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    message = "Game is not selected."
                }, JsonOptions);
            }

            var title = GetGameDisplayTitle(entry);
            var deleteRoot = ResolveGameDeleteRoot(entry);
            if (string.IsNullOrWhiteSpace(deleteRoot))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    message = "Game folder path is invalid."
                }, JsonOptions);
            }

            deleteRoot = Path.GetFullPath(deleteRoot);
            if (IsUnsafeDeletePath(deleteRoot))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    message = "Refused to delete unsafe path."
                }, JsonOptions);
            }

            if (Directory.Exists(deleteRoot))
                Directory.Delete(deleteRoot, recursive: true);

            RemoveGameMetadata(entry.Path);
            RefreshGameList();
            NormalizeSelectedGameAfterRefresh();
            _settingsService.Save(_settings);

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                NotifyRefreshGames();
            });

            return JsonSerializer.Serialize(new
            {
                success = true,
                deletedPath = deleteRoot,
                message = $"Game removed: {title}"
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                message = "Failed to remove game: " + ex.Message
            }, JsonOptions);
        }
    }

    public string GetSettingsJson()
    {
        return JsonSerializer.Serialize(_settings, JsonOptions);
    }

    public void SaveSettingsFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, JsonReadOptions);
            if (loaded != null)
            {
                if (loaded.GameInstallDirs == null) loaded.GameInstallDirs = new List<string>();
                if (loaded.CustomGameNames == null) loaded.CustomGameNames = new Dictionary<string, string>();
                loaded.PkgSources = loaded.PkgSources == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(loaded.PkgSources, StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(loaded.StoreServerUrl))
                    loaded.StoreServerUrl = DefaultStoreServerUrl;
                _settings = loaded;
                _settingsService.Save(_settings);
            }
        }
        catch (Exception ex)
        {
            ShowMessage("Не удалось сохранить настройки: " + ex.Message, "warning");
        }
    }

    public void SetSelectedGame(string path)
    {
        _selectedGamePath = path;
        _settings.LastGamePath = path ?? "";
    }

    public bool LaunchGame()
    {
        _settingsService.Save(_settings);
        try
        {
            if (_lastLaunchedProcess != null && _lastLaunchedProcess.HasExited)
                _lastLaunchedProcess.Dispose();
        }
        catch
        {
            // ignored: process may already be unavailable
        }
        _lastLaunchedProcess = null;
        _lastLaunchStartedAtUtc = null;

        var gamePath = GetSelectedGamePathOrId();
        if (string.IsNullOrEmpty(_settings.EmulatorPath) || !File.Exists(_settings.EmulatorPath))
            return false;
        if (string.IsNullOrEmpty(gamePath))
            return false;

        var selectedEntry = GetSelectedGameEntry();
        var resolvedGamePath = selectedEntry?.Path ?? gamePath;
        _selectedGamePath = resolvedGamePath;
        _settings.LastGamePath = resolvedGamePath;

        _settings.PkgSources ??= new Dictionary<string, string>();
        if (_settings.PkgSources.TryGetValue(resolvedGamePath, out var pkgSourcePath) &&
            !string.IsNullOrWhiteSpace(pkgSourcePath) &&
            File.Exists(pkgSourcePath))
        {
            var gameRootDir = ResolveGameRootDirectoryFromGamePath(resolvedGamePath);
            var shouldExtract = ShouldRunPkgExtractBeforeLaunch(resolvedGamePath, gameRootDir);
            WriteInstallLog(
                $"Launch request for PKG-backed game. gamePath=\"{resolvedGamePath}\", root=\"{gameRootDir}\", pkg=\"{pkgSourcePath}\", shouldExtract={shouldExtract}",
                "LAUNCH",
                gameRootDir);

            if (!shouldExtract)
            {
                var directProcess = _launchService.LaunchProcess(_settings, resolvedGamePath);
                if (directProcess == null)
                    return false;
                _lastLaunchedProcess = directProcess;
                _lastLaunchStartedAtUtc = DateTimeOffset.UtcNow;
                return true;
            }

            return TryStartPkgOperation("extract", resolvedGamePath, pkgSourcePath, () =>
            {
                RunPkgExtractAndLaunch(resolvedGamePath, pkgSourcePath);
            });
        }

        var process = _launchService.LaunchProcess(_settings, resolvedGamePath);
        if (process == null)
            return false;

        _lastLaunchedProcess = process;
        _lastLaunchStartedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }

    public string AddPkgGame(string pkgPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pkgPath))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    started = false,
                    message = "PKG path is empty."
                }, JsonOptions);
            }

            var normalized = Path.GetFullPath(pkgPath.Trim());
            if (!File.Exists(normalized))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    started = false,
                    message = "PKG file was not found."
                }, JsonOptions);
            }

            var started = TryStartPkgOperation("import", "", normalized, () =>
            {
                RunPkgImport(normalized);
            });

            if (!started)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    started = false,
                    message = "Another PKG operation is already running."
                }, JsonOptions);
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                started = true,
                message = "PKG import started."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                started = false,
                message = ex.Message
            }, JsonOptions);
        }
    }

    public string GetPkgOperationStatusJson()
    {
        var snapshot = SnapshotPkgOperation();
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private bool TryStartPkgOperation(string kind, string gamePath, string pkgPath, Action worker)
    {
        lock (_pkgOpSync)
        {
            if (_pkgOperation.Active)
                return false;

            _pkgOperation = new PkgOperationStatus
            {
                Active = true,
                Kind = string.IsNullOrWhiteSpace(kind) ? "none" : kind.Trim().ToLowerInvariant(),
                Stage = "start",
                Message = "Starting operation...",
                GamePath = gamePath ?? "",
                PkgPath = pkgPath ?? "",
                Current = 0,
                Total = 0,
                Percent = 0,
                Success = false,
                Failed = false,
                CurrentFile = "",
                PreviewImagePath = "",
                PreviewTitle = "",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        WriteInstallLog(
            $"PKG operation started. kind={kind}, gamePath=\"{gamePath}\", pkgPath=\"{pkgPath}\"",
            "PKG",
            ResolveLogInstallDirHint(gamePath));

        _pkgOperationTask = Task.Run(() =>
        {
            try
            {
                worker();
            }
            catch (Exception ex)
            {
                WriteInstallLog("Unhandled PKG operation exception: " + ex, "ERROR", ResolveLogInstallDirHint(gamePath));
                SetPkgOperationFailed(ex.Message);
                ShowMessage("PKG operation failed: " + ex.Message, "error");
            }
        });

        return true;
    }

    private void SetPkgOperationProgress(string stage, int current, int total, string currentFile, string? message = null)
    {
        lock (_pkgOpSync)
        {
            var safeTotal = Math.Max(0, total);
            var safeCurrent = Math.Max(0, current);
            _pkgOperation.Stage = stage ?? _pkgOperation.Stage;
            _pkgOperation.Current = safeCurrent;
            _pkgOperation.Total = safeTotal;
            _pkgOperation.CurrentFile = currentFile ?? "";
            if (safeTotal > 0)
            {
                var pct = (int)Math.Round(safeCurrent * 100.0 / safeTotal);
                _pkgOperation.Percent = Math.Clamp(pct, 0, 100);
            }
            else
            {
                _pkgOperation.Percent = 0;
            }
            if (!string.IsNullOrWhiteSpace(message))
                _pkgOperation.Message = message.Trim();
            _pkgOperation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void SetPkgOperationMessage(string stage, string message)
    {
        lock (_pkgOpSync)
        {
            _pkgOperation.Stage = stage ?? _pkgOperation.Stage;
            _pkgOperation.Message = message ?? "";
            _pkgOperation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void SetPkgOperationPreview(string previewImagePath, string previewTitle)
    {
        lock (_pkgOpSync)
        {
            _pkgOperation.PreviewImagePath = previewImagePath ?? "";
            _pkgOperation.PreviewTitle = previewTitle ?? "";
            _pkgOperation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void SetPkgOperationCompleted(string message)
    {
        var snapshot = SnapshotPkgOperation();
        WriteInstallLog(
            $"PKG operation completed. kind={snapshot.Kind}, stage={snapshot.Stage}, message={message}",
            "PKG",
            ResolveLogInstallDirHint(snapshot.GamePath));

        lock (_pkgOpSync)
        {
            _pkgOperation.Active = false;
            _pkgOperation.Success = true;
            _pkgOperation.Failed = false;
            _pkgOperation.Stage = "done";
            _pkgOperation.Percent = 100;
            _pkgOperation.Message = message ?? "Done.";
            _pkgOperation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void SetPkgOperationFailed(string message)
    {
        var snapshot = SnapshotPkgOperation();
        WriteInstallLog(
            $"PKG operation failed. kind={snapshot.Kind}, stage={snapshot.Stage}, message={message}",
            "ERROR",
            ResolveLogInstallDirHint(snapshot.GamePath));

        lock (_pkgOpSync)
        {
            _pkgOperation.Active = false;
            _pkgOperation.Success = false;
            _pkgOperation.Failed = true;
            _pkgOperation.Stage = "error";
            _pkgOperation.Message = string.IsNullOrWhiteSpace(message) ? "Operation failed." : message.Trim();
            _pkgOperation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private PkgOperationStatus SnapshotPkgOperation()
    {
        lock (_pkgOpSync)
        {
            return new PkgOperationStatus
            {
                Active = _pkgOperation.Active,
                Kind = _pkgOperation.Kind,
                Stage = _pkgOperation.Stage,
                Message = _pkgOperation.Message,
                GamePath = _pkgOperation.GamePath,
                PkgPath = _pkgOperation.PkgPath,
                CurrentFile = _pkgOperation.CurrentFile,
                Current = _pkgOperation.Current,
                Total = _pkgOperation.Total,
                Percent = _pkgOperation.Percent,
                Success = _pkgOperation.Success,
                Failed = _pkgOperation.Failed,
                PreviewImagePath = _pkgOperation.PreviewImagePath,
                PreviewTitle = _pkgOperation.PreviewTitle,
                UpdatedAtUtc = _pkgOperation.UpdatedAtUtc
            };
        }
    }

    private void RunPkgImport(string pkgPath)
    {
        var rootInstallDir = ResolvePkgGamesRootDir();
        EnsureDirectoryExists(rootInstallDir);
        EnsureInstallDirectoryRegistered(rootInstallDir);
        WriteInstallLog($"Import request: pkg=\"{pkgPath}\", installRoot=\"{rootInstallDir}\"", "IMPORT", rootInstallDir);

        var toolPath = ResolvePkgVirtualDiskPath();
        if (!File.Exists(toolPath))
        {
            WriteInstallLog($"PkgVirtualDisk.exe not found: \"{toolPath}\"", "ERROR", rootInstallDir);
            SetPkgOperationFailed("PkgVirtualDisk.exe not found in tools folder.");
            return;
        }

        var pkgFileNameNoExt = Path.GetFileNameWithoutExtension(pkgPath);

        SetPkgOperationMessage("prepare", "Checking X: virtual drive...");
        if (!EnsureVirtualDriveXReady(toolPath, out var driveError, rootInstallDir))
        {
            SetPkgOperationFailed(driveError);
            return;
        }

        var mounted = false;
        var gameDir = "";
        try
        {
            SetPkgOperationMessage("mount", "Mounting PKG...");
            WriteInstallLog("Mounting package to X:...", "MOUNT", rootInstallDir);
            var mountResult = RunProcessWithOutput(
                toolPath,
                $"mount --pkg {QuoteArg(pkgPath)} --drive x:",
                line =>
                {
                    var text = string.IsNullOrWhiteSpace(line) ? "Mounting PKG..." : line;
                    SetPkgOperationMessage("mount", text);
                    WriteInstallLog("mount: " + text, "MOUNT", rootInstallDir);
                },
                timeoutMs: 120000);
            LogProcessResultSummary("mount", mountResult, rootInstallDir);

            if (mountResult.TimedOut || mountResult.ExitCode != 0)
            {
                var err = BuildProcessError("Could not mount PKG.", mountResult);
                SetPkgOperationFailed(err);
                return;
            }

            mounted = true;
            var mountedRoot = @"X:\";
            if (!Directory.Exists(mountedRoot))
            {
                SetPkgOperationFailed("Drive X: is not available after mount.");
                return;
            }

            var detected = DetectPkgMetadata(mountedRoot, pkgFileNameNoExt);
            var folderName = BuildPkgTargetFolderName(detected.title, detected.titleId, pkgFileNameNoExt);
            gameDir = EnsureUniqueDirectory(Path.Combine(rootInstallDir, folderName));
            EnsureDirectoryExists(gameDir);
            var previewImagePath = TryPrepareImportPreviewImage(mountedRoot, rootInstallDir, gameDir, folderName);
            SetPkgOperationPreview(previewImagePath, detected.title);
            WriteInstallLog(
                $"Detected title=\"{detected.title}\", titleId=\"{detected.titleId}\", targetDir=\"{gameDir}\"",
                "IMPORT",
                rootInstallDir);

            SetPkgOperationMessage("copy", "Copying files from mounted package...");
            CopyDirectoryWithProgress(
                mountedRoot,
                gameDir,
                (current, total, relPath) =>
                {
                    var msg = $"Copying files... {current}/{Math.Max(1, total)}";
                    SetPkgOperationProgress("copy", current, total, relPath, msg);
                    WriteInstallLog($"copy [{current}/{Math.Max(1, total)}] {relPath}", "COPY", rootInstallDir);
                });
            WriteInstallLog("Copy from mounted package completed.", "COPY", rootInstallDir);

            SetPkgOperationMessage("finalize", "Finalizing package layout...");
            FlattenUrootFolder(gameDir);
            WriteInstallLog("Flattened uroot folder if present.", "FINALIZE", rootInstallDir);

            var preExtractEbootPath = FindFirstFileCaseInsensitive(gameDir, "eboot.bin", maxDepth: 12);
            if (string.IsNullOrWhiteSpace(preExtractEbootPath))
                WriteInstallLog("eboot.bin not found after mount copy. Full extract is required.", "WARN", rootInstallDir);
            else
                WriteInstallLog($"eboot.bin found after mount copy: \"{preExtractEbootPath}\". Running full extract to finalize install.", "INFO", rootInstallDir);

            if (mounted || DriveLetterExists('X'))
            {
                var preExtractUnmount = RunProcessWithOutput(
                    toolPath,
                    "unmount --drive x:",
                    line =>
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            WriteInstallLog("unmount (before full extract): " + line, "UNMOUNT", rootInstallDir);
                    },
                    timeoutMs: 30000);
                LogProcessResultSummary("unmount(before full extract)", preExtractUnmount, rootInstallDir);
                mounted = false;
            }

            SetPkgOperationMessage("extract", "Running full PKG extract...");
            var pkgToolCore = ResolvePkgToolCorePath(toolPath);
            var extractMode = !string.IsNullOrWhiteSpace(pkgToolCore) ? "pkgtool" : "internal";
            WriteInstallLog($"Import extractor selected: {extractMode}", "EXTRACT", rootInstallDir);
            var fullExtractResult = RunProcessWithOutput(
                toolPath,
                BuildPkgExtractArguments(pkgPath, gameDir, extractMode, pkgToolCore),
                line =>
                {
                    if (string.IsNullOrWhiteSpace(line))
                        return;

                    var m = ExtractProgressRegex.Match(line);
                    if (m.Success &&
                        int.TryParse(m.Groups[1].Value, out var current) &&
                        int.TryParse(m.Groups[2].Value, out var total))
                    {
                        var filePart = m.Groups.Count > 3 ? m.Groups[3].Value.Trim() : "";
                        var msg = $"Extracting files... {current}/{Math.Max(1, total)}";
                        SetPkgOperationProgress("extract", current, total, filePart, msg);
                        WriteInstallLog($"extract [{current}/{Math.Max(1, total)}] {filePart}", "EXTRACT", rootInstallDir);
                        return;
                    }

                    SetPkgOperationMessage("extract", line);
                    WriteInstallLog("extract: " + line, "EXTRACT", rootInstallDir);
                });
            LogProcessResultSummary("extract(import)", fullExtractResult, rootInstallDir);

            if (fullExtractResult.TimedOut || fullExtractResult.ExitCode != 0)
            {
                var err = BuildProcessError("PKG extraction during import failed.", fullExtractResult);
                SetPkgOperationFailed(err);
                return;
            }

            SetPkgOperationMessage("finalize", "Finalizing extracted files...");
            FlattenUrootFolder(gameDir);
            WriteInstallLog("Flattened uroot folder after full extract.", "FINALIZE", rootInstallDir);

            // Копирование param.sfo в sce_sys
            try
            {
                var paramSfoPath = Path.Combine(gameDir, "param.sfo");
                var sceSysDir = Path.Combine(gameDir, "sce_sys");

                if (File.Exists(paramSfoPath))
                {
                    WriteInstallLog($"Found param.sfo at: {paramSfoPath}", "INFO", rootInstallDir);

                    // Создаем папку sce_sys, если она не существует
                    EnsureDirectoryExists(sceSysDir);
                    WriteInstallLog($"Ensured sce_sys directory exists: {sceSysDir}", "INFO", rootInstallDir);

                    var targetParamSfoPath = Path.Combine(sceSysDir, "param.sfo");

                    // Копируем файл (перезаписываем если существует)
                    File.Copy(paramSfoPath, targetParamSfoPath, overwrite: true);
                    WriteInstallLog($"Copied param.sfo to: {targetParamSfoPath}", "INFO", rootInstallDir);

                    SetPkgOperationMessage("finalize", "Created param.sfo copy in sce_sys folder");
                }
                else
                {
                    WriteInstallLog($"param.sfo not found at: {paramSfoPath}", "WARN", rootInstallDir);
                }
            }
            catch (Exception ex)
            {
                WriteInstallLog($"Error copying param.sfo to sce_sys: {ex.Message}", "ERROR", rootInstallDir);
                // Не прерываем выполнение, так как это не критическая операция
            }

            var ebootPath = FindFirstFileCaseInsensitive(gameDir, "eboot.bin", maxDepth: 12);
            if (string.IsNullOrWhiteSpace(ebootPath))
            {
                SetPkgOperationFailed("Imported PKG does not contain eboot.bin.");
                return;
            }
            TryWritePkgExtractMarker(ResolveGameRootDirectoryFromGamePath(ebootPath), pkgPath);

            _settings.PkgSources ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _settings.PkgSources[ebootPath] = pkgPath;
            if (!_settings.CustomGameNames.ContainsKey(ebootPath) && !string.IsNullOrWhiteSpace(detected.title))
                _settings.CustomGameNames[ebootPath] = detected.title.Trim();
            _selectedGamePath = ebootPath;
            _settings.LastGamePath = ebootPath;
            _settingsService.Save(_settings);
            WriteInstallLog($"Registered imported game path: \"{ebootPath}\"", "IMPORT", rootInstallDir);

            RefreshGameList();
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                NotifyRefreshGames();
                ShowMessage($"PKG imported: {detected.title}", "info");
            });

            SetPkgOperationCompleted("PKG import completed.");
        }
        finally
        {
            if (mounted || DriveLetterExists('X'))
            {
                WriteInstallLog("Unmounting X: after import...", "UNMOUNT", rootInstallDir);
                var unmountResult = RunProcessWithOutput(
                    toolPath,
                    "unmount --drive x:",
                    line =>
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            WriteInstallLog("unmount: " + line, "UNMOUNT", rootInstallDir);
                    },
                    timeoutMs: 30000);
                LogProcessResultSummary("unmount(finally)", unmountResult, rootInstallDir);
            }
        }
    }

    private void RunPkgExtractAndLaunch(string gamePath, string pkgPath)
    {
        var gameDir = ResolveGameRootDirectoryFromGamePath(gamePath);
        WriteInstallLog($"Extract request: gamePath=\"{gamePath}\", gameRoot=\"{gameDir}\", pkg=\"{pkgPath}\"", "EXTRACT", gameDir);

        var toolPath = ResolvePkgVirtualDiskPath();
        if (!File.Exists(toolPath))
        {
            WriteInstallLog($"PkgVirtualDisk.exe not found: \"{toolPath}\"", "ERROR", gameDir);
            SetPkgOperationFailed("PkgVirtualDisk.exe not found in tools folder.");
            return;
        }

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            SetPkgOperationFailed("Game path is empty.");
            return;
        }

        if (string.IsNullOrWhiteSpace(gameDir))
        {
            SetPkgOperationFailed("Could not resolve game directory.");
            return;
        }

        EnsureDirectoryExists(gameDir);
        SetPkgOperationMessage("prepare", "Preparing extraction...");

        var pkgToolCore = ResolvePkgToolCorePath(toolPath);
        var extractor = !string.IsNullOrWhiteSpace(pkgToolCore) ? "pkgtool" : "internal";
        WriteInstallLog($"Launch extractor selected: {extractor}", "EXTRACT", gameDir);

        if (extractor == "internal" && !EnsureVirtualDriveXReady(toolPath, out var driveError, gameDir))
        {
            SetPkgOperationFailed(driveError);
            return;
        }

        SetPkgOperationMessage("extract", "Extracting PKG...");
        var extractResult = RunProcessWithOutput(
            toolPath,
            BuildPkgExtractArguments(pkgPath, gameDir, extractor, pkgToolCore),
            line =>
            {
                if (string.IsNullOrWhiteSpace(line))
                    return;

                var m = ExtractProgressRegex.Match(line);
                if (m.Success &&
                    int.TryParse(m.Groups[1].Value, out var current) &&
                    int.TryParse(m.Groups[2].Value, out var total))
                {
                    var filePart = m.Groups.Count > 3 ? m.Groups[3].Value.Trim() : "";
                    var msg = $"Extracting files... {current}/{Math.Max(1, total)}";
                    SetPkgOperationProgress("extract", current, total, filePart, msg);
                    WriteInstallLog($"extract [{current}/{Math.Max(1, total)}] {filePart}", "EXTRACT", gameDir);
                    return;
                }

                SetPkgOperationMessage("extract", line);
                WriteInstallLog("extract: " + line, "EXTRACT", gameDir);
            });
        LogProcessResultSummary("extract", extractResult, gameDir);

        if (extractResult.TimedOut || extractResult.ExitCode != 0)
        {
            var err = BuildProcessError("PKG extraction failed.", extractResult);
            SetPkgOperationFailed(err);
            return;
        }

        SetPkgOperationMessage("finalize", "Finalizing extracted files...");
        FlattenUrootFolder(gameDir);
        WriteInstallLog("Flattened uroot folder after extract.", "FINALIZE", gameDir);
        RefreshGameList();
        Application.Current.Dispatcher.BeginInvoke(NotifyRefreshGames);

        var launchPath = FindFirstFileCaseInsensitive(gameDir, "eboot.bin", maxDepth: 12) ?? gamePath;

        if (!string.Equals(launchPath, gamePath, StringComparison.OrdinalIgnoreCase))
            MoveGameMappingsToNewPath(gamePath, launchPath);

        var process = _launchService.LaunchProcess(_settings, launchPath);
        if (process == null)
        {
            SetPkgOperationFailed("Extraction completed, but game launch failed.");
            return;
        }

        TryWritePkgExtractMarker(gameDir, pkgPath);
        _selectedGamePath = launchPath;
        _settings.LastGamePath = launchPath;
        _settingsService.Save(_settings);
        _lastLaunchedProcess = process;
        _lastLaunchStartedAtUtc = DateTimeOffset.UtcNow;
        WriteInstallLog($"Extraction finished. Launch path=\"{launchPath}\"", "EXTRACT", gameDir);
        SetPkgOperationCompleted("Extraction completed. Launching game...");
    }

    private static string ResolvePkgVirtualDiskPath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var primary = Path.Combine(baseDir, "tools", "PkgVirtualDisk.exe");
        if (File.Exists(primary))
            return primary;

        var fallback = Path.Combine(baseDir, "PkgVirtualDisk.exe");
        if (File.Exists(fallback))
            return fallback;

        return primary;
    }

    private static string? ResolvePkgToolCorePath(string pkgVirtualDiskPath)
    {
        var dir = Path.GetDirectoryName(pkgVirtualDiskPath) ?? AppDomain.CurrentDomain.BaseDirectory;
        var dll = Path.Combine(dir, "PkgTool.Core.dll");
        if (File.Exists(dll))
            return dll;
        var exe = Path.Combine(dir, "PkgTool.Core.exe");
        if (File.Exists(exe))
            return exe;
        return null;
    }

    private static string BuildPkgExtractArguments(string pkgPath, string outputDir, string extractor, string? pkgToolCorePath)
    {
        var mode = string.IsNullOrWhiteSpace(extractor) ? "auto" : extractor.Trim().ToLowerInvariant();
        var args = $"extract -pkg {QuoteArg(pkgPath)} --dir {QuoteArg(outputDir)} --extractor {mode}";
        if (mode == "pkgtool" && !string.IsNullOrWhiteSpace(pkgToolCorePath))
            args += $" --pkgtool-core {QuoteArg(pkgToolCorePath)}";
        return args;
    }

    private string TryPrepareImportPreviewImage(string mountedRoot, string rootInstallDir, string gameDir, string folderName)
    {
        try
        {
            var source = FindBackgroundPath(mountedRoot);
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                source = FindFirstFileCaseInsensitive(mountedRoot, "pic1.png", maxDepth: 3)
                    ?? FindFirstFileCaseInsensitive(mountedRoot, "icon0.png", maxDepth: 3);
            }

            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                return "";

            var previewDir = Path.Combine(rootInstallDir, ".install_preview");
            EnsureDirectoryExists(previewDir);

            var ext = Path.GetExtension(source);
            if (string.IsNullOrWhiteSpace(ext))
                ext = ".png";
            var safeName = SanitizePathSegment(folderName);
            var previewPath = Path.Combine(previewDir, safeName + ext);
            File.Copy(source, previewPath, overwrite: true);

            // Also copy next to the target game for persistence after import.
            var gamePreviewPath = Path.Combine(gameDir, "pic1.png");
            if (!string.Equals(previewPath, gamePreviewPath, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(source, gamePreviewPath, overwrite: true); } catch { }
            }

            return previewPath;
        }
        catch (Exception ex)
        {
            WriteInstallLog("Failed to prepare install preview image: " + ex.Message, "WARN", rootInstallDir);
            return "";
        }
    }

    private static string ResolvePkgTitleFallback(string pkgPath)
    {
        return Path.GetFileNameWithoutExtension(pkgPath) ?? "PKG Game";
    }

    private static string BuildPkgTargetFolderName(string title, string titleId, string fallbackName)
    {
        var safeTitle = SanitizePathSegment(string.IsNullOrWhiteSpace(title) ? fallbackName : title);
        var safeId = SanitizePathSegment(titleId);
        if (!string.IsNullOrWhiteSpace(safeId))
            return $"{safeTitle}-{safeId}";
        return safeTitle;
    }

    private static string SanitizePathSegment(string value)
    {
        var v = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(v))
            return "game";

        foreach (var c in Path.GetInvalidFileNameChars())
            v = v.Replace(c, '_');
        v = v.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        while (v.Contains("  ", StringComparison.Ordinal))
            v = v.Replace("  ", " ", StringComparison.Ordinal);
        v = v.Trim(' ', '.', '_');
        if (string.IsNullOrWhiteSpace(v))
            v = "game";
        if (v.Length > 120)
            v = v[..120].TrimEnd(' ', '.', '_');
        return string.IsNullOrWhiteSpace(v) ? "game" : v;
    }

    private static void EnsureDirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }

    private static bool DriveLetterExists(char driveLetter)
    {
        var letter = char.ToUpperInvariant(driveLetter);
        return DriveInfo.GetDrives().Any(d => d.Name.Length > 0 && char.ToUpperInvariant(d.Name[0]) == letter);
    }

    private bool EnsureVirtualDriveXReady(string toolPath, out string error, string? installDirHint = null)
    {
        error = "";
        // If previous run crashed and X: stayed mounted, try to unmount it first.
        if (DriveLetterExists('X'))
        {
            WriteInstallLog("Drive X: is busy, trying forced unmount.", "MOUNT", installDirHint);
            var unmountResult = RunProcessWithOutput(
                toolPath,
                "unmount --drive x:",
                line =>
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        WriteInstallLog("unmount(pre-check): " + line, "UNMOUNT", installDirHint);
                },
                timeoutMs: 30000);
            LogProcessResultSummary("unmount(pre-check)", unmountResult, installDirHint);
        }

        if (DriveLetterExists('X'))
        {
            error = "Drive X: is busy. Release it and try again.";
            WriteInstallLog(error, "ERROR", installDirHint);
            return false;
        }

        WriteInstallLog("Drive X: is free.", "MOUNT", installDirHint);
        return true;
    }

    private static string QuoteArg(string value)
    {
        var safe = value ?? "";
        return "\"" + safe.Replace("\"", "\\\"") + "\"";
    }

    private static ProcessResult RunProcessWithOutput(string fileName, string arguments, Action<string>? onOutput = null, int timeoutMs = 0)
    {
        var result = new ProcessResult();
        var stdout = new List<string>();
        var stderr = new List<string>();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? "",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppDomain.CurrentDomain.BaseDirectory
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (stdout) stdout.Add(e.Data);
            onOutput?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (stderr) stderr.Add(e.Data);
            onOutput?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (timeoutMs > 0)
        {
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                result.TimedOut = true;
            }
        }
        else
        {
            process.WaitForExit();
        }

        process.WaitForExit();
        lock (stdout) result.StdOutLines = stdout.ToList();
        lock (stderr) result.StdErrLines = stderr.ToList();
        result.ExitCode = process.ExitCode;
        return result;
    }

    private static string BuildProcessError(string prefix, ProcessResult result)
    {
        var lines = result.StdErrLines.Count > 0 ? result.StdErrLines : result.StdOutLines;
        var tail = string.Join(" | ", lines.TakeLast(4));
        if (result.TimedOut)
            return $"{prefix} Timeout.";
        if (!string.IsNullOrWhiteSpace(tail))
            return $"{prefix} {tail}";
        return $"{prefix} Exit code: {result.ExitCode}.";
    }

    private (string title, string titleId) DetectPkgMetadata(string mountedRoot, string fallback)
    {
        var title = ResolvePkgTitleFallback(fallback);
        var titleId = "";
        try
        {
            var param = FindFirstFileCaseInsensitive(mountedRoot, "param.sfo", maxDepth: 10);
            if (!string.IsNullOrWhiteSpace(param) && File.Exists(param))
            {
                title = ParamSfoReader.GetTitle(param) ?? title;
                titleId = ParamSfoReader.GetTitleId(param) ?? "";
            }
        }
        catch
        {
            // keep fallback values
        }
        return (title, titleId);
    }

    private static string? FindFirstFileCaseInsensitive(string rootDir, string fileName, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            return null;

        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((rootDir, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
            catch
            {
                // ignore unreadable directories
            }

            if (depth >= maxDepth)
                continue;

            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                    queue.Enqueue((sub, depth + 1));
            }
            catch
            {
                // ignore unreadable directories
            }
        }

        return null;
    }

    private static string EnsureUniqueDirectory(string initialPath)
    {
        if (!Directory.Exists(initialPath))
            return initialPath;

        var parent = Path.GetDirectoryName(initialPath) ?? "";
        var baseName = Path.GetFileName(initialPath);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(parent, $"{baseName}-{i}");
            if (!Directory.Exists(candidate))
                return candidate;
        }
        return Path.Combine(parent, $"{baseName}-{DateTime.UtcNow:yyyyMMddHHmmss}");
    }

    private static void CopyDirectoryWithProgress(string sourceRoot, string targetRoot, Action<int, int, string> onFileCopied)
    {
        var src = Path.GetFullPath(sourceRoot);
        var dst = Path.GetFullPath(targetRoot);
        EnsureDirectoryExists(dst);

        var files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).ToList();
        var total = files.Count;
        var current = 0;

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(src, file);
            var outPath = Path.Combine(dst, relative);
            var outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrWhiteSpace(outDir))
                EnsureDirectoryExists(outDir);

            File.Copy(file, outPath, overwrite: true);
            current++;
            onFileCopied(current, total, relative);
        }
    }

    private static void FlattenUrootFolder(string gameDir)
    {
        var uroot = Path.Combine(gameDir, "uroot");
        if (!Directory.Exists(uroot))
            return;

        foreach (var dir in Directory.GetDirectories(uroot))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var target = Path.Combine(gameDir, name);
            CopyDirectoryOverwrite(dir, target);
            Directory.Delete(dir, recursive: true);
        }

        foreach (var file in Directory.GetFiles(uroot))
        {
            var target = Path.Combine(gameDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
            File.Delete(file);
        }

        try { Directory.Delete(uroot, recursive: true); } catch { }
    }

    private static void CopyDirectoryOverwrite(string sourceDir, string targetDir)
    {
        EnsureDirectoryExists(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var target = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(sub);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            CopyDirectoryOverwrite(sub, Path.Combine(targetDir, name));
        }
    }

    private static bool IsAppSubdirName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return string.Equals(name, "app", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "app0", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveGameRootDirectoryFromGamePath(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return "";

        var dir = Path.GetDirectoryName(gamePath) ?? "";
        if (string.IsNullOrWhiteSpace(dir))
            return "";

        try { dir = Path.GetFullPath(dir); } catch { }
        var leaf = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (IsAppSubdirName(leaf))
        {
            var parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrWhiteSpace(parent))
                return parent;
        }

        return dir;
    }

    private static string GetPkgExtractMarkerPath(string gameRootDir)
    {
        return Path.Combine(gameRootDir, ".pkg_extracted");
    }

    private bool ShouldRunPkgExtractBeforeLaunch(string gamePath, string gameRootDir)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(gameRootDir))
            return true;
        if (!File.Exists(gamePath))
            return true;

        var marker = GetPkgExtractMarkerPath(gameRootDir);
        return !File.Exists(marker);
    }

    private void TryWritePkgExtractMarker(string gameRootDir, string pkgPath)
    {
        if (string.IsNullOrWhiteSpace(gameRootDir))
            return;
        try
        {
            EnsureDirectoryExists(gameRootDir);
            var marker = GetPkgExtractMarkerPath(gameRootDir);
            var body = $"extractedAtUtc={DateTimeOffset.UtcNow:O}{Environment.NewLine}pkgPath={pkgPath}";
            File.WriteAllText(marker, body, Encoding.UTF8);
            WriteInstallLog($"Wrote extract marker: \"{marker}\"", "EXTRACT", gameRootDir);
        }
        catch (Exception ex)
        {
            WriteInstallLog("Failed to write extract marker: " + ex.Message, "WARN", gameRootDir);
        }
    }

    private void TryClearPkgExtractMarker(string gameRootDir)
    {
        if (string.IsNullOrWhiteSpace(gameRootDir))
            return;
        try
        {
            var marker = GetPkgExtractMarkerPath(gameRootDir);
            if (File.Exists(marker))
            {
                File.Delete(marker);
                WriteInstallLog($"Removed extract marker: \"{marker}\"", "EXTRACT", gameRootDir);
            }
        }
        catch (Exception ex)
        {
            WriteInstallLog("Failed to remove extract marker: " + ex.Message, "WARN", gameRootDir);
        }
    }

    private void MoveGameMappingsToNewPath(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
            return;
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (_settings.PkgSources.TryGetValue(oldPath, out var src))
        {
            _settings.PkgSources.Remove(oldPath);
            _settings.PkgSources[newPath] = src;
        }

        if (_settings.CustomGameNames.TryGetValue(oldPath, out var customTitle))
        {
            _settings.CustomGameNames.Remove(oldPath);
            _settings.CustomGameNames[newPath] = customTitle;
        }
    }

    private string ResolveLogInstallDirHint(string? gamePath)
    {
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            var root = ResolveGameRootDirectoryFromGamePath(gamePath);
            if (!string.IsNullOrWhiteSpace(root))
                return root;
        }
        return ResolvePkgGamesRootDir();
    }

    private string ResolveInstallLogPath(string? installDirHint = null)
    {
        var baseDir = !string.IsNullOrWhiteSpace(installDirHint)
            ? installDirHint!.Trim()
            : ResolvePkgGamesRootDir();
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Games");

        try { baseDir = Path.GetFullPath(baseDir); } catch { }
        EnsureDirectoryExists(baseDir);
        return Path.Combine(baseDir, "Install.log");
    }

    private void WriteInstallLog(string message, string level = "INFO", string? installDirHint = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            var path = ResolveInstallLogPath(installDirHint);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{(string.IsNullOrWhiteSpace(level) ? "INFO" : level)}] {message}";
            lock (_installLogSync)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // do not break main flow on logging failures
        }
    }

    private void LogProcessResultSummary(string opName, ProcessResult result, string? installDirHint = null)
    {
        WriteInstallLog(
            $"{opName}: exitCode={result.ExitCode}, timedOut={result.TimedOut}, stdoutLines={result.StdOutLines.Count}, stderrLines={result.StdErrLines.Count}",
            "PROC",
            installDirHint);
    }

    private string ResolvePkgGamesRootDir()
    {
        if (!string.IsNullOrWhiteSpace(_settings.PkgGamesDir))
            return _settings.PkgGamesDir.Trim();

        var firstInstallDir = _settings.GameInstallDirs?
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d));
        if (!string.IsNullOrWhiteSpace(firstInstallDir))
            return firstInstallDir.Trim();

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Games");
    }

    private void EnsureInstallDirectoryRegistered(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return;

        var normalized = installDir.Trim();
        _settings.GameInstallDirs ??= new List<string>();
        var exists = _settings.GameInstallDirs.Any(x => string.Equals(x?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
        if (!exists)
            _settings.GameInstallDirs.Add(normalized);

        if (!string.IsNullOrWhiteSpace(_settings.EmulatorPath) && File.Exists(_settings.EmulatorPath))
            _launchService.AddGameFolder(_settings.EmulatorPath, normalized);

        _settings.PkgGamesDir = normalized;
        _settingsService.Save(_settings);
    }

    public string GetLastLaunchStatusJson()
    {
        var launched = _lastLaunchedProcess != null && _lastLaunchStartedAtUtc.HasValue;
        var hasExited = false;
        var running = false;
        int? exitCode = null;

        if (_lastLaunchedProcess != null)
        {
            try
            {
                hasExited = _lastLaunchedProcess.HasExited;
                running = !hasExited;
                if (hasExited)
                    exitCode = _lastLaunchedProcess.ExitCode;
            }
            catch
            {
                hasExited = true;
                running = false;
            }
        }

        var elapsedMs = _lastLaunchStartedAtUtc.HasValue
            ? Math.Max(0, (long)(DateTimeOffset.UtcNow - _lastLaunchStartedAtUtc.Value).TotalMilliseconds)
            : 0;

        var status = new
        {
            launched,
            running,
            hasExited,
            exitCode,
            elapsedMs,
            exitedWithin3s = hasExited && elapsedMs <= 3000
        };

        return JsonSerializer.Serialize(status, JsonOptions);
    }

    public string GetRecentLaunchErrorLinesJson()
    {
        var logPath = ResolveLaunchLogPath();
        var lines = ReadLinesWithSharedAccess(logPath);

        var errors = lines
            .Select(l => l?.Trim() ?? string.Empty)
            .Where(l => l.Length > 0 && IsErrorLogLine(l))
            .TakeLast(5)
            .Reverse()
            .ToList();

        if (errors.Count == 0)
        {
            errors = lines
                .Select(l => l?.Trim() ?? string.Empty)
                .Where(l => l.Length > 0)
                .TakeLast(5)
                .Reverse()
                .ToList();
        }

        return JsonSerializer.Serialize(errors, JsonOptions);
    }

    public string GetRecentLaunchLogLinesJson()
    {
        var logPath = ResolveLaunchLogPath();
        var lines = ReadLinesWithSharedAccess(logPath)
            .Select(l => l?.Trim() ?? string.Empty)
            .Where(l => l.Length > 0)
            .TakeLast(5)
            .Reverse()
            .ToList();

        return JsonSerializer.Serialize(lines, JsonOptions);
    }

    public bool CreateShortcut()
    {
        _settingsService.Save(_settings);
        var gamePath = GetSelectedGamePathOrId();
        if (string.IsNullOrEmpty(_settings.EmulatorPath) || !File.Exists(_settings.EmulatorPath))
            return false;
        if (string.IsNullOrEmpty(gamePath))
            return false;
        var entry = GetSelectedGameEntry();
        var name = entry?.DisplayTitle ?? Path.GetFileName(Path.GetDirectoryName(gamePath) ?? "ShadPS4");
        return _shortcutService.CreateDesktopShortcut(_settings, gamePath, name, _launchService);
    }

    public void OpenConfigFolder()
    {
        var dir = !string.IsNullOrWhiteSpace(_settings.UserDir) ? _settings.UserDir : _configService.GetDefaultUserDir();
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        try { Process.Start("explorer.exe", $"\"{dir}\""); }
        catch { }
    }

    public void OpenGeneralSettings()
    {
        var w = new GeneralSettingsWindow(_settings, _settingsService);
        w.Owner = this;
        w.ShowDialog();
    }

    public void OpenPathsWindow()
    {
        var w = new PathsWindow(_settings, _settingsService, _configService, _launchService, RefreshGameList);
        w.Owner = this;
        w.Closed += (_, _) => Dispatcher.BeginInvoke(NotifyRefreshGames);
        w.ShowDialog();
    }

    public void OpenGraphicsWindow()
    {
        var w = new GraphicsWindow(_settings, _configService);
        w.Owner = this;
        w.ShowDialog();
    }

    public void OpenAudioWindow()
    {
        var w = new AudioWindow(_settings, _configService);
        w.Owner = this;
        w.ShowDialog();
    }

    public void OpenInputWindow()
    {
        var w = new InputWindow(_settings, _configService);
        w.Owner = this;
        w.ShowDialog();
    }

    public void OpenLogsWindow()
    {
        var userDir = !string.IsNullOrWhiteSpace(_settings.UserDir) ? _settings.UserDir : _configService.GetDefaultUserDir();
        var w = new LogsWindow(userDir);
        w.Owner = this;
        w.ShowDialog();
    }

    private async Task OpenInAppBrowserInternalAsync(string target)
    {
        _inAppBrowserPendingUrl = target;
        InAppBrowserHost.Visibility = Visibility.Visible;
        await EnsureInAppBrowserReadyAsync();
        try
        {
            InAppBrowserView.CoreWebView2.Navigate(target);
            _inAppBrowserCurrentUrl = target;
        }
        catch
        {
            // ignore
        }
    }

    public void OpenInAppBrowser(string url)
    {
        var target = NormalizeBrowserUrl(url);
        Application.Current.Dispatcher.Invoke(() =>
        {
            _ = OpenInAppBrowserInternalAsync(target);
        });
    }

    public void CloseInAppBrowser()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            InAppBrowserHost.Visibility = Visibility.Collapsed;
        });
    }

    public void NavigateInAppBrowser(string url)
    {
        var target = NormalizeBrowserUrl(url);
        Application.Current.Dispatcher.Invoke(() =>
        {
            _ = OpenInAppBrowserInternalAsync(target);
        });
    }

    public void BrowserBack()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (InAppBrowserHost.Visibility != Visibility.Visible || !_inAppBrowserReady || InAppBrowserView.CoreWebView2 == null)
                return;

            if (InAppBrowserView.CoreWebView2.CanGoBack)
                InAppBrowserView.CoreWebView2.GoBack();
        });
    }

    public void BrowserForward()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (InAppBrowserHost.Visibility != Visibility.Visible || !_inAppBrowserReady || InAppBrowserView.CoreWebView2 == null)
                return;

            if (InAppBrowserView.CoreWebView2.CanGoForward)
                InAppBrowserView.CoreWebView2.GoForward();
        });
    }

    public void BrowserReload()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (InAppBrowserHost.Visibility != Visibility.Visible || !_inAppBrowserReady || InAppBrowserView.CoreWebView2 == null)
                return;
            InAppBrowserView.CoreWebView2.Reload();
        });
    }

    public string GetInAppBrowserUrl()
    {
        string url = "";
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (InAppBrowserHost.Visibility != Visibility.Visible)
                return;

            if (_inAppBrowserReady)
                url = InAppBrowserView.Source?.ToString() ?? _inAppBrowserCurrentUrl;
            else
                url = _inAppBrowserPendingUrl;
        });
        return url;
    }

    public bool IsInAppBrowserOpen()
    {
        var isOpen = false;
        Application.Current.Dispatcher.Invoke(() =>
        {
            isOpen = InAppBrowserHost.Visibility == Visibility.Visible;
        });
        return isOpen;
    }

    public void SetInAppBrowserBounds(double x, double y, double width, double height, double viewportWidth, double viewportHeight)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (InAppBrowserHost.Visibility != Visibility.Visible)
                return;

            if (width <= 2 || height <= 2)
                return;

            var sx = viewportWidth > 0.1 ? WebView.ActualWidth / viewportWidth : 1.0;
            var sy = viewportHeight > 0.1 ? WebView.ActualHeight / viewportHeight : 1.0;
            if (double.IsNaN(sx) || double.IsInfinity(sx) || sx <= 0) sx = 1.0;
            if (double.IsNaN(sy) || double.IsInfinity(sy) || sy <= 0) sy = 1.0;

            var left = Math.Max(0, x * sx);
            var top = Math.Max(0, y * sy);
            var w = Math.Max(1, width * sx);
            var h = Math.Max(1, height * sy);

            InAppBrowserHost.Margin = new Thickness(left, top, 0, 0);
            InAppBrowserHost.Width = w;
            InAppBrowserHost.Height = h;
        });
    }

    public void ExitLauncher()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                Close();
            }
            catch
            {
                Application.Current.Shutdown();
            }
        });
    }

    public void NotifyRefreshGames()
    {
        try
        {
            _ = WebView.CoreWebView2?.ExecuteScriptAsync("window.__launcherRefreshGames && window.__launcherRefreshGames();");
        }
        catch { }
    }

    public void ShowMessage(string message, string kind)
    {
        try
        {
            var msgJson = JsonSerializer.Serialize(message ?? string.Empty);
            var kindJson = JsonSerializer.Serialize(string.IsNullOrWhiteSpace(kind) ? "info" : kind);
            var script = $"window.__launcherNotify && window.__launcherNotify({msgJson}, {kindJson});";
            _ = WebView.CoreWebView2?.ExecuteScriptAsync(script);
        }
        catch
        {
            // ignore: if web layer is unavailable, there's nowhere to show UI notification
        }
    }

    public void SetGameDisplayName(string path, string displayTitle)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _settings.CustomGameNames[path] = displayTitle?.Trim() ?? "";
        _settingsService.Save(_settings);
    }

    public void RemoveGameDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _settings.CustomGameNames.Remove(path);
        _settingsService.Save(_settings);
    }

    public string OpenFolderDialog(string title)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new OpenFolderDialog { Title = title ?? "Select folder" };
            return dlg.ShowDialog() == true ? (dlg.FolderName ?? "") : "";
        });
    }

    public string OpenFileDialog(string title, string filter)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new OpenFileDialog { Title = title ?? "Select file", Filter = filter ?? "All|*.*" };
            return dlg.ShowDialog() == true ? (dlg.FileName ?? "") : "";
        });
    }

    public string GetConfigJson()
    {
        var userDir = string.IsNullOrWhiteSpace(_settings.UserDir) ? null : _settings.UserDir;
        var model = _configService.GetOrCreateModel(userDir);
        var snap = _configService.GetSnapshot(model);
        snap.GameInstallDirs = _settings.GameInstallDirs.ToList();
        snap.AddonDir = _settings.AddonDir;
        return JsonSerializer.Serialize(snap, JsonOptions);
    }

    public void SaveConfigJson(string json)
    {
        try
        {
            var snap = JsonSerializer.Deserialize<LauncherConfigSnapshot>(json, JsonOptions);
            if (snap == null) return;
            var userDir = string.IsNullOrWhiteSpace(_settings.UserDir) ? null : _settings.UserDir;
            var model = _configService.GetOrCreateModel(userDir);
            snap.GameInstallDirs = _settings.GameInstallDirs.ToList();
            snap.AddonDir = _settings.AddonDir;
            _configService.UpdateFromLauncher(model, snap);
            _configService.Save(userDir, model);
            _settings.WindowWidth = snap.WindowWidth;
            _settings.WindowHeight = snap.WindowHeight;
            _settingsService.Save(_settings);
        }
        catch { }
    }

    public string GetGameInstallDirsJson()
    {
        return JsonSerializer.Serialize(_settings.GameInstallDirs ?? new List<string>(), JsonOptions);
    }

    public void AddGameFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var p = path.Trim();
        if (_settings.GameInstallDirs.Contains(p)) return;
        _settings.GameInstallDirs.Add(p);
        if (!string.IsNullOrEmpty(_settings.EmulatorPath) && File.Exists(_settings.EmulatorPath))
            _launchService.AddGameFolder(_settings.EmulatorPath, p);
        _settingsService.Save(_settings);
        RefreshGameList();
        NotifyRefreshGames();
    }

    public void RemoveGameFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _settings.GameInstallDirs.Remove(path.Trim());
        _settingsService.Save(_settings);
        RefreshGameList();
        NotifyRefreshGames();
    }

    private string ResolveLaunchLogPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDataShadLog = Path.Combine(appData, "shadPS4", "log", "shad_log.txt");
        var candidates = new List<string> { appDataShadLog };

        if (!string.IsNullOrWhiteSpace(_settings.UserDir))
        {
            var userDir = _settings.UserDir.Trim();
            candidates.Add(Path.Combine(userDir, "log", "shad_log.txt"));
            candidates.Add(Path.Combine(userDir, "shad_log.txt"));
            candidates.Add(Path.Combine(userDir, "shadps4_log.txt"));
        }

        var logDir = GetEmulatorLogDirPath();
        if (!string.IsNullOrWhiteSpace(logDir))
        {
            candidates.Add(Path.Combine(logDir, "shad_log.txt"));
            candidates.Add(Path.Combine(logDir, "log.txt"));
        }

        return candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists) ?? appDataShadLog;
    }

    private static List<string> ReadLinesWithSharedAccess(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new List<string>();

        try
        {
            var lines = new List<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (line != null)
                    lines.Add(line);
            }
            return lines;
        }
        catch
        {
            try
            {
                return File.ReadAllLines(path, Encoding.UTF8).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
    }

    private static bool IsErrorLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return line.IndexOf("<Error>", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("fatal", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("fault", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("crash", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public string GetLogContent()
    {
        var launchLogPath = ResolveLaunchLogPath();
        if (File.Exists(launchLogPath))
        {
            try { return File.ReadAllText(launchLogPath, Encoding.UTF8); }
            catch (Exception ex) { return "Could not read log: " + ex.Message; }
        }

        var userDir = !string.IsNullOrWhiteSpace(_settings.UserDir) ? _settings.UserDir : _configService.GetDefaultUserDir();
        if (string.IsNullOrWhiteSpace(userDir) || !Directory.Exists(userDir))
            return "User folder not set or missing. Set it in Settings and run the emulator at least once.";
        var candidates = new[]
        {
            Path.Combine(userDir, "log", "shad_log.txt"),
            Path.Combine(userDir, "log.txt"),
            Path.Combine(userDir, "shadps4_log.txt"),
            Path.Combine(userDir, "log", "log.txt")
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                try { return File.ReadAllText(p); }
                catch (Exception ex) { return "Could not read log: " + ex.Message; }
            }
        }
        try
        {
            var latest = new DirectoryInfo(userDir)
                .GetFiles("*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest != null)
            {
                try { return File.ReadAllText(latest.FullName); }
                catch (Exception ex) { return "Could not read log: " + ex.Message; }
            }
        }
        catch { }
        return "No log file found in user folder. Run the emulator to generate logs.";
    }

    public bool ClearEmulatorLogs()
    {
        var logFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logDir = GetEmulatorLogDirPath();
        if (!string.IsNullOrWhiteSpace(logDir) && Directory.Exists(logDir))
        {
            try
            {
                foreach (var file in Directory.GetFiles(logDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file);
                    if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                        logFiles.Add(file);
                }
            }
            catch
            {
                // ignore and continue with known paths
            }
        }

        var launchLogPath = ResolveLaunchLogPath();
        if (File.Exists(launchLogPath))
            logFiles.Add(launchLogPath);

        var clearedAny = false;
        foreach (var file in logFiles)
        {
            try
            {
                File.Delete(file);
                clearedAny = true;
            }
            catch
            {
                try
                {
                    File.WriteAllText(file, string.Empty, Encoding.UTF8);
                    clearedAny = true;
                }
                catch
                {
                    // locked/unavailable: leave as is
                }
            }
        }

        return clearedAny;
    }

    public string GetDirectoryEntries(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                var drives = Environment.GetLogicalDrives();
                var arr = new List<object>();
                foreach (var d in drives)
                    arr.Add(new { name = d.TrimEnd('\\'), fullPath = d, isDirectory = true });
                return JsonSerializer.Serialize(arr, JsonOptions);
            }
            var dir = path.Trim();
            if (!Directory.Exists(dir)) return "[]";
            var list = new List<object>();
            foreach (var d in Directory.GetDirectories(dir).OrderBy(Path.GetFileName))
                list.Add(new { name = Path.GetFileName(d), fullPath = d, isDirectory = true });
            foreach (var f in Directory.GetFiles(dir).OrderBy(Path.GetFileName))
                list.Add(new { name = Path.GetFileName(f), fullPath = f, isDirectory = false });
            return JsonSerializer.Serialize(list, JsonOptions);
        }
        catch { return "[]"; }
    }

    public string GetAssetsBaseUrl()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return "file:///" + baseDir.Replace('\\', '/').TrimStart('/').Replace(" ", "%20") + "/";
    }

    public string GetEmulatorInputConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(_settings.UserDir))
        {
            var path = Path.Combine(_settings.UserDir.Trim(), "input_config");
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            return path;
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path2 = Path.Combine(appData, "shadPS4", "input_config");
        if (!Directory.Exists(path2))
            Directory.CreateDirectory(path2);
        return path2;
    }

    public void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch { }
    }

    public string ReadFileContent(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return "";
            return File.ReadAllText(path, System.Text.Encoding.UTF8);
        }
        catch { return ""; }
    }

    public void WriteFileContent(string path, string content)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, content ?? "", System.Text.Encoding.UTF8);
        }
        catch { }
    }

    public string GetLangJson(string langCode)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(langCode)) langCode = "ru";
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var path = Path.Combine(baseDir, "lang", langCode.Trim() + ".json");
            if (!File.Exists(path)) return "{}";
            return File.ReadAllText(path, System.Text.Encoding.UTF8);
        }
        catch { return "{}"; }
    }

    public string GetEmulatorLogDirPath()
    {
        if (!string.IsNullOrWhiteSpace(_settings.UserDir))
        {
            var path = Path.Combine(_settings.UserDir.Trim(), "log");
            return path;
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "shadPS4", "log");
    }

    public string GetGpuDevicesJson()
    {
        var list = new List<object>
        {
            new { id = -1, name = "Auto" }
        };
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gpuIndex = 0;
            for (uint devNum = 0; ; devNum++)
            {
                var adapter = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(null, devNum, ref adapter, 0))
                    break;

                var isMirroring = (adapter.StateFlags & DisplayDeviceMirroringDriver) != 0;
                if (isMirroring)
                    continue;

                var name = (adapter.DeviceString ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (seen.Add(name))
                {
                    list.Add(new { id = gpuIndex, name = $"{gpuIndex}: {name}" });
                    gpuIndex++;
                }
            }
        }
        catch
        {
            // keep fallback auto option
        }
        return JsonSerializer.Serialize(list, JsonOptions);
    }

    private static string ToFileUrl(string fullPath)
    {
        var safe = (fullPath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        return "file:///" + safe.Replace(" ", "%20");
    }

    private static string ResolveThemeAssetUrl(string themeDir, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var raw = value.Trim();
        if (Uri.TryCreate(raw, UriKind.Absolute, out var abs) &&
            (abs.Scheme == Uri.UriSchemeFile || abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            return abs.ToString();
        try
        {
            var fullPath = Path.IsPathRooted(raw)
                ? raw
                : Path.GetFullPath(Path.Combine(themeDir, raw));
            return ToFileUrl(fullPath);
        }
        catch
        {
            return "";
        }
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }
        return null;
    }

    private static Dictionary<string, string> GetMap(JsonElement root, string propertyName, string themeDir, bool resolvePaths)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var item in prop.EnumerateObject())
        {
            var key = item.Name?.Trim();
            if (string.IsNullOrWhiteSpace(key))
                continue;
            string value = item.Value.ValueKind switch
            {
                JsonValueKind.String => item.Value.GetString() ?? "",
                JsonValueKind.Number => item.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => ""
            };
            if (string.IsNullOrWhiteSpace(value))
                continue;
            map[key] = resolvePaths ? ResolveThemeAssetUrl(themeDir, value) : value;
        }

        return map;
    }

    private static double? GetNumber(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (!root.TryGetProperty(n, out var p))
                continue;

            if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var dNum))
                return dNum;

            if (p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                if (!string.IsNullOrWhiteSpace(s) &&
                    double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dStr))
                    return dStr;
            }
        }

        return null;
    }

    private static bool? GetBool(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (!root.TryGetProperty(n, out var p))
                continue;

            if (p.ValueKind == JsonValueKind.True)
                return true;
            if (p.ValueKind == JsonValueKind.False)
                return false;
            if (p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(s))
                    continue;
                if (bool.TryParse(s, out var b))
                    return b;
                if (s == "1") return true;
                if (s == "0") return false;
            }
        }

        return null;
    }

    public string GetThemesJson()
    {
        var list = new List<object>();
        try
        {
            var themesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
            if (Directory.Exists(themesRoot))
            {
                foreach (var dir in Directory.GetDirectories(themesRoot).OrderBy(Path.GetFileName))
                {
                    var manifestPath = Path.Combine(dir, "theme.json");
                    if (!File.Exists(manifestPath))
                        continue;

                    try
                    {
                        var text = File.ReadAllText(manifestPath, Encoding.UTF8);
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;

                        var folderName = Path.GetFileName(dir) ?? "theme";
                        var id = GetString(root, "id") ?? folderName;
                        var name = GetString(root, "name", "title") ?? id;
                        var description = GetString(root, "description", "desc") ?? "";
                        var author = GetString(root, "author") ?? "";
                        var version = GetString(root, "version") ?? "";
                        var previewValue = GetString(root, "preview", "previewImage", "cover", "thumbnail");
                        if (string.IsNullOrWhiteSpace(previewValue))
                        {
                            var pPng = Path.Combine(dir, "preview.png");
                            if (File.Exists(pPng)) previewValue = "preview.png";
                            else
                            {
                                var pJpg = Path.Combine(dir, "preview.jpg");
                                if (File.Exists(pJpg)) previewValue = "preview.jpg";
                            }
                        }
                        var previewUrl = string.IsNullOrWhiteSpace(previewValue) ? "" : ResolveThemeAssetUrl(dir, previewValue);

                        var cssCustomUrl = "";
                        var hasCustomCss = false;
                        if (root.TryGetProperty("css_custom", out var cssProp))
                        {
                            if (cssProp.ValueKind == JsonValueKind.True)
                            {
                                hasCustomCss = true;
                                cssCustomUrl = ResolveThemeAssetUrl(dir, "custom.css");
                            }
                            else if (cssProp.ValueKind == JsonValueKind.String)
                            {
                                var cssVal = cssProp.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(cssVal))
                                {
                                    hasCustomCss = true;
                                    cssCustomUrl = ResolveThemeAssetUrl(dir, cssVal);
                                }
                            }
                        }

                        var palette = GetMap(root, "palette", dir, resolvePaths: false);
                        var styles = GetMap(root, "styles", dir, resolvePaths: false);
                        var sounds = GetMap(root, "sounds", dir, resolvePaths: true);
                        bool? customBackgroundEnabledValue = null;
                        var customBackgroundUrl = "";
                        if (root.TryGetProperty("custom_background", out var customBgProp))
                        {
                            if (customBgProp.ValueKind == JsonValueKind.True)
                            {
                                customBackgroundEnabledValue = true;
                            }
                            else if (customBgProp.ValueKind == JsonValueKind.False)
                            {
                                customBackgroundEnabledValue = false;
                            }
                            else if (customBgProp.ValueKind == JsonValueKind.String)
                            {
                                var bgVal = customBgProp.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(bgVal))
                                {
                                    customBackgroundEnabledValue = true;
                                    customBackgroundUrl = ResolveThemeAssetUrl(dir, bgVal);
                                }
                            }
                            else if (customBgProp.ValueKind == JsonValueKind.Object)
                            {
                                var enabled = GetBool(customBgProp, "enabled", "use", "on");
                                customBackgroundEnabledValue = enabled ?? true;
                                var bgVal = GetString(customBgProp, "path", "url", "image", "background", "value");
                                if (!string.IsNullOrWhiteSpace(bgVal))
                                    customBackgroundUrl = ResolveThemeAssetUrl(dir, bgVal);
                            }
                        }
                        if (string.IsNullOrWhiteSpace(customBackgroundUrl))
                        {
                            var fallbackBg = GetString(root, "background", "backgroundImage", "background_image");
                            if (!string.IsNullOrWhiteSpace(fallbackBg))
                                customBackgroundUrl = ResolveThemeAssetUrl(dir, fallbackBg);
                        }
                        var customBackgroundEnabled = customBackgroundEnabledValue ?? !string.IsNullOrWhiteSpace(customBackgroundUrl);

                        var musicRaw = GetString(root, "music", "musicUrl", "bgMusic", "backgroundMusic");
                        var musicUrl = string.IsNullOrWhiteSpace(musicRaw) ? "" : ResolveThemeAssetUrl(dir, musicRaw);
                        if (string.IsNullOrWhiteSpace(musicUrl) &&
                            sounds.TryGetValue("music", out var soundsMusic) &&
                            !string.IsNullOrWhiteSpace(soundsMusic))
                            musicUrl = soundsMusic;

                        var musicVolume = GetNumber(root, "musicVolume", "music_volume", "bgMusicVolume", "bg_music_volume") ?? 0.35;
                        if (musicVolume > 1.0 && musicVolume <= 100.0)
                            musicVolume /= 100.0;
                        musicVolume = Math.Clamp(musicVolume, 0.0, 1.0);

                        var transitionMs = GetNumber(root, "transitionMs", "themeTransitionMs", "theme_transition_ms") ?? 420;
                        transitionMs = Math.Clamp(transitionMs, 0, 5000);

                        list.Add(new
                        {
                            id,
                            folderName,
                            name,
                            description,
                            author,
                            version,
                            previewUrl,
                            cssCustomUrl,
                            hasCustomCss,
                            palette,
                            styles,
                            sounds,
                            customBackgroundEnabled,
                            customBackgroundUrl,
                            musicUrl,
                            musicVolume,
                            transitionMs
                        });
                    }
                    catch
                    {
                        // ignore malformed theme and continue
                    }
                }
            }
        }
        catch
        {
            // ignore and return collected themes
        }

        return JsonSerializer.Serialize(list, JsonOptions);
    }

    public string GetLauncherVersion()
    {
        try
        {
            var infoVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVersion))
            {
                var clean = infoVersion.Trim();
                var plus = clean.IndexOf('+');
                if (plus >= 0) clean = clean[..plus];
                return clean;
            }

            var asmVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (asmVersion != null)
            {
                if (asmVersion.Build >= 0)
                    return $"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}";
                return $"{asmVersion.Major}.{asmVersion.Minor}";
            }
        }
        catch
        {
            // fall through
        }

        return "0.0.0";
    }

    public string GetStoreCatalogJson()
    {
        var baseUrl = GetStoreServerBaseUrl();
        var endpoint = CombineUrl(baseUrl, "/api/catalog");

        try
        {
            var raw = Http.GetStringAsync(endpoint).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(raw))
                return JsonSerializer.Serialize(new { success = false, items = Array.Empty<object>(), error = "Empty catalog response." }, JsonOptions);

            // Pass-through so server can evolve schema without launcher rebuild.
            return raw;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                items = Array.Empty<object>(),
                error = ex.Message,
                server = baseUrl
            }, JsonOptions);
        }
    }

    public string InstallStorePackage(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
            return JsonSerializer.Serialize(new { success = false, message = "Empty install request." }, JsonOptions);

        StoreInstallRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<StoreInstallRequest>(requestJson, JsonReadOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, message = "Invalid install request: " + ex.Message }, JsonOptions);
        }

        if (request == null)
            return JsonSerializer.Serialize(new { success = false, message = "Install request is null." }, JsonOptions);

        var kind = (request.Type ?? "file").Trim().ToLowerInvariant();
        if (kind is "launcher" or "launcher_update" or "update")
            return InstallLauncherUpdate(requestJson);

        var rawUrl = request.DownloadUrl?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(rawUrl))
            return JsonSerializer.Serialize(new { success = false, message = "downloadUrl is missing." }, JsonOptions);

        var downloadUrl = CombineUrl(GetStoreServerBaseUrl(), rawUrl);

        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShadPS4Launcher");
        var cacheDir = Path.Combine(localAppData, "store-cache");
        Directory.CreateDirectory(cacheDir);

        var requestedName = (request.FileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            try
            {
                var uri = new Uri(downloadUrl);
                requestedName = Path.GetFileName(uri.LocalPath);
            }
            catch
            {
                requestedName = "";
            }
        }
        if (string.IsNullOrWhiteSpace(requestedName))
            requestedName = (request.Id?.Trim() ?? "package") + ".zip";

        foreach (var c in Path.GetInvalidFileNameChars())
            requestedName = requestedName.Replace(c, '_');

        var cacheFile = Path.Combine(cacheDir, requestedName);
        var downloadError = DownloadFileToPath(downloadUrl, cacheFile);
        if (!string.IsNullOrWhiteSpace(downloadError))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                message = "Download failed: " + downloadError,
                url = downloadUrl
            }, JsonOptions);
        }

        var appBase = AppDomain.CurrentDomain.BaseDirectory;
        var targetRoot = kind switch
        {
            "theme" => Path.Combine(appBase, "Themes"),
            "sound" or "sounds" or "soundpack" => Path.Combine(appBase, "Themes"),
            "emulator" => ResolveEmulatorInstallDir(),
            _ => Path.Combine(appBase, "Downloads")
        };

        var relativeSubdir = NormalizeRelativeSubdir(request.TargetSubdir);
        if (!string.IsNullOrWhiteSpace(relativeSubdir))
            targetRoot = Path.Combine(targetRoot, relativeSubdir);

        Directory.CreateDirectory(targetRoot);

        var shouldExtract = request.Extract && Path.GetExtension(cacheFile).Equals(".zip", StringComparison.OrdinalIgnoreCase);
        var installedPath = targetRoot;
        try
        {
            if (shouldExtract)
            {
                ExtractZipSafe(cacheFile, targetRoot);
            }
            else
            {
                installedPath = Path.Combine(targetRoot, requestedName);
                File.Copy(cacheFile, installedPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                message = "Install failed: " + ex.Message,
                path = targetRoot
            }, JsonOptions);
        }

        if (kind == "theme")
            ShowMessage("Theme package installed.", "info");

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = "Installed.",
            kind,
            id = request.Id,
            name = request.Name,
            version = request.Version,
            installedPath
        }, JsonOptions);
    }

    public string CheckLauncherUpdateJson()
    {
        var currentVersion = GetLauncherVersion();
        var baseUrl = GetStoreServerBaseUrl();
        var endpoint = CombineUrl(baseUrl, "/api/launcher/version");

        try
        {
            var raw = Http.GetStringAsync(endpoint).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Update endpoint must return an object.");

            var latest = GetString(root, "version", "latestVersion", "tag") ?? "";
            var download = GetString(root, "downloadUrl", "url", "download") ?? "";
            var notes = GetString(root, "notes", "changelog", "description") ?? "";
            if (!string.IsNullOrWhiteSpace(download))
                download = CombineUrl(baseUrl, download);

            var updateAvailable = IsRemoteVersionNewer(currentVersion, latest);
            return JsonSerializer.Serialize(new
            {
                success = true,
                currentVersion,
                latestVersion = latest,
                updateAvailable,
                downloadUrl = download,
                notes,
                server = baseUrl
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                currentVersion,
                latestVersion = "",
                updateAvailable = false,
                downloadUrl = "",
                notes = "",
                error = ex.Message,
                server = baseUrl
            }, JsonOptions);
        }
    }

    public string InstallLauncherUpdate(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
            return JsonSerializer.Serialize(new { success = false, message = "Empty update request." }, JsonOptions);

        LauncherVersionInfo request;
        try
        {
            request = JsonSerializer.Deserialize<LauncherVersionInfo>(requestJson, JsonReadOptions) ?? new LauncherVersionInfo();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, message = "Invalid update request: " + ex.Message }, JsonOptions);
        }

        if (string.IsNullOrWhiteSpace(request.DownloadUrl))
            return JsonSerializer.Serialize(new { success = false, message = "downloadUrl is missing." }, JsonOptions);

        var baseUrl = GetStoreServerBaseUrl();
        var downloadUrl = CombineUrl(baseUrl, request.DownloadUrl.Trim());
        var incomingVersion = string.IsNullOrWhiteSpace(request.Version) ? request.LatestVersion : request.Version;
        var version = string.IsNullOrWhiteSpace(incomingVersion) ? "latest" : incomingVersion.Trim();
        var versionSafe = ToSafeFileFragment(version);

        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShadPS4Launcher");
        var updatesDir = Path.Combine(localAppData, "updates");
        var stagingDir = Path.Combine(updatesDir, "pending", versionSafe);
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(updatesDir);

        var zipPath = Path.Combine(updatesDir, $"launcher-{versionSafe}.zip");
        var downloadError = DownloadFileToPath(downloadUrl, zipPath);
        if (!string.IsNullOrWhiteSpace(downloadError))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                message = "Update download failed: " + downloadError,
                url = downloadUrl
            }, JsonOptions);
        }

        try
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);
            ExtractZipSafe(zipPath, stagingDir);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                message = "Update extraction failed: " + ex.Message
            }, JsonOptions);
        }

        var pid = Process.GetCurrentProcess().Id;
        var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(appDir, "ShadPS4 Launcher by Hittcliff.exe");

        var scriptPath = Path.Combine(updatesDir, $"apply-update-{DateTime.UtcNow:yyyyMMddHHmmss}.ps1");
        try
        {
            var script = BuildUpdateScript(pid, stagingDir, appDir, currentExe);
            File.WriteAllText(scriptPath, script, Encoding.UTF8);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = updatesDir
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                message = "Could not start update installer: " + ex.Message
            }, JsonOptions);
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try { Close(); } catch { Application.Current.Shutdown(); }
        });

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = "Update installer started.",
            version,
            stagingDir
        }, JsonOptions);
    }

    public string GetThemeCustomCssPath(string themeId)
    {
        try
        {
            var dir = FindThemeDirectoryById(themeId);
            if (string.IsNullOrWhiteSpace(dir))
                return "";

            var cssPath = Path.Combine(dir, "custom.css");
            if (!File.Exists(cssPath))
                File.WriteAllText(cssPath, "/* Theme custom CSS */\n", Encoding.UTF8);
            return cssPath;
        }
        catch
        {
            return "";
        }
    }

    private static string DownloadFileToPath(string url, string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(file);
            return "";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void ExtractZipSafe(string zipPath, string destinationDir)
    {
        var fullDestination = Path.GetFullPath(destinationDir);
        if (!Directory.Exists(fullDestination))
            Directory.CreateDirectory(fullDestination);

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
                continue;

            var destPath = Path.GetFullPath(Path.Combine(fullDestination, entry.FullName));
            var inRoot = destPath.StartsWith(fullDestination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(destPath, fullDestination, StringComparison.OrdinalIgnoreCase);
            if (!inRoot)
                continue;

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destPath);
                continue;
            }

            var parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private string GetStoreServerBaseUrl()
    {
        var raw = _settings?.StoreServerUrl;
        if (string.IsNullOrWhiteSpace(raw))
            raw = DefaultStoreServerUrl;
        raw = raw.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            raw = DefaultStoreServerUrl;
        return raw.TrimEnd('/');
    }

    private static string CombineUrl(string baseUrl, string maybeRelativeOrAbsolute)
    {
        var raw = maybeRelativeOrAbsolute?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(raw))
            return baseUrl;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return raw;

        return new Uri(baseUri, raw).ToString();
    }

    private static string NormalizeRelativeSubdir(string? subdir)
    {
        if (string.IsNullOrWhiteSpace(subdir))
            return "";

        var parts = subdir
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..")
            .Select(p =>
            {
                var v = p.Trim();
                foreach (var c in Path.GetInvalidFileNameChars())
                    v = v.Replace(c, '_');
                return v;
            })
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();

        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    private string ResolveEmulatorInstallDir()
    {
        if (!string.IsNullOrWhiteSpace(_settings.EmulatorInstallDir))
            return _settings.EmulatorInstallDir.Trim();
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "emulators");
    }

    private static string ToSafeFileFragment(string value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "latest" : value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            v = v.Replace(c, '_');
        v = v.Replace(' ', '_');
        return string.IsNullOrWhiteSpace(v) ? "latest" : v;
    }

    private static bool IsRemoteVersionNewer(string currentVersion, string remoteVersion)
    {
        var current = ParseLooseVersion(currentVersion);
        var remote = ParseLooseVersion(remoteVersion);
        return remote > current;
    }

    private static Version ParseLooseVersion(string? value)
    {
        var raw = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return new Version(0, 0, 0, 0);

        if (raw.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            raw = raw[1..];

        var dash = raw.IndexOfAny(new[] { '-', '+', ' ' });
        if (dash > 0)
            raw = raw[..dash];

        if (Version.TryParse(raw, out var v))
            return v;

        var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .Take(4)
            .ToList();
        while (parts.Count < 4)
            parts.Add(0);
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    private static string BuildUpdateScript(int pid, string stagingDir, string appDir, string exePath)
    {
        static string Ps(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

        return string.Join("\n", new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"$pidToWait = {pid}",
            $"$sourceDir = {Ps(stagingDir)}",
            $"$targetDir = {Ps(appDir)}",
            $"$exePath = {Ps(exePath)}",
            "for ($i = 0; $i -lt 80; $i++) {",
            "    if (-not (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue)) { break }",
            "    Start-Sleep -Milliseconds 500",
            "}",
            "Start-Sleep -Milliseconds 600",
            "Copy-Item -Path (Join-Path $sourceDir '*') -Destination $targetDir -Recurse -Force",
            "Start-Process -FilePath $exePath",
            "exit 0"
        });
    }

    private string? FindThemeDirectoryById(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
            return null;

        var wanted = themeId.Trim();
        var themesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
        if (!Directory.Exists(themesRoot))
            return null;

        foreach (var dir in Directory.GetDirectories(themesRoot))
        {
            var folderName = Path.GetFileName(dir) ?? "";
            if (folderName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                return dir;

            var manifestPath = Path.Combine(dir, "theme.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                var id = GetString(doc.RootElement, "id");
                if (!string.IsNullOrWhiteSpace(id) && id.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
            catch
            {
                // ignore malformed theme
            }
        }

        return null;
    }
}