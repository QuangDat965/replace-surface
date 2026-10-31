# Phase 05 — Real image analysis via third-party HTTP APIs

Read repo/docs including `docs/AI_PROVIDER_CONTRACT.md`. Integrate one ACTUAL documented third-party API for segmentation, room understanding or depth that supports the chosen capability. Ask for provider docs/credentials only when not available; do not invent endpoints or simulate real responses.

Deliverables:
- A typed C# `IHttpClientFactory` adapter with auth, throttling, timeout, cancellable poll/webhook behavior as documented.
- Mapping real vendor response to normalized mask/polygon/object labels with source metadata and confidence where available.
- Depth relative vs metric units only if vendor supports depth; room plane candidates with uncertainty.
- Segmentation review/override UI and parametric scene construction from accepted surfaces.
- Errors for unsupported capabilities and invalid response assets; fallback manual annotation remains usable.
- Evaluated results from at least 3 representative photos with explicit limitations.

No Python, no self-hosted model, no assumed SAM/Depth Anything deployment. Run tests and report which provider features were actually verified.
