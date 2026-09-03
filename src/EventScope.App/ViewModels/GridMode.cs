namespace EventScope.App.ViewModels;

/// <summary>Which source the message grid is showing.</summary>
public enum GridMode
{
    /// <summary>The live ring, fed by the running ingest pipeline.</summary>
    Live,

    /// <summary>Rows read back off disk — a captured day, or a search result set.</summary>
    History,
}
