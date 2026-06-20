namespace ClaudeWatch;

using System.Text.Json;

internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeWatch", "settings.json");

    public string DisplayStyle { get; set; } = "Ducks";

    // Whether to show (and fetch, via the OAuth /usage endpoint) the session/weekly usage bars.
    // Defaults to true; a missing key in an older settings file keeps this default.
    public bool ShowUsage { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
