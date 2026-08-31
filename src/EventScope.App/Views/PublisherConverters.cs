using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace EventScope.App.Views;

/// <summary>Converts a <see cref="Publisher.PublisherNode.Depth"/> into a left margin for the
/// tree editor's indent guides (build plan §4.3: "16px per level").</summary>
public sealed class DepthToIndentConverter : IValueConverter
{
    public static readonly DepthToIndentConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int depth ? new Thickness(depth * 16, 0, 0, 0) : new Thickness(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Converts a <see cref="Publisher.PreviewLine.Indent"/> level into a pixel width for
/// the preview pane's indent guides (build plan §4.3: "13px" per level, distinct from the tree
/// editor's own 16px — the mockup uses different indent widths for the two panes).</summary>
public sealed class IndentToWidthConverter : IValueConverter
{
    public static readonly IndentToWidthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int indent ? (double)(indent * 13) : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
