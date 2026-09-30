using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ReforgerHub;

/// <summary>Settings and tracking, saved as JSON in %AppData%\ReforgerHub.</summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReforgerHub");
    private readonly object _lock = new();

    public Settings Settings { get; set; } = new();
    public Dictionary<string, Tracking> Tracking { get; private set; } = new();

    private string SettingsPath => Path.Combine(_dir, "settings.json");
    private string TrackingPath => Path.Combine(_dir, "tracking.json");

    public void Load()
    {
        Directory.CreateDirectory(_dir);
        Settings = Read<Settings>(SettingsPath) ?? new Settings();
        Tracking = Read<Dictionary<string, Tracking>>(TrackingPath) ?? new();
        DetectPaths();
        SaveSettings();
    }

    public void SaveSettings()
    {
        lock (_lock)
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, Json));
    }

    public void SaveTracking()
    {
        lock (_lock)
            File.WriteAllText(TrackingPath, JsonSerializer.Serialize(Tracking, Json));
    }

    public Tracking Track(string guid)
    {
        if (!Tracking.TryGetValue(guid, out var tracking))
        {
            tracking = new Tracking();
            Tracking[guid] = tracking;
        }
        return tracking;
    }

    private static T? Read<T>(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default;
        }
        catch
        {
            return default;
        }
    }

    /// <summary>Finds Workbench and the game in the Steam libraries when the settings don't name them.</summary>
    private void DetectPaths()
    {
        if (File.Exists(Settings.WorkbenchExe) && Directory.Exists(Settings.GameDir))
            return;

        foreach (var library in SteamLibraries())
        {
            var common = Path.Combine(library, "steamapps", "common");
            if (!File.Exists(Settings.WorkbenchExe))
            {
                foreach (var exe in new[] { "ArmaReforgerWorkbenchSteamDiag.exe", "ArmaReforgerWorkbenchSteam.exe" })
                {
                    var candidate = Path.Combine(common, "Arma Reforger Tools", "Workbench", exe);
                    if (File.Exists(candidate))
                    {
                        Settings.WorkbenchExe = candidate;
                        break;
                    }
                }
            }

            var game = Path.Combine(common, "Arma Reforger");
            if (!Directory.Exists(Settings.GameDir) && Directory.Exists(game))
                Settings.GameDir = game;
        }
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? @"C:\Program Files (x86)\Steam";
        steam = steam.Replace('/', '\\');
        var found = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                found.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
