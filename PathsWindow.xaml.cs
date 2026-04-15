using System.Linq;
using System.Windows;
using Microsoft.Win32;
using ShadPS4Launcher.Models;
using ShadPS4Launcher.Services;

namespace ShadPS4Launcher;

public partial class PathsWindow
{
    private readonly LauncherSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly ShadPS4ConfigService _configService;
    private readonly LaunchService _launchService;
    private readonly System.Action _onSaved;

    public PathsWindow(LauncherSettings settings, SettingsService settingsService,
        ShadPS4ConfigService configService, LaunchService launchService, System.Action onSaved)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;
        _configService = configService;
        _launchService = launchService;
        _onSaved = onSaved;
        RefreshList();
    }

    private void RefreshList()
    {
        ListPaths.ItemsSource = null;
        ListPaths.ItemsSource = _settings.GameInstallDirs.ToList();
    }

    private void BtnAdd_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select folder with PS4 games (eboot.bin / sce_sys)" };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FolderName)) return;
        var path = dlg.FolderName.Trim();
        if (!_settings.GameInstallDirs.Contains(path))
        {
            _settings.GameInstallDirs.Add(path);
            if (!string.IsNullOrEmpty(_settings.EmulatorPath) && System.IO.File.Exists(_settings.EmulatorPath))
                _launchService.AddGameFolder(_settings.EmulatorPath, path);
        }
        RefreshList();
    }

    private void BtnRemove_OnClick(object sender, RoutedEventArgs e)
    {
        if (ListPaths.SelectedItem is string path)
        {
            _settings.GameInstallDirs.Remove(path);
            RefreshList();
        }
    }

    private void BtnSave_OnClick(object sender, RoutedEventArgs e)
    {
        _settingsService.Save(_settings);
        var userDir = string.IsNullOrWhiteSpace(_settings.UserDir) ? null : _settings.UserDir;
        var model = _configService.GetOrCreateModel(userDir);
        var snap = _configService.GetSnapshot(model);
        snap.GameInstallDirs = _settings.GameInstallDirs.ToList();
        snap.AddonDir = _settings.AddonDir;
        _configService.UpdateFromLauncher(model, snap);
        _configService.Save(userDir, model);
        _onSaved();
        DialogResult = true;
        Close();
    }

    private void BtnClose_OnClick(object sender, RoutedEventArgs e) => Close();
}
