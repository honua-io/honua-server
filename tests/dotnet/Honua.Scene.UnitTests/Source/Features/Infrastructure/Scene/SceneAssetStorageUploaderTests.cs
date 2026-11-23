// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

public sealed class SceneAssetStorageUploaderTests : IDisposable
{
    private readonly string _stagingDir = Path.Join(Path.GetTempPath(), $"honua-upload-{Guid.NewGuid():N}");

    public SceneAssetStorageUploaderTests()
    {
        Directory.CreateDirectory(_stagingDir);
        File.WriteAllText(Path.Join(_stagingDir, "tileset.json"), "{\"asset\":{\"version\":\"1.1\"}}");
    }

    public void Dispose() => Directory.Delete(_stagingDir, recursive: true);

    [UnitTest]
    public async Task UploadTreeAsync_AccessDenied_IsAnUnwritableAssetStoreNotARetryableFailure()
    {
        var storage = new InMemoryCloudFileStorage
        {
            UploadFailure = UploadResult.CreateFailure("File upload failed.") with { IsAccessDenied = true }
        };

        var act = () => SceneAssetStorageUploader.UploadTreeAsync(storage, _stagingDir, "denied", CancellationToken.None);

        var failure = await act.Should().ThrowAsync<SceneOutputUnwritableException>();
        failure.Which.Code.Should().Be(SceneGenerationErrorCodes.AssetStoreUnwritable);
        failure.Which.Message.Should().Be(SceneOutputUnwritableException.ObjectStoreOperatorMessage);
    }

    [UnitTest]
    public async Task UploadTreeAsync_OtherFailure_StaysAnOrdinaryFailure()
    {
        var storage = new InMemoryCloudFileStorage { UploadFailure = UploadResult.CreateFailure("File upload failed.") };

        var act = () => SceneAssetStorageUploader.UploadTreeAsync(storage, _stagingDir, "transient", CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should().NotBeOfType<SceneOutputUnwritableException>();
    }

    [UnitTest]
    public void LocalOutputFailure_KeepsItsOwnCode()
    {
        new SceneOutputUnwritableException(new IOException("disk full")).Code
            .Should().Be(SceneGenerationErrorCodes.OutputUnwritable);
    }
}
