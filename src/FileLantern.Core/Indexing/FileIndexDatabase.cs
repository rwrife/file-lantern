using System.Globalization;
using System.Text;
using FileLantern.Core;
using Microsoft.Data.Sqlite;

namespace FileLantern.Core.Indexing;

public sealed class FileIndexDatabase : IDisposable
{
    private static readonly string[] ComparisonOperators = [">=", "<=", ">", "<", "="];

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

        var parsed = ParseQuery(query);
        if (!parsed.HasStructuredFilters)
        {
            if (parsed.NameTerms.Count == 0)
            {
                return Array.Empty<SearchResultItem>();
            }

            return parsed.NameTerms.Count == 1
                ? SearchByName(parsed.NameTerms[0], limit)
                : SearchWithFilters(parsed, limit);
        }

        return SearchWithFilters(parsed, limit);
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

    private IReadOnlyList<SearchResultItem> SearchWithFilters(ParsedSearchQuery query, int limit)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var hasContentFilter = query.ContentPhrase is not null;

            var sql = new StringBuilder();
            sql.Append("SELECT f.name, f.path");
            sql.Append(hasContentFilter ? ", snippet(file_content, 1, '[', ']', '…', 12)" : ", NULL");
            sql.Append(" FROM files f ");

            if (hasContentFilter)
            {
                sql.Append("JOIN file_content ON file_content.path = f.path ");
            }

            sql.Append("WHERE 1 = 1 ");

            if (query.Extension is not null)
            {
                sql.Append("AND f.ext = $ext COLLATE NOCASE ");
                command.Parameters.AddWithValue("$ext", query.Extension);
            }

            if (query.SizeFilter is not null)
            {
                sql.Append($"AND f.size {query.SizeFilter.Operator} $sizeBytes ");
                command.Parameters.AddWithValue("$sizeBytes", query.SizeFilter.Value);
            }

            if (query.ModifiedAgeFilter is not null)
            {
                var cutoffTicks = DateTime.UtcNow.Ticks - query.ModifiedAgeFilter.Value;
                var modifiedOperator = query.ModifiedAgeFilter.Operator switch
                {
                    "<" => ">",
                    "<=" => ">=",
                    ">" => "<",
                    ">=" => "<=",
                    "=" => "=",
                    _ => throw new InvalidOperationException($"Unsupported modified filter operator: {query.ModifiedAgeFilter.Operator}")
                };

                sql.Append($"AND f.mtime {modifiedOperator} $modifiedCutoff ");
                command.Parameters.AddWithValue("$modifiedCutoff", cutoffTicks);
            }

            if (hasContentFilter)
            {
                sql.Append("AND file_content MATCH $contentMatch ");
                command.Parameters.AddWithValue("$contentMatch", BuildFtsPhraseQuery(query.ContentPhrase!));
            }

            for (var i = 0; i < query.NameTerms.Count; i++)
            {
                var paramName = $"$nameTerm{i}";
                sql.Append($"AND f.name LIKE {paramName} ESCAPE '\' ");
                command.Parameters.AddWithValue(paramName, $"%{EscapeLikePattern(query.NameTerms[i])}%");
            }

            sql.Append("ORDER BY ");
            if (hasContentFilter)
            {
                sql.Append("bm25(file_content), ");
            }

            if (query.NameTerms.Count > 0)
            {
                var leadTerm = query.NameTerms[0];
                sql.Append("CASE ");
                sql.Append("WHEN f.name = $leadExact COLLATE NOCASE THEN 0 ");
                sql.Append("WHEN f.name LIKE $leadPrefix ESCAPE '\' THEN 1 ");
                sql.Append("ELSE 2 END, ");
                command.Parameters.AddWithValue("$leadExact", leadTerm);
                command.Parameters.AddWithValue("$leadPrefix", $"{EscapeLikePattern(leadTerm)}%");
            }

            sql.Append("LENGTH(f.name) ASC, f.name COLLATE NOCASE ASC ");
            sql.Append("LIMIT $limit;");
            command.Parameters.AddWithValue("$limit", limit);

            command.CommandText = sql.ToString();

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

    private static ParsedSearchQuery ParseQuery(string query)
    {
        var parsed = new ParsedSearchQuery();

        foreach (var token in TokenizeQuery(query.Trim()))
        {
            if (TryApplyFilterToken(token, parsed))
            {
                continue;
            }

            parsed.NameTerms.Add(token);
        }

        return parsed;
    }

