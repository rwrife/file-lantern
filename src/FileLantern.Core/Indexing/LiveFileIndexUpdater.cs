namespace FileLantern.Core.Indexing;

public sealed class LiveFileIndexUpdater : IDisposable
{
    private const long DefaultMaxContentIndexBytes = 1_048_576;
    private static readonly TimeSpan DefaultDebounceWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultReconciliationInterval = TimeSpan.FromMinutes(10);

    private readonly FileIndexDatabase _database;
    private readonly string[] _roots;
    private readonly Action<string>? _log;
    private readonly IReadOnlyList<ITextContentExtractor> _contentExtractors;
    private readonly long _maxContentIndexBytes;
    private readonly TimeSpan _debounceWindow;

    private readonly object _queueGate = new();
    private readonly HashSet<string> _pendingUpserts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _flushTimer;
    private readonly Timer _reconciliationTimer;
    private readonly SemaphoreSlim _reconciliationLock = new(1, 1);

    private int _isFlushing;
    private bool _disposed;

    public LiveFileIndexUpdater(
        FileIndexDatabase database,
        IEnumerable<string> rootPaths,
        Action<string>? log = null,
        IReadOnlyList<ITextContentExtractor>? contentExtractors = null,
        long maxContentIndexBytes = DefaultMaxContentIndexBytes,
        TimeSpan? debounceWindow = null,
        TimeSpan? reconciliationInterval = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        ArgumentNullException.ThrowIfNull(rootPaths);

        if (maxContentIndexBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContentIndexBytes), "Maximum content index size must be non-negative.");
        }

        _debounceWindow = debounceWindow ?? DefaultDebounceWindow;
        if (_debounceWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceWindow), "Debounce window must be positive.");
        }

        var reconciliationPeriod = reconciliationInterval ?? DefaultReconciliationInterval;
        if (reconciliationPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reconciliationInterval), "Reconciliation interval must be positive.");
        }

        _log = log;
        _maxContentIndexBytes = maxContentIndexBytes;
        _contentExtractors = contentExtractors is { Count: > 0 }
            ? contentExtractors
            : new ITextContentExtractor[] { new PlainTextContentExtractor() };

        _roots = rootPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _flushTimer = new Timer(_ => FlushPendingChanges(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _reconciliationTimer = new Timer(_ => _ = ReconcileAsync(), null, reconciliationPeriod, reconciliationPeriod);

        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
            {
                _log?.Invoke($"Live indexing root '{root}' does not exist; it will be checked during reconciliation.");
                continue;
            }

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.Size
                    | NotifyFilters.LastWrite
                    | NotifyFilters.CreationTime,
                Filter = "*"
            };

            watcher.Created += OnCreated;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            _watchers.Add(watcher);
        }

        _ = ReconcileAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _reconciliationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }

        _watchers.Clear();

        _flushTimer.Dispose();
        _reconciliationTimer.Dispose();
        _reconciliationLock.Dispose();
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => QueueUpsert(e.FullPath);

    private void OnChanged(object sender, FileSystemEventArgs e) => QueueUpsert(e.FullPath);

    private void OnDeleted(object sender, FileSystemEventArgs e) => QueueDelete(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        QueueDelete(e.OldFullPath);
        QueueUpsert(e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var exception = e.GetException();
        _log?.Invoke($"Live index watcher error: {exception.GetType().Name} - {exception.Message}. Scheduling reconciliation.");
        _ = ReconcileAsync();
    }

    private void QueueUpsert(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalizedPath = NormalizePath(path);
        if (normalizedPath is null)
        {
            return;
        }

        lock (_queueGate)
        {
            _pendingDeletes.Remove(normalizedPath);
            _pendingUpserts.Add(normalizedPath);
            _flushTimer.Change(_debounceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void QueueDelete(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalizedPath = NormalizePath(path);
        if (normalizedPath is null)
        {
            return;
        }

        lock (_queueGate)
        {
            _pendingUpserts.Remove(normalizedPath);
            _pendingDeletes.Add(normalizedPath);
            _flushTimer.Change(_debounceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ReconcileAsync()
    {
        if (_disposed || _roots.Length == 0)
        {
            return;
        }

        if (!await _reconciliationLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await Task.Run(ReconcileAllRoots).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Live index reconciliation failed: {ex.GetType().Name} - {ex.Message}");
        }
        finally
        {
            _reconciliationLock.Release();
        }
    }

    private void ReconcileAllRoots()
    {
        foreach (var root in _roots)
        {
            if (_disposed)
            {
                return;
            }

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var records = new List<IndexedFileRecord>();

            if (Directory.Exists(root))
            {
                var pending = new Stack<string>();
                pending.Push(root);

                while (pending.Count > 0)
                {
                    var current = pending.Pop();

                    IReadOnlyList<string> files;
                    try
                    {
                        files = Directory.GetFiles(current);
                    }
                    catch (Exception ex) when (IsSkippable(ex))
                    {
                        _log?.Invoke($"Reconciliation skipped files in '{current}': {ex.GetType().Name} - {ex.Message}");
                        continue;
                    }

                    foreach (var filePath in files)
                    {
                        var normalized = NormalizePath(filePath);
                        if (normalized is null)
                        {
                            continue;
                        }

                        seenPaths.Add(normalized);

                        try
                        {
                            var record = IndexedFileRecord.FromPath(normalized);
                            var contentText = TryExtractContent(record);
                            records.Add(record with { ContentText = contentText });
                        }
                        catch (Exception ex) when (IsSkippable(ex))
                        {
                            _log?.Invoke($"Reconciliation skipped file '{filePath}': {ex.GetType().Name} - {ex.Message}");
                        }
                    }

                    IReadOnlyList<string> directories;
                    try
                    {
                        directories = Directory.GetDirectories(current);
                    }
                    catch (Exception ex) when (IsSkippable(ex))
                    {
                        _log?.Invoke($"Reconciliation skipped child directories in '{current}': {ex.GetType().Name} - {ex.Message}");
                        continue;
                    }

                    foreach (var directory in directories)
                    {
                        pending.Push(directory);
                    }
                }
            }

            if (records.Count > 0)
            {
                _database.UpsertMany(records);
            }

            var indexedPaths = _database.ListPathsUnderRoot(root);
            if (indexedPaths.Count == 0)
            {
                continue;
            }

            var stalePaths = indexedPaths.Where(path => !seenPaths.Contains(path)).ToArray();
            if (stalePaths.Length > 0)
            {
                _database.DeleteByPaths(stalePaths);
            }
        }
    }

    private void FlushPendingChanges()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _isFlushing, 1) == 1)
        {
            return;
        }

        try
        {
            string[] pendingDeletes;
            string[] pendingUpserts;

            lock (_queueGate)
            {
                pendingDeletes = _pendingDeletes.ToArray();
                pendingUpserts = _pendingUpserts.ToArray();
                _pendingDeletes.Clear();
                _pendingUpserts.Clear();
            }

            if (pendingDeletes.Length > 0)
            {
                _database.DeleteByPaths(pendingDeletes);
            }

            if (pendingUpserts.Length == 0)
            {
                return;
            }

            var records = new List<IndexedFileRecord>(pendingUpserts.Length);
            var missingPaths = new List<string>();

            foreach (var path in pendingUpserts)
            {
                if (Directory.Exists(path))
                {
                    continue;
                }

                if (!File.Exists(path))
                {
                    missingPaths.Add(path);
                    continue;
                }

                try
                {
                    var record = IndexedFileRecord.FromPath(path);
                    var contentText = TryExtractContent(record);
                    records.Add(record with { ContentText = contentText });
                }
                catch (Exception ex) when (IsSkippable(ex))
                {
                    _log?.Invoke($"Live indexing skipped '{path}': {ex.GetType().Name} - {ex.Message}");
                }
            }

            if (missingPaths.Count > 0)
            {
                _database.DeleteByPaths(missingPaths);
            }

            if (records.Count > 0)
            {
                _database.UpsertMany(records);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Live index flush failed: {ex.GetType().Name} - {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isFlushing, 0);
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

    private static string? NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsSkippable(Exception ex)
        => ex is UnauthorizedAccessException
            or IOException
            or DirectoryNotFoundException
            or FileNotFoundException
            or PathTooLongException;
}
