using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EventScope.App.Publisher;

public enum PreviewSegmentKind
{
    Key,
    Punctuation,
    String,
    Number,
    Literal,
}

/// <summary>
/// The four <see cref="IsKey"/>/<see cref="IsString"/>/... booleans exist so the view can bind
/// <c>Classes.key="{Binding IsKey}"</c> per segment — the same declarative
/// Classes-binding-drives-styling pattern already proven safe at scale for the message grid's
/// <c>large</c>/<c>searchHit</c> cell classes (see <c>RowStateClassSync</c>'s remarks for why
/// an imperative alternative was rejected), rather than a converter mapping
/// <see cref="PreviewSegmentKind"/> straight to a theme brush.
/// </summary>
public sealed record PreviewSegment(string Text, PreviewSegmentKind Kind)
{
    public bool IsKey => Kind == PreviewSegmentKind.Key;
    public bool IsString => Kind == PreviewSegmentKind.String;
    public bool IsNumber => Kind == PreviewSegmentKind.Number;
    public bool IsLiteral => Kind == PreviewSegmentKind.Literal;
    public bool IsPunctuation => Kind == PreviewSegmentKind.Punctuation;
}

/// <summary>One line of the publisher preview pane's coloured JSON (build plan §4.5: key
/// <c>Accent</c>, string <c>Green</c>, number <c>Amber</c>, literal/punctuation <c>Muted</c>).
/// <see cref="Indent"/> counts indent levels (2 spaces each, from <c>JsonSerializer</c>'s
/// default indent), not raw characters.</summary>
public sealed record PreviewLine(int LineNumber, int Indent, IReadOnlyList<PreviewSegment> Segments);

/// <summary>
/// Turns a <see cref="JsonNode"/> into coloured preview lines by pretty-printing it and then
/// classifying each line — simpler and less error-prone than hand-rolling a second JSON
/// writer that also tracks line numbers, at the cost of a regex pass per line. Publisher
/// messages are small (interactive editing, not the ingest hot path), so this is not a
/// perf-sensitive path the way anything in <c>EventScope.Storage</c> is.
/// </summary>
public static class PreviewBuilder
{
    // Captures: leading whitespace, an optional "key": prefix, the value/punctuation text,
    // and a trailing comma. `(?<value>.*?)` is lazy so a trailing comma isn't swallowed into it.
    private static readonly Regex LinePattern = new(
        """^(?<indent>[ ]*)(?:"(?<key>(?:[^"\\]|\\.)*)"\s*:\s*)?(?<value>.*?)(?<comma>,)?$""",
        RegexOptions.Compiled);

    public static IReadOnlyList<PreviewLine> Build(JsonNode? json)
    {
        var text = json is null
            ? "null"
            : json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        var lines = text.Split('\n');
        var result = new List<PreviewLine>(lines.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var match = LinePattern.Match(line);
            var indentLevel = match.Groups["indent"].Length / 2;
            var segments = new List<PreviewSegment>();

            if (match.Groups["key"].Success)
            {
                segments.Add(new PreviewSegment(match.Groups["key"].Value, PreviewSegmentKind.Key));
                segments.Add(new PreviewSegment(": ", PreviewSegmentKind.Punctuation));
            }

            var value = match.Groups["value"].Value;
            if (value.Length > 0)
            {
                segments.Add(new PreviewSegment(value, ClassifyValue(value)));
            }

            if (match.Groups["comma"].Success)
            {
                segments.Add(new PreviewSegment(",", PreviewSegmentKind.Punctuation));
            }

            result.Add(new PreviewLine(i + 1, indentLevel, segments));
        }

        return result;
    }

    private static PreviewSegmentKind ClassifyValue(string value)
    {
        if (value.Length == 0) return PreviewSegmentKind.Punctuation;
        if (value[0] == '"') return PreviewSegmentKind.String;
        if (value is "true" or "false" or "null") return PreviewSegmentKind.Literal;
        if (double.TryParse(value, out _)) return PreviewSegmentKind.Number;
        return PreviewSegmentKind.Punctuation; // "{", "}", "[", "]" and similar structural tokens
    }
}
