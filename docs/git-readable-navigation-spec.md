# Git-readable navigation output for product analysis

## Objective
Keep detailed generated metadata in Git LFS, but make API discovery work through ordinary GitHub file readers and AI coding agents without downloading LFS blobs.

## New CLI switch
`dll2llm analyze --source <install> --output <version> --git-navigation`

Existing output remains unchanged unless this switch is specified.

## Required implementation
1. After inventory and analysis, generate small **ordinary Git** entry points: `README.md`, `SKILL.md`, `INDEX.md`, `documentation/README.md`, `documentation/domains/INDEX.md`, `documentation/relationships/INDEX.md`, `documentation/search/INDEX.md`. These should describe provenance, coverage, limitations, and link to compact navigation.
2. Generate `navigation/assemblies/*.md` grouped by assembly family and `navigation/domains/*.md` for civil objects, alignments, rules/dependencies, EC/persistence, geometry, tools, and native exports. Every entry must contain its exact original assembly metadata path and relevant symbols; do not infer functionality from names alone.
3. Generate `navigation/symbols/<prefix>.md` with bounded file sizes (default 64 KiB), alphabetical ordering, and links to source metadata. Split large groups deterministically; never truncate symbol entries.
4. Generate `navigation/reports/coverage.md` containing inventory counts, analyzed vs failed, skipped signatures, managed/native counts, and dependency failures. Include data provenance and a clear distinction between indexed metadata and experimentally validated behaviour.
5. Keep `index/*.ndjson`, large domain guides, raw metadata and detailed reports in LFS. Write `.gitattributes` rules that explicitly exempt `navigation/**`, root entry points and compact documentation indexes from LFS; Git attributes must be checked with `git check-attr filter -- <path>`.
6. Avoid duplicate large copies: `documentation/search` and `index` should not both store full shard payloads. Select one canonical location and reference it.
7. Produce deterministic, stable filenames and content, sorted ordinally; atomic write/replace; avoid modifying unchanged files; escape Markdown and normalise relative links.
8. Implement a validator that checks: no Git LFS pointers in entry points, all relative links resolve, every navigation reference points to an existing file, no generated file exceeds its configured budget, and index counts reconcile with inventory. Non-zero exit on failure.
9. Add tests for missing dependencies, duplicate assembly names, symbols with nested/generic names, no native binaries, empty inputs, >64KiB splitting, Windows paths, and repeated generation.
10. Update README with Windows PowerShell 5.1 examples and `git lfs track` / `git check-attr` instructions. Do not commit original vendor DLL binaries.

## Acceptance criteria
- GitHub Contents API returns actual Markdown text for all entry points, not `version https://git-lfs.github.com/spec/v1`.
- A user can navigate from `2024/README.md` to a civil rule type, identify its source assembly and precise metadata file, and distinguish API signatures from verified runtime behaviour.
- The existing `analyze` pipeline remains backward-compatible and builds on .NET 10.
- Run `dotnet build -c Release` and automated tests; report results and limitations.

## Implementation note
The current repository main branch contains a single `Program.cs` CLI and a .NET 10 project. Earlier generated product-analysis files in AR-ORDSDK/2024 use Git LFS pointers even for tiny README/INDEX files. Implement this as a product-analysis output feature rather than changing the existing single-DLL skill generation contract.
