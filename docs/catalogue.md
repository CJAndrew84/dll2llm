# Metadata-first product API catalogue

## TL;DR

The `catalog` command now has two inputs: existing metadata JSON, or managed DLL/EXE files read directly with `PEReader`/`MetadataReader`. Both produce the same versioned symbol model, JSONL shards and a human-readable Markdown reference. This is static evidence, not a claim about supported SDK interfaces or verified runtime behaviour.

The original single-DLL reflection/skill command is unchanged. The new catalogue pipeline does not load vendor assemblies, require their dependencies to load, copy DLLs into its output, or alter the installation. An `analyze` pipeline is not present in this branch; these commands do not pretend to update a separate local implementation.

## Build and run (Windows PowerShell 5.1)

From the dll2llm checkout:

```powershell
git fetch origin
git switch feature/git-readable-navigation
git pull --ff-only
dotnet build dll2llm.csproj -c Release
```

Inspect the installed managed assemblies directly. Use a NEW dedicated output folder, outside the input directory:

```powershell
dotnet run --project dll2llm.csproj -c Release -- catalog --mode managed --source "C:\Program Files\Bentley\OpenRoads Designer 2024.00" --output "C:\Repos\AR-ORDSDK\2024-catalog"
```

Public/protected APIs are the default for this mode. Add `--include-nonpublic` for internal discovery. An internal declaration being present does not make it a supported extension API. Native PE files encountered in managed mode are counted as skipped, not decoded or described as C++ classes.

Alternatively, import the existing extraction after hydrating its LFS objects:

```powershell
git -C "C:\Repos\AR-ORDSDK" lfs pull
dotnet run --project dll2llm.csproj -c Release -- catalog --mode metadata --source "C:\Repos\AR-ORDSDK\2024" --output "C:\Repos\AR-ORDSDK\2024-catalog"
```

The format-specific adapters currently support `decoded-clr-metadata-v2` and `pe-export-table`. Unknown formats fail visibly. The legacy JSON importer preserves the original inventory's visibility coverage rather than silently filtering it; some older records omit visibility, nesting or property tables altogether.

Search and validate:

```powershell
dotnet run --project dll2llm.csproj -c Release -- catalog --source "C:\Repos\AR-ORDSDK\2024-catalog" --search "RuleManager" --limit 20
dotnet run --project dll2llm.csproj -c Release -- catalog --source "C:\Repos\AR-ORDSDK\2024-catalog" --search "GetRules" --kind method --json
dotnet run --project dll2llm.csproj -c Release -- catalog --source "C:\Repos\AR-ORDSDK\2024-catalog" --validate
```

Exit codes: `0` success, `1` processing/validation failure or incomplete catalogue, `2` invalid arguments. Search returns `0` with no matches; it does not invent an answer. Query limits must be between 1 and 1,000.

## Output contract

| Output | Purpose | Storage |
| --- | --- | --- |
| `README.md`, `SKILL.md`, `*-INDEX.md` | Small entry points and paged navigation | Normal Git |
| `reference-*.md` | Symbols, signatures, parameters, relationships, accessors and evidence | Normal Git |
| `symbols-*.jsonl` | One independently retrievable symbol per record | LFS |
| `sources-*.jsonl` | Relative path, SHA-256, byte count and processing status | LFS |
| `diagnostics-*.jsonl` | LFS pointer, invalid source, unsupported format and extraction failures | LFS |
| `report.json`, `catalog-manifest.json` | Coverage, exact generated files, sizes, hashes and record counts | Normal Git |
| `.gitattributes` | Explicit plain-text/LFS rules for this generated folder | Normal Git |

Machine records link to their Markdown page and a stable symbol anchor. Reference pages are generated from the same records, not separately paraphrased by an LLM. Every symbol retains source-file identity and either a metadata token or JSON pointer. Raw ECMA-335 signature blobs remain available alongside decoded display signatures.

## Size and reliability policy

Every file produced by the new `catalog` pipeline is checked against a **95,000,000-byte ceiling**, below the requested 100 MB limit. JSONL shards target **64 MiB**; Markdown pages are limited to **48,000 UTF-8 bytes** including headers/newlines. Counters measure actual bytes, not string character counts. Records are streamed without repeatedly re-encoding the whole shard. A single oversized record is rejected, never silently truncated. Navigation is hierarchical so an index cannot grow without bound.

This is our chosen distribution limit, not GitHub's general LFS limit. GitHub documents a 100 MiB limit for ordinary Git files and separate, plan-dependent LFS limits. The stricter project budget is retained regardless.

Generated output is staged and validated before publication. Failed output writes leave the old catalogue intact. Regeneration replaces only an intact, marked, generator-owned directory, removes obsolete generated pages and refuses unrecognised/user-added files or changed generated content. Identical regeneration keeps the existing directory untouched. Keep authored guides/notes outside the generated directory. Use a fresh directory when migrating from older outputs that have no ownership marker.

Fresh runs with unreadable inputs can emit an explicitly incomplete catalogue with diagnostics and a nonzero exit code. The search command refuses incomplete catalogues. `--validate` checks sizes, hashes and JSONL counts, not semantic correctness or runtime behaviour.

The original reflection-only skill generator and any separately developed product-analysis pipeline have NOT yet been brought under this shared size policy.

## Git publication

Do not migrate or rewrite existing repository history for this change. Stage only the new generated folder after reviewing its contents and distribution permissions:

```powershell
git -C "C:\Repos\AR-ORDSDK" check-attr filter -- "2024-catalog/README.md" "2024-catalog/symbols-00001.jsonl"
```

Expected: README `filter: unset`; JSONL `filter: lfs`. Local Git attributes such as `.git/info/attributes` can override repository rules, so check the effective attributes. The tool does not run `git add`, commit, push or change global Git configuration.

## Tests

```powershell
dotnet build tests/Fixtures/Target/Target.csproj -c Release
dotnet run --project tests/dll2llm.Tests.csproj -c Release -- tests/Fixtures/Target/bin/Release/net10.0/Target.dll
```

The regression harness has no extra test-framework package dependency. CI builds and runs it on Windows and Linux. Fixtures are synthetic; no proprietary DLLs or extracted vendor implementation code are included. The test copies a target DLL without its referenced dependency and checks that signatures survive and the target assembly is never loaded.

## Known gaps / next priorities

- Full-corpus testing against the hydrated OpenRoads 2024 dataset and installed mixed-mode binaries.
- Rich custom-attribute decoding, XML documentation, generic constraints, nullable annotations, explicit implementation relationships, assembly-reference graphs and edge-case CLR signature fidelity.
- Native header/PDB parsing and native ABI evidence. Importing exports is not native class reconstruction.
- An indexed SQLite/search service, semantic version comparison and an MCP interface. Current search scans bounded JSONL shards; it is not indexed search.
- Incremental extraction (unchanged-output detection is implemented, skipping unchanged extraction is not).
- Human-authored usage guides and experimentally verified workflows linked to, but not mixed with, static metadata.
- Cleanup of existing nullable warnings in the original reflection generator.

## Primary technical references

- https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pereader?view=net-10.0
- https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.metadatareader?view=net-10.0
- https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-large-files-on-github
- https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-git-large-file-storage