    private static IEnumerable<string> TokenizeQuery(string query)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var character in query)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool TryApplyFilterToken(string token, ParsedSearchQuery query)
    {
        if (token.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
        {
            if (query.Extension is not null)
            {
                return false;
            }

            var extension = token["ext:".Length..].Trim();
            if (TryNormalizeExtension(extension, out var normalizedExtension))
            {
                query.Extension = normalizedExtension;
                return true;
            }

            return false;
        }

        if (token.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
        {
            if (query.SizeFilter is not null)
            {
                return false;
            }

            var sizeText = token["size:".Length..].Trim();
            if (TryParseByteSize(sizeText, out var sizeFilter))
            {
                query.SizeFilter = sizeFilter;
                return true;
            }

            return false;
        }

        if (token.StartsWith("modified:", StringComparison.OrdinalIgnoreCase))
        {
            if (query.ModifiedAgeFilter is not null)
            {
                return false;
            }

            var modifiedText = token["modified:".Length..].Trim();
            if (TryParseAgeFilter(modifiedText, out var ageFilter))
            {
                query.ModifiedAgeFilter = ageFilter;
                return true;
            }

            return false;
        }

        if (token.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
        {
            if (query.ContentPhrase is not null)
            {
                return false;
            }

            var content = token["content:".Length..].Trim();
            if (content.Length > 0)
            {
                query.ContentPhrase = content;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryNormalizeExtension(string value, out string extension)
    {
        extension = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().TrimStart('.');
        if (normalized.Length == 0)
        {
            return false;
        }

        if (normalized.Any(char.IsWhiteSpace))
        {
            return false;
        }

        extension = normalized.ToLowerInvariant();
        return true;
    }

    private static bool TryParseByteSize(string text, out ComparisonFilter sizeFilter)
    {
        sizeFilter = default;

        if (!TrySplitComparison(text, out var @operator, out var literal))
        {
            return false;
        }

        if (!TryParseNumberAndUnit(literal, out var value, out var unit))
        {
            return false;
        }

        var multiplier = unit switch
        {
            "" or "b" => 1d,
            "k" or "kb" or "kib" => 1024d,
            "m" or "mb" or "mib" => 1024d * 1024d,
            "g" or "gb" or "gib" => 1024d * 1024d * 1024d,
            "t" or "tb" or "tib" => 1024d * 1024d * 1024d * 1024d,
            _ => -1d
        };

        if (multiplier < 0)
        {
            return false;
        }

        var bytes = value * multiplier;
        if (bytes < 0 || bytes > long.MaxValue)
        {
            return false;
        }

        sizeFilter = new ComparisonFilter(@operator, Convert.ToInt64(Math.Round(bytes, MidpointRounding.AwayFromZero)));
        return true;
    }

    private static bool TryParseAgeFilter(string text, out ComparisonFilter ageFilter)
    {
        ageFilter = default;

        if (!TrySplitComparison(text, out var @operator, out var literal))
        {
            return false;
        }

        if (!TryParseDurationTicks(literal, out var ticks))
        {
            return false;
        }

        ageFilter = new ComparisonFilter(@operator, ticks);
        return true;
    }

    private static bool TrySplitComparison(string text, out string @operator, out string literal)
    {
        @operator = string.Empty;
        literal = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        foreach (var candidate in ComparisonOperators)
        {
            if (!trimmed.StartsWith(candidate, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = trimmed[candidate.Length..].Trim();
            if (rest.Length == 0)
            {
                return false;
            }

            @operator = candidate;
            literal = rest;
            return true;
        }

        @operator = "=";
        literal = trimmed;
        return true;
    }

    private static bool TryParseNumberAndUnit(string literal, out double value, out string unit)
    {
        value = 0;
        unit = string.Empty;

        if (string.IsNullOrWhiteSpace(literal))
        {
            return false;
        }

        var index = 0;
        while (index < literal.Length &&
               (char.IsDigit(literal[index]) || literal[index] is '.' or ','))
        {
            index++;
        }

        if (index == 0)
        {
            return false;
        }

        var valueText = literal[..index].Replace(',', '.');
        if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (value < 0)
        {
            return false;
        }

        unit = literal[index..].Trim().ToLowerInvariant();
        return true;
    }

    private static bool TryParseDurationTicks(string literal, out long ticks)
    {
        ticks = 0;

        if (!TryParseNumberAndUnit(literal, out var value, out var unit))
        {
            return false;
        }

        if (string.IsNullOrEmpty(unit))
        {
            return false;
        }

        var seconds = unit switch
        {
            "s" => value,
            "m" => value * 60d,
            "h" => value * 60d * 60d,
            "d" => value * 60d * 60d * 24d,
            "w" => value * 60d * 60d * 24d * 7d,
            _ => -1d
        };

        if (seconds < 0)
        {
            return false;
        }

        var tickCount = seconds * TimeSpan.TicksPerSecond;
        if (tickCount <= 0 || tickCount > long.MaxValue)
        {
            return false;
        }

        ticks = Convert.ToInt64(Math.Round(tickCount, MidpointRounding.AwayFromZero));
        return true;
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

    private readonly record struct ComparisonFilter(string Operator, long Value);

    private sealed class ParsedSearchQuery
    {
        public List<string> NameTerms { get; } = [];

        public string? Extension { get; set; }

        public ComparisonFilter? SizeFilter { get; set; }

        public ComparisonFilter? ModifiedAgeFilter { get; set; }

        public string? ContentPhrase { get; set; }

        public bool HasStructuredFilters =>
            Extension is not null
            || SizeFilter is not null
            || ModifiedAgeFilter is not null
            || ContentPhrase is not null;
    }
}
