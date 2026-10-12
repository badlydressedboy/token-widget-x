using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TokenWidget;
public partial class MainWindow : Window
{
    private readonly UsageReader reader = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private Reading[] points = [];
    private bool refreshing;
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Refresh();
        timer.Tick += async (_, _) => await Refresh();
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }
    private async Task Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var all = await Task.Run(() => reader.Refresh(DateTimeOffset.UtcNow));
            var latest = all.LastOrDefault();
            points = latest == null ? [] : all.Where(p => p.Reset == latest.Reset).ToArray();
            Render();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { Status.Text = "Unable to read session logs. Retrying in 30 seconds."; }
        finally { refreshing = false; }
    }
    private void PinChanged(object sender, RoutedEventArgs e) => Topmost = Pin.IsChecked == true;
    private void HorizonChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Render(); }
    private void ChartSizeChanged(object sender, SizeChangedEventArgs e) { if (IsLoaded) Render(); }
    private void Render()
    {
        Chart.Children.Clear();
        if (points.Length == 0)
        {
            Status.Text = $"No weekly Codex meter readings found. Use Codex to generate a usage snapshot. Watching {reader.Root}";
            return;
        }
        var last = points[^1];
        var now = DateTimeOffset.UtcNow;
        var expired = now >= last.Reset;
        var forecast = Horizon.SelectedIndex == 0 ? Burn.CalculateAdaptive(points, now) :
            Burn.Calculate(points, TimeSpan.FromHours(Horizon.SelectedIndex switch { 1 => 6, 3 => 168, _ => 24 }));
        Renewal.Text = $"7-day window · Renews {last.Reset.LocalDateTime:ddd d MMM, HH:mm}";
        Remaining.Text = expired ? "Awaiting renewal" : $"{100 - last.Used:0.#}% left";
        Remaining.FontSize = expired ? 26 : 34;
        Used.Text = $"{last.Used:0.#}% used · {100 - last.Used:0.#}% remaining at last reading";
        Prediction.Text = expired ? "New reading needed" : forecast == null ? "More history needed" :
            forecast.Exhaustion <= last.Reset ? $"Runs out {forecast.Exhaustion.Value.LocalDateTime:ddd HH:mm}" : $"{forecast.RemainingAtReset:0.#}% left at renewal";
        Rate.Text = forecast == null ? "At least 15 minutes of readings needed" : $"{forecast.Rate:0.00} percentage points / hour";
        var age = now - last.Time;
        Status.Text = $"Last reading {last.Time.LocalDateTime:ddd HH:mm:ss} ({Math.Max(0, age.TotalMinutes):0}m ago). " +
            (expired ? "Previous window ended; waiting for a new Codex snapshot. " : age.TotalMinutes > 15 ? "Stale reading; forecast is anchored to that reading. " : "") +
            $"Forecast basis: {forecast?.Basis ?? "insufficient history"}. Assumes that pace continues. " +
            "Local Codex allowance %, refreshed every 30s." +
            (reader.UnreadableFiles > 0 ? $" {reader.UnreadableFiles} file(s) unavailable." : "");

        double w = Chart.ActualWidth - 48, h = Chart.ActualHeight - 28;
        if (w <= 0 || h <= 0) return;
        var start = last.Reset.AddDays(-7);
        double X(DateTimeOffset t) => 35 + Math.Clamp((t - start).TotalDays / 7, 0, 1) * w;
        double Y(double remaining) => 5 + (100 - remaining) / 100 * (h - 10);
        foreach (var level in new[] { 0, 25, 50, 75, 100 })
        {
            Line(35, Y(level), 35 + w, Y(level), "#293548");
            Label($"{level}%", 0, Y(level) - 8, "#8798AF");
        }
        for (int day = 0; day <= 7; day++)
        {
            var date = start.AddDays(day);
            Label(date.LocalDateTime.ToString("ddd"), X(date) - 12, h + 6, "#8798AF");
        }
        var actual = new Polyline { Stroke = Brush("#7DE1CB"), StrokeThickness = 2.5 };
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (i > 0) actual.Points.Add(new(X(p.Time), Y(100 - points[i - 1].Used)));
            actual.Points.Add(new(X(p.Time), Y(100 - p.Used)));
        }
        Chart.Children.Add(actual);
        var dot = new Ellipse { Width = 7, Height = 7, Fill = Brush("#7DE1CB") };
        Canvas.SetLeft(dot, X(last.Time) - 3.5); Canvas.SetTop(dot, Y(100 - last.Used) - 3.5); Chart.Children.Add(dot);
        if (forecast != null && !expired)
        {
            var end = forecast.Exhaustion is { } exhaustion && exhaustion < last.Reset ? exhaustion : last.Reset;
            Line(X(last.Time), Y(100 - last.Used), X(end), Y(forecast.RemainingAtReset), "#FDBB74", true);
        }
        if (now < last.Reset && now >= start) Line(X(now), 0, X(now), h, "#59677D", true);
    }
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private void Line(double x1, double y1, double x2, double y2, string color, bool dashed = false)
    {
        Chart.Children.Add(new Line { X1 = x1, X2 = x2, Y1 = y1, Y2 = y2, Stroke = Brush(color), StrokeThickness = dashed ? 1.7 : 1,
            StrokeDashArray = dashed ? new DoubleCollection { 4, 4 } : null });
    }
    private void Label(string text, double x, double y, string color)
    {
        var label = new TextBlock { Text = text, Foreground = Brush(color), FontSize = 10 };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); Chart.Children.Add(label);
    }
}
