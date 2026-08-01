using FileLantern.Core.Indexing;
using Xunit;

namespace FileLantern.Core.Tests;

public sealed class LiveFileIndexUpdaterTests
{
    [Fact]
    public void FileSystemEvents_CreateModifyRenameDelete_UpdateIndex()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);

        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));
        using var updater = new LiveFileIndexUpdater(
            database,
            new[] { root },
            debounceWindow: TimeSpan.FromMilliseconds(75),
            reconciliationInterval: TimeSpan.FromMinutes(30));

        var createdPath = Path.Combine(root, "draft.txt");
        File.WriteAllText(createdPath, "release candidate alpha");

        Assert.True(WaitUntil(() => database.GetByPath(createdPath) is not null));
        Assert.Contains(database.Search("content:release candidate", limit: 10), r => r.FullPath == createdPath);

        File.WriteAllText(createdPath, "release candidate beta");
        Assert.True(WaitUntil(() =>
            database.Search("content:candidate beta", limit: 10).Any(r => r.FullPath == createdPath)));

        var renamedPath = Path.Combine(root, "release-notes.txt");
        File.Move(createdPath, renamedPath);

        Assert.True(WaitUntil(() =>
            database.GetByPath(createdPath) is null && database.GetByPath(renamedPath) is not null));

        File.Delete(renamedPath);
        Assert.True(WaitUntil(() => database.GetByPath(renamedPath) is null));
    }

    [Fact]
    public void Reconciliation_CatchesChangesMadeWhileWatcherWasOffline()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);

        var stalePath = Path.Combine(root, "stale.txt");
        File.WriteAllText(stalePath, "old content");

        using var database = new FileIndexDatabase(Path.Combine(temp.Path, "index.db"));
        var crawler = new FileCrawler(database);
        crawler.Crawl(new[] { root });

        // Simulate app being closed: filesystem changes happen without a watcher running.
        File.Delete(stalePath);
        var offlinePath = Path.Combine(root, "offline-change.txt");
        File.WriteAllText(offlinePath, "captured after restart");

        using var updater = new LiveFileIndexUpdater(
            database,
            new[] { root },
            debounceWindow: TimeSpan.FromMilliseconds(100),
            reconciliationInterval: TimeSpan.FromMilliseconds(250));

        Assert.True(WaitUntil(() =>
            database.GetByPath(stalePath) is null &&
            database.GetByPath(offlinePath) is not null &&
            database.Search("content:captured after restart", limit: 10).Any(r => r.FullPath == offlinePath)));
    }

    private static bool WaitUntil(Func<bool> predicate, int timeoutMs = 10_000, int pollMs = 50)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(pollMs);
        }

        return predicate();
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
