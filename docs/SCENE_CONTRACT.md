# Semantic scene contract v1

Single source of truth: `SceneDocument` JSON validated at API boundary. A GLB is an export/cache, NOT an editable database model.

## Coordinate convention
World: meters, right-handed, Y up, X right, +Z toward camera in the example fixture. Floor lies in XZ and camera up is +Y. Positions and dimensions use `Vector3`; rotation uses `Quaternion [x,y,z,w]`. Dimensions `width` along local X, `height` along local Y, `depth` along local Z. Converters may remap axes but must be tested.

## Entities
- `SceneDocument`: schemaVersion, sceneId, projectId, revision, units, world, camera, sourceImageId, calibration, active `objects`, optional `deletedObjects` archive, materials, visibility, evidence.
- `SceneObject`: stable UUID, name, category, geometry `{kind: box|plane|extruded_polygon|asset, ...}`, position, rotation, dimensions, visibility, locked, confidence, provenance, surfaces, optional assetId.
- `Surface`: stable UUID, objectId, semanticRole (`floor`, `wall`, `countertop_top`, `countertop_edge`, `cabinet_front`, etc.), faceId, polygon or UV metadata, materialId, tiling specification, evidence link.
- `Material`: stable UUID, name, PBR texture map asset URLs (`baseColor`, `roughness`, `metalness`, `normal`), base color, roughness/metalness in [0,1], and positive `tileSizeMeters` `[width,height]`. Texture assets are uploaded to the local asset API; scene JSON remains their editable reference.
- `Calibration`: reference segment in pixel/scene coordinates, stated physical length, confidence, provenance, applied transform revision.
- `Evidence`: image region/mask URI, segmentation confidence, provider version, may-be-occluded flag.

Use IDs for references; never infer semantic role from a mesh name such as `mesh_2`.

## Semantics of unknowns
- `measurementStatus`: `unknown|estimated|calibrated|user_supplied`.
- `confidence` is optional [0,1] and refers to inference quality, not a guarantee of spatial accuracy.
- Missing value is `null`, not zero meters. Low-confidence measurements should be editable and visually signaled.

## Mutation rules
- Object hide/show never deletes it; deletion requires explicit user intent.
- Optional-object deletion moves the complete object into `deletedObjects`, preserving its ObjectId, SurfaceIds, assignments, and UV settings for restoration. Archived IDs remain reserved and cannot be discarded by scene updates. Locked, architectural, structural, floor, wall, and countertop objects/surfaces cannot be archived or removed.
- Object selection is separate from surface selection. Position and dimensions are meters; rotation is a normalized `[x,y,z,w]` quaternion. Transform mutations change only position, rotation, dimensions, and visibility, preserving the object's surfaces and their material/UV data.
- Replace-material updates selected surfaces' `materialId` references; material definitions stay in `materials` and are not copied into each surface.
- Surface `uv` may store `rotationDegrees`, normalized texture `offset` components in `[0,1]`, and a positive `repeat` multiplier `[u,v]`. Physical repeat counts are derived from face dimensions in meters divided by material tile dimensions, then multiplied by the surface repeat multiplier.
- Transform changes and UV settings are separately undoable.
- Scene revisions use optimistic concurrency (`If-Match` or `expectedRevision`); conflicting edits return 409.
- Every export must include a scene JSON companion, even if 3D export omits semantic metadata.

## Reconstruction analysis and application

`SceneAnalysisResult` v1 is a separate, versioned review artifact; it is not a substitute for `SceneDocument`. It records image-space masks/polygons, candidate surfaces/objects, estimates, confidence, provenance and camera/scale assumptions. Unknown metric scale stays unknown until calibrated or user supplied. Review changes (including include/exclude and explicit links) are applied to a candidate document in preview only. Applying requires explicit confirmation and the current scene revision. Existing linked ObjectIds and SurfaceIds are retained; new entities receive new UUIDs. The source scene is never overwritten as a side effect of analysis or preview.
