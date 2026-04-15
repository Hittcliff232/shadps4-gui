using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ShadPS4Launcher.Models;
using ShadPS4Launcher.Services;

namespace ShadPS4Launcher;

public partial class GraphicsWindow
{
    private readonly LauncherSettings _settings;
    private readonly ShadPS4ConfigService _configService;

    public GraphicsWindow(LauncherSettings settings, ShadPS4ConfigService configService)
    {
        InitializeComponent();
        _settings = settings;
        _configService = configService;
        InitCpuCombo();
        LoadFromConfig();
    }

    private void InitCpuCombo()
    {
        var items = new List<CpuComboItem> { new(-1, "Default (auto)") };
        for (var i = 0; i <= 15; i++)
            items.Add(new CpuComboItem(i, $"CPU {i}"));
        ComboCpu.ItemsSource = items;
        ComboCpu.DisplayMemberPath = "DisplayName";
        ComboCpu.SelectedValuePath = "Id";
    }

    private void LoadFromConfig()
    {
        var userDir = string.IsNullOrWhiteSpace(_settings.UserDir) ? null : _settings.UserDir;
        var model = _configService.Load(userDir);
        var snap = _configService.GetSnapshot(model ?? _configService.GetOrCreateModel(userDir));
        TbWindowWidth.Text = snap.WindowWidth.ToString();
        TbWindowHeight.Text = snap.WindowHeight.ToString();
        TbInternalWidth.Text = snap.InternalWidth.ToString();
        TbInternalHeight.Text = snap.InternalHeight.ToString();
        CbFsr.IsChecked = snap.FsrEnabled;
        CbRcas.IsChecked = snap.RcasEnabled;
        SliderRcas.Value = snap.RcasAttenuation;
        SelectCpuById(snap.GpuId);
    }

    private void SelectCpuById(int id)
    {
        foreach (var item in ComboCpu.Items.Cast<CpuComboItem>())
        {
            if (item.Id == id) { ComboCpu.SelectedItem = item; return; }
        }
        ComboCpu.SelectedIndex = 0;
    }

    private int GetSelectedCpuId()
    {
        if (ComboCpu.SelectedItem is CpuComboItem c) return c.Id;
        return -1;
    }

    private void BtnSave_OnClick(object sender, RoutedEventArgs e)
    {
        var userDir = string.IsNullOrWhiteSpace(_settings.UserDir) ? null : _settings.UserDir;
        var model = _configService.GetOrCreateModel(userDir);
        var snap = _configService.GetSnapshot(model);
        snap.WindowWidth = int.TryParse(TbWindowWidth.Text, out var w) ? w : 1280;
        snap.WindowHeight = int.TryParse(TbWindowHeight.Text, out var h) ? h : 720;
        snap.InternalWidth = int.TryParse(TbInternalWidth.Text, out var iw) ? iw : 1280;
        snap.InternalHeight = int.TryParse(TbInternalHeight.Text, out var ih) ? ih : 720;
        snap.FsrEnabled = CbFsr.IsChecked == true;
        snap.RcasEnabled = CbRcas.IsChecked == true;
        snap.RcasAttenuation = (int)SliderRcas.Value;
        snap.GpuId = GetSelectedCpuId();
        snap.GameInstallDirs = _settings.GameInstallDirs.ToList();
        snap.AddonDir = _settings.AddonDir;
        _settings.WindowWidth = snap.WindowWidth;
        _settings.WindowHeight = snap.WindowHeight;
        _configService.UpdateFromLauncher(model, snap);
        _configService.Save(userDir, model);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_OnClick(object sender, RoutedEventArgs e) => Close();
}
