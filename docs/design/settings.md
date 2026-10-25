# Local settings

This document describes how the desktop app remembers user preferences and session state. Requirements are
in [../requirements/settings.md](../requirements/settings.md); the decision is recorded in
[ADR-018](decisions.md#adr-018--split-versioned-atomic-json-settings).

## Storage layout

```
%LOCALAPPDATA%\WorldGen\            (macOS: ~/Library/Application Support/WorldGen)
  preferences.json                  durable choices
  preferences.json.bak              previous good copy
  session.json                      resumable state
  session.json.bak
  cache\                            content-addressed stage bundles; explicit cleanup, never auto-evicted
  logs\                             diagnostics
```

The cache is working state, not settings: it is described in [artifact-cache.md](artifact-cache.md) and
[requirements/cache.md](../requirements/cache.md). It is safe to delete while the app is closed; the next
run regenerates what is needed. It is not deleted automatically, because it can hold a user's only copy of
an expensive stage result.

Nothing is written beside the executable or into the current working directory. `LocalApplicationData` is
used rather than roaming data because window geometry, GPU renderer choice, and camera state are
machine-specific.

## Schemas

Both files are indented JSON. Property names are matched case-insensitively on load; the examples below use
camelCase for readability, while the app writes the declared PascalCase names.

### `preferences.json`

```json
{
  "schemaVersion": 1,
  "window": { "x": 120, "y": 80, "width": 1540, "height": 940, "maximized": false, "hasSavedPosition": true },
  "defaultWidth": 1024,
  "defaultHeight": 512,
  "rendererPreference": 0,
  "verticalExaggeration": 1.0,
  "threeDViewMode": 0,
  "threeDControlMode": 0,
  "showGrid3D": true
}
```

| Field | Meaning | Validation |
| --- | --- | --- |
| `window.x`, `window.y` | Physical pixels, top-left of the window | must intersect a connected screen; otherwise ignored |
| `window.width`, `window.height` | Device-independent units, normal (non-maximized) size | clamped to `640×480 … screen work area` |
| `window.maximized` | Last non-minimized state was maximized | boolean |
| `window.hasSavedPosition` | Distinguishes "never placed" from "(0, 0)" | boolean |
| `defaultWidth`, `defaultHeight` | Resolution preset used at startup | snapped to `1024/2048/4096/8192` |
| `rendererPreference` | `0` Auto, `1` GPU, `2` CPU | clamped |
| `verticalExaggeration` | `1`, `10`, or `50` | snapped to nearest preset |
| `threeDViewMode` | `0` regional, `1` globe | clamped |
| `threeDControlMode` | `0` orbit, `1` free flight | clamped |
| `showGrid3D` | reference grid on/off | boolean, default `true` |

### `session.json`

```json
{
  "schemaVersion": 1,
  "generation": { "seed": 1337, "plates": 13, "landFraction": 0.29, "...": "..." },
  "exportPrefix": "output/world",
  "threeDEnabled": false,
  "camera": { "targetX": 0, "targetY": 0, "targetZ": 0, "distance": 1500000, "yaw": 0.55, "pitch": 0.85 },
  "globeCamera": { "targetX": 0, "targetY": 0, "targetZ": 0, "distance": 15300000, "yaw": 0.8, "pitch": 0.42 },
  "regionalFlight": { "x": 0, "y": 0, "z": 2000, "yaw": 0, "pitch": -0.35, "speedMultiplier": 1 },
  "globeFlight": { "x": 4069712, "y": 4190332, "z": 2608588, "yaw": 0, "pitch": -0.2, "speedMultiplier": 1 }
}
```

`generation` holds the last-used values for every Stage 1–4 control as typed numbers/booleans. The UI formats
them for display and parses them back only at the boundary; the store never sees strings.

Session state is safe to delete: the app falls back to built-in defaults and a centred window.

## Lifecycle

```
startup
  AppSettingsStore.Load()
    read preferences.json ──► validate/clamp ──► apply window geometry
    read session.json     ──► validate/clamp ──► fill controls, orbit/flight cameras, 3D state
    (legacy ui-settings.json imported once)

running
  window moved/resized/maximized ──┐
  resolution/renderer/exaggeration ─┤
  any settings TextBox changed ─────┼──► update model ──► RequestSave() ──► 600 ms debounce ──► atomic write
  camera settled (drag/wheel/reset)─┘
  3D opened/closed, mode switched ──┘

closing
  CaptureWindowState() (minimized: no-op)
  CaptureSessionFromControls()
  CaptureCameras()
  SaveNow()                          // flush, cancel pending debounce
```

The store owns two mutable models. The window mutates them in response to events and calls `RequestSave`;
nothing writes on a timer except the debounce.

## Atomic write

```csharp
WriteAllText(path + ".tmp", json);
if (File.Exists(path))
{
    File.Copy(path, path + ".bak", overwrite: true);
    File.Move(path + ".tmp", path, overwrite: true);
}
else
{
    File.Move(path + ".tmp", path);
}
```

- The temporary file is in the same directory, so the rename is a same-volume operation.
- The `.bak` copy is the previous good file; a failed write leaves both the old live file and the backup.
- Any `IOException`/`UnauthorizedAccessException` becomes a warning, never an exception that reaches the UI
  event loop.
- Two instances can interleave; the last writer wins and each write is still complete.

## Validation and migration

- `SettingsValidation.Normalize` runs after every load and clamps every field: presets are snapped, numbers
  are checked for `NaN`/infinity, cameras and flight cameras are dropped if any component is non-finite,
  flight pitch and speed are clamped, and the export prefix falls back to `output/world` if blank.
- `schemaVersion` is compared to the current version. Older files go through a migration switch; newer files
  are read best-effort with a warning. The version is rewritten to the current value on the next save.
- A parse failure quarantines the file as `preferences.corrupt-2026-10-23T21-14-05.json`, records a warning,
  and continues with defaults. The user can still start the app and inspect the bad file.
- The legacy `ui-settings.json` (a single flat record with string values) is imported when neither new file
  exists: `SelectedWidth/Height` become the default resolution, the 3D fields become preferences, the
  generation strings are parsed into `session.generation`, and the file is renamed to
  `ui-settings.imported-<timestamp>.json` so it is not imported twice.

## Window geometry

Restoring geometry is the part with real failure modes, so it is explicit:

1. **Capture only normal bounds.** On `PositionChanged`, `SizeChanged`, and `WindowState` changes:
   - `Minimized`: do nothing. The last normal/maximized values stay.
   - `Normal`: record `Position` and `Width`/`Height` as the normal bounds, and remember `Normal`.
   - `Maximized`: remember `Maximized`; keep the previously recorded normal bounds.

   Captures are coalesced for 300 ms. Maximize, minimize, and restore emit position/size events *before*
   `WindowState` settles, and an immediate capture would record the maximized rectangle as the normal size.
   As a second guard, a window covering at least 96 % of the work area is treated as maximized even if the
   state flag is still lagging.
2. **Persist both facts.** `window.maximized` stores the last non-minimized state; the bounds are always the
   normal ones. That is why restoring a maximized window can return to the correct pre-maximize size.
3. **Restore safely.** On startup:
   - clamp the size to the primary screen's work area (minus a margin) and to `640×480 …`;
   - if `hasSavedPosition` and the rectangle intersects a connected screen by at least `120×80` physical
     pixels, place it there; otherwise centre on the primary screen;
   - if `maximized`, apply the normal bounds first and set `WindowState = Maximized` once the window is
     opened.
4. **Never restore minimized.** Minimized is a transient state, not a preference (ST-9).

DPI is not stored: `Position` is physical and `Width`/`Height` are logical, so a DPI change between runs can
shift the window slightly. The on-screen check and centring fallback keep that harmless.

## Threading

All model mutation and all file I/O happen on the UI thread. The debounce is an Avalonia `DispatcherTimer`,
so no lock is needed and no write can interleave with a model update. Stage generation runs on worker threads
and never touches settings.

## Failure handling

| Failure | Behaviour |
| --- | --- |
| Settings directory cannot be created | Warning; app runs with in-memory settings. |
| File is corrupt | Quarantine, defaults, warning in the diagnostics pane. |
| File is read-only | Warning on save; the app keeps running. |
| Schema newer than the app | Best-effort read, warning, saved back as the current schema on the next write. |
| Screen configuration changed | Saved rectangle rejected; window centred. |
| App killed | At most the last debounce interval of changes is lost. |

## Testing

- Round-trip: load → mutate → save → load returns the same values.
- Corrupt file: write `{`, load, assert defaults + quarantine + warning.
- Missing file: assert defaults and that a save creates both files.
- Legacy import: a copy of the old `ui-settings.json` produces the expected preferences/session split and is
  renamed.
- Window geometry: off-screen coordinates are rejected; a maximized preference restores maximized with the
  saved normal size; a minimized window at close does not change the saved state.
- Live updates: changing resolution, renderer, exaggeration, and 3D mode is visible in `preferences.json`
  after the debounce.

## Limits

- No cloud sync or roaming profile; settings are per machine.
- No per-world camera or parameter sets until a project file exists.
- No settings UI/editor; the files are human-readable JSON and can be edited while the app is closed.
