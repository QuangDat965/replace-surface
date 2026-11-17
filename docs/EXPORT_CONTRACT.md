# Export contract — GLB + semantic JSON

Two artifacts are produced from one canonical `SceneDocument`. Neither is a source of truth; `SceneDocument` remains authoritative.

## Format decisions

| Artifact | Library | Location |
| --- | --- | --- |
| `export.json` | `System.Text.Json` | Application layer |
| `export.glb` | **SharpGLTF** (MIT) | Infrastructure only |

SharpGLTF is pinned at `SharpGLTF.Core 1.0.7` + `SharpGLTF.Toolkit 1.0.7` (MIT, `net6.0`/`netstandard2.0` assets, `net10.0` compatible). It is referenced **only** by `src/RoomForge.Infrastructure/Export/`. `RoomForge.Domain` and the Scene JSON schema must never take a dependency on it.

## Layer boundary

```text
SceneDocument (JSON)
    -> SceneToExportModelConverter      (Application, no SharpGLTF)
       SceneExportModel                 (neutral POCOs, System.Numerics only)
    -> SharpGltfSceneExporter           (Infrastructure, the only SharpGLTF consumer)
       GLB bytes
```

Implemented exporter: `Infrastructure/Export/SharpGltfSceneExporter.cs`. The API offers `GET /api/scenes/{sceneId}/export.glb`, and the web editor offers an Export GLB download button. SharpGLTF re-reads fixture output in tests; the Playwright download test additionally runs Khronos `gltf-validator` against the actual downloaded bytes. JSON export remains revisioned and canonical.

## Geometry correctness requirements

### Vertex splitting

A box has six faces with six distinct normals. It **cannot** be exported with eight shared vertex records; that produces averaged normals and per-face UVs that cannot be assigned independently. The exporter must:

- Emit **flat normals**: each triangle's three vertices carry that triangle's face normal.
- Emit **per-face UVs**: UV coordinates are computed in the face's local 2D frame from `faceSizeMeters` and `textureRepeat`, so each face owns its own UV range.
- Expect **24 vertices per box** (4 per face × 6 faces) with an average of fewer than 3 triangles per vertex. The current fixture test asserts 24 position entries per exported box; a regression to 8 shared vertices fails.

### Primitive separation

glTF ties one material to one primitive. A scene object whose surfaces use different `materialId` values therefore requires **one primitive per distinct material**, not one primitive with multiple materials:

- Group the object's faces by `materialId`.
- Emit one `MeshPrimitiveBuilder` per group with that material.
- An object with two assigned materials and uncovered faces produces one primitive per assigned material plus one default-material primitive for uncovered faces. The sample Countertop verifies two assigned materials produce three primitives.

### Identity preservation

- Node name is the **ObjectId** string.
- `extras.roomForgeObjectId` repeats the ObjectId as a stable machine-readable value.
- `extras.roomForgeSurfaceIds` lists the SurfaceIds belonging to that node's primitives, grouped per primitive via `extras.roomForgeSurfaceIds` on each primitive.
- Round-trip validation re-reads the GLB and asserts the ObjectId/SurfaceId sets match the source `SceneDocument` exactly.

### PBR preservation

- `baseColorFactor` from `Material.baseColor` (sRGB hex converted to linear).
- `metallicFactor` from `Material.metalness`; `roughnessFactor` from `Material.roughness`.
- Texture map references are not embedded yet; scenes with any texture map are rejected with `422` rather than silently dropping the map. This remains an open Phase 07 gate.
- Tile size, UV repeat, rotation and offset are baked into exported UVs. glTF has no equivalent of a physical tile size, so baking is the only correct representation.

### Visibility

glTF has no portable visibility flag. RoomForge therefore defines its own behaviour explicitly and keeps it **outside** the core glTF semantics:

- `visible: false` objects are still exported as nodes, so the export is lossless and reversible.
- Visibility is recorded in `extras.roomForgeVisible` (boolean) and `extras.roomForgeMeasurementStatus`.
- The exporter does **not** misuse `KHR_materials_variants`, `scale: 0`, or an empty mesh as an implicit hide, because those are lossy and ambiguous. A consumer that ignores `extras` will render the object; that trade-off is deliberate and documented.

### Bounds validation

World-space AABB parity is checked per object to `1e-3 m`: the unit test reads the GLB JSON and binary chunks, decodes every position float, applies the serialized glTF node matrix and compares its resulting bounds with the source Scene's rotated box corners. The browser export test separately runs Khronos `gltf-validator` against the downloaded GLB and requires zero errors. These checks complement the SharpGLTF read-back tests rather than relying on one library for both export and validation.

## Triangulation

- Convex polygons: fan triangulation.
- Concave polygons: ear clipping, with a check that the polygon is simple (non self-intersecting) first.
- Self-intersecting polygons are **rejected** with a typed error; they are never silently triangulated into nonsense.
- Holes are supported only where the primitive separation allows a separate ring; otherwise the export reports `unsupported_geometry` with the offending `SurfaceId`.

## Explicitly unsupported geometry

The exporter must reject — never approximate — these cases, returning `unsupported_geometry` with the offending ID:

- `geometry.kind` values outside the currently implemented `box`.
- Objects with zero exported faces.
- Surfaces referencing a `materialId` absent from `materials`.
- Texture assets that cannot be decoded or exceed the asset-store limits.
- Non-simple polygons.

## Round-trip verification

`GET /api/scenes/{sceneId}/export.glb` is verified by a test that:

1. Exports a scene containing a multi-material object and an invisible object.
2. Re-reads the GLB with SharpGLTF.
3. Asserts node/ObjectId sets, per-node primitive counts, per-primitive materials, vertex count per box face, binary-derived world AABB tolerance and `extras` values.
4. Asserts `export.json` retains every ObjectId and SurfaceId and the `revision` at export time.
5. Runs the independent Khronos validator against the actual downloaded GLB bytes and requires zero validation errors.
