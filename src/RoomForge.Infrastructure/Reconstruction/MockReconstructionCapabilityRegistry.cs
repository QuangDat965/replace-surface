using RoomForge.Application.Reconstruction;

namespace RoomForge.Infrastructure.Reconstruction;

public sealed class MockReconstructionCapabilityRegistry : IReconstructionCapabilityRegistry
{
    public IReadOnlyList<ReconstructionCapability> GetCapabilities() =>
    [
        new("scene-understanding", true, "roomforge-mock", "Deterministic synthetic scene observations."),
        new("object-segmentation", true, "roomforge-mock", "Deterministic image-space polygons."),
        new("depth-estimation", true, "roomforge-mock", "Unknown metric scale; user measurement required."),
        new("image-to-3d", false, "roomforge-mock", "No mesh generator configured; editable primitive fallback."),
    ];
}
