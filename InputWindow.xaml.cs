using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using ShadPS4Launcher.Models;
using ShadPS4Launcher.Services;

namespace ShadPS4Launcher;

public partial class InputWindow
{
    private readonly LauncherSettings _settings;
    private readonly ShadPS4ConfigService _configService;

    public InputWindow(LauncherSettings settings, ShadPS4ConfigService configService)
    {
        InitializeComponent();
        _settings = settings;
        _configService = configService;
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        var userDir = string.IsNullOrWhiteSpace(_settings.UserDir) ? null : _settings.UserDir;
        var model = _configService.Load(userDir);
        var snap = _configService.GetSnapshot(model ?? _configService.GetOrCreateModel(userDir));
        CbMotionControls.IsChecked = snap.MotionControls;
        CbBackgroundController.IsChecked = snap.BackgroundControllerInput;
    }

    private void BtnSave_OnClick(object sender, RoutedEventArgs e)
    {
        var userDir = string.IsNullOrWhiteSpace(_settings.UserDir) ? null : _settings.UserDir;
        var model = _configService.GetOrCreateModel(userDir);
        var snap = _configService.GetSnapshot(model);
        snap.MotionControls = CbMotionControls.IsChecked == true;
        snap.BackgroundControllerInput = CbBackgroundController.IsChecked == true;
        snap.GameInstallDirs = _settings.GameInstallDirs.ToList();
        snap.AddonDir = _settings.AddonDir;
        _configService.UpdateFromLauncher(model, snap);
        _configService.Save(userDir, model);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_OnClick(object sender, RoutedEventArgs e) => Close();

    private void BtnOpenInputConfig_OnClick(object sender, RoutedEventArgs e)
    {
        var userDir = !string.IsNullOrWhiteSpace(_settings.UserDir) ? _settings.UserDir : _configService.GetDefaultUserDir();
        var inputDir = Path.Combine(userDir, "input_config");
        if (!Directory.Exists(inputDir)) Directory.CreateDirectory(inputDir);
        try { Process.Start("explorer.exe", $"\"{inputDir}\""); } catch { }
    }
}
