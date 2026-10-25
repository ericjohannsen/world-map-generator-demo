# Local settings requirements

## Goal

Remember the user's working environment between runs — window geometry, defaults, renderer choice, camera —
without becoming a second source of truth for a world and without ever preventing the app from starting.

Settings are **not** an input to the generator. The pipeline's determinism contract (NFR-1) covers seed and
explicit options; a stale preferences file must never change an artifact.

## Stores

Two JSON files under the per-user application data directory:

| File | Purpose | Loss impact |
| --- | --- | --- |
| `preferences.json` | Durable choices that should survive a reinstall of the working session: window geometry, default resolution, renderer, exaggeration, 3D mode | Annoying |
| `session.json` | Resumable working state: last generation parameters, export prefix, 3D open state, regional and globe cameras | Harmless |

A future saved-world/project file owns the seed and terrain parameters of that world. Preferences and
session state **must not** be used as the world definition.

## Requirements

| ID | Requirement |
| --- | --- |
| ST-1 | Settings **must** be stored per user in `LocalApplicationData/WorldGen` (macOS: `~/Library/Application Support/WorldGen`), not in the install directory, the working directory, or the registry. |
| ST-2 | Preferences and session state **must** be separate files, so a corrupt or reset session cannot destroy durable choices. |
| ST-3 | Writes **must** be atomic: write a temporary file in the same directory, flush it, then replace the target. The previous good file **must** be kept as `.bak`. A crash mid-write must never leave a truncated live file. |
| ST-4 | Each file **must** carry a `schemaVersion`. Loading a newer version **must** warn and continue best-effort; loading an older version **must** migrate. Unknown fields are ignored and missing fields take defaults. |
| ST-5 | Every loaded value **must** be validated and clamped. A malformed or corrupt file **must** be quarantined as `<name>.corrupt-<timestamp>.json`, defaults used, and a warning surfaced — never an exception at startup. |
| ST-6 | Values **must** be stored with their natural types (numbers as numbers, booleans as booleans), not as display strings. |
| ST-7 | Settings **must** be saved as they change, coalesced by a short debounce, and flushed on window close. Losing the last few seconds of a drag is acceptable; losing the session because the app was closed with the X button is not. |
| ST-8 | The window position, size, and maximized/normal state **must** be restored on the next run, but only if the saved rectangle is still substantially visible on a connected screen. A window restored onto a disconnected monitor is a defect. |
| ST-9 | A minimized window **must not** overwrite the saved geometry or state. Closing while minimized keeps the last maximized/normal geometry and the last maximized/normal flag. |
| ST-10 | Renderer choice, exaggeration preset, 3D view mode, control mode (orbit/fly), grid visibility, and default resolution are preferences. Generation parameters, export prefix, 3D open state, both orbit cameras, and both free-flight cameras are session state. |
| ST-11 | Settings **must not** affect the canonical artifacts, determinism, or any stage. The generator receives only explicit CLI/UI options. |
| ST-12 | Settings **must not** contain secrets. If credentials are added later they belong in the platform credential store, not these files. |
| ST-13 | Multiple running instances may race; last writer wins. No settings file may be left locked or corrupt by the race. |
| ST-14 | On first run, defaults **must** be written so the files are discoverable for support. An existing legacy `ui-settings.json` **must** be imported once and then retired. |
| ST-15 | All settings I/O **must** be synchronous and fast enough for the UI thread (files are small); no settings write may block stage computation. |

## Acceptance

- Move and resize the window, close, relaunch: the same rectangle is restored.
- Maximize, close, relaunch: the window is maximized and restoring it returns to the previous normal size.
- Minimize, close, relaunch: the window restores to the last maximized/normal state, not minimized.
- Move the window to a second monitor, disconnect it, relaunch: the window opens on a visible screen.
- Change the resolution, renderer, exaggeration, or 3D mode and kill the process: the change survives (within
  the debounce window).
- Truncate `preferences.json` by hand and relaunch: the app starts with defaults, the bad file is quarantined,
  and a warning is shown.
- Delete `session.json`: preferences survive and the app starts with default generation parameters.
- Inspect `preferences.json`/`session.json`: no terrain, mesh, cache, or derived data is present.
