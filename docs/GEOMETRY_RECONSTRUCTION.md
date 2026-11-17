# Geometry reconstruction pipeline

This document is normative. It defines how RoomForge turns image annotations into editable three-dimensional geometry, and it forbids the shortcut of treating a two-dimensional image polygon as an accurate three-dimensional surface.

## The pipeline

```text
ImageAnnotationDocument        user-authored or provider-suggested polygons and reference spans, in image pixels
        |  PlaneConstraintBuilder
        v
PlaneSet                       floor / wall / ceiling hypotheses with normals and inlier support
        |  CameraCalibration
        v
CameraModel                    intrinsics K and pose R|t, plus a calibration confidence
        |  RoomTopologyBuilder
        v
RoomTopology                   nodes, edges, faces, junctions, object support, occluded boundaries
        |  ParametricGeometryBuilder
        v
SceneGeometrySpec              provider-neutral, framework-neutral geometry description
        |  SceneDocumentBuilder
        v
SceneDocument                  canonical editable scene (only after explicit confirmation)
```

Every stage is provider-independent and framework-independent. No stage may reference SharpGLTF, three.js, EF Core, or any vendor SDK. Only `SceneDocument` is canonical; everything before it is a reconstruction artifact.

## Stage contract

Every stage returns the same envelope so that "we do not have enough data" is a first-class outcome rather than an exception:

```csharp
public enum StageStatus { Ok, InsufficientData, Unsupported }
public sealed record StageResult<T>(
    T? Value,
    StageStatus Status,
    double Confidence,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> Assumptions);
```

Rules:

- `InsufficientData` is a normal, expected outcome. It is surfaced to the user with the diagnostics that explain what is missing.
- A stage may never promote `InsufficientData` to `Ok` by substituting a default value.
- `Confidence` is inference quality in `[0,1]`, never a claim about metric accuracy.

## Dimension provenance

Measurements carry their own provenance. There are exactly three states and they are not interchangeable:

```csharp
public enum DimensionStatus { Unknown, Estimated, Measured }
public sealed record Measurement(double? Meters, DimensionStatus Status, string Provenance, double? Confidence);
```

| Status | Meaning | How it is produced |
| --- | --- | --- |
| `Unknown` | No defensible value exists | Default. `Meters` must be `null`; zero is never used as a stand-in. |
| `Estimated` | A plausible value derived from incomplete constraints | Derived from annotation proportions plus a camera hypothesis. Must carry a confidence below `High`. |
| `Measured` | A user-supplied reference established scale | Requires a reference span with both a pixel length and a user-entered metre length. |

The pipeline **never invents real-world measurements when geometric constraints are insufficient.** If no reference span exists, the room is reconstructed only up to similarity (rotation and proportion are constrained; absolute scale is not), and the resulting geometry is labelled `Unknown` scale.


## Room topology

Two-dimensional annotation edges do not imply walls. Topology is modelled explicitly and is correctable by the user.

```csharp
public enum EdgeRole { UnknownCandidate, FloorWall, WallWall, WallCeiling, Opening, Occluded }
public enum TopologyNodeKind { Corner, Tjunction, Xjunction, Open }
public enum SupportKind { OnFloor, AgainstWall, Unknown }

public sealed record TopologyNode(Guid Id, double[] WorldMeters, TopologyNodeKind Kind);
public sealed record TopologyEdge(Guid Id, Guid StartNodeId, Guid EndNodeId, EdgeRole Role, string Provenance);
public sealed record TopologyFace(Guid Id, Guid[] EdgeIds, string SemanticRole);
public sealed record ObjectSupport(Guid ObjectId, Guid SurfaceId, SupportKind Kind);
public sealed record RoomTopology(
    Guid Id,
    TopologyNode[] Nodes,
    TopologyEdge[] Edges,
    TopologyFace[] Faces,
    ObjectSupport[] Supports,
    double Confidence,
    ReconstructionAssumptions Assumptions);
```

Binding rules:

