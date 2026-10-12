using TokenWidget;
using System.Text.Json;

var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine($"PASS {name}"); }
var now = DateTimeOffset.UtcNow;
var reset = DateTimeOffset.FromUnixTimeSeconds(now.AddDays(2).ToUnixTimeSeconds());
var points = new[] { new Reading(now.AddHours(-6), reset, 40), new Reading(now, reset, 52) };
var forecast = Burn.Calculate(points, TimeSpan.FromHours(24))!;
Check(Math.Abs(forecast.Rate - 2) < 0.001, "Burn uses elapsed hours");
Check(forecast.Exhaustion == now.AddHours(24) && forecast.RemainingAtReset == 0, "Exhaustion before renewal");
Check(Burn.Calculate([points[0]], TimeSpan.FromDays(7)) == null, "Single snapshot has no forecast");
Check(Burn.Calculate([points[0], new(now, reset, 20)], TimeSpan.FromDays(7)) == null, "Downward reset breaks forecast");
var idle = Burn.Calculate([new(now.AddHours(-6), reset, 40), new(now, reset, 40)], TimeSpan.FromDays(7))!;
Check(idle.Rate == 0 && idle.Exhaustion == null && idle.RemainingAtReset == 60, "Idle usage does not exhaust");
Check(Burn.Calculate([new(now.AddMinutes(-5), reset, 40), new(now, reset, 45)], TimeSpan.FromDays(7)) == null, "Short sample is insufficient");
Check(Burn.Calculate(points, TimeSpan.FromHours(1)) == null, "Lookback excludes old points");

var changing = new[] { new Reading(now.AddDays(-2), reset, 0), new Reading(now.AddHours(-6), reset, 70), new Reading(now, reset, 76) };
var adaptive = Burn.CalculateAdaptive(changing, now)!;
Check(Math.Abs(adaptive.Rate - 1) < 0.001 && adaptive.Basis == "Last 6 hours", "Recent slower model usage overrides fast earlier week");
var accelerating = new[] { new Reading(now.AddDays(-2), reset, 0), new Reading(now.AddHours(-6), reset, 10), new Reading(now, reset, 40) };
Check(Math.Abs(Burn.CalculateAdaptive(accelerating, now)!.Rate - 5) < 0.001, "Recent faster model usage overrides slow earlier week");
var quiet = new[] { new Reading(now.AddDays(-2), reset, 0), new Reading(now.AddHours(-6), reset, 40), new Reading(now, reset, 40) };
Check(Burn.CalculateAdaptive(quiet, now)!.Basis.StartsWith("Week history") && Math.Abs(Burn.CalculateAdaptive(quiet, now)!.Rate - 40.0 / 48) < 0.001, "Quiet six hours falls back to available week history including idle time");
Check(Burn.CalculateAdaptive(points, now.AddHours(8))!.Basis.StartsWith("Week history"), "Stale recent activity does not masquerade as current six-hour activity");
Check(Burn.CalculateAdaptive([new(now.AddDays(-1), reset, 40), new(now, reset, 40)], now)!.Exhaustion == null, "All-week idle history produces flat forecast");
Check(Burn.CalculateAdaptive([new(now.AddDays(-1), reset, 20), new(now.AddMinutes(-5), reset, 40), new(now, reset, 41)], now)!.Basis.StartsWith("Week history"), "Insufficient recent span uses week history");
var jitter = new[] { new Reading(now.AddHours(-6), reset, 40), new Reading(now.AddHours(-1), reset, 50), new Reading(now.AddMinutes(-59), reset, 0), new Reading(now.AddMinutes(-58), reset, 50), new Reading(now, reset, 52) };
Check(Math.Abs(Burn.CalculateAdaptive(jitter, now)!.Rate - 2) < 0.001, "Brief stale meter drop does not shorten six-hour forecast");
Check(Burn.CalculateAdaptive([new(now.AddHours(-6), reset, 40), new(now, reset, 0)], now) == null, "Unconfirmed downward reset is not discarded");
Check(Burn.CalculateAdaptive([new(now.AddDays(-1), reset, 60), new(now.AddHours(-3), reset, 10), new(now, reset, 16)], now)!.Rate == 2, "Sustained downward adjustment starts new segment");
var spacing = new[] { new Reading(now.AddHours(-6), reset, 40), new Reading(now.AddHours(-5.9), reset, 41), new Reading(now.AddHours(-5.8), reset, 42), new Reading(now, reset, 52) };
Check(Burn.CalculateAdaptive(spacing, now)!.Rate == 2, "Uneven sample density does not bias elapsed-time burn");

