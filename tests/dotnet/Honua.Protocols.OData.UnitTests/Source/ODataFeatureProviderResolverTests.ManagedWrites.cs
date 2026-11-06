// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.OData.Services;
using Honua.TestKit.OData;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.OData;

public sealed partial class ODataFeatureProviderResolverTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckWriteSupportAsync_ExplicitDefaultBinding_RequiresManagedStorageProof(bool managed)
    {
        var reader = Substitute.For<IFeatureReader, IManagedFeatureWriteSupport>();
        ((IManagedFeatureWriteSupport)reader).SupportsManagedWritesAsync(41, Arg.Any<CancellationToken>())
            .Returns(managed);
        var writer = Substitute.For<IFeatureWriter>();
        var provider = Substitute.For<IFeatureDataProvider>();
        provider.ProviderName.Returns(DataProviderNames.Postgis);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
        provider.Reader.Returns(reader);
        provider.Writer.Returns(writer);
        var fallbackReader = Substitute.For<IFeatureReader>();
        var resolver = new ODataFeatureProviderResolver(
            fallbackReader, writer, ODataProviderTestData.CreateRouter(Guid.NewGuid(), provider));
        var (snapshot, service, resource, publication) = ODataProviderTestData.CreateSnapshot(null);
        if (managed)
        {
            var binding = snapshot.Graph.StorageBindings.Single() with
            {
                Locator = "honua.features",
                Options = new Dictionary<string, JsonElement>
                {
                    ["primaryKeyColumn"] = JsonSerializer.SerializeToElement("objectid"),
                    ["attributesColumn"] = JsonSerializer.SerializeToElement("attributes"),
                    ["layerDiscriminatorColumn"] = JsonSerializer.SerializeToElement("layer_id")
                }
            };
            snapshot = new MetadataV2GraphSnapshot(snapshot.Graph with { StorageBindings = [binding] }, "test", DateTimeOffset.UtcNow);
        }

        var resolved = await resolver.ResolveQueryReaderAsync(
            snapshot, service, resource, publication, 41, requireCount: true, CancellationToken.None);
        var support = await resolver.CheckWriteSupportAsync(
            snapshot, service, resource, publication, 41, CancellationToken.None);

        resolved.Should().BeSameAs(reader);
        support.Supported.Should().Be(managed);
        await ((IManagedFeatureWriteSupport)reader).Received(1).SupportsManagedWritesAsync(41, Arg.Any<CancellationToken>());
        await fallbackReader.DidNotReceiveWithAnyArgs().GetAsync(default, default, default);
        await writer.DidNotReceiveWithAnyArgs().ApplyEditsAsync(default, default!, default);
    }
}
