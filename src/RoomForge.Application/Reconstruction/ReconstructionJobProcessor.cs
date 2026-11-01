using Microsoft.Extensions.Logging;
using RoomForge.Domain.Reconstruction;

namespace RoomForge.Application.Reconstruction;

public sealed class ReconstructionJobProcessor(
    IReconstructionStore jobs,
    IReconstructionImageStore images,
    ReconstructionAnalysisService analysis,
    ILogger<ReconstructionJobProcessor> logger)
{
    public async Task ProcessAsync(ReconstructionJobWorkItem work, CancellationToken cancellationToken)
    {
        try
        {
            var image = await images.GetAsync(work.ImageId, cancellationToken);
            if (image is null)
            {
                await jobs.FailAsync(work.Id, "source_image_missing", "The source image is no longer available.", false, cancellationToken);
                return;
            }
            await jobs.SetProgressAsync(work.Id, "scene_understanding", 15, cancellationToken);
            var result = await analysis.AnalyzeAsync(image, cancellationToken);
            var errors = SceneAnalysisResultValidator.Validate(result);
            if (errors.Count > 0)
            {
                logger.LogWarning("Mock analysis {AnalysisId} failed contract validation with {ErrorCount} errors.", result.AnalysisId, errors.Count);
                await jobs.FailAsync(work.Id, "analysis_contract_invalid", "The analysis result did not satisfy the supported contract.", false, cancellationToken);
                return;
            }
            await jobs.SetProgressAsync(work.Id, "normalizing_result", 85, cancellationToken);
            await jobs.CompleteAsync(work.Id, ReconstructionAnalysisService.Serialize(result), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Reconstruction job {JobId} failed in attempt {AttemptCount}.", work.Id, work.AttemptCount);
            await jobs.FailAsync(work.Id, "reconstruction_failed", "Reconstruction failed. Retry the job or upload the image again.", false, CancellationToken.None);
        }
    }
}
