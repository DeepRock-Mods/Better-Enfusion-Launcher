using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReforgerHub;

/// <summary>A mod as the Workshop has it now (read from its public page on reforger.armaplatform.com).</summary>
public sealed class RemoteInfo
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public long Size { get; set; }
    public string? GameVersion { get; set; }
    public string? Changelog { get; set; }
    public string? Author { get; set; }
    public int Subscribers { get; set; }
    public double Rating { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool Obsolete { get; set; }
    public List<RemoteDependency> Dependencies { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class RemoteDependency
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public long Size { get; set; }
}

/// <summary>Reads the public Workshop pages.</summary>
public static class Workshop
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex NextData = new("<script id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", RegexOptions.Singleline);

    public static string PageUrl(string guid) => "https://reforger.armaplatform.com/workshop/" + guid;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ReforgerHub/1.0");
        return http;
    }

    public static async Task<RemoteInfo> Fetch(string guid, CancellationToken cancel)
    {
        var info = new RemoteInfo { Guid = guid };
        try
        {
            var html = await Http.GetStringAsync(PageUrl(guid), cancel);
            var match = NextData.Match(html);
            if (!match.Success)
            {
                info.Error = "Not on the Workshop (private, removed or unlisted)";
                return info;
            }

            var asset = JsonNode.Parse(match.Groups[1].Value)?["props"]?["pageProps"]?["asset"];
            if (asset == null)
            {
                info.Error = "Not on the Workshop (private, removed or unlisted)";
                return info;
            }

            info.Name = Str(asset["name"]) ?? "";
            info.Version = Str(asset["currentVersionNumber"]);
            info.Size = asset["currentVersionSize"]?.GetValue<long>() ?? 0;
            info.GameVersion = Str(asset["gameVersion"]);
            info.Author = Str(asset["author"]?["username"]);
            info.Subscribers = asset["subscriberCount"]?.GetValue<int>() ?? 0;
            info.Rating = asset["averageRating"]?.GetValue<double>() ?? 0;
            info.Obsolete = asset["obsolete"]?.GetValue<bool>() ?? false;
            if (DateTime.TryParse(Str(asset["updatedAt"]), out var updated))
                info.UpdatedAt = updated;

            var detail = JsonNode.Parse(match.Groups[1].Value)?["props"]?["pageProps"]?["assetVersionDetail"];
            info.Changelog = Str(detail?["changelog"]);

            if (asset["dependencies"] is JsonArray deps)
            {
                foreach (var dep in deps)
                {
                    info.Dependencies.Add(new RemoteDependency
                    {
                        Guid = (Str(dep?["asset"]?["id"]) ?? "").ToUpperInvariant(),
                        Name = Str(dep?["asset"]?["name"]) ?? "",
                        Version = Str(dep?["version"]),
                        Size = dep?["totalFileSize"]?.GetValue<long>() ?? 0,
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            info.Error = ex is HttpRequestException ? "Could not reach the Workshop" : ex.Message;
        }
        return info;
    }

    private static string? Str(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch { return node?.ToString(); }
    }

    /// <summary>The installed game's version (its exe's file version, e.g. 1.8.0.13).</summary>
    public static string? GameVersion(Settings settings)
    {
        foreach (var name in new[] { "ArmaReforgerSteam.exe", "ArmaReforger.exe" })
        {
            var exe = Path.Combine(settings.GameDir, name);
            if (File.Exists(exe))
                return FileVersionInfo.GetVersionInfo(exe).FileVersion;
        }
        return null;
    }

    /// <summary>The version a downloaded mod has on disk (its ServerData.json).</summary>
    public static string? InstalledVersion(string addonsDir, string guid)
    {
        if (!Directory.Exists(addonsDir))
            return null;

        foreach (var dir in Directory.EnumerateDirectories(addonsDir, "*_" + guid))
        {
            var path = Path.Combine(dir, "ServerData.json");
            if (!File.Exists(path))
                continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("revision", out var revision) && revision.TryGetProperty("version", out var v))
                    return v.GetString();
            }
            catch
            {
                // Being written: the next change event reads it again
            }
        }
        return null;
    }
}

/// <summary>
/// Updates downloaded mods without the game: Bohemia's free dedicated server downloads the mods its config lists
/// (and their dependencies) into a folder of our choice - the game's own mods folder. The server is installed once
/// with Valve's SteamCMD (anonymous login).
/// </summary>
public sealed class Updater
{
    public const string ServerAppId = "1874900";
    private const string SteamCmdZip = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private const string UpdaterScenario = "{ECC61978EDCC2B5A}Missions/23_Campaign.conf";

    private static readonly Regex SteamProgress = new(@"progress:\s*([\d.]+)", RegexOptions.IgnoreCase);
    private static readonly Regex Percent = new(@"(\d{1,3}(?:\.\d+)?)\s*%");
    private static readonly string[] ServerReady = { "Required addons are ready", "Game successfully created", "Entering game mode", "Loading world" };
    private static readonly string[] BlockingProcesses = { "ArmaReforgerSteam", "ArmaReforger", "ArmaReforger_BE", "ArmaReforgerWorkbenchSteam", "ArmaReforgerWorkbenchSteamDiag", "ArmaReforgerWorkbench" };

    private readonly Func<Settings> _settings;
    private readonly Action<object> _push;
    private readonly object _lock = new();
    private CancellationTokenSource? _cancel;
    private Process? _process;

    public Updater(Func<Settings> settings, Action<object> push)
    {
        _settings = settings;
        _push = push;
    }

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReforgerHub");
    private static string SteamCmdDir => Path.Combine(Root, "steamcmd");
    private static string SteamCmdExe => Path.Combine(SteamCmdDir, "steamcmd.exe");

    public string ServerDir => string.IsNullOrWhiteSpace(_settings().ServerDir) ? Path.Combine(Root, "server") : _settings().ServerDir;
    public string ServerExe => Path.Combine(ServerDir, "ArmaReforgerServer.exe");
    public bool ServerInstalled => File.Exists(ServerExe);
    public bool Busy { get; private set; }
    public string? Phase { get; private set; }

    public object State() => new
    {
        serverInstalled = ServerInstalled,
        serverDir = ServerDir,
        busy = Busy,
        phase = Phase,
        gameVersion = Workshop.GameVersion(_settings()),
        blocking = BlockingNow(),
    };

    private static string? BlockingNow()
    {
        foreach (var name in BlockingProcesses)
        {
            if (Process.GetProcessesByName(name).Length > 0)
                return name.Contains("Workbench") ? "Workbench" : "Arma Reforger";
        }
        return null;
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _cancel?.Cancel();
            KillProcess();
        }
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone
        }
    }

    private CancellationToken Begin(string phase)
    {
        lock (_lock)
        {
            if (Busy)
                throw new Exception("The updater is already working");
            Busy = true;
            Phase = phase;
            _cancel = new CancellationTokenSource();
            return _cancel.Token;
        }
    }

    private void End()
    {
        lock (_lock)
        {
            Busy = false;
            Phase = null;
            _process = null;
        }
    }

    private void Report(string phase, string? text = null, double? progress = null, string? line = null) =>
        _push(new { phase, text, progress, line });

    private void Finish(bool ok, string text, object? results = null) =>
        _push(new { phase = "done", ok, text, results });

    // ---------------------------------------------------------------------------------------------
    // One-time install of the dedicated server
    // ---------------------------------------------------------------------------------------------
    public void StartInstall()
    {
        var cancel = Begin("install");
        Task.Run(async () =>
        {
            try
            {
                await EnsureSteamCmd(cancel);
                Directory.CreateDirectory(ServerDir);

                // SteamCMD rejects an install with "Missing configuration" while it is still settling its own
                // update: run the install again when it says so (it is ready by then)
                var exit = 0;
                for (var attempt = 1; attempt <= 3 && !cancel.IsCancellationRequested; attempt++)
                {
                    var missingConfig = false;
                    Report("install", attempt == 1 ? "Installing the Arma Reforger server (one-time download)..." : "SteamCMD was still getting ready - trying the install again...", 0);
                    exit = await RunProcess(SteamCmdExe,
                        $"+force_install_dir \"{ServerDir}\" +login anonymous +app_update {ServerAppId} validate +quit",
                        SteamCmdDir, line =>
                        {
                            if (line.Contains("Missing configuration", StringComparison.OrdinalIgnoreCase))
                                missingConfig = true;
                            var m = SteamProgress.Match(line);
                            double? progress = m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p / 100.0 : null;
                            Report("install", progress != null ? $"Installing the server... {progress * 100:0.0}%" : null, progress, line);
                        }, cancel);

                    if (!missingConfig || ServerInstalled)
                        break;
                }

                if (cancel.IsCancellationRequested)
                    Finish(false, "Install cancelled");
                else if (ServerInstalled)
                    Finish(true, "Updater installed. You can update mods now.");
                else
                    Finish(false, $"SteamCMD finished (code {exit}) but the server is not there - see the log above");
            }
            catch (Exception ex)
            {
                Finish(false, cancel.IsCancellationRequested ? "Install cancelled" : "Install failed: " + ex.Message);
            }
            finally
            {
                End();
            }
        });
    }

    private async Task EnsureSteamCmd(CancellationToken cancel)
    {
        var bootstrapped = Path.Combine(SteamCmdDir, "steamcmd.bootstrapped");
        if (File.Exists(SteamCmdExe) && File.Exists(bootstrapped))
            return;

        if (File.Exists(SteamCmdExe))
        {
            await Bootstrap(bootstrapped, cancel);
            return;
        }

        Report("install", "Downloading SteamCMD...", null);
        Directory.CreateDirectory(SteamCmdDir);
        using var http = new HttpClient();
        var zip = Path.Combine(SteamCmdDir, "steamcmd.zip");
        await using (var file = File.Create(zip))
        await using (var stream = await http.GetStreamAsync(SteamCmdZip, cancel))
            await stream.CopyToAsync(file, cancel);
        ZipFile.ExtractToDirectory(zip, SteamCmdDir, overwriteFiles: true);
        File.Delete(zip);
        await Bootstrap(bootstrapped, cancel);
    }

    /// <summary>SteamCMD's first run only updates SteamCMD itself: let it finish before asking it to install</summary>
    private async Task Bootstrap(string marker, CancellationToken cancel)
    {
        Report("install", "Setting up SteamCMD (first run updates itself)...", null);
        await RunProcess(SteamCmdExe, "+quit", SteamCmdDir, line => Report("install", null, null, line), cancel);
        if (!cancel.IsCancellationRequested)
            File.WriteAllText(marker, DateTime.Now.ToString("O"));
    }

    // ---------------------------------------------------------------------------------------------
    // Updating mods
    // ---------------------------------------------------------------------------------------------
    /// <param name="targets">GUID -> the version the Workshop has now (what "updated" means for it)</param>
    public void StartUpdate(Dictionary<string, (string Name, string? Version)> targets)
    {
        if (!ServerInstalled)
            throw new Exception("Install the updater first (one-time)");
        if (targets.Count == 0)
            throw new Exception("Nothing to update");
        if (BlockingNow() is { } blocking)
            throw new Exception($"Close {blocking} first: it has the mod files open");

        var settings = _settings();
        var addonsDir = settings.WorkshopAddonsDir;
        if (!Directory.Exists(addonsDir))
            throw new Exception("The downloaded mods folder does not exist: " + addonsDir);

        var cancel = Begin("update");
        Task.Run(async () =>
        {
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(addonsDir, "ServerData.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
            };

            var updated = new HashSet<string>();
            void CheckDisk()
            {
                lock (updated)
                {
                    foreach (var (guid, target) in targets)
                    {
                        if (updated.Contains(guid))
                            continue;
                        var installed = Workshop.InstalledVersion(addonsDir, guid);
                        if (installed != null && (target.Version == null || installed == target.Version))
                        {
                            updated.Add(guid);
                            Report("update", $"{target.Name} updated to {installed}", (double)updated.Count / targets.Count, $"[Hub] {target.Name} is now {installed}");
                        }
                    }
                    if (updated.Count == targets.Count)
                        done.TrySetResult("all mods updated");
                }
            }

            watcher.Changed += (_, _) => CheckDisk();
            watcher.Created += (_, _) => CheckDisk();
            watcher.Renamed += (_, _) => CheckDisk();
            watcher.EnableRaisingEvents = true;

            try
            {
                var config = WriteConfig(targets);
                var profile = Path.Combine(Root, "server-profile");
                Directory.CreateDirectory(profile);
                Report("update", $"Starting the download of {targets.Count} mod{(targets.Count == 1 ? "" : "s")}...", 0);

                var run = RunProcess(ServerExe,
                    $"-config \"{config}\" -profile \"{profile}\" -addonDownloadDir \"{addonsDir}\" -maxFPS 30 -logStats 0",
                    ServerDir, line =>
                    {
                        string? text = null;
                        double? progress = null;
                        if (line.Contains("download", StringComparison.OrdinalIgnoreCase))
                        {
                            text = line.Trim();
                            var m = Percent.Match(line);
                            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p))
                                progress = p / 100.0;
                        }
                        Report("update", text, progress, line);

                        // The server moves on to the scenario once every addon is in place
                        if (ServerReady.Any(r => line.Contains(r, StringComparison.OrdinalIgnoreCase)))
                            done.TrySetResult("server finished downloading");
                    }, cancel);

                var first = await Task.WhenAny(done.Task, run);
                KillProcess();
                await run;
                CheckDisk();

                var results = targets.Select(t => new
                {
                    guid = t.Key,
                    name = t.Value.Name,
                    expected = t.Value.Version,
                    installed = Workshop.InstalledVersion(addonsDir, t.Key),
                }).ToList();
                var ok = results.Count(r => r.installed != null && (r.expected == null || r.installed == r.expected));

                if (cancel.IsCancellationRequested)
                    Finish(false, $"Update cancelled ({ok} of {targets.Count} done)", results);
                else if (ok == targets.Count)
                    Finish(true, ok == 1 ? $"{results[0].name} is up to date" : $"All {ok} mods are up to date", results);
                else
                    Finish(false, $"{ok} of {targets.Count} mods updated - see the log for the others", results);
            }
            catch (Exception ex)
            {
                Finish(false, cancel.IsCancellationRequested ? "Update cancelled" : "Update failed: " + ex.Message);
            }
            finally
            {
                watcher.EnableRaisingEvents = false;
                End();
            }
        });
    }

    /// <summary>A private, invisible server whose only job is to download the listed mods.</summary>
    private static string WriteConfig(Dictionary<string, (string Name, string? Version)> targets)
    {
        var mods = new JsonArray();
        foreach (var (guid, target) in targets)
            mods.Add(new JsonObject { ["modId"] = guid, ["name"] = target.Name });

        var config = new JsonObject
        {
            ["bindAddress"] = "127.0.0.1",
            ["bindPort"] = 2411,
            ["publicAddress"] = "127.0.0.1",
            ["publicPort"] = 2411,
            ["game"] = new JsonObject
            {
                ["name"] = "Reforger Hub updater",
                ["password"] = System.Guid.NewGuid().ToString("N")[..12],
                ["scenarioId"] = UpdaterScenario,
                ["maxPlayers"] = 1,
                ["visible"] = false,
                ["crossPlatform"] = false,
                ["mods"] = mods,
            },
        };

        var dir = Path.Combine(Root, "updater");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "server.json");
        File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    // ---------------------------------------------------------------------------------------------
    /// <summary>Runs a console program hidden and hands every output line (split on \r and \n) to onLine.</summary>
    private async Task<int> RunProcess(string exe, string args, string workDir, Action<string> onLine, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var process = Process.Start(info) ?? throw new Exception("Could not start " + Path.GetFileName(exe));
        lock (_lock)
            _process = process;

        using var registration = cancel.Register(KillProcess);
        var output = Pump(process.StandardOutput, onLine);
        var errors = Pump(process.StandardError, onLine);
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(output, errors);
        return process.ExitCode;
    }

    private static async Task Pump(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\r' || c == '\n')
                {
                    if (line.Length > 0)
                        onLine(line.ToString());
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }
        }
        if (line.Length > 0)
            onLine(line.ToString());
    }
}
