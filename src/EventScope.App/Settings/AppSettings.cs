using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventScope.App.Settings;

/// <summary>One user-configured pinned JSON field, as stored in settings — the App-layer
/// mirror of <see cref="EventScope.Storage.Sqlite.PinnedField"/>, kept separate so Storage
/// has no reason to know about JSON settings persistence.</summary>
public sealed record PinnedFieldSetting(string Name, string JsonPath);

/// <summary>
/// User-configurable settings (build plan §5 M2: "settings view for cap, retention, indexed
/// prefix"), persisted as plain JSON under <c>%LOCALAPPDATA%\EventScope\settings.json</c>.
/// No database involved — this is small, infrequently-changed, human-editable-if-needed
/// configuration, not data the app's own storage model is for.
/// </summary>
public sealed class AppSettings
{
    public long RetentionCapBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int RetentionDays { get; set; } = 14;
    public int IndexedPrefixBytes { get; set; } = 2048;
    public List<PinnedFieldSetting> PinnedFields { get; set; } = [];

    [JsonIgnore]
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EventScope", "settings.json");

    /// <summary>Loads from <paramref name="filePath"/> (the real settings file if omitted),
    /// falling back to defaults on any failure (missing file, corrupt JSON, unreadable) — a
    /// broken settings file must never prevent the app from starting. The path parameter
    /// exists so tests can exercise real file I/O against a temp path instead of the user's
    /// actual settings file.</summary>
    public static AppSettings Load(string? filePath = null)
    {
        filePath ??= DefaultFilePath;

        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to defaults.
        }

        return new AppSettings();
    }

    public void Save(string? filePath = null)
    {
        filePath ??= DefaultFilePath;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }
}
