using EventScope.App.Publisher;
using Xunit;

namespace EventScope.App.Tests;

public sealed class SchemaInferenceTests
{
    [Fact]
    public void A_guid_shaped_string_infers_the_guid_token()
    {
        var generator = SchemaInference.InferGenerator("3fa85f64-5717-4562-b3fc-2c963f66afa6", PublisherFieldType.String);

        Assert.Equal("{{guid}}", generator);
    }

    [Fact]
    public void An_iso8601_shaped_string_infers_the_now_iso_token()
    {
        var generator = SchemaInference.InferGenerator("2026-08-31T12:00:00Z", PublisherFieldType.String);

        Assert.Equal("{{now:iso}}", generator);
    }

    [Fact]
    public void An_ordinary_string_is_left_as_a_literal()
    {
        var generator = SchemaInference.InferGenerator("Springfield", PublisherFieldType.String);

        Assert.Equal("Springfield", generator);
    }

    [Fact]
    public void A_whole_number_infers_an_int_range_that_brackets_the_observed_value()
    {
        var generator = SchemaInference.InferGenerator("42", PublisherFieldType.Number);

        Assert.StartsWith("{{int:", generator);
        var (min, max) = ParseRange(generator);
        Assert.InRange(42, min, max);
    }

    [Fact]
    public void A_negative_number_infers_a_range_that_still_brackets_it()
    {
        var generator = SchemaInference.InferGenerator("-7", PublisherFieldType.Number);

        var (min, max) = ParseRange(generator);
        Assert.InRange(-7, min, max);
    }

    [Fact]
    public void A_fractional_number_is_left_as_a_literal_rather_than_bracketed_as_an_int()
    {
        var generator = SchemaInference.InferGenerator("3.14", PublisherFieldType.Number);

        Assert.Equal("3.14", generator);
    }

    private static (long Min, long Max) ParseRange(string generator)
    {
        var inner = generator["{{int:".Length..^2];
        var parts = inner.Split("..");
        return (long.Parse(parts[0]), long.Parse(parts[1]));
    }
}
