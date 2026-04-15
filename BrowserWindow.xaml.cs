using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace ShadPS4Launcher;

public partial class BrowserWindow
{
    private const string DefaultHomeUrl = "https://duckduckgo.com";
    private bool _isReady;
    private string _pendingUrl = DefaultHomeUrl;

    public string CurrentUrl { get; private set; } = "about:blank";

    public BrowserWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        BrowserView.NavigationStarting += BrowserView_OnNavigationStarting;
        BrowserView.NavigationCompleted += BrowserView_OnNavigationCompleted;
        BrowserView.PreviewKeyDown += BrowserView_OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isReady) return;

        try
        {
            await BrowserView.EnsureCoreWebView2Async(null);
            _isReady = true;
            NavigateTo(_pendingUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Browser initialization failed: " + ex.Message, "ShadPS4 Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string NormalizeUrl(string? raw)
    {
        var v = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(v))
            return DefaultHomeUrl;

        if (v.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return v;

        if (Uri.TryCreate(v, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        return "https://" + v;
    }

    public void NavigateTo(string? raw)
    {
        var target = NormalizeUrl(raw);
        _pendingUrl = target;
        TbUrl.Text = target;

        if (!_isReady || BrowserView.CoreWebView2 == null)
            return;

        try
        {
            BrowserView.CoreWebView2.Navigate(target);
        }
        catch
        {
            // ignore navigation failure
        }
    }

    public void GoBack()
    {
        try
        {
            if (BrowserView.CoreWebView2 != null && BrowserView.CoreWebView2.CanGoBack)
                BrowserView.CoreWebView2.GoBack();
        }
        catch
        {
            // ignore
        }
    }

    public void GoForward()
    {
        try
        {
            if (BrowserView.CoreWebView2 != null && BrowserView.CoreWebView2.CanGoForward)
                BrowserView.CoreWebView2.GoForward();
        }
        catch
        {
            // ignore
        }
    }

    public void ReloadPage()
    {
        try
        {
            BrowserView.CoreWebView2?.Reload();
        }
        catch
        {
            // ignore
        }
    }

    private void BrowserView_OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        LoadingBar.Visibility = Visibility.Visible;
    }

    private void BrowserView_OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingBar.Visibility = Visibility.Collapsed;
        var src = BrowserView.Source?.ToString() ?? _pendingUrl;
        CurrentUrl = src;
        TbUrl.Text = src;
    }

    private void BrowserView_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F6)
        {
            TbUrl.Focus();
            TbUrl.SelectAll();
            e.Handled = true;
        }
    }

    private void TbUrl_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        NavigateTo(TbUrl.Text);
        e.Handled = true;
    }

    private void BtnBack_OnClick(object sender, RoutedEventArgs e) => GoBack();

    private void BtnForward_OnClick(object sender, RoutedEventArgs e) => GoForward();

    private void BtnReload_OnClick(object sender, RoutedEventArgs e) => ReloadPage();

    private void BtnHome_OnClick(object sender, RoutedEventArgs e) => NavigateTo(DefaultHomeUrl);

    private void BtnGo_OnClick(object sender, RoutedEventArgs e) => NavigateTo(TbUrl.Text);

    private void BtnKeyboard_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "osk.exe",
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }

        TbUrl.Focus();
        TbUrl.SelectAll();
    }

    private void BtnClose_OnClick(object sender, RoutedEventArgs e) => Close();
}
