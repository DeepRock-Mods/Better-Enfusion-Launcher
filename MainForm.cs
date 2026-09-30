using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ReforgerHub;

/// <summary>The window: a WebView2 showing the UI (wwwroot), and the commands the UI calls.</summary>
public sealed class MainForm : Form
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(10, 12, 16) };
    private readonly Store _store;
    private readonly string? _screenshot;
    private List<Project> _projects = new();
    private CancellationTokenSource? _statsCancel;
    private CancellationTokenSource? _checkCancel;
    private readonly Dictionary<string, RemoteInfo> _remote = new();
    private readonly Updater _updater;

    public MainForm(Store? store = null, string? screenshot = null)
    {
        _store = store ?? new Store();
        _screenshot = screenshot;
        Text = "Reforger Hub";
        Width = 1600;
        Height = 960;
        MinimumSize = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 12, 16);
        var icon = Path.Combine(AppContext.BaseDirectory, "wwwroot", "icon.ico");
        if (File.Exists(icon))
            Icon = new Icon(icon);

        _updater = new Updater(() => _store.Settings, progress => Push("updater", progress));
        Controls.Add(_web);
        FormClosing += (_, _) => _updater.Cancel();
        Load += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        _store.Load();

        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReforgerHub", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, data);
        await _web.EnsureCoreWebView2Async(env);

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.SetVirtualHostNameToFolderMapping("hub.local", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Allow);
        MapModsFolder();
        core.WebMessageReceived += OnMessage;
        core.Navigate("https://hub.local/index.html");
    }

    /// <summary>Downloaded mods' thumbnails are served from https://mods.local/</summary>
    private void MapModsFolder()
    {
        var dir = _store.Settings.WorkshopAddonsDir;
        if (Directory.Exists(dir))
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping("mods.local", dir, CoreWebView2HostResourceAccessKind.Allow);
    }

    // ---------------------------------------------------------------------------------------------
    // Messaging: the UI sends {id, cmd, args}; we answer {id, ok, result|error}. Pushes are {event, data}.
    // ---------------------------------------------------------------------------------------------
    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? message;
        try { message = JsonNode.Parse(e.WebMessageAsJson); }
        catch { return; }

        var id = message?["id"]?.GetValue<int>() ?? 0;
        var cmd = message?["cmd"]?.GetValue<string>() ?? "";
        var args = message?["args"] as JsonObject ?? new JsonObject();
        try
        {
            var result = await HandleCommand(cmd, args);
            Post(new { id, ok = true, result });
        }
        catch (Exception ex)
        {
            Post(new { id, ok = false, error = ex.Message });
        }
    }

    private void Post(object payload)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Post(payload));
            return;
        }
        _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, Json));
    }

    private void Push(string name, object data) => Post(new { @event = name, data });

    private static string Arg(JsonObject args, string name) => args[name]?.GetValue<string>() ?? "";

    private async Task<object?> HandleCommand(string cmd, JsonObject args)
    {
        switch (cmd)
        {
            case "rendered":
            {
                // Demo mode: the UI has finished its entry animations; capture it and close
                if (_screenshot == null)
                    return false;

                await using (var file = File.Create(_screenshot))
                    await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                BeginInvoke(Close);
                return true;
            }

            case "init":
            case "rescan":
                return await Rescan();

            case "setTracking":
            {
                var tracking = _store.Track(Arg(args, "guid"));
                var patch = args["patch"] as JsonObject ?? new JsonObject();
                if (patch["status"] is JsonNode status) tracking.Status = status.GetValue<string>();
                if (patch["notes"] is JsonNode notes) tracking.Notes = notes.GetValue<string>();
                if (patch["favorite"] is JsonNode favorite) tracking.Favorite = favorite.GetValue<bool>();
                if (patch["tags"] is JsonArray tags) tracking.Tags = tags.Select(t => t!.GetValue<string>()).Where(t => t.Length > 0).Distinct().ToList();
                _store.SaveTracking();
                return tracking;
            }

            case "openWorkbench":
                return OpenWorkbench(Arg(args, "guid"));

            case "workbenchState":
                return Logs.WorkbenchState(_store.Settings.LogsDir);

            case "openPath":
            {
                var path = Arg(args, "path");
                if (!File.Exists(path) && !Directory.Exists(path))
                    throw new Exception("Not found: " + path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return true;
            }

            case "openInCode":
            {
                Process.Start(new ProcessStartInfo("code", $"\"{Arg(args, "path")}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                return true;
            }

            case "copy":
                Invoke(() => Clipboard.SetText(Arg(args, "text")));
                return true;

            case "logErrors":
            {
                var dir = Arg(args, "dir");
                if (string.IsNullOrEmpty(dir))
                    dir = Logs.LatestLogDir(_store.Settings.LogsDir) ?? "";
                return await Task.Run(() => Logs.ReadErrors(string.IsNullOrEmpty(dir) ? null : dir));
            }

            case "saveSettings":
            {
                var settings = args["settings"].Deserialize<Settings>(Json);
                if (settings != null)
                {
                    settings.ExtraDirs = settings.ExtraDirs.Where(d => d.Length > 0).ToList();
                    _store.Settings = settings;
                    _store.SaveSettings();
                    Invoke(MapModsFolder);
                }
                return await Rescan();
            }

            case "browseFolder":
                return Invoke(() =>
                {
                    using var dialog = new FolderBrowserDialog { SelectedPath = Arg(args, "start"), UseDescriptionForTitle = true, Description = "Choose a folder" };
                    return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
                });

            case "browseFile":
                return Invoke(() =>
                {
                    using var dialog = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", FileName = Arg(args, "start") };
                    return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
                });

            case "newProject":
                return NewProject(args);

            case "appShortcuts":
                Launcher.CreateAppShortcuts();
                return true;

            case "updaterState":
                return _updater.State();

            case "checkUpdates":
            {
                var guids = (args["guids"] as JsonArray)?.Select(g => g!.GetValue<string>()).ToList();
                StartUpdateCheck(guids);
                return true;
            }

            case "remoteInfo":
                lock (_remote)
                    return _remote.Values.ToList();

            case "installUpdater":
                _updater.StartInstall();
                return true;

            case "updateMods":
            {
                var guids = (args["guids"] as JsonArray)?.Select(g => g!.GetValue<string>().ToUpperInvariant()).ToList() ?? new();
                var targets = new Dictionary<string, (string Name, string? Version)>();
                foreach (var guid in guids)
                {
                    var project = _projects.FirstOrDefault(p => p.Guid == guid && p.Source == "workshop");
                    RemoteInfo? remote;
                    lock (_remote)
                        _remote.TryGetValue(guid, out remote);
                    targets[guid] = (remote?.Name is { Length: > 0 } n ? n : project?.Title ?? guid, remote?.Version);
                }
                _updater.StartUpdate(targets);
                return true;
            }

            case "cancelUpdate":
                _updater.Cancel();
                return true;

            case "openUrl":
            {
                var url = Arg(args, "url");
                if (!url.StartsWith("https://"))
                    throw new Exception("Not a web link");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }

            case "projectShortcut":
            {
                var project = Launcher.Find(_projects, Arg(args, "guid")) ?? throw new Exception("Project not found");
                return Launcher.CreateProjectShortcut(project);
            }
        }

        throw new Exception("Unknown command " + cmd);
    }

    // ---------------------------------------------------------------------------------------------
    private async Task<object> Rescan()
    {
        _projects = await Task.Run(() => Scanner.Scan(_store.Settings));
        StartBackgroundWork();
        return new
        {
            settings = _store.Settings,
            demo = _store.Demo,
            projects = _projects.Select(Describe),
            tracking = _store.Tracking,
        };
    }

    private object Describe(Project p) => new
    {
        p.Guid, p.Id, p.Title, p.Dir, p.GprojPath, p.Source, p.Dependencies, p.Version, p.GameVersion, p.Changelog,
        thumbnail = p.Thumbnail == null ? null : "https://mods.local/" + Uri.EscapeDataString(Path.GetFileName(p.Dir)) + "/thumbnail.png",
        p.Stats,
    };

    /// <summary>Folder stats and Workbench sessions are slow to gather: pushed to the UI when ready</summary>
    private void StartBackgroundWork()
    {
        _statsCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _statsCancel = cancel;
        var projects = _projects.ToList();
        var logs = _store.Settings.LogsDir;

        Task.Run(() =>
        {
            var sessions = Logs.ReadSessions(logs);
            if (!cancel.IsCancellationRequested)
                Push("sessions", sessions);
        });

        Task.Run(() =>
        {
            // Your projects first: they are the ones you work on
            foreach (var project in projects.OrderBy(p => p.Source == "local" ? 0 : 1))
            {
                if (cancel.IsCancellationRequested)
                    return;

                project.Stats = Scanner.ComputeStats(project);
                Push("stats", new { guid = project.Guid, dir = project.Dir, stats = project.Stats });
            }
        });
    }

    /// <summary>Reads the Workshop pages of the downloaded mods (a few at a time), pushing each one as it arrives</summary>
    private void StartUpdateCheck(List<string>? only)
    {
        _checkCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _checkCancel = cancel;
        var guids = (only ?? _projects.Where(p => p.Source == "workshop").Select(p => p.Guid).ToList()).Distinct().ToList();
        Push("checking", new { count = guids.Count });

        Task.Run(async () =>
        {
            using var gate = new SemaphoreSlim(4);
            var tasks = guids.Select(async guid =>
            {
                await gate.WaitAsync(cancel.Token);
                try
                {
                    var info = await Workshop.Fetch(guid, cancel.Token);
                    lock (_remote)
                        _remote[guid] = info;
                    Push("remote", info);
                }
                finally
                {
                    gate.Release();
                }
            });
            try
            {
                await Task.WhenAll(tasks);
                Push("checked", new { count = guids.Count });
            }
            catch (OperationCanceledException)
            {
                // A newer check replaced this one
            }
        });
    }

    private object OpenWorkbench(string guid)
    {
        var project = Launcher.Find(_projects, guid) ?? throw new Exception("Project not found");
        return Launcher.OpenWorkbench(_store, project);
    }

    private object NewProject(JsonObject args)
    {
        var title = Arg(args, "title").Trim();
        if (title.Length == 0)
            throw new Exception("Give the project a name");

        var id = new string(title.Where(char.IsLetterOrDigit).ToArray());
        if (id.Length == 0)
            id = "NewAddon";

        var dir = Path.Combine(_store.Settings.LocalAddonsDir, title);
        if (Directory.Exists(dir))
            throw new Exception("A folder with that name already exists");

        var deps = (args["dependencies"] as JsonArray)?.Select(d => d!.GetValue<string>()).ToList() ?? new List<string>();
        if (!deps.Contains(Scanner.BaseGameGuid))
            deps.Add(Scanner.BaseGameGuid);

        var guid = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "Scripts", "Game"));
        var lines = new List<string> { "GameProject {", $" ID \"{id}\"", $" GUID \"{guid}\"", $" TITLE \"{title}\"", " Dependencies {" };
        lines.AddRange(deps.Select(d => $"  \"{d}\""));
        lines.AddRange(new[] { " }", "}" });
        File.WriteAllText(Path.Combine(dir, "addon.gproj"), string.Join("\n", lines) + "\n");

        var tracking = _store.Track(guid);
        tracking.Status = "idea";
        _store.SaveTracking();
        return new { guid };
    }
}
