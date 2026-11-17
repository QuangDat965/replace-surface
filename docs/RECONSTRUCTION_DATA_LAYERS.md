# Reconstruction data layers

Five artifacts with distinct ownership, versions and mutation rules. They are never collapsed into one another.

| Layer | Persistence | Version field | Mutable by | Immutable after |
| --- | --- | --- | --- | --- |
| `SourceImage` | `ImageAssets` + local asset store | — (content-addressed) | Nobody | Upload succeeds |
| `AnnotationDocument` | `AnnotationDocuments` table | `contractVersion: 1` | The user, via `PUT` with `If-Match` | Applied into a candidate |
| `SceneAnalysisResult` | `ReconstructionJobs.AnalysisJson` | `contractVersion: 1` | Nobody | Job reaches `succeeded` |
| `ReconstructionCandidate` | `ReconstructionCandidates` table | `contractVersion: 1` | Nobody (append-only) | Inserted |
| `SceneDocument` | `Scenes` table | `schemaVersion: 1` | The user, via `PUT` with `If-Match` | Only replaces a previous revision, which is retained |

## SourceImage

Stored once, never modified. Holds server-generated storage ID, decoded width/height, content type and creation time. Client filenames and client-supplied paths are never accepted as storage identifiers. Deleting an image is refused while any job, annotation or candidate references it.

## AnnotationDocument

The user-owned editing surface: polygons in image pixels, semantic roles, reference spans and camera hints. Independent of any provider; it is what a human draws when no AI key exists.

```json
{
  "contractVersion": 1,
  "annotationId": "...",
  "projectId": "...",
  "sourceImageId": "...",
  "revision": 3,
  "imageWidth": 3024,
  "imageHeight": 4032,
  "regions": [
    { "id": "...", "semanticRole": "floor", "polygon": { "points": [{ "x": 120, "y": 2100 }] }, "provenance": "user_supplied", "confidence": null }
  ],
  "referenceSpans": [
    { "id": "...", "start": { "x": 120, "y": 2100 }, "end": { "x": 820, "y": 2100 }, "knownMeters": 2.4, "provenance": "user_supplied" }
  ],
  "cameraHint": { "assumedFocalLengthPixels": null, "assumedPrincipalPoint": null },
  "notes": []
}
```

Rules:

- `revision` uses optimistic concurrency; a stale `If-Match` returns `409` with the current revision.
- `confidence` is `null` for user-authored regions. User input is never assigned a fabricated confidence.
- A region may be deleted freely; annotations are not canonical scene data, so archive rules do not apply to them.
- No provider ID, vendor payload or model version appears here.

## SceneAnalysisResult

Provider output, normalized. Unchanged from v1 except that it becomes strictly immutable: once a job reaches `succeeded`, the JSON is byte-stable. Any correction becomes a new annotation or a candidate, never an in-place edit of an analysis result.

## ReconstructionCandidate

An append-only record that freezes one possible reconstruction:

```json
{
  "contractVersion": 1,
  "candidateId": "...",
  "projectId": "...",
  "annotationId": "...",
  "annotationRevision": 3,
  "analysisResultRef": null,
  "targetSceneId": "...",
  "baseSceneRevision": 7,
  "geometrySpec": { "...": "SceneGeometrySpec" },
  "topology": { "...": "RoomTopology" },
  "alignment": { "...": "AlignmentReport" },
  "diagnostics": ["scale_unknown"],
  "createdAt": "..."
}
```

Rules:

- Insert-only. Re-previewing the same annotation revision with different camera settings creates a **new** candidate rather than overwriting the old one, so uncertainty history survives.
- `baseSceneRevision` is captured at creation. Applying a candidate whose base revision is stale fails with `409` instead of silently merging.
- A candidate never mutates `SceneDocument`.

## SceneDocument

Canonical and editable. It changes only when a candidate is applied with explicit confirmation **and** the expected revision matches. Every prior revision remains in `SceneVersions`.

## Compatibility and migration

Existing artifacts must keep validating:

- `examples/sample-scene.json` is a golden fixture; `SceneDocumentValidator` must accept it unchanged.
- `examples/analysis-v1.json` is a golden fixture; `SceneAnalysisResultValidator` must accept it unchanged.
- New Scene JSON fields (`topology`, `dimensionSources`) are optional. A missing field is valid and means "not reconstructed", never "zero".
- `SceneAnalysisResult` readers must ignore unknown fields rather than fail, so a v1 result stays readable after additive changes.

Regression tests for all four points are required before any layer schema changes.

## Provenance rules that apply to every layer

- Every geometric value carries a `DimensionStatus` (`Unknown | Estimated | Measured`) and a provenance string.
- `Unknown` means the metre value is `null`. Zero is never used as a placeholder for unknown.
- A value derived from a user-supplied reference span is `Measured` only for that span; dimensions extrapolated from it are `Estimated` with a reduced confidence.
- No layer may record a metre value that cannot be traced to either a reference span or an explicit provider output.
