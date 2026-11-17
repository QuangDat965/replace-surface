using System.Numerics;
using System.Text.Json.Serialization;
using RoomForge.Domain.Annotations;
using RoomForge.Domain.Reconstruction;

namespace RoomForge.Domain.Geometry;

[JsonConverter(typeof(JsonStringEnumConverter<StageStatus>))]
public enum StageStatus
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("insufficient_data")] InsufficientData,
    [JsonStringEnumMemberName("unsupported")] Unsupported,
}
[JsonConverter(typeof(JsonStringEnumConverter<DimensionStatus>))]
public enum DimensionStatus
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("estimated")] Estimated,
    [JsonStringEnumMemberName("measured")] Measured,
}
[JsonConverter(typeof(JsonStringEnumConverter<TopologyEdgeRole>))]
public enum TopologyEdgeRole
{
    [JsonStringEnumMemberName("unknown_candidate")] UnknownCandidate,
    [JsonStringEnumMemberName("floor_wall")] FloorWall,
    [JsonStringEnumMemberName("wall_wall")] WallWall,
    [JsonStringEnumMemberName("wall_ceiling")] WallCeiling,
    [JsonStringEnumMemberName("opening")] Opening,
    [JsonStringEnumMemberName("occluded")] Occluded,
}
[JsonConverter(typeof(JsonStringEnumConverter<TopologyNodeKind>))]
public enum TopologyNodeKind { Corner, TJunction, XJunction, Open }
[JsonConverter(typeof(JsonStringEnumConverter<SupportKind>))]
public enum SupportKind { OnFloor, AgainstWall, Unknown }
[JsonConverter(typeof(JsonStringEnumConverter<GeometryKind>))]
public enum GeometryKind
{
    [JsonStringEnumMemberName("box")] Box,
    [JsonStringEnumMemberName("plane")] Plane,
    [JsonStringEnumMemberName("extruded_polygon")] ExtrudedPolygon,
}

public sealed record StageResult<T>(T? Value, StageStatus Status, double Confidence, IReadOnlyList<string> Diagnostics, IReadOnlyList<string> Assumptions);
public sealed record PlaneCandidate(Guid RegionId, string SemanticRole, ImagePolygon Polygon, string Provenance);
public sealed record PlaneSet(Guid AnnotationId, IReadOnlyList<PlaneCandidate> Candidates, IReadOnlyList<string> Diagnostics);
public sealed record Measurement(double? Meters, DimensionStatus Status, string Provenance, double? Confidence);
public sealed record TopologyNode(Guid Id, ImagePoint ImagePoint, TopologyNodeKind Kind);
public sealed record TopologyEdge(Guid Id, Guid RegionId, Guid StartNodeId, Guid EndNodeId, ImagePoint Start, ImagePoint End,
    TopologyEdgeRole Role, string Provenance);
public sealed record TopologyFace(Guid Id, Guid RegionId, string SemanticRole, IReadOnlyList<Guid> EdgeIds);
public sealed record ObjectSupport(Guid ObjectId, Guid? SurfaceId, SupportKind Kind);
public sealed record RoomTopology(Guid Id, Guid AnnotationId, int AnnotationRevision, int Revision,
    IReadOnlyList<TopologyNode> Nodes, IReadOnlyList<TopologyEdge> Edges, IReadOnlyList<TopologyFace> Faces,
    IReadOnlyList<ObjectSupport> Supports, double Confidence, IReadOnlyList<string> Diagnostics);

public sealed record GeometryVertex(double[] ObjectLocalMeters);
public sealed record GeometryFace(int[] VertexIndices, string SemanticRole, string FaceId);
public sealed record GeometryInstance(Guid StableId, string Name, string Category, GeometryKind Kind,
    double[] PositionMeters, double[] RotationQuaternion, Measurement[] Dimensions,
    IReadOnlyList<GeometryVertex> Vertices, IReadOnlyList<GeometryFace> Faces, bool Visible,
    IReadOnlyDictionary<string, Measurement>? DimensionSources = null);
public sealed record SceneGeometrySpec(int ContractVersion, Guid SourceAnnotationId, Guid? CameraModelId,
    Guid TopologyId, IReadOnlyList<GeometryInstance> Instances, ReconstructionAssumptions Assumptions,
    IReadOnlyList<string> Diagnostics);

