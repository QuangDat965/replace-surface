# Progress

## 2026-10-31 â€” Phase 00 scaffold completed; database runtime unverified

Repository began as a blueprint, prompts, example scene, seven skills, six role files, and one development workflow. No runnable application or solution existed.

Added a .NET 10 solution with Domain, Application, Infrastructure and API projects, EF/PostgreSQL configuration, a bounded local asset store, health/OpenAPI/sample-scene endpoints, a React/Vite workspace, Docker Compose configuration, role/skill/workflow files, and this phase plan. The sample scene is authored data, with no image reconstruction or editor behavior claimed.

Observed verification:

- `dotnet build src/RoomForge.slnx`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx --no-build`: 3 passed.
- `npm --prefix apps/web run build`: passed.
- `npm --prefix apps/web run test`: 2 passed.
- `npm --prefix apps/web run lint`: passed.
- Live API smoke: `/health` returned `ok`, `/api/sample-scene` returned 5 objects and 3 materials, `/openapi/v1.json` returned HTTP 200.
- Live Vite smoke: root returned HTTP 200 with `RoomForge AI` title; proxied `/health` and `/api/sample-scene` returned valid responses.
- Checked YAML frontmatter on all 14 repo skill files.
- An initial sample endpoint smoke returned HTTP 500 because it serialized a disposed `JsonDocument`; fixed by cloning the root element and reran successfully.

At Phase 00 signoff, Docker was unavailable and no database credentials had been configured, so database runtime was not checked. The Phase 01 entry records the subsequent local PostgreSQL probe and implementation.

## 2026-10-31 — Phase 01 completed

Used the existing local PostgreSQL instance on port 5432. The configured User Secrets key was `ConnectionStrings:RoomForgeDatabase`, so the API and EF design-time factory now accept it as an alias for `ConnectionStrings:RoomForge`. The API can derive a local maintenance connection to database `postgres` from the configured RoomForge connection when a separate admin connection is not set. Provisioning remains restricted to local `postgres` and only creates/checks `roomforge`; API and EF target validation accepts only local `roomforge`. No command connected to `banhang`.

Verification actually run:

- `dotnet build src/RoomForge.slnx`: passed, zero warnings/errors.
- `dotnet run --no-build --project src/RoomForge.Api/RoomForge.Api.csproj -- --ensure-roomforge-database` (Development): passed; reported `roomforge` ready and no other database accessed.
- `./scripts/verify-phase01-persistence.ps1`: passed; API connected to `roomforge`, applied pending EF migrations, saved a changed floor material, restarted the API, and confirmed revision, material assignment and stable SurfaceIds persisted.
- `npm --prefix apps/web run test:e2e`: 1 Playwright test passed, selecting a surface, changing its material, saving, reloading and confirming the assignment.
- First Playwright attempt found Chromium absent; installed the Playwright Chromium browser and reran successfully.
- `dotnet test src/RoomForge.slnx`: 8 passed.
- `npm --prefix apps/web run build`: passed; viewer chunk 938.79 kB / 248.67 kB gzip.
- `npm --prefix apps/web run test`: 3 passed.
- `npm --prefix apps/web run lint`: passed.

The existing PostgreSQL service was used without Docker. The only database created/queried by this work was `roomforge` (maintenance connection to `postgres` was used for existence check/provisioning); `banhang` was not accessed. No credentials were printed or added to repository files.

## 2026-10-31 — Phase 02 advanced material editor completed

Implemented a Scene JSON-backed material library and editor. Material definitions remain in `SceneDocument.materials`; surfaces continue to store stable `materialId` assignments. Material definitions retain base color, roughness, metalness, physical tile width/height in meters, and optional base-color, roughness, metalness and normal texture asset URLs. A selected surface stores rotation, U/V offset and independent U/V repeat multipliers. The R3F viewer applies maps with sRGB base-color handling, linear data maps, repeat wrapping and face-meter-derived repeats. Physical repeat factors are computed from each box face's dimensions and its material tile size.

Added bounded PNG/JPEG/WebP texture upload (10 MB maximum, matching MIME/signature, full decode, 8192-pixel side and 40-megapixel limits), server-generated IDs, path-safe asset retrieval, material preview swatches, material creation/assignment, PBR controls and scene-snapshot undo/redo. Texture assets use a shared local root; legacy API project-root asset lookup remains read-only so existing Scene JSON texture references remain available. No relational material table or EF schema change was needed because Scene JSON is already persisted in PostgreSQL JSONB. UV offsets are normalized to `[0,1]`. Object transforms, dimensions and visibility remain a separate Phase 02 follow-up, and no AI/reconstruction work was added.

Verification actually run against the existing local `roomforge` PostgreSQL database:

- `dotnet build src/RoomForge.slnx -c Release`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx`: 12 passed, including malformed and oversized texture rejection, complete texture decode, material/UV validation and local storage guards.
- `npm --prefix apps/web run build`: passed.
- `npm --prefix apps/web run test`: 6 passed, including 0.6 m tiles giving 5 × 6 repeats on a 3.0 × 3.6 m face, UV edits isolated to one SurfaceId, and material definitions separate from surface references.
- `npm --prefix apps/web run lint`: passed.
- `npm --prefix apps/web run test:e2e`: 1 passed. Browser created/edited a material, uploaded valid base-color and normal-map PNGs, set PBR/tile/UV values, assigned it only to the floor, exercised two-step undo/redo, saved, reloaded, and confirmed stable ObjectIds/SurfaceIds and unchanged wall assignment.
- `./scripts/verify-phase01-persistence.ps1`: passed after E2E at scene revision 22. It restarted the API and verified floor assignment, stable SurfaceIds, PBR map references/values, UV settings, and retrieval of persisted texture maps remained available.
- PostgreSQL migration-on-startup completed successfully; no EF migration was necessary because the existing Scene JSONB schema stores the additive Scene v1 material data.

During implementation, E2E exposed a stale Vite API proxy port, asset-root differences between API launch modes, and an invalid PNG fixture that had passed signature-only checks; these were corrected. Uploads now fully decode PNG/JPEG/WebP with Magick.NET, an Apache-2.0 package, before saving. The restart script uses the same shared asset root as Playwright and checks texture references safely. PostgreSQL access remained scoped to `roomforge`; `banhang` was never accessed. No credentials were printed or committed.

