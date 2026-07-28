# file-lantern — Plan

## Scope

A Windows desktop utility that indexes local files and provides instant search:

- **Filename search** across selected drives/folders (fast prefix + fuzzy).
- **Full-text content search** for common text/document formats.
- **Filters**: extension, size, modified date, path, and `content:` predicate.
- **Incremental updates** so the index stays fresh without full rescans.
- **Optional local-AI mode** for natural-language queries via a tiny local model.
- **Privacy-first**: index stored locally; zero cloud dependency for core value.

In scope for v1: single-user desktop app, indexing local + mounted drives,
text/document full-text extraction, a responsive search UI, and an optional
AI query translator that targets a local OpenAI-compatible endpoint.

## Architecture / tech approach

- **Language/runtime:** .NET 8, C#.
- **UI:** WPF (Windows-native, low overhead) with a search-as-you-type box and
  virtualized results list.
- **Index store:** SQLite with FTS5 for full-text; a normalized table for file
  metadata (path, name, ext, size, mtime, hash). FTS5 gives fast content search
  without an external service.
- **Filesystem crawl:** initial recursive enumeration; incremental updates via
  `ReadDirectoryChangesW` (FileSystemWatcher) with a debounced work queue.
- **Content extraction:** pluggable extractors — plain text/code/markdown first,
  then PDF (e.g. PdfPig) and Office (OpenXML) in later milestones. Binary files
  index metadata only.
- **Query parser:** small grammar for prefixes/filters (`content:`, `ext:`,
  `size:`, `modified:`) compiled to SQL + FTS queries.
- **Local-AI adapter:** optional module that sends the raw query to a local
  OpenAI-compatible endpoint (Ollama / llama.cpp) and expects structured JSON
  (keywords + filters) back; result is fed through the same query engine.
  Fails closed to normal search if the endpoint is unavailable.
- **Testing:** xUnit for the parser, indexer, and query engine.

## Milestones

- **M1 — Filename index + instant search (core):** crawl selected roots, store
  metadata in SQLite, search-as-you-type on filenames.
- **M2 — Full-text content index:** FTS5 content indexing for text/code/markdown.
- **M3 — Incremental/live updates:** FileSystemWatcher-driven index maintenance.
- **M4 — Query filters:** `ext:`, `size:`, `modified:`, `content:` predicates.
- **M5 — Optional local-AI queries:** natural-language → structured query adapter.
- **M6 — Windows packaging:** installer + self-contained build.

## Non-goals

- No cloud sync, remote index, or account system.
- No cross-platform GUI in v1 (Windows-first; core index logic kept portable).
- Not a file manager — search and open only, no bulk file operations in v1.
- No mandatory AI: every core feature must work with AI disabled.
- No indexing of remote/network drives beyond user-mounted paths in v1.

## Packaging target for Windows

- Primary: self-contained single-file **.NET 8 win-x64** build.
- Installer: MSIX or a lightweight Inno Setup / WiX MSI for Windows 10/11.
- Distribute via GitHub Releases; portable ZIP as a secondary option.
