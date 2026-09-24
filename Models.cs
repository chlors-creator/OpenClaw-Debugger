using System.ComponentModel;

namespace OpenClawDebugger;

public sealed class ConnectionSettings
{
    public string Host { get; set; } = "106.14.173.90";
    public string Username { get; set; } = "admin";
    public int Port { get; set; } = 22;
    public string WorkspacePath { get; set; } = "/home/admin/.openclaw/workspace";
    public string StickersPath { get; set; } = "/home/admin/.openclaw/workspace/stickers";

    public string Target => $"{Username}@{Host}";
}

public sealed class RemoteFile
{
    public string Root { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string Kind { get; init; } = "text";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
    public DateTimeOffset? ModifiedUtc { get; init; }
    public bool Editable { get; init; }
    public bool IsImage => Kind == "image";
    public string Key => $"{Root}:{RelativePath}";
    public string DisplayName => RelativePath.Replace('/', '\\');
    public string SizeLabel => Size < 1024 ? $"{Size} B" : $"{Size / 1024.0:N1} KB";
    public string ModifiedLabel => ModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
    public override string ToString() => $"{DisplayName}    {SizeLabel}";
}

public sealed record RemoteFileContent(
    string Root,
    string RelativePath,
    string Sha256,
    long Size,
    DateTimeOffset ModifiedUtc,
    string? Text,
    byte[]? Binary,
    byte[] RawBytes);

public sealed class StickerRow : INotifyPropertyChanged
{
    private string _tagsText = "";
    private double _weight = 1;

    public string Id { get; init; } = "";
    public string ImagePath { get; init; } = "";
    public string TagsText
    {
        get => _tagsText;
        set
        {
            if (_tagsText == value) return;
            _tagsText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagsText)));
        }
    }

    public double Weight
    {
        get => _weight;
        set
        {
            if (_weight.Equals(value)) return;
            _weight = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Weight)));
        }
    }

    public string TagSummary => string.IsNullOrWhiteSpace(TagsText) ? "无标签" : TagsText;
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => $"{Id} · {ImagePath}";
}

public sealed class UserSettings
{
    public ConnectionSettings Connection { get; set; } = new();
    public string ThemeName { get; set; } = "Atri";
    public string PrivateDirectory { get; set; } = SettingsRepository.DefaultPrivateDirectory;
}