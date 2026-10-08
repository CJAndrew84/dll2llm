# DLL2LLM — Extended API Documentation Generator

DLL2LLM generates evidence-backed, human-readable and AI-readable documentation from managed .NET assemblies, native Windows PE exports and optional C++ SDK declarations. This fork extends the original Autodesk-oriented tool with product-root scanning and a unified documentation pipeline.

> **Development status:** The Windows .NET 10 build and synthetic smoke tests pass on the feature branch. Extraction against OpenRail Designer 2026 and OpenRoads Designer 2026 SDK binaries has **not** been validated. Native exports and symbol-name matches do not imply a supported callable API.

## Quick start: one command

Requires Windows x64 and .NET 10. Use your **actual installation path**, not the illustrative path below.

```powershell
dotnet build dll2llm.sln -c Release
dotnet run --project dll2llm.csproj -c Release -- analyze `
  --source "C:\Path\To\InstalledProduct" `
  --output "C:\Repos\ProductAPI"
```

When SDK files are installed separately:

```powershell
dotnet run --project dll2llm.csproj -c Release -- analyze `
  --source "C:\Path\To\InstalledProduct" `
  --sdk "C:\Path\To\ProductSDK" `
  --output "C:\Repos\ProductAPI"
```

The pipeline inventories DLLs, creates SHA-256 manifests, extracts CLR metadata and native PE exports, optionally parses SDK headers and import libraries, and composes the results into a searchable index and Markdown documentation. Stages are recorded in `analysis-report.json`; failures produce a nonzero exit code.

**Note:** the one-command pipeline currently produces metadata-based Markdown reference pages; it does not automatically run the original reflection-based, XML-enriched skill generator for every managed assembly. Optional SDK extraction invokes `clang++` and `llvm-readobj` and requires them on PATH.

## Generated layout

```text
ProductAPI/
  inventory.json
  manifest.json
  analysis-report.json
  composition-report.json
  managed/                 CLR metadata JSON per assembly
  native/                  PE export JSON per native DLL
  sdk-manifest.json        If --sdk is supplied
  sdk-headers.json         If --sdk is supplied
  sdk-libraries.json       If --sdk is supplied
  index/
    api-index.json         Unified symbol catalogue
  skills/
    SKILL.md               Evidence-source skill entry point
    INDEX.md
    clr-metadata.md
    pe-export-table.md
    clang-ast.md            Only when declarations are available
  documentation/
    README.md              Human entry point
    SKILL.md               AI agent navigation and evidence rules
    INDEX.md               Domain and API navigation
    domains/               Heuristically classified capability guides
    api/                   Namespace-grouped exact reference pages
    relationships/         Limitations; verified graph not yet implemented
    search/api-index.json
    reports/composition-report.json
    sources/README.md
```

Files are generated only when their input data is available; for example, `clang-ast.md` is absent without extracted C++ declarations.

## Documentation model

Three complementary views are provided:

1. **Domains:** keyword-based, multi-label capability classification (civil geometry, terrain, rules, EC data, DGN/CAD, UI, annotation, ProjectWise and other). Classification is heuristic, not an assertion of SDK support.
2. **API reference:** extracted type and member names, decoded signatures where possible, evidence type and source location.
3. **Search:** JSON index for programmatic exact lookup and an AI-readable skill entry point.

A true inheritance/call/dependency graph is not yet generated. Do not infer runtime relationships from shared names.

## Extraction capabilities

| Command | Function | Output |
|---|---|---|
| `analyze --source ROOT --output DIR [--sdk SDK]` | Full product-root orchestration | Complete output tree |
| `scan ROOT [--output FILE]` | Recursive managed/native DLL classification | Inventory JSON |
| `metadata ASSEMBLY [--output FILE]` | Static CLR metadata, decoded signatures and raw blobs | Metadata JSON |
| `exports DLL [--output FILE]` | Native PE export names, ordinals, forwarded exports | Export JSON |
| `headers SDK [--include DIR] [--delta-manifest FILE]` | Clang AST C++ declarations | Header JSON |
| `import-libs SDK [--tool llvm-readobj]` | COFF import/library symbols | Library JSON |
| `pdb ROOT [--tool llvm-pdbutil]` | Optional public PDB symbol dump | PDB JSON |
| `demangle EXPORTS_JSON [--tool llvm-undname]` | Optional MSVC decorated-name decoding | Demangled JSON |
| `correlate MANAGED HEADERS EXPORTS` | Candidate managed/native name matches | Correlation JSON |
| `index MANAGED HEADERS EXPORTS OUTDIR` | Legacy three-input unified index | Index JSON and Markdown |
| `search-index INDEX QUERY` | Search the unified symbol index | Console |
| `manifest ROOT [--previous FILE]` | SHA-256 baseline and changed/deleted file report | Manifest JSON |
| `merge-headers OLD DELTA MANIFEST OUTPUT` | Incremental header catalogue merge | Header JSON |
| `audit-recovery SKILLS_DIR [--baseline FILE]` | Count recovered and skipped Markdown markers | Audit JSON |
| `self-test` | Internal synthetic regression assertions | Console and exit code |

The original `dll2llm file1.dll [file2.dll] --output DIR [--xml FILE] [--install DIR]` workflow remains available. It now supports repeatable `--reference-dir DIR` and `--resolution-report FILE` options.

## CLR signature recovery

The original reflection-based Markdown generator attempts a metadata-token fallback when method or property signature formatting fails due to unresolved types. Successful recovery is marked `[RECOVERED: CLR metadata]`. The metadata-only extractor separately reads CLR type/method/field definitions without loading assemblies and retains raw ECMA-335 signature blobs. Recovered signatures should be treated as metadata evidence, not a guarantee of runtime loadability.

```powershell
dotnet run --project dll2llm.csproj -c Release -- "C:\Path\To\Managed.dll" `
  --reference-dir "C:\Path\To\ProductRoot" `
  --resolution-report "C:\Results\resolution.json" `
  --output "C:\Results\OriginalSkill"
```

