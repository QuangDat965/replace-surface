# Implementation plan

The [master blueprint](ROOMFORGE_MASTER_BLUEPRINT.md) governs scope. Execute one phase at a time. Each phase exits only after its own acceptance path is witnessed and the shared Definition of Done below passes. Do not treat fixture or mock data as real inference.

## Shared Definition of Done

Required flow works through its intended UI and API; contracts and decisions are updated; affected builds and meaningful tests pass; security and measurement provenance are reviewed; exact evidence and remaining limitations are recorded in `docs/PROGRESS.md`.

## Active execution plan — approved backlog (Phases 05–07)

Phases 03 **Manual photo reconstruction** and 04 **Durable AI jobs with mock provider** completed on 2026-11-01 against the local `roomforge` database. Continue the user-approved sequence through Phase 05 real perception, Phase 06 image-to-3D and Phase 07 export/operations. Live AI smoke calls remain opt-in under the approved total $20 USD cap; no provider credential is currently configured.

Scoped implementation sequence and expected paths:

1. **Analyze/contract:** keep `SceneDocument` canonical; define a versioned `SceneAnalysisResult` in Domain with image-space polygons, object/surface candidates, uncertainty, provenance, camera and scale assumptions. Define provider capabilities/interfaces in Application; mock result normalization must label every inference `mock`/`estimated`.

Superseded by the layered design in `docs/GEOMETRY_RECONSTRUCTION.md`: analysis output no longer converts directly to geometry. `SceneReconstructionConverter`'s fabricated dimension defaults (`3.6 m` width, `2.7 m` wall height, walls fixed at `z = -1.8`) are removed in M3 and replaced by the plane → camera → topology → parametric geometry pipeline.
2. **Persist/upload:** extend the existing local asset store with bounded decoded room-image upload; add image metadata and `ProcessingJob` records in EF/PostgreSQL; add migration. Scope processing to configured `roomforge` only.
3. **Durable mock worker:** add idempotent job creation, queued/processing/retry/failed/cancelled/succeeded states, progress/stage, attempt limits, lease/restart recovery, and a .NET `BackgroundService`. Keep AI interfaces provider-neutral for scene understanding, segmentation, depth and image-to-3D; only deterministic mock adapters are registered.
4. **Review/apply API:** expose image upload, job create/status/cancel/retry, analysis result, and preview/apply endpoints. Build a candidate scene without writing it; apply only after explicit confirmation and current scene revision. Match existing entities by explicit stable links/semantic correspondence and retain their ObjectIds/SurfaceIds; allocate UUIDs only for genuinely new entities. Never overwrite the source scene automatically.
5. **Web vertical slice:** add an image/reconstruction workspace with upload, status/progress/errors, mock provenance, include/exclude and object type/dimension editing, camera/scale assumption review, preview and explicit apply. Preserve existing material/object editor navigation and scene behavior.
6. **Verify/review/document:** test image validation, job idempotency/retry/cancel/recovery, normalized mock contracts, review edits, identity preservation, preview non-mutation, explicit apply, PostgreSQL persistence/reload, and Playwright. Run .NET/frontend builds, tests and lint; update API/Scene/provider contracts, plan and progress.

