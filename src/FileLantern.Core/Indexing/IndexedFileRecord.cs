namespace FileLantern.Core.Indexing;

public sealed record IndexedFileRecord(
    string Path,
    string Name,
    string Extension,
    long Size,
    long ModifiedTimeUtcTicks,
    string? ContentText = null)
{
    public static IndexedFileRecord FromPath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fileInfo = new FileInfo(filePath);

        return new IndexedFileRecord(
            fileInfo.FullName,
            fileInfo.Name,
            fileInfo.Extension.TrimStart('.').ToLowerInvariant(),
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks);
    }
}
