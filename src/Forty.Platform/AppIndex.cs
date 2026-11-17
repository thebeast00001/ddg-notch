using System.Diagnostics;
using Forty.Core;
using Windows.Management.Deployment;

namespace Forty.Platform;

/// <summary>
/// The apps you can open (F39): every shortcut in the Start menu's program folders (yours and the
/// machine's) and every installed app package that lists itself in Start. Read-only: nothing is
/// changed, and names stay on this PC. Opening one goes the way the Start menu goes: a shortcut
/// through the shell, a package by its app id through the Apps folder.
/// </summary>
public static class AppIndex
{
    public static IReadOnlyList<InstalledApp> Load()
    {
        var apps = new List<InstalledApp>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
        {
            string programs = Path.Combine(root, "Programs");
            if (!Directory.Exists(programs)) continue;
            foreach (var pattern in new[] { "*.lnk", "*.url" })   // games and some apps add web shortcuts (steam://…)
                foreach (var file in Directory.EnumerateFiles(programs, pattern, options))
                    apps.Add(new InstalledApp(Path.GetFileNameWithoutExtension(file), file, AppKind.Shortcut));
        }
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main))
            {
                if (package.IsFramework || package.IsResourcePackage) continue;
                IReadOnlyList<Windows.ApplicationModel.Core.AppListEntry> entries;
                try { entries = package.GetAppListEntries(); }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException) { continue; }
                foreach (var entry in entries)
                {
                    string name = entry.DisplayInfo?.DisplayName ?? "";
                    if (name.Length > 0 && !name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        apps.Add(new InstalledApp(name, entry.AppUserModelId, AppKind.Packaged));
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Packages couldn't be listed; the Start menu's shortcuts still are.
        }
        return apps;
    }

    /// <summary>Opens an app the way the Start menu does. False if Windows refused.</summary>
    public static bool Launch(InstalledApp app)
    {
        try
        {
            var start = app.Kind == AppKind.Packaged
                ? new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { @"shell:AppsFolder\" + app.Launch } }
                : new ProcessStartInfo(app.Launch) { UseShellExecute = true };
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }
}
