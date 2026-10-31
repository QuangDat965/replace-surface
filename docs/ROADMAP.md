# Roadmap and acceptance gates (.NET orchestration + external AI APIs)

## Phase 01 — Vertical slice
Bootstrap React/R3F + ASP.NET Core 10 + PostgreSQL, versioned scene JSON, sample floor/wall/countertop and save/reload. No AI dependency. Exit: functional 3D room/editor demo with real backend persistence.

## Phase 02 — Advanced material editor; object editing follow-up
Material library, safe texture assets, physical PBR tile dimensions, per-surface UV transforms, undo/redo and revisioned persistence. The material-editor slice is complete when floor assignment/settings remain independent from adjacent surfaces and texture/PBR/UV data survive reload/restart. Object transforms, size editing and hide/show remain a separate Phase 02 follow-up.

## Phase 03 — Manual image annotation/calibration
Upload photo, polygon/mask editor, known-length reference anchors, manual room planes and parametric room shell. Exit: editable approximate scene from user-specified geometry without any AI key.

## Phase 04 — .NET AI job infrastructure
PostgreSQL job lifecycle, hosted BackgroundService, provider registry + typed HttpClient, normalized DTOs and mock external-provider adapter, status polling/failed/retry/cancel UI. Exit: mock job processed after API restart with provider clearly labeled mock, no Python service.

## Phase 05 — Real image understanding via API
Implement at least one REAL third-party segmentation/room analysis provider from documented API; optional depth adapter only when provider supports it. Download/validate masks, map image coordinates to annotations, show confidence and manual override. Exit: 3 sample photos have reviewable real observations; absent capabilities marked unsupported.

## Phase 06 — Per-object 3D API and export
Implement real remote image-to-3D provider. Mask/crop -> remote job -> validated result asset -> GLB or explicitly supported conversion -> attach to stable scene object -> manual adjust location/scale/visibility -> export GLB + JSON. Exit: generated object can be hidden/deleted and scene export opens in a standard glTF viewer.

## Later
Multiple images, multi-room, precise staircase modeling, construction cost estimator (only with calibrated dimensions), billing/cost tracking, worker scaling and provider routing.
