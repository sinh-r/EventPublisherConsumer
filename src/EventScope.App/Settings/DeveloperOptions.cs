namespace EventScope.App.Settings;

/// <summary>
/// Affordances that exist for <i>developing</i> EventScope rather than for using it, gated on the
/// build configuration and the environment. Deliberately separate from <see cref="AppSettings"/>:
/// nothing here is persisted, surfaced in the settings view, or discoverable by a user.
/// </summary>
public static class DeveloperOptions
{
    /// <summary>Set to <c>1</c> to force <see cref="ShowFakeSource"/> on in any build.</summary>
    public const string FakeSourceVariable = "EVENTSCOPE_FAKE_SOURCE";

    /// <summary>
    /// Whether the built-in "Fake source" entry appears in the connection manager's list.
    ///
    /// <para>
    /// It is the synthetic <see cref="Core.Ingest.FakeEventSource"/> stream, and it exists so the
    /// app can be built and exercised with no broker anywhere near it. In a build handed to
    /// somebody else it is a saved connection that looks real, produces invented traffic, and
    /// explains neither — so it is Debug-only by default.
    /// </para>
    ///
    /// <para>
    /// <c>EVENTSCOPE_FAKE_SOURCE=1</c> forces it back on, because Release is the configuration
    /// worth measuring and demoing, and losing the no-broker path in exactly that configuration
    /// would mean needing a live Kafka to check anything at all.
    /// </para>
    ///
    /// <para>
    /// <c>EVENTSCOPE_MEASURE</c> also forces it on. An unattended measurement run
    /// (<c>MainWindow.Measurement.cs</c>) drives the Fake source deliberately, and it runs in
    /// Release because Release is the only build whose numbers mean anything — so the
    /// measurement harness would otherwise be the first casualty of hiding this.
    /// </para>
    ///
    /// <para>
    /// <b>This hides UI entries and nothing else.</b>
    /// <see cref="Connections.ConnectionKind.Fake"/>, <c>FakeEventSource</c> and the
    /// <c>EventSourceFactory</c> branch that builds it are all untouched, so a profile that
    /// names it — or a test that asks for one — still resolves exactly as before.
    /// </para>
    /// </summary>
    public static bool ShowFakeSource =>
        IsDebugBuild
        || Environment.GetEnvironmentVariable(FakeSourceVariable) == "1"
        || Environment.GetEnvironmentVariable(MeasureVariable) is not null;

    /// <summary>Set by an unattended measurement run; see <see cref="ShowFakeSource"/>.</summary>
    public const string MeasureVariable = "EVENTSCOPE_MEASURE";

    private static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif
}
