namespace FileLantern.Core.Indexing;

public interface ITextContentExtractor
{
    bool CanExtract(string filePath, string extension);

    string? ExtractText(string filePath);
}
