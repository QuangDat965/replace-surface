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
