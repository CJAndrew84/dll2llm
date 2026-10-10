# API catalogue implementation progress

## TL;DR

The catalogue is no longer a filename-only prototype. Managed extraction, format-specific JSON imports, source evidence, bounded machine/human outputs, search and integrity checks are implemented. **Builds and all 38 regression tests passed on both Windows and Linux** for code commit `79424bb8c1fc0198ecf4eb8100aced09b6a15de5`.

[Verified CI run](https://github.com/CJAndrew84/dll2llm/actions/runs/38071503127) · [Usage guide](catalogue.md) · [Draft PR #2](https://github.com/CJAndrew84/dll2llm/pull/2)

The original `Program.cs` reflection implementation still emits 24 nullable warnings. They have not been suppressed. These passing fixture tests are not full OpenRoads installation validation or a claim that undocumented APIs are supported.

## Implemented and exercised

| Area | Delivered |
| --- | --- |
| Managed input | `PEReader`/`MetadataReader`; no target assembly loading; types, methods, constructors, overloads, properties, accessors, fields, events, generics, parameters, base/interface references and raw signature blobs |
| Existing extraction | Explicit `decoded-clr-metadata-v2` and `pe-export-table` adapters; unknown formats fail visibly |
| Native evidence | Export identity, name/ordinal, RVA and forwarder retained without fabricated callable signatures |
| Canonical records | Shared symbol/parameter model; source identity, token/JSON pointer, evidence and human-reference link |
| Human reference | Types grouped with their members; signature, parameters, accessors and evidence on bounded pages |
| Machine retrieval | JSONL shards, substring search, kind filter, bounded result count and optional JSON output |
| Output safety | UTF-8 byte budgets, streaming record writes, hierarchical indexes, manifest hashes and JSONL record counts |
| Regeneration | Staged publication, complete-output preservation on failed extraction, obsolete-page removal, unchanged-output preservation and refusal to delete user-added files |
| Git access | Small Markdown entry points and reference pages excluded from LFS; JSONL explicitly tracked by LFS; effective attributes tested with Git |
| Failure handling | Separate LFS pointer, malformed JSON, unsupported format, empty input, partial metadata and native-out-of-scope reporting |
| Automated validation | Windows/Linux builds plus 38 synthetic regression tests, including a managed assembly with its dependency deliberately absent |

## Remaining priorities

| Priority | Work | Acceptance condition |
| --- | --- | --- |
| P0 | Full hydrated OpenRoads 2024 and mixed-mode assembly validation | Counts reconcile with source metadata; discrepancies have explicit explanations; no silent loss |
| P0 | Apply shared output budgets to the original reflection generator and any separate local `analyze` pipeline | Every output-producing route enforces the same sub-100 MB policy |
| P0 | Richer managed evidence | Generic constraints, custom attribute tokens and assembly-reference identity now emitted; tests and difficult signatures still require validation |
| P1 | Native header/PDB pipeline | Recover declared class/member information with source evidence, keeping unknown ABI data unknown |
| P1 | Indexed catalogue storage | A queryable SQLite implementation respects the same per-file distribution policy; no single oversized database |
| P1 | Explicit relationship and dependency graph | Source-backed edges rather than inferred runtime behaviour; cross-assembly identities resolved or marked unresolved |
| P1 | XML documentation and human task guides | Descriptions cite source documentation; inferred or runtime-verified guidance remains distinguished |
| P1 | Version comparison and incremental extraction | Stable symbol comparison across builds; unchanged files skipped without hiding changed dependencies |
| P2 | MCP retrieval and documentation website | Built on validated catalogue/search interfaces; preserve private/public publication boundaries |

## Scope notes

`Complete` means the selected extraction scope processed without recorded failures. Native binaries skipped by `--mode managed` are reported as outside that scope. Missing fields in legacy JSON remain unknown. The catalogue does not prove runtime correctness, supported API status or full native class coverage.

If a new run has invalid/missing inputs, it cannot replace an existing complete catalogue. Console diagnostics explain the failure; use a fresh output directory to retain an incomplete diagnostic package. The generator does not modify repository history, publish binaries, merge its PR or run Git write commands on behalf of the user.

## Latest P0 changes

- Legacy reflection output now enforces a 95,000,000-byte limit (CI passed for commit `129afbf6805ece3085e1145f91f25260c195ee36`).
- CLR metadata catalogue now records generic constraints, custom attribute tokens and assembly references; these are metadata evidence, not decoded attribute constructor values.
- OpenRoads 2024 installation testing is delegated to a Windows laptop with the hydrated binaries and SDK. Do not mark that validation complete until the user runs it.