public static class PlaneConstraintBuilder
{
    public static StageResult<PlaneSet> Build(AnnotationDocument annotation)
    {
        var errors = AnnotationDocumentValidator.Validate(annotation);
        if (errors.Count > 0) return new StageResult<PlaneSet>(null, StageStatus.Unsupported, 0, errors, []);
        var candidates = annotation.Regions.Select(region => new PlaneCandidate(region.Id, region.SemanticRole, region.Polygon, region.Provenance)).ToArray();
        if (candidates.Length == 0)
            return new StageResult<PlaneSet>(null, StageStatus.InsufficientData, 0,
                ["Draw at least one semantic region before estimating planes."], []);
        var diagnostics = candidates.Where(candidate => candidate.SemanticRole is "floor" or "wall" or "ceiling")
            .Select(candidate => $"Region {candidate.RegionId} is a 2D {candidate.SemanticRole} hypothesis; its 3D plane normal is unknown.").ToArray();
        return new StageResult<PlaneSet>(new PlaneSet(annotation.AnnotationId, candidates, diagnostics), StageStatus.Ok, .35,
            diagnostics, ["Image polygons alone do not determine 3D plane orientation or room depth."]);
    }
}

public static class RoomTopologyBuilder
{
    public static StageResult<RoomTopology> Build(AnnotationDocument annotation)
    {
        var planeResult = PlaneConstraintBuilder.Build(annotation);
        if (planeResult.Status != StageStatus.Ok || planeResult.Value is null)
            return new StageResult<RoomTopology>(null, planeResult.Status, planeResult.Confidence, planeResult.Diagnostics, planeResult.Assumptions);

        var nodes = new List<TopologyNode>();
        var edges = new List<TopologyEdge>();
        var faces = new List<TopologyFace>();
        foreach (var region in annotation.Regions)
        {
            var regionNodeIds = region.Polygon.Points.Select((point, index) =>
            {
                var id = StableId(region.Id, $"node-{index}");
                nodes.Add(new TopologyNode(id, point, TopologyNodeKind.Corner));
                return id;
            }).ToArray();
            var regionEdges = new List<Guid>();
            for (var index = 0; index < region.Polygon.Points.Count; index++)
            {
                var edgeId = StableId(region.Id, $"edge-{index}");
                var next = (index + 1) % region.Polygon.Points.Count;
                edges.Add(new TopologyEdge(edgeId, region.Id, regionNodeIds[index], regionNodeIds[next],
                    region.Polygon.Points[index], region.Polygon.Points[next], TopologyEdgeRole.UnknownCandidate, "inferred_candidate"));
                regionEdges.Add(edgeId);
            }
            faces.Add(new TopologyFace(StableId(region.Id, "face"), region.Id, region.SemanticRole, regionEdges));
        }

        var topology = new RoomTopology(Guid.NewGuid(), annotation.AnnotationId, annotation.Revision, 1, nodes, edges, faces, [],
            .25, ["All edge roles are unknown candidates until manually corrected."]);
        return new StageResult<RoomTopology>(topology, StageStatus.Ok, topology.Confidence, topology.Diagnostics,
            ["Nodes are image-space annotation corners; no 3D junctions are inferred from shared pixels."]);
    }

    public static IReadOnlyList<string> ValidateCorrections(RoomTopology topology, IReadOnlyList<(Guid EdgeId, TopologyEdgeRole Role)> corrections)
    {
        var errors = new List<string>();
        var ids = new HashSet<Guid>();
        var knownEdges = topology.Edges.Select(edge => edge.Id).ToHashSet();
        foreach (var correction in corrections)
        {
            if (!ids.Add(correction.EdgeId)) errors.Add("An edge may be corrected only once per request.");
            if (!knownEdges.Contains(correction.EdgeId)) errors.Add($"Topology edge {correction.EdgeId} does not exist.");
            if (!Enum.IsDefined(correction.Role)) errors.Add("Unsupported topology edge role.");
        }
        return errors;
    }

    private static Guid StableId(Guid source, string suffix)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(source.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(suffix)).ToArray());
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public static class CameraCalibrationBuilder
{
    public static StageResult<CameraModel> Build(AnnotationDocument annotation, CameraModel? suppliedModel = null)
    {
        if (suppliedModel is not null)
        {
            try
            {
                if (!float.IsFinite(suppliedModel.PositionMeters.X) || !float.IsFinite(suppliedModel.PositionMeters.Y) || !float.IsFinite(suppliedModel.PositionMeters.Z))
                    throw new ArgumentException("Camera world position must be finite meters.");
                _ = CoordinateTransforms.ImageToCamera(new ImagePoint(suppliedModel.Cx, suppliedModel.Cy), suppliedModel);
                _ = CoordinateTransforms.WorldToCamera(suppliedModel.PositionMeters, suppliedModel);
                if (suppliedModel.Id == Guid.Empty || string.IsNullOrWhiteSpace(suppliedModel.IntrinsicsStatus) || string.IsNullOrWhiteSpace(suppliedModel.Provenance))
                    throw new ArgumentException("Camera identity and provenance are required.");
                return new StageResult<CameraModel>(suppliedModel, StageStatus.Ok, .5,
                    ["Camera intrinsics and pose were supplied as a hypothesis; reprojection is not proof of metric accuracy."],
                    ["Camera model provenance: " + suppliedModel.Provenance]);
            }
            catch (ArgumentException exception)
            {
                return new StageResult<CameraModel>(null, StageStatus.Unsupported, 0, [exception.Message], []);
            }
        }

        if (annotation.CameraHint.FocalLengthPixels is null || annotation.CameraHint.PrincipalPoint is null)
            return new StageResult<CameraModel>(null, StageStatus.InsufficientData, 0,
                ["Camera focal length and principal point are unknown. Provide a camera hypothesis to verify photo alignment."], []);
        return new StageResult<CameraModel>(null, StageStatus.InsufficientData, 0,
            ["Intrinsics are present, but camera world position and orientation are missing."],
            ["No camera pose is inferred from a single 2D polygon."]);
    }
}

