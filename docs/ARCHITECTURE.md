# Architecture — no Python, third-party AI APIs only

```text
apps/web  (React + R3F + Konva)
     | REST / OpenAPI + job status polling
src/RoomForge.Api (ASP.NET Core 10)
     |-> Application (AI job orchestration, scene build, use cases)
     |-> Domain (semantic scene, dimensions, materials, contracts)
     |-> Infrastructure
           |-> EF Core / Npgsql / PostgreSQL
           |-> local files or S3/MinIO
           |-> AiProviderRegistry + HttpClientFactory + vendor adapters
           |-> Hosted BackgroundService job dispatcher (separate worker host later)
                        | HTTPS, optional verified webhooks
             third-party image analysis / segmentation / depth / image-to-3D APIs
```

## What the AI provider owns
A remote vendor MAY provide segmentation masks, depth estimates, image-to-3D mesh generation, scene analysis or some combination. Do not assume one endpoint provides all. Provider-specific capabilities determine enabled workflow stages. Do not deploy Python or model weights.

## What RoomForge owns
1. Upload and assets (photos/masks/crops/textures/GLBs).
2. Typed provider-independent operations and durable job state.
3. Normalizing masks/labels/camera/depth into observations with confidence and provenance.
4. Constructing parametric walls/floor/countertops and associating distinct editable surfaces.
5. Calibrating size, positioning/rotating/hiding individual objects, assigning PBR materials.
6. Versioned semantic scene JSON, persistence and GLB export.

## Pipeline
1. User uploads an image; validate and store it.
2. POST analysis-jobs returns `{jobId,status}` immediately.
3. Durable worker claims and leases job. If stage uses a remote async API, it submits once and persists `externalJobId` + provider name before polling; avoid double-paid requests by idempotency/reconciliation.
4. Worker polls vendor job or accepts verified webhook, verifies results, downloads with size/content safeguards to storage, saves raw response reference.
5. Provider adapter maps raw response to internal `ImageObservationSet` and/or `GeneratedMeshAsset`. Unsupported stages remain explicitly unavailable.
6. User reviews 2D segments/geometry proposals, calibrates known length, constructs editable geometry. Optional furniture generation uses masked crops and separately trackable object jobs.
7. User assigns materials, edits scene and exports GLB + JSON.

## Job model
Use a persisted `ProcessingJob` table with `Id`, `ProjectId`, `Stage`, `Provider`, `Status`, `ProviderJobId`, `AttemptCount`, `NextAttemptAt`, `LeaseUntil`, `IdempotencyKey`, `ResultRef`, `ErrorCode`, timestamps. Example lifecycle: `queued -> submitting -> provider_processing -> downloading -> normalizing -> succeeded` or `failed/cancelled`. Cancellation may be local-only if provider lacks cancellation. Retry transient failures with backoff and per-provider rate limits. Recover expired leases after restart. Do not equate provider timeout with safe re-submission of a paid generation request.

### Submission states

The persisted job separates *local* progress from *remote* submission confidence, because a lost response must never cause a duplicate paid request:

| Submission state | Meaning | Permitted next action |
| --- | --- | --- |
| `NotAttempted` | Never sent to the provider | Submit |
| `InProgress` | Request is in flight; lease held | Wait; recover lease if it expires |
| `OutcomeUnknown` | Request may or may not have been accepted (timeout, connection reset, 5xx after send) | **Reconcile first.** Never resubmit blindly |
| `Accepted` | Provider confirmed and returned an external job ID | Poll |
| `Running` | Provider reports the remote job is executing | Poll |
| `Completed` | Provider returned a terminal success | Download, normalize |
| `TerminalFailure` | Unrecoverable, or attempts exhausted | Report; user-initiated retry only |

An `OutcomeUnknown` job is reconciled before any resubmission: the worker looks up the provider for an existing job matching the recorded idempotency key, and only submits again when the provider documents idempotency support **and** no prior job exists. Providers without documented idempotency that reach `OutcomeUnknown` are marked for human review rather than retried automatically.

## Reconstruction data layers

Five separable artifacts, each with its own version and mutation rules: `SourceImage`, `AnnotationDocument`, `SceneAnalysisResult`, `ReconstructionCandidate`, `SceneDocument`. Only the last is canonical and editable, and it changes only after explicit confirmation with a matching revision. See `docs/RECONSTRUCTION_DATA_LAYERS.md`.

## Geometry reconstruction pipeline

Annotations never convert directly into 3D geometry. The ordered stages are plane constraints, camera calibration, room topology, parametric geometry, then Scene JSON. Every stage returns `Ok | InsufficientData | Unsupported` with diagnostics and never substitutes a default for a missing measurement. See `docs/GEOMETRY_RECONSTRUCTION.md`.

## Coordinate contracts

Five spaces (image pixels, camera, world metres, object-local, texture UV) each have exactly one implementation and round-trip tests that import no UI framework. See `docs/COORDINATE_SYSTEMS.md`.

## Export boundary

`Application` maps Scene JSON to a neutral `SceneExportModel`. SharpGLTF is referenced only by `Infrastructure/Export/`. Requirements cover flat normals, per-face UVs, one primitive per material, ObjectId/SurfaceId preservation in `extras`, bounds validation and round-trip tests. glTF has no visibility flag, so visibility travels in documented `extras` rather than through a lossy renderer trick. See `docs/EXPORT_CONTRACT.md`.


## Data
Project, ImageAsset, SceneVersion (JSONB), Material, Asset, ProcessingJob; large files stored in local/S3/MinIO. Provider raw response archived as evidence, with sensitive fields scrubbed.

## Internal provider contracts
- `ISegmentationProvider` => masks/polygons/object labels, coordinate frame, confidence where provided.
- `IDepthProvider` => depth map with relative-vs-metric provenance.
- `IRoomLayoutProvider` => image-space room corners/planes and uncertainty.
- `IObject3DProvider` => async generated model asset with format, scale/orientation caveats, external job ID.
- Optional unified multimodal client may implement multiple interfaces; use provider capability discovery and routing.
- `Mock*Provider` allows deterministic offline test flows, never branded as real inference.

## Camera/geometry caution
Single images have ambiguous camera parameters, global scale and occluded shapes. A depth API alone does not guarantee a metric room. Demand user-provided reference lengths and allow overrides. Prioritize parametric surfaces over one-shot full-room meshes to preserve material editing.

## Reconstruction foundation (Phases 03 + 04)

Room images use the existing asset-storage abstraction with bounded full decode and server-generated storage IDs. Image metadata and idempotent reconstruction jobs are persisted in PostgreSQL. An ASP.NET Core `BackgroundService` leases work, records progress/attempt state, recovers expired processing leases, and supports cancellation/retry. Analysis contracts are provider-neutral and versioned; only deterministic mock providers are registered. The web review flow supports inclusion, type/dimension edits, scale/camera assumption review, non-persisting preview, and explicit revision-checked application into canonical Scene JSON. Phase 05/06 real provider adapters remain out of scope.
