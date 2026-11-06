// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Security;

public sealed partial class CrossProtocolPermissionMatrixTests
{
    private sealed class GrpcWriteLayerCatalog : ITestMetadataV2GraphSource
    {
        public TestMetadataV2GraphProvider BuildProvider()
        {
            var graph = ReadSnapshot(new RbacTestLayerCatalog().BuildProvider()).Graph;
            var resource = graph.Resources.Single(resource => resource.Metadata.Id == "res-layer-0");
            var binding = new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "grpc-write-alpha", Name = "alpha" },
                ResourceId = resource.Metadata.Id,
                StorageType = MetadataV2StorageType.RelationalTable,
                Locator = "honua.features",
                StorageLayerId = ServiceRbacTestFixture.AlphaLayerId,
                Options = new Dictionary<string, JsonElement>
                {
                    ["attributesColumn"] = JsonSerializer.SerializeToElement("attributes"),
                    ["layerDiscriminatorColumn"] = JsonSerializer.SerializeToElement("layer_id")
                }
            };
            FeatureStorageMapping.FromMetadata(resource, binding).SupportsManagedWrites.Should().BeTrue();
            var provider = new TestMetadataV2GraphProvider(graph with
            {
                Resources = graph.Resources.Select(candidate => candidate.Metadata.Id == resource.Metadata.Id
                    ? candidate with { StorageBindingIds = [binding.Metadata.Id] }
                    : candidate).ToArray(),
                StorageBindings = [binding]
            });
            var snapshot = ReadSnapshot(provider);
            foreach (var publication in snapshot.Graph.Publications.Where(publication =>
                         publication.ResourceId == resource.Metadata.Id))
            {
                var resolved = snapshot.ResolveStorageBinding(publication);
                resolved.Should().BeSameAs(binding);
                resolved!.StorageLayerId.Should().Be(ServiceRbacTestFixture.AlphaLayerId);
            }

            return provider;
        }
    }

    private static MetadataV2GraphSnapshot ReadSnapshot(TestMetadataV2GraphProvider provider)
    {
        var pending = provider.GetCurrentAsync();
        pending.IsCompletedSuccessfully.Should().BeTrue();
        return pending.Result;
    }
}