public static class ParametricGeometryBuilder
{
    public static StageResult<SceneGeometrySpec> Build(AnnotationDocument annotation, RoomTopology topology,
        Guid floorRegionId, Guid calibrationSpanId, Guid? cameraModelId = null)
    {
        if (topology.AnnotationId != annotation.AnnotationId || topology.AnnotationRevision != annotation.Revision)
            return new StageResult<SceneGeometrySpec>(null, StageStatus.Unsupported, 0,
                ["Topology was built from a different annotation revision. Rebuild topology before geometry."], []);
        var floor = annotation.Regions.SingleOrDefault(region => region.Id == floorRegionId && region.SemanticRole == "floor");
        if (floor is null) return new StageResult<SceneGeometrySpec>(null, StageStatus.InsufficientData, 0,
            ["Annotate and select a floor polygon before building a room shell."], []);
        var span = annotation.ReferenceSpans.SingleOrDefault(reference => reference.Id == calibrationSpanId);
        if (span is null) return new StageResult<SceneGeometrySpec>(null, StageStatus.InsufficientData, 0,
            ["Add and select a measured reference span before assigning scale."], []);

        var pixelLength = Math.Sqrt(Math.Pow(span.End.X - span.Start.X, 2) + Math.Pow(span.End.Y - span.Start.Y, 2));
        if (!double.IsFinite(pixelLength) || pixelLength < 1 || !double.IsFinite(span.KnownMeters) || span.KnownMeters <= 0)
            return new StageResult<SceneGeometrySpec>(null, StageStatus.Unsupported, 0, ["The selected calibration span is invalid."], []);
        var metersPerPixel = span.KnownMeters / pixelLength;
        var points = floor.Polygon.Points;
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        var widthMeters = (maxX - minX) * metersPerPixel;
        var depthMeters = (maxY - minY) * metersPerPixel;
        if (widthMeters <= 0 || depthMeters <= 0)
            return new StageResult<SceneGeometrySpec>(null, StageStatus.InsufficientData, 0, ["The floor outline has no usable planar extent."], []);

        var centerX = (minX + maxX) / 2;
        var centerZ = (minY + maxY) / 2;
        var floorId = StableId(floor.Id, "geometry-floor");
        var instances = new List<GeometryInstance>
        {
            new(floorId, "Reconstructed floor", "architecture", GeometryKind.Box,
                [0, 0, 0], [0, 0, 0, 1],
                [new(widthMeters, DimensionStatus.Estimated, "Image-plane proportion scaled by one user reference span.", .35),
                 new(null, DimensionStatus.Unknown, "No floor thickness measurement.", null),
                 new(depthMeters, DimensionStatus.Estimated, "Image-plane proportion scaled by one user reference span.", .2)],
                [], [], true,
                new Dictionary<string, Measurement> { ["width"] = new(span.KnownMeters, DimensionStatus.Measured, "user_supplied reference span", 1) }),
        };
        var diagnostics = new List<string>
        {
            "The floor is a rectangular proxy from the annotation bounds; the original polygon remains available for review.",
            "Only the selected reference span is measured. The other axis is estimated because a single photo has perspective distortion.",
        };

        var wallEdges = topology.Edges.Where(edge => edge.RegionId == floor.Id && edge.Role == TopologyEdgeRole.FloorWall).ToArray();
        var verticalSpan = annotation.ReferenceSpans.Where(reference => Math.Abs(reference.End.Y - reference.Start.Y) > Math.Abs(reference.End.X - reference.Start.X))
            .OrderByDescending(reference => Math.Abs(reference.End.Y - reference.Start.Y)).FirstOrDefault();
        if (wallEdges.Length > 0 && verticalSpan is null)
            diagnostics.Add("Manually classified floor-wall edges were omitted because no vertical wall-height reference span exists.");
        foreach (var edge in wallEdges)
        {
            if (verticalSpan is null) break;
            var dx = edge.End.X - edge.Start.X;
            var dz = edge.End.Y - edge.Start.Y;
            var length = Math.Sqrt(dx * dx + dz * dz) * metersPerPixel;
            if (length <= 0) continue;
            var midpointX = (edge.Start.X + edge.End.X) / 2;
            var midpointZ = (edge.Start.Y + edge.End.Y) / 2;
            var yaw = Math.Atan2(dz, dx);
            instances.Add(new GeometryInstance(StableId(edge.Id, "geometry-wall"), "Reconstructed wall", "architecture", GeometryKind.Box,
                [(midpointX - centerX) * metersPerPixel, verticalSpan.KnownMeters / 2, (midpointZ - centerZ) * metersPerPixel],
                [0, Math.Sin(yaw / 2), 0, Math.Cos(yaw / 2)],
                [new(length, DimensionStatus.Estimated, "Image-plane edge length scaled by one reference span.", .3),
                 new(verticalSpan.KnownMeters, DimensionStatus.Measured, "user_supplied vertical reference span", 1),
                 new(null, DimensionStatus.Unknown, "No wall thickness measurement.", null)], [], [], true));
        }

        var assumptions = new ReconstructionAssumptions("camera-pose-unknown", "user_supplied", metersPerPixel,
            ["Camera perspective was not rectified.", "Alignment must not be interpreted as metric accuracy."], pixelLength, span.KnownMeters);
        var spec = new SceneGeometrySpec(1, annotation.AnnotationId, cameraModelId, topology.Id, instances, assumptions, diagnostics);
        return new StageResult<SceneGeometrySpec>(spec, StageStatus.Ok, .35, diagnostics,
            ["Floor coordinates use an image-plane approximation anchored at the floor polygon bounds."]);
    }

