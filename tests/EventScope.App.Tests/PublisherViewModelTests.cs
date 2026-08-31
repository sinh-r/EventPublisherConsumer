using EventScope.App.Publisher;
using EventScope.App.ViewModels;
using EventScope.Core.Abstractions;
using EventScope.Core.Models;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>No Avalonia dependency — plain view-model logic, needs no <see cref="HeadlessFixture"/>.
/// <see cref="PublisherViewModel.Recompute"/> is called directly rather than waiting on the
/// 150 ms debounce, exactly why it is public.</summary>
public sealed class PublisherViewModelTests
{
    private sealed class RecordingSink : IEventSink
    {
        public List<OutgoingMessage> Published { get; } = [];
        public SourceCapabilities Capabilities { get; } = new()
        {
            CanPeekNonDestructively = true,
            SupportsPartitions = false,
            SupportsSubscriptions = false,
            SupportsSessions = false,
            SupportsDeadLetterQueue = false,
            SupportsReplay = false,
            SupportsOffsetCommit = false,
        };

        public Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken)
        {
            Published.Add(message);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void An_unresolved_ref_surfaces_as_inline_validation_text_with_its_line()
    {
        var tree = new PublisherTreeModel();
        var field = tree.AddField(tree.Root, "id");
        field.Generator = "{{ref:$.missing}}";
        var vm = new PublisherViewModel(tree);

        vm.Recompute();

        Assert.True(vm.HasValidationIssue);
        Assert.Contains("unresolved", vm.ValidationText);
        Assert.Contains("$.missing", vm.ValidationText);
    }

    [Fact]
    public void A_cycle_surfaces_as_inline_validation_text()
    {
        var tree = new PublisherTreeModel();
        var a = tree.AddField(tree.Root, "a");
        a.Generator = "{{ref:$.b}}";
        var b = tree.AddField(tree.Root, "b");
        b.Generator = "{{ref:$.a}}";
        var vm = new PublisherViewModel(tree);

        vm.Recompute();

        Assert.True(vm.HasValidationIssue);
        Assert.Contains("cycle", vm.ValidationText);
    }

    [Fact]
    public void A_valid_tree_has_no_validation_issue()
    {
        var tree = new PublisherTreeModel();
        tree.AddField(tree.Root, "id").Generator = "{{guid}}";
        var vm = new PublisherViewModel(tree);

        vm.Recompute();

        Assert.False(vm.HasValidationIssue);
        Assert.Equal(string.Empty, vm.ValidationText);
    }

    [Fact]
    public void Editing_a_node_updates_the_flattened_rows_the_view_binds_to()
    {
        var tree = new PublisherTreeModel();
        var vm = new PublisherViewModel(tree);

        vm.AddFieldCommand.Execute(null);

        Assert.Single(vm.Rows);
    }

    [Fact]
    public void DeleteField_removes_the_row()
    {
        var tree = new PublisherTreeModel();
        var vm = new PublisherViewModel(tree);
        vm.AddFieldCommand.Execute(null);
        var node = vm.Rows[0];

        vm.DeleteFieldCommand.Execute(node);

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Publish_sends_the_generated_body_to_the_configured_sink()
    {
        var tree = new PublisherTreeModel();
        tree.AddField(tree.Root, "id").Generator = "fixed-value";
        var sink = new RecordingSink();
        var vm = new PublisherViewModel(tree, () => sink);

        await vm.PublishCommand.ExecuteAsync(null);

        var message = Assert.Single(sink.Published);
        Assert.Equal("fixed-value", message.Body!["id"]!.GetValue<string>());
        Assert.Equal("Published.", vm.PublishStatus);
    }

    [Fact]
    public async Task Publish_without_a_configured_sink_reports_no_target_rather_than_throwing()
    {
        var tree = new PublisherTreeModel();
        tree.AddField(tree.Root, "id").Generator = "{{guid}}";
        var vm = new PublisherViewModel(tree);

        await vm.PublishCommand.ExecuteAsync(null);

        Assert.Equal("No publish target connected.", vm.PublishStatus);
    }

    [Fact]
    public async Task Publish_with_an_unresolved_ref_refuses_rather_than_publishing_a_broken_message()
    {
        var tree = new PublisherTreeModel();
        tree.AddField(tree.Root, "id").Generator = "{{ref:$.missing}}";
        var sink = new RecordingSink();
        var vm = new PublisherViewModel(tree, () => sink);

        await vm.PublishCommand.ExecuteAsync(null);

        Assert.Empty(sink.Published);
        Assert.Equal("Fix validation issues before publishing.", vm.PublishStatus);
    }

    [Fact]
    public async Task Burst_publishes_the_requested_count_with_a_fresh_guid_each_time()
    {
        var tree = new PublisherTreeModel();
        tree.AddField(tree.Root, "id").Generator = "{{guid}}";
        var sink = new RecordingSink();
        var vm = new PublisherViewModel(tree, () => sink) { BurstCount = 5 };

        await vm.BurstCommand.ExecuteAsync(null);

        Assert.Equal(5, sink.Published.Count);
        var ids = sink.Published.Select(m => m.Body!["id"]!.GetValue<string>()).ToHashSet();
        Assert.Equal(5, ids.Count);
    }

    [Fact]
    public void LoadFromConsumedMessage_replaces_the_tree_and_infers_generators()
    {
        var vm = new PublisherViewModel();

        vm.LoadFromConsumedMessage(System.Text.Json.Nodes.JsonNode.Parse(
            """{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}"""));

        var row = Assert.Single(vm.Rows);
        Assert.Equal("{{guid}}", row.Generator);
        Assert.False(vm.HasValidationIssue);
    }

    [Fact]
    public void Selecting_the_envelope_tab_updates_the_selected_flags()
    {
        var vm = new PublisherViewModel();

        vm.SelectEnvelopeTabCommand.Execute(null);
        Assert.True(vm.IsEnvelopeTabSelected);
        Assert.False(vm.IsPreviewTabSelected);

        vm.SelectPreviewTabCommand.Execute(null);
        Assert.True(vm.IsPreviewTabSelected);
        Assert.False(vm.IsEnvelopeTabSelected);
    }

    [Fact]
    public async Task Publish_carries_envelope_fields_set_on_the_view_model()
    {
        var tree = new PublisherTreeModel();
        var sink = new RecordingSink();
        var vm = new PublisherViewModel(tree, () => sink)
        {
            PartitionKey = "region-1",
            CorrelationId = "corr-123",
        };

        await vm.PublishCommand.ExecuteAsync(null);

        var message = Assert.Single(sink.Published);
        Assert.Equal("region-1", message.PartitionKey);
        Assert.Equal("corr-123", message.CorrelationId);
    }
}
