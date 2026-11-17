using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RoomForge.Application.Reconstruction;
using RoomForge.Application.Annotations;
using RoomForge.Domain.Reconstruction;
using RoomForge.Domain.Annotations;
using RoomForge.Infrastructure.Persistence;
using RoomForge.Infrastructure.Reconstruction;
using RoomForge.Infrastructure.Storage;
using System.Net;
using System.Text;
using ImageMagick;
using ImageMagick.Drawing;

namespace RoomForge.IntegrationTests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class ReconstructionJobPersistenceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task FalSamSubmissionIsPersistedAndResultIsNormalizedToReviewedFloorPolygon()
    {
        await ResetJobsAsync();
        var (image, jobId) = await CreateJobAsync();
        await using var scope = fixture.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IReconstructionStore>();
        var work = await jobs.TryClaimAsync(CancellationToken.None);
        Assert.Equal(jobId, work?.Id);

        var assetRoot = Path.Combine(Path.GetTempPath(), $"roomforge-fal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(assetRoot);
        await File.WriteAllBytesAsync(Path.Combine(assetRoot, image.StorageId + ".png"), [1, 2, 3]);
        try
        {
            var handler = new FalFixtureHandler((request, _) =>
            {
                if (request.RequestUri!.Host == "v3b.fal.media") return Task.FromResult(MaskPngResponse());
                var response = request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
                    ? Json(HttpStatusCode.OK, """{"status":"COMPLETED","request_id":"019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b"}""")
                    : request.RequestUri.AbsolutePath.EndsWith("/requests/019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b", StringComparison.Ordinal)
                        ? Json(HttpStatusCode.OK, """{"masks":[{"url":"https://v3b.fal.media/mask.png","content_type":"image/png","file_size":300,"width":32,"height":32}],"scores":[0.91],"boxes":[[0.5,0.6,0.4,0.3]]}""")
                        : Json(HttpStatusCode.OK, """{"request_id":"019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b"}""");
                return Task.FromResult(response);
            });
            var provider = new FalSam3SegmentationProvider(new FalSam3QueueClient(new HttpClient(handler), new FalSam3Options("fixture-key")), new LocalAssetStore(assetRoot));

            var output = await provider.SegmentAsync(image, jobId, jobs, CancellationToken.None);
            var saved = await jobs.GetAsync(jobId, CancellationToken.None);
            var candidate = output.Polygons[ReconstructionCandidateIds.ForImage(image.Id, "floor")];

            Assert.Equal(SubmissionStates.Completed, saved?.SubmissionState);
            Assert.Equal("fal-ai/sam-3/image", saved?.ProviderId);
            Assert.Equal("019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b", saved?.ProviderJobId);
            Assert.Equal(0.91, output.Confidences![ReconstructionCandidateIds.ForImage(image.Id, "floor")]);
            Assert.Contains("mask-row-envelope", output.Provenance);
            Assert.NotEqual(4, candidate.Points.Count);
        }
        finally { Directory.Delete(assetRoot, recursive: true); }
    }

    [Fact]
    public async Task UnknownFalSubmitOutcomeIsPersistedAndNeverResubmitted()
    {
        await ResetJobsAsync();
        var (image, jobId) = await CreateJobAsync();
        await using var scope = fixture.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IReconstructionStore>();
        await jobs.TryClaimAsync(CancellationToken.None);
        var assetRoot = Path.Combine(Path.GetTempPath(), $"roomforge-fal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(assetRoot);
        await File.WriteAllBytesAsync(Path.Combine(assetRoot, image.StorageId + ".png"), [1, 2, 3]);
        try
        {
            var calls = 0;
            var handler = new FalFixtureHandler((_, _) =>
            {
                calls++;
                throw new HttpRequestException("synthetic socket failure");
            });
            var provider = new FalSam3SegmentationProvider(new FalSam3QueueClient(new HttpClient(handler), new FalSam3Options("fixture-key")), new LocalAssetStore(assetRoot));

            await Assert.ThrowsAsync<ProviderSubmissionOutcomeUnknownException>(() => provider.SegmentAsync(image, jobId, jobs, CancellationToken.None));
            var saved = await jobs.GetAsync(jobId, CancellationToken.None);
            Assert.Equal(SubmissionStates.OutcomeUnknown, saved?.SubmissionState);
            await Assert.ThrowsAsync<ProviderSubmissionOutcomeUnknownException>(() => provider.SegmentAsync(image, jobId, jobs, CancellationToken.None));
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(assetRoot, recursive: true); }
    }

    [Fact]
    public async Task AcceptedFalJobResumesPollingAfterRestartWithoutAnotherSubmit()
    {
        await ResetJobsAsync();
        var (image, jobId) = await CreateJobAsync();
        await using var scope = fixture.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IReconstructionStore>();
        await jobs.TryClaimAsync(CancellationToken.None);
        const string remoteId = "019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b";
        Assert.True(await jobs.SetSubmissionStateAsync(jobId, SubmissionStates.NotAttempted, SubmissionStates.InProgress,
            "fal-ai/sam-3/image", null, false, CancellationToken.None));
        Assert.True(await jobs.SetSubmissionStateAsync(jobId, SubmissionStates.InProgress, SubmissionStates.Accepted,
            "fal-ai/sam-3/image", remoteId, false, CancellationToken.None));

        var assetRoot = Path.Combine(Path.GetTempPath(), $"roomforge-fal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(assetRoot);
        await File.WriteAllBytesAsync(Path.Combine(assetRoot, image.StorageId + ".png"), [1, 2, 3]);
        try
        {
            var handler = new FalFixtureHandler((request, _) => request.RequestUri!.Host == "v3b.fal.media"
                ? Task.FromResult(MaskPngResponse())
                : request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
                    ? Task.FromResult(Json(HttpStatusCode.OK, """{"status":"COMPLETED","request_id":"019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b"}"""))
                    : Task.FromResult(Json(HttpStatusCode.OK, """{"masks":[{"url":"https://v3b.fal.media/mask.png","content_type":"image/png","file_size":300,"width":32,"height":32}],"scores":[0.91],"boxes":[[0.5,0.6,0.4,0.3]]}""")));
            var provider = new FalSam3SegmentationProvider(new FalSam3QueueClient(new HttpClient(handler), new FalSam3Options("fixture-key")), new LocalAssetStore(assetRoot));

            await provider.SegmentAsync(image, jobId, jobs, CancellationToken.None);

            Assert.Equal(SubmissionStates.Completed, (await jobs.GetAsync(jobId, CancellationToken.None))?.SubmissionState);
            Assert.Equal(0, handler.PostCount);
        }
        finally { Directory.Delete(assetRoot, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentProviderReservationsStayWithinAggregateSpendCap()
    {
        await ResetJobsAsync();
        var (_, jobId) = await CreateJobAsync();
        await using (var claimScope = fixture.CreateScope())
            Assert.Equal(jobId, (await claimScope.ServiceProvider.GetRequiredService<IReconstructionStore>().TryClaimAsync(CancellationToken.None))?.Id);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var scope = fixture.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IReconstructionStore>()
                .TryReserveProviderSpendAsync(jobId, "fixture-provider", 0.5m, 1m, CancellationToken.None);
        }));

        Assert.Equal(2, results.Count(value => value));
        await using var verifyScope = fixture.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<RoomForgeDbContext>();
        Assert.Equal(1m, (await db.ProviderSpendReservations.SumAsync(reservation => (decimal?)reservation.AmountUsd)) ?? 0m);
    }

    [Fact]
    public async Task ConcurrentRequestsWithSameIdempotencyKeyCreateOneJob()
    {
        await ResetJobsAsync();
        var image = await fixture.CreateImageAsync();
        var key = $"concurrent-{Guid.NewGuid():N}";
        var submissions = Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var scope = fixture.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IReconstructionStore>()
                .CreateOrGetAsync(new ReconstructionJobRequest(fixture.ProjectId, image.Id, key), CancellationToken.None);
        });

        var ids = await Task.WhenAll(submissions);

        Assert.Single(ids.Distinct());
        await using var verifyScope = fixture.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<RoomForgeDbContext>();
        Assert.Equal(1, await db.ReconstructionJobs.CountAsync(job => job.ProjectId == fixture.ProjectId && job.IdempotencyKey == key));
    }

    [Fact]
    public async Task ExpiredLeaseCanBeRecoveredAfterWorkerRestart()
    {
        await ResetJobsAsync();
        var (image, jobId) = await CreateJobAsync();
        await using var storeScope = fixture.CreateScope();
        var store = storeScope.ServiceProvider.GetRequiredService<IReconstructionStore>();
        var firstClaim = await store.TryClaimAsync(CancellationToken.None);
        Assert.Equal(jobId, firstClaim?.Id);
        Assert.Equal(1, firstClaim?.AttemptCount);

        await using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoomForgeDbContext>();
            await db.ReconstructionJobs.Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseUntil, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        await using var restartedScope = fixture.CreateScope();
        var recovered = await restartedScope.ServiceProvider.GetRequiredService<IReconstructionStore>().TryClaimAsync(CancellationToken.None);
        Assert.Equal(jobId, recovered?.Id);
        Assert.Equal(2, recovered?.AttemptCount);
        Assert.Equal(image.Id, recovered?.ImageId);
    }

    [Fact]
    public async Task TransientFailuresBackOffAndBecomeTerminalAtMaxAttempts()
    {
        await ResetJobsAsync();
        var (_, jobId) = await CreateJobAsync();
        await using var storeScope = fixture.CreateScope();
        var store = storeScope.ServiceProvider.GetRequiredService<IReconstructionStore>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var claimed = await store.TryClaimAsync(CancellationToken.None);
            Assert.Equal(jobId, claimed?.Id);
            Assert.Equal(attempt, claimed?.AttemptCount);
            await store.FailAsync(jobId, "temporary_provider_failure", "Temporary failure.", true, CancellationToken.None);

            await using var scope = fixture.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RoomForgeDbContext>();
            var record = await db.ReconstructionJobs.SingleAsync(job => job.Id == jobId);
            if (attempt < 3)
            {
                Assert.Equal("queued", record.Status);
                Assert.True(record.NextAttemptAt > DateTimeOffset.UtcNow);
                await db.ReconstructionJobs.Where(job => job.Id == jobId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.NextAttemptAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddSeconds(-1)));
            }
            else
            {
                Assert.Equal("failed", record.Status);
                Assert.Null(record.NextAttemptAt);
                Assert.Equal("temporary_provider_failure", record.ErrorCode);
            }
        }

        Assert.Null(await store.TryClaimAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UserRetryIsAllowedOnlyForSafeFailedJobsWithinAttemptBudget()
    {
        await ResetJobsAsync();
        var (_, jobId) = await CreateJobAsync();
        await using var scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReconstructionStore>();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var work = await store.TryClaimAsync(CancellationToken.None);
            Assert.Equal(jobId, work?.Id);
            Assert.Equal(attempt, work?.AttemptCount);
            await store.FailAsync(jobId, "reviewable_failure", "A safe terminal failure.", false, CancellationToken.None);
            Assert.Equal("failed", (await store.GetAsync(jobId, CancellationToken.None))?.Status);

            if (attempt < 3)
            {
                Assert.True(await store.RetryAsync(jobId, CancellationToken.None));
                var queued = await store.GetAsync(jobId, CancellationToken.None);
                Assert.Equal("queued", queued?.Status);
                Assert.Equal(SubmissionStates.NotAttempted, queued?.SubmissionState);
                Assert.Equal(attempt, queued?.AttemptCount);
            }
            else Assert.False(await store.RetryAsync(jobId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task AmbiguousSubmissionRequiresReconciliationBeforeRetry()
    {
        await ResetJobsAsync();
        var (_, jobId) = await CreateJobAsync();
        await using var storeScope = fixture.CreateScope();
        var store = storeScope.ServiceProvider.GetRequiredService<IReconstructionStore>();
        Assert.True(await store.SetSubmissionStateAsync(jobId, SubmissionStates.NotAttempted, SubmissionStates.InProgress,
            "fixture-provider", null, false, CancellationToken.None));
        Assert.True(await store.SetSubmissionStateAsync(jobId, SubmissionStates.InProgress, SubmissionStates.OutcomeUnknown,
            "fixture-provider", null, false, CancellationToken.None));
        await store.FailAsync(jobId, "submission_outcome_unknown", "Provider outcome is unknown.", false, CancellationToken.None);

        Assert.False(await store.RetryAsync(jobId, CancellationToken.None));
        Assert.False(await store.RecordReconciliationAsync(jobId, SubmissionStates.OutcomeUnknown, SubmissionStates.NotAttempted, null, CancellationToken.None));
        Assert.True(await store.RecordReconciliationAsync(jobId, SubmissionStates.OutcomeUnknown, SubmissionStates.Accepted,
            "external-fixture-job", CancellationToken.None));
        var snapshot = await store.GetAsync(jobId, CancellationToken.None);
        Assert.Equal(SubmissionStates.Accepted, snapshot?.SubmissionState);
        Assert.Equal(1, snapshot?.ReconciliationAttempts);
        Assert.NotNull(snapshot?.LastReconciledAt);
        Assert.Equal("external-fixture-job", snapshot?.ProviderJobId);

        var (_, supportedJobId) = await CreateJobAsync();
        Assert.True(await store.SetSubmissionStateAsync(supportedJobId, SubmissionStates.NotAttempted, SubmissionStates.InProgress,
            "idempotent-fixture-provider", null, true, CancellationToken.None));
        Assert.True(await store.SetSubmissionStateAsync(supportedJobId, SubmissionStates.InProgress, SubmissionStates.OutcomeUnknown,
            "idempotent-fixture-provider", null, true, CancellationToken.None));
        Assert.True(await store.RecordReconciliationAsync(supportedJobId, SubmissionStates.OutcomeUnknown,
            SubmissionStates.NotAttempted, null, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsTerminalAndDoesNotGetClaimed()
    {
        await ResetJobsAsync();
        var (_, jobId) = await CreateJobAsync();
        await using var scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReconstructionStore>();
        Assert.True(await store.CancelAsync(jobId, CancellationToken.None));
        Assert.Null(await store.TryClaimAsync(CancellationToken.None));
        Assert.Equal("cancelled", (await store.GetAsync(jobId, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task MalformedProviderOutputFailsJobAndKeepsSourceImage()
    {
        await ResetJobsAsync();
        var (image, jobId) = await CreateJobAsync();
        await using var scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReconstructionStore>();
        var images = scope.ServiceProvider.GetRequiredService<IReconstructionImageStore>();
        var work = await store.TryClaimAsync(CancellationToken.None);
        Assert.Equal(jobId, work?.Id);

        var analysis = new ReconstructionAnalysisService(
            new InvalidPolygonProvider(), new EmptySegmentationProvider(), new UnknownDepthProvider(), new NoMeshProvider(), store);
        var processor = new ReconstructionJobProcessor(store, images, analysis, NullLogger<ReconstructionJobProcessor>.Instance);
        await processor.ProcessAsync(work!, CancellationToken.None);

        var failed = await store.GetAsync(jobId, CancellationToken.None);
        Assert.Equal("failed", failed?.Status);
        Assert.Equal("analysis_contract_invalid", failed?.ErrorCode);
        Assert.NotNull(await images.GetAsync(image.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AnnotationDocumentPersistsWithOptimisticRevisionChecks()
    {
        await ResetJobsAsync();
        var image = await fixture.CreateImageAsync(1200, 800);
        await using var scope = fixture.CreateScope();
        var annotations = scope.ServiceProvider.GetRequiredService<AnnotationPersistence>();
        var created = await annotations.CreateAsync(fixture.ProjectId, image.Id, CancellationToken.None);
        Assert.NotNull(created);
        Assert.Equal(1, created.Revision);
        Assert.Equal(1200, created.ImageWidth);

        var region = new AnnotationRegion(Guid.NewGuid(), "floor", new ImagePolygon(
            [new(50, 300), new(1100, 300), new(1150, 750), new(20, 750)]));
        var span = new AnnotationReferenceSpan(Guid.NewGuid(), new ImagePoint(100, 500), new ImagePoint(600, 500), 2.5);
        var update = new UpdateAnnotationDocument([region], [span], new AnnotationCameraHint(null, null), ["Measured from site plan."]);
        var saved = await annotations.UpdateAsync(fixture.ProjectId, created.AnnotationId, 1, update, CancellationToken.None);
        Assert.Equal(UpdateAnnotationStatus.Saved, saved.Status);
        Assert.Equal(2, saved.Document?.Revision);

        var stale = await annotations.UpdateAsync(fixture.ProjectId, created.AnnotationId, 1,
            update with { Notes = ["stale write"] }, CancellationToken.None);
        Assert.Equal(UpdateAnnotationStatus.RevisionConflict, stale.Status);
        Assert.Equal(2, stale.Document?.Revision);

        var reloaded = await annotations.GetAsync(fixture.ProjectId, created.AnnotationId, CancellationToken.None);
        Assert.Equal(2, reloaded?.Revision);
        var reloadedRegion = reloaded?.Regions.Single();
        Assert.Equal(region.Id, reloadedRegion?.Id);
        Assert.Equal(region.Polygon.Points, reloadedRegion?.Polygon.Points);
        Assert.Equal(2.5, reloaded?.ReferenceSpans.Single().KnownMeters);
        Assert.Equal("Measured from site plan.", reloaded?.Notes.Single());
    }

    private async Task<(ReconstructionImage Image, Guid JobId)> CreateJobAsync()
    {
        var image = await fixture.CreateImageAsync();
        await using var scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReconstructionStore>();
        var jobId = await store.CreateOrGetAsync(new ReconstructionJobRequest(fixture.ProjectId, image.Id, Guid.NewGuid().ToString("N")), CancellationToken.None);
        return (image, jobId);
    }

    private async Task ResetJobsAsync()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RoomForgeDbContext>();
        await db.ReconstructionJobs.Where(job => job.ProjectId == fixture.ProjectId).ExecuteDeleteAsync();
    }

    private sealed class InvalidPolygonProvider : ISceneUnderstandingProvider
    {
        public string ProviderId => "invalid-fixture";
        public string ProviderVersion => "1";
        public Task<SceneUnderstandingOutput> UnderstandAsync(ReconstructionImage source, CancellationToken cancellationToken) =>
            Task.FromResult(new SceneUnderstandingOutput(
                [new DetectedSurface(Guid.NewGuid(), "wall", new ImagePolygon([new(0, 0), new(source.Width + 1, 10), new(10, 10)]), null, null, .8, "fixture")], []));
    }

    private sealed class EmptySegmentationProvider : IObjectSegmentationProvider
    {
        public string ProviderId => "invalid-fixture";
        public Task<SegmentationOutput> SegmentAsync(ReconstructionImage source, Guid jobId, IReconstructionStore jobs, CancellationToken cancellationToken) =>
            Task.FromResult(new SegmentationOutput(new Dictionary<Guid, ImagePolygon>(), "fixture"));
    }

    private sealed class UnknownDepthProvider : IDepthEstimationProvider
    {
        public string ProviderId => "invalid-fixture";
        public Task<DepthEstimationOutput> EstimateAsync(ReconstructionImage source, CancellationToken cancellationToken) =>
            Task.FromResult(new DepthEstimationOutput("unknown", null, []));
    }

    private sealed class NoMeshProvider : IImageTo3DProvider
    {
        public string ProviderId => "invalid-fixture";
        public Task<ImageTo3DOutput> GenerateAsync(ReconstructionImage source, DetectedObject item, CancellationToken cancellationToken) =>
            Task.FromResult(new ImageTo3DOutput(false, "not configured", null, null));
    }

    private sealed class FalFixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public int PostCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Method == HttpMethod.Post) PostCount++;
            return respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage MaskPngResponse()
    {
        using var image = new MagickImage(MagickColors.Black, 32, 32);
        image.Draw(new Drawables().FillColor(MagickColors.White).Rectangle(12, 15, 20, 22));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image.ToByteArray(MagickFormat.Png)) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return response;
    }
}