    private static Guid StableId(Guid source, string suffix)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(source.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(suffix)).ToArray());
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public sealed record ProjectedEdge(Guid EdgeId, ImagePoint[] Points, bool FullyVisible, double MaxErrorPx);
public sealed record AlignmentReport(int ContractVersion, Guid SourceAnnotationId, Guid CameraModelId,
    IReadOnlyList<ProjectedEdge> Edges, double RmsErrorPixels, int VisibleSampleCount, int UnprojectableSampleCount,
    IReadOnlyList<string> UncertaintyNotes);

public static class PhotoAlignmentVerifier
{
    public static StageResult<AlignmentReport> Verify(AnnotationDocument annotation, RoomTopology topology,
        CameraModel? camera, IReadOnlyDictionary<Guid, (Vector3 Start, Vector3 End)> worldEdges)
    {
        if (camera is null) return new StageResult<AlignmentReport>(null, StageStatus.InsufficientData, 0,
            ["Camera position and orientation are required for photo alignment."], []);
        var projected = new List<ProjectedEdge>();
        var squaredErrors = new List<double>();
        var unprojectable = 0;
        foreach (var edge in topology.Edges)
        {
            if (!worldEdges.TryGetValue(edge.Id, out var world)) continue;
            var start = CoordinateTransforms.ProjectToImage(world.Start, camera, annotation.ImageWidth, annotation.ImageHeight);
            var end = CoordinateTransforms.ProjectToImage(world.End, camera, annotation.ImageWidth, annotation.ImageHeight);
            if (start.Point is null || end.Point is null)
            {
                unprojectable++;
                projected.Add(new ProjectedEdge(edge.Id, [], false, double.NaN));
                continue;
            }
            var startError = Distance(start.Point, edge.Start);
            var endError = Distance(end.Point, edge.End);
            var maxError = Math.Max(startError, endError);
            squaredErrors.Add(startError * startError);
            squaredErrors.Add(endError * endError);
            projected.Add(new ProjectedEdge(edge.Id, [start.Point, end.Point], true, maxError));
        }
        if (projected.Count == 0) return new StageResult<AlignmentReport>(null, StageStatus.InsufficientData, 0,
            ["No topology edges were supplied for alignment verification."], []);
        var rms = squaredErrors.Count == 0 ? double.NaN : Math.Sqrt(squaredErrors.Average());
        var report = new AlignmentReport(1, annotation.AnnotationId, camera.Id, projected, rms, squaredErrors.Count, unprojectable,
            ["Reprojection error measures consistency with the annotations and camera hypothesis, not real-world accuracy.",
             unprojectable > 0 ? "The reported RMS excludes edges behind or outside the camera view." : "All projected edge endpoints were visible."]);
        return new StageResult<AlignmentReport>(report, StageStatus.Ok, .25, report.UncertaintyNotes, ["Alignment is not a metric accuracy guarantee."]);
    }

    private static double Distance(ImagePoint left, ImagePoint right) => Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));
}
