// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Moq;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed partial class FeatureProviderQueryRouterTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("missing")]
    [InlineData("owner")]
    [InlineData("implicit-owner")]
    [InlineData("publication-owner")]
    public async Task ResolveBindingAsync_InvalidAuthorizedBinding_RejectsBeforeProviderLookup(string scenario)
    {
        var (snapshot, service, resource, publication) = CreateSnapshot(Guid.NewGuid().ToString());
        if (scenario is "owner" or "implicit-owner")
        {
            var binding = snapshot.Graph.StorageBindings.Single() with { ResourceId = "other-resource" };
            snapshot = new MetadataV2GraphSnapshot(snapshot.Graph with { StorageBindings = [binding] }, "test", DateTimeOffset.UtcNow);
        }
        if (scenario == "implicit-owner")
        {
            publication = publication with { StorageBindingId = null };
        }
        if (scenario == "missing")
        {
            publication = publication with { StorageBindingId = "missing-binding" };
        }
        if (scenario == "publication-owner")
        {
            publication = publication with { ResourceId = "other-resource" };
        }
        var connections = new Mock<ISecureConnectionRegistry>(MockBehavior.Strict);
        var router = new FeatureProviderQueryRouter(connections.Object, new FeatureDataProviderRegistry([]));

        var act = () => router.ResolveBindingAsync(
            snapshot, service, resource, publication, 41, FeatureProviderReadOperation.Query);

        await Assert.ThrowsAsync<InvalidOperationException>(act);
        connections.VerifyNoOtherCalls();
    }
}
