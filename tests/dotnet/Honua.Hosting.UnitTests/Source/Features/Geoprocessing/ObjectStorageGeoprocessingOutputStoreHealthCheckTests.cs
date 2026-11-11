// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class ObjectStorageGeoprocessingOutputStoreHealthCheckTests
{
    private static readonly IOptions<GeoprocessingOutputStagingOptions> _staging =
        Options.Create(new GeoprocessingOutputStagingOptions { Enabled = true });

    [UnitTest]
    public async Task CheckHealthAsync_EmptyReachableBucket_IsHealthy()
    {
        var storage = new SharedBucketCloudFileStorage(new SharedBucket(), CloudStorageProvider.AwsS3, string.Empty);

        var result = await CheckAsync(storage);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [UnitTest]
    public async Task CheckHealthAsync_BucketDoesNotExist_IsUnhealthyThoughAnObjectLookupReadsAsAbsent()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(default!, default).ReturnsForAnyArgs((CloudFile?)null);
        storage.ListFilesAsync(default, default, default, default)
            .ThrowsAsyncForAnyArgs(new IOException("The specified bucket does not exist: honua-private-bucket"));

        var result = await CheckAsync(storage);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().NotContain("honua-private-bucket");
    }

    private static Task<HealthCheckResult> CheckAsync(ICloudFileStorage storage)
    {
        var store = new ObjectStorageGeoprocessingOutputObjectStore(
            storage,
            _staging,
            new GeoprocessingOutputStorageKeyPrefix(string.Empty));
        return new ObjectStorageGeoprocessingOutputStoreHealthCheck(store, _staging)
            .CheckHealthAsync(new HealthCheckContext());
    }
}
