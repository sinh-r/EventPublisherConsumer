using System.Text.Json.Nodes;
using EventScope.App.Publisher;
using EventScope.Core.Generation;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>No Avalonia dependency — plain tree-model logic, needs no <see cref="HeadlessFixture"/>.</summary>
public sealed class PublisherTreeModelTests
{
    [Fact]
    public void AddField_appends_to_the_flattened_projection()
    {
        var tree = new PublisherTreeModel();

        var node = tree.AddField(tree.Root, "orderId");

        var row = Assert.Single(tree.FlattenedRows);
        Assert.Same(node, row);
        Assert.Equal("orderId", row.Key);
        Assert.Equal("$.orderId", row.JsonPath);
    }

    [Fact]
    public void A_nested_object_field_flattens_depth_first()
    {
        var tree = new PublisherTreeModel();
        var address = tree.AddField(tree.Root, "address", PublisherFieldType.Object);
        tree.AddField(address, "city");
        tree.AddField(tree.Root, "total");

        Assert.Equal(["address", "city", "total"], tree.FlattenedRows.Select(r => r.Key));
        Assert.Equal("$.address.city", tree.FlattenedRows[1].JsonPath);
    }

    [Fact]
    public void An_array_elements_path_uses_its_index_not_a_key()
    {
        var tree = new PublisherTreeModel();
        var tags = tree.AddField(tree.Root, "tags", PublisherFieldType.Array);
        var element = new PublisherNode(tags, tags.Depth + 1, isArrayElement: true);
        tags.Children.Add(element);
        tree.Rebuild();

        Assert.Equal("$.tags[0]", element.JsonPath);
    }

    [Fact]
    public void Delete_removes_the_node_and_its_row()
    {
        var tree = new PublisherTreeModel();
        var node = tree.AddField(tree.Root, "toRemove");

        tree.Delete(node);

        Assert.Empty(tree.FlattenedRows);
        Assert.Empty(tree.Root.Children);
    }

    [Fact]
    public void Renaming_a_key_updates_dependent_JsonPaths_and_raises_Changed()
    {
        var tree = new PublisherTreeModel();
        var node = tree.AddField(tree.Root, "old");
        var changedCount = 0;
        tree.Changed += () => changedCount++;

        node.Key = "renamed";

        Assert.Equal("$.renamed", node.JsonPath);
        Assert.True(changedCount > 0);
    }

    [Fact]
    public void CollectLeafTemplates_only_returns_primitive_leaves_keyed_by_path()
    {
        var tree = new PublisherTreeModel();
        var address = tree.AddField(tree.Root, "address", PublisherFieldType.Object);
        var city = tree.AddField(address, "city");
        city.Generator = "Springfield";
        var total = tree.AddField(tree.Root, "total");
        total.Generator = "42";

        var leaves = tree.CollectLeafTemplates();

        Assert.Equal(2, leaves.Count);
        Assert.Contains(leaves, l => l.Path == "$.address.city" && l.Template == "Springfield");
        Assert.Contains(leaves, l => l.Path == "$.total" && l.Template == "42");
    }

    [Fact]
    public void ApplyValues_writes_generated_values_onto_the_matching_nodes()
    {
        var tree = new PublisherTreeModel();
        var field = tree.AddField(tree.Root, "id");
        field.Generator = "{{guid}}";

        var plan = GenerationPlanner.Build(tree.CollectLeafTemplates());
        var values = new GenerationRunner().Fill(plan);
        tree.ApplyValues(plan, values);

        Assert.True(Guid.TryParse(field.Value, out _));
    }

    [Fact]
    public void ToJson_builds_an_object_tree_matching_field_types()
    {
        var tree = new PublisherTreeModel();
        var name = tree.AddField(tree.Root, "name");
        name.Value = "Ada";
        var age = tree.AddField(tree.Root, "age", PublisherFieldType.Number);
        age.Value = "37";
        var active = tree.AddField(tree.Root, "active", PublisherFieldType.Boolean);
        active.Value = "true";

        var json = tree.ToJson();

        Assert.Equal("Ada", json!["name"]!.GetValue<string>());
        Assert.Equal(37, json["age"]!.GetValue<double>());
        Assert.True(json["active"]!.GetValue<bool>());
    }

    [Fact]
    public void FromJson_round_trips_an_existing_document_with_generator_seeded_from_the_literal_value()
    {
        var original = JsonNode.Parse("""{"id":"abc","nested":{"n":1}}""");

        var tree = PublisherTreeModel.FromJson(original);

        Assert.Equal(["id", "nested", "n"], tree.FlattenedRows.Select(r => r.Key));
        var idNode = tree.FlattenedRows.Single(r => r.Key == "id");
        Assert.Equal("abc", idNode.Generator);
        Assert.Equal(PublisherFieldType.Object, tree.Root.Type);
    }

    [Fact]
    public void LoadFrom_with_inference_seeds_a_guid_shaped_leaf_with_the_guid_token()
    {
        var tree = new PublisherTreeModel();

        tree.LoadFrom(JsonNode.Parse("""{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}"""), inferGenerators: true);

        Assert.Equal("{{guid}}", tree.FlattenedRows.Single().Generator);
    }

    [Fact]
    public void LoadFrom_replaces_content_in_place_keeping_the_same_FlattenedRows_instance()
    {
        var tree = new PublisherTreeModel();
        tree.AddField(tree.Root, "old");
        var rowsInstance = tree.FlattenedRows;

        tree.LoadFrom(JsonNode.Parse("""{"new":"value"}"""));

        Assert.Same(rowsInstance, tree.FlattenedRows);
        Assert.Equal(["new"], tree.FlattenedRows.Select(r => r.Key));
    }
}
