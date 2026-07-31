using System.Text;

namespace FileLantern.Core.Indexing;

public sealed class PlainTextContentExtractor : ITextContentExtractor
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "rst", "log", "csv", "tsv",
        "json", "yaml", "yml", "xml", "html", "htm", "css",
        "js", "jsx", "ts", "tsx", "mjs", "cjs",
        "cs", "csproj", "sln", "vb", "fs", "fsproj",
        "py", "java", "kt", "kts", "go", "rs", "swift",
        "c", "h", "cpp", "hpp", "cc", "hh", "m", "mm",
        "php", "rb", "sh", "bash", "zsh", "ps1", "bat", "cmd",
        "sql", "toml", "ini", "cfg", "conf", "gitignore"
    };

    private static readonly HashSet<string> SupportedExtensionlessFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "README", "LICENSE", "NOTICE", "CHANGELOG", "Makefile", "Dockerfile"
    };

    public bool CanExtract(string filePath, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (SupportedExtensions.Contains(extension))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(extension))
        {
            return false;
        }

        var fileName = Path.GetFileName(filePath);
        return SupportedExtensionlessFileNames.Contains(fileName);
    }

    public string? ExtractText(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (LooksBinary(filePath))
        {
            return null;
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    private static bool LooksBinary(string filePath)
    {
        const int sampleBytes = 4096;
        var buffer = new byte[sampleBytes];

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = stream.Read(buffer, 0, buffer.Length);

        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0)
            {
                return true;
            }
        }

        return false;
    }
}
