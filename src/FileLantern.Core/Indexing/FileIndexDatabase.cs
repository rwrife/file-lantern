using FileLantern.Core;
using Microsoft.Data.Sqlite;

namespace FileLantern.Core.Indexing;

public sealed class FileIndexDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public FileIndexDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();

        EnsureSchema();
    }

    public void EnsureSchema()
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS files (
                    path TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    ext TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    mtime INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_files_name ON files(name);
                CREATE INDEX IF NOT EXISTS idx_files_ext ON files(ext);

                CREATE VIRTUAL TABLE IF NOT EXISTS file_content
                USING fts5(path UNINDEXED, body);
                """;

            command.ExecuteNonQuery();
        }
    }

    public void UpsertMany(IEnumerable<IndexedFileRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        ThrowIfDisposed();

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            using var upsertFileCommand = _connection.CreateCommand();
            upsertFileCommand.Transaction = transaction;
            upsertFileCommand.CommandText = """
                INSERT INTO files(path, name, ext, size, mtime)
                VALUES ($path, $name, $ext, $size, $mtime)
                ON CONFLICT(path) DO UPDATE SET
                    name = excluded.name,
                    ext = excluded.ext,
                    size = excluded.size,
                    mtime = excluded.mtime;
                """;

            var pathParam = upsertFileCommand.CreateParameter();
            pathParam.ParameterName = "$path";
            upsertFileCommand.Parameters.Add(pathParam);

            var nameParam = upsertFileCommand.CreateParameter();
            nameParam.ParameterName = "$name";
            upsertFileCommand.Parameters.Add(nameParam);

            var extParam = upsertFileCommand.CreateParameter();
            extParam.ParameterName = "$ext";
            upsertFileCommand.Parameters.Add(extParam);

            var sizeParam = upsertFileCommand.CreateParameter();
            sizeParam.ParameterName = "$size";
            upsertFileCommand.Parameters.Add(sizeParam);

            var mtimeParam = upsertFileCommand.CreateParameter();
            mtimeParam.ParameterName = "$mtime";
            upsertFileCommand.Parameters.Add(mtimeParam);

            using var clearContentCommand = _connection.CreateCommand();
            clearContentCommand.Transaction = transaction;
            clearContentCommand.CommandText = "DELETE FROM file_content WHERE path = $path;";
            var clearPathParam = clearContentCommand.CreateParameter();
            clearPathParam.ParameterName = "$path";
            clearContentCommand.Parameters.Add(clearPathParam);

            using var upsertContentCommand = _connection.CreateCommand();
            upsertContentCommand.Transaction = transaction;
            upsertContentCommand.CommandText = "INSERT INTO file_content(path, body) VALUES ($path, $body);";
            var contentPathParam = upsertContentCommand.CreateParameter();
            contentPathParam.ParameterName = "$path";
            upsertContentCommand.Parameters.Add(contentPathParam);
            var bodyParam = upsertContentCommand.CreateParameter();
            bodyParam.ParameterName = "$body";
            upsertContentCommand.Parameters.Add(bodyParam);

            foreach (var record in records)
            {
                pathParam.Value = record.Path;
                nameParam.Value = record.Name;
                extParam.Value = record.Extension;
                sizeParam.Value = record.Size;
                mtimeParam.Value = record.ModifiedTimeUtcTicks;

                upsertFileCommand.ExecuteNonQuery();

                clearPathParam.Value = record.Path;
                clearContentCommand.ExecuteNonQuery();

                if (!string.IsNullOrWhiteSpace(record.ContentText))
                {
                    contentPathParam.Value = record.Path;
                    bodyParam.Value = record.ContentText;
                    upsertContentCommand.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }

    public void DeleteByPaths(IEnumerable<string> fullPaths)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);
        ThrowIfDisposed();

        var normalizedPaths = fullPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedPaths.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            using var deleteContentCommand = _connection.CreateCommand();
            deleteContentCommand.Transaction = transaction;
            deleteContentCommand.CommandText = "DELETE FROM file_content WHERE path = $path;";
            var contentPathParam = deleteContentCommand.CreateParameter();
            contentPathParam.ParameterName = "$path";
            deleteContentCommand.Parameters.Add(contentPathParam);

            using var deleteFileCommand = _connection.CreateCommand();
            deleteFileCommand.Transaction = transaction;
            deleteFileCommand.CommandText = "DELETE FROM files WHERE path = $path;";
            var filePathParam = deleteFileCommand.CreateParameter();
            filePathParam.ParameterName = "$path";
            deleteFileCommand.Parameters.Add(filePathParam);

            foreach (var path in normalizedPaths)
            {
                contentPathParam.Value = path;
                deleteContentCommand.ExecuteNonQuery();

                filePathParam.Value = path;
                deleteFileCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<string> ListPathsUnderRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ThrowIfDisposed();

        var normalizedRoot = Path.GetFullPath(rootPath);
        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT path
                FROM files
                WHERE path = $root
                   OR path LIKE $prefix ESCAPE '\'
                ORDER BY path COLLATE NOCASE ASC;
                """;

            command.Parameters.AddWithValue("$root", normalizedRoot);
            command.Parameters.AddWithValue("$prefix", $"{EscapeLikePattern(rootPrefix)}%");

            var paths = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                paths.Add(reader.GetString(0));
            }

            return paths;
        }
    }

    public int CountFiles()
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM files;";

            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    public IndexedFileRecord? GetByPath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ThrowIfDisposed();

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT path, name, ext, size, mtime FROM files WHERE path = $path;";
            command.Parameters.AddWithValue("$path", Path.GetFullPath(fullPath));

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new IndexedFileRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4));
        }
    }

    public IReadOnlyList<SearchResultItem> Search(string query, int limit = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ThrowIfDisposed();

        if (limit <= 0)
        {
            return Array.Empty<SearchResultItem>();
        }

        var trimmedQuery = query.Trim();
        var contentQuery = TryGetContentQuery(trimmedQuery);

        return contentQuery is not null
            ? SearchByContent(contentQuery, limit)
            : SearchByName(trimmedQuery, limit);
    }

    public IReadOnlyList<SearchResultItem> SearchByName(string query, int limit = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ThrowIfDisposed();

        if (limit <= 0)
        {
            return Array.Empty<SearchResultItem>();
        }

        var trimmedQuery = query.Trim();
        var escaped = EscapeLikePattern(trimmedQuery);
        var prefixPattern = $"{escaped}%";
        var containsPattern = $"%{escaped}%";

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT name, path
                FROM files
                WHERE name LIKE $contains ESCAPE '\'
                ORDER BY
                    CASE
                        WHEN name = $exact COLLATE NOCASE THEN 0
                        WHEN name LIKE $prefix ESCAPE '\' THEN 1
                        ELSE 2
                    END,
                    LENGTH(name) ASC,
                    name COLLATE NOCASE ASC
                LIMIT $limit;
                """;

            command.Parameters.AddWithValue("$contains", containsPattern);
            command.Parameters.AddWithValue("$prefix", prefixPattern);
            command.Parameters.AddWithValue("$exact", trimmedQuery);
            command.Parameters.AddWithValue("$limit", limit);

            var results = new List<SearchResultItem>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SearchResultItem(
                    reader.GetString(0),
                    reader.GetString(1)));
            }

            return results;
        }
    }

    public IReadOnlyList<SearchResultItem> SearchByContent(string phrase, int limit = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phrase);
        ThrowIfDisposed();

        if (limit <= 0)
        {
            return Array.Empty<SearchResultItem>();
        }

        var trimmedPhrase = phrase.Trim();
        if (string.IsNullOrWhiteSpace(trimmedPhrase))
        {
            return Array.Empty<SearchResultItem>();
        }

        var ftsQuery = BuildFtsPhraseQuery(trimmedPhrase);

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT f.name, f.path, snippet(file_content, 1, '[', ']', '…', 12)
                FROM file_content
                JOIN files f ON f.path = file_content.path
                WHERE file_content MATCH $match
                ORDER BY bm25(file_content), f.name COLLATE NOCASE ASC
                LIMIT $limit;
                """;

            command.Parameters.AddWithValue("$match", ftsQuery);
            command.Parameters.AddWithValue("$limit", limit);

            var results = new List<SearchResultItem>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SearchResultItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            return results;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _connection.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static string BuildFtsPhraseQuery(string phrase)
    {
        var escaped = phrase.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string? TryGetContentQuery(string query)
    {
        var markerIndex = query.IndexOf("content:", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var predicate = query[(markerIndex + "content:".Length)..].Trim();
        return predicate.Length == 0 ? null : predicate;
    }
}
