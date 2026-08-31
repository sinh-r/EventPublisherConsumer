using System.Text;

namespace EventScope.Core.Generation;

/// <summary>
/// Build plan §3.5 pass 2: fills every leaf's generated value from a <see cref="GenerationPlan"/>.
/// Reuses a <c>string?[]</c> indexed by leaf index and a scratch <see cref="StringBuilder"/>
/// across calls — "a burst of 1,000 is one plan plus 1,000 <c>Fill</c> calls", which is what
/// makes repeated fills of the same plan cheap. Not thread-safe; one runner per publish
/// session, same as everything else that owns mutable scratch state in this codebase.
/// </summary>
public sealed class GenerationRunner(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly StringBuilder _scratch = new();
    private string?[] _values = [];

    /// <summary>Fills every leaf and returns the values indexed the same way as
    /// <see cref="GenerationPlan.Leaves"/>. The returned array is reused on the next call —
    /// copy it out before calling <see cref="Fill"/> again if the caller needs to keep it.
    /// A leaf that is itself part of (or depends on) a reported cycle may read a not-yet-filled
    /// dependency as empty — <see cref="GenerationPlan.Diagnostics"/> is how the caller finds
    /// out before publish, not a runtime exception.</summary>
    public IReadOnlyList<string?> Fill(GenerationPlan plan)
    {
        if (_values.Length != plan.Leaves.Count)
        {
            _values = new string?[plan.Leaves.Count];
        }
        else
        {
            Array.Clear(_values);
        }

        foreach (var nodeIndex in plan.FillOrder)
        {
            _values[nodeIndex] = Resolve(plan, nodeIndex);
        }

        return _values;
    }

    private string Resolve(GenerationPlan plan, int nodeIndex)
    {
        var segments = plan.Segments[nodeIndex];

        if (segments.Count == 1 && segments[0].Kind == SegmentKind.Literal)
        {
            return segments[0].Text ?? string.Empty;
        }

        _scratch.Clear();
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Literal:
                    _scratch.Append(segment.Text);
                    break;
                case SegmentKind.Ref:
                    if (segment.Text is not null && plan.IndexByPath.TryGetValue(segment.Text, out var targetIndex))
                    {
                        _scratch.Append(_values[targetIndex]);
                    }
                    break;
                case SegmentKind.Guid:
                    _scratch.Append(Guid.CreateVersion7());
                    break;
                case SegmentKind.Int:
                    _scratch.Append(NextInt(segment.Text));
                    break;
                case SegmentKind.Pick:
                    _scratch.Append(PickOne(segment.Text));
                    break;
                case SegmentKind.Now:
                    _scratch.Append(FormatNow(segment.Text));
                    break;
            }
        }

        return _scratch.ToString();
    }

    private static int NextInt(string? argument)
    {
        var (min, max) = ParseIntRange(argument);
        return Random.Shared.Next(min, max + 1); // inclusive upper bound, matching the min..max syntax
    }

    private static (int Min, int Max) ParseIntRange(string? argument)
    {
        const int defaultMin = 0;
        const int defaultMax = 1_000_000;

        if (string.IsNullOrEmpty(argument)) return (defaultMin, defaultMax);

        var separator = argument.IndexOf("..", StringComparison.Ordinal);
        if (separator < 0) return (defaultMin, defaultMax);

        var minText = argument[..separator];
        var maxText = argument[(separator + 2)..];
        if (!int.TryParse(minText, out var min) || !int.TryParse(maxText, out var max) || min > max)
        {
            return (defaultMin, defaultMax);
        }

        return (min, max);
    }

    private static string PickOne(string? argument)
    {
        if (string.IsNullOrEmpty(argument)) return string.Empty;

        var options = argument.Split('|');
        return options[Random.Shared.Next(options.Length)];
    }

    private string FormatNow(string? argument)
    {
        var now = _time.GetUtcNow();
        if (string.IsNullOrEmpty(argument) || argument.Equals("iso", StringComparison.OrdinalIgnoreCase))
        {
            return now.ToString("O");
        }

        return now.ToString(argument);
    }
}
