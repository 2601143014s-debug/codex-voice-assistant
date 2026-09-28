using System.Text.Json;

namespace CodexVoiceAssistant.Infrastructure.Settings;

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "CodexVoiceAssistant",
            "settings.json");
    }

    public string Path => _path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(
                       File.ReadAllText(_path),
                       JsonOptions)
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(
            _path,
            JsonSerializer.Serialize(settings, JsonOptions));
    }
}
