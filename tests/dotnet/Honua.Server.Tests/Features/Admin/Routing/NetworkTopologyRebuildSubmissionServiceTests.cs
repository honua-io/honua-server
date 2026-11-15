// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Abstractions;
using Honua.Server.Features.Admin.Routing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin.Routing;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.OperationsProgress)]
public sealed class NetworkTopologyRebuildSubmissionServiceTests
{
    [UnitTest]
    public async Task SubmitAsync_WithoutJobStore_ReportsMissingDurableOrchestrationWithoutCreatingAttempt()
    {
        var rebuildStore = Substitute.For<INetworkTopologyRebuildStore>();
        using var services = new ServiceCollection().BuildServiceProvider();
        var service = new NetworkTopologyRebuildSubmissionService(
            rebuildStore, services, NullLogger<NetworkTopologyRebuildSubmissionService>.Instance);

        var error = await Assert.ThrowsAsync<NetworkTopologyRebuildUnavailableException>(
            () => service.SubmitAsync("dataset", 1, 1, 1, 4326));

        Assert.Contains("durable job orchestration", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Redis", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rebuildStore.ReceivedCalls());
    }
}
