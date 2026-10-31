using RoomForge.Infrastructure.Storage;

namespace RoomForge.UnitTests;

public class LocalAssetStoreTests
{
    [Fact]
    public async Task Rejects_oversized_asset_and_removes_partial_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "roomforge-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalAssetStore(root);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new MemoryStream(new byte[10]), 5, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Stores_only_fully_decoded_texture_assets_and_serves_them_by_id()
    {
        var root = Path.Combine(Path.GetTempPath(), "roomforge-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalAssetStore(root);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==");
            var saved = await store.SaveTextureAsync(new MemoryStream(png), "image/png", CancellationToken.None);
            Assert.Equal("image/png", saved.ContentType);
            Assert.Matches("^[0-9a-f]{32}$", saved.Id);
            var opened = store.OpenTexture(saved.Id);
            Assert.NotNull(opened);
            await using (opened!.Value.Content)
            {
                using var actual = new MemoryStream();
                await opened.Value.Content.CopyToAsync(actual);
                Assert.Equal(png, actual.ToArray());
            }
            Assert.Null(store.OpenTexture("..\\banhang"));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveTextureAsync(new MemoryStream(png), "image/svg+xml", CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveTextureAsync(new MemoryStream("not a png"u8.ToArray()), "image/png", CancellationToken.None));
            var malformedPng = png[..^8];
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveTextureAsync(new MemoryStream(malformedPng), "image/png", CancellationToken.None));
            var oversizedPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAIAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveTextureAsync(new MemoryStream(oversizedPng), "image/png", CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
