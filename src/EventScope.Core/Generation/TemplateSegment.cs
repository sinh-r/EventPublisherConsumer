namespace EventScope.Core.Generation;

public enum SegmentKind
{
    /// <summary>Plain text, copied through unchanged.</summary>
    Literal,

    /// <summary>{{ref:$.path}} — resolves to another leaf's generated value.</summary>
    Ref,

    /// <summary>{{guid}} — <see cref="Guid.CreateVersion7"/>.</summary>
    Guid,

    /// <summary>{{int}} or {{int:min..max}} — <see cref="Random.Shared"/>, inclusive range.</summary>
    Int,

    /// <summary>{{pick:a|b|c}} — <see cref="Random.Shared"/> chooses one option.</summary>
    Pick,

    /// <summary>{{now}} or {{now:format}} — the current instant.</summary>
    Now,
}

/// <summary>One lexed piece of a leaf's generator template. <see cref="Text"/> holds the
/// literal text for <see cref="SegmentKind.Literal"/>, or the token's argument (the part
/// after the first ':') for every other kind — e.g. the JSON path for <see cref="SegmentKind.Ref"/>,
/// or null when the token took no argument (bare {{guid}}, {{int}}, {{now}}).</summary>
public sealed record TemplateSegment(SegmentKind Kind, string? Text, TextSpan Span);
