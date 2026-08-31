using EventScope.Core.Generation;
using Xunit;

namespace EventScope.Core.Tests.Generation;

public sealed class GeneratorLexerTests
{
    [Fact]
    public void A_plain_string_is_a_single_literal_segment()
    {
        var segments = GeneratorLexer.Lex("hello world");

        var segment = Assert.Single(segments);
        Assert.Equal(SegmentKind.Literal, segment.Kind);
        Assert.Equal("hello world", segment.Text);
    }

    [Fact]
    public void Literal_text_and_tokens_interleave_correctly()
    {
        var segments = GeneratorLexer.Lex("order-{{ref:$.id}}-{{guid}}");

        Assert.Equal(4, segments.Count);
        Assert.Equal((SegmentKind.Literal, "order-"), (segments[0].Kind, segments[0].Text));
        Assert.Equal((SegmentKind.Ref, "$.id"), (segments[1].Kind, segments[1].Text));
        Assert.Equal((SegmentKind.Literal, "-"), (segments[2].Kind, segments[2].Text));
        Assert.Equal((SegmentKind.Guid, (string?)null), (segments[3].Kind, segments[3].Text));
    }

    [Fact]
    public void Token_kind_is_case_insensitive()
    {
        var segments = GeneratorLexer.Lex("{{REF:$.a}}");

        Assert.Equal(SegmentKind.Ref, Assert.Single(segments).Kind);
    }

    [Fact]
    public void An_unrecognized_token_kind_is_kept_as_literal_text()
    {
        var segments = GeneratorLexer.Lex("{{bogus}}");

        var segment = Assert.Single(segments);
        Assert.Equal(SegmentKind.Literal, segment.Kind);
        Assert.Equal("{{bogus}}", segment.Text);
    }

    [Fact]
    public void An_unterminated_token_is_kept_as_literal_text()
    {
        var segments = GeneratorLexer.Lex("prefix {{guid not closed");

        var segment = Assert.Single(segments);
        Assert.Equal(SegmentKind.Literal, segment.Kind);
        Assert.Equal("prefix {{guid not closed", segment.Text);
    }

    [Fact]
    public void A_tokens_span_covers_the_full_braces_and_reports_its_line()
    {
        var segments = GeneratorLexer.Lex("line one\nline two {{ref:$.x}} tail");

        var refSegment = Assert.Single(segments, s => s.Kind == SegmentKind.Ref);
        var expectedStart = "line one\nline two ".Length;
        Assert.Equal(expectedStart, refSegment.Span.Start);
        Assert.Equal("{{ref:$.x}}".Length, refSegment.Span.Length);
        Assert.Equal(2, refSegment.Span.Line);
    }

    [Theory]
    [InlineData("{{int}}", SegmentKind.Int, null)]
    [InlineData("{{int:1..100}}", SegmentKind.Int, "1..100")]
    [InlineData("{{pick:a|b|c}}", SegmentKind.Pick, "a|b|c")]
    [InlineData("{{now}}", SegmentKind.Now, null)]
    [InlineData("{{now:iso}}", SegmentKind.Now, "iso")]
    public void Token_argument_is_split_on_the_first_colon(string template, SegmentKind kind, string? argument)
    {
        var segment = Assert.Single(GeneratorLexer.Lex(template));

        Assert.Equal(kind, segment.Kind);
        Assert.Equal(argument, segment.Text);
    }
}
