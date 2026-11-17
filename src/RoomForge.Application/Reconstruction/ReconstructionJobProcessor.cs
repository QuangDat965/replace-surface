using Microsoft.Extensions.Logging;
using RoomForge.Domain.Reconstruction;

namespace RoomForge.Application.Reconstruction;

public sealed class ReconstructionJobProcessor(
    IReconstructionStore jobs,
    IReconstructionImageStore images,
    ReconstructionAnalysisService analysis,
    ILogger<ReconstructionJobProcessor> logger)
{
    /// <summary>
    /// Classifies a failure so the store can decide between a bounded retry and a terminal failure.
    /// Transient causes are retried with backoff; contract or data faults are permanent.
    /// </summary>
    public static bool IsRetryable(Exception exception) => exception switch
    {
        // The job was cancelled by the host shutting down, not by a data problem.
        OperationCanceledException => true,
        // Network, DNS, TLS and provider 5xx/429 surface here in the mock-free path.
        HttpRequestException => true,
        TimeoutException => true,
        IOException => true,
        // A malformed or contract-violating result will not become valid on retry.
        InvalidDataException => false,
        _ => false,
    };

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
            var result = await analysis.AnalyzeAsync(image, work.Id, cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leave the job in processing so the expired lease makes it recoverable after restart,
            // rather than burning a retry attempt on a host shutdown.
            logger.LogInformation("Reconstruction job {JobId} was interrupted by cancellation.", work.Id);
            throw;
        }
        catch (Exception exception)
        {
            var retryable = IsRetryable(exception);
            logger.LogError(exception, "Reconstruction job {JobId} failed in attempt {AttemptCount}. Retryable: {Retryable}.",
                work.Id, work.AttemptCount, retryable);
            var budgetExceeded = exception is ProviderSpendLimitExceededException;
            await jobs.FailAsync(work.Id, budgetExceeded ? "provider_spend_limit_reached" : "reconstruction_failed",
                budgetExceeded
                    ? "The approved live AI spend limit has been reached; no provider request was submitted."
                    : retryable
                        ? "Reconstruction failed due to a transient error. It will be retried automatically."
                        : "Reconstruction failed. Retry the job or upload the image again.",
                retryable, CancellationToken.None);
        }
    }
}
