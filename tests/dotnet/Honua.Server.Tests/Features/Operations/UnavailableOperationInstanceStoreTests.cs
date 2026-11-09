// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Server.Features.Operations;
using Xunit;

namespace Honua.Server.Tests.Features.OperationsUnavailable;

public sealed class UnavailableOperationInstanceStoreTests
{
    [Fact]
    public async Task GetAsync_WithoutPostgres_ThrowsTypedCapabilityFailure()
    {
        var store = new UnavailableOperationInstanceStore();

        var exception = await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => store.GetAsync("operation-1"));

        Assert.Equal("postgres", exception.MissingDependency);
        Assert.DoesNotContain("caching.redis", exception.Message);
    }
}
