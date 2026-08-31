using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EventScope.App.Publisher;

/// <summary>
/// One node of the publisher's editable JSON tree (build plan §5 M3: "JsonNode-backed tree
/// with an observable flattened projection"). <see cref="Generator"/> is the leaf's editable
/// template string (fed to <see cref="EventScope.Core.Generation.GenerationPlanner"/> — a
/// plain literal like <c>"hello"</c> or a token expression like <c>"{{guid}}"</c>);
/// <see cref="Value"/> is a read-only preview of what that template last resolved to, kept in
/// sync by <see cref="EventScope.App.ViewModels.PublisherViewModel.Recompute"/> rather than
/// independently editable — the mockup draws Value as an input, but letting a literal Value
/// and a separate Generator template disagree about which one wins has no specified
/// precedence, so this collapses the two into "edit the template, watch the value."
/// Meaningless for <see cref="PublisherFieldType.Object"/>/<see cref="PublisherFieldType.Array"/>,
/// which carry their content in <see cref="Children"/> instead.
/// </summary>
public partial class PublisherNode : ObservableObject
{
    public PublisherNode? Parent { get; }

    public int Depth { get; }

    /// <summary>True for an element of a JSON array — its <see cref="Key"/> is not
    /// user-editable; its position in <see cref="Parent"/>'s children is its identity.</summary>
    public bool IsArrayElement { get; }

    public ObservableCollection<PublisherNode> Children { get; } = [];

    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PublisherFieldType Type { get; set; } = PublisherFieldType.String;

    [ObservableProperty]
    public partial string Generator { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    public bool IsContainer => Type is PublisherFieldType.Object or PublisherFieldType.Array;

    partial void OnTypeChanged(PublisherFieldType value) => OnPropertyChanged(nameof(IsContainer));

    public PublisherNode(PublisherNode? parent, int depth, bool isArrayElement = false)
    {
        Parent = parent;
        Depth = depth;
        IsArrayElement = isArrayElement;
    }

    /// <summary>A JSON-path-shaped identity (<c>$.a.b[0]</c>) used both as the key the
    /// generator engine indexes leaves by and as the display label for array elements.
    /// Computed on demand rather than cached — trees here are small and edited interactively,
    /// so a stale cached path would be a worse bug than the recomputation cost.</summary>
    public string JsonPath
    {
        get
        {
            if (Parent is null) return "$";

            var parentPath = Parent.JsonPath;
            return IsArrayElement
                ? $"{parentPath}[{Parent.Children.IndexOf(this)}]"
                : $"{parentPath}.{Key}";
        }
    }
}