| Phase | Objective and scope | Dependencies and technical tasks | Deliverables | Acceptance and test strategy |
| --- | --- | --- | --- | --- |
| 00 Bootstrap | Establish a runnable, verifiable workspace and development rules. No editor or AI integration. | Blueprint and existing documents. Add .NET solution/layers, React/Vite shell, EF/PostgreSQL configuration, Docker services, health/sample endpoints, fixture contract check, roles, skills, workflows and docs. | Builds, deterministic sample scene, local run instructions, agent kit. | Both builds and test suites pass; health and sample endpoints respond; fixture validates. Docker services checked when Docker is available. Shared DoD. |
| 01 Viewer and persistence | Edit a sample room in 3D and save/reload individual surface material assignments. No AI. | Phase 00 and Scene v1 contract. Use the existing local PostgreSQL server on port 5432. Create only a separate `roomforge` database when absent; never connect to or alter `banhang`. Read app/admin credentials from .NET User Secrets or environment variables. Configure EF Core/Npgsql, add and apply migrations, then add project/scene API, R3F geometry and raycast selection, orbit controls, material change, optimistic revision check. API startup is restricted to database `roomforge` on local port 5432. Do not add SQLite or require Docker. | Working floor/wall/countertop viewer and durable scene save. | Verify the API connects to `roomforge`; select floor, change only its material, save, reload, retain stable IDs; restart API and reload persisted Scene JSON. Unit/API integration and Playwright smoke tests. Shared DoD. |
| 02 Advanced material editor and object editing follow-up (**completed 2026-10-31**) | Add surface-specific PBR authoring and independent semantic object editing. No AI. | Phase 01 surface identity/persistence. Add safe PNG/JPEG/WebP uploads with full decode and dimension limits, PBR maps, physical tile dimensions, per-surface UV rotation/normalized `[0,1]` offset/repeat, previews, undo/redo and revision persistence. Add object-level selection, meter transforms, quaternion rotation, dimension scaling, visibility, optional-object archive/restore, structural protection and transform gizmos. Keep assignments on surfaces and definitions in materials. | Material library, uploaded texture assets, physical-scale preview, durable material settings, object inspector and reversible object archive. | Material scale and adjacent surface independence; PBR/UV/upload and object transform/visibility/archive state survive save/reload and API restart; object edits preserve stable ObjectIds/SurfaceIds and materials; undo/redo; structural object/surface protections; API, unit, frontend and Playwright tests. Shared DoD. Evidence recorded in `docs/PROGRESS.md`. |
| 03 Manual photo reconstruction (**completed 2026-11-01**) | Build approximate 3D from annotated photo without vendor keys. | Completed Phase 02 editor; safe image upload and calibration contract. Added image-space annotation, measured scale anchors, camera/world alignment hypothesis, topology correction, parametric shell and append-only preview/apply. | Manual-first photo-to-scene path and versioned reconstruction review contract. | Witnessed all ten steps using a licensed interior photo: manual floor/wall annotations and measurements; preview with unknown dimensions explicit; alignment report with uncertainty caveat; confirmed apply; generated geometry/material editing; PostgreSQL reload and API-restart persistence. Evidence in `docs/PROGRESS.md` and `scripts/verify-geometry-persistence.ps1`. |
| 04 Durable AI jobs with mock provider (**completed 2026-11-01**) | Establish provider-neutral asynchronous orchestration in .NET. | Phase 03 review path and persisted assets. Added capability-driven interfaces for scene understanding, segmentation, depth and image-to-3D; persisted idempotent jobs, leases/recovery, BackgroundService, status/error UI, deterministic mock outputs and explicit preview/apply. No vendor integration. | Durable mock reconstruction job, versioned `SceneAnalysisResult`, reviewable observations labeled mock, explicit scene conversion. | API restart during worker processing recovers the persisted job; idempotency, bounded retry/backoff, cancellation, malformed mock results, submission-state safeguards, progress/failure, stable identity and preview-vs-apply pass against PostgreSQL and Playwright. No paid calls. Shared DoD. |
| 05 Real perception adapter | Receive documented third-party segmentation/layout/depth where supported. | Phase 04, selected provider's official docs, secure credentials, privacy decision. Add typed HTTP adapter, canonical mapping, result validation and human review. | One verified real capability; explicit unsupported states. | At least one real photo has reviewable observations from a verified provider, with fixture tests and opt-in live smoke. No invented capability. Shared DoD. |
| 06 Per-object image-to-3D | Import an individual generated asset while retaining editable scene semantics. | Phase 05 review flow; documented 3D provider and output license/format. Add crop/mask request, remote job, safe asset download, scale/orientation review and primitive fallback. | Generated object attached to stable scene object. | Real provider output can be adjusted, hidden and saved/reloaded; unsupported output is clearly rejected. Adapter, geometry and UI tests. Shared DoD. |
| 07 Export and production reliability | Deliver portable scene exports and deployable recovery/security baseline. | Phases 01–06. Add GLB plus JSON export, schema migration checks, asset packaging, metrics/cost limits, deployment configuration and security review. | End-to-end export and operations guide. | Import exported GLB in independent viewer; JSON retains semantic IDs; upload-to-export flow and restart recovery pass. Shared DoD. |

## First runnable vertical slice

Phase 01 starts from `examples/sample-scene.json`: render floor, two walls, countertop and chair proxy; select floor by SurfaceId; assign a different sample material; save with expected revision; reload from PostgreSQL and verify the same IDs and assignment. This is authored sample data, not a reconstruction claim.

## Milestones (phase numbering preserved)

Roadmap phase numbers in `docs/ROADMAP.md` are unchanged. The milestone labels below order the remaining approved work by dependency and risk.

