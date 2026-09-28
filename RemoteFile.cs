namespace OpenClawDebugger;

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
