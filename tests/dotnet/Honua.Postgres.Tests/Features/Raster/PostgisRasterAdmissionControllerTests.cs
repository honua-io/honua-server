// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Postgres.Features.Raster;
using Microsoft.Extensions.Options;

namespace Honua.Postgres.Tests.Features.Raster;

public sealed class PostgisRasterAdmissionControllerTests
{
    [Fact]
    public async Task AcquireAsync_UnknownPredictedWork_FailsClosedBeforeAdmission()
    {
        using var controller = CreateController();
        var request = PostgisRasterGovernanceTestData.Request(cost:
            PostgisRasterGovernanceTestData.Cost() with
            {
                InputPixels = long.MaxValue,
                UnknownInputs = [nameof(RasterCostEstimate.InputPixels)],
            });

        var act = async () => await controller.AcquireAsync(request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<PostgisRasterGovernanceException>();
        exception.Which.ErrorCode.Should().Be("postgis-raster-cost-unknown");
        exception.Which.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task AcquireAsync_TenantWorkOverride_FailsAtStricterCeiling()
    {
        var options = Options();
        options.Tenants["tenant-a"] = new PostgisRasterTenantPolicy
        {
            WorkLimits = new PostgisRasterTenantWorkLimits { MaxInputPixels = 100 },
        };
        using var controller = CreateController(options);
        var request = PostgisRasterGovernanceTestData.Request(cost:
            PostgisRasterGovernanceTestData.Cost() with { InputPixels = 101 });

        var act = async () => await controller.AcquireAsync(request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<PostgisRasterGovernanceException>();
        exception.Which.ErrorCode.Should().Be("postgis-raster-work-limit-exceeded");
        exception.Which.Message.Should().Contain("input-pixels");
    }

    [Fact]
    public async Task AcquireAsync_PerTenantPressure_DoesNotConsumeAnotherTenantsGlobalSlot()
    {
        var options = Options();
        options.MaxConcurrency = 2;
        options.MaxConcurrencyPerTenant = 1;
        options.QueueTimeout = TimeSpan.FromMilliseconds(75);
        using var controller = CreateController(options);
        await using var firstTenantLease = await controller.AcquireAsync(
            PostgisRasterGovernanceTestData.Request("tenant-a"),
            CancellationToken.None);

        var saturatedAct = async () => await controller.AcquireAsync(
            PostgisRasterGovernanceTestData.Request("tenant-a"),
            CancellationToken.None);
        await using var otherTenantLease = await controller.AcquireAsync(
            PostgisRasterGovernanceTestData.Request("tenant-b"),
            CancellationToken.None);

        var exception = await saturatedAct.Should().ThrowAsync<PostgisRasterGovernanceException>();
        exception.Which.ErrorCode.Should().Be("postgis-raster-admission-timeout");
        exception.Which.IsRetryable.Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_ReleasedLeaseRestoresCapacity()
    {
        var options = Options();
        options.MaxConcurrency = 1;
        options.MaxConcurrencyPerTenant = 1;
        using var controller = CreateController(options);
        var request = PostgisRasterGovernanceTestData.Request();

        await using (await controller.AcquireAsync(request, CancellationToken.None))
        {
        }

        await using var secondLease = await controller.AcquireAsync(request, CancellationToken.None);
        secondLease.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_TenantSnapshotMismatch_IsPermanentFailure()
    {
        using var controller = CreateController();
        var request = PostgisRasterGovernanceTestData.Request() with
        {
            Parameters = new Dictionary<string, string>
            {
                [RasterProviderExecutionParameterKeys.TenantId] = "tenant-b",
            },
        };

        var act = async () => await controller.AcquireAsync(request, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<PostgisRasterGovernanceException>();
        exception.Which.ErrorCode.Should().Be("postgis-raster-tenant-mismatch");
        exception.Which.IsRetryable.Should().BeFalse();
    }

    private static PostgisRasterAdmissionController CreateController(
        PostgisRasterExecutionOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options()));

    private static PostgisRasterExecutionOptions Options() => new()
    {
        RequiredRole = "raster_role",
        SearchPathSchema = "honua",
        MaxConcurrency = 4,
        MaxConcurrencyPerTenant = 2,
        QueueTimeout = TimeSpan.FromMilliseconds(250),
    };
}
