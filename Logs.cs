using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ReforgerHub;

/// <summary>Reads the Workbench log folders: which project each session opened, how long it ran, and errors.</summary>
public static class Logs
{
    // Workbench names the project it opens in its autosave / thumbnail folder lines
    private static readonly Regex ProjectRx = new(@"Projects[\\/]([^\\/]+)[\\/](autosaves|thumbnails)", RegexOptions.IgnoreCase);
    private static readonly Regex ErrorRx = new(@"^\s*(\d\d:\d\d:\d\d\.\d+)?\s*(\w+)\s*\((E|F)\):\s*(.*)$");

    public static Dictionary<string, SessionInfo> ReadSessions(string logsDir)
    {
        var result = new Dictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(logsDir))
            return result;

        foreach (var dir in Directory.EnumerateDirectories(logsDir, "logs_*"))
        {
            var log = Path.Combine(dir, "console.log");
            if (!File.Exists(log))
                continue;

            var id = ProjectOf(log);
            if (id == null)
                continue;

            var start = StartOf(dir) ?? File.GetCreationTime(log);
            var end = File.GetLastWriteTime(log);
            if (!result.TryGetValue(id, out var info))
            {
                info = new SessionInfo();
                result[id] = info;
            }

            info.Sessions++;
            var hours = (end - start).TotalHours;
            if (hours > 0 && hours < 24)
                info.Hours += hours;
            if (info.LastSession == null || end > info.LastSession)
            {
                info.LastSession = end;
                info.LastLogDir = dir;
            }
        }
        return result;
    }

    /// <summary>The project ID a session opened (read from the first part of its log)</summary>
    public static string? ProjectOf(string log)
    {
        try
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            for (int i = 0; i < 1500; i++)
            {
                var line = reader.ReadLine();
                if (line == null)
                    break;

                var m = ProjectRx.Match(line);
                if (m.Success)
                    return m.Groups[1].Value;
            }
        }
        catch
        {
            // Locked or unreadable
        }
        return null;
    }

    private static DateTime? StartOf(string dir)
    {
        // logs_2026-09-29_19-50-02
        var name = Path.GetFileName(dir);
        return DateTime.TryParseExact(name.Replace("logs_", ""), "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ? start : null;
    }

    public static string? LatestLogDir(string logsDir)
    {
        if (!Directory.Exists(logsDir))
            return null;

        return Directory.EnumerateDirectories(logsDir, "logs_*")
            .OrderByDescending(Directory.GetLastWriteTime)
            .FirstOrDefault();
    }

    /// <summary>Errors of a session, deduplicated with counts, newest session if none given</summary>
    public static object ReadErrors(string? logDir)
    {
        var log = logDir == null ? null : Path.Combine(logDir, "console.log");
        if (log == null || !File.Exists(log))
            return new { dir = logDir, project = (string?)null, compileFailed = false, errors = Array.Empty<object>() };

        var counts = new Dictionary<string, (string module, string text, int count)>();
        var order = new List<string>();
        bool compileFailed = false;
        using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var m = ErrorRx.Match(line);
                if (!m.Success)
                    continue;

                var module = m.Groups[2].Value;
                var text = m.Groups[4].Value.Trim();
                if (text.Contains("Can't compile"))
                    compileFailed = true;

                var key = module + "|" + text;
                if (counts.TryGetValue(key, out var existing))
                {
                    counts[key] = (existing.module, existing.text, existing.count + 1);
                }
                else
                {
                    counts[key] = (module, text, 1);
                    order.Add(key);
                }
            }
        }

        var errors = order.Select(k => counts[k]).Select(e => new { module = e.module, text = e.text, count = e.count }).Take(400).ToList();
        return new { dir = logDir, project = ProjectOf(log), compileFailed, errors };
    }

    /// <summary>The project the running Workbench has open, if any</summary>
    public static object WorkbenchState(string logsDir)
    {
        var running = Process.GetProcesses().Any(p =>
        {
            try { return p.ProcessName.StartsWith("ArmaReforgerWorkbench", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });

        string? project = null;
        if (running)
        {
            var latest = LatestLogDir(logsDir);
            if (latest != null)
                project = ProjectOf(Path.Combine(latest, "console.log"));
        }
        return new { running, project };
    }
}
