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
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task CheckWriteSupportAsync_DefaultBinding_RequiresManagedStorageProof(bool managed, bool explicitBinding)
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

        if (!explicitBinding)
        {
            publication = publication with { StorageBindingId = null };
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitInvalidBinding_DoesNotFallBackToResourcePrimary(bool wrongResource)
    {
        var reader = Substitute.For<IFeatureReader>();
        var writer = Substitute.For<IFeatureWriter>();
        var resolver = new ODataFeatureProviderResolver(reader, writer, providerQueryRouter: null);
        var (snapshot, service, resource, publication) = ODataProviderTestData.CreateSnapshot(null);
        if (wrongResource)
        {
            var binding = snapshot.Graph.StorageBindings.Single() with { ResourceId = "other-resource" };
            snapshot = new MetadataV2GraphSnapshot(snapshot.Graph with { StorageBindings = [binding] }, "test", DateTimeOffset.UtcNow);
        }
        else
        {
            publication = publication with { StorageBindingId = "missing-explicit-binding" };
        }

        var read = () => resolver.ResolveQueryReaderAsync(
            snapshot, service, resource, publication, 41, requireCount: true, CancellationToken.None);
        var write = () => resolver.CheckWriteSupportAsync(
            snapshot, service, resource, publication, 41, CancellationToken.None);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*invalid storage binding*");
        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*invalid storage binding*");
        await reader.DidNotReceiveWithAnyArgs().GetAsync(default, default, default);
        await writer.DidNotReceiveWithAnyArgs().ApplyEditsAsync(default, default!, default);
    }

    private static MetadataV2GraphSnapshot WithoutStorageBindings(MetadataV2GraphSnapshot snapshot)
        => new(snapshot.Graph with
        {
            StorageBindings = [],
            Resources = snapshot.Graph.Resources.Select(resource => resource with
            {
                StorageBindingIds = [],
                PrimaryStorageBindingId = null
            }).ToArray()
        }, "test", DateTimeOffset.UtcNow);
}
