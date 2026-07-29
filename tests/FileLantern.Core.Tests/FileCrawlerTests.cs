using FileLantern.Core.Indexing;
using Xunit;

namespace FileLantern.Core.Tests;

public sealed class FileCrawlerTests
{
    [Fact]
    public void Crawl_PopulatesFilesTable_ForNestedDirectoriesAndMultipleRoots()
    {
        using var temp = new TemporaryDirectory();
        var rootA = Path.Combine(temp.Path, "root-a");
        var rootB = Path.Combine(temp.Path, "root-b");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        Directory.CreateDirectory(Path.Combine(rootA, "nested"));

        var alphaPath = Path.Combine(rootA, "alpha.txt");
        var betaPath = Path.Combine(rootA, "nested", "beta.log");
        var gammaPath = Path.Combine(rootB, "gamma.md");

        File.WriteAllText(alphaPath, "alpha");
        File.WriteAllText(betaPath, "beta");
        File.WriteAllText(gammaPath, "gamma");

        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));
        var crawler = new FileCrawler(database);

        var result = crawler.Crawl(new[] { rootA, rootB });

        Assert.Equal(2, result.RootsScanned);
        Assert.Equal(3, result.FilesIndexed);
        Assert.Equal(0, result.SkippedPaths);
        Assert.Equal(3, database.CountFiles());

        var indexedAlpha = database.GetByPath(alphaPath);
        Assert.NotNull(indexedAlpha);
        Assert.Equal("alpha.txt", indexedAlpha!.Name);
        Assert.Equal("txt", indexedAlpha.Extension);
    }

    [Fact]
    public void Crawl_ReRun_UpdatesExistingRowsWithoutDuplicates()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);

        var filePath = Path.Combine(root, "sample.txt");
        File.WriteAllText(filePath, "first version");

        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));
        var crawler = new FileCrawler(database);

        var first = crawler.Crawl(new[] { root });
        var firstRecord = database.GetByPath(filePath);
        Assert.NotNull(firstRecord);

        Thread.Sleep(25);
        File.WriteAllText(filePath, "first version with more content");

        var second = crawler.Crawl(new[] { root });
        var secondRecord = database.GetByPath(filePath);
        Assert.NotNull(secondRecord);

        Assert.Equal(1, first.FilesIndexed);
        Assert.Equal(1, second.FilesIndexed);
        Assert.Equal(1, database.CountFiles());
        Assert.True(secondRecord!.Size > firstRecord!.Size);
        Assert.True(secondRecord.ModifiedTimeUtcTicks >= firstRecord.ModifiedTimeUtcTicks);
    }

    [Fact]
    public void Crawl_WhenAccessDenied_SkipsAndContinues()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root");
        var blocked = Path.Combine(root, "blocked");
        Directory.CreateDirectory(blocked);

        var accessible = Path.Combine(root, "ok.txt");
        File.WriteAllText(accessible, "ok");

        var logs = new List<string>();
        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));
        var crawler = new ThrowingFileCrawler(database, blocked, logs.Add);

        var result = crawler.Crawl(new[] { root });

        Assert.Equal(1, database.CountFiles());
        Assert.True(result.SkippedPaths >= 1);
        Assert.Contains(logs, log => log.Contains("UnauthorizedAccessException", StringComparison.Ordinal));
    }

    private sealed class ThrowingFileCrawler : FileCrawler
    {
        private readonly string _blockedDirectory;

        public ThrowingFileCrawler(FileIndexDatabase database, string blockedDirectory, Action<string> log)
            : base(database, log)
        {
            _blockedDirectory = Path.GetFullPath(blockedDirectory);
        }

        protected override IReadOnlyList<string> GetFiles(string path)
        {
            if (string.Equals(Path.GetFullPath(path), _blockedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Simulated access denied for unit test.");
            }

            return base.GetFiles(path);
        }
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
