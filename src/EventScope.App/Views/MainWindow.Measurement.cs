using System.Diagnostics;
using Avalonia.Threading;

namespace EventScope.App.Views;

/// <summary>
/// M1c acceptance-criteria measurement mode (build plan §6): "10,000 msg/s for 60s, no
/// frame over 100 ms" and "heap growth under 50 MB across that run" both need a real
/// windowed process — a headless test has no compositor and no real dispatcher starvation
/// to measure against. Activated by <c>EVENTSCOPE_MEASURE=&lt;seconds&gt;</c>
/// (see <c>build/Measure-M1Acceptance.ps1</c>, which sets it and attaches
/// <c>dotnet-counters</c> for the heap-growth half); inert otherwise, so this never affects
/// a normal run. Auto-starts streaming and auto-closes when done, so a script only has to
/// launch the process and read the CSV back — no UI Automation click-driving needed for
/// this measurement (unlike the Start/Stop smoke-drive PROGRESS.md's M1b entry describes).
/// </summary>
public partial class MainWindow
{
    private const string MeasureEnvVar = "EVENTSCOPE_MEASURE";
    private const string OutputEnvVar = "EVENTSCOPE_MEASURE_OUTPUT";

    private void MaybeStartMeasurementSession()
    {
        var raw = Environment.GetEnvironmentVariable(MeasureEnvVar);
        if (string.IsNullOrEmpty(raw)) return;

        var seconds = int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 60;
        var outputPath = Environment.GetEnvironmentVariable(OutputEnvVar)
            ?? Path.Combine(Path.GetTempPath(), "eventscope-frame-times.csv");

        Opened += (_, _) => RunMeasurementSession(seconds, outputPath);
    }

    private void RunMeasurementSession(int seconds, string outputPath)
    {
        ViewModel.Start();

        // DispatcherPriority.Render with a 16 ms nominal interval: if the UI thread is ever
        // busy (ingest coalescer work, GC, anything), this tick fires late by exactly however
        // long the thread was unavailable — the direct, honest measurement of "frame time"
        // for a real windowed process, as opposed to a headless test's CPU-only proxy (see
        // EventScope.App.Tests.AcceptanceCriteriaTests' scroll test remarks on that gap).
        var frameTimesMs = new List<double>();
        var stopwatch = Stopwatch.StartNew();
        var lastTick = TimeSpan.Zero;

        var probe = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        probe.Tick += (_, _) =>
        {
            var now = stopwatch.Elapsed;
            if (lastTick != TimeSpan.Zero)
            {
                frameTimesMs.Add((now - lastTick).TotalMilliseconds);
            }

            lastTick = now;
        };
        probe.Start();

        // Samples the ingest channel's byte budget and the SQLite batch writer's pending
        // queue alongside frame time, so a heap-growth investigation can correlate growth
        // against in-flight buffering (expected, bounded by ByteBudgetLimit) versus
        // something unbounded. See PROGRESS.md's heap-growth investigation.
        var byteBudgetSamples = new List<(double ElapsedSeconds, long Used, long Limit, int QueuePending)>();
        var budgetProbe = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        budgetProbe.Tick += (_, _) =>
        {
            byteBudgetSamples.Add((
                stopwatch.Elapsed.TotalSeconds,
                ViewModel.CurrentByteBudgetUsed,
                ViewModel.CurrentByteBudgetLimit,
                ViewModel.CurrentBatchWriterPending));
        };
        budgetProbe.Start();

        var stopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        stopTimer.Tick += (_, _) =>
        {
            stopTimer.Stop();
            probe.Stop();
            budgetProbe.Stop();
            try
            {
                ViewModel.ToggleRunCommand.Execute(null); // stops streaming; DisposeAsync runs on Closing
                WriteFrameTimesCsv(outputPath, seconds, frameTimesMs);
                WriteByteBudgetCsv(outputPath, byteBudgetSamples);
            }
            finally
            {
                // An exception above must never leave the process running with no visible
                // way to stop it — this mode is meant to run unattended from a script.
                Close();
            }
        };
        stopTimer.Start();
    }

    private static void WriteByteBudgetCsv(
        string frameOutputPath,
        List<(double ElapsedSeconds, long Used, long Limit, int QueuePending)> samples)
    {
        var outputPath = Path.Combine(
            Path.GetDirectoryName(frameOutputPath) ?? Path.GetTempPath(),
            "gui-byte-budget.csv");

        var lines = new List<string> { "elapsed_seconds,byte_budget_used,byte_budget_limit,batch_writer_pending" };
        lines.AddRange(samples.Select(s =>
            $"{s.ElapsedSeconds:F2},{s.Used},{s.Limit},{s.QueuePending}"));
        File.WriteAllLines(outputPath, lines);
    }

    private static void WriteFrameTimesCsv(string outputPath, int durationSeconds, List<double> frameTimesMs)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (frameTimesMs.Count == 0)
        {
            File.WriteAllText(outputPath, "duration_seconds,sample_count,p50_ms,p99_ms,max_ms,over_100ms_count\n" +
                $"{durationSeconds},0,0,0,0,0\n");
            return;
        }

        var sorted = frameTimesMs.OrderBy(v => v).ToList();
        var p50 = sorted[sorted.Count / 2];
        var p99 = sorted[(int)((sorted.Count - 1) * 0.99)];
        var max = sorted[^1];
        var over100 = sorted.Count(v => v > 100.0);

        File.WriteAllText(outputPath,
            "duration_seconds,sample_count,p50_ms,p99_ms,max_ms,over_100ms_count\n" +
            $"{durationSeconds},{sorted.Count},{p50:F3},{p99:F3},{max:F3},{over100}\n");
    }
}
