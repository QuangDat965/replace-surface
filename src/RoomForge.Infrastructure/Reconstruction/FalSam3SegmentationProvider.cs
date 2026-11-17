using RoomForge.Application.Reconstruction;
using RoomForge.Domain.Reconstruction;
using RoomForge.Infrastructure.Storage;

namespace RoomForge.Infrastructure.Reconstruction;

public sealed class FalSam3SegmentationProvider(FalSam3QueueClient fal, LocalAssetStore assets) : IObjectSegmentationProvider
{
    private const string Provider = "fal-ai/sam-3/image";
    public string ProviderId => Provider;

    public async Task<SegmentationOutput> SegmentAsync(ReconstructionImage image, Guid jobId, IReconstructionStore jobs,
        CancellationToken cancellationToken)
    {
        if (image.JobId is { } sourceJobId && sourceJobId != jobId)
            throw new InvalidOperationException("The segmentation job does not match the source image context.");
        var job = await jobs.GetAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException("The reconstruction job no longer exists.");

        if (job.SubmissionState == SubmissionStates.TerminalFailure && job.ProviderId == Provider)
        {
            if (!await jobs.SetSubmissionStateAsync(jobId, SubmissionStates.TerminalFailure, SubmissionStates.NotAttempted,
                    Provider, null, false, cancellationToken))
                throw new InvalidOperationException("Could not reset a provider-confirmed rejected submission for retry.");
            job = job with { SubmissionState = SubmissionStates.NotAttempted };
        }

        Guid requestId;
        if (job.SubmissionState == SubmissionStates.NotAttempted)
        {
            var storedImage = assets.OpenRoomImage(image.StorageId)
                ?? throw new InvalidDataException("The source image asset is unavailable.");
            await using var stream = storedImage.Content;
            var imageBytes = await ReadImageAsync(stream, cancellationToken);
            await jobs.SetProgressAsync(jobId, "provider_submission", 28, cancellationToken);
            if (!await jobs.SetSubmissionStateAsync(jobId, SubmissionStates.NotAttempted, SubmissionStates.InProgress,
                    Provider, null, false, cancellationToken))
                throw new InvalidOperationException("Could not reserve the provider submission state.");
            if (!await jobs.TryReserveProviderSpendAsync(jobId, Provider, 0.01m, 20m, cancellationToken))
            {
                await jobs.SetSubmissionStateAsync(jobId, SubmissionStates.InProgress, SubmissionStates.TerminalFailure,
                    Provider, null, false, CancellationToken.None);
                throw new ProviderSpendLimitExceededException("The approved aggregate live AI smoke-test budget of 20 USD would be exceeded.");
            }

            try
            {
                using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var heartbeat = KeepLeaseAliveAsync(jobId, jobs, heartbeatStop.Token);
                FalQueueSubmission submission;
                try { submission = await fal.SubmitAsync(imageBytes, image.ContentType, "floor", cancellationToken); }
                finally
                {
                    await heartbeatStop.CancelAsync();
                    try { await heartbeat; }
                    catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
                }
                requestId = submission.RequestId;
                if (!await jobs.SetSubmissionStateAsync(jobId, SubmissionStates.InProgress, SubmissionStates.Accepted,
                        Provider, requestId.ToString("D"), false, CancellationToken.None))
                    throw new InvalidOperationException("fal accepted the request, but its ID could not be persisted.");
            }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                await jobs.SetSubmissionStateAsync(jobId, SubmissionStates.InProgress, SubmissionStates.TerminalFailure,
                    Provider, null, false, CancellationToken.None);
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                await jobs.SetSubmissionStateAsync(jobId, SubmissionStates.InProgress, SubmissionStates.OutcomeUnknown,
                    Provider, null, false, CancellationToken.None);
                throw new ProviderSubmissionOutcomeUnknownException("fal SAM 3 submission may have been accepted; automatic resubmission is blocked.", exception);
            }
            catch (InvalidDataException)
            {
                await jobs.SetSubmissionStateAsync(jobId, SubmissionStates.InProgress, SubmissionStates.TerminalFailure,
                    Provider, null, false, CancellationToken.None);
                throw;
            }
        }
        else if (job.SubmissionState is SubmissionStates.Accepted or SubmissionStates.Running)
        {
            if (!string.Equals(job.ProviderId, Provider, StringComparison.Ordinal) ||
                !Guid.TryParse(job.ProviderJobId, out requestId))
                throw new InvalidDataException("Persisted provider submission metadata is incomplete or belongs to another provider.");
        }
        else if (job.SubmissionState == SubmissionStates.InProgress)
        {
            await jobs.SetSubmissionStateAsync(jobId, SubmissionStates.InProgress, SubmissionStates.OutcomeUnknown,
                Provider, null, false, CancellationToken.None);
            throw new ProviderSubmissionOutcomeUnknownException("The host stopped during provider submission; automatic resubmission is blocked.");
        }
        else if (job.SubmissionState == SubmissionStates.OutcomeUnknown)
        {
            throw new ProviderSubmissionOutcomeUnknownException("Provider submission outcome is unknown; reconcile before any resubmission.");
        }
        else
        {
            throw new InvalidOperationException($"Cannot continue SAM 3 work from submission state '{job.SubmissionState}'.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < TimeSpan.FromMinutes(15))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await jobs.SetProgressAsync(jobId, "provider_polling", 42, cancellationToken);
            var status = await fal.GetStatusAsync(requestId, cancellationToken);
            if (status.Status == "IN_PROGRESS")
            {
                var snapshot = await jobs.GetAsync(jobId, cancellationToken);
                if (snapshot?.SubmissionState == SubmissionStates.Accepted)
                    await jobs.SetSubmissionStateAsync(jobId, SubmissionStates.Accepted, SubmissionStates.Running,
                        Provider, requestId.ToString("D"), false, cancellationToken);
            }
            else if (status.Status == "COMPLETED")
            {
                if (!string.IsNullOrWhiteSpace(status.Error))
                {
                    var snapshot = await jobs.GetAsync(jobId, cancellationToken);
                    var current = snapshot?.SubmissionState;
                    if (current is SubmissionStates.Accepted or SubmissionStates.Running)
                        await jobs.SetSubmissionStateAsync(jobId, current, SubmissionStates.TerminalFailure,
                            Provider, requestId.ToString("D"), false, CancellationToken.None);
                    throw new InvalidDataException("fal SAM 3 reported a terminal inference failure.");
                }

                await jobs.SetProgressAsync(jobId, "provider_result", 72, cancellationToken);
                var result = await fal.GetResultAsync(requestId, cancellationToken);
                var index = Enumerable.Range(0, result.Masks.Count)
                    .Where(candidate => candidate < result.Boxes.Count && candidate < result.Scores.Count && result.Scores[candidate] is not null)
                    .OrderByDescending(candidate => result.Scores[candidate])
                    .FirstOrDefault(-1);
                if (index < 0 || result.Boxes[index] is not { Length: 4 } box || result.Scores[index] is not { } score)
                    throw new InvalidDataException("fal SAM 3 did not return a scored bounding box for the floor prompt.");

                var centerX = box[0] * image.Width;
                var centerY = box[1] * image.Height;
                var width = box[2] * image.Width;
                var height = box[3] * image.Height;
                var left = Math.Clamp(centerX - width / 2, 0, image.Width);
                var right = Math.Clamp(centerX + width / 2, 0, image.Width);
                var top = Math.Clamp(centerY - height / 2, 0, image.Height);
                var bottom = Math.Clamp(centerY + height / 2, 0, image.Height);
                if (right - left < 1 || bottom - top < 1) throw new InvalidDataException("fal SAM 3 floor box is degenerate.");
                var candidateId = ReconstructionCandidateIds.ForImage(image.Id, "floor");
                var maskBytes = await fal.DownloadMaskAsync(result.Masks[index], cancellationToken);
                var polygon = FalMaskPolygonExtractor.Extract(maskBytes, image.Width, image.Height, box);
                var polygonSource = "mask-row-envelope";
                if (polygon is null)
                {
                    polygon = new ImagePolygon([new(left, top), new(right, top), new(right, bottom), new(left, bottom)]);
                    polygonSource = "provider-box-fallback";
                }
                var currentState = (await jobs.GetAsync(jobId, cancellationToken))?.SubmissionState;
                if (currentState is SubmissionStates.Accepted or SubmissionStates.Running)
                    await jobs.SetSubmissionStateAsync(jobId, currentState, SubmissionStates.Completed,
                        Provider, requestId.ToString("D"), false, cancellationToken);
                return new SegmentationOutput(
                    new Dictionary<Guid, ImagePolygon> { [candidateId] = polygon },
                    $"fal-ai/sam-3/image;prompt=floor;{polygonSource};score={score:0.000}",
                    new Dictionary<Guid, double> { [candidateId] = score });
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException("fal SAM 3 remained queued or running past the 15-minute polling limit.");
    }

    private static async Task<byte[]> ReadImageAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > 10 * 1024 * 1024) throw new InvalidDataException("Source image exceeds the provider's 10 MB input limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        if (buffer.Length == 0) throw new InvalidDataException("Source image is empty.");
        return buffer.ToArray();
    }

    private static async Task KeepLeaseAliveAsync(Guid jobId, IReconstructionStore jobs, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await jobs.SetProgressAsync(jobId, "provider_submission", 30, cancellationToken);
    }
}

public sealed class ProviderSubmissionOutcomeUnknownException(string message, Exception? innerException = null)
    : Exception(message, innerException);
