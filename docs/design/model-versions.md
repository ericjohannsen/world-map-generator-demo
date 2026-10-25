# Model versioning

Requirements are in [../requirements/model-versions.md](../requirements/model-versions.md).

## Four axes

| Axis | Constant | Scope | Purpose |
| --- | --- | --- | --- |
| Stage model | `ModelVersions.BaseTopology`, `Erosion`, `Climate`, `Hydrology` | One per stage | Behaviour identity; part of the cache key |
| Settings schema | `ModelVersions.SettingsSchema` | Whole settings document | Parameter shape and migrations |
| Cache schema | `ModelVersions.CacheSchema` | `StageBundle` layout | Binary compatibility |
| Result hash (planned) | derived | One artifact | Integrity and duplicate detection; not stored yet |

The model versions are deliberately **not** a single global pipeline number. A hydrology fix must not throw
away the Stage 1 cache.

## Registry

`src/WorldGen.Core/models.json` is embedded in Core and loaded by `ModelRegistry`. Each entry carries:

```json
{
  "version": 1,
  "status": "dev",
  "compatibility": "initial",
  "reason": "Initial tracked base-topology model; predates the registry.",
  "releasedIn": null,
  "introduced": [],
  "removed": []
}
```

- `status`: `dev`, `released`, or (if an entry is missing) `unknown`.
- `compatibility`: `metadata-only`, `performance-only`, `output-changing`, or `schema-breaking`.
- `releasedIn`: release identifier, null until the release pipeline marks it.
- `introduced` / `removed`: parameter names, for humans and migration review.

`ModelRegistry.ValidateCurrent()` fails if any version declared in code is absent from the registry. The
registry is embedded, so a build cannot silently ship a version that has no explanation.

**All current models are `dev`.** The release pipeline is future work; when it exists, it flips entries to
`released` and records `releasedIn`. Cleanup reads the current status, not a snapshot from when the artifact
was created.

## Bump policy

| Change | Model version | Settings schema | Cache schema |
| --- | --- | --- | --- |
| Numeric bug fix | bump | usually unchanged | unchanged |
| New parameter, default changes output | bump | bump | unchanged |
| New parameter, default preserves output and math is byte-identical | no bump (golden test) | bump | unchanged |
| New parameter with compatibility default for old projects | no bump if verified | bump | unchanged |
| Refactor proven byte-identical | no bump | unchanged | unchanged |
| Refactor with float-order change | bump | unchanged | unchanged |
| Manifest/preview/UI only | no bump | unchanged | unchanged |
| New bundle array or field | maybe | maybe | bump |
| New stage | new stage version | bump | unchanged |

The **golden-hash test** is the arbiter for “byte-identical”. It runs representative seeds and compares the
stage arrays, not the manifest or previews.

## Parameter-to-version mapping

The version alone is not enough. Three things answer “which parameters belong to which version”:

1. **Versioned settings schema** with migrations. Old projects run through a migration chain; new fields take
   a *compatibility default* that reproduces the old behaviour rather than the new default.
2. **Resolved settings in every sidecar.** `stage.json` stores the settings used, including defaults, so an
   artifact explains itself.
3. **The registry’s `introduced`/`removed` lists**, which let a reviewer see what changed between versions.

## Compatibility and old worlds

- Old cache entries are never deleted by a model bump; they remain under their old keys.
- A world/project (future) references its stage keys, so it continues to load the old model.
- New generations use the new key. The app labels a cached stage with its model version.
- “Regenerate with current model” is an explicit action; it creates a new variant rather than replacing the
  old terrain.
- A model bump is therefore a **new identity**, not a destructive migration.

## Provenance

`stage.json`, the bundle header, and the public manifest record the stage model version, settings schema
version, and cache schema version. The manifest format is version 4. Tool/git versions belong in provenance
fields, never in the cache key.

## Cleanup interaction

- `released` models are retained by default.
- `dev` models may be pruned when unreferenced and explicitly selected.
- `unknown` models are left alone.
- A future world-aware cleanup protects every key referenced by a world before applying size/age rules.

## Limits

- The registry is manual. A developer must update it when bumping a version; the validation test catches
  missing entries, not a misleading `reason` text.
- Settings migration is currently schema 1 with no historical migrations. The compatibility-default mechanism
  is designed but not yet exercised.
- No release pipeline exists; `releasedIn` is always null.
