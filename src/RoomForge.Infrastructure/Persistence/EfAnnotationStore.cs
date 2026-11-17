using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RoomForge.Application.Annotations;
using RoomForge.Domain.Annotations;

namespace RoomForge.Infrastructure.Persistence;

public sealed class EfAnnotationStore(RoomForgeDbContext db) : IAnnotationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AnnotationDocument> CreateAsync(AnnotationDocument document, CancellationToken cancellationToken)
    {
        var errors = AnnotationDocumentValidator.Validate(document);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        if (!await db.Projects.AnyAsync(project => project.Id == document.ProjectId, cancellationToken) ||
            !await db.ImageAssets.AnyAsync(image => image.Id == document.SourceImageId && image.ProjectId == document.ProjectId, cancellationToken))
            throw new InvalidOperationException("The source image does not belong to the project.");

        var now = DateTimeOffset.UtcNow;
        db.AnnotationDocuments.Add(new AnnotationDocumentRecord
        {
            AnnotationId = document.AnnotationId,
            ProjectId = document.ProjectId,
            SourceImageId = document.SourceImageId,
            Revision = document.Revision,
            Document = JsonSerializer.Serialize(document, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return document;
    }

    public async Task<AnnotationDocument?> GetAsync(Guid projectId, Guid annotationId, CancellationToken cancellationToken)
    {
        var json = await db.AnnotationDocuments.AsNoTracking()
            .Where(record => record.ProjectId == projectId && record.AnnotationId == annotationId)
            .Select(record => record.Document)
            .SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<AnnotationDocument>(json, JsonOptions);
    }

    public async Task<UpdateAnnotationResult> UpdateAsync(AnnotationDocument document, int expectedRevision, CancellationToken cancellationToken)
    {
        var errors = AnnotationDocumentValidator.Validate(document);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        var now = DateTimeOffset.UtcNow;
        var count = await db.AnnotationDocuments
            .Where(record => record.AnnotationId == document.AnnotationId && record.ProjectId == document.ProjectId && record.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(record => record.Revision, document.Revision)
                .SetProperty(record => record.Document, JsonSerializer.Serialize(document, JsonOptions))
                .SetProperty(record => record.UpdatedAt, now), cancellationToken);
        if (count == 1) return new UpdateAnnotationResult(UpdateAnnotationStatus.Saved, document);

        var current = await GetAsync(document.ProjectId, document.AnnotationId, cancellationToken);
        return current is null
            ? new UpdateAnnotationResult(UpdateAnnotationStatus.NotFound, null)
            : new UpdateAnnotationResult(UpdateAnnotationStatus.RevisionConflict, current);
    }
}
