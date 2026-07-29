using Microsoft.Data.Sqlite;

namespace FileLantern.Core.Indexing;

public sealed class FileIndexDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
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
            """;

        command.ExecuteNonQuery();
    }

    public void UpsertMany(IEnumerable<IndexedFileRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        ThrowIfDisposed();

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO files(path, name, ext, size, mtime)
            VALUES ($path, $name, $ext, $size, $mtime)
            ON CONFLICT(path) DO UPDATE SET
                name = excluded.name,
                ext = excluded.ext,
                size = excluded.size,
                mtime = excluded.mtime;
            """;

        var pathParam = command.CreateParameter();
        pathParam.ParameterName = "$path";
        command.Parameters.Add(pathParam);

        var nameParam = command.CreateParameter();
        nameParam.ParameterName = "$name";
        command.Parameters.Add(nameParam);

        var extParam = command.CreateParameter();
        extParam.ParameterName = "$ext";
        command.Parameters.Add(extParam);

        var sizeParam = command.CreateParameter();
        sizeParam.ParameterName = "$size";
        command.Parameters.Add(sizeParam);

        var mtimeParam = command.CreateParameter();
        mtimeParam.ParameterName = "$mtime";
        command.Parameters.Add(mtimeParam);

        foreach (var record in records)
        {
            pathParam.Value = record.Path;
            nameParam.Value = record.Name;
            extParam.Value = record.Extension;
            sizeParam.Value = record.Size;
            mtimeParam.Value = record.ModifiedTimeUtcTicks;

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public int CountFiles()
    {
        ThrowIfDisposed();

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM files;";

        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IndexedFileRecord? GetByPath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ThrowIfDisposed();

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
}
