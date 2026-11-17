# External AI Provider API integration contract

## Scope
All inference is remotely hosted by third parties. The ASP.NET Core backend orchestrates calls. This file defines the internal **design contract**, not real vendor endpoints. Do NOT fabricate endpoints, payloads, authentication methods or pricing. Implement a real adapter only after reading the chosen provider's official documentation.

## Capabilities
Provider capabilities may be independently supported: `ImageAnalysis`, `Segmentation`, `DepthEstimate`, `RoomLayoutEstimate`, `ImageTo3D`, `GLBOutput`, `AsyncJobs`, `Webhooks`, `Cancellation`. One vendor can implement many; some may expose only a single image-to-3D task. UI and orchestration must adapt rather than assuming all outputs are available.

## Internal C# API concept
```csharp
public interface IImageTo3DProvider
{
    Task<AiSubmissionResult> SubmitAsync(ImageTo3DRequest request, CancellationToken ct);
    Task<AiJobSnapshot> GetStatusAsync(string externalJobId, CancellationToken ct);
    Task<GeneratedAsset> FetchResultAsync(string externalJobId, CancellationToken ct);
}

public interface ISegmentationProvider
{
    Task<SegmentationResult> SegmentAsync(SegmentationRequest request, CancellationToken ct);
}
```
These are illustrative contracts, not a command to hardcode provider APIs. An adapter may implement an internal async interface for segmentation as needed. Provider registry selects by required capability and configuration.

## Canonical result shapes
- `SegmentationResult`: image dimensions, regions (stable region ID, label, mask asset ref or polygon, confidence if returned), coordinate space, source provider + model version.
- `DepthResult`: depth artifact reference, size, relative/metric/unknown units, confidence and camera notes when available.
- `RoomLayoutResult`: image-space keypoints/planes, confidence, geometrical assumptions and coordinate system.
- `GeneratedAsset`: file reference, format (GLB/glTF/OBJ/etc), bytes, checksum, orientation/scale provenance, optional preview metadata. Prefer GLB; reject unsupported formats unless a tested conversion adapter exists.

## Long-running remote jobs
1. API saves `ProcessingJob` in PostgreSQL and returns HTTP 202.
2. .NET worker claims a lease, enforces provider quotas and submits with idempotency key where supported.
3. Save provider job ID; poll status OR handle verified callback, according to provider docs.
4. On success, download from a trusted provider host with redirects/size/type constraints. Persist binary in object storage and checksum. Store only private internal asset references in project/scene data.
5. Normalize to canonical types, validate all dimensions/IDs and update durable result metadata. UI can then review and accept candidate geometry.
6. Support retry with exponential backoff + jitter and a dead-letter/failure state. If submission may have succeeded but response was lost, reconcile first: avoid creating duplicate paid jobs.

## HTTP engineering rules
Use `IHttpClientFactory`, typed clients, explicit timeout and CancellationToken; consider `429 Retry-After`, `5xx`, transient network failures and rate limits. Validate schema carefully. Never leak secrets in logs, URLs or browser. Use environment variables/user-secrets/vault for credentials. Scope temporary image URLs; confirm third-party privacy, retention and licensing terms before sending user photos.

## Test matrix
- Mock submits and returns realistic normalized metadata; UI labels it mock.
- 200 result, async 202 pending, 429 retry, 5xx retry, timeout, malformed JSON, unsupported capability, bad signed callback, invalid output URL, oversized file, missing GLB, cancelled job, process restart/recovery.
- Provider adapter tests run without actual paid requests. Separate opt-in live smoke tests only when credentials/docs are available.

## RoomForge reconstruction interfaces (Phases 03 + 04)

The current Application boundary exposes independent `ISceneUnderstandingProvider`, `IObjectSegmentationProvider`, `IDepthEstimationProvider`, and `IImageTo3DProvider` capabilities plus `IReconstructionCapabilityRegistry`. Implementations return normalized internal DTOs; vendor DTOs must remain inside future Infrastructure adapters. Only deterministic mock implementations are registered in this foundation.

The persisted `SceneAnalysisResult` v1 carries image size, detected architectural surfaces and objects, image-space polygons, estimated dimensions/positions, confidence/provenance, and camera/scale assumptions. Mock provenance is labeled `mock`/`estimated`; absolute scale remains unknown unless a user supplies a reference length. Mock image-to-3D reports mesh generation unavailable and uses editable primitive fallback.

The Infrastructure project contains a fixture-tested, typed fal SAM 3 queue transport client and an opt-in `IObjectSegmentationProvider`. Enable it with `Reconstruction:SegmentationProvider=fal-sam3` and set `FAL_KEY` in server configuration/User Secrets or the environment. The provider persists the seven-state submission transition before POST, stores the external request ID after acceptance, resumes polling from the stored ID after restart, refreshes the processing lease while submitting/polling, and marks ambiguous submission failures `outcome_unknown` so retry stays blocked. Before each POST it makes a PostgreSQL reservation from the shared cross-provider ledger; concurrent reservations are transaction-serialized and the hard aggregate cap cannot exceed the approved $20 USD. The first wired capability asks SAM 3 for one floor region, downloads the documented PNG mask from the trusted fal media host, validates its dimensions and extracts a bounded row-envelope polygon inside the normalized provider box. It preserves provider score and labels the polygon source `mask-row-envelope`; an empty/unusable mask falls back to the provider box and records `provider-box-fallback`. This envelope is an approximation of mask extents, not a pixel-accurate contour. The user can correct the candidate in the existing analysis review before preview/apply. Live calls remain opt-in and require the current price and privacy/model/account retention conditions to be reviewed. The provider is not enabled by default.

### Phase 06 provider transport foundation

`FalHunyuan3DQueueClient` uses the documented `fal-ai/hunyuan3d-v3/image-to-3d` queue endpoint, server-side `FAL_KEY`, data-URI image input, safety checker enabled, `generate_type=Geometry`, and omits optional PBR/custom face-count settings. It parses queue status and the `model_glb` result, permits only HTTPS `fal.media` asset URLs, rejects redirects, caps output at 80 MB, checks GLB v2 container length/magic, and parses the GLB before returning bytes. This client now powers a separate durable per-object mesh job workflow: the user confirms an image-pixel crop, the worker stores ObjectId and crop digest, reserves $0.50 from the shared hard $20 USD spend ledger, persists submission transitions/provider job ID and resumes polling after restart, validates and stores GLB, then waits for explicit revision-checked apply. It does not replace the semantic object transform or architecture surface assignments. Provider use remains disabled unless configured with `Reconstruction:ImageTo3DProvider=fal-hunyuan3d`, `Reconstruction:FalHunyuanDataTransferApproved=true`, and server-side `FAL_KEY`. Public pricing (rechecked 2026-11-01) lists $0.225 for Geometry and +$0.15 for custom face count, PBR or multi-view; account pricing and privacy/retention still require confirmation. No live request has been made. PostgreSQL fixture tests cover durable job completion, output validation/storage and shared spend reservation without external calls. Remaining gates include adjust/visibility/save/reload E2E, one permitted real mesh and confirmation of account/model privacy/retention.
