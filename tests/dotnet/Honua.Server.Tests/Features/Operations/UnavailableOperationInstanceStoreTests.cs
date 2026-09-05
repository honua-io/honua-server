// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Server.Features.Operations;
using Xunit;

namespace Honua.Server.Tests.Features.OperationsUnavailable;

public sealed class UnavailableOperationInstanceStoreTests
{
    [Fact]
    public async Task GetAsync_WithoutRedis_ThrowsTypedCapabilityFailure()
    {
        var store = new UnavailableOperationInstanceStore();

        var exception = await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => store.GetAsync("operation-1"));

        Assert.Equal("redis", exception.MissingDependency);
        // A missing Redis connection is distinct from an unentitled Redis deployment;
        // the refusal must identify the dependency without conflating those remedies.
        Assert.DoesNotContain("caching.redis", exception.Message);
    }
}
