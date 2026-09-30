namespace ReforgerHub;

/// <summary>
/// Demo mode (<c>ReforgerHub.exe --demo shot.png</c>): made-up example projects and mods in a temporary folder, used for
/// the README screenshot so no real setup is shown. The window captures itself once the UI has rendered, then closes.
/// </summary>
public static class Demo
{
    private sealed record Addon(string Title, string Guid, string Status, string[] Tags, bool Favorite, int HoursAgo, string Notes, params string[] Deps);

    private sealed record Mod(string Title, string Guid, string Version, string GameVersion, int DaysAgo);

    private static readonly Addon[] Projects =
    {
        new("Frontline Logistics", "3F8A21C07B94E5D2", "progress", new[] { "gamemode", "ui" }, true, 1, "Supply trucks: finish the unload action", "58D0FB3206B6F859", "5B91F0E6C2A7D348"),
        new("Night Ops Pack", "9C4E7B12A5D8F063", "testing", new[] { "gear" }, false, 5, "NVG brightness pass", "58D0FB3206B6F859", "D4F82A6B19C07E53", "FFFFFFFFFFFF0042"),
        new("Medic Overhaul", "E27D5A9C31B04F86", "published", new[] { "medical", "gameplay" }, true, 30, "v1.4 live", "58D0FB3206B6F859"),
        new("Radio Chatter", "5B91F0E6C2A7D348", "progress", new[] { "audio" }, false, 52, "", "58D0FB3206B6F859"),
        new("Convoy Escort", "C6A03D8E4F1B9275", "idea", new[] { "gamemode" }, false, 140, "Escort missions along main roads", "58D0FB3206B6F859", "3F8A21C07B94E5D2"),
        new("Weather Tools", "71E4B9C5D0F2A836", "archived", new[] { "tools" }, false, 400, "", "58D0FB3206B6F859"),
    };

    private static readonly Mod[] Mods =
    {
        new("Community Weapons Pack", "D4F82A6B19C07E53", "2.3.1", "1.8.0.13", 3),
        new("Desert Terrain Kit", "2A6C94E1F7B3D508", "1.0.7", "1.6.0.119", 60),
        new("Vehicle Handling Plus", "8E1B3F7D52C9A640", "0.9.4", "1.8.0.10", 12),
    };

    /// <summary>A store whose settings point at freshly written example folders</summary>
    public static Store Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReforgerHubDemo");
        if (Directory.Exists(root))
            Directory.Delete(root, true);

        var local = Path.Combine(root, "addons");
        var workshop = Path.Combine(root, "downloaded");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);

        var store = new Store(Path.Combine(root, "profile"));
        store.Settings = new Settings
        {
            LocalAddonsDir = local, WorkshopAddonsDir = workshop, LogsDir = logs, CheckUpdatesOnStart = false,
        };

        foreach (var p in Projects)
        {
            var dir = Path.Combine(local, p.Title);
            Directory.CreateDirectory(Path.Combine(dir, "Scripts", "Game"));
            var id = new string(p.Title.Where(char.IsLetterOrDigit).ToArray());
            var deps = string.Join("\n", p.Deps.Select(d => $"  \"{d}\""));
            File.WriteAllText(Path.Combine(dir, "addon.gproj"),
                $"GameProject {{\n ID \"{id}\"\n GUID \"{p.Guid}\"\n TITLE \"{p.Title}\"\n Dependencies {{\n{deps}\n }}\n}}\n");
            File.WriteAllText(Path.Combine(dir, "Scripts", "Game", id + ".c"), "// example\n");
            Age(dir, TimeSpan.FromHours(p.HoursAgo));

            var tracking = store.Track(p.Guid);
            tracking.Status = p.Status;
            tracking.Tags = p.Tags.ToList();
            tracking.Favorite = p.Favorite;
            tracking.Notes = p.Notes;
            tracking.LastOpened = DateTime.Now - TimeSpan.FromHours(p.HoursAgo);
        }

        foreach (var m in Mods)
        {
            var dir = Path.Combine(workshop, m.Title.Replace(" ", "") + "_" + m.Guid);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "addon.gproj"),
                $"GameProject {{\n ID \"{m.Title.Replace(" ", "")}\"\n GUID \"{m.Guid}\"\n TITLE \"{m.Title}\"\n Dependencies {{\n  \"58D0FB3206B6F859\"\n }}\n}}\n");
            File.WriteAllText(Path.Combine(dir, "ServerData.json"),
                $"{{\"id\":\"{m.Guid}\",\"name\":\"{m.Title}\",\"revision\":{{\"version\":\"{m.Version}\",\"gameVersion\":\"{m.GameVersion}\",\"changelog\":\"\"}}}}");
            Age(dir, TimeSpan.FromDays(m.DaysAgo));
        }

        return store;
    }

    private static void Age(string dir, TimeSpan age)
    {
        var when = DateTime.Now - age;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetLastWriteTime(file, when);
    }
}
