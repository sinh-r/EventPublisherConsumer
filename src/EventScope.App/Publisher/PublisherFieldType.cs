namespace EventScope.App.Publisher;

public enum PublisherFieldType
{
    String,
    Number,
    Boolean,
    Null,
    Object,
    Array,
}

/// <summary>A stable, enum-order array for the tree editor's type dropdown to bind
/// <c>ItemsSource</c> to directly, rather than re-deriving it via reflection in XAML.</summary>
public static class PublisherFieldTypes
{
    public static readonly PublisherFieldType[] All = Enum.GetValues<PublisherFieldType>();
}
