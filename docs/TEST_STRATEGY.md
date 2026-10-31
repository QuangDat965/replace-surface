# Acceptance tests and quality gates

## Deterministic geometry
- `box` dimensions in meters appear correctly in world space; zero/negative dims rejected.
- Y-up / XZ-floor consistent between scene JSON, viewer and GLB export.
- A plane normal points toward the room interior (unless explicitly double-sided).
- Transform object and undo restores original transform and object identity.
- Invisible objects do not render but persist and can become visible again.

## Material correctness
- Set floor tile to 0.6 x 0.6 meters on 3.0 x 3.6 m floor; repeat counts approximately 5 x 6 given aligned rectangular UVs.
- Surface repeat multipliers, rotation and offset affect only that SurfaceId; material definition edits remain separate from surface assignments.
- PNG/JPEG/WebP uploads require matching image signatures, full image decoding, dimensions no greater than 8192 pixels per side or 40 megapixels, and a 10 MB encoded-size limit; texture asset references, PBR values and normalized surface UV settings survive save/reload and API restart.
- PBR texture color-space and normal map loaded appropriately; failure displays fallback rather than blank scene.
- Countertop-top material change does not alter countertop edge or cabinet front.
- Preserve material assignments across save/reload and export.

## Persistence and API
- Schema version/UUID validation; patch 409 on stale revision; asset URLs scoped; upload validation.
- Scene save/reload returns same semantic surface IDs and assignment data.
- Phase 01 persistence smoke uses local PostgreSQL `roomforge`; restart the API between save and reload and verify revision, Scene JSON and SurfaceIds. Target guards reject any database other than local `roomforge` before connecting. Never inspect `banhang`; no Docker/SQLite fallback.
- Jobs survive API restart; invalid model result returns a typed failure without losing original photo.

## Perception and 3D (later)
- Compare masks and room plane proposals with annotated fixtures.
- Explicit mark of relative depth vs metric depth.
- Record camera assumptions and dimension provenance.
- Error paths for no walls detected, unresolvable vanishing point, missing scale, occlusion and unrealistic model bounds.
- Model export re-import has acceptable bounding box/orientation and retained object identity in companion JSON.

## UI E2E
Start sample project -> select floor -> assign material -> adjust tiling -> hide furniture -> resize countertop -> undo -> save -> reload -> verify.