## 2026-10-31 — Phase 02 object editing follow-up completed

Added independent object selection and an object inspector with meter-based X/Y/Z position, Euler-degree rotation mapped to the canonical quaternion, physical width/height/depth inputs, visibility controls, and translate/rotate/resize R3F gizmos. Gizmo scale is committed as explicit dimensions in meters. Transform operations only replace object position, rotation and dimensions, retaining the same active object, surfaces, material references and UV settings. Scene edits use the existing undo/redo scene snapshots and optimistic Scene JSON save path.

Optional deletion requires confirmation and moves the complete object into `deletedObjects`; restore moves it back with the same ObjectId and SurfaceIds. Structural/locked objects are protected in the editor and API persistence validation. Updates cannot remove protected objects/surfaces, hard-discard optional objects, or discard already archived objects. Extended the Scene contract and API contract to document these rules. Extended the local PostgreSQL restart verifier to compare transforms, visibility, active/archive object IDs and surface assignment/UV records.

Verification actually run:

- `dotnet build src/RoomForge.slnx`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx`: 14 passed, including API persistence validation for protected object/surface retention and optional object archive/restore.
- `npm --prefix apps/web run build`: passed.
- `npm --prefix apps/web run test`: 10 passed across 3 files, including transform identity preservation, surface/material/UV preservation, visibility, archive/restore, structural protection and invalid transform rejection.
- `npm --prefix apps/web run lint`: passed without warnings after removing the numeric-input state synchronization lint warning.
- `npm --prefix apps/web run test:e2e`: 2 passed. Material PBR/UV/upload persistence and object move/rotate/resize, gizmo mode selection, hide/show, undo/redo, confirmed archive, restore, save and reload all passed. Object E2E additionally passed again after the final numeric-input change.
- `./scripts/verify-phase01-persistence.ps1`: passed against local PostgreSQL `roomforge` at scene revision 33 after an actual API process restart. The verifier compared object transforms, visibility, surface records and archive state, then confirmed the existing material/PBR/UV and texture asset checks.
- `git diff --check`: passed.

All database access used only the configured local `roomforge` database (and the existing constrained provisioner where applicable). `banhang` was never accessed. No credentials were printed or committed. No AI provider integration, Docker requirement, SQLite, or schema migration was introduced for this follow-up.

## 2026-10-31 — AI reconstruction foundation (Phases 03 + 04, active)

Roadmap numbering is unchanged: Phase 03 remains **Manual photo reconstruction**, Phase 04 remains **Durable AI jobs with mock provider**, Phase 05 remains real perception, and Phase 06 remains real image-to-3D. This task delivered a shared upload → durable mock analysis → human review → candidate preview → explicitly confirmed Scene JSON application path. No paid provider integration was started.

Implemented a versioned `SceneAnalysisResult` v1 contract with bounded image dimensions and image-space polygons, detected surfaces/objects, confidence/provenance, estimated dimensions and positions, and explicit camera/scale assumptions. Unknown scale remains unknown unless a user adds a measured pixel/real-length anchor. Provider-neutral Application interfaces cover scene understanding, segmentation, depth estimation, image-to-3D, and capability discovery. Only deterministic mock providers are registered; mock outputs explicitly identify their provenance, and image-to-3D reports unavailable with primitive fallback.

Added decoded and bounded PNG/JPEG/WebP room-image upload through the existing local asset store, PostgreSQL image metadata and durable reconstruction jobs, idempotency keys, bounded attempts, leases/recovery, progress, cancel/retry states, and a .NET background worker. The React review workspace supports image-space mask review, object inclusion/exclusion, type and dimension edits, explicit object/surface links, camera/scale assumption review, non-persisting preview, and explicit revision-checked apply. Existing Scene JSON remains canonical; preview does not write the scene, and linked identities/material/UV data are retained. The original scene changes only after confirmation.

The additive EF migration `20261031131534_AddReconstructionJobs` was applied to the configured local PostgreSQL `roomforge` database. `scripts/verify-reconstruction-persistence.ps1` passed: it uploaded an image, created an idempotent mock job, verified completed result and preview non-mutation, rejected unconfirmed apply, explicitly applied a candidate, restarted the API process, then reloaded the job, image bytes and saved Scene JSON with stable ObjectId/SurfaceIds and surface material/UV data. The API health endpoint reported only `roomforge`; `banhang` was never accessed and credentials were not printed.

Verification actually run:

- `dotnet build src/RoomForge.slnx --no-restore -m:1 -nr:false -v:minimal`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx -m:1 -nr:false -v:minimal`: 18 passed, including contract validation, deterministic mock capability behavior, object exclusion and stable identity conversion.
- `npm --prefix apps/web run build`: passed.
- `npm --prefix apps/web run test`: 10 passed across 3 files.
- `npm --prefix apps/web run lint`: passed.
- `npm --prefix apps/web run test:e2e`: 4 passed serially, including secure invalid-image rejection, mock upload/job/review/idempotency, preview non-mutation, explicit apply and stable identities, plus the existing material and object-editing browser flows. The reconstruction E2E reseeds and restores its reserved sample-scene fixture to avoid cross-test state contamination.
- `scripts/verify-reconstruction-persistence.ps1`: passed after an actual API restart against `roomforge`.
- `git diff --check`: passed after final edits.

Phase 03 and Phase 04 remain active rather than being marked complete: the current foundation reviews provider-produced image polygons but does not yet provide direct polygon drawing/edit handles or the full manual-only parametric room-authoring workflow. The current verification proves success/idempotency and restart persistence; exhaustive transient-failure injection and retry-after-failure recovery still need a dedicated test before Phase 04's complete acceptance gate. Phases 05 and 06 remain out of scope.

Pre-commit re-verification of this slice: `dotnet build src/RoomForge.slnx --no-restore -m:1 -nr:false -v:minimal` passed with zero warnings/errors; `dotnet test src/RoomForge.slnx --no-build` passed 18/18; `npm --prefix apps/web run test` passed 10/10; `git diff --check` passed. The web dev proxy and Playwright webServer now target the API's configured `http://127.0.0.1:5248` profile port instead of `5080`. Playwright was not re-run for this re-verification (it was run earlier with 4 passing).

