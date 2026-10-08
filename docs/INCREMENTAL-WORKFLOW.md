# Incremental Bentley SDK workflow

1. Run a full `manifest` and `headers` extraction against the SDK.
2. Store the manifest and header JSON **outside Git**, especially for proprietary Bentley SDK material.
3. After an SDK update, run `manifest --previous baseline.json`.
4. Run `headers --delta-manifest updated-manifest.json` to parse only modified/new headers.
5. Run `merge-headers previous-headers.json delta-headers.json updated-manifest.json merged-headers.json`.
6. Use the merged header catalogue for `correlate` and `index`.

Example:

```powershell
dll2llm manifest "C:\Bentley\SDK" --output baseline.json
dll2llm headers "C:\Bentley\SDK" --output headers-full.json
# After SDK changes:
dll2llm manifest "C:\Bentley\SDK" --previous baseline.json --output updated.json
dll2llm headers "C:\Bentley\SDK" --delta-manifest updated.json --output headers-delta.json
dll2llm merge-headers headers-full.json headers-delta.json updated.json headers-merged.json
```

The merge refuses to silently omit a changed header. Deleted headers are removed. Preserve the prior full catalogue until the merged result has been validated.

## Recovery measurement

```powershell
dll2llm audit-recovery "C:\Bentley\GeneratedSkills" --output recovery.json
```

This counts `[SKIPPED ...]` and `[RECOVERED: CLR metadata]` markers. The original 2,811 figure comes from a separate AR-ORDSDK ingestion report and may not use exactly the same counting method. Compare like-for-like generated corpora and review differences before claiming recovered counts.

## Limits to completion

CI uses generic fixtures and Windows binaries. Actual OpenRail Designer 2026 and OpenRoads Designer 2026 SDK compatibility requires running the extraction on the installed product machine. No Bentley proprietary binaries or headers should be uploaded to this repository. Name-based native/managed correlation is exploratory, not an API compatibility guarantee.
