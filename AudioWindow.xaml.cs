using System.Linq;
using System.Windows;
using ShadPS4Launcher.Models;
using ShadPS4Launcher.Services;

namespace ShadPS4Launcher;

public partial class AudioWindow
{
    private readonly LauncherSettings _settings;
    private readonly ShadPS4ConfigService _configService;

    public AudioWindow(LauncherSettings settings, ShadPS4ConfigService configService)
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
        SliderVolume.Value = snap.Volume;
        TbExtraDmem.Text = snap.ExtraDmemMb.ToString();
    }

    private void BtnSave_OnClick(object sender, RoutedEventArgs e)
    {
        var userDir = string.IsNullOrWhiteSpace(_settings.UserDir) ? null : _settings.UserDir;
        var model = _configService.GetOrCreateModel(userDir);
        var snap = _configService.GetSnapshot(model);
        snap.Volume = (int)SliderVolume.Value;
        snap.ExtraDmemMb = int.TryParse(TbExtraDmem.Text, out var dmem) ? dmem : 0;
        snap.GameInstallDirs = _settings.GameInstallDirs.ToList();
        snap.AddonDir = _settings.AddonDir;
        _configService.UpdateFromLauncher(model, snap);
        _configService.Save(userDir, model);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_OnClick(object sender, RoutedEventArgs e) => Close();
}