## 2026-11-01 — Phase 04 reliability work resumed

Preserved the existing uncommitted reconstruction foundation and added durable submission-state transition validation. State writes now compare the expected current state; `outcome_unknown` cannot be changed through the ordinary setter, and reconciliation only clears it to `not_attempted` when provider idempotency is recorded as supported. Reconciled accepted/running/completed outcomes require and persist a provider job ID. User retry rejects jobs with an unresolved remote submission state. The reconciliation GET contract is documented as observational; it does not claim to contact a provider.

Added discoverable PostgreSQL integration tests for concurrent idempotent job creation, lease recovery, bounded retries/backoff, reconciliation safeguards, cancellation and malformed provider output preserving the source image. Integration setup now fails explicitly when the approved local `roomforge` connection is unavailable, and removes job rows before their referenced image rows. An EF scaffolding correction briefly rolled back the uncommitted submission migration; the corrected combined migration `20261101050709_AddSubmissionReliabilityAndAnnotations` was then applied by the integration test run to local `roomforge`. No other database was accessed.

Verification witnessed for this resumed slice:

- `dotnet build src/RoomForge.slnx --no-restore -m:1 -nr:false -v:minimal`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx --no-build -m:1 -nr:false -v:minimal`: passed, 6 integration + 26 unit tests, zero skipped.
- `npm --prefix apps/web run test:e2e`: passed, 4/4, including durable mock reconstruction preview and explicit apply; the full run took about 2.4 minutes.
- Phase 04 remains active. The current pipeline uses in-process deterministic mocks, so an actual provider-side reconciliation call and process restart during a live worker operation remain unverified and are prerequisites for closing the phase.

## 2026-11-01 — Phase 03 manual annotation slice implemented

Added a versioned `AnnotationDocument` contract and validator for bounded image-pixel polygons, supported semantic roles, user-supplied measurement spans and camera hints. Documents are stored separately from `SceneDocument` in PostgreSQL JSONB and reference a project-owned source image. `POST` creates a blank manual document, `GET` returns the document and numeric ETag, and `PUT` accepts only annotation fields with `If-Match`, increments the revision server-side and returns `409` on stale updates or `428` when the precondition is absent. The image reference is protected by a restrictive FK.

The reconstruction workspace now has an upload-for-manual-annotation path that creates no analysis job. The Konva editor draws and edits polygon points in image pixels, snaps input to one-pixel increments, supports region roles and measured spans, validates simple polygons and span lengths, and saves revisions. The latest project annotation is restored when the workspace is reopened; users can continue editing after navigating away or refreshing the app.

Verification witnessed:

- `dotnet build src/RoomForge.slnx --no-restore -m:1 -nr:false -v:minimal`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed, 7 PostgreSQL integration tests + 29 unit tests, zero skipped. Includes JSONB reload, revision conflict and image/job persistence checks.
- `npm --prefix apps/web run build`: passed.
- `npm --prefix apps/web run test`: passed, 14 tests across 4 files, including image/display coordinate round trip, snapping, polygon self-intersection and measured-span validation.
- `npm --prefix apps/web run lint`: passed.
- `npm --prefix apps/web run test:e2e -- --grep "manual image annotations"`: passed. Browser uploaded a generated test image, drew a region and measured span, saved, reopened the workspace, changed the region role, confirmed revision 3 after reload and received 409 for a stale write.
- `npm --prefix apps/web run test:e2e`: passed, 5/5. The mock reconstruction, invalid-image rejection, manual annotation, material/UV persistence, and object editing/archive flows all passed together.

Phase 03 remains active pending the geometry/API/UI work tracked below and its ten-step acceptance run. Phase 04 also remains active pending provider-side reconciliation and a real API process restart during worker processing.

## 2026-11-01 — Phase 03 geometry foundations (active)

Added a pure geometry layer for image/camera/world/object-local transforms, projection status, plane hypotheses, topology candidates, camera calibration checks, parametric candidate geometry and photo reprojection reports. Topology edges begin as `unknown_candidate`; no wall is inferred from a floor outline. Camera calibration and parametric generation return `InsufficientData` when a pose or user reference is missing. Estimated dimensions retain explicit provenance and unobserved floor thickness remains `null/Unknown` in the geometry spec. Reprojection diagnostics explicitly avoid claiming metric accuracy.

Removed the preview converter's fabricated 3.6 m / 2.7 m / -1.8 m defaults. Surface detections remain 2D candidates with diagnostics until placement and thickness are known; SceneDocument retains its current positive-dimension requirement. The full geometry spec is not yet persisted as an append-only scene candidate, and application to SceneDocument and the Phase 03 ten-step acceptance run remain outstanding.

Verification witnessed for this slice:

- `dotnet build src/RoomForge.slnx --no-restore -m:1 -nr:false -v:minimal`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed, 7 PostgreSQL integration + 36 unit tests, zero skipped. New tests cover transforms, unknown topology, missing/invalid camera, missing reference and unknown dimensions.
- `git diff --check`: passed; only Git line-ending normalization notices were emitted.
- Earlier web build/test/lint and 5/5 Playwright E2E remain evidence for M2.

Extended the slice with a persisted topology store, additive `AddRoomTopologies` migration, and OpenAPI routes to build, reload and patch topology using expected-revision compare-and-swap. Edges remain unknown until the user selects a role; corrections are saved with `user_supplied` provenance. A topology tied to an outdated annotation cannot be corrected; it must be rebuilt. The React annotation workspace now exposes this correction flow and reloads the saved topology when reopened. Added an alignment route that validates a supplied camera model and world edge coordinates before calculating reprojection error. `docs/API_CONTRACT.md` now distinguishes implemented topology/alignment routes from planned geometry/candidate/export routes.

Updated verification witnessed:

- `dotnet build src/RoomForge.slnx --no-restore -m:1 -nr:false -v:minimal`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed, 8 PostgreSQL integration + 37 unit tests, zero skipped. The integration test creates, reloads, corrects and reloads topology, then verifies a stale revision is rejected. Unit tests also verify projection RMS against a synthetic camera fixture and preserve the accuracy caveat.
- `npm --prefix apps/web run build`: passed after topology editor integration.
- `npm --prefix apps/web run test`: passed, 14 tests across four files.
- `npm --prefix apps/web run lint`: passed without warnings after removing the synchronous effect state write.
- `npm --prefix apps/web run test:e2e -- --grep "manual image annotations"`: passed, including topology classification, save and workspace reload.
- The same E2E was rerun after adding geometry preview; it asserts that missing floor thickness remains `unknown`.

Added append-only reconstruction candidate records and a migration, an API to save/reload geometry candidates against exact Scene/annotation/topology revisions, explicit meter overrides for unknown dimensions, and a separately confirmed apply use case through `ScenePersistence`. Apply rejects unconfirmed requests, changed source/base revisions, existing object IDs, unsupported geometry and unknown/nonpositive dimensions. It creates stable SurfaceIds for generated floor/wall faces and assigns a Scene material, so PBR editing remains surface-specific. Generated structural objects stay movable/scalable/rotatable and visible-toggleable while protected from deletion. Candidate JSON and its source revisions remain stored after apply. The UI can fill unknown dimensions, save a candidate without changing the Scene, reload it on reopening, inspect a 3D candidate preview and explicitly confirm apply.

Latest focused verification:

- `dotnet test src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed, 10 PostgreSQL integration + 37 unit tests, zero skipped. Includes append-only candidate persistence and applying a candidate to an isolated valid Scene fixture only after confirmation; verifies the resulting revision and stored dimensions.
- `npm --prefix apps/web run test:e2e -- --grep "manual image annotations"`: passed, 1/1, covering polygon/span annotation, topology correction and reload, geometry preview with unknown thickness, candidate save/reload and unchanged Scene revision before apply.
- The browser flow draws a horizontal floor scale and vertical wall-height measurement, classifies the floor boundary as wall supports, previews the floor plus four walls, fills unknown thickness values and opens the persisted candidate in the 3D viewer.
- `npm --prefix apps/web run build`: passed after the 3D preview UI change.
- `npm --prefix apps/web run test`: 14 passed; `npm --prefix apps/web run lint`: passed without warnings.

