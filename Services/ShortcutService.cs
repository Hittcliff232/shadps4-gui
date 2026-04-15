using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.InteropServices.ComTypes;
using ShadPS4Launcher.Models;

namespace ShadPS4Launcher.Services;

/// <summary>
/// Creates .lnk shortcuts on Windows (e.g. desktop) for a game + launcher settings.
/// </summary>
public sealed class ShortcutService
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    private static readonly Type ShellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!;

    public string DesktopPath =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>
    /// Create a desktop shortcut that runs the emulator with the given game and display name.
    /// </summary>
    public bool CreateDesktopShortcut(LauncherSettings settings, string gamePathOrId, string displayName, LaunchService launchService)
    {
        if (string.IsNullOrEmpty(settings.EmulatorPath) || !File.Exists(settings.EmulatorPath))
            return false;

        var args = launchService.BuildArguments(settings, gamePathOrId);
        var workingDir = Path.GetDirectoryName(settings.EmulatorPath) ?? "";
        var shortcutPath = Path.Combine(DesktopPath, SanitizeFileName(displayName) + ".lnk");
        return CreateShortcut(shortcutPath, settings.EmulatorPath, args, workingDir, displayName);
    }

    public bool CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDir, string? description = null)
    {
        try
        {
            var link = (IShellLinkW)Activator.CreateInstance(ShellLinkType)!;
            link.SetPath(targetPath);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(workingDir);
            if (!string.IsNullOrEmpty(description))
                link.SetDescription(description);

            var persist = (IPersistFile)link;
            persist.Save(shortcutPath, false);
            Marshal.ReleaseComObject(link);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "ShadPS4 Game" : name;
    }
}
