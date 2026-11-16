// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Tests.ProviderUnitSupport;

internal sealed class StubV2Provider(int storageLayerId, string? permanentFilterExpression) : IMetadataV2GraphProvider
{
    public string Locator { get; init; } = "gis.parcels";

    public IReadOnlyList<MetadataV2Field> SchemaFields { get; init; } = [];

    public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "Parcels" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields = SchemaFields,
            PermanentFilter = permanentFilterExpression == null ? null : new MetadataV2PermanentFilter
            {
                Expression = permanentFilterExpression,
                Language = MetadataV2PermanentFilterLanguages.GeoServicesSql
            }
        };
        var storageBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-parcels", Name = "binding-parcels" },
            ResourceId = resource.Metadata.Id,
            StorageLayerId = storageLayerId,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = Locator
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [resource],
            StorageBindings = [storageBinding]
        };
        return ValueTask.FromResult(new MetadataV2GraphSnapshot(graph, "etag-stub", DateTimeOffset.UtcNow));
    }

    public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);
}
