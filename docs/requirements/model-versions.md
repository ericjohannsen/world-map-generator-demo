# Model versioning requirements

## Goal

Know exactly which algorithm produced a terrain, which parameters belonged to that algorithm, and whether
that model was ever released. The same identifiers drive cache keys, provenance, migrations, and eventual
cleanup of models that no user references.

## Identifiers

| ID | Meaning | Changes when |
| --- | --- | --- |
| `modelVersion[stage]` | The algorithm that produces a stage's numbers | Code, constants, defaults, or operation order can change output |
| `settingsSchemaVersion` | Shape and meaning of the settings document | Parameters are added, removed, renamed, or their meaning changes |
| `cacheSchemaVersion` | Layout of the cached stage bundle | Field set or serialization changes |
| `resultHash` | Integrity/identity of the bytes | Derived, never “changed”; planned, not yet stored |

Model versions are **per stage** (`s1` base topology, `s2` erosion, `s3` climate, `s4` hydrology). A hydrology
change must not invalidate Stage 1.

## Requirements

| ID | Requirement |
| --- | --- |
| MV-1 | Every stage **must** declare an integer `modelVersion` in code. A stage result must never be produced without one. |
| MV-2 | A checked-in registry (`models.json`) **must** describe every released or development model version: stage, version, status, compatibility, reason, `releasedIn`, introduced parameters, removed parameters. |
| MV-3 | Status is exactly `dev`, `released`, or `unknown`. **Until the release pipeline exists, every current model is `dev`** and `releasedIn` is null. |
| MV-4 | The app/CLI **must** validate at startup or in tests that every current code version exists in the registry. A missing entry is a build error, not a silent fallback. |
| MV-5 | A stage's model version **must** be bumped when code, constants, defaults, or operation order can change that stage's output. It **must not** be bumped for UI-only, preview-only, or manifest-only changes. |
| MV-6 | A performance-only refactor may keep the version only if a golden-hash regression test proves byte-identical output for representative seeds. |
| MV-7 | A parameter added with a default that preserves old behavior **must** also be handled by settings migration: an old project keeps the compatibility default, while a new project may use a new default. |
| MV-8 | Old projects **must** keep loading their original cached results after an upgrade. A model bump creates a new variant; it must not silently replace the old world’s terrain. |
| MV-9 | Every cached stage sidecar and every public manifest **must** record the stage model version, settings schema version, and cache schema version. |
| MV-10 | The cache key **must not** include the assembly version, git commit, runtime version, or UI state. Those are provenance only. |
| MV-11 | The registry **must** be the source of truth for cleanup: released models are retained unless explicitly selected; `dev` models may be pruned when unreferenced; `unknown` models are not pruned automatically. |
| MV-12 | The release pipeline (future) **must** flip a registry entry to `released` and record `releasedIn`. Until then, no model is released. |

## Compatibility classes

| Class | Meaning | Cache effect |
| --- | --- | --- |
| `metadata-only` | Manifest/preview/UI changed; numeric result unchanged | No stage invalidation; regenerate derived output |
| `performance-only` | Refactor proven byte-identical | No bump, or a bump labelled for provenance |
| `output-changing` | Same settings produce different numeric arrays | New stage key; old cached variants retained |
| `schema-breaking` | Bundle layout cannot be read without migration | `cacheSchemaVersion` bump; old entries are legacy |

## Acceptance

- `dotnet run --project src/WorldGen.Cli -- --cache-list` shows a status and compatibility for every entry.
- Removing a current version from `models.json` makes the validation test fail.
- Changing a stage's model version produces a new key and does not reuse the old entry.
- Loading an old project after a model bump still resolves its old stage keys.
- All current entries report `dev` / `initial`.
