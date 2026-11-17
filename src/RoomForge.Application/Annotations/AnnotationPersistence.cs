using RoomForge.Domain.Annotations;
using RoomForge.Application.Reconstruction;

namespace RoomForge.Application.Annotations;

public sealed record UpdateAnnotationDocument(
    IReadOnlyList<AnnotationRegion> Regions,
    IReadOnlyList<AnnotationReferenceSpan> ReferenceSpans,
    AnnotationCameraHint CameraHint,
    IReadOnlyList<string> Notes);

public enum UpdateAnnotationStatus { Saved, NotFound, RevisionConflict }

public sealed record UpdateAnnotationResult(UpdateAnnotationStatus Status, AnnotationDocument? Document);

public interface IAnnotationStore
{
    Task<AnnotationDocument> CreateAsync(AnnotationDocument document, CancellationToken cancellationToken);
    Task<AnnotationDocument?> GetAsync(Guid projectId, Guid annotationId, CancellationToken cancellationToken);
    Task<UpdateAnnotationResult> UpdateAsync(AnnotationDocument document, int expectedRevision, CancellationToken cancellationToken);
}

public sealed class AnnotationPersistence(IAnnotationStore store, IReconstructionImageStore images)
{
    public async Task<AnnotationDocument?> CreateAsync(Guid projectId, Guid sourceImageId, CancellationToken cancellationToken)
    {
        var image = await images.GetAsync(sourceImageId, cancellationToken);
        if (image is null || image.ProjectId != projectId) return null;
        var document = new AnnotationDocument(1, Guid.NewGuid(), projectId, sourceImageId, 1, image.Width, image.Height,
            [], [], new AnnotationCameraHint(null, null), []);
        return await store.CreateAsync(document, cancellationToken);
    }

    public Task<AnnotationDocument?> GetAsync(Guid projectId, Guid annotationId, CancellationToken cancellationToken) =>
        store.GetAsync(projectId, annotationId, cancellationToken);

    public async Task<UpdateAnnotationResult> UpdateAsync(Guid projectId, Guid annotationId, int expectedRevision,
        UpdateAnnotationDocument update, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(projectId, annotationId, cancellationToken);
        if (current is null) return new UpdateAnnotationResult(UpdateAnnotationStatus.NotFound, null);
        if (current.Revision != expectedRevision)
            return new UpdateAnnotationResult(UpdateAnnotationStatus.RevisionConflict, current);

        var replacement = current with
        {
            Revision = checked(current.Revision + 1),
            Regions = update.Regions,
            ReferenceSpans = update.ReferenceSpans,
            CameraHint = update.CameraHint,
            Notes = update.Notes,
        };
        var errors = AnnotationDocumentValidator.Validate(replacement);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        return await store.UpdateAsync(replacement, expectedRevision, cancellationToken);
    }
}
