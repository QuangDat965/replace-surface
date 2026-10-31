# Architecture decisions (living ADR)

1. **Semantic Scene JSON is canonical.** GLB stores per-object meshes or exported render data, while JSON preserves editable surfaces, dimensions, materials and relations.
2. **Parametric architecture first.** Walls, floors, stairs and countertops should remain editable and independent of generated object GLBs.
3. **Web-first.** React Three Fiber owns rendering; ASP.NET Core owns persistence and orchestration. WPF embedding is a possible later integration.
4. **All AI inference is third-party HTTP API.** Explicitly no Python, FastAPI, GPU, local model weights or self-hosted AI workers.
5. **Capabilities vary by vendor.** Split segmentation, depth, layout and object generation interfaces; allow optional unified provider implementations, and never silently pretend missing capabilities exist.
6. **Persist durable jobs in PostgreSQL.** Start with .NET BackgroundService, migrate to separate .NET worker host or broker when required.
7. **Physical scale needs calibration.** No guarantee of metric precision from a monocular image even with vendor AI.
8. **Remote generation requires guardrails.** Budget, timeout, cost tracking, deduplication, provider rate limits, verified webhooks and safe downloaded assets.

## Open decisions
- Vendor(s) and their documented payloads: auth, submission, polling, callback, output schemas, licensing/retention, pricing.
- Whether any chosen provider supports segmentation, relative/metric depth or room layout.
- Which formats are returned (prefer GLB), and how unsupported outputs will be converted or rejected.
- Photo retention/privacy, user-provided scale anchors, model editing requirements.
