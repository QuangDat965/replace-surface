# API contracts (lock to OpenAPI during coding)

- `POST /api/projects` -> `{id,name}`
- `POST /api/projects/{projectId}/images` multipart -> `{imageId,uri,width,height}`
- `POST /api/projects/{projectId}/analysis-jobs` `{imageId,steps:["segment","depth","layout"],provider?,idempotencyKey}` -> `202 {jobId,status}`
- `POST /api/projects/{projectId}/object-generation-jobs` `{imageId,maskId?,objectId,provider?,idempotencyKey}` -> `202 {jobId,status}`
- `GET /api/jobs/{jobId}` -> `{jobId,status,stage,progress?,provider,externalJobId?,resultRef?,error?}`
- `GET /api/ai/capabilities` -> supported workflows, selected providers and whether configured, WITHOUT secrets.
- `GET /api/projects/{projectId}/analysis-results/{resultId}` -> normalized masks/polygons/depth/layout candidates with provenance.
- `GET /api/scenes/{sceneId}` -> SceneDocument + revision.
- `PUT /api/scenes/{sceneId}` -> SceneDocument, with `If-Match: "0"` for first save or the current revision for update; returns updated JSON and ETag, `409` on conflict.
- `PATCH /api/scenes/{sceneId}/objects/{objectId}` -> transform/geometry/visibility with expectedRevision.
- `PATCH /api/scenes/{sceneId}/surfaces/{surfaceId}` -> material + UV with expectedRevision.
- `POST /api/scenes/{sceneId}/calibrations` -> image anchors + actual length in meters + revision.
- `POST /api/scenes/{sceneId}/exports` `{format:"glb",includeSemanticJson:true}` -> job result.
- `POST /api/assets/textures` with a PNG, JPEG or WebP body and matching content type -> `{url,contentType}`; limited to 10 MB, fully decoded, and bounded to 8192 pixels per side and 40 megapixels.
- `GET /api/assets/textures/{id}` -> uploaded texture bytes for a 32-character server-generated asset ID. Client paths and filenames are never accepted.

Material definitions live in the versioned `SceneDocument.materials` array; surface assignment is a `materialId` reference in `SceneObject.surfaces`. Material edits and assignments persist through `PUT /api/scenes/{sceneId}` with the current `If-Match` revision.

Object transforms and visibility persist through the same versioned SceneDocument update. Optional object deletion archives the full object in `deletedObjects` for stable-ID restoration; updates that discard an optional object, archive a locked/structural object, remove a protected structural surface, or discard an archived object are rejected. Object transforms retain surface records and their material/UV assignments.

## Remote provider integration (Infrastructure only)
`ISegmentationProvider`, `IDepthProvider`, `IRoomLayoutProvider`, `IObject3DProvider` with normalized versioned DTOs. A provider may return results synchronously or through submit/status/result operations. Store provider ID, version, external task ID and raw artifact references. Never assume one vendor implements all capabilities. See `docs/AI_PROVIDER_CONTRACT.md`.

## Vendor callbacks
If a vendor supports webhooks: `POST /api/webhooks/ai/{provider}` with mandatory provider-supported signature verification, replay protection and server-side correlation to a persisted job. Only enable when documented and testable; otherwise poll vendor task endpoint.

## Error policy
Use Problem Details for bad input/unsupported capability/revision conflicts. Map provider rate limit, invalid vendor output, timeouts and invalid artifacts to stable internal error codes. Never return API keys, signed provider credentials or sensitive raw vendor errors to the browser.

## Phase 01 local PostgreSQL boundary

The Phase 01 API accepts only a `ConnectionStrings:RoomForge` value (or the existing `ConnectionStrings:RoomForgeDatabase` alias) targeting local PostgreSQL port 5432 and database `roomforge`. A separate provisioning command accepts only local port 5432 and maintenance database `postgres`; it creates `roomforge` when absent. Neither path can connect to `banhang`. Configure values with .NET User Secrets or environment variables. No SQLite fallback or Docker dependency is supported.

## Reconstruction foundation (Phases 03 + 04)