Phase 03 remains active pending browser-level confirmed apply, camera/world alignment controls, direct geometry editing after apply, save/reload after API restart and the full ten-step flow. Phase 05 provider survey is documented in `docs/PROVIDER_EVALUATION.md`; no adapter is enabled and no live API was called. Phase 06 has a documented Hunyuan3D candidate, but its provider job/crop/asset integration is not implemented. Phase 07 export/operations gates have not started.

## 2026-11-01 — Phase 03 geometry browser acceptance extended

Extended the manual reconstruction browser flow to annotate the floor and a separate wall region, add independent floor-scale and wall-height measurements, classify the floor boundary edges, preview a floor plus four wall candidates, fill unknown thickness values, persist/reload the append-only candidate and confirm apply. The flow asserts that preview/save do not change the Scene revision and that explicit apply does. Generated architecture stays transformable while remaining protected from deletion. The test fixture now preserves protected architecture by category/surface semantics as well as the `locked` flag, matching the Scene validator.

Verification witnessed for the current worktree:

- `dotnet build src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed, 10 PostgreSQL integration + 37 unit tests, zero skipped.
- `npm --prefix apps/web run build`: passed.
- `npm --prefix apps/web test`: passed, 14 tests across 4 files.
- `npm --prefix apps/web run lint`: passed.
- `npm --prefix apps/web run test:e2e -- e2e/reconstruction.spec.ts`: passed, 4/4 serially. Covers mock upload/review/preview/explicit apply, invalid-image rejection, manual annotations with two regions and two measurements, topology correction, geometry preview, candidate persistence/reload and explicit candidate apply.

The Phase 03 apply gate is now exercised in the browser. Remaining Phase 03 gates: interactive camera/world alignment controls, edit/transform/material changes and save/reload after API restart, and the complete ten-step acceptance run. Phase 04 still lacks provider-side reconciliation and a restart while a real worker operation is in flight. Phase 05/06 provider adapters and asset pipelines remain unimplemented; no live API calls have been made and live-smoke spend is **$0 of the approved $20 USD cap**. Phase 07 export and operational reliability remain unimplemented. No phase is marked complete by this update.

## 2026-11-01 — Phase 03 camera alignment UI and confirmed apply

Added camera intrinsics, camera position/quaternion and per-topology-edge world endpoint inputs to the manual reconstruction workspace. The UI submits the camera hypothesis and meter coordinates to the existing alignment endpoint and displays projected RMS, visibility counts, and uncertainty notes. It explicitly states that reprojection consistency does not prove metric accuracy. The browser test supplies a synthetic camera/world-edge hypothesis through these controls and receives the alignment report.

Extended the same browser path to reopen the saved candidate, explicitly apply it, then read the canonical Scene and verify the generated architecture floor and its independent material assignment. After apply the workspace reloads and annotation revision editing still works. Candidate preview now omits instances already present in the applied Scene, preventing duplicate React object keys when viewing an applied candidate.

Verification for this slice:

- `npm --prefix apps/web run build`: passed.
- `npm --prefix apps/web run lint`: passed.
- `npm --prefix apps/web run test`: 14 passed across 4 files (last full run before this UI addition; no unit source changed).
- `npm --prefix apps/web run test:e2e -- e2e/reconstruction.spec.ts`: passed 4/4 before adding the final explicit-apply assertion; after that assertion, the manual annotation/alignment/apply test passed 1/1, and a further rerun passed 1/1 with no duplicate-key console warnings.
- `dotnet build` and `dotnet test` remain green from the immediately preceding full check: zero warnings/errors; 10 PostgreSQL integration + 37 unit tests, zero skipped. No backend code changed in this UI slice.

Phase 03 now has UI alignment and browser-confirmed apply. Direct dimension and material edits for generated architecture have since passed save/reload through PostgreSQL in the browser; restart persistence for the geometry path and the complete ten-step acceptance checklist remain outstanding. Phases 04–07 remain active as already recorded; live provider spend remains $0/$20 USD.

The manual browser acceptance now additionally edits the generated floor width and assigns a different surface material in the main editor, saves through the Scene API, reloads from PostgreSQL and checks both values against the stored Scene JSON. Its focused run passed after the UI alignment/candidate-apply changes. This does not yet cover an API restart for the geometry candidate, nor does it supply a licensed real interior photo.

## 2026-11-01 — Phase 03 complete

Closed the remaining Phase 03 acceptance gates with the licensed interior-photo fixture `examples/interior-reference.jpg`; source and usage details are in `examples/interior-reference.LICENSE.md`. The Playwright manual path uploads that photo without starting an AI job, draws separate floor and wall regions, records horizontal scale and vertical height references, corrects floor-wall topology, enters a camera/world-edge hypothesis and reviews reprojection, previews the estimated room shell with unknown thickness still explicit, saves/reopens the candidate, and applies only after confirmation. In the main Scene editor it changes the generated floor width and assigns a different material to its surface, saves, reloads from PostgreSQL and verifies the stored dimensions/material.

Added `scripts/verify-geometry-persistence.ps1` as an API restart verifier. It starts the API against the configured local database, checks `/health/database` reports `roomforge`, creates the annotated room/candidate from the same licensed interior photo, applies the candidate, edits its Scene dimensions/material, stops and restarts the API, then verifies annotation/topology revisions, candidate provenance, floor/wall objects, stable IDs, edited dimensions/material and Scene revision. It never accesses `banhang` or prints connection credentials.

Final Phase 03 verification:

- `dotnet build src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed 10 PostgreSQL integration + 37 unit tests, zero skipped.
- `npm --prefix apps/web run build`: passed.
- `npm --prefix apps/web test`: passed 14 tests across 4 files.
- `npm --prefix apps/web run lint`: passed.
- `npm --prefix apps/web run test:e2e -- e2e/reconstruction.spec.ts`: passed 4/4 serially. The manual photo path includes alignment, confirmed candidate apply, generated geometry edits, material reassignment, save and reload.
- `scripts/verify-geometry-persistence.ps1`: passed after an actual API stop/start against local `roomforge` using the licensed photo.
- `git diff --check`: passed; Git may report configured LF-to-CRLF normalization notices.

