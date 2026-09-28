namespace OpenClawDebugger;

public sealed record RemoteFileContent(
    string Root,
    string RelativePath,
    string Sha256,
    long Size,
    DateTimeOffset ModifiedUtc,
    string? Text,
    byte[]? Binary,
    byte[] RawBytes);
