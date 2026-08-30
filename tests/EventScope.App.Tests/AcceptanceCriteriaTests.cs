using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Threading;
using EventScope.App.Collections;
using EventScope.App.ViewModels;
using EventScope.Core.Models;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// Measures one of the build plan's five M1 acceptance criteria (§6) that needs a real
/// DataGrid — the other four live in <c>EventScope.Acceptance.Tests</c> (cold segment read
/// latency, zero loss under saturation — no Avalonia dependency, so kept out of this
/// assembly on purpose, see that project's .csproj remarks) and
/// <c>build/Measure-M1Acceptance.ps1</c> (UI frame time at 10k msg/s, heap growth — need a
/// real windowed process a headless test can't provide).
///
/// Gated behind <c>EVENTSCOPE_SOAK=1</c> so the normal fast suite stays fast — this
/// intentionally scrolls more rows than a unit test needs to, to get a real number rather
/// than a toy one. Writes its measurement to a CSV under
/// <c>tests/EventScope.Bench/baselines/acceptance/</c> so the numbers are reviewable, not
/// just asserted — see that directory's README for machine details.
/// </summary>
public sealed class AcceptanceCriteriaTests
{
    public static bool SoakEnabled => Environment.GetEnvironmentVariable("EVENTSCOPE_SOAK") == "1";

    public AcceptanceCriteriaTests() => HeadlessFixture.EnsureInitialized();

    [Fact(Skip = "Set EVENTSCOPE_SOAK=1 to run — larger volumes than a unit test needs.",
        SkipUnless = nameof(SoakEnabled))]
    public void Scrolling_fifty_thousand_rows_stays_under_the_frame_budget()
    {
        // xunit.v3's in-process runner does not guarantee this method body executes on the
        // same OS thread that HeadlessFixture.EnsureInitialized() ran on (the thread Avalonia's
        // dispatcher actually bound to) — confirmed by measurement: run alongside other test
        // classes, constructing a DataGrid here threw "Call from invalid thread" even though
        // the identical construction in DataGridVirtualizationSpikeTests passed. Dispatcher.
        // Invoke marshals onto the real UI thread regardless of which thread called it from.
        Dispatcher.UIThread.Invoke(() =>
        {
            const int rowCount = 50_000;
            const int warmupSteps = 10;
            const int scrollSteps = 60;

            var view = BuildPopulatedView(rowCount);
            var grid = BuildGrid(view);
            var window = new Window { Content = grid, Width = 800, Height = 400 };
            window.Show();
            HeadlessFixture.Pump();

            // Untimed warm-up, same reasoning BenchmarkDotNet's WarmupCount exists for: the
            // first few scroll steps pay one-time JIT and first-layout costs unrelated to
            // steady-state per-frame cost, which is what the 16 ms budget is actually about.
            // Measured directly: without this, max was 47 ms on the very first steps while
            // p50 across the whole run was 4.8 ms — a warm-up artifact, not a regression.
            for (var i = 0; i < warmupSteps; i++)
            {
                window.MouseWheel(new Point(400, 200), new Vector(0, -3));
                HeadlessFixture.Pump();
            }

            // A headless process has no real compositor, so this measures what
            // MessageRowsView's design actually controls — the CPU-side cost of realizing
            // newly-scrolled-in rows — not GPU frame presentation. That is the part of the
            // 16 ms budget this codebase can regress; the rest is Avalonia's own pipeline.
            var frameTimesMs = new List<double>(scrollSteps);
            var sw = new Stopwatch();

            for (var i = 0; i < scrollSteps; i++)
            {
                sw.Restart();
                window.MouseWheel(new Point(400, 200), new Vector(0, -3));
                HeadlessFixture.Pump();
                sw.Stop();
                frameTimesMs.Add(sw.Elapsed.TotalMilliseconds);
            }

            window.Close();

            frameTimesMs.Sort();
            var p50 = frameTimesMs[frameTimesMs.Count / 2];
            var p99 = frameTimesMs[(int)((frameTimesMs.Count - 1) * 0.99)];
            var max = frameTimesMs[^1];

            WriteAcceptanceCsv("scroll-frame-time.csv",
                ["row_count", "scroll_steps", "p50_ms", "p99_ms", "max_ms"],
                [rowCount.ToString(), scrollSteps.ToString(), $"{p50:F3}", $"{p99:F3}", $"{max:F3}"]);

            Assert.True(max < 16.0,
                $"a scroll step took {max:F2} ms to realize (p50={p50:F2}, p99={p99:F2}), over the 16 ms frame budget");
        });
    }

    private static MessageRowsView BuildPopulatedView(int rowCount)
    {
        var view = new MessageRowsView(65_536);
        var headers = new MessageHeader[rowCount];
        var previews = new string?[rowCount];
        var subjects = new string[rowCount];
        var correlationIds = new string[rowCount];

        for (var i = 0; i < rowCount; i++)
        {
            headers[i] = new MessageHeader(
                sequence: i, enqueuedTicks: DateTime.UtcNow.Ticks + i, rowId: i,
                segmentId: 0, offset: i * 128, length: 128,
                subjectId: i % 16, correlationInternId: i % 1000,
                partition: (short)(i % 4), flags: MessageFlags.None);
            previews[i] = $"preview-{i}";
            subjects[i] = $"orders.created.{i % 16}";
            correlationIds[i] = Guid.NewGuid().ToString();
        }

        view.AppendBatch(headers, previews, subjects, correlationIds);
        return view;
    }

    private static DataGrid BuildGrid(MessageRowsView view)
    {
        var grid = new DataGrid
        {
            ItemsSource = view,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserSortColumns = false,
            RowHeight = 26,
            Width = 800,
            Height = 400,
        };

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Subject",
            Binding = new Binding(nameof(MessageRowViewModel.Subject)),
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Correlation ID",
            Binding = new Binding(nameof(MessageRowViewModel.CorrelationId)),
        });

        // Load-bearing for a test that scrolls repeatedly (unlike DataGridVirtualizationSpikeTests'
        // single-scroll checks, which never exercise this): without wiring UnloadingRow to
        // NotifyRowUnloaded exactly like MainWindow.axaml.cs does, MessageRowsView's own
        // _realized dictionary and DataGrid's row containers both grow unboundedly across
        // repeated scrolls instead of recycling, which measures a test-harness bug, not
        // MessageRowsView's real steady-state cost. Confirmed by measurement: without this,
        // per-scroll cost climbed from ~5 ms to ~180 ms across just 70 scroll steps.
        grid.UnloadingRow += (_, e) => view.NotifyRowUnloaded(e.Row.Index);

        return grid;
    }

    private static void WriteAcceptanceCsv(string fileName, string[] header, string[] row)
    {
        var directory = Path.Combine(FindRepoRoot(), "tests", "EventScope.Bench", "baselines", "acceptance");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, string.Join(',', header) + Environment.NewLine + string.Join(',', row) + Environment.NewLine);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "EventScope.slnx")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return dir ?? throw new DirectoryNotFoundException("Could not locate repo root (EventScope.slnx) from " + AppContext.BaseDirectory);
    }
}
