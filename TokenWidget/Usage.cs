using System.IO;
using System.Text.Json;

namespace TokenWidget;

public record Reading(DateTimeOffset Time, DateTimeOffset Reset, double Used);
public record Forecast(double Rate, DateTimeOffset? Exhaustion, double RemainingAtReset, string Basis = "Selected interval");

public static class Burn
{
    private static Reading[] StableReadings(IReadOnlyList<Reading> points)
    {
        var stable = new List<Reading>();
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (stable.Count > 0 && point.Reset == stable[^1].Reset && point.Used < stable[^1].Used)
            {
                // Concurrent conversations can briefly report older meter values.
                // Require an observed recovery; never invent one for the latest reading.
                var recovery = i + 1;
                while (recovery < points.Count && points[recovery].Reset == point.Reset &&
                    points[recovery].Time - point.Time <= TimeSpan.FromMinutes(30) &&
                    points[recovery].Used < stable[^1].Used) recovery++;
                if (recovery < points.Count && points[recovery].Reset == point.Reset &&
                    points[recovery].Time - point.Time <= TimeSpan.FromMinutes(30) &&
                    points[recovery].Used >= stable[^1].Used)
                {
                    i = recovery - 1;
                    continue;
                }
            }
            stable.Add(point);
        }
        return stable.ToArray();
    }

    public static Forecast? CalculateAdaptive(IReadOnlyList<Reading> points, DateTimeOffset now)
    {
        // A quiet/stale six-hour interval must not reuse yesterday's active six hours.
        var recent = points.Where(p => p.Time >= now.AddHours(-6) && p.Time <= now).ToArray();
        var estimate = Calculate(recent, TimeSpan.FromHours(6));
        if (estimate is { Rate: > 0 }) return estimate with { Basis = "Last 6 hours" };
        var week = Calculate(points.Where(p => p.Time <= now).ToArray(), TimeSpan.FromDays(7));
        return week == null ? null : week with { Basis = "Week history (6h quiet / insufficient)" };
    }

    public static Forecast? Calculate(IReadOnlyList<Reading> points, TimeSpan window)
    {
        points = StableReadings(points);
        if (points.Count < 2) return null;
        var last = points[^1];
        // Never fit across a manual reset or other downward adjustment.
        var start = 0;
        for (var i = 1; i < points.Count; i++)
            if (points[i].Used < points[i - 1].Used || points[i].Reset != points[i - 1].Reset) start = i;
        var recent = points.Skip(start).Where(p => p.Time >= last.Time - window).ToArray();
        if (recent.Length < 2) return null;
        var hours = (last.Time - recent[0].Time).TotalHours;
        if (hours < 0.25) return null;
        var rate = (last.Used - recent[0].Used) / hours;
        if (rate < 0) return null;
        DateTimeOffset? end = rate > 0 ? last.Time.AddHours((100 - last.Used) / rate) : null;
        return new(rate, end, Math.Clamp(100 - last.Used - rate * (last.Reset - last.Time).TotalHours, 0, 100));
    }
}

// Incremental shared-file reader: session logs remain owned by Codex.
public sealed class UsageReader
{
    private readonly Dictionary<string, long> offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(DateTimeOffset, DateTimeOffset), Reading> readings = new();
    public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ??
        Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "sessions");
    public int UnreadableFiles { get; private set; }

    public Reading[] Refresh(DateTimeOffset now)
    {
        UnreadableFiles = 0;
        if (!Directory.Exists(Root)) return [];
        // Folder dates identify conversation creation, not the events appended today.
        // Discover recent writes anywhere in the tree, including resumed old sessions.
        foreach (var file in Directory.EnumerateFiles(Root, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < now.UtcDateTime.AddDays(-8)) continue;
                Read(file, now.AddDays(-8));
            }
            catch (IOException) { UnreadableFiles++; }
            catch (UnauthorizedAccessException) { UnreadableFiles++; }
        }
        foreach (var key in readings.Keys.Where(k => k.Item1 < now.AddDays(-8)).ToArray()) readings.Remove(key);
        return readings.Values.OrderBy(p => p.Time).ToArray();
    }

    private void Read(string path, DateTimeOffset cutoff)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = offsets.GetValueOrDefault(path);
        if (offset > stream.Length) offset = 0;
        stream.Position = offset;
        // Commit only complete lines so a concurrent append cannot lose an event.
        using var line = new MemoryStream();
        var buffer = new byte[65536];
        long position = offset;
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                position++;
                if (buffer[i] == 10)
                {
                    Parse(line.ToArray(), cutoff);
                    line.SetLength(0);
                    offsets[path] = position;
                }
                else line.WriteByte(buffer[i]);
            }
        }
    }

    private void Parse(byte[] line, DateTimeOffset cutoff)
    {
        if (!System.Text.Encoding.UTF8.GetString(line).Contains("\"rate_limits\"", StringComparison.Ordinal)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "event_msg") return;
            var payload = root.GetProperty("payload");
            if (payload.GetProperty("type").GetString() != "token_count") return;
            var time = root.GetProperty("timestamp").GetDateTimeOffset();
            if (time < cutoff || !payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return;
            if (limits.TryGetProperty("limit_id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() != "codex") return;
            foreach (var name in new[] { "primary", "secondary" })
            {
                if (!limits.TryGetProperty(name, out var meter) || meter.ValueKind != JsonValueKind.Object) continue;
                if (!meter.TryGetProperty("window_minutes", out var minutes) || minutes.GetInt32() != 10080) continue;
                var reset = DateTimeOffset.FromUnixTimeSeconds(meter.GetProperty("resets_at").GetInt64());
                var used = meter.GetProperty("used_percent").GetDouble();
                if (!double.IsFinite(used) || used < 0 || used > 100 || time >= reset || time < reset.AddDays(-7)) continue;
                readings[(time, reset)] = new(time, reset, used);
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentOutOfRangeException) { }
    }
}
