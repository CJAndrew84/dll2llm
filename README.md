# DLL2LLM — Managed and Native API Documentation Generator

**Generate documentation for humans and AI assistants from a software installation root.**

This fork extends the original DLL2LLM with recursive product scanning, CLR metadata extraction, native Windows PE export analysis, optional C++ SDK header and import-library inspection, PDB symbol discovery, incremental manifests, and automatic domain-organised documentation.

> Native unmanaged DLLs **are supported for static PE export analysis**. This is not full C++ decompilation: internal non-exported classes and callable ABI contracts cannot be reliably reconstructed from exports alone.

## Quick start

Windows x64 and the .NET 10 SDK are required to build the fork.

```powershell
git clone https://github.com/CJAndrew84/dll2llm.git
cd dll2llm
git switch feature/native-pe-inventory
dotnet build dll2llm.sln -c Release

dotnet run --project dll2llm.csproj -c Release -- analyze `
  --source "C:\Path\To\InstalledProduct" `
  --output "C:\Repos\ProductAPI"
```

Optionally provide `--sdk "C:\Path\To\ProductSDK"` for SDK headers and import libraries. This requires `clang++` and `llvm-readobj` on PATH. Paths above are examples.

## What is generated?

```text
ProductAPI/
  documentation/
    README.md                Human entry point
    SKILL.md                 LLM instructions
    INDEX.md                 Domain and API navigation
    domains/                 Automatically classified capability guides
    api/                     Evidence-backed namespace reference pages
    relationships/           Scope and limitations
    search/api-index.json    Searchable symbol index
    reports/
    sources/
  skills/                    Origin-grouped Agent Skill files
  index/api-index.json       Unified machine-readable index
  managed/                   Per-assembly CLR metadata
  native/                    Per-DLL native exports
  inventory.json
  manifest.json
  composition-report.json
  analysis-report.json
```

With `--sdk`, the pipeline also creates `sdk-headers.json`, `sdk-libraries.json` and `sdk-manifest.json`.

## Capabilities and limits

| Input             | Extracted evidence                                     | Limitation                            |
| ----------------- | ------------------------------------------------------ | ------------------------------------- |
| Managed .NET DLL  | Types, methods, fields, decoded and raw CLR signatures | Not proof of runtime loadability      |
| Native DLL        | PE exports, ordinals, RVAs and forwarders              | Not complete internal C++ classes     |
| C++ SDK headers   | Clang AST declarations and type relationships          | Requires correct compiler environment |
| COFF .lib         | Import/library symbol inventory                        | Not all libraries are import stubs    |
| PDB               | Optional public debug symbols                          | Only available symbols                |
| Decorated exports | Optional MSVC demangling                               | Not a supported API guarantee         |

## Commands

| Command                                            | Purpose                                 |
| -------------------------------------------------- | --------------------------------------- |
| `analyze --source ROOT --output DIR [--sdk SDK]` | Complete product scan and documentation |
| `scan ROOT`                                      | Managed/native DLL inventory            |
| `metadata DLL`                                   | Static CLR metadata                     |
| `exports DLL`                                    | Native PE export analysis               |
| `headers SDK`                                    | C++ AST extraction                      |
| `import-libs SDK`                                | COFF symbol inspection                  |
| `pdb ROOT`                                       | PDB public symbol inspection            |
| `demangle EXPORTS_JSON`                          | MSVC name demangling                    |
| `correlate MANAGED HEADERS EXPORTS`              | Candidate managed/native matches        |
| `index MANAGED HEADERS EXPORTS DIR`              | Build index from individual inputs      |
| `search-index INDEX QUERY`                       | Search indexed symbols                  |
| `manifest ROOT`                                  | SHA-256 file inventory and deltas       |
| `merge-headers OLD DELTA MANIFEST OUTPUT`        | Merge incremental header results        |
| `audit-recovery SKILLS_DIR`                      | Measure recovery markers                |
| `self-test`                                      | Run internal regression tests           |

## Original .NET skill generation remains supported

```powershell
dotnet run --project dll2llm.csproj -c Release -- "C:\Path\To\ManagedApi.dll" `
  --reference-dir "C:\Path\To\ProductRoot" `
  --resolution-report "C:\Results\resolution.json" `
  --output "C:\Results\Skill"
```

This reflection-based workflow supports multiple DLL arguments, `--xml` and `--install`. Failed method/property formatting can fall back to exact CLR metadata tokens and marks recovered entries `[RECOVERED: CLR metadata]`. The `analyze` pipeline uses separate metadata-based reference generation and does not automatically run the XML-enriched legacy generator for every assembly.

## Quality, validation and security

The Windows .NET 10 build and synthetic smoke tests have passed on this feature branch. **OpenRail Designer 2026 and OpenRoads Designer 2026 SDK validation remains outstanding.** The previously reported 2,811 skipped AR-ORDSDK signatures have not yet been remeasured with this implementation.

Domain organisation is heuristic, and name-based managed/native correlation is exploratory rather than a verified call graph. Static binary inspection does not execute inspected DLLs, but the legacy reflection workflow loads managed assemblies. Keep generated output outside the scanned source directory and do not commit proprietary vendor binaries or SDK materials.

For full installation instructions, all CLI options, incremental extraction, evidence confidence, generated files and release criteria, read the [complete feature guide](docs/FEATURE-GUIDE.md), [incremental workflow](docs/INCREMENTAL-WORKFLOW.md) and [release gate](docs/RELEASE-GATE.md).

## Licence and attribution

MIT licence; see [LICENSE](LICENSE). Originally created by Joao Martins for [Autodesk Platform Services](https://github.com/autodesk-platform-services/dll2llm). This fork retains upstream attribution and the original managed DLL skill-generation workflow.
