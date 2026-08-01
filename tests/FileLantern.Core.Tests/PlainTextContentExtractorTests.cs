using FileLantern.Core.Indexing;
using Xunit;

namespace FileLantern.Core.Tests;

public sealed class PlainTextContentExtractorTests
{
    [Theory]
    [InlineData("note.txt")]
    [InlineData("README")]
    [InlineData("Program.cs")]
    [InlineData("journal.md")]
    public void CanExtract_ReturnsTrue_ForSupportedTextAndCodeFiles(string fileName)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, fileName);
        File.WriteAllText(path, "content");

        var extractor = new PlainTextContentExtractor();
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        Assert.True(extractor.CanExtract(path, extension));
    }

    [Fact]
    public void ExtractText_ReturnsFileContent_ForTextFile()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "todo.md");
        File.WriteAllText(path, "- [ ] ship M2\n- [ ] write tests");

        var extractor = new PlainTextContentExtractor();

        var extracted = extractor.ExtractText(path);

        Assert.Equal("- [ ] ship M2\n- [ ] write tests", extracted);
    }

    [Fact]
    public void ExtractText_ReturnsNull_ForBinaryLikeFile()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "image.bin");
        File.WriteAllBytes(path, new byte[] { 0x01, 0x02, 0x00, 0x04, 0x05 });

        var extractor = new PlainTextContentExtractor();

        var extracted = extractor.ExtractText(path);

        Assert.Null(extracted);
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
