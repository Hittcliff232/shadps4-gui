using System.IO;
using System.Windows;
using Microsoft.Win32;
using ShadPS4Launcher.Models;
using ShadPS4Launcher.Services;

namespace ShadPS4Launcher;

public partial class GeneralSettingsWindow
{
    private readonly LauncherSettings _settings;
    private readonly SettingsService _settingsService;

    public GeneralSettingsWindow(LauncherSettings settings, SettingsService settingsService)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        TbEmulatorPath.Text = _settings.EmulatorPath;
        TbUserDir.Text = _settings.UserDir;
        TbAddonDir.Text = _settings.AddonDir;
        TbPatchFile.Text = _settings.PatchFile;
        TbOverrideRoot.Text = _settings.OverrideRoot;
        TbWaitForPid.Text = _settings.WaitForPid?.ToString() ?? "";
        TbCustomLaunchArgs.Text = _settings.CustomLaunchArgs;
        CbFullscreen.IsChecked = _settings.Fullscreen;
        CbShowFps.IsChecked = _settings.ShowFps;
        CbIgnoreGamePatch.IsChecked = _settings.IgnoreGamePatch;
        CbWaitForDebugger.IsChecked = _settings.WaitForDebugger;
        CbConfigClean.IsChecked = _settings.ConfigClean;
        CbConfigGlobal.IsChecked = _settings.ConfigGlobal;
        CbLogAppend.IsChecked = _settings.LogAppend;
    }

    private void SaveToSettings()
    {
        _settings.EmulatorPath = TbEmulatorPath.Text?.Trim() ?? "";
        _settings.UserDir = TbUserDir.Text?.Trim() ?? "";
        _settings.AddonDir = TbAddonDir.Text?.Trim() ?? "";
        _settings.PatchFile = TbPatchFile.Text?.Trim() ?? "";
        _settings.OverrideRoot = TbOverrideRoot.Text?.Trim() ?? "";
        _settings.WaitForPid = int.TryParse(TbWaitForPid.Text?.Trim(), out var waitPid) && waitPid > 0 ? waitPid : null;
        _settings.CustomLaunchArgs = TbCustomLaunchArgs.Text?.Trim() ?? "";
        _settings.Fullscreen = CbFullscreen.IsChecked == true;
        _settings.ShowFps = CbShowFps.IsChecked == true;
        _settings.IgnoreGamePatch = CbIgnoreGamePatch.IsChecked == true;
        _settings.WaitForDebugger = CbWaitForDebugger.IsChecked == true;
        _settings.ConfigClean = CbConfigClean.IsChecked == true;
        _settings.ConfigGlobal = CbConfigGlobal.IsChecked == true;
        _settings.LogAppend = CbLogAppend.IsChecked == true;
    }

    private void BtnSave_OnClick(object sender, RoutedEventArgs e)
    {
        SaveToSettings();
        _settingsService.Save(_settings);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_OnClick(object sender, RoutedEventArgs e) => Close();

    private void BtnBrowseEmulator_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select shadps4.exe", Filter = "Executable|*.exe|All|*.*" };
        if (dlg.ShowDialog() == true) TbEmulatorPath.Text = dlg.FileName;
    }

    private void BtnBrowseUserDir_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select ShadPS4 user data folder" };
        if (dlg.ShowDialog() == true) TbUserDir.Text = dlg.FolderName;
    }

    private void BtnBrowseAddonDir_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select addon folder" };
        if (dlg.ShowDialog() == true) TbAddonDir.Text = dlg.FolderName;
    }

    private void BtnBrowsePatch_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select patch file", Filter = "All|*.*" };
        if (dlg.ShowDialog() == true) TbPatchFile.Text = dlg.FileName;
    }
}
