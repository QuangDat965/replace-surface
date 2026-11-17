# Architecture decisions (living ADR)

1. **Semantic Scene JSON is canonical.** GLB stores per-object meshes or exported render data, while JSON preserves editable surfaces, dimensions, materials and relations.
2. **Parametric architecture first.** Walls, floors, stairs and countertops should remain editable and independent of generated object GLBs.
3. **Web-first.** React Three Fiber owns rendering; ASP.NET Core owns persistence and orchestration. WPF embedding is a possible later integration.
4. **All AI inference is third-party HTTP API.** Explicitly no Python, FastAPI, GPU, local model weights or self-hosted AI workers.
5. **Capabilities vary by vendor.** Split segmentation, depth, layout and object generation interfaces; allow optional unified provider implementations, and never silently pretend missing capabilities exist.
6. **Persist durable jobs in PostgreSQL.** Start with .NET BackgroundService, migrate to separate .NET worker host or broker when required.
7. **Physical scale needs calibration.** No guarantee of metric precision from a monocular image even with vendor AI.
8. **Remote generation requires guardrails.** Budget, timeout, cost tracking, deduplication, provider rate limits, verified webhooks and safe downloaded assets.
9. **Geometry reconstruction is a layered pipeline, not a polygon-to-3D shortcut.** Image annotations pass through plane constraints, camera calibration, room topology and parametric geometry before reaching Scene JSON. A two-dimensional polygon does not define a three-dimensional surface. See `docs/GEOMETRY_RECONSTRUCTION.md`.
10. **Room topology is first-class and manually correctable.** Shared edges, junctions, floor/wall adjacency, wall/wall junctions, object support and occluded boundaries are modelled explicitly. Every inferred edge starts as `UnknownCandidate`; the builder never promotes a floor polygon edge to a wall. Manual corrections override inference and are recorded as `user_supplied`.
11. **Five coordinate spaces with tested contracts.** Image pixels, camera, world metres, object-local and texture UV each have one implementation and one round-trip test. No UI framework participates in the maths. Image Y-down is flipped in exactly one function. See `docs/COORDINATE_SYSTEMS.md`.
12. **Export engine is independent of Domain and Scene JSON.** `Application` converts Scene JSON into a neutral `SceneExportModel`; only `Infrastructure/Export/` references SharpGLTF (MIT, 1.0.7). Swapping the exporter library must touch one file. See `docs/EXPORT_CONTRACT.md`.
13. **Five reconstruction data layers stay separate.** `SourceImage`, `AnnotationDocument`, `SceneAnalysisResult`, `ReconstructionCandidate` and `SceneDocument` have distinct ownership, versions and mutation rules. `SceneDocument` changes only after explicit confirmation with a matching revision. See `docs/RECONSTRUCTION_DATA_LAYERS.md`.
14. **Durable job submission has seven explicit states.** Submission-not-attempted, submission-in-progress, submission-outcome-unknown, remote-accepted, remote-running, remote-completed and terminal-failure are distinct. An ambiguous network failure never triggers a second paid submission unless the provider documents idempotency support; the job is reconciled first.
15. **Photo alignment is not metric accuracy.** Reprojecting reconstructed geometry onto the source image verifies self-consistency with the annotations and camera hypothesis. A low reprojection error is never presented as evidence of correct real-world dimensions.
16. **Never invent measurements.** When geometric constraints are insufficient, dimensions are `Unknown` with a `null` metre value, and the geometry is reconstructed only up to similarity. `estimated` and `measured` are separate statuses with distinct provenance rules.

## Open decisions
- Vendor(s) and their documented payloads: auth, submission, polling, callback, output schemas, licensing/retention, pricing.
- Whether any chosen provider supports segmentation, relative/metric depth or room layout.
- Which formats are returned (prefer GLB), and how unsupported outputs will be converted or rejected.
- Photo retention/privacy, user-provided scale anchors, model editing requirements.
