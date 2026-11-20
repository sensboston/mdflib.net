using System.Text.Json;
using Avalonia.Platform.Storage;

namespace MdfScope;

/// <summary>
/// Small per-user settings (%APPDATA%/MdfScope/settings.json): the last folders for measurements, DBC files and
/// CSV exports are remembered separately, since they usually live in different places.
/// </summary>
internal sealed class Settings
{
    public string? MeasurementFolder { get; set; }
    public string? DbcFolder { get; set; }
    public string? CsvFolder { get; set; }
    public string? LastDbc { get; set; }

    /// <summary>Width of the channel panel the user dragged to; null means it fits the names.</summary>
    public double? LeftPanelWidth { get; set; }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MdfScope", "settings.json");

    public static Settings Current { get; } = Load();

    static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The folder to start a picker in, if it still exists.</summary>
    public static async Task<IStorageFolder?> StartIn(IStorageProvider storage, string? folder) =>
        folder != null && Directory.Exists(folder) ? await storage.TryGetFolderFromPathAsync(folder) : null;

    /// <summary>Remembers the folder of a chosen file.</summary>
    public static void Remember(Action<Settings, string> set, string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
        {
            set(Current, dir);
            Current.Save();
        }
    }
}