| Milestone | Roadmap phase | Objective | Exit gate |
| --- | --- | --- | --- |
| **M1 (completed)** | 04 | Reliability gates for durable jobs | Integration tests against local `roomforge` cover concurrent idempotency, lease recovery, retries through `MaxAttempts`, cancellation, malformed output and source-image preservation. The API verifier kills the host during real worker processing and confirms recovery after restart. Seven-state submission safeguards and reconciliation status are tested. |
| **M2 (completed)** | 03 | Manual polygon annotation | Konva-based editor with separately tested coordinate mapping, polygon editing, snapping and validation modules. `AnnotationDocument` persists with optimistic concurrency. Browser acceptance annotates a licensed real interior photo without an AI key. |
| **M3 (completed)** | 03 | Topology, geometry reconstruction and photo alignment | Ten-step real-photo path passed; see Phase 03 acceptance evidence below and `docs/PROGRESS.md`. |
| **M4 (in progress)** | 05 | Real perception provider | Official provider survey and fixture-tested typed SAM 3 queue provider; durable `in_progress`/accepted/running/completed/outcome-unknown lifecycle; resume without duplicate submit; aggregate PostgreSQL spend reservation capped at $20; downloaded PNG mask normalization to a bounded editable row-envelope polygon with provider-box fallback. Remaining: provider privacy/retention decision, three licensed-photo live observations within $20, and human review of mask-envelope quality. |
| **M5 (in progress)** | 06 | Per-object image-to-3D provider | User-confirmed crop, PostgreSQL job keyed to stable ObjectId/crop digest, $0.50 reservation against the shared $20 cap, persisted Hunyuan queue transitions, safe GLB validation/storage, explicit Scene apply and viewer loading are implemented. Synthetic end-to-end PostgreSQL provider flow passes. Remaining: browser verification of adjust/visibility/save/reload, provider-account privacy/retention decision and one permitted real generated mesh. |
| **M6 (in progress)** | 07 | Export and round-trip verification | `export.json` and a box-only `export.glb` API/UI slice use SharpGLTF; tests now derive world bounds from decoded GLB position floats and enforce 1 mm parity, while Playwright validates downloaded bytes with Khronos `gltf-validator`. Remaining: embed texture assets, support agreed plane/extruded geometry, and exercise JSON/GLB parity on a generated Scene end to end. |
| **M7 (in progress)** | 07 | Operational hardening | `docs/LOCAL_OPERATIONS.md` now documents local-only `roomforge` configuration, provider spend/privacy gates and coordinated DB/asset backup and restore safeguards. Remaining: automated restore drill, migration/asset consistency checks, metrics, unreferenced asset cleanup and operational quota controls beyond the provider reservation ledger. |

## Phase 03 acceptance path

Phase 03 is complete only when every step below has been witnessed against the running system, not described:

1. Upload a real interior photo.
2. Draw floor and wall annotations manually.
3. Define measured reference dimensions.
4. Create a valid editable 3D room shell.
5. Preserve uncertain dimensions as estimates or unknowns.
6. Preview the reconstructed scene.
7. Compare the reconstructed geometry against the original image.
8. Apply changes only after explicit confirmation.
9. Modify geometry and replace surface materials.
10. Save and reload through PostgreSQL, verified after an API restart.

Skipped integration tests do not satisfy any acceptance gate. A gate that cannot be executed must be reported as blocked, never as passed.

## Phase 04 acceptance path

Phase 04 is complete only when these are witnessed:

- A job survives an API restart mid-processing and completes or is recoverable afterwards.
- Duplicate submission with the same idempotency key yields one job, including under concurrency.
- Transient failure retries with backoff up to `MaxAttempts`, then reaches terminal failure.
- An ambiguous submission outcome (`OutcomeUnknown`) triggers reconciliation, not a duplicate paid submit.
- Cancellation and bounded retry behave as documented, and a malformed result becomes a typed failure that preserves the source image.
- Preview never mutates Scene JSON; apply requires explicit confirmation and a matching revision.

## Decisions and risks

- A single image cannot provide reliable absolute dimensions. Phase 03 requires user measurement and keeps uncertainty visible.
- Provider capabilities, pricing, authentication, retention and output formats remain open until official docs are selected in Phase 05/06.
- Phase 01 uses the existing local PostgreSQL service on port 5432. Provision only database `roomforge`, using `postgres` as the maintenance database; never connect to or modify `banhang`. Credentials come from .NET User Secrets or environment variables. No Docker service or SQLite fallback is part of this phase.
- `ConnectionStrings:RoomForge` must target local port 5432 and database `roomforge`; API startup rejects other databases before opening a connection. Database provisioning accepts only local port 5432 with maintenance database `postgres`.
- A mesh may lack separable faces. Use a clearly labeled primitive/proxy fallback when a selected surface cannot be edited independently.
