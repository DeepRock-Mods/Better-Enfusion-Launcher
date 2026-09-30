using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReforgerHub;

/// <summary>Finds addons (folders with an addon.gproj) and reads what they are and what they depend on.</summary>
public static class Scanner
{
    public const string BaseGameGuid = "58D0FB3206B6F859";

    private static readonly Regex IdRx = new("^\\s*ID\\s+\"([^\"]*)\"", RegexOptions.Multiline);
    private static readonly Regex GuidRx = new("^\\s*GUID\\s+\"([^\"]*)\"", RegexOptions.Multiline);
    private static readonly Regex TitleRx = new("^\\s*TITLE\\s+\"([^\"]*)\"", RegexOptions.Multiline);
    private static readonly Regex DepsRx = new("Dependencies\\s*\\{([^}]*)\\}", RegexOptions.Singleline);
    private static readonly Regex QuotedRx = new("\"([0-9A-Fa-f]{16})\"");

    public static List<Project> Scan(Settings settings)
    {
        var projects = new List<Project>();
        ScanRoot(settings.LocalAddonsDir, "local", projects);
        foreach (var extra in settings.ExtraDirs)
            ScanRoot(extra, "local", projects);
        ScanRoot(settings.WorkshopAddonsDir, "workshop", projects);

        // The base game, so dependencies on it resolve
        var game = new Project
        {
            Guid = BaseGameGuid, Id = "ArmaReforger", Title = "Arma Reforger", Source = "game",
            Dir = Path.Combine(settings.GameDir, "addons", "data"),
        };
        var gameGproj = Path.Combine(game.Dir, "ArmaReforger.gproj");
        if (File.Exists(gameGproj))
            game.GprojPath = gameGproj;
        projects.Add(game);
        return projects;
    }

    private static void ScanRoot(string root, string source, List<Project> into)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var gproj = Path.Combine(dir, "addon.gproj");
            if (!File.Exists(gproj))
                continue;

            try
            {
                var project = ReadProject(gproj, source);
                if (source == "workshop")
                    ReadServerData(project);
                into.Add(project);
            }
            catch
            {
                // A broken gproj is skipped rather than failing the scan
            }
        }
    }

    public static Project ReadProject(string gproj, string source)
    {
        var text = File.ReadAllText(gproj);
        var dir = Path.GetDirectoryName(gproj)!;
        var project = new Project
        {
            GprojPath = gproj,
            Dir = dir,
            Source = source,
            Id = Match(IdRx, text),
            Guid = Match(GuidRx, text).ToUpperInvariant(),
            Title = Match(TitleRx, text),
        };
        if (string.IsNullOrEmpty(project.Title))
            project.Title = string.IsNullOrEmpty(project.Id) ? Path.GetFileName(dir) : project.Id;

        var deps = DepsRx.Match(text);
        if (deps.Success)
        {
            foreach (Match m in QuotedRx.Matches(deps.Groups[1].Value))
                project.Dependencies.Add(m.Groups[1].Value.ToUpperInvariant());
        }
        return project;
    }

    private static string Match(Regex rx, string text)
    {
        var m = rx.Match(text);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static void ReadServerData(Project project)
    {
        var path = Path.Combine(project.Dir, "ServerData.json");
        var thumb = Path.Combine(project.Dir, "thumbnail.png");
        if (File.Exists(thumb))
            project.Thumbnail = thumb;
        if (!File.Exists(path))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } title)
                project.Title = title;
            if (root.TryGetProperty("revision", out var revision))
            {
                if (revision.TryGetProperty("version", out var v))
                    project.Version = v.GetString();
                if (revision.TryGetProperty("gameVersion", out var g))
                    project.GameVersion = g.GetString();
                if (revision.TryGetProperty("changelog", out var c))
                    project.Changelog = c.GetString();
            }
        }
        catch
        {
            // Metadata is optional
        }
    }

    /// <summary>Size, file counts and last change of a project folder (slow: run in the background).</summary>
    public static ProjectStats ComputeStats(Project project)
    {
        var stats = new ProjectStats();
        if (!Directory.Exists(project.Dir) || project.Source == "game")
            return stats;

        DateTime latest = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(project.Dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden }))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                continue;

            FileInfo info;
            try { info = new FileInfo(file); } catch { continue; }
            stats.Files++;
            stats.SizeBytes += info.Length;
            if (info.LastWriteTime > latest && !file.EndsWith(".rdb", StringComparison.OrdinalIgnoreCase))
                latest = info.LastWriteTime;

            switch (info.Extension.ToLowerInvariant())
            {
                case ".c": stats.Scripts++; break;
                case ".et": stats.Prefabs++; break;
                case ".layout": stats.Layouts++; break;
                case ".ent": stats.Worlds++; break;
            }
        }

        if (latest > DateTime.MinValue)
            stats.LastModified = latest;

        var head = Path.Combine(project.Dir, ".git", "HEAD");
        if (File.Exists(head))
        {
            var reference = File.ReadAllText(head).Trim();
            stats.GitBranch = reference.StartsWith("ref: refs/heads/") ? reference["ref: refs/heads/".Length..] : "detached";
        }
        return stats;
    }
}