Phase 03 is complete. Its geometry is intentionally approximate: planar image-space proportions use user measurements, the rectangular floor proxy is not perspective rectification, and reprojection RMS only checks consistency with the supplied camera/world-edge hypothesis. No exact metric accuracy is claimed. Phase 04 remains active pending provider-side reconciliation and a process restart during a live worker operation. Phases 05–07 remain active; the provider survey is recorded, but provider adapters, 3D asset processing and export/operations are not yet complete. Live AI calls remain at $0 of the approved $20 USD cap.

## 2026-11-01 — Phase 04 durable mock jobs complete

The seven-state remote-submission safeguards and bounded local worker lifecycle now have both integration and process-level evidence. `OutcomeUnknown` cannot be changed with the ordinary state setter, retry is rejected while remote outcome is uncertain, reconciliation records require safe transitions and provider job IDs for accepted/running/completed states, and the public reconciliation status endpoint reports persisted state plus `requiresReconciliationBeforeSubmit`. Since Phase 04 runs only a deterministic local mock, the endpoint is explicitly observational; provider-side lookup will be supplied by the Phase 05/06 adapter that owns a real remote submission.

Added a bounded test configuration for mock processing delay and worker lease duration. `scripts/verify-reconstruction-persistence.ps1` now starts a real local API process against `roomforge`, submits and completes an idempotent mock job, checks the reconciliation response, preview/apply behavior and Scene/image persistence, then starts another long-running mock job, kills the API process while its worker is inside scene understanding, restarts the API with a short lease and confirms the job is reclaimed and succeeds on attempt 2. It verifies the resulting analysis and source image persist. Credentials are never output; the script confirms the connected database name is `roomforge`.

Added a PostgreSQL integration test for user retry after safe terminal failures, including rejection after the third/max attempt. Existing tests cover concurrent duplicate keys, expired lease recovery, exponential retry/backoff to `MaxAttempts`, cancellation, ambiguous submission safeguards, malformed provider output and source-image retention.

Phase 04 verification witnessed:

- `dotnet build src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx -m:1 -nr:false -v:minimal`: 11 PostgreSQL integration + 37 unit tests passed, zero skipped.
- `scripts/verify-reconstruction-persistence.ps1`: passed; in-flight process restart recovered the job on attempt 2, and the reconciliation status endpoint returned the safe persisted mock state.
- `npm --prefix apps/web run test:e2e -- e2e/reconstruction.spec.ts`: passed 4/4 after backend changes.

Phase 04 is complete. Its remaining capability boundary is explicit: no external provider is configured in the mock phase, so an `OutcomeUnknown` remote submission remains blocked until a real provider adapter performs lookup. Phases 05–07 remain active. Live API spend remains $0 of the approved $20 USD total cap.

## 2026-11-01 — Provider survey refresh and Phase 07 JSON export slice

Rechecked fal's official SAM 3 API/model pages and Hunyuan3D v3 model page. SAM 3 accepts a required `image_url`, supports data URIs and hosted URLs, and documents queue submit/status/result; the model page shows $0.005 per request. Hunyuan3D v3 returns a `model/gltf-binary` GLB and the schema example is about 64 MB; the previously surveyed Geometry-only price is an estimate to recheck in the account before live use. The official privacy policy was last updated 2026-07-22 and includes uploaded images in data collected. API Services terms say client content sent to third-party models is transferred to those third parties and the no-training restriction excludes models marked Pending Enterprise Ready. Per-model retention/deletion and DPA applicability remain unconfirmed; customer photos must not be sent. No key was found or displayed, and live spend remains $0/$20.

Implemented `GET /api/scenes/{sceneId}/export.json` as a downloadable versioned envelope containing the persisted Scene, export format version and exact revision. Added an Export JSON action in the editor and updated the API contract. This deliberately exports the last saved revision; unsaved browser edits are not silently included. GLB export and the rest of Phase 07 are not implemented yet.

Verification for this slice:

