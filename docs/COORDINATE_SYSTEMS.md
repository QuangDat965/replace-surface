# Coordinate system contracts

This document is normative. Every conversion between coordinate spaces in RoomForge must match one of the transforms defined here, and every transform must be covered by a test that does not import a UI framework (no Konva, no three.js, no React).

## The five spaces

| Space | Units | Origin | Axes | Notes |
| --- | --- | --- | --- | --- |
| **Image** | pixels (float) | top-left corner | +X right, **+Y down** | Bounds `[0, width] × [0, height]`. This is the space of `DetectedSurface.Polygon` and `DetectedObject.Polygon`. |
| **Camera** | metres (up-to-scale before calibration) | camera center | +X right, +Y up, **−Z forward** | Matches glTF/OpenGL conventions so export never has to flip an axis. |
| **World** | metres | `world.origin` | +Y up, XZ floor plane, right-handed | Defined by `SceneDocument.world` and `docs/SCENE_CONTRACT.md`. |
| **Object-local** | metres | object center | +X width, +Y height, +Z depth | Space before `position`, `rotation`, `dimensions` are applied. |
| **Texture UV** | normalized `[0,1]` | — | +U right, +V up | Derived per face by `faceSizeMeters` / `textureRepeat`. |

## Conversion functions

Each function below lives in a single named place and is the only permitted implementation of that conversion.

### `ImageToCamera` — image pixels to camera space

Image Y grows downward, camera Y grows upward. The flip must happen in exactly one function:

```text
x_cam = (u - cx) / fx
y_cam = -(v - cy) / fy          // negative: image Y-down -> camera Y-up
z_cam = -1                       // ideal image plane at depth 1
```

Only meaningful once `fx, fy, cx, cy` are known. Before calibration the camera space is **up-to-scale and up-to-projection**: results are direction-only and must be labelled `Estimated` or `Unknown`, never `Measured`.

### `CameraToWorld` — camera space to world space

```text
p_world = R * p_cam + t
```

`R` is a right-handed rotation matrix and `t` is the camera position in world metres. The inverse, `WorldToCamera`, is `Rᵀ * (p_world - t)`.

### `WorldToObjectLocal` / `ObjectLocalToWorld`

```text
p_local  = Q⁻¹ * (p_world - position)      // Q is the object rotation quaternion
p_world  = Q * p_local + position
```

Dimensions apply **after** this transform: local X spans ±width/2, local Y spans ±height/2, local Z spans ±depth/2.

### `ProjectToImage` — world/object points back to image pixels

The inverse of `ImageToCamera ∘ CameraToWorld`, used by photo alignment verification:

```text
p_cam = Rᵀ * (p_world - t)
if (p_cam.z >= 0) => point is behind the camera, return Unprojectable
u = cx + fx * ( p_cam.x / -p_cam.z )
v = cy - fy * ( p_cam.y / -p_cam.z )
```

Points behind the camera or outside `[0,W]×[0,H]` are reported as unprojectable rather than clamped. Clamping would hide the uncertainty that alignment verification exists to surface.

### UV mapping

Per face, repeat counts come from physical face size divided by the material tile size, multiplied by the surface `repeat` multiplier. Texture transforms apply in this order, matching `SceneViewer`:

```text
1. repeat  (u * repeatU, v * repeatV)
2. rotate about center (0.5, 0.5) by rotationDegrees
3. offset  (+ offsetU, + offsetV)
```

This order is part of the contract; changing it changes every rendered surface.

## Invariants that must be tested

1. `WorldToCamera ∘ CameraToWorld` round-trips to `< 1e-9` absolute error for finite inputs.
2. `ObjectLocalToWorld ∘ WorldToObjectLocal` round-trips to `< 1e-9`.
3. `ProjectToImage ∘ ImageToCamera` round-trips to `< 1e-6` pixels for points in front of the camera.
4. Image Y-down is flipped exactly once: a transform chain that applies the flip twice must fail a dedicated test.
5. A point behind the camera is reported `Unprojectable`; it is never returned as a plausible pixel.
6. UV transform order produces different output when any two steps are swapped — a test asserts the order, not just the presence of the steps.

## Where these functions live

- `src/RoomForge.Domain/Geometry/CoordinateTransforms.cs` — pure `System.Numerics` maths, no third-party dependencies. Used by the server for alignment verification and by export for bounds validation.
- `apps/web/src/geometry/` — image/display mapping for the annotation editor. Contains no Konva or three.js imports so it is unit-testable in vitest without a canvas.

Neither location may import the other's runtime concerns: the server never imports browser modules, and the browser never reimplements a server transform with different rounding.
