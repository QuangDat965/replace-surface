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
