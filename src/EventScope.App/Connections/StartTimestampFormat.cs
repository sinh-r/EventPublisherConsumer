using System.Globalization;

namespace EventScope.App.Connections;

/// <summary>
/// The one reading of a typed start timestamp, shared by the connection editor's
/// <c>Timestamp</c> start position and the toolbar's custom replay window.
///
/// <para>
/// Invariant and exact rather than locale-dependent: a start position that means a different
/// moment on a differently-configured machine is worse than one that refuses an ambiguous
/// input. Centralised so the two entry points cannot drift on what a typed date means — they
/// would otherwise disagree silently, and the only symptom would be a run that started
/// somewhere the user did not ask for.
/// </para>
/// </summary>
public static class StartTimestampFormat
{
    /// <summary>Accepted forms, most precise first. A bare date is midnight UTC.</summary>
    public static readonly string[] Formats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"];

    /// <summary>What to tell the user when <see cref="TryParseUtc"/> refuses. One message for
    /// both call sites, for the same reason the parse itself is shared.</summary>
    public const string Requirement = "Start timestamp must be UTC in the form yyyy-MM-dd HH:mm:ss.";

    /// <summary>Parses <paramref name="text"/> as a UTC instant. Surrounding whitespace is
    /// tolerated; anything else is refused rather than guessed at.</summary>
    public static bool TryParseUtc(string? text, out DateTime value) =>
        DateTime.TryParseExact(
            text?.Trim(),
            Formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
}
