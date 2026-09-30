namespace ReforgerHub;

/// <summary>An addon found on disk: one of your Workbench projects, a downloaded mod, or the base game.</summary>
public sealed class Project
{
    public string Guid { get; set; } = "";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Dir { get; set; } = "";
    public string GprojPath { get; set; } = "";
    /// <summary>"local", "workshop" or "game"</summary>
    public string Source { get; set; } = "local";
    public List<string> Dependencies { get; set; } = new();

    // Workshop metadata (ServerData.json)
    public string? Version { get; set; }
    public string? GameVersion { get; set; }
    public string? Changelog { get; set; }
    public string? Thumbnail { get; set; }

    // Filled in by the background stats pass
    public ProjectStats? Stats { get; set; }
}

public sealed class ProjectStats
{
    public long SizeBytes { get; set; }
    public int Files { get; set; }
    public int Scripts { get; set; }
    public int Prefabs { get; set; }
    public int Layouts { get; set; }
    public int Worlds { get; set; }
    public DateTime? LastModified { get; set; }
    public string? GitBranch { get; set; }
}

/// <summary>What you track about a project (saved in %AppData%\ReforgerHub\tracking.json).</summary>
public sealed class Tracking
{
    public string Status { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string Notes { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTime? LastOpened { get; set; }
    public int OpenCount { get; set; }
}

/// <summary>Workbench sessions found in the log folders, per project ID.</summary>
public sealed class SessionInfo
{
    public int Sessions { get; set; }
    public DateTime? LastSession { get; set; }
    public double Hours { get; set; }
    public string? LastLogDir { get; set; }
}

public sealed class Settings
{
    public string LocalAddonsDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "ArmaReforgerWorkbench", "addons");
    public string WorkshopAddonsDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "ArmaReforger", "addons");
    public string LogsDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "ArmaReforgerWorkbench", "logs");
    public string WorkbenchExe { get; set; } = "";
    public string GameDir { get; set; } = "";
    public List<string> ExtraDirs { get; set; } = new();
    /// <summary>The Arma Reforger dedicated server used to update mods (empty: the Hub's own copy)</summary>
    public string ServerDir { get; set; } = "";
    public bool CheckUpdatesOnStart { get; set; } = true;
}
