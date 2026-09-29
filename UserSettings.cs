namespace OpenClawDebugger;

public sealed class UserSettings
{
    public ConnectionSettings Connection { get; set; } = new();
    public string ThemeName { get; set; } = "Atri";
    public string PrivateDirectory { get; set; } = SettingsRepository.DefaultPrivateDirectory;
    public int BackupRetentionCount { get; set; } = 5;
}
