# Bentley extraction development guide

This branch adds experimental static scanning and dependency diagnostics. These commands have **not yet been validated on OpenRail Designer 2026**.

## Inventory all DLLs (native and managed)

```powershell
dotnet run -- scan "C:\Program Files\Bentley\OpenRail Designer 2026" --output ".\inventory.json"
```

## Extract managed DLL documentation with additional dependency locations

```powershell
dotnet run -- "C:\Path\To\Bentley.CifNET.4.0.dll" `
  --reference-dir "C:\Program Files\Bentley\OpenRail Designer 2026" `
  --resolution-report ".\resolution.json" `
  --output ".\skills"
```

The resolver searches supplied directories recursively, indexes managed assembly identities, and attempts exact version matches. A native DLL is not a managed reference candidate. A missing dependency may still be a native runtime component, a framework mismatch, or a different load-context problem.

## Read CLR metadata without executing the assembly

```powershell
dotnet run -- metadata "C:\Path\To\Bentley.CifNET.4.0.dll" --output ".\metadata.json"
```

Metadata mode records raw ECMA-335 method and field signatures. It does **not** yet decode those blobs into C# types or merge them with the generated skill Markdown.

## Acceptance criteria before claiming recovery

1. Build on Windows x64 with .NET 10.
2. Verify managed/native classification on known binaries.
3. Verify exact-version dependency matching and missing-directory reporting.
4. Confirm metadata extraction succeeds on a managed assembly with intentionally unavailable dependencies.
5. Compare skipped-signature counts against the 2,811-signature AR-ORDSDK baseline.
6. Keep native/managed and ORD/OpenRail product provenance separate.
7. Do not commit Bentley proprietary binaries, SDK headers or decompiled source.

## Roadmap

- Decode metadata signatures to readable, evidence-tagged types.
- Add automated integration tests and CI.
- Extract native PE exports, then optional PDB symbol metadata.
- Parse SDK headers and import libraries when the SDK is installed.
- Cross-reference managed and native symbols with explicit confidence levels.
- Produce a unified, searchable index and validate on OpenRail 2026 and ORD 2026.