`GET /api/reconstruction/capabilities` reports the active analysis, segmentation, depth and image-to-3D providers without secrets. SAM 3 segmentation is opt-in with `Reconstruction:SegmentationProvider=fal-sam3` plus server-only `FAL_KEY`; the default remains deterministic mock. The real segmentation provider currently contributes one prompted floor bounding-box polygon with provider score/provenance, which is editable in the review workspace and is not presented as a pixel contour.

- `POST /api/projects/{projectId}/images` multipart field `image` accepts bounded, fully decoded PNG/JPEG/WebP images and returns image metadata plus a server-owned asset URL. `GET /api/assets/images/{storageId}` streams stored bytes; client filenames and paths are not used as storage paths.
- `GET /api/reconstruction/capabilities` reports configured provider IDs and explicitly unavailable outputs; SAM 3 is opt-in by server configuration as described above.
- `POST /api/projects/{projectId}/reconstruction-jobs` accepts `{imageId,idempotencyKey}` and returns HTTP 202 with the existing job for duplicate project/key submissions.
- `GET /api/reconstruction/jobs/{jobId}`, `POST .../{jobId}/cancel`, and `POST .../{jobId}/retry` expose durable status, progress, cancellation, and bounded retry.
- `POST /api/reconstruction/jobs/{jobId}/preview` accepts a reviewed v1 analysis and optional target scene/revision. It builds and stores a candidate only; it does not mutate Scene JSON.
- `GET /api/reconstruction/jobs/{jobId}/preview` returns the candidate document. `POST /api/reconstruction/jobs/{jobId}/apply` requires `{confirmed:true,expectedRevision}` and applies through normal Scene persistence with revision conflict protection.

`SceneAnalysisResult` is a versioned review artifact, separate from canonical editable `SceneDocument`. Analysis provenance and camera/scale assumptions remain explicit. Updating an existing scene requires explicit object/surface links; unmatched entities get new UUIDs. The original scene is unchanged until a matching preview is explicitly confirmed and applied.

## Reconstruction pipeline endpoints (Phases 03 + 04)

All endpoints below operate on the configured local `roomforge` database only. IDs are server-generated UUIDs. Revision-checked mutations require `If-Match` (or an `expectedRevision` body field) and return `409` with the current revision on conflict.

### Annotation document (user-owned, provider-independent)

- `POST /api/projects/{projectId}/annotations` `{sourceImageId}` -> `201 AnnotationDocument`, `ETag: "1"`; source image must belong to the project.
- `GET /api/projects/{projectId}/annotations/{annotationId}` -> `AnnotationDocument` + numeric revision `ETag`.
- `PUT /api/projects/{projectId}/annotations/{annotationId}` accepts only `{regions,referenceSpans,cameraHint,notes}` with numeric `If-Match`; server increments revision, returns the updated document and ETag, `409` with `currentRevision` when stale, or `428` when the header is missing/invalid. Region roles are `floor|wall|object|ceiling|opening|other`; user-authored geometry requires `provenance:"user_supplied"` and `confidence:null`. No provider or Scene fields are accepted here.

### Topology

- `POST /api/projects/{projectId}/annotations/{annotationId}/topology` -> `201 {id, revision, nodes, edges, faces, supports, diagnostics}`. Creates an append-only topology record from the current annotation revision. Every edge starts `unknown_candidate`.
- `GET /api/projects/{projectId}/topologies/{topologyId}` -> persisted topology with numeric ETag.
- `PATCH /api/projects/{projectId}/topologies/{topologyId}` `{expectedRevision, edges:[{edgeId, role}]}` -> corrected topology with ETag; stale revision returns `409`. Corrections persist `user_supplied` provenance. Valid roles: `floor_wall`, `wall_wall`, `wall_ceiling`, `opening`, `occluded`, `unknown_candidate`. If the source annotation has changed, rebuild topology first.

### Geometry and alignment

`POST /api/projects/{projectId}/annotations/{annotationId}/topologies/{topologyId}/alignment` accepts `{camera:{id,fx,fy,cx,cy,positionMeters,rotationCameraToWorld,intrinsicsStatus,provenance},worldEdges:[{edgeId,startMeters,endMeters}]}`. It returns projected endpoints, pixel RMS and visibility counts. A stale topology/annotation revision returns `409`; invalid camera/edge coordinates are rejected. The response states reprojection consistency is not metric accuracy.