- `dotnet build src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed 11 PostgreSQL integration + 37 unit tests, zero skipped.
- `npm --prefix apps/web run build`: passed.
- `npm --prefix apps/web test`: passed 14 tests across 4 files.
- `npm --prefix apps/web run lint`: passed.
- `git diff --check`: passed; Git reports only configured LF-to-CRLF normalization notices.

Remaining approved work: Phase 05 still needs a typed SAM adapter integrated with the durable submission/reconciliation lifecycle, reviewed mask/polygon path, UI capability/error handling, fixtures and three live observations. Phase 06 still needs a safe crop/image-to-3D provider pipeline, asset validation/import/edit/save/reload and one real mesh. Phase 07 still needs GLB export, independent round-trip checks, cost/quota controls, metrics, asset cleanup, backup/restore and personal-machine operations guidance. No live provider call was made because credentials and provider-specific retention/DPA conditions are not established.

### Phase 05 transport client foundation

Added `FalSam3QueueClient`, a typed `HttpClient` wrapper based on fal's official SAM 3 and queue REST contracts. It submits a bounded PNG/JPEG/WebP data URI with an explicit prompt and mask/score/box options, reads queue status and results, validates the request ID, state, result URL host (`*.fal.media`), PNG type, file-size/dimension bounds and optional confidence/box values, and caps JSON metadata at 1 MB. `FAL_KEY` can be supplied by .NET configuration/User Secrets or the environment and is injected only in server-side DI; it is omitted from logs/errors. HTTP 429 and 5xx are retryable `HttpRequestException`s for the existing bounded job retry policy; provider rejections/schema errors fail as non-transient data errors. It does not fetch the returned mask yet.

The client is deliberately not activated by the reconstruction job worker. Current worker analysis invokes segmentation without durable pre-submit state transitions, so activation would allow a restart to issue duplicate paid requests and cannot safely reconcile an ambiguous submission. Before wiring it, extend worker submission lifecycle to persist `in_progress` before POST, store request ID on acceptance, resume polling from persisted ID after restart, and set `outcome_unknown` on ambiguous submit failures; then normalize trusted mask output to an editable image-space representation and wire it into human review. The phase's real-photo and privacy gates are still outstanding.

Verification for this provider foundation:

- `dotnet test tests/RoomForge.UnitTests/RoomForge.UnitTests.csproj -m:1 -nr:false -v:minimal`: passed 43 unit tests, including 6 SAM queue transport fixture cases; no real network calls.
- Live provider spend remains $0/$20 USD.

### Phase 05 durable SAM segmentation path

Wired the opt-in `FalSam3SegmentationProvider` into the durable reconstruction analysis path. Configure `Reconstruction:SegmentationProvider=fal-sam3` and a server-side `FAL_KEY`; absent that explicit selection, deterministic mock segmentation remains active. Before queue submission, the worker writes `in_progress`; on acceptance it persists the fal request ID; a recovered worker with `accepted`/`running` polls that saved ID without POSTing again. The worker refreshes its PostgreSQL lease during the potentially long submission and each polling interval. A socket/5xx/timeout ambiguity is persisted as `outcome_unknown` and the job cannot retry automatically. A definitive 429/rejection is marked `terminal_failure` and may use the existing bounded retry path. Successful SAM output currently contributes its highest-scored documented normalized box as a four-point image-space floor polygon; it is explicitly labeled `provider-box` and carries the provider score. It is not a pixel contour. The review UI now lets the user edit every polygon vertex and labels corrected geometry `user-corrected`; preview validation accepts only that exact provenance change while keeping IDs, provider score, role and estimated dimensions protected. Capability UI shows the selected provider ID and analysis provenance instead of claiming every result is mock.

Added PostgreSQL-backed integration fixtures for (a) accepted request/result persistence with score and box normalization, (b) resuming an already accepted job without a second POST, (c) ambiguous POST failure persisting `outcome_unknown` and blocking a second POST, and (d) concurrent spend reservations never exceeding the configured cap. No external network or paid call is made by those tests. Provider reservations are persisted in PostgreSQL and serialized across concurrent workers with a transaction-scoped advisory lock. The shared ledger totals reservations across providers and rejects any reservation above the hard $20 USD aggregate ceiling; SAM reserves $0.01 per request (twice the currently listed $0.005 price) as a safety margin before POST. Reservations are conservative and are not refunded after a rejected/ambiguous attempt. This shared contract can also gate Hunyuan3D in Phase 06. Live smoke stays disabled until provider privacy/retention terms are confirmed. No customer or licensed fixture image was sent to fal.

Verification after lifecycle/UI wiring:

- `dotnet build src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed, zero warnings/errors.
- `dotnet test src/RoomForge.slnx -m:1 -nr:false -v:minimal`: passed 15 PostgreSQL integration + 43 unit tests, zero skipped. Includes the new concurrent spend-cap reservation test and applied its migration on local `roomforge`.
- `npm --prefix apps/web run build`: passed (Vite emitted the existing large-bundle advisory).
- `npm --prefix apps/web test`: passed 14 tests across 4 files.
- `npm --prefix apps/web run lint`: passed.
- `npm --prefix apps/web run test:e2e -- e2e/reconstruction.spec.ts`: passed 4/4; includes polygon correction review/preview, manual annotation flow and explicit geometry apply.
- `git diff --check`: passed; only line-ending normalization notices.

Rechecked the official fal DPA (updated 2026-07-31): it describes processing scope and retention until agreement termination, with deletion at termination. The public SAM 3 page does not establish whether the hosted model is called through a third-party API or marked “Pending Enterprise Ready,” nor whether this account's DPA coverage applies. API Services terms describe those exclusions and possible third-party transfer. This is recorded in `docs/PROVIDER_EVALUATION.md`; only public/licensed non-sensitive photos are eligible for a smoke test until the model/account details are confirmed. Phase 05 remains incomplete: perform credentialed licensed-photo smoke calls under the $20 cap, review outputs from three representative photos, and improve from the current box-derived polygon to a pixel-mask contour if observed quality supports that change. Phase 06 and remaining Phase 07 gates are still open.

### Phase 05 mask normalization follow-up — 2026-11-01

