# Bentley SDK extraction — validation and release gate

This branch adds native PE export inspection, managed metadata decoding, recursive managed dependency resolution, Clang C++ declaration extraction, COFF import-library inspection, optional PDB public symbols, MSVC demangling, low-confidence correlation, and a unified searchable index.

## Requirements
- Windows x64, .NET 10 SDK
- Optional LLVM tools: `clang++`, `llvm-readobj`, `llvm-pdbutil`, `llvm-undname`
- Bentley products/SDK installed locally for Bentley-specific testing

## Example commands

```powershell
dotnet build dll2llm.sln -c Release
dotnet run --project dll2llm.csproj -c Release -- self-test
dotnet run --project dll2llm.csproj -c Release -- scan "C:\Bentley\Product" --output "C:\Results\inventory.json"
dotnet run --project dll2llm.csproj -c Release -- metadata "C:\Bentley\Product\Bentley.CifNET.4.0.dll" --output "C:\Results\managed.json"
dotnet run --project dll2llm.csproj -c Release -- exports "C:\Bentley\Product\Native.dll" --output "C:\Results\exports.json"
dotnet run --project dll2llm.csproj -c Release -- headers "C:\Bentley\SDK" --include "C:\Bentley\SDK\include" --output "C:\Results\headers.json"
dotnet run --project dll2llm.csproj -c Release -- import-libs "C:\Bentley\SDK" --output "C:\Results\libs.json"
dotnet run --project dll2llm.csproj -c Release -- pdb "C:\Bentley\SDK" --output "C:\Results\pdb.json"
dotnet run --project dll2llm.csproj -c Release -- demangle "C:\Results\exports.json" --output "C:\Results\demangled.json"
dotnet run --project dll2llm.csproj -c Release -- correlate "C:\Results\managed.json" "C:\Results\headers.json" "C:\Results\exports.json" --output "C:\Results\correlation.json"
dotnet run --project dll2llm.csproj -c Release -- index "C:\Results\managed.json" "C:\Results\headers.json" "C:\Results\exports.json" "C:\Results\index"
dotnet run --project dll2llm.csproj -c Release -- search-index "C:\Results\index\api-index.json" "RuleManager"
```

Paths are placeholders. LLVM and SDK versions must be configured to match the installed product.

## Known limitations
- Metadata-only output is separate from the original Markdown skill generator; recovered signatures are not automatically substituted for skipped reflection members.
- Raw C++ declarations may include system headers; SDK header compilation requires accurate macros, include directories and compiler flags.
- Native export and PDB symbols do not establish a supported callable API.
- Correlation currently uses low-confidence name matching; no managed/native call graph is inferred.
- Some import libraries are static archives rather than import stubs.
- There is no incremental content-hash cache yet.
- Native binaries and Bentley SDK content must not be committed to this repository.

## Required release evidence
- [ ] Windows CI passes on the final commit
- [ ] Managed dependency resolver tested against missing, duplicate and version-mismatched references
- [ ] CLR metadata recovery merged into generated skill documentation
- [ ] Native symbols tested with known PE/PDB fixtures
- [ ] C++ AST verified against actual SDK headers and compiler configuration
- [ ] Source/version provenance and cross-reference quality reviewed
- [ ] Baseline 2,811 skipped AR-ORDSDK signatures remeasured after regeneration
- [ ] OpenRail Designer 2026 extraction validated locally
- [ ] OpenRoads Designer 2026 + SDK extraction validated when installed

Do not mark the project complete or merge to main until these release gates are satisfied.