1. `RoomTopologyBuilder` produces every edge with `EdgeRole.UnknownCandidate`. It never assigns `FloorWall` automatically. The previous behaviour of turning every floor polygon edge into a wall is retired.
2. Junctions are created only when two edges share a node within a `0.01 m` tolerance. A shared pixel coordinate is not sufficient.
3. `EdgeRole.Occluded` marks boundaries hidden behind furniture or cropped by the frame. Occluded edges do not generate geometry; they generate an explicit gap with an uncertainty note.
4. `EdgeRole.Opening` (doorway, window) generates geometry with a hole rather than a filled wall.
5. Manual correction is a first-class operation: `PATCH /api/topology/{topologyId}` accepts `{expectedRevision, edges:[{edgeId, role}]}` and returns the corrected topology plus diagnostics. Manual corrections always override inferred roles and are recorded with provenance `user_supplied`.

## Camera calibration

Calibration is attempted from the annotated plane set:

- Two or more mutually consistent vanishing-point pairs yield a `Calibrated` intrinsics estimate.
- A single plane plus an assumed principal point yields `Estimated` only, and the assumption is recorded verbatim in `Assumptions`.
- No usable constraints yield `InsufficientData`, and downstream stages receive no camera rather than a fabricated camera.

`CameraModel` records `Fx, Fy, Cx, Cy`, `PositionMeters`, `RotationQuaternion`, `CalibrationConfidence`, and whether intrinsics were `Calibrated`, `Estimated` or `Unknown`.

## Geometry specification

`SceneGeometrySpec` is the neutral hand-off to the scene builder and to export. It contains no material IDs (materials are assigned later in Scene JSON), no vendor types and no renderer types:

```csharp
public enum GeometryKind { Box, Plane, ExtrudedPolygon }
public sealed record GeometryVertex(double[] ObjectLocalMeters);
public sealed record GeometryFace(int[] VertexIndices, string SemanticRole, string FaceId);
public sealed record GeometryInstance(
    Guid StableId,
    string Name,
    string Category,
    GeometryKind Kind,
    double[] PositionMeters,
    double[] RotationQuaternion,
    Measurement[] Dimensions,
    IReadOnlyList<GeometryVertex> Vertices,
    IReadOnlyList<GeometryFace> Faces,
    bool Visible);
public sealed record SceneGeometrySpec(
    int ContractVersion,
    Guid SourceAnnotationId,
    Guid? CameraModelId,
    Guid TopologyId,
    IReadOnlyList<GeometryInstance> Instances,
    ReconstructionAssumptions Assumptions,
    IReadOnlyList<string> Diagnostics);
```

`GeometryKind.ExtrudedPolygon` carries arbitrary floor outlines, including concave ones. Triangulation is the exporter's responsibility and its limitations are documented in `docs/EXPORT_CONTRACT.md`.

## Photo alignment verification

Alignment answers one question only: does the reconstructed geometry project back onto the annotated image? It does **not** answer whether the real room has those real dimensions.

```csharp
public sealed record ProjectedEdge(Guid EdgeId, ImagePoint[] Points, bool FullyVisible, double MaxErrorPx);
public sealed record AlignmentReport(
    int ContractVersion,
    Guid SourceAnnotationId,
    Guid CameraModelId,
    IReadOnlyList<ProjectedEdge> Edges,
    double RmsErrorPixels,
    int VisibleSampleCount,
    int UnprojectableSampleCount,
    IReadOnlyList<string> UncertaintyNotes);
```

Behaviour:

- Edges whose endpoints fall behind the camera are counted in `UnprojectableSampleCount` and excluded from `RmsErrorPixels`; the report states explicitly that the error is partial.
- A low RMS error means the geometry is self-consistent with the annotations and camera hypothesis. It is **not** evidence of correct real-world size. The API response and the UI both carry that statement.
- Camera adjustment is available in the reconstruction workspace: the user may change intrinsics, camera position/orientation and supplied world-edge coordinates, then re-run alignment. The current report is displayed in the workspace and returned by the API; a durable camera-hypothesis history is not yet stored.

## What is explicitly out of scope

- Multi-image reconstruction and bundle adjustment.
- Metric accuracy without a user-supplied reference span.
- Automatic semantic understanding beyond what a configured provider returns (Phase 05).

## Relationship to the previous converter

`SceneReconstructionConverter` previously defaulted missing dimensions to `3.6 m` width and `2.7 m` wall height, and placed every wall at `z = -1.8`. That is measurement fabrication and it is removed. Its replacement is the `ParametricGeometryBuilder` stage, which derives geometry only from annotated constraints and reports `InsufficientData` otherwise.

6. Object-to-surface support is inferred as `Unknown` unless the object's footprint demonstrably overlaps a floor face within tolerance; it is never assumed to rest on the floor.
