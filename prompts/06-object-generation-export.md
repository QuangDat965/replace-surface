# Phase 06 — Third-party image-to-3D per-object generation + export

Read repo/docs including `docs/AI_PROVIDER_CONTRACT.md`. Implement `IImageTo3DProvider` against a documented external API from a selected provider. Do not invent credentials, endpoints or returned asset formats. No local GPU or Python.

Scope:
1. User chooses object region/crop/mask to generate; stores a stable scene object ID and submits a persisted job.
2. .NET worker submits/polls or processes verified provider webhook, enforces quotas/budget, validates external result, downloads to storage.
3. Import GLB where provided; if provider returns OBJ/other formats, explicitly implement and test a conversion path or show unsupported format, never silently label an OBJ a GLB.
4. Associate generated mesh with editable transform, approximate bounds, scale calibration and object visibility. Preserve separate parametric architecture and surface material editing.
5. Export whole scene GLB plus semantic scene JSON; verify re-import in a standard glTF viewer and compare resulting object bounds.
6. Mock/fallback to primitive/cached asset is always available when vendor API fails.

Test paid provider only with authorized credentials and opt-in; report real vs mock results precisely.