The SAM provider path now downloads the returned PNG mask only from a validated fal media host, rejects redirects and oversized/non-PNG assets, decodes pixels through Magick.NET's pixel mapping, and derives a bounded row-envelope polygon within the provider box. A PostgreSQL integration fixture covers the complete result path and asserts the mask-derived provenance and a non-rectangular polygon. If the mask contains no usable foreground, the provider falls back to the validated box and labels that provenance. This envelope is an approximation and does not claim exact mask contour quality. The focused PostgreSQL integration test passed. Full suite passed: 15 PostgreSQL integration tests and 43 unit tests, zero skipped; the compiler reported a nullable warning in the pixel buffer check that was subsequently fixed and still needs a clean-build rerun. Live provider spend remains $0/$20 USD; no real image was sent. M4 still requires account/model retention confirmation and three human-reviewed licensed-photo observations. Phases 06–07 remain open.

### Verification update — 2026-11-01

After the nullable-buffer fix, `dotnet build src/RoomForge.slnx -m:1 -nr:false -v:minimal` passed with 0 warnings and 0 errors; `dotnet test src/RoomForge.slnx -m:1 -nr:false -v:minimal` passed all 15 PostgreSQL integration and 43 unit tests with zero skipped; web build, 14 web tests and lint passed; `npm run test:e2e -- e2e/reconstruction.spec.ts` passed all 4 browser tests. These verify the mock/manual and persistence paths, not a paid provider call. Phase 05 live review remains pending under the $20 aggregate cap and privacy gate. Phase 06 Hunyuan asset generation/import and Phase 07 GLB/operations gates remain unimplemented and open.

### Phase 07 GLB export slice — 2026-11-01

Added `SharpGLTFSceneExporter` in Infrastructure with SharpGLTF Toolkit 1.0.7, a revision-backed `GET /api/scenes/{sceneId}/export.glb` route, and a web Export GLB control. The current exporter supports box geometry: six separate quads with face normals and tiled/rotated/offset UVs; one primitive per assigned material plus a default primitive for uncovered faces; PBR base color/metalness/roughness; ObjectId and SurfaceId extras; and explicit visibility/measurement status extras. It rejects non-box geometry and mapped textures as `422` rather than silently dropping them. SharpGLTF round-trip tests verify all sample object/surface IDs, 24 position entries per box, the two-material Countertop's three material groups, hidden-object extras and unsupported geometry rejection. `dotnet test` targeted exporter tests passed 2/2. The browser E2E test downloaded a `.glb`; all four Playwright reconstruction tests passed. `dotnet test` full suite passed 15 PostgreSQL integration + 45 unit tests, zero skipped; web build/tests/lint passed. Remaining Phase 07 gates include texture embedding, plane/extruded geometry, bounds parity at 1e-3 m, an independent second GLB parser/validator, operational quotas/metrics/asset cleanup/backup-restore/runbook. Phase 06 provider job, selected crop, safe generated-asset import and persistence remain open. No live AI calls; spend remains $0/$20 USD.

### Phase 07 GLB export slice — 2026-11-01

Added `SharpGltfSceneExporter` in Infrastructure with SharpGLTF Toolkit 1.0.7, a revision-backed `GET /api/scenes/{sceneId}/export.glb` route, and a web Export GLB control. The current exporter supports box geometry: six independent quads with face normals and tiled/rotated/offset UVs; one primitive per assigned material plus a default primitive for uncovered faces; PBR base color/metalness/roughness; ObjectId and SurfaceId extras; and explicit visibility/measurement status extras. It rejects non-box geometry and mapped textures as HTTP 422 rather than silently dropping them. SharpGLTF read-back tests verify sample object/surface IDs, 24 position entries per box, material grouping, hidden-object extras and unsupported geometry rejection. `dotnet test` passed 15 PostgreSQL integration + 45 unit tests, zero skipped; web build/tests/lint passed; the browser E2E suite passed 4/4, including GLB download. Remaining Phase 07 gates: texture embedding, plane/extruded geometry, bounds parity at 1e-3 m, an independent second GLB validator, quotas/metrics/asset cleanup/backup-restore/runbook. Phase 06 provider job, user-selected crop, safe generated-asset import and persistence remain open. No live AI calls; spend remains $0/$20 USD.

### Phase 06 Hunyuan3D transport fixture follow-up — 2026-11-01

Added a typed fal Hunyuan3D v3 queue transport client based on the current official schema (queue submit/status/result; `model_glb` metadata) and registered it in Infrastructure with redirects disabled. Geometry mode keeps the safety checker on, requests no PBR and omits custom face count/multi-view because those are separately charged options. The client restricts image/output types and sizes, accepts only HTTPS fal media result URLs, rejects redirects, validates GLB v2 container metadata and parses the binary with SharpGLTF before returning it. Synthetic HTTP fixtures test submit/auth/body, polling, result parsing, trusted-host checks, input rejection and a full valid GLB download/parse: 3/3 passed. This remains transport-only, not an active provider or durable object-mesh job; no reservation or external submit is wired, so it cannot yet be used for production generation. Official model page rechecked 2026-11-01: Geometry $0.225, custom face count/PBR/multi-view each add $0.15. No live calls; spend $0/$20 USD. Phase 06 remains open pending object-specific durable jobs, user-confirmed crop, quota reservation before submission, asset attachment/edit/save/reload, provider account privacy/retention confirmation and one permitted real mesh.

### Phase 06 durable object-mesh workflow â€” 2026-11-01

Wired the Hunyuan3D queue client to a separate PostgreSQL-backed object-mesh job pipeline. A user explicitly confirms the reviewed image-pixel crop and current Scene revision; the server verifies that the source analysis succeeded, source image and movable non-structural Scene object match, bounds the crop and stores its PNG plus SHA-256. Job idempotency rejects reuse with different source/object/scene/revision/crop identity. The worker claims a lease, reserves $0.50 against the shared hard $20 USD cap before remote submit, persists `in_progress` and accepted provider IDs, resumes polling accepted jobs without another submit, marks ambiguous submits `outcome_unknown`, validates and stores GLB privately. Attaching the asset to Scene requires a separate explicit revision-checked apply. Scene mesh references preserve semantic ObjectId, transforms and independent structural surface material assignments; the viewer loads the generated asset while retaining the semantic object fallback.

