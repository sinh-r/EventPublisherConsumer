using System.Text.Json;

namespace EventScope.App.Connections;

/// <summary>
/// Persists saved connections (UI spec §6) as plain JSON under
/// <c>%LOCALAPPDATA%\EventScope\connections.json</c> — the same small,
/// infrequently-changed, human-editable-if-needed shape as
/// <see cref="EventScope.App.Settings.AppSettings"/>, and deliberately not SQLite for the
/// same reason that one gives.
/// </summary>
public sealed class ConnectionStore
{
    [System.Text.Json.Serialization.JsonIgnore]
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EventScope", "connections.json");

    /// <summary>Loads from <paramref name="filePath"/> (the real file if omitted), falling
    /// back to an empty list on any failure (missing file, corrupt JSON, unreadable) — a
    /// broken connections file must never block startup, same rule as
    /// <see cref="EventScope.App.Settings.AppSettings.Load"/>. The
    /// built-in "Fake source" entry is never stored here; callers that want it present in a
    /// displayed list add it themselves (see <see cref="ConnectionProfile.CreateFakeSource"/>).</summary>
    public static List<ConnectionProfile> Load(string? filePath = null)
    {
        filePath ??= DefaultFilePath;

        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize<List<ConnectionProfile>>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to an empty list.
        }

        return [];
    }

    public static void Save(IReadOnlyList<ConnectionProfile> profiles, string? filePath = null)
    {
        filePath ??= DefaultFilePath;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }
}
