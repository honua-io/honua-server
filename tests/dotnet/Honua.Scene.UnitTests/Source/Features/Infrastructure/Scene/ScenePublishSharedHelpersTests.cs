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
            Guid.NewGuid(),
            "scene-null",
            "Null Scene",
            null,
            "/tmp/scene-null",
            null,
            SceneContentKind.PointCloud,
            [-122.5, 37.7, -122.4, 37.8],
            3600,
            null,
            requiresAuth: false,
            "publisher",
            CancellationToken.None);

        result.Should().BeNull();
    }

    [UnitTest]
    public async Task TryRegisterSceneAsync_PreservesContentKindAndMapsConflicts()
    {
        var registration = new StubRegistrationService();
        var datasetId = Guid.NewGuid();

        var registered = await CityGmlScenePublishExecutor.TryRegisterSceneAsync(
            registration,
            datasetId,
            "pc-scene",
            "Point Cloud Scene",
            "lidar",
            "/tmp/pc-scene",
            "prefix/pc-scene",
            SceneContentKind.PointCloud,
            [-122.5, 37.7, -122.4, 37.8],
            1800,
            "pro",
            requiresAuth: true,
            "alice",
            CancellationToken.None);

        registered.Should().Be(datasetId);
        registration.Records.Should().ContainSingle();
        var record = registration.Records[0];
        record.ContentKind.Should().Be(SceneContentKind.PointCloud);
        record.ServingFormat.Should().Be(SceneServingFormat.ThreeDTiles);
        record.Crs.Should().Be("EPSG:4979");
        record.RequiresAuth.Should().BeTrue();
        record.IsPublic.Should().BeFalse();

        registration.RejectNextRegistration = true;
        var conflictAct = async () => await CityGmlScenePublishExecutor.TryRegisterSceneAsync(
            registration,
            Guid.NewGuid(),
            "pc-scene",
            "Point Cloud Scene",
            null,
            "/tmp/pc-scene",
            null,
            SceneContentKind.Buildings,
            [-122.5, 37.7, -122.4, 37.8],
            1800,
            null,
            requiresAuth: false,
            "alice",
            CancellationToken.None);

        (await conflictAct.Should().ThrowAsync<ValidationException>())
            .Which.Message.Should().StartWith(SceneGenerationErrorCodes.SceneRegistrationConflict);
    }
}
