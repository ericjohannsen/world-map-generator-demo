# 3D preview

This document describes the interactive 3D terrain preview: the in-window view that displaces the active
stage's heightmap, shades it, and lets the user orbit, tilt, pan, and zoom into a mountain. Requirements
are in [../requirements/preview-3d.md](../requirements/preview-3d.md); the choice of renderer is recorded
in [ADR-017](decisions.md#adr-017--in-window-opengl-3d-preview-with-a-cpu-raster-fallback).

The short version: **one scene, two views, two rasterizers.** The regional view displaces a tangent-plane
window of the active heightmap; the globe view displaces a sphere with the same heightmap. Both share the
albedo, the lighting, the exaggeration presets, and the fallback renderer.

## Two views

The **regional** view is a plane tangent at the map window's centre, with longitude scaled by
`cos(centre latitude)`, so a 2:1 world map has the correct 2:1 aspect. The **globe** is a UV sphere displaced
radially by the same heightmap. Both are visualisations of the same `Heightmap`, not geodynamic surfaces.

The GPU path is the product; the CPU path exists so the feature still works when Avalonia cannot create an
OpenGL context, and it is the regression reference because it shares the mesh, camera, and albedo.

## Non-goals

- **Not a simulation.** The preview never samples the analytic terrain function for the *eroded* or
  *hydrological* stages; it consumes the heightmap those stages produced. This keeps 2D and 3D identical
  (P3D-9) at the cost of detail below the map's cell size, which is the same limit the 2D zoom has today.
- **Not a tile server (yet).** Detail follows the camera by rebuilding a window of the existing map at a
  finer vertex spacing. There is no disk cache and no multi-ring clipmap: the rebuild path is the seam
  where Stage 6 tiling will plug in. The globe is a single fixed-resolution sphere.
- **No animated globe.** Rotation is user-driven; there is no auto-spin, day/night cycle, cloud layer, or
  star field.

## Data flow

```
active Heightmap (float32 metres)
        │
        ├── TerrainMesh.Build(window, columns, rows, exaggeration) ──► regional mesh ─┐
        ├── TerrainGlobeMesh.Build(radius, lon/lat segments, exaggeration) ──► globe mesh ─┤
        │                                                                                  │
        └── TerrainPreview.RenderAlbedo() ──► RGBA8 colour texture ─────────────────────────┤
                                                                                           ├──► Terrain3DGlControl (GPU)
OrbitCamera (target, distance, yaw, pitch) ──► view-projection ────────────────────────────┴──► Terrain3DSoftwareRenderer (CPU)
```

### Albedo, not the preview image

`TerrainPreview.Render` bakes hillshading with a fixed sun. Feeding that image to a lit 3D surface shades
every slope twice: once in the texture, once by the 3D lighting. `TerrainPreview.RenderAlbedo` shares the
same land/ocean palette but returns flat colour. The 2D map keeps the shaded image; the 3D view uses the
albedo. This is P3D-5.

### Mesh

`TerrainMesh.Build` (in `WorldGen.Core`, no UI dependency) samples the map bilinearly, including longitude
wrap, and emits an interleaved mesh:

| Attribute | Type | Meaning |
| --- | --- | --- |
| position | `float3` | `x` east metres, `y` north metres, `z` elevation metres × exaggeration |
| normal | `float3` | central differences on the sampled grid, in the same frame |
| uv | `float2` | position in the source map, `[0,1]`, wrapped in `u` |

World extents use `metresPerDegreeLat = R·π/180` and `metresPerDegreeLon = R·π/180·cos(centreLat)`, with
`cos` clamped so polar windows stay finite. Elevations come from `Heightmap.Elevations` at full float
precision; nothing is quantised to 8-bit (P3D-2).

Vertex spacing is chosen from the camera:

```
windowHalfSize ≈ clamp(distance × 0.9, mapCell × 8, worldHalfSize)
columns        ≈ clamp(windowWidth / mapCell, 65, 1025) rounded down to a multiple of 64
rows           ≈ columns / 2 (world) or matched to the window aspect
```

This is the whole "zoom adds detail" mechanism: the same map, a smaller window, therefore denser vertices
and a smaller world-space triangle. At the map's own cell size the mesh stops gaining detail and the
triangle count stops growing; no amount of zoom invents data that is not there.

Rebuilds happen on a worker thread. The render loop swaps in the finished mesh atomically, so a rebuild
never blocks a frame (P3D-8). A rebuild is triggered when the camera target leaves the central 60 % of the
current window, when the window span changes by more than 30 %, or when resolution or exaggeration changes;
rebuilds are throttled to at most one every 200 ms, and the previous window remains visible in between.

### Globe mesh

`TerrainGlobeMesh.Build` (also in Core) emits a UV sphere: 512 × 256 segments for the GPU and 192 × 96 for the
software fallback.

| Attribute | Meaning |
| --- | --- |
| position | planet-centric metres: unit direction × (`R` + max(elevation, 0) × exaggeration) |
| normal | central differences of the displaced positions along longitude and latitude, falling back to the radial direction at the poles |
| uv | equirectangular map coordinates, seam duplicated with `u = 1` |

Oceans are clamped to the sea-level sphere. Displacing bathymetry would turn the water surface into a
rugged shell that reads as terrain; the ocean floor stays visible in the albedo instead. The seam column is
duplicated so the texture does not wrap backwards, and the pole rows collapse to a point, which is the
usual UV-sphere pinching trade-off.

The globe mesh is built once per map and exaggeration (not per camera move), on a worker thread, and is
handed to whichever rasteriser is active through the host's fixed-mesh slot. The regional camera cache is
bypassed entirely in globe mode.

The globe camera is a true orbit around the planet centre: target fixed at the origin, distance clamped to
`1.03R … 12R`, pitch allowed below the horizon (`±83°`). It is a separate `OrbitCamera` instance, so
switching modes preserves both viewpoints (P3D-14). The shader adds a thin atmospheric rim at the
silhouette (`1 − |n·v|`, cubed) and disables distance fog; the clear colour becomes near-black space.

### Exaggeration presets

Both views share three presets: **1×**, **10×**, and **50×**. Exaggeration multiplies mesh `z` (regional)
or radial displacement (globe) and is part of the mesh key, so changing it rebuilds the mesh; it never
touches the heightmap, the camera's real elevation readout, or any artifact (P3D-7). At 50× a regional
camera close to a peak can find itself inside the relief, which is why the ground clamp raises the eye and
pushes it outward until it clears the exaggerated surface.

### GPU path

`Terrain3DGlControl` derives from `Avalonia.OpenGL.Controls.OpenGlControlBase`. Avalonia binds a
framebuffer with a depth attachment and calls `OnOpenGlRender` on a dedicated GL context; the control draws
into it and Avalonia composites the result into the visual tree, so there is no airspace problem and normal
Avalonia controls can sit on top.

Shaders are supplied in two variants from one body: GLSL ES 1.00 for ANGLE/OpenGL ES and GLSL 1.20 for
desktop OpenGL. Attributes are bound to fixed locations before linking (`0` position, `1` uv, `2` normal)
so no attribute lookups are needed. Lighting is a directional sun plus ambient, with a distance haze toward
the sky colour; water is the ocean-coloured part of the albedo with a mild specular boost.

The albedo texture is capped at 4096×2048 and uploaded as RGBA8. For an 8192×4096 stage the texture is
downsampled; geometry and normals still come from the full-resolution map, so the silhouette and relief stay
correct even where the colour is softer.

### CPU path

`Terrain3DSoftwareRenderer` rasterises the same mesh with a z-buffer into an RGBA buffer. The host passes
the globe mesh and a space background in globe mode; otherwise the renderer asks the regional cache:

1. Transform vertices by the camera's view-projection on a worker thread.
2. Backface-cull and clip triangles with any vertex behind the near plane (whole-triangle rejection; the
   heightfield is a regular grid, so clipping artifacts at the frame edge are acceptable).
3. Rasterise with barycentric interpolation and perspective-correct UVs, writing colour and depth.
4. Shade with the same sun direction and albedo sampling.

Resolution scales with interaction: 0.5× while the camera moves, up to 1.0× when it settles. This is the
P3D-11 fallback budget. The renderer is intentionally simple: it is a correctness and availability
fallback, not a competitor to the GPU path.

### Picking and the cursor readout

`TerrainScene.TryPick(screenPoint, camera, viewport)` marches the camera ray against the heightmap using the
same bilinear sampler, with a coarse step followed by a bisection refinement. It returns latitude, longitude,
and elevation, which the host forwards to the existing cursor readout, so the readout means the same thing in
both views (P3D-9).

### Position HUD

A translucent, non-hit-test border sits in the top-right of the viewport and shows four monospaced lines in a
light 11 px font:

```text
LAT  --.----° N
LON ---.----° E
HDG  ---°
ALT  ----- m
```

- **2D:** the values follow the cursor; when the pointer has not moved they show the map centre. Heading is
  `000°` because the map is north-up, and altitude is the terrain elevation under that point.
- **3D:** the values follow the active camera at 10 Hz. Latitude/longitude come from the camera eye (regional
  tangent plane or globe direction), heading from the free-flight yaw or the regional orbit view direction,
  and altitude is above sea level in real metres. The displayed altitude divides out vertical exaggeration;
  globe orbit, which looks straight at the planet centre, shows `---°` for heading because no compass bearing
  is meaningful.
- The border has `IsHitTestVisible="False"`, so it never steals orbit, pan, zoom, or flight input.
- The HUD is hidden when no map is loaded. It is deliberately separate from the bottom cursor readout: the
  HUD is camera/position telemetry, while the cursor bar is the full inspection report for a picked cell.

## Camera

`OrbitCamera` is a spherical orbit. The regional camera orbits a ground target; the globe camera orbits the
planet centre with the target fixed at the origin:

| Field | Range / meaning |
| --- | --- |
| `Target` | regional: world metres, `z` snapped to the terrain surface; globe: origin |
| `Distance` | regional: 150 m … 4 × world diagonal; globe: 1.03R … 12R; zoom is exponential in wheel delta |
| `Yaw` | radians, wraps |
| `Pitch` | regional: 3° … 89° above the horizon; globe: −83° … +83° |
| `FieldOfView` | 55° vertical |

- **Left drag** orbits (yaw/pitch).
- **Middle drag, or Shift + left drag**, pans the target in the camera's ground plane, scaled by distance.
  Panning is regional-only; in globe mode every drag orbits.
- **Wheel** zooms toward the cursor: the target moves toward the picked point as distance shrinks, so zoom
  stays anchored to what the user is looking at.
- After any change, the target's `z` is set to `max(terrainHeight, 0)` and the eye is raised if it would be
  below the surface. This is P3D-6. In globe mode the camera is clamped outside the planet instead.
- Vertical exaggeration is a scene-level preset (1×, 10×, 50×), not a camera field.

### Free flight

`FreeFlightCamera` implements the same `IPreviewCamera` contract as the orbit camera, so the rasterisers,
picking, and mesh cache work unchanged. Its authoritative state is a **unit quaternion orientation** plus a
position and velocity; the legacy `Yaw`/`Pitch` scalars are derived from the quaternion for persistence and the
HUD, and assigning them rebuilds the orientation for older call sites. Local axes are +X = screen right,
+Y = view direction, +Z = screen up, so `Forward = Transform(+Y)`, `Up = Transform(+Z)` and
`Right = Forward × Up`.

Using a quaternion instead of geographic yaw/pitch is what removes pole gimbal lock: the orientation never
depends on the geographic north/east frame, which collapses at the poles. Geographic north/east are only used
when the HUD needs a compass bearing, and never feed back into the camera.

Movement is tangent to the local sphere at the current altitude: **W/S** follow the horizontal projection of
the look direction, **A/D** strafe along the screen-right vector, and **E/C** move along the local up. Looking
down does not dive. **Q/E** turn around the local vertical (the sphere normal), which keeps the up axis radial
and the flight direction tangent. Input is smoothed with an exponential velocity blend. Speed is
`clamp(height above ground × 0.2, 3 m/s, 50 km/s) × wheel multiplier`, with Shift ×4 and Ctrl ×0.25, so the
same keys work for a walk at 2 m and an approach from orbit. The wheel changes the multiplier continuously;
**+ / = / numpad +** and **− / _ / numpad −** change it in 0.1× steps and can reach exactly zero.

**Pole crossing.** After each globe step, the camera re-derives yaw/pitch from the pre-move *world-space* look
direction instead of keeping local yaw fixed. Local north flips by 180° at a pole; without the
re-derivation, holding **W** points back toward the pole on the far side and the camera bounces. With it, the
view direction stays continuous, the heading flips to the far-side bearing (000° → 180°), and flight
continues across the pole. The regional camera has a fixed north and does not need this step.

**Tangent integration.** Globe motion is not a straight world-space step. For each frame, the tangential
part of the velocity moves the position along the sphere and the radius stays fixed; only the intentional
climb/descend component changes the radius. The carried velocity is then projected back onto the new tangent
plane. The look yaw is re-derived from the world direction, while the *pitch angle* is preserved, so a level
view remains level as the local horizon rotates. Without these steps, a straight chord has an outward radial
component and the camera slowly climbs; preserving the raw 3D look vector also tilts the view upward. At high
speed either effect makes the planet drop out of frame. The regional camera uses the same tangent input but
keeps its fixed +Z up.

**Yaw-only turning.** **Q/E** applies a quaternion rotation about the local **vertical** (the radial
direction) in fixed **3° steps**, triggered by the keydown edge. Rotating about the radial keeps pitch and
altitude unchanged when the view is already pitched; rotating about the camera's own up would tilt the
horizon. The axis comes from the position, not from a geographic basis, so it is well defined at the poles.
The old implementation sampled a held key once per render tick, so a tap could be missed entirely and a long
frame could produce a much larger turn; the result depended on timer latency. The step is now independent of
frame rate. The app has a single window-level flight-key router (the host no longer adds a second key
handler), which prevents a press from being applied twice.

**View up.** `GetViewMatrix` uses the orientation's own up axis directly (`CreateLookAt(Position,
Position + Forward, Up)`). Because the orientation is a quaternion, the up vector is orthogonal by
construction and there is no threshold or polar fallback that can flip the frame near a pole or a
near-vertical look.

**Tangent updates.** On each globe frame the position moves along the sphere and the carried velocity is
projected back onto the new tangent plane. The world look heading from before the move is then combined with
the pitch from before the move to build a fresh orientation against the new surface normal, so a level view
stays level and a pitched view keeps its pitch as the local horizon rotates.

**Reset view.** Reset places the globe camera at the equator on the 0° longitude meridian, 100 km above sea
level, facing due east; the regional camera resets to the default orbit view. Saved `Yaw`/`Pitch` are
converted back into an orientation by `SetYawPitch` on restore, so a restored flight does not point straight
down.

Clamping:

- **regional:** `z >= displaySurface(x, y) + clearance`, with `x/y` inside the map. Clearance is
  `1.8 m × exaggeration`, so it stays 1.8 m in real units.
- **globe:** `|position| >= globeSurfaceRadius + 20 m × exaggeration`, where the surface radius is the
  displaced land or the sea-level sphere.

The regional mesh window follows the camera: focus is 35 % of the window ahead along the ground direction and
the half extent is `clamp(altitude × 8 + 4 km, 4 km, world diagonal)`. The globe uses its fixed mesh.

Input: **W/A/S/D** (or arrows) move along the tangent plane, **Space** climbs, **C** descends, **Q/E** turn
in fixed 3° steps, **+/-** change speed in 0.1× steps (zero is allowed), **Shift** boost, **Ctrl**
precision, left drag to look,
wheel to change speed, double-click to reset. Keys are routed at the window level, so they work after clicking
a panel button; a focused text box keeps its characters. Speed, height above ground, and the multiplier are
shown while flying.

Both orbit cameras, both free-flight cameras, the view mode, and the control mode are persisted independently,
so switching between 2D, regional 3D, globe, orbit, and fly does not lose the viewpoint (UI-7, P3D-14,
P3D-18).

### Reference grid

A faint grid at sea level gives motion over open water without competing with the terrain. It is on by
default and can be toggled in the 3D panel (`showGrid3D` in preferences).

- **Regional:** east/north metric lines across the current mesh window. The spacing is a "nice"
  1/2/5 × 10^n value chosen so about eight major lines span the window; subdivisions are one tenth of that.
- **Globe:** meridians and parallels over the visible patch, with the same major/subdivision ratio. The
  angular spacing adapts to altitude, so a 2 km flight sees sub-degree lines while an orbital view sees tens
  of degrees.
- **Placement:** lines are drawn at sea level (`z = 2 m` regionally, `r = R + 2 m` on the globe), so land
  occludes them and water shows them. This is deliberate: the grid is a water reference, not a floating
  overlay.
- **Style:** pale blue, major alpha 0.145, subdivision alpha 0.055, one pixel wide.
- **Rendering:** the GPU path uses a small line program and VBO with alpha blending and depth testing (depth
  writes disabled), with a small clip-space depth bias so the lines do not z-fight with the sea surface at
  orbital distances; the CPU fallback rasterises the same segments with a DDA against the terrain depth
  buffer.
- **Rebuild:** the grid is rebuilt at most every 250 ms when the camera focus/altitude or spacing changes.
  The builder caps the vertex count and doubles the spacing if a view would produce too many lines.

## Integration with the 2D viewer

`Terrain3DHost` owns both rasterizers, the camera, and the scene. It is a `Grid` with the active renderer,
a small status/hint overlay, and an API:

```csharp
host.SetScene(TerrainScene scene, bool resetCamera);  // null clears the view
host.SetViewMode(Terrain3DViewMode.Regional | Globe);
host.SetControlMode(Terrain3DControlMode.Orbit | Fly);
host.Preference = RendererPreference.Auto | Gpu | Cpu;
host.IsActive = true;                                  // attach and start rendering
host.VerticalExaggeration = 10f;                       // presets: 1, 10, 50
```

The host is placed in the existing viewport area and shown by a `3D` view-mode button. `SetScene` is
called whenever the active stage changes, using a `TerrainScene` built from the same `Heightmap` instance
the 2D view displays, so no resampling or restyling occurs on the switch (P3D-9). `IsActive = false` stops
rendering entirely; the GL control is hidden and stops requesting frames.

## Failure handling

| Failure | Response |
| --- | --- |
| Avalonia cannot create a GL context | `OnOpenGlInit` failure or a null GL version raises `RendererUnavailable`; the host switches to CPU and shows why. |
| GL context lost | `OnOpenGlLost` raises the same event; the host rebuilds GPU state on the next attach, or stays on CPU if it keeps failing. |
| Software renderer exception | Frame is skipped, status shows the error once, camera keeps working. |
| No map yet | Host shows a neutral prompt; no renderer is attached. |

A manual `GPU / CPU / Auto` selector lets the user pin a renderer for comparison and for machines where the
automatic probe is wrong (P3D-4).

## Performance budget

At 1920×1080, a 2048×1024 map and a 1025×513 mesh (≈ 1.05 M triangles):

| Stage | GPU | CPU fallback |
| --- | --- | --- |
| Vertex + fragment | ~1–3 ms | — |
| Vertex transform | — | 5–10 ms |
| Raster 960×540 | — | 25–40 ms |
| Mesh rebuild (worker) | 4–8 ms per 1025×513 build | same |
| Globe mesh (worker) | ~40 ms for 512×256 (GPU) + ~6 ms for 192×96 (CPU) | same |
| Texture upload (4096×2048) | ~30 ms, on stage change only | — |

The GPU path holds 60 fps comfortably. The CPU fallback is expected to sit near 15–20 fps at half resolution
during motion and sharpen when idle, which satisfies P3D-11 but is visibly a fallback.

## Limits and future work

1. **No sub-cell detail.** The mesh cannot show a ridge the source map did not resolve. Stage 6 tiling plus
   the analytic sampler is the fix; `TerrainMesh.Build` already takes an arbitrary window, which is the
   interface a tile source would replace.
2. **Single window, no rings.** Distant terrain is only present if it is inside the rebuilt window; the
   camera's far plane and the window extent are chosen to avoid a visible edge, but a multi-ring clipmap
   would let the near field be denser without dropping the far field.
3. **Flat sea.** Water is part of the heightfield surface at 0 m; there is no transparency, refraction, or
   shoreline foam. Rivers from Stage 4 are visible only through the albedo, not as geometry.
4. **Globe resolution is fixed.** The sphere uses 512 × 256 segments (≈ 0.7°) on the GPU and 192 × 96 on the
   CPU fallback, which is enough for a planet overview and a low-altitude approach but soft when flying close
   to the ground. Regional mode is the close-inspection view. Pole rows pinch, as with any UV sphere; a
   cube-sphere with per-face tiling is the upgrade path and would also remove the pinch.
5. **Globe picking uses the sea-level sphere.** The hit point on a 50× mountain sits slightly in front of the
   visible surface; the elevation readout still comes from the heightmap and is correct.

## Verification

- `P3D-*` acceptance is manual and visual, plus these machine checks:
  - the mesh builder's elevation range matches the source map after dividing by exaggeration;
  - a round-trip pick at a mesh vertex returns the same elevation within one cell;
  - the CPU and GPU renderers produce images within a small colour tolerance for the same camera pose.
- `Terrain3DHost.CaptureNextFrame()` reads the control framebuffer back with `glReadPixels` and raises
  `FrameCaptured`; the development harness uses it to compare terrain coverage and mean luminance
  between the GPU and CPU paths at the same pose.
- Determinism is unaffected: nothing here writes canonical artifacts.
