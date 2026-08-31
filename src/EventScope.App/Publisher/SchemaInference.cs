using System.Globalization;
using System.Text.RegularExpressions;

namespace EventScope.App.Publisher;

/// <summary>
/// Infers a generator template for one leaf from its observed literal value (build plan §5
/// M3 step 10: "schema inference from a consumed message ... GUID regex → {{guid}},
/// ISO-8601 → {{now:iso}}, numeric → {{int:min..max}} bracketing the observed value") — the
/// engine behind "Use as publish template".
/// </summary>
public static partial class SchemaInference
{
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?$")]
    private static partial Regex Iso8601Pattern();

    public static string InferGenerator(string literalValue, PublisherFieldType type)
    {
        if (type == PublisherFieldType.String)
        {
            if (GuidPattern().IsMatch(literalValue)) return "{{guid}}";
            if (Iso8601Pattern().IsMatch(literalValue)) return "{{now:iso}}";
            return literalValue;
        }

        if (type == PublisherFieldType.Number &&
            double.TryParse(literalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            number == Math.Floor(number) &&
            number is >= long.MinValue and <= long.MaxValue)
        {
            var observed = (long)number;
            // "Bracketing the observed value" isn't specified more precisely than that - a
            // symmetric span at least as wide as the observed magnitude, floored at zero for
            // an observed value that was itself non-negative (most IDs/counts are), keeps a
            // generated burst plausible without guessing at a domain-specific range.
            var span = Math.Max(10, Math.Abs(observed));
            var min = observed >= 0 ? 0 : observed - span;
            var max = observed + span;
            return $"{{{{int:{min}..{max}}}}}";
        }

        return literalValue;
    }
}
