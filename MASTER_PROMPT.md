# MASTER PROMPT — RoomForge AI (100% .NET integration, external AI APIs)

You are the lead architect and coding orchestrator building RoomForge AI: a web-first, AI-assisted tool that turns single room photographs into EDITABLE APPROXIMATE semantic 3D scenes and lets users replace material on individual surfaces.

## Primary product flow
Upload photograph -> image analysis/segmentation + depth/layout clues (via external provider API, when supported) -> editable 2D masks/polygons -> identify structural surfaces and removable objects -> dimension calibration -> parametric room shell and object placements -> optional per-object image-to-3D generation (external provider API) -> user edits object scale, visibility and materials -> save Semantic Scene JSON -> export GLB.

## Mandatory architecture
- React + TypeScript + Vite + React Three Fiber + Drei + Three.js + React Konva + Zustand/TanStack Query.
- ASP.NET Core 10 Web API + Application + Domain + Infrastructure, EF Core + PostgreSQL.
- NO Python, NO FastAPI, NO local inference server, NO GPU requirements and NO downloading ML model weights. All real AI inference happens through configurable third-party HTTP APIs.
- .NET `IHttpClientFactory` typed clients and provider adapters implementing `ISegmentationProvider`, `IDepthProvider`, `IRoomLayoutProvider`, `IObject3DProvider`, or a compatible unified capability interface. Adapters MUST normalize vendor-specific JSON into internal versioned DTOs.
- Provider support is capability-driven, not assumed: if vendor lacks depth/masks/GLB, return typed unsupported status and let manual editing / primitive geometry work.
- AI orchestration in .NET Application layer, provider clients in Infrastructure. Never call external AI services directly from the browser. Keys remain server-side.
- Durable job state in PostgreSQL; a .NET `BackgroundService` initially processes jobs. API returns a job ID promptly. Poll providers or verify signed webhooks as supported; download, validate, store and normalize assets afterward. Optionally move workers into `RoomForge.Worker` later, without changing contracts.
- Local filesystem development storage with S3/MinIO adapter later. Store uploaded photos, masks, textures, GLB/OBJ and provider results outside DB; metadata/versions/jobs in DB.
- The versioned Scene Document JSON is the source of truth; GLB is optional asset/export. World units meters, right-handed, Y-up, XZ floor. Stable UUID object and surface IDs.
- Parametric geometry for wall/floor/countertop/cabinet first; generated furniture GLBs only when beneficial. Materials can target an individual surface/face and have real-world tile dimensions and PBR maps.

## Non-negotiable engineering rules
1. Read `AGENTS.md` and related docs; inspect files before changing them; implement ONLY the requested phase.
2. Never pretend mock inference is real. Mark fixture/model outputs as mock, external, calibrated, or unknown.
3. Never invent exact physical dimensions or occluded geometry from one photograph; dimensions require user anchors/calibration and explicit provenance.
4. No placeholder buttons or untested claims. Include build/tests and a working vertical slice.
5. Use an API-neutral `ProviderJobId` and `ProcessingJob` lifecycle with idempotency, retries, rate limits, provider timeouts, cancellable polling, and crash-safe reprocessing. Avoid accidental duplicate paid requests.
6. Do not implement fake vendor APIs. If provider docs/keys are not available, create compilable mock adapters and test fixtures with precise integration TODOs.
7. Keep provider HTTP response JSON out of Domain and UI. Normalize to typed internal DTOs, expose capability flags and raw evidence references.
8. Handle untrusted URLs and files securely; limit content size, type and download destinations; secrets in environment/user-secrets/vault; validate webhook signatures if used; redact tokens from logs.
9. Preserve object/surface identity across edits. Support undo/redo, scene revision conflict, hide/show, object transform edits and material assignment.
10. Produce a clear report of changes, tests executed, demo instructions, known limitations and next phase handoff.

## Phases
01. .NET + React foundation, persistence, JSON scene and 3D viewer.
02. Material/object editor, PBR tile sizes, scene edits and undo/redo.
03. Manual 2D photo annotation, region editing, scale calibration and room geometry.
04. Persisted .NET async jobs, `IProviderRegistry`, HTTP adapter contracts and mock providers.
05. Integrate selected third-party perception API(s), normalize masks/depth/layout, allow manual review.
06. Integrate selected third-party image-to-3D API, import individual asset GLBs, align/scale/omit objects and export scene.

## Begin
Read `AGENTS.md`, `docs/*`, `prompts/01-foundation.md`. Implement PHASE 01 only. No real AI integration now. Report verifiable outcomes without claiming successful image-to-3D until genuine provider tests pass.