var temp = Path.Combine(Path.GetTempPath(), "token-widget-tests-" + Guid.NewGuid());
var original = Environment.GetEnvironmentVariable("CODEX_HOME");
try
{
 Environment.SetEnvironmentVariable("CODEX_HOME", temp);
 var dir = Path.Combine(temp, "sessions", now.ToString("yyyy/MM/dd"));
 Directory.CreateDirectory(dir);
 string Event(DateTimeOffset time, int minutes, double used, string field = "primary") => JsonSerializer.Serialize(new
 {
   timestamp = time, type = "event_msg", payload = new { type = "token_count", rate_limits = new Dictionary<string, object>
   { [field] = new { window_minutes = minutes, used_percent = used, resets_at = reset.ToUnixTimeSeconds() } } }
 });
 var file = Path.Combine(dir, "test.jsonl");
 var first = Event(now.AddHours(-1), 10080, 40, "secondary");
 File.WriteAllText(file, first + "\n" + first + "\n" + Event(now, 300, 99) + "\n{broken}\n");
 var reader = new UsageReader();
 Check(reader.Refresh(now).Length == 1, "Deduplicate and select weekly secondary meter");
 var next = Event(now, 10080, 42);
 File.AppendAllText(file, next[..(next.Length / 2)]);
 Check(reader.Refresh(now).Length == 1, "Ignore incomplete concurrent append");
 File.AppendAllText(file, next[(next.Length / 2)..] + "\n");
 Check(reader.Refresh(now).Length == 2, "Recover completed append");
 Check(reader.Refresh(now).Length == 2, "Unchanged file remains deduplicated");
 var oldDir = Path.Combine(temp, "sessions", now.AddMonths(-1).ToString("yyyy/MM/dd"));
 Directory.CreateDirectory(oldDir);
 var resumedFile = Path.Combine(oldDir, "resumed.jsonl");
 File.WriteAllText(resumedFile, Event(now.AddMonths(-1), 10080, 10) + "\n" + Event(now.AddMinutes(-5), 10080, 41) + "\n");
 Check(reader.Refresh(now).Length == 3, "Recent event in old conversation folder is discovered; old event excluded");
 File.AppendAllText(resumedFile, Event(now.AddMinutes(1), 10080, 43) + "\n");
 Check(reader.Refresh(now.AddMinutes(1))[^1].Used == 43, "Resumed old conversation continues refreshing");
}
finally
{
 Environment.SetEnvironmentVariable("CODEX_HOME", original);
 // This unique test directory is created and owned by this test process.
 Directory.Delete(temp, true);
}
Console.WriteLine($"{checks} checks passed.");
if (args.Contains("--live"))
{
 var live = new UsageReader().Refresh(now);
 var last = live.LastOrDefault();
 Console.WriteLine($"Live weekly readings: {live.Length}; latest used: {last?.Used}%; renewal: {last?.Reset:O}");
 if (last != null)
 {
   var current = live.Where(p => p.Reset == last.Reset).ToArray();
   Console.WriteLine($"Adaptive forecast: {Burn.CalculateAdaptive(current, now)}");
   var drops = current.Zip(current.Skip(1)).Where(pair => pair.Second.Used < pair.First.Used).ToArray();
   Console.WriteLine($"Downward meter changes: {drops.Length}");
   foreach (var pair in drops.TakeLast(6))
   {
     var recovery = current.FirstOrDefault(p => p.Time > pair.Second.Time && p.Used >= pair.First.Used);
     Console.WriteLine($"Drop {pair.Second.Time:O}: {pair.First.Used}% -> {pair.Second.Used}%; recovered after {(recovery?.Time - pair.Second.Time)?.TotalMinutes:0.0} minutes");
   }
 }
}
