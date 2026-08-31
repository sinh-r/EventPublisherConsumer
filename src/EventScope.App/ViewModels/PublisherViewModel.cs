using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventScope.App.Publisher;
using EventScope.Core.Abstractions;
using EventScope.Core.Generation;
using EventScope.Core.Models;

namespace EventScope.App.ViewModels;

/// <summary>
/// Drives the publisher panel (build plan §5 M3 steps 9–10): the tree editor's flattened
/// rows, the generation plan recomputed on every edit (debounced 150 ms, matching the
/// debounce this codebase already uses for inline validation elsewhere — see
/// <see cref="SearchViewModel"/>), the coloured JSON preview and envelope tab, and
/// publish/burst. <see cref="Recompute"/> is public specifically so tests can drive it
/// synchronously instead of racing the debounce timer.
///
/// <para>No <see cref="IEventSink"/> exists to publish to until build plan §5 M3 step 10
/// (<c>KafkaEventSink</c>) — <paramref name="sinkProvider"/> defaults to "no sink", and
/// <see cref="Publish"/>/<see cref="Burst"/> report "no publish target connected" rather than
/// doing nothing silently. This view model, its tree editing, its validation, and its preview
/// are step 9's whole scope; wiring a real sink in is step 10's.</para>
/// </summary>
public partial class PublisherViewModel : ObservableObject
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly PublisherTreeModel _tree;
    private readonly GenerationRunner _runner;
    private readonly Func<IEventSink?> _sinkProvider;
    private CancellationTokenSource? _debounceCts;

    public ObservableCollection<PublisherNode> Rows => _tree.FlattenedRows;

    [ObservableProperty]
    public partial IReadOnlyList<PreviewLine> PreviewLines { get; set; } = [];

    /// <summary>Empty when the current plan has no cycles or unresolved refs — the build
    /// plan's own "Invalid: unresolved {{ref:$.missing}} at line 8" wording, rendered here
    /// rather than only in a hidden diagnostics object.</summary>
    [ObservableProperty]
    public partial string ValidationText { get; set; } = string.Empty;

    public bool HasValidationIssue => ValidationText.Length > 0;

    partial void OnValidationTextChanged(string value) => OnPropertyChanged(nameof(HasValidationIssue));

    [ObservableProperty]
    public partial string ContentType { get; set; } = "application/json";

    [ObservableProperty]
    public partial string PartitionKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SessionId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CorrelationId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int BurstCount { get; set; } = 1;

    [ObservableProperty]
    public partial string PublishStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; } // 0 = preview, 1 = envelope

    public bool IsPreviewTabSelected => SelectedTabIndex == 0;

    public bool IsEnvelopeTabSelected => SelectedTabIndex == 1;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPreviewTabSelected));
        OnPropertyChanged(nameof(IsEnvelopeTabSelected));
    }

    public PublisherViewModel(
        PublisherTreeModel? tree = null,
        Func<IEventSink?>? sinkProvider = null,
        TimeProvider? timeProvider = null)
    {
        _tree = tree ?? new PublisherTreeModel();
        _sinkProvider = sinkProvider ?? (() => null);
        _runner = new GenerationRunner(timeProvider);
        _tree.Changed += ScheduleRecompute;
        Recompute();
    }

    [RelayCommand]
    private void SelectPreviewTab() => SelectedTabIndex = 0;

    [RelayCommand]
    private void SelectEnvelopeTab() => SelectedTabIndex = 1;

    [RelayCommand]
    private void AddField() => _tree.AddField(_tree.Root, NextDefaultKey());

    [RelayCommand]
    private void DeleteField(PublisherNode? node)
    {
        if (node is not null) _tree.Delete(node);
    }

    [RelayCommand]
    private void Regenerate() => Recompute();

    [RelayCommand]
    private async Task PublishAsync()
    {
        Recompute();
        if (HasValidationIssue)
        {
            PublishStatus = "Fix validation issues before publishing.";
            return;
        }

        var sink = _sinkProvider();
        if (sink is null)
        {
            PublishStatus = "No publish target connected.";
            return;
        }

        try
        {
            await sink.PublishAsync(BuildOutgoingMessage(), CancellationToken.None).ConfigureAwait(true);
            PublishStatus = "Published.";
        }
        catch (Exception ex)
        {
            PublishStatus = $"Publish failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task BurstAsync()
    {
        var sink = _sinkProvider();
        if (sink is null)
        {
            PublishStatus = "No publish target connected.";
            return;
        }

        if (BurstCount <= 0) return;

        var succeeded = 0;
        for (var i = 0; i < BurstCount; i++)
        {
            // One Fill per copy, per build plan §3.5's own description of a burst — the plan
            // is cached (built once above, in Recompute's own GenerationPlanner.Build call
            // history), only the fill re-runs.
            Recompute();
            if (HasValidationIssue) break;

            await sink.PublishAsync(BuildOutgoingMessage(), CancellationToken.None).ConfigureAwait(true);
            succeeded++;
        }

        PublishStatus = $"Published {succeeded}/{BurstCount}.";
    }

    /// <summary>Rebuilds the generation plan and fills it — public so tests (and
    /// <see cref="Regenerate"/>/<see cref="PublishAsync"/>) can force it synchronously instead
    /// of waiting on <see cref="ScheduleRecompute"/>'s debounce.</summary>
    public void Recompute()
    {
        var plan = GenerationPlanner.Build(_tree.CollectLeafTemplates());
        var values = _runner.Fill(plan);
        _tree.ApplyValues(plan, values);
        ValidationText = BuildValidationText(plan.Diagnostics);
        PreviewLines = PreviewBuilder.Build(_tree.ToJson());
    }

    private void ScheduleRecompute()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = RecomputeAfterDebounceAsync(cts);
    }

    private async Task RecomputeAfterDebounceAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(Debounce, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer edit
        }

        if (cts.IsCancellationRequested) return;
        Recompute();
    }

    private OutgoingMessage BuildOutgoingMessage() => new()
    {
        Body = _tree.ToJson()!,
        ContentType = string.IsNullOrEmpty(ContentType) ? null : ContentType,
        PartitionKey = string.IsNullOrEmpty(PartitionKey) ? null : PartitionKey,
        SessionId = string.IsNullOrEmpty(SessionId) ? null : SessionId,
        CorrelationId = string.IsNullOrEmpty(CorrelationId) ? null : CorrelationId,
    };

    private string NextDefaultKey()
    {
        var n = _tree.Root.Children.Count + 1;
        var candidate = $"field{n}";
        while (_tree.Root.Children.Any(c => c.Key == candidate))
        {
            n++;
            candidate = $"field{n}";
        }

        return candidate;
    }

    private static string BuildValidationText(PlanDiagnostics diagnostics)
    {
        if (!diagnostics.HasIssues) return string.Empty;

        var parts = new List<string>();
        foreach (var unresolved in diagnostics.Unresolved)
        {
            parts.Add($"Invalid: unresolved {{{{ref:{unresolved.TargetPath}}}}} at line {unresolved.Span.Line}");
        }

        foreach (var cycle in diagnostics.Cycles)
        {
            var walk = string.Join(" → ", cycle.Hops.Select(h => h.FromPath).Append(cycle.Hops[0].FromPath));
            parts.Add($"Invalid: cycle {walk}");
        }

        return string.Join("; ", parts);
    }
}
