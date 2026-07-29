namespace FileLantern.Core.Indexing;

public sealed record CrawlResult(int RootsScanned, int FilesIndexed, int SkippedPaths);