Added the mesh job endpoints to the API contract and provider configuration/safety notes. A PostgreSQL synthetic-provider integration test covers completed queue flow, four HTTP fixture requests (submit/status/result/download), validated asset persistence and spend ledger reservation without external calls.

Verification for this slice:

- `dotnet test src/RoomForge.slnx --no-restore`: passed 17 PostgreSQL integration + 52 unit tests, zero skipped; the command built the solution.
- `npm --prefix apps/web run build`: passed; Vite reports a large-chunk advisory (largest bundle about 1.0 MB minified).
- `npm --prefix apps/web run test`: passed 14 tests across 4 files.
- `npm --prefix apps/web run lint`: passed.
- The first full focused Playwright run had 3/4 pass and exposed Windows text-encoding-sensitive assertions. Those locators were made encoding-tolerant; rerunning the affected manual annotation/alignment/apply path passed 1/1 in 1.5 minutes. The final full rerun after those edits passed 4/4 in 3.9 minutes.

Phase 06 remains incomplete. Outstanding: Playwright coverage for generated asset attach/adjust/visibility/save/reload; one real generated mesh after privacy/retention and credential checks; user-facing provider capability and failure handling review. No customer image was sent; live provider spend remains $0/$20. Phase 05 still needs three human-reviewed licensed-photo observations and provider/account privacy approval. Phase 07 still needs texture embedding or explicitly scoped rejection, geometry/bounds parity and independent GLB validation, quotas/metrics/asset cleanup, backup/restore and a personal-machine runbook.

### Phase 07 local operations runbook â€” 2026-11-01

Added `docs/LOCAL_OPERATIONS.md` covering local PostgreSQL `roomforge` startup, secret handling, provider privacy/spend gates, coordinated database plus asset backup and guarded restore guidance. This documents the manual operator path but does not claim a restore drill, automated retention/cleanup, asset consistency checker or metrics implementation. M7 remains open for those gates.

### Phase 07 independent GLB validation and bounds parity â€” 2026-11-01

Added Khronos `gltf-validator` (Apache-2.0, upstream KhronosGroup) as a web dev dependency. The Playwright download flow now validates the actual `.glb` file saved by the browser, not the API `Content-Disposition` response body, and requires zero Khronos validator errors. The first test attempt exposed that Playwright does not provide the download bytes through `APIResponse.body()` for attachment responses; the test now reads the download artifact itself. This catches structural GLB errors independently of SharpGLTF.

Expanded the SharpGLTF unit round-trip to parse the GLB JSON/BIN chunks, decode every POSITION float from its buffer views, apply the serialized glTF node matrix, and compare per-object world AABBs against source Scene dimensions/rotation/position to 0.001 m. This first revealed the test's initial matrix layout conversion was transposed; the correct column-major glTF data mapping to System.Numerics row-vector convention is now asserted. Exporter-focused unit tests pass 2/2; the focused Playwright export plus Khronos validation path passed 1/1. Full suites are to be rerun after this slice.

This closes M6's independent validator and bounds parity checks for the supported box exporter. M6 remains open for texture embedding and generated-scene full export acceptance. Phase 05/06 live provider gates and M7 operational automation remain open; provider spend stays $0/$20 USD.

### Mock kitchen and isolated photo replacement — 2026-11-17

Added an authored parametric kitchen template with stable object/surface IDs, independent countertop top/edges, cabinet fronts, backsplash, walls and floor. A template picker loads the kitchen into its own scene ID, leaving the original saved scene intact. Existing scene revision checks and PostgreSQL storage handle saves. The kitchen dimensions are illustrative/estimated, not inferred from a photo; no external model or third-party asset is bundled.

Added a selected-surface photo workflow: PNG/JPEG/WebP selection, rectangular percentage crop, bounded PNG conversion and upload through the existing texture endpoint. Apply creates a new material assigned only to the selected SurfaceId, with neutral white tint and one image across the face by default. Rotation, repeat, physical tile dimensions, undo/redo and persistence reuse the existing editor. Selection highlighting now uses a subtle emissive overlay instead of replacing the material color. The original shared-material library editor still intentionally edits every surface referencing that material; the new photo action isolates the selected surface.

No AI calls, keys or provider changes. Automated and browser tests were intentionally omitted at the user's request. Build result is recorded below; save/upload/browser acceptance has not been rerun for this slice. External GLB imports remain a future slice requiring licensed models with separable semantic surfaces. Existing GLB export limitations for mapped textures remain.

Verification: `npm --prefix apps/web run build` passed (TypeScript and Vite); the existing large-bundle advisory remains. The public Tunnelmole root returned HTTP 200 after the update. No test suites or scripted browser checks were run for this change, as requested.

### Tunnel upload response fix — 2026-11-17

Reproduced the reported photo-upload failure: a valid PNG returned local HTTP
201, while the same POST through Tunnelmole returned nginx HTTP 502. Tunnel logs
confirmed that the API had actually returned 201. Added a loopback buffered HTTP
origin (`apps/web/scripts/tunnel-origin.mjs`) to remove hop-by-hop response headers
and supply Content-Length before forwarding via Tunnelmole. Started Vite on
5174 and the bridge on the existing tunnel origin 5173, retaining the public URL.
After the change the public texture POST returned 201 with JSON asset URL and
public database health returned 200. No test suite was run, per user instruction;
these were targeted diagnostic HTTP requests. The bridge is HTTP-only and does
not forward Vite HMR; its startup instructions are in LOCAL_OPERATIONS.md.

### Uploaded texture rendering fix — 2026-11-17

Made PBR texture slots explicit (null when absent) and invalidate the material
shader when asynchronously loaded maps change. Surface texture failures now reach
the visible error message, including reconstruction previews; late image loads
after effect cleanup are disposed. Also guarded scene loads by request generation:
the overlapping initial requests in React StrictMode could otherwise clear an
already-selected surface when the older request finished later.

Web production build passed. A targeted browser diagnosis through the public
Tunnelmole URL applied an unsaved red/yellow diagnostic PNG to the main countertop;
the captured image visibly shows the pattern on that top only, with the countertop
edge, island, cabinets and walls unchanged. No alerts were shown. The diagnostic
scene was not saved. No test suite was run.
