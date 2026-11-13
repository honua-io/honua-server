// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class CdnEmergencyTransitionTests
{
    private readonly CloudFrontLimits _cloudFront = new();
    private readonly ManualTimeProvider _time = new();
    private readonly SharedCacheOptions _options = new();

    [UnitTest]
    public async Task InvalidateAfterEditAsync_EmergencyEnabledWithPendingPurge_UsesShorterWindow()
    {
        var service = Service(new SharedLeases(_time));
        await service.InvalidateAfterEditAsync(["#layer"], CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        await service.InvalidateAfterEditAsync(["#layer"], CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        _options.Emergency.AllPublicLayers = true;
        await service.InvalidateAfterEditAsync(["#layer"], CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(3));

        await _cloudFront.AcceptedCountReaches(2);
        _time.Advance(CdnInvalidationService.CoalescingWindow);
        await Task.Yield();
        _cloudFront.Accepted.Should().HaveCount(2);
    }

    [UnitTest]
    public async Task InvalidateAfterEditAsync_NormalLeaseHeldByOtherTask_EmergencyPurgeDoesNotWaitForIt()
    {
        var leases = new SharedLeases(_time);
        var first = Service(leases);
        var second = Service(leases);
        await first.InvalidateAfterEditAsync(["#layer"], CancellationToken.None);
        _time.Advance(CdnInvalidationService.EmergencyCoalescingWindow);
        _options.Emergency.AllPublicLayers = true;

        await second.InvalidateAfterEditAsync(["#layer"], CancellationToken.None);

        _cloudFront.Accepted.Should().HaveCount(2);
        await first.InvalidateAfterEditAsync(["#layer"], CancellationToken.None);
        _cloudFront.Accepted.Should().HaveCount(2);
    }

    private CdnInvalidationService Service(SharedLeases leases)
        => new(_cloudFront, _time, NullLogger<CdnInvalidationService>.Instance,
            options: new StaticOptions(_options), leaseStore: leases);
}
