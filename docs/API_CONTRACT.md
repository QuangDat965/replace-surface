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

- `POST /api/projects/{projectId}/images` multipart field `image` accepts bounded, fully decoded PNG/JPEG/WebP images and returns image metadata plus a server-owned asset URL. `GET /api/assets/images/{storageId}` streams stored bytes; client filenames and paths are not used as storage paths.
- `GET /api/reconstruction/capabilities` reports mock-only capabilities and explicitly unavailable outputs.
- `POST /api/projects/{projectId}/reconstruction-jobs` accepts `{imageId,idempotencyKey}` and returns HTTP 202 with the existing job for duplicate project/key submissions.
- `GET /api/reconstruction/jobs/{jobId}`, `POST .../{jobId}/cancel`, and `POST .../{jobId}/retry` expose durable status, progress, cancellation, and bounded retry.
- `POST /api/reconstruction/jobs/{jobId}/preview` accepts a reviewed v1 analysis and optional target scene/revision. It builds and stores a candidate only; it does not mutate Scene JSON.
- `GET /api/reconstruction/jobs/{jobId}/preview` returns the candidate document. `POST /api/reconstruction/jobs/{jobId}/apply` requires `{confirmed:true,expectedRevision}` and applies through normal Scene persistence with revision conflict protection.

`SceneAnalysisResult` is a versioned review artifact, separate from canonical editable `SceneDocument`. Analysis provenance and camera/scale assumptions remain explicit. Updating an existing scene requires explicit object/surface links; unmatched entities get new UUIDs. The original scene is unchanged until a matching preview is explicitly confirmed and applied.
