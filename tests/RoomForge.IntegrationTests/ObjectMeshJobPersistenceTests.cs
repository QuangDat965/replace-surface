using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RoomForge.Application.Reconstruction;
using RoomForge.Application.Scenes;
using RoomForge.Infrastructure.Persistence;
using RoomForge.Infrastructure.Export;
using RoomForge.Infrastructure.Reconstruction;
using RoomForge.Infrastructure.Storage;

namespace RoomForge.IntegrationTests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class ObjectMeshJobPersistenceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task ObjectMeshJobIsIdempotentAndKeepsRemoteSubmissionAcrossClaims()
    {
        var image = await fixture.CreateImageAsync();
        var sceneId = Guid.NewGuid();
        var scene = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-scene.json")))!.AsObject();
        scene["sceneId"] = sceneId;
        scene["projectId"] = fixture.ProjectId;
        using (var document = JsonDocument.Parse(scene.ToJsonString()))
        {
            await using var scope = fixture.CreateScope();
            var saved = await scope.ServiceProvider.GetRequiredService<ScenePersistence>()
                .SaveAsync(sceneId, document.RootElement, 0, CancellationToken.None);
            Assert.Equal(SaveSceneStatus.Saved, saved.Status);
        }
        Guid analysisJobId;
        await using (var scope = fixture.CreateScope())
        {
            var analysisJobs = scope.ServiceProvider.GetRequiredService<IReconstructionStore>();
            analysisJobId = await analysisJobs.CreateOrGetAsync(new ReconstructionJobRequest(fixture.ProjectId, image.Id,
                $"mesh-test-{Guid.NewGuid():N}"), CancellationToken.None);
            var claimedAnalysis = await analysisJobs.TryClaimAsync(CancellationToken.None);
            Assert.Equal(analysisJobId, claimedAnalysis!.Id);
            await analysisJobs.CompleteAsync(analysisJobId, "{}", CancellationToken.None);
            Assert.True(await analysisJobs.TryReserveProviderSpendAsync(analysisJobId, "fal-ai/hunyuan3d-v3/image-to-3d",
                0.50m, 20m, CancellationToken.None));
        }
        var objectId = Guid.Parse("00000000-0000-4000-8000-000000000005");
        var request = new ObjectMeshJobRequest(fixture.ProjectId, analysisJobId, image.Id, sceneId, objectId, 1,
            Guid.NewGuid().ToString("N"), "image/png", new string('a', 64), $"mesh-idempotency-{Guid.NewGuid():N}");
        Guid jobId;
        await using (var scope = fixture.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IObjectMeshJobStore>();
            jobId = await jobs.CreateOrGetAsync(request, CancellationToken.None);
            Assert.Equal(jobId, await jobs.CreateOrGetAsync(request, CancellationToken.None));
        }

        await using (var scope = fixture.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IObjectMeshJobStore>();
            var claimed = await jobs.TryClaimAsync(CancellationToken.None);
            Assert.NotNull(claimed);
            Assert.Equal(jobId, claimed!.Id);
            Assert.Equal(ObjectMeshSubmissionStates.NotAttempted, claimed.SubmissionState);
            Assert.True(await jobs.SetSubmissionAsync(jobId, ObjectMeshSubmissionStates.NotAttempted,
                ObjectMeshSubmissionStates.InProgress, null, CancellationToken.None));
            Assert.True(await jobs.SetSubmissionAsync(jobId, ObjectMeshSubmissionStates.InProgress,
                ObjectMeshSubmissionStates.Accepted, "019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b", CancellationToken.None));
        }
        await using (var scope = fixture.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IObjectMeshJobStore>();
            var persisted = await jobs.GetAsync(jobId, CancellationToken.None);
            Assert.Equal(ObjectMeshSubmissionStates.Accepted, persisted!.SubmissionState);
            Assert.Equal("019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b", persisted.ProviderJobId);
            Assert.True(await jobs.CompleteAsync(jobId, Guid.NewGuid().ToString("N"), CancellationToken.None));
            var complete = await jobs.GetAsync(jobId, CancellationToken.None);
            Assert.Equal("completed", complete!.Status);
            Assert.Equal(ObjectMeshSubmissionStates.Completed, complete.SubmissionState);
        }
    }

    [Fact]
    public async Task FixtureProviderCompletesObjectMeshJobAndReservesSharedBudgetWithoutLiveCalls()
    {
        var image = await fixture.CreateImageAsync();
        var sceneId = Guid.NewGuid();
        var scene = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-scene.json")))!.AsObject();
        scene["sceneId"] = sceneId;
        scene["projectId"] = fixture.ProjectId;
        using (var document = JsonDocument.Parse(scene.ToJsonString()))
        {
            await using var scope = fixture.CreateScope();
            var saved = await scope.ServiceProvider.GetRequiredService<ScenePersistence>()
                .SaveAsync(sceneId, document.RootElement, 0, CancellationToken.None);
            Assert.Equal(SaveSceneStatus.Saved, saved.Status);
        }

        var analysisJobId = await CreateCompletedAnalysisJob(image);
        var root = Path.Combine(Path.GetTempPath(), $"roomforge-mesh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var localAssets = new LocalAssetStore(root);
        var cropStorageId = await localAssets.SaveAsync(new MemoryStream(new byte[] { 1, 2, 3 }), 1024, CancellationToken.None);
        var meshJobRequest = new ObjectMeshJobRequest(fixture.ProjectId, analysisJobId, image.Id, sceneId,
            Guid.Parse("00000000-0000-4000-8000-000000000005"), 1, cropStorageId, "image/png", new string('b', 64), $"mesh-worker-{Guid.NewGuid():N}");
        Guid meshJobId;
        ObjectMeshWorkItem work;
        await using (var scope = fixture.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IObjectMeshJobStore>();
            meshJobId = await store.CreateOrGetAsync(meshJobRequest, CancellationToken.None);
            work = (await store.TryClaimAsync(CancellationToken.None))!;
            Assert.Equal(meshJobId, work.Id);
        }

        var glb = SampleGlb();
        var handler = new HunyuanFixtureHandler(glb);
        try
        {
            using var client = new HttpClient(handler);
            var queue = new FalHunyuan3DQueueClient(client, new FalHunyuan3DOptions("fixture-secret", Enabled: true, DataTransferApproved: true));
            await using var scope = fixture.CreateScope();
            var processor = new ObjectMeshJobProcessor(scope.ServiceProvider.GetRequiredService<IObjectMeshJobStore>(),
                scope.ServiceProvider.GetRequiredService<IReconstructionStore>(), queue, localAssets);
            await processor.ProcessAsync(work, CancellationToken.None);
            var saved = await scope.ServiceProvider.GetRequiredService<IObjectMeshJobStore>().GetAsync(meshJobId, CancellationToken.None);
            Assert.Equal("completed", saved!.Status);
            Assert.Equal(ObjectMeshSubmissionStates.Completed, saved.SubmissionState);
            Assert.NotNull(saved.OutputAssetStorageId);
            Assert.Equal(4, handler.RequestCount);
            var ledger = scope.ServiceProvider.GetRequiredService<RoomForgeDbContext>().ProviderSpendReservations
                .Where(item => item.JobId == analysisJobId).ToList();
            Assert.Single(ledger);
            Assert.Equal(0.50m, ledger[0].AmountUsd);
            var asset = localAssets.OpenStoredAsset(saved.OutputAssetStorageId!, "model/gltf-binary");
            Assert.NotNull(asset);
            asset!.Value.Content.Dispose();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private async Task<Guid> CreateCompletedAnalysisJob(ReconstructionImage image)
    {
        await using var scope = fixture.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IReconstructionStore>();
        var id = await jobs.CreateOrGetAsync(new ReconstructionJobRequest(fixture.ProjectId, image.Id, $"mesh-worker-analysis-{Guid.NewGuid():N}"), CancellationToken.None);
        var claimed = await jobs.TryClaimAsync(CancellationToken.None);
        Assert.Equal(id, claimed!.Id);
        await jobs.CompleteAsync(id, JsonSerializer.Serialize(new { sourceImageId = image.Id }), CancellationToken.None);
        return id;
    }

    private static byte[] SampleGlb()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-scene.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return new SharpGltfSceneExporter().Export(document.RootElement);
    }

    private sealed class HunyuanFixtureHandler(byte[] glb) : HttpMessageHandler
    {
        private static readonly Guid RequestId = Guid.Parse("019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b");
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri!.Host == "queue.fal.run" && request.Method == HttpMethod.Post)
                return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { request_id = RequestId })));
            if (request.RequestUri.Host == "queue.fal.run" && request.RequestUri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"status":"COMPLETED"}"""));
            if (request.RequestUri.Host == "queue.fal.run")
                return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    model_glb = new { url = "https://v3b.fal.media/files/model.glb", content_type = "model/gltf-binary", file_size = glb.Length }
                })));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(glb) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("model/gltf-binary");
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}
