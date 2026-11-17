using RoomForge.Application.Reconstruction;

namespace RoomForge.Infrastructure.Reconstruction;

public sealed class MockReconstructionCapabilityRegistry : IReconstructionCapabilityRegistry
{
    private readonly string segmentationProviderId;
    private readonly bool segmentationAvailable;
    private readonly string segmentationDescription;
    private readonly string imageTo3DProviderId;
    private readonly bool imageTo3DAvailable;
    private readonly string imageTo3DDescription;

    public MockReconstructionCapabilityRegistry(string segmentationProviderId = "roomforge-mock", bool segmentationAvailable = true,
        string? segmentationDescription = null, string imageTo3DProviderId = "roomforge-mock", bool imageTo3DAvailable = false,
        string imageTo3DDescription = "No mesh generator configured; editable primitive fallback.")
    {
        this.segmentationProviderId = segmentationProviderId;
        this.segmentationAvailable = segmentationAvailable;
        this.segmentationDescription = segmentationDescription ?? "Deterministic image-space polygons.";
        this.imageTo3DProviderId = imageTo3DProviderId;
        this.imageTo3DAvailable = imageTo3DAvailable;
        this.imageTo3DDescription = imageTo3DDescription;
    }

    public IReadOnlyList<ReconstructionCapability> GetCapabilities() =>
    [
        new("scene-understanding", true, "roomforge-mock", "Deterministic synthetic scene observations."),
        new("object-segmentation", segmentationAvailable, segmentationProviderId, segmentationDescription),
        new("depth-estimation", true, "roomforge-mock", "Unknown metric scale; user measurement required."),
        new("image-to-3d", imageTo3DAvailable, imageTo3DProviderId, imageTo3DDescription),
    ];
}
