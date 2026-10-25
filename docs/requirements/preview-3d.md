# 3D preview requirements

## Goal

The world map answers *where* things are. A 3D preview answers *what it would feel like to stand there*:
the shape of a ridge, the fall of a valley, the scale of a mountain belt. It is an inspection tool for
terrain that already exists — it must never change the canonical data or the exported artifacts.

The 3D view is deliberately a **regional, camera-following heightfield view**, not a planetarium. A globe
is a separate presentation mode ([PX-5](pipeline.md#stage-7--presentation-and-export)); this view exists to
fly into terrain.

## Requirements

| ID | Requirement |
| --- | --- |
| P3D-1 | The UI **must** provide an in-window 3D terrain view with height displacement, colour mapping, pan, tilt, orbit, logarithmic zoom, and **free flight**, in two modes: a **regional** view over a map window and a **globe** view of the whole planet. No external browser, helper process, or network access is required. |
| P3D-2 | Geometry **must** be sampled from the active stage's float32 `Heightmap` in metres. Elevations must not be quantised below the 16-bit PNG window before they reach the mesh; colour is a derived albedo, never the canonical store. |
| P3D-3 | The 3D view **must** render on the GPU through Avalonia's OpenGL control (`OpenGlControlBase`), using the depth buffer that Avalonia attaches to the control's framebuffer. |
| P3D-4 | A CPU software renderer **must** be available as a fallback and **must** produce a usable image of the same scene (same camera, same mesh, same albedo) when OpenGL is unavailable, fails to initialise, or is lost. Selection is automatic, with a visible status and a manual override. |
| P3D-5 | The colour map **must** be rendered without baked hillshading. Lighting is computed in the 3D view, otherwise slopes are shaded twice and the image is unusable at grazing angles. |
| P3D-6 | The camera **must** clamp its target to the terrain surface and the viewpoint above it, so that tilt and zoom cannot pass through the ground or under the sea floor. Tilt is limited to roughly 3–89° from horizontal. |
| P3D-7 | The view **must** offer vertical exaggeration at **1×, 10×, and 50×**, selectable in both regional and globe modes. Exaggeration affects the preview only and **must not** feed back into any stage or export. |
| P3D-8 | Mesh detail **must** follow the camera: the sampled window and the vertex spacing shrink as the camera zooms in, up to the resolution of the source map. The mesh **must** be rebuilt off the render thread so that panning and tilting stay responsive. |
| P3D-9 | 2D and 3D views **must** show the same terrain for the same stage. Switching modes must not silently reload, resample, or restyle the surface. Coordinates shown in the cursor readout **must** agree between the two views at the same point. |
| P3D-10 | The 3D view **must not** affect determinism or artifacts: it is read-only, performs no simulation, and writes nothing unless the user explicitly exports a capture. |
| P3D-11 | Rendering performance **must** hold at least 30 fps at 1920×1080 on an integrated GPU for a 2048×1024 map, and the software fallback **must** stay interactive (at least 8 fps during drag) by rendering at reduced resolution while the camera moves. |
| P3D-12 | The implementation **must not** add a third-party runtime dependency or a WebView. Only the framework's OpenGL support and the .NET base class library may be used. |
| P3D-13 | The globe **must** displace a sphere by the same heightmap and albedo as the regional view, keep oceans on the sea-level sphere (bathymetry stays in the colour, not the silhouette), and have no antimeridian seam. Pole pinching from the UV sphere is accepted and documented. |
| P3D-14 | Globe camera, mode, and exaggeration **must** be independent of the regional camera and persist between runs. Switching modes **must not** reload or restyle the terrain. |
| P3D-15 | Both the regional and globe views **must** offer a free-flight control mode with movement along the tangent plane at the current altitude, strafing, climb/descend, an independent look direction, and a speed control. **Q/E must** turn around the local vertical (the sphere normal); orbit remains available as the overview mode. |
| P3D-16 | Free flight **must** clamp the camera above the visible surface (terrain or sea) with an eye clearance, so it cannot pass through a mountain, under the sea, or inside the globe. The regional camera **must** stay inside the map window. |
| P3D-17 | Flight speed **must** scale with height above the ground so the same controls work at bird height and at orbital approach, with a boost (×4) and a precision mode (×0.25). The wheel and the **+ / = / numpad +** and **− / _ / numpad −** keys **must** adjust speed; the keys step by 0.1× and reach exactly zero. The UI **must** show speed, height above ground, and the multiplier while flying. |
| P3D-18 | Flight keys **must** work without clicking a specific control, except when a text box has focus; the control mode and both free-flight cameras **must** persist between runs. |
| P3D-19 | The 3D view **must** offer a reference grid, enabled by default: a metric grid in regional mode and a lat/long graticule in globe mode, with major lines and fainter subdivisions. The grid **must** sit at sea level so land occludes it, and the on/off state **must** persist. |
| P3D-20 | The map/3D viewport **must** show a non-interactive heads-up display with latitude, longitude, heading, and altitude in a light, small font. In 2D the heading is north-up (`000°`) and altitude is the terrain elevation under the readout; in 3D the values follow the active camera, with altitude in real metres (vertical exaggeration removed). The HUD **must not** block pointer input. |
| P3D-21 | Globe free flight **must** cross a pole continuously. Holding forward through a pole **must not** bounce the camera back; the world-space look direction is preserved and the local heading flips to the far-side bearing (for example, `000°` becomes `180°`). |
| P3D-22 | Globe tangent flight **must** preserve altitude and level pitch. Forward motion **must** be integrated on the sphere rather than as a straight world-space step, so holding **W** does not gradually climb away or tilt a level view until the planet drops out of frame. Only intentional climb/descend changes altitude. |
| P3D-23 | **Q/E** turning **must** change yaw only. It **must** be edge-triggered with the same fixed step per keypress, independent of render-frame timing, so short taps are not missed and a tap never produces an accumulated large turn. It **must not** change pitch or altitude. |
| P3D-24 | The free-flight view matrix **must** use the local up projected perpendicular to the look direction, not a hard threshold that substitutes a polar north vector. Turning near a pole or near vertical **must not** cause a sudden roll or nose/pitch jump. |
| P3D-25 | Free-flight orientation **must** be stored as a quaternion, not as geographic yaw/pitch. **Q/E** **must** apply a fixed-step rotation about the local vertical, so turning near a pole or while pitched **must not** gimbal-lock or change pitch or altitude. Legacy yaw/pitch values **must** be derived for persistence and the HUD and **must not** feed back into the orientation. |
| P3D-26 | Reset **must** place the globe camera at the equator on the 0° longitude meridian, 100 km above sea level, facing due east (heading `090°`, pitch `0°`). Restoring a saved globe flight **must** rebuild the orientation from the saved heading and pitch instead of leaving the camera pointing straight down. |

## Acceptance

- Generate Stage 1, erode it, then open the 3D view: the terrain appears displaced with the same coastline
  as the 2D map, lit from a consistent sun direction, and the horizon fades instead of ending abruptly.
- Zoom from the whole map to a single mountain; the silhouette gains detail rather than becoming a flat
  coloured square, and the mesh does not pop visibly at the window boundary.
- Drag-tilt to ~5° above the horizon: the camera never enters the ground and the surface remains lit.
- Disable hardware acceleration or force the CPU fallback: the same view renders, more slowly, with a status
  line saying which renderer is active.
- Toggle vertical exaggeration between 1×, 10×, and 50× in both modes: peaks rise, the cursor readout and
  exports are unchanged, and the globe keeps its water at sea level.
- Switch to the globe, drag to rotate the planet, zoom in until a continent fills the view, then switch
  back: the regional camera is where it was left, and the same stage is still shown.
- Choose **Fly**, hold **W**, drag to look, press **Space**: the camera moves over the landscape and climbs;
  **Q/E** turn, the wheel changes speed, and double-click returns to a sensible starting view.
- Look down while holding **W**: the camera keeps flying level along the tangent instead of diving.
- Fly north through a pole while holding **W**: latitude passes 90, longitude flips by 180°, heading becomes
  south (`180°`), and the camera continues instead of bouncing back to the near side.
- Fly level near a pole at high speed: the horizon stays level and altitude stays essentially constant; the
  planet does not drop away or shrink below the camera.
- Hold or tap **Q/E** while flying: the view turns left/right only; pitch and altitude remain unchanged. Every
  tap produces the same fixed turn, with no missed taps or sudden large jumps.
- Turn near a pole while looking close to vertical: the view frame stays continuous; there is no sudden roll
  or nose/pitch jump.
- Reset the globe flight view: the camera sits at the equator on the 0° meridian, 100 km up, looking due east
  (HUD heading `090°`), not down at the planet.
- Restore a saved globe flight: the camera keeps its saved heading and pitch instead of pointing at the planet.
- Fly down to the ground: the camera stops at eye height, follows the surface, and cannot enter a mountain or
  the sea. Switch to the globe and fly down: the same controls clamp above the globe surface.
- Click a settings text box and type `wasd`: the text box receives the characters and the camera does not fly.
- Toggle **Grid** off and on: the faint reference lines disappear and return over water; relaunch and the
  setting is remembered.
- Switch between 2D and 3D repeatedly: no re-run, no colour shift, no stage change.

## Non-goals for this milestone

- A physically shaded globe atmosphere, clouds, night lights, or a star field; the globe has a single
  atmospheric rim and a flat space background.
- Water as a physically shaded, refractive surface; sea level is a flat coloured surface inside the heightfield.
- Multi-ring geometry clipmaps and disk-cached tiles; the camera window rebuild covers the current need and
  is the seam that later tiling plugs into.
- Shadows, ambient occlusion, atmospheric scattering, and vegetation.
- Auto-rotation or an animated day/night cycle.
- Full flight dynamics (roll, banking, gravity, stall) or a visible avatar; free flight is a constrained
  camera, not a flight simulator.