- `POST /api/projects/{projectId}/annotations/{annotationId}/geometry` accepts `{topologyId,floorRegionId,calibrationSpanId}` and returns the parametric geometry spec plus plane, camera and geometry stage statuses/diagnostics. Missing data is returned as `insufficient_data`; this endpoint computes an ephemeral candidate and does not persist or mutate a Scene.

Append-only candidates and Scene apply are implemented through these routes:

- `POST /api/projects/{projectId}/annotations/{annotationId}/geometry-candidates` accepts `{topologyId,sceneId,expectedSceneRevision,floorRegionId,calibrationSpanId,dimensionOverrides:[{instanceId,dimensionIndex,meters}]}`. Overrides can fill only unknown positive meter dimensions. Creates a new append-only candidate and returns `201`.
- `GET /api/projects/{projectId}/geometry-candidates/{candidateId}` returns the saved candidate and captured Scene/annotation/topology revisions.
- `POST /api/projects/{projectId}/geometry-candidates/{candidateId}/apply` `{confirmed:true,expectedRevision}` applies only when Scene, annotation and topology revisions still match. Scene persistence validates the new document. Missing dimensions return `422`; stale revisions return `409`. Applying a candidate never overwrites SceneDocument without explicit confirmation.

### Export (Phase 07, partial)

- `GET /api/scenes/{sceneId}/export.json` is implemented and registered in development OpenAPI. It returns a downloadable versioned wrapper `{format,formatVersion,revision,exportedAt,scene}` from the persisted revision; IDs are unchanged. Unsaved browser edits are not included.

- `GET /api/scenes/{sceneId}/export.glb` is implemented and registered in development OpenAPI. It returns a downloadable GLB generated from the persisted Scene revision, with ObjectId/SurfaceId extras and material-separated face primitives. Unsupported geometry or texture maps return `422`; it currently supports box geometry only and does not embed texture maps. See `docs/EXPORT_CONTRACT.md` for supported details and limitations.

### Per-object mesh generation (Phase 06, partial)

- `POST /api/reconstruction/jobs/{analysisJobId}/objects/{objectId}/mesh` accepts `{objectId,sceneId,expectedSceneRevision,crop:{x,y,width,height},idempotencyKey,confirmed:true}`. The crop is an explicit image-pixel rectangle reviewed by the user. The endpoint requires an available, explicitly enabled image-to-3D provider and returns `202` with the durable object-mesh job; the same idempotency key and request returns the existing job, while reusing that key with different inputs returns `409`.
- `GET /api/object-mesh-jobs/{jobId}` returns durable status and provider submission state. `GET /api/object-mesh-jobs/{jobId}/asset` streams the validated server-owned GLB only after completion.
- `POST /api/object-mesh-jobs/{jobId}/apply` accepts `{confirmed:true,expectedRevision}` and attaches the completed asset URL to the matching semantic ObjectId through revision-checked Scene persistence. It does not replace object identity, dimensions, transforms, or independently assigned architectural surface materials.
- Hunyuan3D generation requires `Reconstruction:ImageTo3DProvider=fal-hunyuan3d`, `Reconstruction:FalHunyuanDataTransferApproved=true`, and server-side `FAL_KEY`. The current durable worker reserves $0.50 per request from the shared hard $20 USD ceiling before submitting. Provider/account privacy approval and real-photo acceptance remain prerequisites to live use; fixture tests never call fal.

### Job reconciliation

- `GET /api/reconstruction/jobs/{jobId}/reconciliation` -> `{submissionState, providerId, providerJobId, idempotencySupported, reconciliationAttempts, lastCheckedAt, requiresReconciliationBeforeSubmit}`. Returns the persisted seven-state status defined in `docs/ARCHITECTURE.md` and indicates whether a provider lookup is required before another submission.

The reconciliation endpoint is observational: it reports persisted state and does not claim to contact a provider. Retry returns `409` unless local processing failed within its retry budget and remote submission state is safe to reset. Provider reconciliation must be performed by a configured provider adapter; `outcome_unknown` may return to `not_attempted` only after a confirmed lookup miss from a provider that documents idempotency support. Accepted/running/completed states require a persisted external job ID.
