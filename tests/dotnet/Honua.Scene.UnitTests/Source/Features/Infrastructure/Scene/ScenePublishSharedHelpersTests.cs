// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

public sealed class ScenePublishSharedHelpersTests
{
    [UnitTest]
    public void ValidateOptions_ValidArguments_DoesNotThrow()
    {
        var act = () => CityGmlScenePublishExecutor.ValidateOptions("My Scene", "enterprise", 3600);
        act.Should().NotThrow();
    }

    [UnitTest]
    public void ValidateOptions_NegativeCacheMaxAge_ThrowsOptionsInvalid()
    {
        var act = () => CityGmlScenePublishExecutor.ValidateOptions("My Scene", null, -5);
        act.Should().Throw<ValidationException>()
            .Which.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
    }

    [UnitTest]
    public async Task TryRegisterSceneAsync_NullRegistration_ReturnsNull()
    {
        var result = await CityGmlScenePublishExecutor.TryRegisterSceneAsync(
            registration: null,
            new SceneRegistrationRequest
            {
                DatasetId = Guid.NewGuid(),
                SceneId = "scene-null",
                DisplayName = "Null Scene",
                Description = null,
                OutputDirectory = "/tmp/scene-null",
                AssetStoragePrefix = null,
                ContentKind = SceneContentKind.PointCloud,
                Bounds = [-122.5, 37.7, -122.4, 37.8],
                CacheMaxAgeSeconds = 3600,
                EditionGate = null,
                RequiresAuth = false,
                CreatedBy = "publisher",
            },
            CancellationToken.None);

        result.Should().BeNull();
    }

    [UnitTest]
    public async Task TryRegisterSceneAsync_PreservesContentKindAndMapsConflicts()
    {
        var registration = new StubRegistrationService();
        var datasetId = Guid.NewGuid();

        var registered = await RegisterProtectedPointCloudAsync(registration, datasetId);

        registered.Should().Be(datasetId);
        registration.Records.Should().ContainSingle();
        var record = registration.Records[0];
        record.ContentKind.Should().Be(SceneContentKind.PointCloud);
        record.ServingFormat.Should().Be(SceneServingFormat.ThreeDTiles);
        record.Crs.Should().Be("EPSG:4979");
        record.RequiresAuth.Should().BeTrue();
        record.IsPublic.Should().BeFalse();

        registration.RejectNextRegistration = true;
        var conflictAct = () => RegisterPublicBuildingsAsync(registration);

        (await conflictAct.Should().ThrowAsync<ValidationException>())
            .Which.Message.Should().StartWith(SceneGenerationErrorCodes.SceneRegistrationConflict);
    }

    private static Task<Guid?> RegisterProtectedPointCloudAsync(StubRegistrationService registration, Guid datasetId)
        => CityGmlScenePublishExecutor.TryRegisterSceneAsync(
            registration,
            new SceneRegistrationRequest
            {
                DatasetId = datasetId,
                SceneId = "pc-scene",
                DisplayName = "Point Cloud Scene",
                Description = "lidar",
                OutputDirectory = "/tmp/pc-scene",
                AssetStoragePrefix = "prefix/pc-scene",
                ContentKind = SceneContentKind.PointCloud,
                Bounds = [-122.5, 37.7, -122.4, 37.8],
                CacheMaxAgeSeconds = 1800,
                EditionGate = "pro",
                RequiresAuth = true,
                CreatedBy = "alice",
            },
            CancellationToken.None);

    private static Task<Guid?> RegisterPublicBuildingsAsync(StubRegistrationService registration)
        => CityGmlScenePublishExecutor.TryRegisterSceneAsync(
            registration,
            new SceneRegistrationRequest
            {
                DatasetId = Guid.NewGuid(),
                SceneId = "pc-scene",
                DisplayName = "Point Cloud Scene",
                Description = null,
                OutputDirectory = "/tmp/pc-scene",
                AssetStoragePrefix = null,
                ContentKind = SceneContentKind.Buildings,
                Bounds = [-122.5, 37.7, -122.4, 37.8],
                CacheMaxAgeSeconds = 1800,
                EditionGate = null,
                RequiresAuth = false,
                CreatedBy = "alice",
            },
            CancellationToken.None);
}
