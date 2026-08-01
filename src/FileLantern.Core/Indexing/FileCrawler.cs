namespace FileLantern.Core.Indexing;

public class FileCrawler
{
    private const long DefaultMaxContentIndexBytes = 1_048_576;

    private readonly FileIndexDatabase _database;
    private readonly Action<string>? _log;
    private readonly IReadOnlyList<ITextContentExtractor> _contentExtractors;
    private readonly long _maxContentIndexBytes;

    public FileCrawler(
        FileIndexDatabase database,
        Action<string>? log = null,
        IReadOnlyList<ITextContentExtractor>? contentExtractors = null,
        long maxContentIndexBytes = DefaultMaxContentIndexBytes)
    {
        if (maxContentIndexBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContentIndexBytes), "Maximum content index size must be non-negative.");
        }

        _database = database ?? throw new ArgumentNullException(nameof(database));
        _log = log;
        _maxContentIndexBytes = maxContentIndexBytes;
        _contentExtractors = contentExtractors is { Count: > 0 }
            ? contentExtractors
            : new ITextContentExtractor[] { new PlainTextContentExtractor() };
    }

    public CrawlResult Crawl(IEnumerable<string> rootPaths)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);

        var normalizedRoots = rootPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var filesIndexed = 0;
        var skippedPaths = 0;

        _database.UpsertMany(EnumerateIndexedFiles(
            normalizedRoots,
            onIndexed: () => filesIndexed++,
            onSkipped: () => skippedPaths++));

        return new CrawlResult(normalizedRoots.Length, filesIndexed, skippedPaths);
    }

    protected virtual bool DirectoryExists(string path) => Directory.Exists(path);

    protected virtual IReadOnlyList<string> GetFiles(string path) => Directory.GetFiles(path);

    protected virtual IReadOnlyList<string> GetDirectories(string path) => Directory.GetDirectories(path);

    protected virtual IndexedFileRecord BuildRecord(string filePath) => IndexedFileRecord.FromPath(filePath);

    private IEnumerable<IndexedFileRecord> EnumerateIndexedFiles(
        IReadOnlyList<string> roots,
        Action onIndexed,
        Action onSkipped)
    {
        foreach (var root in roots)
        {
            if (!DirectoryExists(root))
            {
                onSkipped();
                _log?.Invoke($"Skipped root '{root}' because it does not exist or is not a directory.");
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var current = pending.Pop();

                IReadOnlyList<string> files;
                try
                {
                    files = GetFiles(current);
                }
                catch (Exception ex) when (IsSkippable(ex))
                {
                    onSkipped();
                    _log?.Invoke($"Skipped '{current}': {ex.GetType().Name} - {ex.Message}");
                    continue;
                }

                foreach (var filePath in files)
                {
                    IndexedFileRecord record;
                    try
                    {
                        record = BuildRecord(filePath);
                    }
                    catch (Exception ex) when (IsSkippable(ex))
                    {
                        onSkipped();
                        _log?.Invoke($"Skipped file '{filePath}': {ex.GetType().Name} - {ex.Message}");
                        continue;
                    }

                    string? contentText = null;
                    try
                    {
                        contentText = TryExtractContent(record);
                    }
                    catch (Exception ex) when (IsSkippable(ex))
                    {
                        _log?.Invoke($"Skipped content extraction for '{filePath}': {ex.GetType().Name} - {ex.Message}");
                    }

                    onIndexed();
                    yield return record with { ContentText = contentText };
                }

                IReadOnlyList<string> directories;
                try
                {
                    directories = GetDirectories(current);
                }
                catch (Exception ex) when (IsSkippable(ex))
                {
                    onSkipped();
                    _log?.Invoke($"Skipped child directories in '{current}': {ex.GetType().Name} - {ex.Message}");
                    continue;
                }

                foreach (var directory in directories)
                {
                    pending.Push(directory);
                }
            }
        }
    }

    private string? TryExtractContent(IndexedFileRecord record)
    {
        if (record.Size > _maxContentIndexBytes)
        {
            return null;
        }

        foreach (var extractor in _contentExtractors)
        {
            if (!extractor.CanExtract(record.Path, record.Extension))
            {
                continue;
            }

            return extractor.ExtractText(record.Path);
        }

        return null;
    }

    private static bool IsSkippable(Exception ex)
        => ex is UnauthorizedAccessException
            or IOException
            or DirectoryNotFoundException
            or FileNotFoundException
            or PathTooLongException;
}
