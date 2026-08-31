namespace EventScope.Core.Generation;

/// <summary>
/// Splits one leaf's generator template into literal and token segments (build plan §3.5
/// pass 1: "lex every leaf's generator to tokens"). A template mixes literal text with
/// <c>{{...}}</c> tokens freely, e.g. <c>"order-{{ref:$.id}}-{{guid}}"</c>.
///
/// <para>Grammar (not specified verbatim by the build plan — filled in here): a token is
/// <c>{{kind}}</c> or <c>{{kind:argument}}</c> where <c>kind</c> is one of
/// <c>ref</c>/<c>guid</c>/<c>int</c>/<c>pick</c>/<c>now</c> (case-insensitive). An unknown
/// kind, or an unterminated <c>{{</c>, is treated as literal text rather than rejected — a
/// template with a typo should still round-trip visibly rather than vanish.</para>
/// </summary>
public static class GeneratorLexer
{
    public static IReadOnlyList<TemplateSegment> Lex(string template)
    {
        var segments = new List<TemplateSegment>();
        var literalStart = 0;
        var i = 0;

        while (i < template.Length)
        {
            if (template[i] != '{' || i + 1 >= template.Length || template[i + 1] != '{')
            {
                i++;
                continue;
            }

            var close = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                i++; // unterminated "{{" - treat as ordinary text
                continue;
            }

            if (i > literalStart)
            {
                AddLiteral(segments, template, literalStart, i);
            }

            var tokenStart = i;
            var tokenLength = close + 2 - i;
            var inner = template.AsSpan(i + 2, close - (i + 2));
            var colon = inner.IndexOf(':');
            var kindText = colon < 0 ? inner : inner[..colon];
            var argument = colon < 0 ? null : new string(inner[(colon + 1)..]);
            var span = new TextSpan(tokenStart, tokenLength, LineOf(template, tokenStart));

            if (TryParseKind(kindText, out var kind))
            {
                segments.Add(new TemplateSegment(kind, argument, span));
            }
            else
            {
                // Unrecognized token kind - keep the raw text visible rather than dropping it.
                segments.Add(new TemplateSegment(SegmentKind.Literal, template.Substring(tokenStart, tokenLength), span));
            }

            i = close + 2;
            literalStart = i;
        }

        if (literalStart < template.Length)
        {
            AddLiteral(segments, template, literalStart, template.Length);
        }

        return segments;
    }

    private static void AddLiteral(List<TemplateSegment> segments, string template, int start, int end)
    {
        var text = template[start..end];
        segments.Add(new TemplateSegment(SegmentKind.Literal, text, new TextSpan(start, end - start, LineOf(template, start))));
    }

    private static bool TryParseKind(ReadOnlySpan<char> kindText, out SegmentKind kind)
    {
        if (kindText.Equals("ref", StringComparison.OrdinalIgnoreCase)) { kind = SegmentKind.Ref; return true; }
        if (kindText.Equals("guid", StringComparison.OrdinalIgnoreCase)) { kind = SegmentKind.Guid; return true; }
        if (kindText.Equals("int", StringComparison.OrdinalIgnoreCase)) { kind = SegmentKind.Int; return true; }
        if (kindText.Equals("pick", StringComparison.OrdinalIgnoreCase)) { kind = SegmentKind.Pick; return true; }
        if (kindText.Equals("now", StringComparison.OrdinalIgnoreCase)) { kind = SegmentKind.Now; return true; }
        kind = default;
        return false;
    }

    private static int LineOf(string template, int position)
    {
        var line = 1;
        for (var i = 0; i < position; i++)
        {
            if (template[i] == '\n') line++;
        }
        return line;
    }
}
