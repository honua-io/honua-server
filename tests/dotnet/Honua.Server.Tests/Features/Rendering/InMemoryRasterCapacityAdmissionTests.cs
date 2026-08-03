// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Capacity;
using Honua.Infrastructure.Rendering;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Rendering;

public sealed class InMemoryRasterCapacityAdmissionTests
{
    [Fact]
    public async Task TryAcquireAsync_StaticWorkDenial_DoesNotConsumeConcurrency()
    {
        var admission = CreateAdmission(maxGlobal: 1, maxPerTenant: 1, maxObjectBytes: 10);

        var denied = await admission.TryAcquireAsync(Request("tenant-a", new RasterCapacityWork(0, 0, 0, 11, 0)));
        var admitted = await admission.TryAcquireAsync(Request("tenant-a", RasterCapacityWork.Empty));

        denied.IsAdmitted.Should().BeFalse();
        denied.DenialKind.Should().Be(RasterCapacityDenialKind.WorkLimitExceeded);
        denied.Dimension.Should().Be(RasterCapacityDimension.ObjectRangeBytes);
        admitted.IsAdmitted.Should().BeTrue();
        await admitted.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_EnforcesPerTenantFairnessIndependentlyFromGlobalCapacity()
    {
        var admission = CreateAdmission(maxGlobal: 3, maxPerTenant: 1);
        var tenantA = await admission.TryAcquireAsync(Request("tenant-a", RasterCapacityWork.Empty));

        var sameTenant = await admission.TryAcquireAsync(Request("tenant-a", RasterCapacityWork.Empty));
        var otherTenant = await admission.TryAcquireAsync(Request("tenant-b", RasterCapacityWork.Empty));

        tenantA.IsAdmitted.Should().BeTrue();
        sameTenant.DenialKind.Should().Be(RasterCapacityDenialKind.TenantConcurrencyExceeded);
        sameTenant.Dimension.Should().Be(RasterCapacityDimension.TenantConcurrency);
        sameTenant.RetryAfterSeconds.Should().Be(1);
        otherTenant.IsAdmitted.Should().BeTrue();

        await tenantA.Lease!.DisposeAsync();
        await otherTenant.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_ReleasedLeaseRestoresGlobalAndTenantCapacity()
    {
        var admission = CreateAdmission(maxGlobal: 1, maxPerTenant: 1);
        var first = await admission.TryAcquireAsync(Request(string.Empty, RasterCapacityWork.Empty));
        var denied = await admission.TryAcquireAsync(Request("tenant-b", RasterCapacityWork.Empty));

        denied.DenialKind.Should().Be(RasterCapacityDenialKind.GlobalConcurrencyExceeded);
        var firstLease = first.Lease!;
        await firstLease.DisposeAsync();
        await firstLease.DisposeAsync();

        var afterRelease = await admission.TryAcquireAsync(Request(string.Empty, RasterCapacityWork.Empty));
        afterRelease.IsAdmitted.Should().BeTrue();
        await afterRelease.Lease!.DisposeAsync();
    }

    private static InMemoryRasterCapacityAdmission CreateAdmission(
        int maxGlobal,
        int maxPerTenant,
        long maxObjectBytes = 1_000)
        => new(Options.Create(new RasterCapacityOptions
        {
            MaxWebOutputCells = 1_000,
            MaxWebOutputBytes = 1_000,
            MaxObjectRangeRequests = 1_000,
            MaxObjectRangeBytes = maxObjectBytes,
            MaxPostGisWorkUnits = 1_000,
            MaxConcurrentRequests = maxGlobal,
            MaxConcurrentRequestsPerTenant = maxPerTenant,
            RetryAfterSeconds = 1,
        }));

    private static RasterCapacityRequest Request(string tenant, RasterCapacityWork work)
        => new("test.raster", tenant, work, RasterCapacityOverflowAction.SubmitDurableJob);
}
