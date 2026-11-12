// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Capabilities;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.OperationsUnavailable;

public sealed class UnavailableOperationInstanceStoreTests
{
    [UnitTest]
    public async Task GetAsync_WithoutSystemDatabase_ThrowsTypedCapabilityFailure()
    {
        var store = new UnavailableOperationInstanceStore();

        var exception = await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => store.GetAsync("operation-1"));

        Assert.Equal(CapabilityUnavailableCodes.SystemDatabaseDependency, exception.MissingDependency);
        Assert.DoesNotContain("redis", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
