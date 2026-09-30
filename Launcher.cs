using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReforgerHub;

/// <summary>Starts Workbench on a project, and makes Windows shortcuts to the Hub or straight to a project.</summary>
public static class Launcher
{
    public static Tracking OpenWorkbench(Store store, Project project)
    {
        var exe = store.Settings.WorkbenchExe;
        if (!File.Exists(exe))
            throw new Exception("Workbench not found. Set its path in Settings.");

        if (string.IsNullOrEmpty(project.GprojPath))
            throw new Exception("This project has no addon.gproj");

        // Workbench only finds the base game (and mods the project needs) in the addon folders it is given
        var settings = store.Settings;
        var addonDirs = new[]
            {
                Path.Combine(settings.GameDir, "addons"),
                Path.Combine(Path.GetDirectoryName(exe)!, "addons"),
                settings.LocalAddonsDir,
                settings.WorkshopAddonsDir,
            }
            .Concat(settings.ExtraDirs)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var arguments = $"-gproj \"{project.GprojPath}\" -addonsDir \"{string.Join(",", addonDirs)}\"";
        Process.Start(new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });

        var tracking = store.Track(project.Guid);
        tracking.LastOpened = DateTime.Now;
        tracking.OpenCount++;
        store.SaveTracking();
        return tracking;
    }

    /// <summary>Picks the project to open for a GUID: your own copy before a downloaded one</summary>
    public static Project? Find(IEnumerable<Project> projects, string guidOrName)
    {
        var list = projects.ToList();
        return list.FirstOrDefault(p => p.Source == "local" && p.Guid.Equals(guidOrName, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(p => p.Guid.Equals(guidOrName, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(p => p.Source == "local" && (p.Title.Equals(guidOrName, StringComparison.OrdinalIgnoreCase) || p.Id.Equals(guidOrName, StringComparison.OrdinalIgnoreCase)));
    }

    // ---------------------------------------------------------------------------------------------
    // Shortcuts (.lnk through the Windows Script Host shell object)
    // ---------------------------------------------------------------------------------------------
    public static string AppExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ReforgerHub.exe");
    public static string AppIcon => Path.Combine(AppContext.BaseDirectory, "wwwroot", "icon.ico");

    /// <summary>Desktop and Start menu shortcuts to the Hub</summary>
    public static void CreateAppShortcuts()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        CreateShortcut(Path.Combine(desktop, "Reforger Hub.lnk"), AppExe, "", AppIcon, "Arma Reforger project hub");
        CreateShortcut(Path.Combine(startMenu, "Reforger Hub.lnk"), AppExe, "", AppIcon, "Arma Reforger project hub");
    }

    /// <summary>A desktop shortcut that opens one project straight in Workbench</summary>
    public static string CreateProjectShortcut(Project project)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var name = string.Concat(project.Title.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(desktop, $"{name} (Workbench).lnk");
        CreateShortcut(path, AppExe, $"--open {project.Guid}", AppIcon, $"Open {project.Title} in Arma Reforger Workbench");
        return path;
    }

    private static void CreateShortcut(string path, string target, string arguments, string icon, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new Exception("Windows Script Host is not available");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Arguments = arguments;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = description;
            if (File.Exists(icon))
                link.IconLocation = icon + ",0";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
