using RoomForge.Application.Reconstruction;
using RoomForge.Infrastructure.Storage;

namespace RoomForge.Infrastructure.Reconstruction;

public sealed class ObjectMeshJobProcessor(IObjectMeshJobStore meshJobs, IReconstructionStore analysisJobs,
    FalHunyuan3DQueueClient provider, LocalAssetStore assets)
{
    private const decimal ReservationUsd = 0.50m;
    private const decimal AggregateCapUsd = 20m;

    public async Task ProcessAsync(ObjectMeshWorkItem work, CancellationToken cancellationToken)
    {
        var job = await meshJobs.GetAsync(work.Id, cancellationToken);
        if (job is null) return;
        var submissionState = work.SubmissionState;
        if (submissionState == ObjectMeshSubmissionStates.InProgress)
        {
            await meshJobs.SetSubmissionAsync(work.Id, ObjectMeshSubmissionStates.InProgress,
                ObjectMeshSubmissionStates.OutcomeUnknown, null, CancellationToken.None);
            await meshJobs.FailAsync(work.Id, "provider_outcome_unknown",
                "Provider submission may have been accepted; automatic resubmission is blocked.", CancellationToken.None);
            return;
        }

        var requestId = work.ProviderJobId is not null && Guid.TryParse(work.ProviderJobId, out var acceptedId) ? acceptedId : Guid.Empty;
        if (submissionState == ObjectMeshSubmissionStates.NotAttempted)
        {
            if (!provider.IsConfigured)
            {
                await meshJobs.FailAsync(work.Id, "image_to_3d_provider_not_configured",
                    "Configure the fal API key on the server before submitting an image-to-3D job.", cancellationToken);
                return;
            }
            var crop = assets.OpenStoredAsset(work.CropAssetStorageId, work.CropContentType);
            if (crop is null) { await meshJobs.FailAsync(work.Id, "crop_asset_missing", "The selected crop asset is unavailable.", cancellationToken); return; }
            byte[] cropBytes;
            await using (crop.Value.Content)
            {
                using var buffer = new MemoryStream();
                await crop.Value.Content.CopyToAsync(buffer, cancellationToken);
                if (buffer.Length is 0 or > 10 * 1024 * 1024)
                { await meshJobs.FailAsync(work.Id, "crop_asset_invalid", "The selected crop is outside the supported size limit.", cancellationToken); return; }
                cropBytes = buffer.ToArray();
            }
                if (!await meshJobs.SetSubmissionAsync(work.Id, ObjectMeshSubmissionStates.NotAttempted,
                        ObjectMeshSubmissionStates.InProgress, null, cancellationToken)) return;
                submissionState = ObjectMeshSubmissionStates.InProgress;
            if (!await analysisJobs.TryReserveProviderSpendAsync(job.AnalysisJobId, FalHunyuan3DQueueClient.ModelId,
                    ReservationUsd, AggregateCapUsd, cancellationToken))
            {
                await meshJobs.SetSubmissionAsync(work.Id, ObjectMeshSubmissionStates.InProgress,
                    ObjectMeshSubmissionStates.TerminalFailure, null, CancellationToken.None);
                await meshJobs.FailAsync(work.Id, "provider_spend_limit_reached",
                    "The approved aggregate live AI smoke-test budget would be exceeded.", CancellationToken.None);
                return;
            }
            try
            {
                var submitted = await provider.SubmitGeometryAsync(cropBytes, work.CropContentType, cancellationToken);
                requestId = submitted.RequestId;
                if (!await meshJobs.SetSubmissionAsync(work.Id, ObjectMeshSubmissionStates.InProgress,
                        ObjectMeshSubmissionStates.Accepted, requestId.ToString("D"), CancellationToken.None))
                {
                    await meshJobs.FailAsync(work.Id, "provider_id_persistence_failed",
                        "Provider accepted the request but its ID could not be persisted.", CancellationToken.None);
                    return;
                }
                submissionState = ObjectMeshSubmissionStates.Accepted;
            }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                await meshJobs.SetSubmissionAsync(work.Id, ObjectMeshSubmissionStates.InProgress,
                    ObjectMeshSubmissionStates.TerminalFailure, null, CancellationToken.None);
                await meshJobs.FailAsync(work.Id, "provider_rate_limited", "The provider rejected the submission for rate limiting.", CancellationToken.None);
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                await meshJobs.SetSubmissionAsync(work.Id, ObjectMeshSubmissionStates.InProgress,
                    ObjectMeshSubmissionStates.OutcomeUnknown, null, CancellationToken.None);
                await meshJobs.FailAsync(work.Id, "provider_outcome_unknown",
                    "Provider submission may have been accepted; automatic resubmission is blocked.", CancellationToken.None);
                return;
            }
            catch (InvalidDataException)
            {
                await meshJobs.SetSubmissionAsync(work.Id, ObjectMeshSubmissionStates.InProgress,
                    ObjectMeshSubmissionStates.TerminalFailure, null, CancellationToken.None);
                await meshJobs.FailAsync(work.Id, "provider_contract_invalid", "Provider returned an invalid submission response.", CancellationToken.None);
                return;
            }
        }
        else if (submissionState is ObjectMeshSubmissionStates.Accepted or ObjectMeshSubmissionStates.Running)
        {
            if (requestId == Guid.Empty)
            {
                await meshJobs.FailAsync(work.Id, "provider_job_id_missing", "Accepted provider work has no saved request ID.", CancellationToken.None);
                return;
            }
        }
        else
        {
            await meshJobs.FailAsync(work.Id, "submission_state_not_retryable",
                "This provider submission state cannot be submitted again automatically.", CancellationToken.None);
            return;
        }

        var began = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - began < TimeSpan.FromMinutes(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await meshJobs.SetProgressAsync(work.Id, 25, cancellationToken);
            var status = await provider.GetStatusAsync(requestId, cancellationToken);
            if (status.Status == "COMPLETED")
            {
                if (!string.IsNullOrWhiteSpace(status.Error))
                {
                    var state = submissionState == ObjectMeshSubmissionStates.Running
                        ? ObjectMeshSubmissionStates.Running : ObjectMeshSubmissionStates.Accepted;
                    await meshJobs.SetSubmissionAsync(work.Id, state, ObjectMeshSubmissionStates.TerminalFailure,
                        requestId.ToString("D"), CancellationToken.None);
                    await meshJobs.FailAsync(work.Id, "provider_generation_failed", "The image-to-3D provider reported a terminal failure.", CancellationToken.None);
                    return;
                }
                await meshJobs.SetProgressAsync(work.Id, 70, cancellationToken);
                var output = await provider.GetResultAsync(requestId, cancellationToken);
                var bytes = await provider.DownloadAndValidateGlbAsync(output, cancellationToken);
                using var stream = new MemoryStream(bytes, writable: false);
                var assetId = await assets.SaveAsync(stream, 80 * 1024 * 1024, cancellationToken);
                await meshJobs.CompleteAsync(work.Id, assetId, cancellationToken);
                return;
            }
            if (status.Status == "IN_PROGRESS" && submissionState == ObjectMeshSubmissionStates.Accepted &&
                await meshJobs.SetSubmissionAsync(work.Id, ObjectMeshSubmissionStates.Accepted,
                    ObjectMeshSubmissionStates.Running, requestId.ToString("D"), cancellationToken))
                submissionState = ObjectMeshSubmissionStates.Running;
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
        // Keep the accepted/running remote ID and let the expired processing lease be reclaimed.
        // The next worker resumes polling the same request; it never submits a duplicate.
        await meshJobs.SetProgressAsync(work.Id, 85, CancellationToken.None);
    }
}
