using FileLantern.Core.Indexing;
using Xunit;

namespace FileLantern.Core.Tests;

public sealed class FileIndexDatabaseSearchTests
{
    [Fact]
    public void SearchByName_RanksExactThenPrefixThenSubstringMatches()
    {
        using var temp = new TemporaryDirectory();
        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));

        var records = new[]
        {
            BuildRecord(temp.Path, "report"),
            BuildRecord(temp.Path, "report.txt"),
            BuildRecord(temp.Path, "report-final.txt"),
            BuildRecord(temp.Path, "monthly-report.txt"),
            BuildRecord(temp.Path, "notes.md")
        };

        database.UpsertMany(records);

        var ranking = database.SearchByName("report", limit: 10).ToArray();
        Assert.Equal(4, ranking.Length);
        Assert.Equal("report", ranking[0].Name);
        Assert.Equal("report.txt", ranking[1].Name);
        Assert.Equal("report-final.txt", ranking[2].Name);
        Assert.Equal("monthly-report.txt", ranking[3].Name);
    }

    [Fact]
    public void SearchByName_EscapesSqlLikeWildcards()
    {
        using var temp = new TemporaryDirectory();
        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));

        database.UpsertMany(new[]
        {
            BuildRecord(temp.Path, "100%coverage.md"),
            BuildRecord(temp.Path, "under_score.txt"),
            BuildRecord(temp.Path, "plain.txt")
        });

        var percentResults = database.SearchByName("%", limit: 10).Select(r => r.Name).ToArray();
        Assert.Equal(new[] { "100%coverage.md" }, percentResults);

        var underscoreResults = database.SearchByName("_", limit: 10).Select(r => r.Name).ToArray();
        Assert.Equal(new[] { "under_score.txt" }, underscoreResults);
    }

    [Fact]
    public void SearchByName_RespectsResultLimit()
    {
        using var temp = new TemporaryDirectory();
        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));

        database.UpsertMany(new[]
        {
            BuildRecord(temp.Path, "alpha-1.txt"),
            BuildRecord(temp.Path, "alpha-2.txt"),
            BuildRecord(temp.Path, "alpha-3.txt")
        });

        var results = database.SearchByName("alpha", limit: 2);
        Assert.Equal(2, results.Count);
    }

    private static IndexedFileRecord BuildRecord(string root, string fileName)
    {
        var fullPath = Path.Combine(root, fileName);
        return new IndexedFileRecord(
            fullPath,
            fileName,
            Path.GetExtension(fileName).TrimStart('.'),
            size: 123,
            modifiedTimeUtcTicks: DateTime.UtcNow.Ticks);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"file-lantern-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
