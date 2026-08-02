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

    [Fact]
    public void Search_WithContentFilter_ReturnsContentMatchesWithSnippets()
    {
        using var temp = new TemporaryDirectory();
        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));

        database.UpsertMany(new[]
        {
            BuildRecord(temp.Path, "invoice-a.md", contentText: "Client requested refund for April invoice"),
            BuildRecord(temp.Path, "invoice-b.md", contentText: "Paid in full"),
            BuildRecord(temp.Path, "notes.txt")
        });

        var results = database.Search("content:\"requested refund\"", limit: 10).ToArray();

        Assert.Single(results);
        Assert.Equal("invoice-a.md", results[0].Name);
        Assert.False(string.IsNullOrWhiteSpace(results[0].Snippet));
        Assert.Contains("refund", results[0].Snippet!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_WithExtFilter_LimitsByExtension()
    {
        using var temp = new TemporaryDirectory();
        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));

        database.UpsertMany(new[]
        {
            BuildRecord(temp.Path, "design.pdf"),
            BuildRecord(temp.Path, "design.md"),
            BuildRecord(temp.Path, "notes.txt")
        });

        var results = database.Search("ext:pdf", limit: 10).Select(result => result.Name).ToArray();

        Assert.Equal(new[] { "design.pdf" }, results);
    }

    [Fact]
    public void Search_WithSizeFilter_ParsesComparatorAndUnits()
    {
        using var temp = new TemporaryDirectory();
        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));

        database.UpsertMany(new[]
        {
            BuildRecord(temp.Path, "small.zip", size: 2 * 1024L * 1024L),
            BuildRecord(temp.Path, "large.zip", size: 12 * 1024L * 1024L)
        });

        var results = database.Search("size:>10mb", limit: 10).Select(result => result.Name).ToArray();

        Assert.Equal(new[] { "large.zip" }, results);
    }

    [Fact]
    public void Search_WithModifiedFilter_ParsesRelativeAgeComparators()
    {
        using var temp = new TemporaryDirectory();
        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));
        var now = DateTime.UtcNow;

        database.UpsertMany(new[]
        {
            BuildRecord(temp.Path, "fresh.txt", modifiedUtc: now.Subtract(TimeSpan.FromDays(2))),
            BuildRecord(temp.Path, "stale.txt", modifiedUtc: now.Subtract(TimeSpan.FromDays(14)))
        });

        var recent = database.Search("modified:<7d", limit: 10).Select(result => result.Name).ToArray();
        var old = database.Search("modified:>7d", limit: 10).Select(result => result.Name).ToArray();

        Assert.Equal(new[] { "fresh.txt" }, recent);
        Assert.Equal(new[] { "stale.txt" }, old);
    }

    [Fact]
    public void Search_CombinesFiltersWithFreeTextTerms()
    {
        using var temp = new TemporaryDirectory();
        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));
        var now = DateTime.UtcNow;

        database.UpsertMany(new[]
        {
            BuildRecord(
                temp.Path,
                "report-april.pdf",
                size: 12 * 1024L * 1024L,
                modifiedUtc: now.Subtract(TimeSpan.FromDays(2)),
                contentText: "Quarterly revenue for North America"),
            BuildRecord(
                temp.Path,
                "report-april.txt",
                size: 12 * 1024L * 1024L,
                modifiedUtc: now.Subtract(TimeSpan.FromDays(2)),
                contentText: "Quarterly revenue for North America"),
            BuildRecord(
                temp.Path,
                "report-old.pdf",
                size: 12 * 1024L * 1024L,
                modifiedUtc: now.Subtract(TimeSpan.FromDays(20)),
                contentText: "Quarterly revenue for North America"),
            BuildRecord(
                temp.Path,
                "notes-april.pdf",
                size: 12 * 1024L * 1024L,
                modifiedUtc: now.Subtract(TimeSpan.FromDays(2)),
                contentText: "Sprint planning notes")
        });

        var results = database.Search(
            "report ext:pdf size:>10mb modified:<7d content:\"Quarterly revenue\"",
            limit: 10).ToArray();

        Assert.Single(results);
        Assert.Equal("report-april.pdf", results[0].Name);
        Assert.False(string.IsNullOrWhiteSpace(results[0].Snippet));
    }

    [Fact]
    public void Search_InvalidFilterSyntax_IsTreatedAsText()
    {
        using var temp = new TemporaryDirectory();
        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));

        database.UpsertMany(new[]
        {
            BuildRecord(temp.Path, "size:>>10mb cheatsheet.txt"),
            BuildRecord(temp.Path, "normal.txt")
        });

        var results = database.Search("size:>>10mb", limit: 10).ToArray();

        Assert.Single(results);
        Assert.Equal("size:>>10mb cheatsheet.txt", results[0].Name);
    }

    [Fact]
    public void Search_CombinesContentAndExtFilters()
    {
        using var temp = new TemporaryDirectory();
        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));

        database.UpsertMany(new[]
        {
            BuildRecord(temp.Path, "todo.md", contentText: "TODO: wire dependency injection"),
            BuildRecord(temp.Path, "todo.txt", contentText: "TODO: wire dependency injection")
        });

        var results = database.Search("ext:md content:TODO", limit: 10).ToArray();

        Assert.Single(results);
        Assert.Equal("todo.md", results[0].Name);
    }

    private static IndexedFileRecord BuildRecord(
        string root,
        string fileName,
        long size = 123,
        DateTime? modifiedUtc = null,
        string? contentText = null)
    {
        var fullPath = Path.Combine(root, fileName);
        var record = new IndexedFileRecord(
            fullPath,
            fileName,
            Path.GetExtension(fileName).TrimStart('.'),
            size,
            (modifiedUtc ?? DateTime.UtcNow).Ticks);

        return contentText is null
            ? record
            : record with { ContentText = contentText };
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
