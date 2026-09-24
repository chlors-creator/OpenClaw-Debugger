using System.IO;
using System.Text;
using System.Text.Json;

namespace OpenClawDebugger;

public static class SettingsRepository
{
    public static string DefaultPrivateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ChatGPT", "Openclaw", "OpenClaw-Debugger-Private");

    public static async Task<UserSettings> LoadAsync()
    {
        var settings = new UserSettings { PrivateDirectory = DefaultPrivateDirectory };
        Directory.CreateDirectory(settings.PrivateDirectory);
        foreach (var name in new[] { "Rollback", "Exports", "Secrets" })
            Directory.CreateDirectory(Path.Combine(settings.PrivateDirectory, name));

        var path = Path.Combine(settings.PrivateDirectory, "settings.json");
        if (!File.Exists(path))
        {
            await SaveAsync(settings);
            return settings;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<UserSettings>(await File.ReadAllTextAsync(path));
            if (loaded is not null)
            {
                loaded.PrivateDirectory = settings.PrivateDirectory;
                loaded.Connection ??= new ConnectionSettings();
                settings = loaded;
            }
        }
        catch (JsonException)
        {
        }
        return settings;
    }

    public static async Task SaveAsync(UserSettings settings)
    {
        Directory.CreateDirectory(settings.PrivateDirectory);
        var path = Path.Combine(settings.PrivateDirectory, "settings.json");
        var text = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
    }
}