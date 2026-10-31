# Phase 02 — Material editor

Act as the RoomForge AI orchestrating coding agent. Follow `AGENTS.md`, `MASTER_PROMPT.md`, relevant `docs/*`, and the appropriate skills. Inspect the current repository state first.

## Scope of this phase slice
Implement a scene-backed material library and inspector: safe image texture upload, base-color/roughness/metalness/normal maps, physical tile width and height in meters, surface-specific UV rotation/offset/repeat multipliers on both axes, PBR preview, per-surface assignment, undo/redo and revisioned Scene JSON saves. Preserve stable ObjectIds and SurfaceIds. Object transforms, dimensions and visibility are a separate follow-up; do not implement them in this slice. No 2D-to-3D reconstruction or AI.

## Execution contract
1. Produce a short implementation plan and list of files expected to change.
2. Implement the **smallest complete vertical slice** satisfying this phase; no unrequested next-phase features.
3. Keep material definitions in SceneDocument.materials and store assignments as Surface.materialId references. Upload only bounded, signature-checked raster images to server-owned asset paths; never trust a client filename or path.
4. Update APIs/contracts/docs and write meaningful tests.
5. Run available build, test and demo commands. Do not claim unrun results.
6. Finish with: changed files, test results, demo instructions, known limitations, and next-phase handoff.

## Acceptance
Test that a 0.6 m tile repeats five times across a 3.0 m face; changing a floor's assignment and UVs leaves the adjacent wall unchanged; uploaded textures, roughness/metalness and UV settings survive save/reload and an API restart; and undo/redo restores edits. Run browser E2E against the existing local `roomforge` database. Never access `banhang`; do not add SQLite, Docker or Python.