The resolver searches managed DLLs recursively, checks assembly identity/version, reports missing or incompatible dependencies and skips native DLLs as CLR references.

## Incremental extraction

```powershell
dll2llm manifest "C:\SDK" --output baseline.json
dll2llm headers "C:\SDK" --output headers-full.json
# After updating the SDK:
dll2llm manifest "C:\SDK" --previous baseline.json --output updated.json
dll2llm headers "C:\SDK" --delta-manifest updated.json --output headers-delta.json
dll2llm merge-headers headers-full.json headers-delta.json updated.json headers-merged.json
```

The manifest compares SHA-256 hashes of DLL, PDB, LIB and header files. The merge retains unchanged header records, replaces changed records and removes deleted headers. This is an **incremental header pipeline**, not yet an incremental cache for the entire `analyze` command.

## Native analysis and confidence

- PE exports reveal names and ordinals, not complete C++ class contracts.
- Clang AST declarations are derived from headers and may require product-specific include paths, defines and compiler configuration.
- `llvm-pdbutil` can inspect public PDB information where available.
- `llvm-undname` can demangle MSVC-style decorated export names.
- Correlation currently matches normalised names. Qualified matches may be labelled medium confidence when unambiguous; short-name or ambiguous matches remain low confidence.
- Neither confidence level proves a managed/native call relationship or supported ABI.

## Quality, safety and limitations

- Scanning and metadata/PE inspection are static; they do not execute inspected binaries. Reflection-based generation still loads managed assemblies.
- Inaccessible directories are skipped during binary inventory, but some SDK-wide recursive enumeration paths still need hardening.
- Very large installations may create large JSON/Markdown outputs and require substantial disk space and processing time.
- Avoid scanning an output folder inside the product root, to prevent generated content from being rediscovered.
- Do not commit proprietary SDK headers, DLLs, PDBs, or decompiled material.
- Automatic domain classification is not LLM-authored technical guidance.
- The initial `analyze` command does not yet automatically include optional PDB/demangling output in the unified index.
- Exact per-product compatibility, native symbol completeness and recovery rates require local validation.

## Tests and release

```powershell
dotnet build dll2llm.sln -c Release
dotnet run --project dll2llm.csproj -c Release -- self-test
```

GitHub Actions builds on Windows with .NET 10 and runs generic inventory, metadata and native export smoke tests. The feature branch has passing CI. Before merging, validate against installed Bentley products, review output quality and compare the AR-ORDSDK baseline of 2,811 skipped signatures using like-for-like counting.

See [Bentley extraction guide](docs/BENTLEY-EXTRACTION.md), [incremental workflow](docs/INCREMENTAL-WORKFLOW.md) and [release gate](docs/RELEASE-GATE.md).

## Attribution

Forked from [Autodesk Platform Services dll2llm](https://github.com/autodesk-platform-services/dll2llm). Preserve upstream licence and attribution.
