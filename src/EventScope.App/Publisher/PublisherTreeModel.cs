using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using EventScope.Core.Generation;

namespace EventScope.App.Publisher;

/// <summary>
/// Owns one publisher message's editable tree and its flattened projection (build plan §5
/// M3). <see cref="FlattenedRows"/> is a depth-first walk of every non-root node, kept in
/// sync on every structural change (<see cref="AddField"/>/<see cref="Delete"/>) and on any
/// node's <see cref="PublisherNode.Key"/>/<see cref="PublisherNode.Type"/>/
/// <see cref="PublisherNode.Generator"/> edit — this is what a <c>DataGrid</c> or
/// <c>ItemsControl</c> binds to for a tree that renders as a flat, indented row list, matching
/// the mockup's own actual markup (a flat <c>sc-for</c> with per-row indent guides, not a
/// collapsible tree control).
/// </summary>
public sealed class PublisherTreeModel
{
    public PublisherNode Root { get; }

    public ObservableCollection<PublisherNode> FlattenedRows { get; } = [];

    /// <summary>Fires after any edit that could change generation output — key, type,
    /// generator text, or tree structure. <see cref="ViewModels.PublisherViewModel"/>
    /// debounces this into a plan recompute.</summary>
    public event Action? Changed;

    public PublisherTreeModel(PublisherNode? root = null)
    {
        Root = root ?? new PublisherNode(null, 0) { Type = PublisherFieldType.Object };
        SubscribeAll(Root);
        Rebuild();
    }

    public PublisherNode AddField(PublisherNode parent, string key, PublisherFieldType type = PublisherFieldType.String)
    {
        var node = new PublisherNode(parent, parent.Depth + 1) { Key = key, Type = type };
        SubscribeAll(node);
        parent.Children.Add(node);
        Rebuild();
        Changed?.Invoke();
        return node;
    }

    public void Delete(PublisherNode node)
    {
        node.Parent?.Children.Remove(node);
        Rebuild();
        Changed?.Invoke();
    }

    public void Rebuild()
    {
        FlattenedRows.Clear();
        Flatten(Root);
    }

    private void Flatten(PublisherNode node)
    {
        foreach (var child in node.Children)
        {
            FlattenedRows.Add(child);
            if (child.IsContainer) Flatten(child);
        }
    }

    private void SubscribeAll(PublisherNode node)
    {
        node.PropertyChanged += OnNodePropertyChanged;
        foreach (var child in node.Children) SubscribeAll(child);
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PublisherNode.Key) or nameof(PublisherNode.Type) or nameof(PublisherNode.Generator)))
        {
            return;
        }

        Rebuild(); // a key rename or a leaf becoming a container changes the flattened shape
        Changed?.Invoke();
    }

    /// <summary>Every primitive leaf in the tree, keyed by its <see cref="PublisherNode.JsonPath"/>
    /// — exactly the input <see cref="GenerationPlanner.Build"/> needs. Containers contribute
    /// no leaf of their own, only their descendants do.</summary>
    public IReadOnlyList<LeafTemplate> CollectLeafTemplates()
    {
        var leaves = new List<LeafTemplate>();
        CollectLeaves(Root, leaves);
        return leaves;
    }

    private static void CollectLeaves(PublisherNode node, List<LeafTemplate> leaves)
    {
        foreach (var child in node.Children)
        {
            if (child.IsContainer)
            {
                CollectLeaves(child, leaves);
            }
            else
            {
                leaves.Add(new LeafTemplate(child.JsonPath, child.Generator));
            }
        }
    }

    /// <summary>Writes each leaf's freshly-generated value back onto its node's
    /// <see cref="PublisherNode.Value"/> for display — the live preview build plan §3.5
    /// describes, applied to the tree rather than to a flat array the UI can't bind to.</summary>
    public void ApplyValues(GenerationPlan plan, IReadOnlyList<string?> values)
    {
        foreach (var row in FlattenedRows)
        {
            if (row.IsContainer) continue;
            if (plan.IndexByPath.TryGetValue(row.JsonPath, out var index))
            {
                row.Value = values[index] ?? string.Empty;
            }
        }
    }

    public JsonNode? ToJson() => BuildJson(Root);

    private static JsonNode? BuildJson(PublisherNode node)
    {
        switch (node.Type)
        {
            case PublisherFieldType.Object:
                var obj = new JsonObject();
                foreach (var child in node.Children) obj[child.Key] = BuildJson(child);
                return obj;
            case PublisherFieldType.Array:
                var arr = new JsonArray();
                foreach (var child in node.Children) arr.Add(BuildJson(child));
                return arr;
            case PublisherFieldType.Number:
                return double.TryParse(node.Value, out var number) ? JsonValue.Create(number) : JsonValue.Create(node.Value);
            case PublisherFieldType.Boolean:
                return JsonValue.Create(bool.TryParse(node.Value, out var boolean) && boolean);
            case PublisherFieldType.Null:
                return null;
            case PublisherFieldType.String:
            default:
                return JsonValue.Create(node.Value);
        }
    }

    /// <summary>Builds a tree from an existing JSON document — the seam schema inference
    /// (build plan §5 M3 step 10, "Use as publish template") plugs into; every leaf's
    /// <see cref="PublisherNode.Generator"/> starts as the observed literal value verbatim,
    /// so an untouched template still publishes exactly what was consumed.</summary>
    public static PublisherTreeModel FromJson(JsonNode? json)
    {
        var root = new PublisherNode(null, 0);
        PopulateFrom(root, json);
        return new PublisherTreeModel(root);
    }

    private static void PopulateFrom(PublisherNode node, JsonNode? json)
    {
        switch (json)
        {
            case JsonObject obj:
                node.Type = PublisherFieldType.Object;
                foreach (var (key, value) in obj)
                {
                    var child = new PublisherNode(node, node.Depth + 1) { Key = key };
                    PopulateFrom(child, value);
                    node.Children.Add(child);
                }
                break;

            case JsonArray arr:
                node.Type = PublisherFieldType.Array;
                foreach (var value in arr)
                {
                    var child = new PublisherNode(node, node.Depth + 1, isArrayElement: true);
                    PopulateFrom(child, value);
                    node.Children.Add(child);
                }
                break;

            case JsonValue value:
                node.Type = value.GetValueKind() switch
                {
                    JsonValueKind.Number => PublisherFieldType.Number,
                    JsonValueKind.True or JsonValueKind.False => PublisherFieldType.Boolean,
                    _ => PublisherFieldType.String,
                };
                node.Value = value.ToJsonString().Trim('"');
                node.Generator = node.Value;
                break;

            default: // null or missing
                node.Type = PublisherFieldType.Null;
                break;
        }
    }
}
